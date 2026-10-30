using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 只在 caravan-exit 日志开启时记录撤离选点与重复 Goto 的现场；不读会触发 Rebuild 的
    /// IsExitCell/Grid 属性，以免诊断本身修复或改变待排查的旧网格状态。
    /// </summary>
    internal static class SeamlessEvacuationDiagnostics
    {
        private sealed class PawnState
        {
            public int Tick = -1;
            public int JobsThisTick;
            public int LastSummaryTick = -600;
            public int LastDetailTick = -600;
            public int LastExitAttemptTick = -600;
            public int LastSpotEntryTick = -600;
            public int SelectionTick = -1;
            public IntVec3 SelectionCell = IntVec3.Invalid;
            public string SelectionSource = "unknown";
            public TraverseMode SelectionMode;
            public bool SelectionCanBash;
            public bool SelectionLogged;
            public string LastLoggedSelectionSource;
            public TraverseMode LastLoggedSelectionMode;
            public string LastLoggedDutyCanDig;
            public string LastExitDecision;
        }

        private static readonly Dictionary<Pawn, PawnState> States = new();
        private static readonly AccessTools.FieldRef<ExitMapGrid, bool> DirtyRef =
            AccessTools.FieldRefAccess<ExitMapGrid, bool>("dirty");
        private static readonly AccessTools.FieldRef<ExitMapGrid, BoolGrid> GridRef =
            AccessTools.FieldRefAccess<ExitMapGrid, BoolGrid>("exitMapGrid");

        private static PawnState StateFor(Pawn pawn)
        {
            if (States.TryGetValue(pawn, out var state)) return state;
            if (States.Count >= 128) States.Clear();
            state = new PawnState();
            States[pawn] = state;
            return state;
        }

        internal static void RecordBestExitSelection(Pawn pawn, IntVec3 cell, bool found,
            TraverseMode mode, bool canBash, string source)
        {
            if (!RimExodusLog.Enabled(RimExodusLogModule.CaravanExit) || pawn == null) return;
            var state = StateFor(pawn);
            state.SelectionTick = GenTicks.TicksGame;
            state.SelectionCell = found ? cell : IntVec3.Invalid;
            state.SelectionSource = source;
            state.SelectionMode = mode;
            state.SelectionCanBash = canBash;
            string dutyCanDig = pawn.mindState?.duty?.canDig.ToString() ?? "null";
            if (!state.SelectionLogged || state.LastLoggedSelectionSource != source
                || state.LastLoggedSelectionMode != mode || state.LastLoggedDutyCanDig != dutyCanDig)
            {
                state.SelectionLogged = true;
                state.LastLoggedSelectionSource = source;
                state.LastLoggedSelectionMode = mode;
                state.LastLoggedDutyCanDig = dutyCanDig;
                Log.Message($"[RimExodus] [caravan-exit] Best exit selection: {pawn.LabelShort} "
                    + $"tick={state.SelectionTick} map={pawn.Map?.uniqueID} position={pawn.Position} "
                    + $"source={source} found={found} target={cell} mode={mode} "
                    + $"duty={pawn.mindState?.duty?.def?.defName ?? "none"} dutyCanDig={dutyCanDig} canBash={canBash}.");
            }
        }

        internal static void OnEvacuationJobStarted(Pawn pawn, Job job)
        {
            if (!RimExodusLog.Enabled(RimExodusLogModule.CaravanExit) || pawn?.Map == null || job == null) return;
            int tick = GenTicks.TicksGame;
            var state = StateFor(pawn);
            if (state.Tick != tick)
            {
                state.Tick = tick;
                state.JobsThisTick = 0;
            }
            state.JobsThisTick++;

            if (tick < state.LastSummaryTick || tick - state.LastSummaryTick >= 600)
            {
                state.LastSummaryTick = tick;
                Log.Message($"[RimExodus] [caravan-exit] Evacuation grant: {pawn.LabelShort} "
                    + $"(faction={pawn.Faction?.Name ?? "none"}, map={pawn.Map.uniqueID}, "
                    + $"lord={pawn.GetLord()?.LordJob?.GetType().Name ?? "none"}, "
                    + $"duty={pawn.mindState?.duty?.def?.defName ?? "none"}, "
                    + $"dutyCanDig={pawn.mindState?.duty?.canDig.ToString() ?? "null"}, "
                    + $"locomotion={job.locomotionUrgency}, position={pawn.Position}, target={job.targetA.Cell}, "
                    + $"exitMapOnArrival={job.exitMapOnArrival}, failIfCantJoin={job.failIfCantJoinOrCreateCaravan}, "
                    + $"playerForced={job.playerForced}).");
            }

            if (state.JobsThisTick != 3 || (tick >= state.LastDetailTick && tick - state.LastDetailTick < 600)) return;
            state.LastDetailTick = tick;
            LogRepeatedJob(pawn, job, state);
        }

        private static void LogRepeatedJob(Pawn pawn, Job job, PawnState state)
        {
            var map = pawn.Map;
            var current = pawn.Position;
            var target = job.targetA.Cell;
            bool selectionMatches = state.SelectionTick == GenTicks.TicksGame && state.SelectionCell == target;
            string source = selectionMatches ? state.SelectionSource : "unknown-or-other-selector";
            string mode = selectionMatches ? state.SelectionMode.ToString() : "unknown";
            string canBash = selectionMatches ? state.SelectionCanBash.ToString() : "unknown";
            string currentExit = RawExitCell(map, current);
            string targetExit = RawExitCell(map, target);
            bool mapUsesGrid = map.exitMapGrid.MapUsesExitGrid;
            bool gridDirty = DirtyRef(map.exitMapGrid);
            string effectiveCurrentExit = !mapUsesGrid ? "False"
                : gridDirty ? "pending-rebuild" : currentExit;
            string effectiveTargetExit = !mapUsesGrid ? "False"
                : gridDirty ? "pending-rebuild" : targetExit;
            bool currentSpot = SeamlessMapTransferTrigger.HasEnterSpotAt(map, current);
            bool targetSpot = SeamlessMapTransferTrigger.HasEnterSpotAt(map, target);
            int worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var band = worldTile >= 0 ? SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x) : null;
            string targetReachable = selectionMatches && target.InBounds(map)
                ? map.reachability.CanReach(current, target, PathEndMode.OnCell,
                    TraverseParms.For(pawn, Danger.Deadly, state.SelectionMode, state.SelectionCanBash)).ToString()
                : "unknown";

            Log.Message($"[RimExodus] [caravan-exit] Repeated exit job: {pawn.LabelShort} "
                + $"tick={GenTicks.TicksGame} map={map.uniqueID}(wt={worldTile},size={map.Size},"
                + $"parent={map.Parent?.GetType().Name ?? "none"},home={map.IsPlayerHome}) "
                + $"position={current} target={target} atTarget={current == target} "
                + $"exitMapOnArrival={job.exitMapOnArrival} failIfCantJoin={job.failIfCantJoinOrCreateCaravan} "
                + $"playerForced={job.playerForced} "
                + $"selection={source} mode={mode} effectiveCanDig={(selectionMatches ? (state.SelectionMode == TraverseMode.PassAllDestroyableThings).ToString() : "unknown")} "
                + $"dutyCanDig={pawn.mindState?.duty?.canDig.ToString() ?? "null"} canBash={canBash} "
                + $"canReachMapEdge={pawn.CanReachMapEdge()} targetReachable={targetReachable} "
                + $"patherMoving={pawn.pather.Moving} nextCell={pawn.pather.nextCell}.");
            Log.Message($"[RimExodus] [caravan-exit] Exit cells: {pawn.LabelShort} "
                + $"mapUsesExitGrid={mapUsesGrid} gridDirty={gridDirty} "
                + $"currentExit={effectiveCurrentExit} targetExit={effectiveTargetExit} "
                + $"rawCurrentExit={currentExit} rawTargetExit={targetExit} "
                + $"currentSpot={currentSpot} targetSpot={targetSpot} "
                + $"currentInBand={band?.Band.Contains(current).ToString() ?? "unknown"} "
                + $"targetInBand={band?.Band.Contains(target).ToString() ?? "unknown"} "
                + $"{SeamlessExitSpotFinder.DescribeEnterSpots(map)} "
                + $"corners=[{TerrainAt(map, new IntVec3(0, 0, 0))},"
                + $"{TerrainAt(map, new IntVec3(map.Size.x - 1, 0, 0))},"
                + $"{TerrainAt(map, new IntVec3(0, 0, map.Size.z - 1))},"
                + $"{TerrainAt(map, new IntVec3(map.Size.x - 1, 0, map.Size.z - 1))}].");
            Log.Message($"[RimExodus] [caravan-exit] Cell contents: {pawn.LabelShort} "
                + $"current={DescribeCell(map, current)} target={DescribeCell(map, target)}.");
        }

        internal static void OnTryExitMap(Pawn pawn, Job job, string decision)
        {
            if (!RimExodusLog.Enabled(RimExodusLogModule.CaravanExit) || pawn?.Map == null) return;
            int tick = GenTicks.TicksGame;
            var state = StateFor(pawn);
            if (tick >= state.LastExitAttemptTick && tick - state.LastExitAttemptTick < 600
                && state.LastExitDecision == decision) return;
            state.LastExitAttemptTick = tick;
            state.LastExitDecision = decision;
            Log.Message($"[RimExodus] [caravan-exit] JobDriver_Goto.TryExitMap: {pawn.LabelShort} "
                + $"tick={tick} decision={decision} map={pawn.Map.uniqueID} position={pawn.Position} "
                + $"target={job?.targetA.Cell.ToString() ?? "none"} targetSpot={SeamlessMapTransferTrigger.HasEnterSpotAt(pawn.Map, job?.targetA.Cell ?? IntVec3.Invalid)}.");
        }

        internal static void OnEnteringSpot(Pawn pawn, IntVec3 cell, Map map)
        {
            if (!RimExodusLog.Enabled(RimExodusLogModule.CaravanExit) || pawn == null || map == null
                || !SeamlessMapTransferTrigger.HasEnterSpotAt(map, cell)) return;
            int tick = GenTicks.TicksGame;
            var state = StateFor(pawn);
            if (tick >= state.LastSpotEntryTick && tick - state.LastSpotEntryTick < 600) return;
            state.LastSpotEntryTick = tick;

            CompSeamlessTileEnterSpot comp = null;
            var things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def != SeamlessTransferGrants.EnterSpotDef) continue;
                comp = things[i].TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp != null) break;
            }
            bool hasGrant = SeamlessTransferGrants.TryGet(pawn, out var grant);
            bool arrivalLoaded = comp != null && comp.hasArrival
                && SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out _);
            Log.Message($"[RimExodus] [caravan-exit] Entering spot: {pawn.LabelShort} "
                + $"tick={tick} map={map.uniqueID} from={pawn.Position} to={cell} "
                + $"job={pawn.CurJob?.def?.defName ?? "none"} exitMapOnArrival={pawn.CurJob?.exitMapOnArrival.ToString() ?? "null"} "
                + $"grant={(hasGrant ? grant.Kind.ToString() : "none")} "
                + $"targetWorldTile={comp?.targetWorldTile.ToString() ?? "null"} "
                + $"hasArrival={comp?.hasArrival.ToString() ?? "null"} arrivalLoaded={arrivalLoaded}.");
        }

        private static string RawExitCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return "out-of-bounds";
            var grid = GridRef(map.exitMapGrid);
            return grid == null ? "unbuilt" : grid[cell].ToString();
        }

        private static string TerrainAt(Map map, IntVec3 cell)
        {
            return cell.InBounds(map) ? map.terrainGrid.TerrainAt(cell)?.defName ?? "null" : "out-of-bounds";
        }

        private static string DescribeCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return "out-of-bounds";
            var things = map.thingGrid.ThingsListAt(cell);
            var names = new List<string>();
            for (int i = 0; i < things.Count && i < 12; i++)
                names.Add($"{things[i].def?.defName ?? "null-def"}#{things[i].ThingID}");
            if (things.Count > 12) names.Add($"+{things.Count - 12} more");
            return $"{cell}(terrain={TerrainAt(map, cell)},walkable={cell.Walkable(map)},"
                + $"rectEdge={cell.OnEdge(map)},things=[{string.Join(",", names)}])";
        }
    }
}
