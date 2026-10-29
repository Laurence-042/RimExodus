using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// RefugeePodCrash creates an already downed pawn in a drop pod. Vanilla's drop search accepts
    /// standable seam cells, including the transport ring, where the patient cannot be rescued.
    /// Correct the final pod cell so the normal search and its random fallback are both covered.
    /// </summary>
    [HarmonyPatch(typeof(DropPodUtility), nameof(DropPodUtility.MakeDropPodAt))]
    internal static class Patch_DropPodUtility_MakeDropPodAt_DownedPawn
    {
        private static void Prefix(ref IntVec3 c, Map map, ActiveTransporterInfo info)
        {
            if (map == null || info == null || !c.InBounds(map)) return;
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x);
            if (!band.Band.Contains(c) && !SeamlessPolygonGeometry.IsVoidCell(band, worldTile, map.Size.x, c))
                return;

            var hasDownedPawn = false;
            foreach (Thing thing in info.innerContainer)
            {
                if (thing is Pawn { Downed: true })
                {
                    hasDownedPawn = true;
                    break;
                }
            }
            if (!hasDownedPawn) return;

            // Search outwards from the chosen landing cell. A reachable core cell preserves the
            // event's local arrival while keeping the patient outside all three seam rings.
            foreach (var candidate in GenRadial.RadialCellsAround(c, 80f, useCenter: false))
            {
                if (!band.TransitionBand.Contains(candidate)
                    || !DropCellFinder.IsGoodDropSpot(candidate, map, allowFogged: false, canRoofPunch: false)
                    || !map.reachability.CanReachColony(candidate))
                    continue;

                var original = c;
                c = candidate;
                RimExodusLog.Message(RimExodusLogModule.Generation,
                    $"Downed pawn pod landing moved off seam on tile {worldTile}: {original} -> {c}");
                return;
            }

            // A steep corner or blocked local region can put the usable core farther than the
            // radial pattern reaches. Search the whole map once before giving up.
            foreach (var candidate in map.AllCells)
            {
                if (!band.TransitionBand.Contains(candidate)
                    || !DropCellFinder.IsGoodDropSpot(candidate, map, allowFogged: false, canRoofPunch: true)
                    || !map.reachability.CanReachColony(candidate))
                    continue;

                var original = c;
                c = candidate;
                RimExodusLog.Message(RimExodusLogModule.Generation,
                    $"Downed pawn pod landing moved off seam on tile {worldTile}: {original} -> {c}");
                return;
            }

            // No reachable core cell exists. Keep the original landing rather than lose the pawn.
            Log.Warning($"[RimExodus] No reachable core landing cell for downed pawn pod on tile {worldTile} at {c}.");
        }
    }
}
