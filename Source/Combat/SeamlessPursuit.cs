using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    internal static class SeamlessPursuit
    {
        internal static bool CanStart(Pawn pawn)
        {
            var lord = pawn?.GetLord();
            return lord == null || SeamlessRaidContinuity.IsPursuitPhase(lord);
        }

        internal static Thing Target(Pawn pawn, Job job)
        {
            if (job == null || job.playerForced || job.exitMapOnArrival || pawn?.mindState == null
                || job.jobGiver is JobGiver_AIFollowPawn)
                return null;
            var attack = job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic;
            if (!attack && job.def != JobDefOf.Goto) return null;
            if (pawn.Faction == Faction.OfPlayer && !pawn.InAggroMentalState
                && !(pawn.GetLord()?.LordJob is LordJob_SlaveRebellion)) return null;
            if (attack) return job.targetA.Thing;

            var target = job.targetA.Thing;
            if (target != null)
                return pawn.HostileTo(target) || IsRaidMarch(pawn, target.Map) ? target : null;

            // A cast-position Goto has a cell target. The duty and the live enemy jointly prove intent.
            return SeamlessRaidContinuity.IsPursuitPhase(pawn.GetLord())
                ? pawn.mindState.enemyTarget : null;
        }

        internal static bool CanCross(Pawn pawn, Thing target, Map arrival)
        {
            if (target?.Spawned != true || target.Map != arrival || !pawn.Spawned || pawn.Downed || pawn.Dead)
                return false;
            if (IsRaidMarch(pawn, arrival)) return CanStart(pawn);
            return SeamlessCombatCoords.Enabled && pawn.HostileTo(target) && CanStart(pawn);
        }

        private static bool IsRaidMarch(Pawn pawn, Map arrival)
        {
            return arrival != null && pawn.GetLord()?.LordJob is LordJob_SeamlessRaidMarch march
                && march.Marching && SeamlessTileRegistry.GetMapWorldTile(arrival) == march.TargetTile;
        }
    }
}
