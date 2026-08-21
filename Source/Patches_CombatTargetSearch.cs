using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图索敌 patch（阶段5 S2，轻量门控——用户定夺 2026-08，勿回退为 VMF 式 BestAttackTarget
    /// 完整克隆：克隆量大且仍需另行处理 JobGiver_AIFightEnemy 的裸坐标混用）。
    ///
    /// ① <see cref="Patch_AttackTargetFinder_BestAttackTarget"/> Postfix——原版结果非空直接放行
    ///    （**本图目标永远优先**）；为空时扫活跃邻图的 attackTargetsCache 目标池，统一坐标过滤
    ///    （距离/射程/敌对/威胁失效/玩家阵营 fog），并以"**当前可射击**"（我们接管的跨图射击线
    ///    从当前位置可命中）为最终门控取最近者。"当前可射"门控使 AI think 直接走 AttackStatic
    ///    分支，结构性规避 JobGiver_AIFightEnemy 用邻图本地坐标算走位距离的坐标混用问题
    ///    （走位类跨图追击由 S3 的追击扫描/战斗踩点承担，不在索敌里暴露不可射目标）。
    /// ② <see cref="Patch_AttackTargetFinder_CanSee"/> Prefix——seer/target 分属互为活跃邻居的
    ///    两图时用分段 LOS（语义对齐原版 CanSee：目标可射格 × [直射 + seer 侧 lean 格] 组合）。
    ///
    /// 简化边界（观察项）：威胁评分/众数选择等原版高级仲裁未复刻（距离最近决胜）；
    /// LOSBlockableByGas 的气体校验在统一格上按宿主图查（邻图段盲烟漏检，缝区罕见）。
    /// </summary>
    public static class Patches_CombatTargetSearch
    {
        internal static readonly List<SeamlessTileGraph.NeighborInfo> TempNeighbors = new List<SeamlessTileGraph.NeighborInfo>(8);
    }

    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        public static void Postfix(IAttackTargetSearcher searcher, TargetScanFlags flags, Predicate<Thing> validator,
            float minDist, float maxDist, IntVec3 locus, float maxTravelRadiusFromLocus,
            bool canBashDoors, bool canTakeTargetsCloserThanEffectiveMinRange, bool canBashFences, bool onlyRanged,
            ref IAttackTarget __result)
        {
            if (__result != null) return; // 本图优先：原版有结果不动。
            if (!SeamlessCombatCoords.Enabled) return;
            var searcherThing = searcher?.Thing;
            if (searcherThing?.Map == null || searcherThing.Map.Disposed) return;
            var verb = searcher.CurrentEffectiveVerb;
            if (!(verb is Verb_LaunchProjectile) || verb.verbProps.IsMeleeAttack) return; // 近战/灵能不做跨图索敌（近战走 S3 追击扫描过缝）
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            if (caster?.Map == null) return;

            var map = searcherThing.Map;
            if (!SeamlessCombatCoords.HasActiveSeamNeighbors(map)) return;

            var rangeSq = verb.EffectiveRange * verb.EffectiveRange;
            var minDistSq = minDist * minDist;
            var maxDistSq = maxDist * maxDist;
            var hasLocus = maxTravelRadiusFromLocus < 9999f && locus.IsValid;
            var locusReach = maxTravelRadiusFromLocus + verb.EffectiveRange;
            var maxLocusDistSq = locusReach * locusReach;
            var needThreat = (flags & TargetScanFlags.NeedThreat) != TargetScanFlags.None
                || (flags & TargetScanFlags.NeedAutoTargetable) != TargetScanFlags.None;
            var needActiveThreat = (flags & TargetScanFlags.NeedActiveThreat) != TargetScanFlags.None;
            var searcherPos = searcherThing.Position;

            var neighbors = Patches_CombatTargetSearch.TempNeighbors;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, neighbors);

            IAttackTarget best = null;
            var bestDistSq = float.MaxValue;
            var targetInfo = new LocalTargetInfo();
            int poolTotal = 0, rejectBounds = 0, rejectDist = 0, rejectHostile = 0, rejectHitLine = 0;
            for (var n = 0; n < neighbors.Count; n++)
            {
                var neighbor = neighbors[n];
                if (!SeamlessCombatCoords.TryGetCombatLink(map, neighbor.map, out var link)) continue;

                var pool = neighbor.map.attackTargetsCache.GetPotentialTargetsFor(searcher);
                for (var i = 0; i < pool.Count; i++)
                {
                    var t = pool[i];
                    var thing = t.Thing;
                    if (thing == searcherThing || thing == null || !thing.Spawned || thing.Destroyed) continue;
                    poolTotal++;

                    var unified = thing.Position + link.offset;
                    // 门限已放宽（2026-08 用户定夺"看得见就打得着"）：统一格不再要求宿主方形内——
                    // 射击线逐格阻挡按多边形归属路由，深目标可射。此处不再有 bounds 拒绝。
                    var distSq = searcherPos.DistanceToSquared(unified);
                    if (distSq < minDistSq || distSq > maxDistSq || distSq > rangeSq) { rejectDist++; continue; }
                    if (hasLocus && unified.DistanceToSquared(locus) > maxLocusDistSq) { rejectDist++; continue; }
                    if (!searcherThing.HostileTo(thing)) { rejectHostile++; continue; }
                    if (validator != null && !validator(thing)) { rejectHostile++; continue; }
                    if (needThreat && t.ThreatDisabled(searcher)) { rejectHostile++; continue; }
                    if (needActiveThreat && !GenHostility.IsActiveThreatTo(t, searcherThing.Faction)) { rejectHostile++; continue; }
                    if ((flags & TargetScanFlags.NeedNonBurning) != TargetScanFlags.None && thing.IsBurning()) { rejectHostile++; continue; }
                    // 跨图候选不做 fog 过滤（2026-08 实测修正）：邻图接缝带多数仍在雾中（揭雾只绕
                    // 本图中心），而 SeamlessTileRenderer.DrawNeighborPawns 无条件把邻图 pawn 画给
                    // 玩家——可见性口径跟渲染走，看得见就打得着；原版 fog 门只对玩家阵营搜索者，
                    // 这里对玩家侧同样放开（跨缝连续可视是本 mod 的核心体验）。

                    // "当前可射击"门控（跨图射击线 = 射程 + 分段 LOS + 统一格界内）。
                    targetInfo = new LocalTargetInfo(thing);
                    if (!verb.TryFindShootLineFromTo(searcherPos, targetInfo, out _)) { rejectHitLine++; continue; }

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = t;
                    }
                }
            }

            if (best != null)
            {
                __result = best;
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Cross-map target acquired: {searcherThing.LabelShort} -> {best.Thing.LabelShort} "
                        + $"(unified dist {Mathf.Sqrt(bestDistSq):F0}).");
            }
            else if ((RimExodusMod.Settings?.verboseLogging ?? false) && poolTotal > 0)
            {
                Log.Message($"[RimExodus] Cross-map scan: {searcherThing.LabelShort} saw {poolTotal} neighbor candidates, "
                    + $"rejected bounds={rejectBounds} dist={rejectDist} hostile={rejectHostile} hitline={rejectHitLine}.");
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee))]
    public static class Patch_AttackTargetFinder_CanSee
    {
        public static bool Prefix(Thing seer, Thing target, Func<IntVec3, bool> validator, ref bool __result)
        {
            if (seer?.Map == null || target?.Map == null || seer.Map == target.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(seer.Map, target.Map, out var link)) return true;

            __result = SeamlessCrossMapSight.CanSeeCrossMap(seer, target, in link, validator);
            return false;
        }
    }
}
