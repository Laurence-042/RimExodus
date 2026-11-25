using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    // An exit Job remains an exit Job on every map. This table only helps the next
    // vanilla exit search keep its direction and avoid repeating an edge.
    internal static class SeamlessEvacuationRouting
    {
        private sealed class Route
        {
            internal readonly HashSet<PlanetTile> Visited = new HashSet<PlanetTile>();
            internal IntVec3 Origin = IntVec3.Invalid;
            internal IntVec3 Direction;
            internal Lord Lord;
            internal LordToil Phase;
            internal Map Map;
            internal IntVec3 Rejected = IntVec3.Invalid;
            internal int RetryAfter;
        }

        private static ConditionalWeakTable<Pawn, Route> routes = new ConditionalWeakTable<Pawn, Route>();
        [System.ThreadStatic] internal static Pawn ExitSearchPawn;

        internal static bool IsOrdinaryExit(Job job) => job?.def != null
            && (job.def == JobDefOf.Goto || job.def == JobDefOf.Flee)
            && job.exitMapOnArrival && !job.playerForced && !job.failIfCantJoinOrCreateCaravan
            && !job.targetA.HasThing && job.targetA.IsValid;

        internal static void Reset() => routes = new ConditionalWeakTable<Pawn, Route>();

        private static Route Current(Pawn pawn)
        {
            if (!routes.TryGetValue(pawn, out var route)) return null;
            var lord = pawn.GetLord();
            if (route.Map == pawn.Map && route.Lord == lord && route.Phase == lord?.CurLordToil)
                return route;
            routes.Remove(pawn);
            return null;
        }

        private static Route GetOrCreate(Pawn pawn)
        {
            var route = Current(pawn);
            if (route != null) return route;
            var lord = pawn.GetLord();
            route = new Route { Map = pawn.Map, Lord = lord, Phase = lord?.CurLordToil, Origin = pawn.Position };
            routes.Add(pawn, route);
            return route;
        }

        internal static bool WasVisited(Pawn pawn, PlanetTile tile) => Current(pawn)?.Visited.Contains(tile) == true;

        internal static void Reject(Pawn pawn, IntVec3 exit)
        {
            var route = GetOrCreate(pawn);
            route.Rejected = exit;
            route.RetryAfter = GenTicks.TicksGame + 120;
        }

        internal static void Arrive(Pawn pawn, Map from, IntVec3 exit, Map to)
        {
            if (!routes.TryGetValue(pawn, out var route) || route.Map != from)
            {
                routes.Remove(pawn);
                route = new Route();
                routes.Add(pawn, route);
            }

            route.Visited.Add(SeamlessTileRegistry.GetMapWorldTile(from));
            route.Visited.Add(SeamlessTileRegistry.GetMapWorldTile(to));
            if (route.Direction == IntVec3.Zero && route.Origin.IsValid)
                route.Direction = exit - route.Origin;
            if (route.Direction == IntVec3.Zero
                && SeamlessViewProjection.TryProject(from, from.Center, to, out var origin))
                route.Direction = to.Center - origin;
            route.Map = to;
            route.Lord = pawn.GetLord();
            route.Phase = route.Lord?.CurLordToil;
            route.Rejected = IntVec3.Invalid;
        }

        // Called inside the vanilla exit giver, before it chooses between walking and digging.
        // Returning false from this method leaves selection to the existing edge finder.
        internal static bool TryChooseExit(Pawn pawn, TraverseMode mode, bool canBash, out IntVec3 exit)
        {
            exit = IntVec3.Invalid;
            var map = pawn.Map;
            var def = SeamlessTransferGrants.EnterSpotDef;
            if (map == null || def == null || !SeamlessRaidContinuity.CanWithdrawAcross(pawn.GetLord()))
                return false;

            var route = GetOrCreate(pawn);
            bool avoidingBlockedCell = route.Rejected.IsValid && GenTicks.TicksGame < route.RetryAfter;
            if (route.Visited.Count == 0 && !avoidingBlockedCell)
                return false;

            var cells = new List<IntVec3>();
            var targets = new List<PlanetTile>();
            var neighbors = new Dictionary<PlanetTile, Map>();
            var nativeExits = SeamlessMapTopology.ExitCells(map, entireBand: false);
            var nativeExitSet = nativeExits == null ? null : new HashSet<IntVec3>(nativeExits);
            var vehicle = SeamlessVehiclesCompat.IsVehicle(pawn);
            foreach (var thing in map.listerThings.ThingsOfDef(def))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null || (avoidingBlockedCell && route.Rejected == thing.Position)) continue;
                var tile = comp.TargetTile;
                if (!neighbors.TryGetValue(tile, out var neighbor))
                {
                    SeamlessTileGraph.TryGetMapByWorldTile(tile, out neighbor);
                    neighbors.Add(tile, neighbor);
                }
                if (route.Visited.Contains(tile) || neighbor == null)
                {
                    if (nativeExitSet != null && !nativeExitSet.Contains(thing.Position)) continue;
                }
                else
                {
                    if (!comp.hasArrival || (!vehicle && !SeamlessPathCostField.CanEnterCell(neighbor, comp.cachedArrivalCell, pawn)))
                        continue;
                }
                cells.Add(thing.Position);
                targets.Add(tile);
            }
            if (nativeExits != null)
            {
                foreach (var cell in nativeExits)
                {
                    if ((avoidingBlockedCell && route.Rejected == cell)
                        || SeamlessMapTransferTrigger.HasEnterSpotAt(map, cell)) continue;
                    cells.Add(cell);
                    targets.Add(PlanetTile.Invalid);
                }
            }
            if (cells.Count == 0) return true;

            var traverse = TraverseParms.For(pawn, Danger.Deadly, mode, canBash);
            // The native pathfinder must be able to select a diggable exit so that the giver
            // can dispatch the original digging job instead of immediately issuing a Goto.
            var costs = mode == TraverseMode.ByPawn
                ? SeamlessPathCostField.FloodCosts(map, pawn.Position, cells, pawn, traverse) : null;
            SeamlessEvacuationExitScore best = default;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var delta = cell - pawn.Position;
                int cost = costs?[i] ?? delta.LengthHorizontalSquared;
                if (cost == int.MaxValue || (cost <= 0 && cell != pawn.Position)) continue;
                long progress = (long)delta.x * route.Direction.x + (long)delta.z * route.Direction.z;
                var score = new SeamlessEvacuationExitScore(progress, System.Math.Max(1, cost), route.Visited.Contains(targets[i]));
                if (exit.IsValid && score.CompareTo(best) >= 0) continue;
                bool reachable = vehicle ? SeamlessVehiclesCompat.CanReachLocal(pawn, map, pawn.Position, cell)
                    : map.reachability.CanReach(pawn.Position, cell, PathEndMode.OnCell, traverse);
                if (!reachable) continue;
                exit = cell;
                best = score;
            }
            return true;
        }
    }
}
