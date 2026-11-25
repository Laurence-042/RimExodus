using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using RimWorld;

namespace RimExodus
{
    [HarmonyPatch]
    static class Patch_JobDriver_ExitMap_SeamlessRoute
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(JobDriver_Goto), "TryExitMap");
            yield return AccessTools.Method(typeof(JobDriver_Flee), "ExitMap");
        }

        static bool Prefix(JobDriver __instance)
        {
            var pawn = __instance.pawn;
            var map = pawn?.Map;
            var job = __instance.job;
            if (map == null || !SeamlessEvacuationRouting.IsOrdinaryExit(job)
                || !SeamlessMapTransferTrigger.HasEnterSpotAt(map, job.targetA.Cell)
                || !SeamlessRaidContinuity.CanWithdrawAcross(pawn.GetLord()))
                return true;

            // Entering the inner exit band must not finish an exit before the chosen
            // seam. The same-cell case reaches this driver without a pather step.
            if (pawn.Position != job.targetA.Cell)
            {
                SeamlessEvacuationDiagnostics.OnTryExitMap(pawn, job, "defer-target-spot");
                return false;
            }
            SeamlessEvacuationDiagnostics.OnTryExitMap(pawn, job, "at-target-spot");
            return SeamlessMapTransferTrigger.AllowNativeWithdrawal(pawn, map, pawn.Position);
        }
    }

    [HarmonyPatch(typeof(JobGiver_ExitMap), "TryGiveJob")]
    static class Patch_JobGiver_ExitMap_SeamlessRoute
    {
        static void Prefix(Pawn pawn, bool ___failIfCantJoinOrCreateCaravan, out Pawn __state)
        {
            __state = SeamlessEvacuationRouting.ExitSearchPawn;
            SeamlessEvacuationRouting.ExitSearchPawn = ___failIfCantJoinOrCreateCaravan ? null : pawn;
        }

        static void Finalizer(Pawn __state) => SeamlessEvacuationRouting.ExitSearchPawn = __state;
    }

    [HarmonyPatch(typeof(JobGiver_PrisonerEscape), "TryGiveJob")]
    static class Patch_JobGiver_PrisonerEscape_SeamlessRoute
    {
        static void Prefix(Pawn pawn, out Pawn __state)
        {
            __state = SeamlessEvacuationRouting.ExitSearchPawn;
            SeamlessEvacuationRouting.ExitSearchPawn = pawn;
        }

        static void Finalizer(Pawn __state) => SeamlessEvacuationRouting.ExitSearchPawn = __state;
    }
}
