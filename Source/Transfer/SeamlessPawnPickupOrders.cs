using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    internal static class SeamlessPawnPickupOrders
    {
        internal static bool IsPickupOrder(Job job)
        {
            return job?.playerForced == true && job.def != null && (job.def == JobDefOf.Rescue || job.def == JobDefOf.Capture
                || job.def == JobDefOf.Arrest || job.def == JobDefOf.CarryDownedPawnDrafted);
        }

        internal static bool HasForeignTarget(Pawn pawn, Job job)
        {
            return IsPickupOrder(job) && pawn?.Map != null
                && (IsForeign(job.targetA, pawn.Map) || IsForeign(job.targetB, pawn.Map));
        }

        private static bool IsForeign(LocalTargetInfo target, Map source)
            => target.Thing?.MapHeld is Map map && map != source;

        internal static bool TargetsOnMap(Job job, Map map)
        {
            return map != null && job.targetA.Thing is Pawn { Spawned: true, Dead: false } patient
                && patient.Map == map
                && (job.def == JobDefOf.CarryDownedPawnDrafted
                    || job.targetB.Thing is Building_Bed { Spawned: true } bed && bed.Map == map);
        }

        // StartJob runs after vanilla has preserved the player's ordering and queue semantics.
        // This job has not started, so the grant can carry it even if the pawn is already on the spot.
        internal static void BridgeOrReject(Pawn pawn, Job job)
        {
            var target = job.targetA.Thing;
            var map = target?.Map;
            SeamlessCommandTargets.Remove(pawn);
            if (TargetsOnMap(job, map) && map != pawn.Map
                && SeamlessBoundaryRules.IsCrossMapOrderable(pawn)
                && SeamlessViewProjection.TryProject(map, target.Position, pawn.Map, out _)
                && SeamlessCrossMapOrders.TryBridgeJob(pawn, map, target.Position, job))
                return;

            Messages.Message("NoPath".Translate(), pawn, MessageTypeDefOf.RejectInput, historical: false);
            JobMaker.ReturnToPool(job);
        }
    }

    // TryTakeOrderedJob calls this wrapper before it interrupts the current job or queues the
    // order. Defer both reservations to the original driver on arrival; write neither map here.
    [HarmonyPatch(typeof(Job), nameof(Job.TryMakePreToilReservations))]
    internal static class Patch_Job_TryMakePreToilReservations_CrossMapPawnPickup
    {
        private static bool Prefix(Job __instance, Pawn driverPawn, ref bool __result)
        {
            if (!SeamlessPawnPickupOrders.HasForeignTarget(driverPawn, __instance)) return true;
            __result = true;
            return false;
        }
    }
}
