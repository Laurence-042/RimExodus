using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    internal enum RaidEntryPreference { Default, Native, Outer }
    internal enum RaidEntryScope { Incident, Strategy, Arrival }

    /// <summary>
    /// Selects an unseen outer edge for supported arrivals. The vanilla worker still generates
    /// the group on the attacked map first. A neighbor becomes the host only after the complete
    /// native squads and landing cells pass preflight.
    /// </summary>
    internal static class SeamlessRaidOuterSpawn
    {
        /// <summary>总开关（设置 raidOuterSpawnEnabled，默认开）。</summary>
        internal static bool Enabled => RimExodusMod.Settings?.raidOuterSpawnEnabled ?? true;

        internal static RaidEntryPreference EntryPreference(RimExodusSettings settings, RaidEntryScope scope, string defName)
        {
            if (settings == null || defName == null) return RaidEntryPreference.Default;
            if (settings.raidEntryPreferences.TryGetValue($"{scope}/{defName}", out var preference)) return preference;
            return scope == RaidEntryScope.Incident && settings.raidOuterSpawnLocalIncidents.Contains(defName)
                ? RaidEntryPreference.Native : RaidEntryPreference.Default;
        }

        internal static void SetEntryPreference(RimExodusSettings settings, RaidEntryScope scope, string defName,
            RaidEntryPreference preference)
        {
            if (scope == RaidEntryScope.Incident) settings.raidOuterSpawnLocalIncidents.Remove(defName);
            string key = $"{scope}/{defName}";
            if (preference == RaidEntryPreference.Default) settings.raidEntryPreferences.Remove(key);
            else settings.raidEntryPreferences[key] = preference;
        }

        internal static void ResetEntryPreferences(RimExodusSettings settings)
        {
            settings.raidEntryPreferences.Clear();
            settings.raidOuterSpawnLocalIncidents.Clear();
        }

        internal static bool IsStructurallyLocalWorker(System.Type workerType)
        {
            return workerType != null
                && (typeof(IncidentWorker_PsychicRitualSiege).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_EntitySwarm).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_ShamblerAssault).IsAssignableFrom(workerType));
        }

        internal static bool IsStrategyMapBound(RaidStrategyDef strategy)
        {
            var worker = strategy?.workerClass;
            // Vanilla walking siege can choose its camp before the Travel toil reaches it.
            return worker != null && ((worker != typeof(RaidStrategyWorker_Siege)
                    && typeof(RaidStrategyWorker_Siege).IsAssignableFrom(worker))
                || typeof(RaidStrategyWorker_PsychicRitualSiege).IsAssignableFrom(worker));
        }

        internal static bool UsesNativeEntry(RaidStrategyDef strategy)
        {
            var preference = EntryPreference(RimExodusMod.Settings, RaidEntryScope.Strategy, strategy?.defName);
            return preference == RaidEntryPreference.Native
                || (preference == RaidEntryPreference.Default && IsStrategyMapBound(strategy));
        }

        private static string ResolvedNativeReason(IncidentParms parms)
        {
            if (parms == null) return null;
            if (UsesNativeEntry(parms.raidStrategy)) return "raid strategy uses native entry";
            var mode = parms.raidArrivalMode;
            if (mode != null && !mode.walkIn) return $"arrival mode {mode.defName} requires native placement";
            return EntryPreference(RimExodusMod.Settings, RaidEntryScope.Arrival, mode?.defName) == RaidEntryPreference.Native
                ? $"arrival mode {mode.defName} is configured to use native entry" : null;
        }

        private static void LogNativeEntry(IncidentParms parms, Map map, string reason)
        {
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Raid outer spawn: using native entry on target map {map.uniqueID} "
                + $"(strategy={parms.raidStrategy?.defName ?? parms.raidStrategy?.workerClass?.Name ?? "unresolved"}; {reason}).");
        }

        internal static bool CanStageOnHost(Lord lord) =>
            lord.LordJob.GetType() == typeof(LordJob_StageThenAttack)
            && lord.CurLordToil == lord.Graph?.StartingToil
            && lord.CurLordToil?.data is LordToilData_Stage;

        private static readonly AccessTools.FieldRef<LordJob_StageThenAttack, IntVec3> NativeStageLocation =
            AccessTools.FieldRefAccess<LordJob_StageThenAttack, IntVec3>("stageLoc");
        private static readonly AccessTools.FieldRef<LordJob_Siege, IntVec3> NativeSiegeLocation =
            AccessTools.FieldRefAccess<LordJob_Siege, IntVec3>("siegeSpot");

        internal static void SetStagingPoint(Lord lord, IntVec3 cell)
        {
            // Update both the running duty and the native job's graph configuration for reloads.
            ((LordToilData_Stage)lord.CurLordToil.data).stagingPoint = cell;
            NativeStageLocation((LordJob_StageThenAttack)lord.LordJob) = cell;
        }

        internal static LordToilData_Siege PreparingSiege(Lord lord)
        {
            if (lord.LordJob.GetType() != typeof(LordJob_Siege) || lord.CurLordToil is not LordToil_Travel
                || lord.CurLordToil != lord.Graph?.StartingToil) return null;
            var siege = lord.Graph.lordToils.Find(toil => toil is LordToil_Siege)?.data as LordToilData_Siege;
            return siege?.blueprints.Count == 0 ? siege : null;
        }

        internal static void SetSiegePoint(Lord lord, LordToilData_Siege siege, IntVec3 cell)
        {
            // Travel, the later construction toil, and the graph rebuilt on load share this cell.
            ((LordToil_Travel)lord.CurLordToil).SetDestination(cell);
            siege.siegeCenter = cell;
            NativeSiegeLocation((LordJob_Siege)lord.LordJob) = cell;
        }

        /// <summary>一次事件的候选宿主图及该图允许的外缘方向。先随机排图，再逐图试入口。</summary>
        internal sealed class SpawnMapCandidate
        {
            public Map HostMap;
            public readonly List<PlanetTile> FacingTiles = new();
        }

        /// <summary>
        /// 窗口 = 本次 TryExecuteWorker。目标图 A 保持原版事件参数；跨图计划在选到
        /// 宿主图入口后才生效，MakeLords/worker Postfix 再把已生成的组迁到宿主图。
        /// </summary>
        internal sealed class Plan
        {
            public Map TargetMap;
            public IncidentParms Parms;
            public List<SpawnMapCandidate> CandidateMaps;
            public bool EntrySelected;
            public PlanetTile SelectedFacingTile = PlanetTile.Invalid;
            public Map CrossHostMap;
            public PlanetTile CrossFacingTile;
            public List<IntVec3> CrossAnchors;
            public int CreatedTick;
        }

        internal static Plan Current;

        /// <summary>
        /// 外缘计划窗口内经 PawnGenerator 生成的 pawn（发狂动物/食尸鬼/奇美拉三个**不走
        /// RaidStrategyWorker.MakeLords** 的 worker 由此捕获生成组——它们的 worker 直接
        /// GenSpawn.Spawn + LordMaker.MakeNewLord，没有携带 pawn 列表的公共执行点可挂）。
        /// Begin 前/End 时清空；raid 族只在休眠目标的事件收尾使用此列表。
        /// </summary>
        internal static readonly List<Pawn> WindowPawns = new List<Pawn>();

        /// <summary>PawnGenerator.GeneratePawn Postfix 的捕获入口（仅外缘计划窗口内生效）。</summary>
        internal static void NotifyWindowPawnGenerated(Pawn pawn)
        {
            if (Current != null && pawn != null) WindowPawns.Add(pawn);
        }

        /// <summary>读 LordJob_AssaultColony 私有旗标；专用工作只在被袭击图启动。</summary>
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> sappersRef;
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> breachersRef;
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> avoidGridRef;
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> opportunisticWeaponsRef;

        // =====================================================================
        // 入口（Patches_RaidOuterSpawn 调用）
        // =====================================================================

        /// <summary>
        /// 袭击 worker 的 TryExecuteWorker Prefix：枚举资格缝并建立计划。
        /// allowCrossMap = false 只列触发图候选；true 同时列活跃邻图候选。
        /// incidentDefName = worker.def.defName（事件级"仅本图生成"判据）；workerType =
        /// worker 实际类型（结构性本图判据—— PsychicRitualSiege 等建了计划也无消费者，
        /// 直接短路省掉误导性的"cross-map plan"日志）。
        /// 任何异常 = 无计划（原版行为）。
        /// </summary>
        internal static void BeginIncident(IncidentParms parms, bool allowCrossMap, string incidentDefName = null,
            System.Type workerType = null)
        {
            End(); // 防御性清陈旧（上一次异常泄漏的窗口）
            try
            {
                if (!(parms?.target is Map mapA) || mapA.Disposed) return;
                var tileA = SeamlessTileRegistry.GetMapWorldTile(mapA);
                if (!tileA.Valid)
                {
                    LogNativeEntry(parms, mapA, "map has no seamless world tile");
                    return;
                }
                if (SeamlessDormancyManager.IsDormant(mapA))
                {
                    // Retain only the existing generation window for dormant incident cleanup.
                    // Null candidates leave native entry selection untouched; the group stays on this map.
                    Current = new Plan { TargetMap = mapA, CreatedTick = GenTicks.TicksGame };
                    LogNativeEntry(parms, mapA, "target map is dormant");
                    return;
                }
                bool fixedIncident = IsStructurallyLocalWorker(workerType);
                var incidentPreference = EntryPreference(RimExodusMod.Settings, RaidEntryScope.Incident, incidentDefName);
                string reason = !Enabled ? "outer-edge arrivals are disabled"
                    : incidentPreference == RaidEntryPreference.Native
                        || (incidentPreference == RaidEntryPreference.Default && fixedIncident) ? "incident uses native entry"
                    : ResolvedNativeReason(parms) ?? (!SeamlessEdgeCells.HasSeamEdge(mapA) ? "target map has no usable seam edge" : null);
                if (reason != null)
                {
                    LogNativeEntry(parms, mapA, reason);
                    return;
                }

                // 跨图资格的 parms 层守卫（仅查调用前已稳定的字段）：faction 故意不查——raid 族
                // worker 在 TryExecuteWorker **内部**才解析 faction（TryGenerateRaidInfo →
                // TryResolveRaidFaction），Prefix 时点恒为 null，查了等于全员判否（2026-09 实测
                // "18 候选全部不可用"的根因）。敌对性由 TryRelocateToHost 在 MakeLords 时点复验，
                // 友好援军在那里被安全拦下（计划被弃 = 原版落点）。
                var crossAllowed = allowCrossMap && !fixedIncident && !IsStrategyMapBound(parms.raidStrategy)
                    && SeamlessCombatCoords.Enabled
                    && parms.controllerPawn == null
                    && (parms.attackTargets == null || parms.attackTargets.Count == 0);

                // ---- 枚举候选宿主图：同一图的可入场方向归在一起 ----
                var ring1 = SeamlessMapTopology.NeighborTiles(tileA, mapA.Size.x);
                var candidates = new List<SpawnMapCandidate>();
                foreach (var n in ring1)
                {
                    if (!n.Valid) continue;
                    var mapB = MapAt(n);
                    if (!IsActive(mapB))
                    {
                        // 无图或休眠的邻居不能作为宿主：从 A 朝该邻居的一侧进场。
                        AddCandidate(candidates, mapA, n);
                        continue;
                    }
                    if (!crossAllowed) continue;
                    // 活跃邻图 B 可作宿主，入口朝向尚未加载或休眠的 C。
                    foreach (var u in SeamlessMapTopology.NeighborTiles(n, mapB.Size.x))
                    {
                        if (!u.Valid || u == tileA) continue;
                        if (!IsActive(MapAt(u))) AddCandidate(candidates, mapB, u);
                    }
                }
                var fallback = candidates.Count == 0;
                if (fallback && crossAllowed)
                {
                    // 一跳、二跳都活跃时，允许活跃 B 的任意非 A 外边。
                    foreach (var n in ring1)
                    {
                        if (!n.Valid) continue;
                        var mapB = MapAt(n);
                        if (!IsActive(mapB)) continue;
                        foreach (var u in SeamlessMapTopology.NeighborTiles(n, mapB.Size.x))
                        {
                            if (u.Valid && u != tileA) AddCandidate(candidates, mapB, u);
                        }
                    }
                }
                if (candidates.Count == 0)
                {
                    LogNativeEntry(parms, mapA, crossAllowed ? "no eligible outside edge" : "neighbor-map arrivals are unavailable");
                    return;
                }

                // 地图先随机，再随机各图内的外缘方向；失败时按这个顺序试下一张图。
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = Rand.Range(0, i + 1);
                    (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                }
                foreach (var candidate in candidates)
                {
                    var facing = candidate.FacingTiles;
                    for (int i = facing.Count - 1; i > 0; i--)
                    {
                        int j = Rand.Range(0, i + 1);
                        (facing[i], facing[j]) = (facing[j], facing[i]);
                    }
                }

                Current = new Plan
                {
                    TargetMap = mapA,
                    Parms = parms,
                    CandidateMaps = candidates,
                    CreatedTick = GenTicks.TicksGame,
                };
                if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"Raid outer spawn: target map {mapA.uniqueID} (wt={tileA}), {candidates.Count} candidate maps");
                    if (fallback) sb.Append(" (all-active fallback)");
                    sb.Append(':');
                    foreach (var candidate in candidates)
                        sb.Append($" [host={candidate.HostMap.uniqueID} facings={string.Join(",", candidate.FacingTiles)}]");
                    RimExodusLog.Message(RimExodusLogModule.Combat, sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] RaidOuterSpawn plan failed, vanilla spawn behavior kept: " + ex);
                Current = null;
            }
        }

        private static void AddCandidate(List<SpawnMapCandidate> candidates, Map host, PlanetTile facingTile)
        {
            var candidate = candidates.Find(c => c.HostMap == host);
            if (candidate == null)
            {
                candidate = new SpawnMapCandidate { HostMap = host };
                candidates.Add(candidate);
            }
            if (!candidate.FacingTiles.Contains(facingTile))
                candidate.FacingTiles.Add(facingTile);
        }

        /// <summary>worker 的 TryExecuteWorker Postfix：清计划与窗口捕获。</summary>
        internal static void End()
        {
            Current = null;
            WindowPawns.Clear();
        }

        internal static void LogUnusedPlan(IncidentParms parms)
        {
            var plan = Current;
            if (plan?.CandidateMaps == null || plan.EntrySelected || plan.Parms != parms) return;
            LogNativeEntry(parms, plan.TargetMap, ResolvedNativeReason(parms) ?? "arrival did not use the outer-edge plan");
        }

        // =====================================================================
        // 入场选择：先地图、再该图外缘；全失败才回到 A 的无约束选点
        // =====================================================================

        /// <summary>
        /// 由 CellFinder 的 4 参入口传入调用方原版 validator。返回 true 表示已处理本次
        /// 选点；全部候选图失败时清掉计划并返回 false，让 CellFinder 在触发图 A 上按
        /// 未约束的原有接缝池和同一 validator 选点。
        /// </summary>
        internal static bool TryResolveEntry(Predicate<IntVec3> validator, Map map,
            out IntVec3 entry, out bool found)
        {
            entry = IntVec3.Invalid;
            found = false;
            var plan = Current;
            if (plan == null || map != plan.TargetMap || plan.CandidateMaps == null) return false;
            // Raid strategy resolution occurs between BeginIncident and the native entry query.
            var nativeReason = ResolvedNativeReason(plan.Parms);
            if (nativeReason != null)
            {
                LogNativeEntry(plan.Parms, map, nativeReason);
                plan.CandidateMaps = null;
                return false;
            }
            if (SeamlessDormancyManager.IsDormant(map)) return false;
            if (GenTicks.TicksGame - plan.CreatedTick > 120)
            {
                Current = null;
                return false;
            }

            // Distributed 等 arrival mode 会多次取格。第一格确定宿主后，后续格固定同一图，
            // 不在已经生成部分 pawn 后改宿主。
            if (plan.EntrySelected)
            {
                if (plan.CrossHostMap != null)
                    found = TryFindValidatedCell(SeamlessEdgeCells.GetSeamEdgeCellsWithBandFallback(map),
                        validator, out entry);
                else
                {
                    var facing = new List<IntVec3>();
                    SeamlessEdgeCells.PopulateFacingCells(map, plan.SelectedFacingTile, facing);
                    found = TryFindValidatedCell(facing, validator, out entry);
                }
                if (!found)
                    Log.Warning($"[RimExodus] Raid outer spawn: no valid entry for another pawn group on target map {map.uniqueID} after host selection.");
                return true;
            }

            var tileA = SeamlessTileRegistry.GetMapWorldTile(map);
            bool fixedStrategy = IsStrategyMapBound(plan.Parms.raidStrategy);
            foreach (var candidate in plan.CandidateMaps)
            {
                if (fixedStrategy && candidate.HostMap != map) continue;
                if (!IsActive(candidate.HostMap)) continue;
                foreach (var facingTile in candidate.FacingTiles)
                {
                    if (candidate.HostMap == map)
                    {
                        var facing = new List<IntVec3>();
                        SeamlessEdgeCells.PopulateFacingCells(map, facingTile, facing);
                        if (!TryFindValidatedCell(facing, validator, out entry)) continue;
                        plan.EntrySelected = true;
                        plan.SelectedFacingTile = facingTile;
                        found = true;
                        RimExodusLog.Message(RimExodusLogModule.Combat,
                            $"Raid outer spawn: entry {entry} on target map {map.uniqueID} facing wt={facingTile}.");
                        return true;
                    }

                    // B 图必须有可走到 A 侧缝的入口。原生 worker 仍以 A 为 target，故同时
                    // 要在 A 找到合法临时出生格；迁移在生成后、向玩家展示前完成。
                    var anchors = new List<IntVec3>();
                    if (!TryBuildCrossAnchors(candidate.HostMap, facingTile, tileA, anchors)) continue;
                    if (!TryFindValidatedCell(SeamlessEdgeCells.GetSeamEdgeCellsWithBandFallback(map),
                            validator, out entry)) continue;
                    plan.EntrySelected = true;
                    plan.CrossHostMap = candidate.HostMap;
                    plan.CrossFacingTile = facingTile;
                    plan.CrossAnchors = anchors;
                    found = true;
                    RimExodusLog.Message(RimExodusLogModule.Combat,
                        $"Raid outer spawn: entry {entry} on target map {map.uniqueID}; host map " +
                        $"{candidate.HostMap.uniqueID} facing wt={facingTile} ({anchors.Count} anchors).");
                    return true;
                }
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: no valid entry on candidate map {candidate.HostMap.uniqueID}, trying next map.");
            }

            Current = null;
            Log.Warning($"[RimExodus] Raid outer spawn: all candidate maps failed for target map {map.uniqueID}, using normal target-map entry.");
            return false;
        }

        /// <summary>
        /// 少数 arrival mode 直接使用预设 spawnCenter、不调用 4 参边缘选点。
        /// 原版已经成功在 A 生成了组，此时只需按相同随机地图顺序尝试 B 的入口；
        /// 遇到 A 候选或所有 B 失败则保留原版落点。
        /// </summary>
        private static void SelectMapAfterVanillaEntry(Plan plan)
        {
            if (plan == null || plan.EntrySelected || plan.CandidateMaps == null) return;
            var mapA = plan.TargetMap;
            if (!IsActive(mapA)) return;
            var tileA = SeamlessTileRegistry.GetMapWorldTile(mapA);
            foreach (var candidate in plan.CandidateMaps)
            {
                if (!IsActive(candidate.HostMap)) continue;
                if (candidate.HostMap == mapA)
                {
                    plan.EntrySelected = true;
                    LogNativeEntry(plan.Parms, mapA, "selected the target-map candidate after vanilla placement");
                    return;
                }
                foreach (var facingTile in candidate.FacingTiles)
                {
                    var anchors = new List<IntVec3>();
                    if (!TryBuildCrossAnchors(candidate.HostMap, facingTile, tileA, anchors)) continue;
                    plan.EntrySelected = true;
                    plan.CrossHostMap = candidate.HostMap;
                    plan.CrossFacingTile = facingTile;
                    plan.CrossAnchors = anchors;
                    return;
                }
            }
            Current = null;
            Log.Warning($"[RimExodus] Raid outer spawn: no usable host map for target map {mapA.uniqueID} after vanilla entry; keeping target-map spawn.");
        }

        private static bool TryFindValidatedCell(List<IntVec3> cells, Predicate<IntVec3> validator,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (cells == null || cells.Count == 0) return false;
            var scratch = new List<IntVec3>(cells);
            for (int i = scratch.Count - 1; i > 0; i--)
            {
                int j = Rand.Range(0, i + 1);
                (scratch[i], scratch[j]) = (scratch[j], scratch[i]);
            }
            foreach (var cell in scratch)
            {
                try
                {
                    if (!validator(cell)) continue;
                    result = cell;
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] Raid outer spawn entry validation failed at {cell}: {ex}");
                }
            }
            return false;
        }

        // =====================================================================
        // 跨图生成消费（RaidStrategyWorker.MakeLords Postfix）
        // =====================================================================

        /// <summary>
        /// Validate the resolved walk-in raid before moving its complete native squads.
        /// </summary>
        internal static bool TryRelocateToHost(IncidentParms parms, List<Pawn> pawns)
        {
            var plan = Current;
            if (plan?.CandidateMaps == null || parms?.target != plan.TargetMap) return false;
            if (SeamlessDormancyManager.IsDormant(plan.TargetMap)) return false;
            var nativeReason = ResolvedNativeReason(parms);
            if (nativeReason != null || IsStrategyMapBound(parms.raidStrategy))
            {
                if (!plan.EntrySelected)
                    LogNativeEntry(parms, plan.TargetMap, nativeReason ?? "the strategy's duties must run on the target map");
                plan.CandidateMaps = null;
                return false;
            }
            if (!plan.EntrySelected)
            {
                try { SelectMapAfterVanillaEntry(plan); }
                catch (Exception ex)
                {
                    Log.Error("[RimExodus] Raid outer spawn host selection failed; keeping target-map spawn: " + ex);
                    Current = null;
                    return false;
                }
            }
            if (plan.CrossHostMap == null) return false;
            // 静默守卫的可诊断化（Combat 模块门控）：拒绝迁移的原因必须可见——"按设计排除"
            //（空投/围攻/工兵等）与"坏了"在日志上不可区分的话，回归排查会绕远路。
            bool Decline(string reason)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: cross plan declined at MakeLords — {reason}.");
                return false;
            }
            try
            {
                // arrival mode 此时已解析（MakeLords 晚于 TryGenerateRaidInfo）——仅步行族跨图。
                if (parms.raidArrivalMode == null || !parms.raidArrivalMode.walkIn)
                    return Decline($"arrival mode {parms.raidArrivalMode?.defName ?? "null"} is not walk-in");
                if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer))
                    return Decline($"faction {parms.faction?.def?.defName ?? "null"} is not hostile to player");
                if (parms.controllerPawn != null) return Decline("controlled raid (controllerPawn)");
                if (parms.attackTargets != null && parms.attackTargets.Count > 0) return Decline("targeted raid (attackTargets)");
                if (pawns == null || pawns.Count == 0) return Decline("empty pawn group");

                var targetMap = (Map)parms.target;
                var targetTile = SeamlessTileRegistry.GetMapWorldTile(targetMap);
                var jobs = new Dictionary<Lord, LordJob>();
                sappersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("sappers");
                breachersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");
                avoidGridRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("useAvoidGridSmart");
                opportunisticWeaponsRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("canPickUpOpportunisticWeapons");
                foreach (var pawn in pawns)
                {
                    if (pawn?.Spawned != true || pawn.Map != targetMap)
                        return Decline("pawn group has not arrived on the target map");
                    var lord = pawn.GetLord();
                    if (lord == null) return Decline("pawn has no native assault squad");
                    if (jobs.ContainsKey(lord)) continue;
                    if (lord.LordJob.GetType() == typeof(LordJob_AssaultColony) && lord.LordJob is LordJob_AssaultColony assault
                        && (sappersRef(assault) || breachersRef(assault)))
                    {
                        if (!targetTile.Valid) return Decline("sapper/breacher target has no seamless world tile");
                        jobs.Add(lord, new LordJob_SeamlessRaidMarch(lord.faction, targetTile,
                            lord.GetUniqueLoadID(), sappersRef(assault), breachersRef(assault), parms.canKidnap,
                            parms.canTimeoutOrFlee, parms.canSteal, marching: true,
                            useAvoidGridSmart: avoidGridRef(assault), canPickUpOpportunisticWeapons: opportunisticWeaponsRef(assault)));
                    }
                    else if (CanStageOnHost(lord) || PreparingSiege(lord) != null || SeamlessRaidContinuity.IsPursuitPhase(lord))
                    {
                        jobs.Add(lord, null);
                    }
                    else
                    {
                        return Decline($"native phase {lord.CurLordToil?.GetType().Name ?? "unknown"} stays on its map");
                    }
                }

                if (!TryTransferGroupToHost(parms, pawns, jobs, out var hostMap)) return false;

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: relocated {pawns.Count} raiders of {parms.faction.def.defName} from map " +
                    $"{targetMap.uniqueID} to host map {hostMap.uniqueID} (facing wt={plan.CrossFacingTile}).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] Raid outer spawn could not finish relocation: " + ex);
                return false;
            }
        }

        internal static bool KeepDormantRaidLocal(IncidentParms parms, List<Pawn> pawns)
        {
            if (!(parms?.target is Map map) || !SeamlessDormancyManager.IsDormant(map)
                || !SeamlessTileRegistry.GetMapWorldTile(map).Valid) return false;
            try
            {
                var enemies = 0;
                if (pawns != null)
                {
                    foreach (var pawn in pawns)
                    {
                        if (pawn == null || !pawn.Spawned || pawn.Map != map || pawn.Dead) continue;
                        // Sleep removed the old contents once. These new pawns registered during SpawnSetup.
                        // Native deregistration is buffered, so this is safe even inside a Thing tick.
                        Find.TickManager.DeRegisterAllTickabilityFor(pawn);
                        if (pawn.HostileTo(Faction.OfPlayer)) enemies++;
                    }
                }
                if (enemies > 0)
                {
                    var observer = SeamlessDormancyManager.GetSleepObserver(map);
                    var coordinates = Find.WorldGrid.LongLatOf(map.Tile);
                    var position = coordinates.y.ToStringLatitude() + " " + coordinates.x.ToStringLongitude();
                    var place = map.Parent.LabelCap;
                    var body = observer != null
                        ? "RimExodus_DormantRaidBody".Translate(observer, position, place)
                        : "RimExodus_DormantRaidBodyUnobserved".Translate(position, place);
                    Find.LetterStack.ReceiveLetter("RimExodus_DormantRaidLabel".Translate(), body,
                        LetterDefOf.ThreatBig, map.Parent);
                    RimExodusLog.Message(RimExodusLogModule.Combat,
                        $"Raid outer spawn: {enemies} enemies stay on dormant map {map.uniqueID}; distant smoke warning sent.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] Dormant raid handling failed; group kept on target map: " + ex);
            }
            return true;
        }

        // The explicit workers either keep their native squad or have pawn-local AI.
        internal static bool TryRelocateWindowGroup(IncidentParms parms)
        {
            if (KeepDormantRaidLocal(parms, WindowPawns)) return false;
            var plan = Current;
            if (plan?.CandidateMaps == null) return false;
            if (!plan.EntrySelected)
            {
                try { SelectMapAfterVanillaEntry(plan); }
                catch (Exception ex)
                {
                    Log.Error("[RimExodus] Raid outer spawn host selection failed; keeping target-map spawn: " + ex);
                    Current = null;
                    return false;
                }
            }
            if (plan.CrossHostMap == null) return false;
            try
            {
                if (!(parms?.target is Map mapA)) return false;
                if (WindowPawns.Count == 0) return false;
                var pawns = new List<Pawn>();
                for (int i = 0; i < WindowPawns.Count; i++)
                {
                    var p = WindowPawns[i];
                    if (p != null && p.Spawned && !p.Destroyed && p.Map == mapA) pawns.Add(p);
                }
                if (pawns.Count == 0) return false;

                if (!TryTransferGroupToHost(parms, pawns, null, out var hostMap)) return false;

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: relocated {pawns.Count} spawned pawns ({pawns[0].KindLabel}) from map " +
                    $"{mapA.uniqueID} to host map {hostMap.uniqueID} (facing wt={plan.CrossFacingTile}); " +
                    "march to target handled by cross-map melee/pursuit chain.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] Raid outer spawn could not finish moving the captured group: " + ex);
                return false;
            }
            finally
            {
                WindowPawns.Clear(); // 即取即清：成功失败都不留到下一窗口（End 亦兜底清）
            }
        }

        // Preflight the entire spawn group before changing any Pawn or Lord membership.
        private static bool TryTransferGroupToHost(IncidentParms parms, List<Pawn> pawns,
            Dictionary<Lord, LordJob> jobs, out Map hostMap)
        {
            hostMap = null;
            var plan = Current;
            // 时效守卫（与 TryResolveEntry 同款）：计划生命周期 = 本次 TryExecuteWorker。
            if (GenTicks.TicksGame - plan.CreatedTick > 120)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Raid outer spawn: cross plan expired at relocation (plan tick leak — vanilla kept).");
                Current = null;
                return false;
            }
            hostMap = plan.CrossHostMap;
            if (hostMap.Disposed || SeamlessDormancyManager.IsDormant(hostMap))
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: host map {hostMap.uniqueID} no longer active at relocation (disposed={hostMap.Disposed}).");
                return false;
            }
            if (!(parms?.target is Map mapA) || mapA == hostMap)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Raid outer spawn: relocation skipped — parms.target is not a valid target map.");
                return false;
            }

            // 预放置（全有或全无）：每 pawn 从打乱后的锚点散布。
            var anchors = plan.CrossAnchors;
            var cells = new List<IntVec3>(pawns.Count);
            foreach (var pawn in pawns)
            {
                var placed = IntVec3.Invalid;
                for (int i = anchors.Count - 1; i >= 0; i--)
                {
                    int j = Rand.Range(0, i + 1);
                    (anchors[i], anchors[j]) = (anchors[j], anchors[i]);
                    var c = CellFinder.RandomClosewalkCellNear(anchors[i], hostMap, 8);
                    if (c.IsValid && c.Standable(hostMap))
                    {
                        placed = c;
                        break;
                    }
                }
                if (!placed.IsValid)
                {
                    RimExodusLog.Message(RimExodusLogModule.Combat,
                        $"Raid outer spawn: relocation aborted, no standable cell on host map {hostMap.uniqueID} for {pawn.LabelShort}.");
                    return false;
                }
                cells.Add(placed);
            }

            var groups = new Dictionary<Lord, List<Pawn>>();
            foreach (var pawn in pawns)
            {
                var lord = pawn.GetLord();
                if (lord == null) continue;
                if (!groups.TryGetValue(lord, out var members))
                {
                    members = new List<Pawn>();
                    groups.Add(lord, members);
                }
                members.Add(pawn);
            }
            var continuity = SeamlessRaidContinuity.Current;
            if (groups.Count != 0 && continuity == null) return false;
            foreach (var pair in groups)
            {
                if (pair.Key.ownedPawns.Count != pair.Value.Count || pair.Key.ownedBuildings.Count != 0
                    || pair.Key.ownedCorpses.Count != 0)
                {
                    LogNativeEntry(parms, mapA, $"Lord {pair.Key.loadID} has members or objects outside the moving group");
                    return false;
                }
            }

            // Choose map-local objectives from the planned landing cells before moving any pawn.
            var setupPoints = new Dictionary<Lord, (IntVec3 Cell, LordToilData_Siege Siege)>();
            foreach (var pair in groups)
            {
                bool stages = CanStageOnHost(pair.Key);
                var siege = PreparingSiege(pair.Key);
                if (!stages && siege == null) continue;
                var entry = cells[pawns.IndexOf(pair.Value[0])];
                var point = RCellFinder.FindSiegePositionFrom(entry, hostMap, allowRoofed: stages, errorOnFail: false);
                setupPoints.Add(pair.Key, (point.IsValid ? point : entry, siege));
            }

            SeamlessTickThrottle.Unthrottle(hostMap, "raid outer spawn (host of cross-map raid)");
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                pawn.GetLord()?.RemovePawn(pawn);
                pawn.DeSpawn();
                var rot = Rot4.FromAngleFlat((hostMap.Center - cells[i]).AngleFlat);
                GenSpawn.Spawn(pawn, cells[i], hostMap, rot);
            }
            foreach (var pair in groups)
            {
                var lord = pair.Key;
                LordJob replacement = null;
                jobs?.TryGetValue(lord, out replacement);
                bool hasSetup = setupPoints.TryGetValue(lord, out var setup);
                if (hasSetup)
                {
                    if (setup.Siege != null) SetSiegePoint(lord, setup.Siege, setup.Cell);
                    else SetStagingPoint(lord, setup.Cell);
                }
                continuity.RelocateSpawnedSquad(lord, hostMap, pair.Value, replacement);
                if (hasSetup)
                {
                    var task = setup.Siege != null ? "Siege" : "StageThenAttack";
                    RimExodusLog.Message(RimExodusLogModule.Combat,
                        $"Raid outer spawn: {task} Lord {lord.loadID} sets up at {setup.Cell} on host map {hostMap.uniqueID}.");
                }
            }
            return true;
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>取世界 tile 上的 MapParent 图（原生口径，不过滤休眠——勿用 TryGetMapByWorldTile）。</summary>
        private static Map MapAt(PlanetTile worldTile)
        {
            var map = Find.World.worldObjects.MapParentAt(worldTile)?.Map;
            return map != null && !map.Disposed ? map : null;
        }

        /// <summary>活跃 = 有图且未休眠（降频算活跃）。</summary>
        private static bool IsActive(Map map)
        {
            return map != null && !SeamlessDormancyManager.IsDormant(map);
        }

        /// <summary>
        /// 跨图候选的锚点集：hostMap 朝 facingTile 的传送点格中，可站且能走到"朝 A 侧缝"的格
        /// （防把袭击者放进密封口袋——行军必须能到达 host→A 的接缝）。通过 anchors 返回。
        /// </summary>
        private static bool TryBuildCrossAnchors(Map hostMap, PlanetTile facingTile, PlanetTile tileA, List<IntVec3> anchors)
        {
            anchors.Clear();
            var facing = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(hostMap, facingTile, facing);
            if (facing.Count == 0) return false;

            // host 朝 A 侧的可站 spot（行军可达性判定的目的地集合）。
            var aSide = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(hostMap, tileA, aSide);
            var aSideStandable = new List<IntVec3>();
            foreach (var c in aSide)
            {
                if (c.Standable(hostMap)) aSideStandable.Add(c);
            }
            if (aSideStandable.Count == 0) return false;

            foreach (var anchor in facing)
            {
                if (!anchor.Standable(hostMap)) continue;
                foreach (var dest in aSideStandable)
                {
                    // 口径对齐原版 EdgeWalkIn 谓词（NoPassClosedDoors + Danger.Some）——
                    // 只验证"能走到 A 侧缝"，段内实际寻路由原版 A* 与桥接链完成。
                    if (hostMap.reachability.CanReach(anchor, dest, PathEndMode.Touch,
                            TraverseMode.NoPassClosedDoors, Danger.Some))
                    {
                        anchors.Add(anchor);
                        break;
                    }
                }
            }
            return anchors.Count > 0;
        }
    }
}
