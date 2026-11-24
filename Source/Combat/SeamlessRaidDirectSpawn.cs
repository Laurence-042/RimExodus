using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// Plans a raid on the attacked map, then materializes it once on the far side of an active
    /// neighboring map. The incident target never changes outside the narrow native spawn calls.
    /// </summary>
    internal static class SeamlessRaidDirectSpawn
    {
        private const float RayEpsilon = 0.05f;
        private const float LocalSearchRadius = 14f;

        [ThreadStatic] private static bool resolvingNativeAnchor;
        [ThreadStatic] private static bool spawningOnHost;
        [ThreadStatic] private static RaidExecutionTrace executionTrace;

        internal static bool ResolvingNativeAnchor => resolvingNativeAnchor;

        internal sealed class WindowState
        {
            internal Map AttackedMap;
            internal bool Redirected;
            internal bool PreviousSpawningOnHost;
        }

        internal sealed class LordCreationState
        {
            internal Map AttackedMap;
            internal long ProbeStart;
        }

        private sealed class RaidExecutionTrace
        {
            internal IncidentParms Parms;
            internal string Worker;
            internal string Phase;
            internal List<Pawn> Pawns;
            internal bool? GenerateResult;
        }

        internal static void BeginExecutionTrace(IncidentParms parms, IncidentWorker worker)
        {
            executionTrace = new RaidExecutionTrace
            {
                Parms = parms,
                Worker = worker?.GetType().FullName ?? "null",
                Phase = "TryExecuteWorker.begin"
            };
        }

        internal static void MarkExecutionPhase(IncidentParms parms, string phase)
        {
            var trace = executionTrace;
            if (trace == null || (parms != null && trace.Parms != parms)) return;
            trace.Phase = phase;
        }

        internal static void MarkExecutionException(IncidentParms parms, string scope)
        {
            var trace = executionTrace;
            if (trace == null || (parms != null && trace.Parms != parms)) return;
            trace.Phase = $"{trace.Phase} -> {scope}.exception";
        }

        internal static void CaptureGeneratedPawns(IncidentParms parms, List<Pawn> pawns, bool result)
        {
            var trace = executionTrace;
            if (trace == null || trace.Parms != parms) return;
            trace.Pawns = pawns;
            trace.GenerateResult = result;
            trace.Phase = result ? "TryGenerateRaidInfo.end:true" : "TryGenerateRaidInfo.end:false";
        }

        internal static void EndExecutionTrace(IncidentParms parms)
        {
            if (executionTrace?.Parms == parms) executionTrace = null;
        }

        internal static WindowState BeginWindowIncident(IncidentParms parms, float edgeRoadChance)
        {
            var state = new WindowState { AttackedMap = parms?.target as Map };
            var plan = SeamlessRaidOuterSpawn.Current;
            if (state.AttackedMap == null || plan?.TargetMap != state.AttackedMap || plan.CandidateMaps == null
                || !plan.AllowCrossMap || !SeamlessCombatCoords.Enabled) return state;
            var preference = SeamlessRaidOuterSpawn.EntryPreference(RimExodusMod.Settings,
                RaidEntryScope.Incident, plan.IncidentDefName);
            if (preference == RaidEntryPreference.Native) return state;

            var mode = DefDatabase<PawnsArrivalModeDef>.GetNamedSilentFail("EdgeWalkIn");
            if (mode == null) return state;
            resolvingNativeAnchor = true;
            IntVec3 source;
            try
            {
                if (!RCellFinder.TryFindRandomPawnEntryCell(out source, state.AttackedMap, edgeRoadChance))
                    return state;
            }
            finally
            {
                resolvingNativeAnchor = false;
            }
            var previousMode = parms.raidArrivalMode;
            parms.raidArrivalMode = mode;
            try
            {
                var nextRotation = 0;
                var planned = false;
                var center = IntVec3.Invalid;
                while (TryPlanHost(plan, parms, mode.Worker, source, out center, nextRotation))
                {
                    if (HasValidatedHostEntry(plan.CrossHostMap, edgeRoadChance))
                    {
                        planned = true;
                        break;
                    }
                    nextRotation = plan.DirectionRotation + 1;
                }
                if (!planned)
                {
                    plan.CandidateMaps = null;
                    return state;
                }
                plan.EntrySelected = true;
                plan.NativeSpawnCenter = source;
                plan.HostSpawnCenter = center;
                parms.spawnCenter = center;
                parms.target = plan.CrossHostMap;
                state.PreviousSpawningOnHost = spawningOnHost;
                spawningOnHost = true;
                state.Redirected = true;
                return state;
            }
            finally
            {
                parms.raidArrivalMode = previousMode;
            }
        }

        internal static void FinishWindowIncident(IncidentParms parms, WindowState state)
        {
            if (state?.AttackedMap == null) return;
            parms.target = state.AttackedMap;
            if (!state.Redirected) return;
            spawningOnHost = state.PreviousSpawningOnHost;
            RegisterNativeLords(parms, SeamlessRaidOuterSpawn.WindowPawns, state.AttackedMap);
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Raid direct spawn: generated {SeamlessRaidOuterSpawn.WindowPawns.Count} incident pawns directly " +
                $"on host map {SeamlessRaidOuterSpawn.Current?.CrossHostMap?.uniqueID} for attacked map {state.AttackedMap.uniqueID}.");
        }

        internal static bool TryResolveSpawnCenter(PawnsArrivalModeWorker worker, IncidentParms parms)
        {
            MarkExecutionPhase(parms, "TryResolveRaidSpawnCenter.begin");
            bool resolved;
            resolvingNativeAnchor = true;
            try
            {
                resolved = worker.TryResolveRaidSpawnCenter(parms);
            }
            finally
            {
                resolvingNativeAnchor = false;
            }
            MarkExecutionPhase(parms, resolved
                ? "TryResolveRaidSpawnCenter.end:true"
                : "TryResolveRaidSpawnCenter.end:false");
            if (!resolved) return false;

            var plan = SeamlessRaidOuterSpawn.Current;
            if (!CanPlan(plan, parms, worker))
            {
                if (plan?.Parms == parms) plan.CandidateMaps = null;
                return true;
            }

            var target = plan.TargetMap;
            var source = parms.spawnCenter;
            if (!source.IsValid)
            {
                if (!TrySynthesizeNativeAnchor(worker, target, out source))
                {
                    plan.CandidateMaps = null;
                    return true;
                }
            }

            if (!TryPlanHost(plan, parms, worker, source, out var hostCenter))
            {
                plan.CandidateMaps = null;
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid direct spawn: all rotated host candidates failed for map {target.uniqueID}; keeping native placement.");
                return true;
            }

            plan.NativeSpawnCenter = source;
            parms.spawnCenter = hostCenter;
            plan.EntrySelected = true;
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Raid direct spawn: mapped {source} on attacked map {target.uniqueID} to {hostCenter} " +
                $"on host map {plan.CrossHostMap.uniqueID} (shared={plan.SharedSeamPoint}, far={plan.FarSeamPoint}, " +
                $"rotation={plan.DirectionRotation}).");
            return true;
        }

        private static bool TrySynthesizeNativeAnchor(PawnsArrivalModeWorker worker, Map target,
            out IntVec3 source)
        {
            source = IntVec3.Invalid;
            if (worker is PawnsArrivalModeWorker_RandomDrop)
                source = DropCellFinder.RandomDropSpot(target);
            else if (worker is PawnsArrivalModeWorker_EdgeDropGroups)
                source = DropCellFinder.FindRaidDropCenterDistant(target);
            else if (worker is PawnsArrivalModeWorker_EdgeWalkInDistributed
                || worker is PawnsArrivalModeWorker_EdgeWalkInDistributedGroups
                || worker is PawnsArrivalModeWorker_EdgeWalkInGroups)
            {
                resolvingNativeAnchor = true;
                try
                {
                    var roadChance = worker is PawnsArrivalModeWorker_EdgeWalkInDistributed
                        ? CellFinder.EdgeRoadChance_Ignore : CellFinder.EdgeRoadChance_Hostile;
                    RCellFinder.TryFindRandomPawnEntryCell(out source, target, roadChance);
                }
                finally
                {
                    resolvingNativeAnchor = false;
                }
            }
            if (source.IsValid) return true;

            var edge = SeamlessEdgeCells.GetSeamEdgeCellsWithBandFallback(target);
            if (edge == null || edge.Count == 0) return false;
            source = edge[Rand.Range(0, edge.Count)];
            return true;
        }

        internal static List<Pawn> SpawnThreats(RaidStrategyWorker worker, IncidentParms parms)
        {
            MarkExecutionPhase(parms, "SpawnThreats.begin");
            var plan = ActivePlan(parms);
            if (plan == null)
            {
                var nativePawns = worker.SpawnThreats(parms);
                MarkExecutionPhase(parms, "SpawnThreats.end");
                return nativePawns;
            }
            var pawns = OnHost(parms, plan.CrossHostMap, () => worker.SpawnThreats(parms));
            plan.GeneratedPawnCount = pawns?.Count ?? -1;
            // Mech clusters create their map-bound Lord inside SpawnCluster and override MakeLords
            // with an empty method, so register that native Lord here without touching the raid
            // incident's outer execution method.
            if (worker is RaidStrategyWorker_SiegeMechanoid)
                RegisterNativeLords(parms, pawns, plan.TargetMap);
            MarkExecutionPhase(parms, "SpawnThreats.end");
            return pawns;
        }

        internal static void Arrive(PawnsArrivalModeWorker worker, List<Pawn> pawns, IncidentParms parms)
        {
            MarkExecutionPhase(parms, "Arrive.begin");
            var plan = ActivePlan(parms);
            if (plan == null)
            {
                worker.Arrive(pawns, parms);
                MarkExecutionPhase(parms, "Arrive.end");
                return;
            }
            plan.GeneratedPawnCount = pawns?.Count ?? -1;
            if (worker is PawnsArrivalModeWorker_RandomDrop)
            {
                while (true)
                {
                    if (TryArriveRandomDrop(pawns, parms, plan))
                    {
                        MarkExecutionPhase(parms, "Arrive.end");
                        return;
                    }
                    var nextRotation = plan.DirectionRotation + 1;
                    if (!plan.NativeSpawnCenter.IsValid
                        || !TryPlanHost(plan, parms, worker, plan.NativeSpawnCenter,
                            out var nextCenter, nextRotation)) break;
                    plan.EntrySelected = true;
                    plan.HostSpawnCenter = nextCenter;
                    parms.spawnCenter = nextCenter;
                }
                plan.EntrySelected = false;
                ClearHost(plan);
                plan.CandidateMaps = null;
                worker.Arrive(pawns, parms);
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid direct spawn: random-drop group preflight failed for map {plan.TargetMap.uniqueID}; " +
                    "kept the entire group on its native map.");
                MarkExecutionPhase(parms, "Arrive.end");
                return;
            }
            OnHost(parms, plan.CrossHostMap, () =>
            {
                var previous = spawningOnHost;
                spawningOnHost = true;
                try { worker.Arrive(pawns, parms); }
                finally { spawningOnHost = previous; }
                return true;
            });
            MarkExecutionPhase(parms, "Arrive.end");
        }

        internal static LordCreationState BeginLordCreation(IncidentParms parms)
        {
            var plan = ActivePlan(parms);
            if (plan == null) return null;
            var state = new LordCreationState
            {
                AttackedMap = plan.TargetMap,
                ProbeStart = RimExodusPerfProbe.Now()
            };
            parms.target = plan.CrossHostMap;
            return state;
        }

        internal static void FinishLordCreation(IncidentParms parms, List<Pawn> pawns,
            LordCreationState state, bool succeeded)
        {
            if (state?.AttackedMap == null) return;
            parms.target = state.AttackedMap;
            try
            {
                if (succeeded) RegisterNativeLords(parms, pawns, state.AttackedMap);
            }
            finally
            {
                RimExodusPerfProbe.Record(RimExodusPerfProbe.RaidSpawnPostfix,
                    state.ProbeStart, pawns?.Count ?? 0);
            }
        }

        internal static void LogIncidentFailure(IncidentParms parms, Exception exception)
        {
            var trace = executionTrace;
            var plan = SeamlessRaidOuterSpawn.Current;
            if (exception == null) return;

            var pawns = trace?.Pawns;
            var nullPawns = 0;
            var factionlessPawns = 0;
            var apparellessPawns = 0;
            if (pawns != null)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn == null)
                    {
                        nullPawns++;
                        continue;
                    }
                    if (pawn.Faction == null) factionlessPawns++;
                    if (pawn.apparel == null) apparellessPawns++;
                }
            }

            Log.Error("[RimExodus] Raid phase failure: " +
                $"phase={trace?.Phase ?? "trace-missing"}, worker={trace?.Worker ?? "null"}, " +
                $"generateResult={(trace?.GenerateResult.HasValue == true ? trace.GenerateResult.Value.ToString() : "null")}, " +
                $"strategy={parms?.raidStrategy?.defName ?? "null"}, strategyWorker={parms?.raidStrategy?.Worker?.GetType().FullName ?? "null"}, " +
                $"arrival={parms?.raidArrivalMode?.defName ?? "null"}, arrivalWorker={parms?.raidArrivalMode?.Worker?.GetType().FullName ?? "null"}, " +
                $"faction={parms?.faction?.def?.defName ?? "null"}, targetMap={(parms?.target as Map)?.uniqueID.ToString() ?? "null"}, " +
                $"controller={parms?.controllerPawn?.LabelShort ?? "null"}, spawnCenter={parms?.spawnCenter.ToString() ?? "null"}, " +
                $"pawns={(pawns == null ? "null" : pawns.Count.ToString())}, nullPawns={nullPawns}, " +
                $"factionlessPawns={factionlessPawns}, apparellessPawns={apparellessPawns}, " +
                $"pawnGroups={(parms?.pawnGroups == null ? "null" : parms.pawnGroups.Count.ToString())}, " +
                $"entrySelected={(plan?.Parms == parms && plan.EntrySelected)}, attackedMap={plan?.TargetMap?.uniqueID.ToString() ?? "null"}, " +
                $"hostMap={plan?.CrossHostMap?.uniqueID.ToString() ?? "null"}, logRaidInfo={DebugSettings.logRaidInfo}. " +
                exception);
        }

        private static bool TryArriveRandomDrop(List<Pawn> pawns, IncidentParms parms,
            SeamlessRaidOuterSpawn.Plan plan)
        {
            var host = plan.CrossHostMap;
            if (host == null || pawns == null) return false;
            var spots = new List<IntVec3>(pawns.Count);
            for (int i = 0; i < pawns.Count; i++)
            {
                var found = IntVec3.Invalid;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    var candidate = DropCellFinder.RandomDropSpot(host);
                    if (!candidate.IsValid || !HasReachableLandingNear(host, candidate, plan)) continue;
                    found = candidate;
                    break;
                }
                if (!found.IsValid) return false;
                spots.Add(found);
            }

            var hostile = parms.faction != null && parms.faction.HostileTo(Faction.OfPlayer);
            OnHost(parms, host, () =>
            {
                for (int i = 0; i < pawns.Count; i++)
                    DropPodUtility.DropThingsNear(spots[i], host, Gen.YieldSingle((Thing)pawns[i]),
                        parms.podOpenDelay, canInstaDropDuringInit: false, leaveSlag: true,
                        parms.canRoofPunch ?? hostile, forbid: true, allowFogged: true, parms.faction);
                return true;
            });
            return true;
        }

        internal static IntVec3 FindStandaloneClusterPosition(Map map, MechClusterSketch sketch,
            int maxTries, float spawnCloseToColonyChance)
        {
            var native = MechClusterUtility.FindClusterPosition(map, sketch, maxTries, spawnCloseToColonyChance);
            var plan = SeamlessRaidOuterSpawn.Current;
            if (!native.IsValid || plan?.TargetMap != map || plan.CandidateMaps == null || !plan.AllowCrossMap
                || !SeamlessCombatCoords.Enabled || SeamlessDormancyManager.IsDormant(map)) return native;
            var preference = SeamlessRaidOuterSpawn.EntryPreference(RimExodusMod.Settings,
                RaidEntryScope.Incident, plan.IncidentDefName);
            if (preference != RaidEntryPreference.Outer) return native;

            var mode = DefDatabase<PawnsArrivalModeDef>.GetNamedSilentFail("MechClusterDrop")
                ?? new PawnsArrivalModeDef { defName = "MechClusterDrop", workerClass = typeof(PawnsArrivalModeWorker_ClusterDrop) };
            var previousMode = plan.Parms.raidArrivalMode;
            var previousSketch = plan.Parms.mechClusterSketch;
            plan.Parms.raidArrivalMode = mode;
            plan.Parms.mechClusterSketch = sketch;
            try
            {
                if (!TryPlanHost(plan, plan.Parms, mode.Worker, native, out var mapped)) return native;
                plan.EntrySelected = true;
                plan.NativeSpawnCenter = native;
                plan.HostSpawnCenter = mapped;
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Mech cluster direct spawn: mapped {native} on attacked map {map.uniqueID} to {mapped} " +
                    $"on host map {plan.CrossHostMap.uniqueID}.");
                return mapped;
            }
            finally
            {
                plan.Parms.raidArrivalMode = previousMode;
                plan.Parms.mechClusterSketch = previousSketch;
            }
        }

        internal static List<Thing> SpawnStandaloneCluster(IntVec3 center, Map map, MechClusterSketch sketch,
            bool dropInPods, bool canAssaultColony, string questTag)
        {
            var plan = SeamlessRaidOuterSpawn.Current;
            var host = plan?.EntrySelected == true && plan.TargetMap == map ? plan.CrossHostMap : null;
            var spawned = MechClusterUtility.SpawnCluster(center, host ?? map, sketch, dropInPods,
                canAssaultColony, questTag);
            if (host == null) return spawned;

            var continuity = SeamlessRaidContinuity.Current;
            if (continuity == null) return spawned;
            var seen = new HashSet<Lord>();
            foreach (var pawn in spawned.OfType<Pawn>())
            {
                var lord = pawn.GetLord();
                if (lord != null && seen.Add(lord)) continuity.RegisterDirectSquad(lord, map, null);
            }
            return spawned;
        }

        internal static bool TryResolveHostEntry(Predicate<IntVec3> validator, Map map,
            out IntVec3 entry, out bool found)
        {
            entry = IntVec3.Invalid;
            found = false;
            var plan = SeamlessRaidOuterSpawn.Current;
            if (!spawningOnHost || plan?.CrossHostMap != map || !plan.FarFacingTile.Valid) return false;

            var cells = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(map, plan.FarFacingTile, cells);
            Shuffle(cells);
            foreach (var cell in cells)
            {
                if (!CanMarchToAttackedSeam(map, cell, plan)) continue;
                try
                {
                    if (!validator(cell)) continue;
                    entry = cell;
                    found = true;
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] Raid direct host entry validation failed at {cell}: {ex}");
                }
            }
            // This was a host-constrained query. Do not leak back to the generic all-edge pool,
            // which could silently put a later distributed pawn on a different side.
            return true;
        }

        internal static bool TryFindGroupCenter(Map map, List<Pair<List<Pawn>, IntVec3>> groups,
            bool arriveInPods, out IntVec3 center)
        {
            center = IntVec3.Invalid;
            var plan = SeamlessRaidOuterSpawn.Current;
            if (!spawningOnHost || plan?.CrossHostMap != map) return false;

            var candidates = new List<IntVec3>();
            foreach (var cell in GenRadial.RadialCellsAround(plan.HostSpawnCenter, LocalSearchRadius, true))
            {
                if (!cell.InBounds(map)) continue;
                if (arriveInPods)
                {
                    if (DropCellFinder.TryFindDropSpotNear(cell, map, out var drop, allowFogged: true,
                            canRoofPunch: true, maxRadius: 6) && HasReachableLandingNear(map, drop, plan))
                        candidates.Add(drop);
                }
                else if (cell.Standable(map) && CanMarchToAttackedSeam(map, cell, plan))
                    candidates.Add(cell);
            }
            if (candidates.Count == 0) return false;

            Shuffle(candidates);
            var bestDistance = float.MinValue;
            foreach (var candidate in candidates)
            {
                var nearest = float.MaxValue;
                for (int i = 0; i < groups.Count; i++)
                    nearest = Mathf.Min(nearest, candidate.DistanceToSquared(groups[i].Second));
                if (groups.Count == 0) nearest = 0f;
                if (!center.IsValid || nearest > bestDistance)
                {
                    center = candidate;
                    bestDistance = nearest;
                }
            }
            return true;
        }

        private static bool CanPlan(SeamlessRaidOuterSpawn.Plan plan, IncidentParms parms,
            PawnsArrivalModeWorker worker)
        {
            if (plan?.CandidateMaps == null || parms?.target != plan.TargetMap || !plan.AllowCrossMap
                || !SeamlessRaidOuterSpawn.Enabled || !SeamlessCombatCoords.Enabled
                || SeamlessDormancyManager.IsDormant(plan.TargetMap)) return false;
            if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer)) return false;
            if (parms.controllerPawn != null || parms.attackTargets is { Count: > 0 }) return false;
            if (!SeamlessRaidOuterSpawn.SupportsDirectArrival(parms.raidArrivalMode)
                || !SeamlessRaidOuterSpawn.SupportsDirectStrategy(parms.raidStrategy)) return false;

            var settings = RimExodusMod.Settings;
            if (SeamlessRaidOuterSpawn.EntryPreference(settings, RaidEntryScope.Incident, plan.IncidentDefName)
                    == RaidEntryPreference.Native
                || SeamlessRaidOuterSpawn.EntryPreference(settings, RaidEntryScope.Strategy,
                    parms.raidStrategy?.defName) == RaidEntryPreference.Native) return false;
            var arrival = SeamlessRaidOuterSpawn.EntryPreference(settings, RaidEntryScope.Arrival,
                parms.raidArrivalMode?.defName);
            if (arrival == RaidEntryPreference.Native) return false;
            if (arrival == RaidEntryPreference.Default && !parms.raidArrivalMode.walkIn) return false;
            return !SeamlessRaidOuterSpawn.IsStrategyMapBound(parms.raidStrategy);
        }

        private static bool TryPlanHost(SeamlessRaidOuterSpawn.Plan plan, IncidentParms parms,
            PawnsArrivalModeWorker worker, IntVec3 source, out IntVec3 hostCenter, int startRotation = 0)
        {
            hostCenter = IntVec3.Invalid;
            var target = plan.TargetMap;
            var tile = SeamlessTileRegistry.GetMapWorldTile(target);
            var vertices = SeamlessPolygonGeometry.BuildPolygonVertices(tile, target.Size.x);
            if (vertices.Count < 3) return false;

            var center = new Vector2(target.Size.x * 0.5f, target.Size.z * 0.5f);
            var baseVector = new Vector2(source.x + 0.5f, source.z + 0.5f) - center;
            if (baseVector.sqrMagnitude < 1f) baseVector = Vector2.up * Mathf.Max(1f, target.Size.x * 0.25f);

            var sourceEdges = SeamlessMapTopology.Edges(tile, target.Size.x);
            for (int rotation = Mathf.Max(0, startRotation); rotation < vertices.Count; rotation++)
            {
                var angle = rotation * Mathf.PI * 2f / vertices.Count;
                var direction = Rotate(baseVector, angle).normalized;
                var rotatedPoint = center + Rotate(baseVector, angle);
                if (!TryRayExit(vertices, center, direction, -1, out var q, out var sourceEdge)) continue;
                if (sourceEdge < 0 || sourceEdge >= sourceEdges.Count) continue;
                var hostTile = sourceEdges[sourceEdge].Destination;
                var host = MapAt(hostTile);
                if (!IsActive(host) || !worker.CanUseOnMap(host)) continue;
                if (!SeamlessViewProjection.TryProject(target, new Vector3(q.x, 0f, q.y), host, out var projected)) continue;

                var hostVertices = SeamlessPolygonGeometry.BuildPolygonVertices(hostTile, host.Size.x);
                var hostEdges = SeamlessMapTopology.Edges(hostTile, host.Size.x);
                var sharedEdge = SeamlessMapTopology.FindEdge(hostEdges, tile);
                var qPrime = new Vector2(projected.x, projected.z);
                if (!TryRayExit(hostVertices, qPrime + direction * RayEpsilon, direction, sharedEdge,
                        out var qFar, out var farEdge)) continue;
                if (farEdge < 0 || farEdge >= hostEdges.Count || !hostEdges[farEdge].Destination.Valid) continue;

                var depth = Vector2.Distance(rotatedPoint, q);
                var expected = qFar - direction * depth;
                var expectedCell = new IntVec3(Mathf.RoundToInt(expected.x - 0.5f), 0,
                    Mathf.RoundToInt(expected.y - 0.5f));

                plan.CrossHostMap = host;
                plan.FarFacingTile = hostEdges[farEdge].Destination;
                plan.SharedSeamPoint = new IntVec3(Mathf.RoundToInt(qPrime.x - 0.5f), 0,
                    Mathf.RoundToInt(qPrime.y - 0.5f));
                plan.FarSeamPoint = new IntVec3(Mathf.RoundToInt(qFar.x - 0.5f), 0,
                    Mathf.RoundToInt(qFar.y - 0.5f));
                plan.DirectionRotation = rotation;

                if (parms.raidArrivalMode.walkIn && !HasUsableFarEntry(host, plan))
                {
                    ClearHost(plan);
                    continue;
                }
                if (worker is PawnsArrivalModeWorker_EdgeWalkInDistributed
                    && !HasValidatedHostEntry(host, CellFinder.EdgeRoadChance_Ignore))
                {
                    ClearHost(plan);
                    continue;
                }

                if (!TryFindHostCenter(worker, parms, host, expectedCell, plan, out hostCenter))
                {
                    ClearHost(plan);
                    continue;
                }
                plan.HostSpawnCenter = hostCenter;
                return true;
            }
            ClearHost(plan);
            return false;
        }

        private static bool HasUsableFarEntry(Map host, SeamlessRaidOuterSpawn.Plan plan)
        {
            var cells = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(host, plan.FarFacingTile, cells);
            foreach (var cell in cells)
                if (cell.Standable(host) && (host.TileInfo.AllowRoofedEdgeWalkIn || !cell.Roofed(host))
                    && CanMarchToAttackedSeam(host, cell, plan)) return true;
            return false;
        }

        private static bool HasValidatedHostEntry(Map host, float edgeRoadChance)
        {
            var previous = spawningOnHost;
            spawningOnHost = true;
            try
            {
                return RCellFinder.TryFindRandomPawnEntryCell(out _, host, edgeRoadChance);
            }
            finally
            {
                spawningOnHost = previous;
            }
        }

        private static bool TryFindHostCenter(PawnsArrivalModeWorker worker, IncidentParms parms, Map host,
            IntVec3 expected, SeamlessRaidOuterSpawn.Plan plan, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (worker is PawnsArrivalModeWorker_ClusterDrop)
                return TryFindClusterCenter(parms, host, expected, out result);

            if (!parms.raidArrivalMode.walkIn)
            {
                if (worker is PawnsArrivalModeWorker_RandomDrop)
                {
                    result = expected.ClampInsideMap(host);
                    return result.IsValid && HasReachableLandingNear(host, result, plan);
                }
                var canRoofPunch = parms.faction != null && parms.faction.HostileTo(Faction.OfPlayer);
                foreach (var candidate in GenRadial.RadialCellsAround(expected, LocalSearchRadius, true))
                {
                    if (!candidate.InBounds(host)
                        || !DropCellFinder.TryFindDropSpotNear(candidate, host, out var drop,
                            allowFogged: true, canRoofPunch, maxRadius: 6)
                        || !HasReachableLandingNear(host, drop, plan)) continue;
                    result = drop;
                    return true;
                }
                return false;
            }

            var preferDark = worker is PawnsArrivalModeWorker_EdgeWalkInDarkness;
            IntVec3 fallback = IntVec3.Invalid;
            foreach (var cell in GenRadial.RadialCellsAround(expected, LocalSearchRadius, true))
            {
                if (!cell.InBounds(host) || !cell.Standable(host)
                    || (!host.TileInfo.AllowRoofedEdgeWalkIn && cell.Roofed(host))
                    || !CanMarchToAttackedSeam(host, cell, plan)) continue;
                if (!preferDark || host.glowGrid.PsychGlowAt(cell) == PsychGlow.Dark)
                {
                    result = cell;
                    return true;
                }
                if (!fallback.IsValid) fallback = cell;
            }
            result = fallback;
            return result.IsValid;
        }

        private static bool HasReachableLandingNear(Map host, IntVec3 drop, SeamlessRaidOuterSpawn.Plan plan)
        {
            foreach (var cell in GenRadial.RadialCellsAround(drop, 8f, true))
                if (cell.InBounds(host) && cell.Standable(host) && CanMarchToAttackedSeam(host, cell, plan))
                    return true;
            return false;
        }

        private static bool TryFindClusterCenter(IncidentParms parms, Map host, IntVec3 expected, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (parms.mechClusterSketch == null) return false;
            var bestScore = float.MinValue;
            foreach (var cell in GenRadial.RadialCellsAround(expected, 30f, true))
            {
                if (!cell.InBounds(host)) continue;
                var score = MechClusterUtility.GetClusterPositionScore(cell, host, parms.mechClusterSketch);
                if (score <= bestScore) continue;
                bestScore = score;
                result = cell;
                if (score >= 99.99f) return true;
            }
            return result.IsValid && bestScore > 0f;
        }

        private static bool CanMarchToAttackedSeam(Map host, IntVec3 from, SeamlessRaidOuterSpawn.Plan plan)
        {
            var cells = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(host,
                SeamlessTileRegistry.GetMapWorldTile(plan.TargetMap), cells);
            foreach (var cell in cells)
                if (cell.Standable(host) && host.reachability.CanReach(from, cell, PathEndMode.Touch,
                        TraverseMode.NoPassClosedDoors, Danger.Some)) return true;
            return false;
        }

        private static void RegisterNativeLords(IncidentParms parms, List<Pawn> pawns, Map attackedMap)
        {
            var continuity = SeamlessRaidContinuity.Current;
            if (continuity == null || pawns == null) return;
            var seen = new HashSet<Lord>();
            foreach (var pawn in pawns)
            {
                var lord = pawn?.GetLord();
                if (lord == null || !seen.Add(lord)) continue;
                continuity.RegisterDirectSquad(lord, attackedMap,
                    SeamlessRaidOuterSpawn.MarchingReplacement(lord, attackedMap, parms));
            }
        }

        private static SeamlessRaidOuterSpawn.Plan ActivePlan(IncidentParms parms)
        {
            var plan = SeamlessRaidOuterSpawn.Current;
            return plan?.EntrySelected == true && plan.CrossHostMap != null && parms?.target == plan.TargetMap
                ? plan : null;
        }

        private static T OnHost<T>(IncidentParms parms, Map host, Func<T> action)
        {
            var original = parms.target;
            parms.target = host;
            try { return action(); }
            finally { parms.target = original; }
        }

        private static void ClearHost(SeamlessRaidOuterSpawn.Plan plan)
        {
            plan.CrossHostMap = null;
            plan.FarFacingTile = PlanetTile.Invalid;
            plan.SharedSeamPoint = IntVec3.Invalid;
            plan.FarSeamPoint = IntVec3.Invalid;
            plan.HostSpawnCenter = IntVec3.Invalid;
            plan.DirectionRotation = -1;
        }

        private static Map MapAt(PlanetTile tile)
        {
            if (!tile.Valid) return null;
            var map = Find.World.worldObjects.MapParentAt(tile)?.Map;
            return map != null && !map.Disposed ? map : null;
        }

        private static bool IsActive(Map map) => map != null && !SeamlessDormancyManager.IsDormant(map);

        private static Vector2 Rotate(Vector2 value, float radians)
        {
            var sin = Mathf.Sin(radians);
            var cos = Mathf.Cos(radians);
            return new Vector2(value.x * cos - value.y * sin, value.x * sin + value.y * cos);
        }

        private static bool TryRayExit(IReadOnlyList<Vector2> vertices, Vector2 origin, Vector2 direction,
            int excludedEdge, out Vector2 intersection, out int edge)
        {
            intersection = Vector2.zero;
            edge = -1;
            var nearest = float.MaxValue;
            for (int i = 0; i < vertices.Count; i++)
            {
                if (i == excludedEdge) continue;
                var a = vertices[i];
                var segment = vertices[(i + 1) % vertices.Count] - a;
                var denominator = Cross(direction, segment);
                if (Mathf.Abs(denominator) < 0.0001f) continue;
                var delta = a - origin;
                var t = Cross(delta, segment) / denominator;
                var u = Cross(delta, direction) / denominator;
                if (t <= RayEpsilon || u < -0.0001f || u > 1.0001f || t >= nearest) continue;
                nearest = t;
                intersection = origin + direction * t;
                edge = i;
            }
            return edge >= 0;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static void Shuffle<T>(List<T> values)
        {
            for (int i = values.Count - 1; i > 0; i--)
            {
                var j = Rand.Range(0, i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }
    }
}
