using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    internal static class SeamlessPawnPickupEvaluation
    {
        internal static bool TryGetNeighbor(Pawn traveler, Pawn target, out Map targetMap)
        {
            targetMap = target?.Map;
            return traveler?.Map != null && targetMap != null && targetMap != traveler.Map
                && SeamlessBoundaryRules.IsCrossMapOrderable(traveler)
                && Patches_ReachabilityCrossMap.IsCommandedToward(traveler, targetMap)
                && SeamlessViewProjection.TryProject(targetMap, target.Position, traveler.Map, out _);
        }
    }

    // Keep the original eligibility and reservation rules. This read-only check runs in the
    // patient's map after testing the approach to the seam in the rescuer's real map.
    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.CanRescueNow))]
    internal static class Patch_HealthAIUtility_CanRescueNow_CrossMap
    {
        private static bool Prefix(Pawn rescuer, Pawn patient, bool forced, ref bool __result,
            out SeamlessVirtualTeleporter __state)
        {
            __state = default;
            if (!forced || !SeamlessPawnPickupEvaluation.TryGetNeighbor(rescuer, patient, out var map))
                return true;
            if (!SeamlessCrossMapOrders.CanBridgeTo(rescuer, map))
            {
                __result = false;
                return false;
            }

            __state = new SeamlessVirtualTeleporter(rescuer, map, patient.Position);
            return true;
        }

        private static void Finalizer(SeamlessVirtualTeleporter __state) => __state.Dispose();
    }

    // The route to the bed begins at the pickup cell. FindBedFor reads the traveler's position
    // and reservations both when building the menu and when the chosen option creates its job.
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.FindBedFor),
        new[] { typeof(Pawn), typeof(Pawn), typeof(bool), typeof(bool), typeof(GuestStatus?) })]
    internal static class Patch_RestUtility_FindBedFor_CrossMap
    {
        private static void Prefix(Pawn sleeper, Pawn traveler, out SeamlessVirtualTeleporter __state)
        {
            __state = SeamlessPawnPickupEvaluation.TryGetNeighbor(traveler, sleeper, out var map)
                ? new SeamlessVirtualTeleporter(traveler, map, sleeper.Position)
                : default;
        }

        private static void Finalizer(SeamlessVirtualTeleporter __state) => __state.Dispose();
    }
}
