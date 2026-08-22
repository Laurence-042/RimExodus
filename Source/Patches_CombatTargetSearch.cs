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
    ///    从当前位置可命中）为最终门控取最近者。**注意（2026-08 审计勘误）**：可射门控并不足以
    ///    让原版 JobGiver_AIFightEnemy 产出战斗 job——其 Wait_Combat 捷径要求"掩体旁或 <5 格"
    ///    （跨图坐标混算全假），fallback 的 CastPositionFinder 也在错误坐标系全灭——由
    ///    <see cref="Patch_JobGiver_AIFightEnemy_TryGiveJob_CrossMap"/> 兜底补齐 job 生成。
    ///    走位类跨图追击由 S3 的追击扫描/战斗踩点承担（CastPositionFinder 跨图候选仍为观察项）。
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
            int poolTotal = 0, rejectDist = 0, rejectHostile = 0, rejectHitLine = 0;
            var nearestRejectedDistSq = float.MaxValue;
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
                    var distSq = searcherPos.DistanceToSquared(unified);
                    // 距离口径 = 原版 caller maxDist（接战半径 65，2026-08 勘误：原版获取不查武器射程——
                    // 射程属于 CastPositionFinder 抵近层；武器射程过滤曾把 42-65 接战圈提前拒掉）。
                    if (distSq < minDistSq || distSq > maxDistSq)
                    {
                        rejectDist++;
                        if (distSq < nearestRejectedDistSq) nearestRejectedDistSq = distSq;
                        continue;
                    }
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
                var nearest = nearestRejectedDistSq < float.MaxValue ? $" nearestRejectedDist={Mathf.Sqrt(nearestRejectedDistSq):F0}" : "";
                Log.Message($"[RimExodus] Cross-map scan: {searcherThing.LabelShort} saw {poolTotal} neighbor candidates, "
                    + $"rejected dist={rejectDist}{nearest} hostile={rejectHostile} hitline={rejectHitLine}.");
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

    /// <summary>
    /// 跨图索敌的 job 生成兜底（2026-08 修复"敌人不跨图索敌"的主断点）：索敌本身成功（BestAttackTarget
    /// Postfix 已取得邻图目标），但原版 TryGiveJob 的 Wait_Combat 捷径要求"掩体旁（CalculateOverallBlockChance
    /// 用混算坐标）或 &lt;5 格（位置差 ~200+ 格恒假）"，fallback 的 CastPositionFinder 射击位搜索又在错误
    /// 坐标系全灭 → 返回 null → 敌人站着不还击。Postfix：结果为 null 且 enemyTarget 在活跃邻图、远程
    /// verb、当前格可射（跨图射击线）→ 按原版 L132-135 分支构造 Wait_Combat，开火链交给已跨图化的
    /// JobDriver_Wait.CheckForAutoAttack → BestShootTargetFromCurrentPosition（本文件 ①）→ TryStartAttack。
    /// 近战敌人不适用（跨图无远程手段，走追击链设计）。子类（AIFightEnemies/Manhunter/Defend* 等）
    /// 均不覆写 TryGiveJob，patch 基类实现即全覆盖。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
    public static class Patch_JobGiver_AIFightEnemy_TryGiveJob_CrossMap
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || pawn?.Map == null) return;
            var enemyTarget = pawn.mindState?.enemyTarget;
            if (enemyTarget == null || enemyTarget.Destroyed || enemyTarget.Map == null || enemyTarget.Map == pawn.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, enemyTarget.Map, out _)) return;

            var verb = pawn.TryGetAttackVerb(enemyTarget, !pawn.IsColonist);
            if (verb == null || verb.verbProps.IsMeleeAttack) return;
            if (!verb.CanHitTarget(enemyTarget)) return; // 跨图射击线（射程 + 分段 LOS）
            if (!(pawn.Position.WalkableBy(pawn.Map, pawn) && pawn.Map.pawnDestinationReservationManager.CanReserve(pawn.Position, pawn, pawn.Drafted)))
                return;

            pawn.pather?.StopDead();
            __result = JobMaker.MakeJob(JobDefOf.Wait_Combat,
                JobGiver_AIFightEnemy.ExpiryInterval_ShooterSucceeded.RandomInRange, checkOverrideOnExpiry: true);

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Cross-map combat job: {pawn.LabelShort} Wait_Combat -> {enemyTarget.LabelShort} on map {enemyTarget.Map.uniqueID}.");
        }
    }

    /// <summary>
    /// 跨图目标的保持判定（2026-08，主断点的伴生修复）：原版 ShouldLoseTarget 用 CanReach（跨图
    /// 安静 false）+ 位置差（混算坐标 &gt; keepRadius 恒真）判丢——跨图目标每 think tick 被丢掉再
    /// 重取，400 tick engage 宽限失效（症状：反复 acquired 日志、瞬断 LOS 即永久丢目标）。Prefix：
    /// 跨图目标改用"当前可射"作保持判据（可射 = 还在交战即保持；不可射或近战 = 丢目标——近战
    /// 跨图无手段，由追击链/战斗踩点承担）。保留原版 ThreatDisabled 失效语义。
    /// </summary>
    /// <summary>
    /// 跨图目标的保持判定（2026-08，主断点的伴生修复）：原版 ShouldLoseTarget 用 CanReach（跨图
    /// 安静 false）+ 位置差（混算坐标 &gt; keepRadius 恒真）判丢——跨图目标每 think tick 被丢掉再
    /// 重取，400 tick engage 宽限失效（症状：反复 acquired 日志、瞬断 LOS 即永久丢目标）。Prefix：
    /// 跨图目标改用"当前可射"作保持判据（可射 = 还在交战即保持；不可射或近战 = 丢目标——近战
    /// 跨图无手段，由追击链/战斗踩点承担）。保留原版 ThreatDisabled 失效语义。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "ShouldLoseTarget")]
    public static class Patch_JobGiver_AIFightEnemy_ShouldLoseTarget_CrossMap
    {
        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            var enemyTarget = pawn?.mindState?.enemyTarget;
            if (enemyTarget == null || enemyTarget.Destroyed || enemyTarget.Map == null || pawn.Map == null
                || enemyTarget.Map == pawn.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, enemyTarget.Map, out _)) return true;

            var verb = pawn.TryGetAttackVerb(enemyTarget, !pawn.IsColonist);
            var canStillShoot = verb != null && !verb.verbProps.IsMeleeAttack && verb.CanHitTarget(enemyTarget);
            __result = !canStillShoot || ((enemyTarget as IAttackTarget)?.ThreatDisabled(pawn) ?? false);
            return false;
        }
    }

    /// <summary>
    /// 跨图推进 giver（2026-08 原版两层模型复刻，用户定夺"只 patch 获取和寻路"）：
    /// 原版远距推进 = `JobGiver_AIGotoNearestHostile` **全图无半径**扫本图 attackTargetsCache 取最近敌人
    /// + CanReach → Goto(目标本身)（AssaultColony duty 树第⑥位 fallback；有本图建筑时第⑤位
    /// AITrashBuildingsDistant 优先——"有建筑优先拆"语义跨图自然保留）。接战半径 65/72 只属于
    /// 第③位 AIFightEnemies（enemyTarget 选取），与本 giver 无关（2026-08 agent 实证勘误：
    /// 56 为从不生效的代码默认值，Core XML 全部覆写 65/72；获取不查武器射程）。
    /// Postfix：原生结果 null（本图无敌——玩家在缝对面的场景）→ 扫活跃邻图缓存取统一坐标最近者，
    /// CanBridgeTo 替代 CanReach（跨图"走得到"语义），按原版 :49-61 复刻 Goto(目标 thing)
    /// （collideWithPawns/checkOverrideOnExpire/expireRequiresEnemiesNearby/intervalScalingTarget 全原生）。
    /// 过缝由 StartPath 包装按结构判据识别（Goto 且 targetA Thing 在邻图——**不能用 dutyTag 自标识**：
    /// ThinkNode_Duty.TryIssueJobPackage 会把 think tree 产出的 job.dutyTag 无条件覆写为 duty.tag(null)，
    /// 2026-08 实证的"推进 job 从未被桥接"根因），续跑 Goto 走向目标，途中进入接战圈/射程
    /// 即被原生 AIFightEnemies 接管开火。非战斗 pawn 目标跨图仅认 IsCombatant（原版 LOS 替代项
    /// 是本图坐标混算，不复刻）。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIGotoNearestHostile), "TryGiveJob")]
    public static class Patch_JobGiver_AIGotoNearestHostile_CrossMap
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !SeamlessCombatCoords.Enabled) return;
            if (pawn?.Map == null || pawn.Map.Disposed || !pawn.Spawned) return;
            var map = pawn.Map;
            if (!SeamlessCombatCoords.HasActiveSeamNeighbors(map)) return;

            var neighbors = Patches_CombatTargetSearch.TempNeighbors;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, neighbors);

            Thing best = null;
            var bestDistSq = float.MaxValue;
            for (var n = 0; n < neighbors.Count; n++)
            {
                var neighbor = neighbors[n];
                if (!SeamlessCombatCoords.TryGetCombatLink(map, neighbor.map, out var link)) continue;
                if (!SeamlessCrossMapOrders.CanBridgeTo(pawn, neighbor.map)) continue;

                var pool = neighbor.map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                for (var i = 0; i < pool.Count; i++)
                {
                    var t = pool[i];
                    var thing = t?.Thing;
                    if (thing == null || !thing.Spawned || thing.Destroyed) continue;
                    // 过滤镜像原版 :32（NPC 阵营侧 fog 项天然不适用）。
                    if (t.ThreatDisabled(pawn) || !AttackTargetFinder.IsAutoTargetable(t)) continue;
                    if (thing is Pawn p && !p.IsCombatant()) continue; // 非战斗 pawn 跨图仅认 IsCombatant（见类头）
                    var distSq = pawn.Position.DistanceToSquared(thing.Position + link.offset);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = thing;
                    }
                }
            }

            if (best == null) return;

            var job = JobMaker.MakeJob(JobDefOf.Goto, best);
            job.intervalScalingTarget = TargetIndex.A;
            job.checkOverrideOnExpire = true;
            job.expireRequiresEnemiesNearby = true;
            job.collideWithPawns = true;
            __result = job;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Cross-map approach: {pawn.LabelShort} -> {best.LabelShort} on map {best.Map.uniqueID} "
                    + $"(unified dist {Mathf.Sqrt(bestDistSq):F0}).");
        }
    }
}
