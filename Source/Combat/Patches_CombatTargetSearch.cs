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

        /// <summary>近战跨图索敌的共享扫描暂存（与 <see cref="TempNeighbors"/> 分开，防重入互踩）。</summary>
        internal static readonly List<SeamlessTileGraph.NeighborInfo> MeleeScanNeighbors = new List<SeamlessTileGraph.NeighborInfo>(8);

        /// <summary>
        /// &gt;0 = 抑制 BestAttackTarget Postfix 的**近战**跨图注入。JobGiver_Manhunter 的接管窗口置位：
        /// 其原版兜底分支会对跨图目标坐标走本图镜像路径（FindPathNow 到语义错误的合法坐标），
        /// 窗口内必须拿不到跨图目标（见 <see cref="Patch_JobGiver_Manhunter_CrossMapMelee"/>）。
        /// Postfix 无条件复位；Prefix 首句防御性清零——异常泄漏最多影响一个 think 周期。
        /// </summary>
        internal static int SuppressMeleeCrossMap;
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
            // 2026-09 近战跨图索敌（用户定夺：发狂动物/食尸鬼/奇美拉/近战袭击者）：近战无跨图攻击
            // 手段，改为"看见即锁定"（免可射门控），由 TryGiveJob/JobGiver_Manhunter 的跨图推进
            // 接管发桥接 Goto 走过去打；灵能/特殊弹道仍不支持。抑制窗口内不注入（见
            // SuppressMeleeCrossMap 注释——JobGiver_Manhunter 原版兜底会走镜像坐标）。
            bool meleeSearch;
            if (SeamlessDirectFireSupport.IsSupportedVerb(verb)) meleeSearch = false;
            else if (verb != null && verb.verbProps.IsMeleeAttack && Patches_CombatTargetSearch.SuppressMeleeCrossMap == 0) meleeSearch = true;
            else return;
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

            // 近战扫描源（2026-09 实测修正）：无阵营搜索者（发狂动物/蹒跚怪）在
            // attackTargetsCache 的潜在目标池里**根本没有玩家阵营 pawn**——GetPotentialTargetsFor
            // 仅对有阵营搜索者并入 TargetsHostileToFaction；原版近战索敌本就走
            // ClosestThingReachable 全图扫描而非该池。近战跨图候选改扫邻图 pawn 表
            //（建筑/炮塔不参与——近战拆塔跨图非目标场景）。
            List<IAttackTarget> meleePool = meleeSearch ? new List<IAttackTarget>() : null;

            IAttackTarget best = null;
            var bestDistSq = float.MaxValue;
            var targetInfo = new LocalTargetInfo();
            for (var n = 0; n < neighbors.Count; n++)
            {
                var neighbor = neighbors[n];
                if (!SeamlessCombatCoords.TryGetCombatLink(map, neighbor.map, out var link)) continue;

                List<IAttackTarget> pool;
                if (meleeSearch)
                {
                    meleePool.Clear();
                    var neighborPawns = neighbor.map.mapPawns.AllPawnsSpawned;
                    for (int i = 0; i < neighborPawns.Count; i++)
                        if (neighborPawns[i] is IAttackTarget it) meleePool.Add(it);
                    pool = meleePool;
                }
                else pool = neighbor.map.attackTargetsCache.GetPotentialTargetsFor(searcher);
                for (var i = 0; i < pool.Count; i++)
                {
                    var t = pool[i];
                    var thing = t.Thing;
                    if (thing == searcherThing || thing == null || !thing.Spawned || thing.Destroyed) continue;
                    var unified = thing.Position + link.offset;
                    var distSq = searcherPos.DistanceToSquared(unified);
                    // 距离口径 = 原版 caller maxDist（接战半径 65，2026-08 勘误：原版获取不查武器射程——
                    // 射程属于 CastPositionFinder 抵近层；武器射程过滤曾把 42-65 接战圈提前拒掉）。
                    if (distSq < minDistSq || distSq > maxDistSq)
                    {
                        continue;
                    }
                    if (hasLocus && unified.DistanceToSquared(locus) > maxLocusDistSq) continue;
                    if (!searcherThing.HostileTo(thing)) continue;
                    if (validator != null && !validator(thing)) continue;
                    if (needThreat && t.ThreatDisabled(searcher)) continue;
                    if (needActiveThreat && !GenHostility.IsActiveThreatTo(t, searcherThing.Faction)) continue;
                    if ((flags & TargetScanFlags.NeedNonBurning) != TargetScanFlags.None && thing.IsBurning()) continue;
                    // 跨图候选不做 fog 过滤（2026-08 实测修正）：邻图接缝带多数仍在雾中（揭雾只绕
                    // 本图中心），而 SeamlessTileRenderer.DrawNeighborPawns 无条件把邻图 pawn 画给
                    // 玩家——可见性口径跟渲染走，看得见就打得着；原版 fog 门只对玩家阵营搜索者，
                    // 这里对玩家侧同样放开（跨缝连续可视是本 mod 的核心体验）。

                    if (!meleeSearch)
                    {
                        // "当前可射击"门控（跨图射击线 = 射程 + 分段 LOS + 统一格界内）。
                        // 近战免此门——锁定的意义是"走过去打"，能否走到由推进链的 CanBridgeTo 把关。
                        targetInfo = new LocalTargetInfo(thing);
                        if (!verb.TryFindShootLineFromTo(searcherPos, targetInfo, out _)) continue;
                    }

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
    /// 跨图索敌的 job 生成兜底（2026-08 修复"敌人不跨图索敌"的主断点）+ 近战推进接管（2026-09）。
    ///
    /// 【Prefix，近战】跨图近战目标 + 可桥接 → 桥接 Goto（目标 Thing 在邻图，StartPath 包装按
    /// 结构判据桥接，过缝后原版近战自然接手）。原版对跨图目标会产出 AttackMelee job——
    /// JobDriver 无法跨图预约/寻路，必失败循环。远程/无手段放行原方法体。
    /// 【Postfix，远程】索敌成功但原版 TryGiveJob 产不出 job（Wait_Combat 捷径的掩体/&lt;5 格
    /// 判据用混算坐标恒假、CastPositionFinder 在错误坐标系全灭）→ 按 L132-135 构造 Wait_Combat，
    /// 开火链交给已跨图化的 JobDriver_Wait.CheckForAutoAttack。
    /// 子类（AIFightEnemies/Manhunter*/ShamblerFight/Defend* 等）均不覆写或经非虚 base 调用命中。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "TryGiveJob")]
    public static class Patch_JobGiver_AIFightEnemy_TryGiveJob_CrossMap
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (!SeamlessCombatCoords.Enabled) return true;
                var enemyTarget = pawn?.mindState?.enemyTarget;
                if (enemyTarget == null || enemyTarget.Destroyed || enemyTarget.Map == null || pawn?.Map == null
                    || enemyTarget.Map == pawn.Map || pawn.Map.Disposed) return true;
                if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, enemyTarget.Map, out _)) return true;

                var verb = pawn.TryGetAttackVerb(enemyTarget, !pawn.IsColonist);
                if (verb == null || !verb.verbProps.IsMeleeAttack) return true; // 远程：原版 + Postfix 兜底
                if (!SeamlessCrossMapOrders.CanBridgeTo(pawn, enemyTarget.Map)) return true; // 不可达 → ShouldLoseTarget 丢弃

                var job = JobMaker.MakeJob(JobDefOf.Goto, enemyTarget);
                job.expiryInterval = Rand.Range(360, 480); // 对齐原版 ExpiryInterval_Melee 默认（protected 不可读）
                job.checkOverrideOnExpire = true;
                job.expireRequiresEnemiesNearby = true;
                job.collideWithPawns = true;
                __result = job;

                if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                    Log.Message($"[RimExodus] Cross-map melee approach: {pawn.LabelShort} -> {enemyTarget.LabelShort} on map {enemyTarget.Map.uniqueID}.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] AIFightEnemy cross-map melee prefix failed, vanilla behavior kept: " + ex);
                return true;
            }
        }

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

            if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                Log.Message($"[RimExodus] Cross-map combat job: {pawn.LabelShort} Wait_Combat -> {enemyTarget.LabelShort} on map {enemyTarget.Map.uniqueID}.");
        }
    }

    /// <summary>
    /// 跨图目标的保持判定（2026-08 主断点的伴生修复；2026-09 增近战）：原版 ShouldLoseTarget 用
    /// CanReach（跨图安静 false）+ 位置差（混算坐标 &gt; keepRadius 恒真）判丢——跨图目标每 think tick
    /// 被丢掉再重取，400 tick engage 宽限失效（症状：反复 acquired 日志、瞬断 LOS 即永久丢目标）。
    /// Prefix：跨图目标改用"仍可交战"作保持判据——远程 = 当前可射；近战 = 可桥接（跨图 Goto 推进中）；
    /// 不可射且不可桥接 = 丢目标。保留原版 ThreatDisabled 失效语义。
    /// 注意：ShamblerFight 覆写 ShouldLoseTarget（含 enemyTarget.Map == pawn.Map 硬检查）且不调
    /// base——本 patch 对食尸鬼不生效；食尸鬼的跨图目标由 TryGiveJob 的近战 Prefix 在其
    /// UpdateEnemyTarget 丢弃前先行接管（每 think 现找，丢弃即重锁，无宽限损失）。
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
            // 远程：当前可射即保持；近战（2026-09）：可桥接即保持——推进 Prefix 会发跨图 Goto
            // 走过去打。不可桥接的近战跨图目标仍立即丢弃（防无限重锁循环）。
            var stillEngaged = verb != null
                && (!verb.verbProps.IsMeleeAttack
                    ? verb.CanHitTarget(enemyTarget)
                    : SeamlessCrossMapOrders.CanBridgeTo(pawn, enemyTarget.Map));
            __result = !stillEngaged || ((enemyTarget as IAttackTarget)?.ThreatDisabled(pawn) ?? false);
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

            if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                Log.Message($"[RimExodus] Cross-map approach: {pawn.LabelShort} -> {best.LabelShort} on map {best.Map.uniqueID} "
                    + $"(unified dist {Mathf.Sqrt(bestDistSq):F0}).");
        }
    }

    /// <summary>
    /// 发狂动物跨图近战（2026-09 用户定夺）：JobGiver_Manhunter 不走 AIFightEnemy 链（无
    /// mindState.enemyTarget，每 think 现找目标），且其原版兜底分支对跨图目标会 FindPathNow 到
    /// **本图镜像坐标**（跨图 Position 在同尺寸图上恰为合法格——语义错误但路径可找到，动物会走向
    /// 镜像位置）。因此本 giver 自管跨图，不依赖 BestAttackTarget Postfix 的近战注入：
    /// Prefix 置抑制位后按原版同参探测本图目标——本图有目标放行原版（注入被抑制 = 纯原版行为）；
    /// 本图无目标且存在可桥接的跨图目标时接管为桥接 Goto（过缝后由对图原版逻辑接手近战）；
    /// 否则放行原版（其查找被抑制，返回 null 而非镜像路径）。Postfix 无条件解除抑制。
    /// 覆写 canBashDoors=false 的奇美拉 ChimeraAttack duty 等子节点同样命中。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Manhunter), "TryGiveJob")]
    public static class Patch_JobGiver_Manhunter_CrossMapMelee
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            try
            {
                Patches_CombatTargetSearch.SuppressMeleeCrossMap = 0; // 防御性清（上一次异常泄漏自愈）
                if (!SeamlessCombatCoords.Enabled) return true;
                if (pawn?.Map == null || pawn.Map.Disposed || !pawn.Spawned) return true;
                if (pawn.TryGetAttackVerb(null) == null) return true; // 与原版首检一致
                if (!SeamlessCombatCoords.HasActiveSeamNeighbors(pawn.Map)) return true;

                Patches_CombatTargetSearch.SuppressMeleeCrossMap = 1;
                // 本图目标探测：与原版 FindPawnTarget 同参（近战注入被抑制 → 纯本图结果）。
                Predicate<Thing> validator = x => x is Pawn && (int)x.def.race.intelligence >= 1;
                var own = AttackTargetFinder.BestAttackTarget(pawn,
                    TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable, validator,
                    0f, 9999f, IntVec3.Invalid, float.MaxValue, true, true, true);
                if (own != null) return true; // 本图目标优先 → 原版

                if (SeamlessCrossMapMeleeSearch.TryFindCrossMapMeleeTarget(pawn, validator, 9999f, out var target)
                    && SeamlessCrossMapOrders.CanBridgeTo(pawn, target.Map))
                {
                    var job = JobMaker.MakeJob(JobDefOf.Goto, target);
                    job.expiryInterval = Rand.Range(420, 900); // 对齐原版 MaxMeleeChaseTicks/MinMeleeChaseTicks
                    job.checkOverrideOnExpire = true;
                    job.expireRequiresEnemiesNearby = true;
                    job.collideWithPawns = true;
                    __result = job;

                    if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                        Log.Message($"[RimExodus] Cross-map melee approach: {pawn.LabelShort} -> {target.LabelShort} on map {target.Map.uniqueID}.");
                    return false;
                }
                return true; // 无可用跨图目标 → 原版（查找被抑制 → 返回 null）
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] JobGiver_Manhunter cross-map prefix failed, vanilla behavior kept: " + ex);
                return true;
            }
        }

        public static void Postfix()
        {
            Patches_CombatTargetSearch.SuppressMeleeCrossMap = 0;
        }
    }

    /// <summary>
    /// 跨图近战目标扫描工具（JobGiver_Manhunter 接管专用）：活跃邻图 pawn → 统一坐标最近者。
    /// 候选过滤与 BestAttackTarget Postfix 同口径（距离/敌对/validator/Spawned），无 LOS 与
    /// 可射门——锁定的意义是"走过去打"，可达性由调用方的 CanBridgeTo 把关。
    /// 扫描源 = 邻图 pawn 表而非 attackTargetsCache 池（无阵营搜索者的池不含玩家阵营 pawn，
    /// 见 BestAttackTarget Postfix 内 2026-09 注；原版近战索敌同走全图扫描）。
    /// </summary>
    internal static class SeamlessCrossMapMeleeSearch
    {
        internal static bool TryFindCrossMapMeleeTarget(Pawn pawn, Predicate<Thing> validator, float maxDist, out Thing best)
        {
            best = null;
            var map = pawn.Map;
            if (map == null) return false;
            var maxDistSq = maxDist * maxDist;
            var neighbors = Patches_CombatTargetSearch.MeleeScanNeighbors;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, neighbors);
            var bestDistSq = float.MaxValue;
            for (var n = 0; n < neighbors.Count; n++)
            {
                var neighbor = neighbors[n];
                if (!SeamlessCombatCoords.TryGetCombatLink(map, neighbor.map, out var link)) continue;

                var neighborPawns = neighbor.map.mapPawns.AllPawnsSpawned;
                for (var i = 0; i < neighborPawns.Count; i++)
                {
                    var thing = neighborPawns[i];
                    if (thing == null || thing.Dead || !thing.Spawned || thing.Destroyed) continue;
                    var unified = thing.Position + link.offset;
                    var distSq = pawn.Position.DistanceToSquared(unified);
                    if (distSq > maxDistSq || distSq >= bestDistSq) continue;
                    if (!pawn.HostileTo(thing)) continue;
                    if (thing is IAttackTarget at && at.ThreatDisabled(pawn)) continue;
                    if (validator != null && !validator(thing)) continue;
                    bestDistSq = distSq;
                    best = thing;
                }
            }
            return best != null;
        }
    }
}
