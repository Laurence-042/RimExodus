using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimExodus
{
    internal enum RaidEntryPreference { Default, Native, Outer }
    internal enum RaidEntryScope { Incident, Strategy, Arrival }

    /// <summary>
    /// Opens an incident-scoped arrival plan and owns the player-configurable eligibility rules.
    /// Supported raids resolve their native attacked-map anchor, then SeamlessRaidDirectSpawn maps
    /// that anchor to a neighboring host before any pawn, pod, building, or Lord is created there.
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
            // MechClusterDrop likewise materializes its complete map-bound cluster on the chosen host.
            return worker != null && ((worker != typeof(RaidStrategyWorker_Siege)
                    && worker != typeof(RaidStrategyWorker_SiegeMechanoid)
                    && typeof(RaidStrategyWorker_Siege).IsAssignableFrom(worker))
                || typeof(RaidStrategyWorker_PsychicRitualSiege).IsAssignableFrom(worker));
        }

        internal static bool SupportsDirectStrategy(RaidStrategyDef strategy)
        {
            var worker = strategy?.workerClass;
            return worker == typeof(RaidStrategyWorker_ImmediateAttack)
                || worker == typeof(RaidStrategyWorker_ImmediateAttackSmart)
                || worker == typeof(RaidStrategyWorker_StageThenAttack)
                || worker == typeof(RaidStrategyWorker_Siege)
                || worker == typeof(RaidStrategyWorker_SiegeMechanoid)
                || worker == typeof(RaidStrategyWorker_ImmediateAttackSappers)
                || worker == typeof(RaidStrategyWorker_ImmediateAttackBreaching)
                || worker == typeof(RaidStrategyWorker_ImmediateAttackBreachingSmart);
        }

        internal static bool SupportsDirectArrival(PawnsArrivalModeDef arrival)
        {
            var worker = arrival?.workerClass;
            if (worker == null) return false;
            return worker == typeof(PawnsArrivalModeWorker_EdgeWalkIn)
                || worker == typeof(PawnsArrivalModeWorker_EdgeWalkInDarkness)
                || worker == typeof(PawnsArrivalModeWorker_EdgeWalkInDistributed)
                || worker == typeof(PawnsArrivalModeWorker_EdgeWalkInDistributedGroups)
                || worker == typeof(PawnsArrivalModeWorker_EdgeWalkInGroups)
                || worker == typeof(PawnsArrivalModeWorker_EdgeDrop)
                || worker == typeof(PawnsArrivalModeWorker_EdgeDropGroups)
                || worker == typeof(PawnsArrivalModeWorker_CenterDrop)
                || worker == typeof(PawnsArrivalModeWorker_RandomDrop)
                || worker == typeof(PawnsArrivalModeWorker_ClusterDrop);
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
            if (mode != null && !SupportsDirectArrival(mode))
                return $"arrival mode {mode.defName} requires native placement";
            var preference = EntryPreference(RimExodusMod.Settings, RaidEntryScope.Arrival, mode?.defName);
            if (preference == RaidEntryPreference.Native)
                return $"arrival mode {mode.defName} is configured to use native entry";
            if (mode != null && preference == RaidEntryPreference.Default && !mode.walkIn)
                return $"arrival mode {mode.defName} defaults to native placement";
            return null;
        }

        private static void LogNativeEntry(IncidentParms parms, Map map, string reason)
        {
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Raid outer spawn: using native entry on target map {map.uniqueID} "
                + $"(strategy={parms.raidStrategy?.defName ?? parms.raidStrategy?.workerClass?.Name ?? "unresolved"}; {reason}).");
        }

        /// <summary>一次事件的候选宿主图及该图允许的外缘方向。先随机排图，再逐图试入口。</summary>
        internal sealed class SpawnMapCandidate
        {
            public Map HostMap;
            public readonly List<PlanetTile> FacingTiles = new();
        }

        /// <summary>
        /// 窗口 = 本次 TryExecuteWorker。目标图 A 保持原版事件参数；只有原版空间生成调用
        /// 临时看到最终宿主图，避免先在 A 生成后再迁移产生地图阶段副作用。
        /// </summary>
        internal sealed class Plan
        {
            public Map TargetMap;
            public IncidentParms Parms;
            public List<SpawnMapCandidate> CandidateMaps;
            public bool AllowCrossMap;
            public string IncidentDefName;
            public bool EntrySelected;
            public PlanetTile SelectedFacingTile = PlanetTile.Invalid;
            public Map CrossHostMap;
            public PlanetTile FarFacingTile = PlanetTile.Invalid;
            public IntVec3 SharedSeamPoint = IntVec3.Invalid;
            public IntVec3 FarSeamPoint = IntVec3.Invalid;
            public IntVec3 NativeSpawnCenter = IntVec3.Invalid;
            public IntVec3 HostSpawnCenter = IntVec3.Invalid;
            public int DirectionRotation = -1;
            public int GeneratedPawnCount = -1;
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

        internal static LordJob MarchingReplacement(Lord lord, Map attackedMap, IncidentParms parms)
        {
            if (lord?.LordJob?.GetType() != typeof(LordJob_AssaultColony)
                || lord.LordJob is not LordJob_AssaultColony assault || attackedMap == null) return null;
            sappersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("sappers");
            breachersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");
            if (!sappersRef(assault) && !breachersRef(assault)) return null;
            var targetTile = SeamlessTileRegistry.GetMapWorldTile(attackedMap);
            if (!targetTile.Valid) return null;
            avoidGridRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("useAvoidGridSmart");
            opportunisticWeaponsRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("canPickUpOpportunisticWeapons");
            return new LordJob_SeamlessRaidMarch(lord.faction, targetTile, lord.GetUniqueLoadID(),
                sappersRef(assault), breachersRef(assault), parms?.canKidnap ?? true,
                parms?.canTimeoutOrFlee ?? true, parms?.canSteal ?? true, marching: true,
                useAvoidGridSmart: avoidGridRef(assault),
                canPickUpOpportunisticWeapons: opportunisticWeaponsRef(assault));
        }

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
                // "18 候选全部不可用"的根因）。敌对性由直接生成计划在原版解析完 faction 后复验，
                // 友好援军会安全保留原版落点。
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
                    AllowCrossMap = allowCrossMap,
                    IncidentDefName = incidentDefName,
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
            if (SeamlessRaidDirectSpawn.ResolvingNativeAnchor) return false;
            if (SeamlessRaidDirectSpawn.TryResolveHostEntry(validator, map, out entry, out found)) return true;
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
                var facing = new List<IntVec3>();
                SeamlessEdgeCells.PopulateFacingCells(map, plan.SelectedFacingTile, facing);
                found = TryFindValidatedCell(facing, validator, out entry);
                if (!found)
                    Log.Warning($"[RimExodus] Raid outer spawn: no valid entry for another pawn group on target map {map.uniqueID} after host selection.");
                return true;
            }

            foreach (var candidate in plan.CandidateMaps)
            {
                // Cross-map raids are consumed by SeamlessRaidDirectSpawn before any spatial
                // call. This selector remains only for map-bound incidents such as swarm travel.
                if (candidate.HostMap != map) continue;
                if (!IsActive(candidate.HostMap)) continue;
                foreach (var facingTile in candidate.FacingTiles)
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
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: no valid entry on candidate map {candidate.HostMap.uniqueID}, trying next map.");
            }

            Current = null;
            Log.Warning($"[RimExodus] Raid outer spawn: all candidate maps failed for target map {map.uniqueID}, using normal target-map entry.");
            return false;
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

    }
}
