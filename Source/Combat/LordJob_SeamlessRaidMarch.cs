using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// The outer-map leg of a sapper/breacher raid uses ordinary assault duties. The
    /// map-local breach destination is built only after the raiders reach the attacked map.
    /// The identity and original flags travel with the lord job through saves and transfers.
    /// </summary>
    public sealed class LordJob_SeamlessRaidMarch : LordJob_AssaultColony
    {
        private PlanetTile targetWorldTile = PlanetTile.Invalid;
        private string raidIdentity;
        private bool originalSappers;
        private bool originalBreachers;
        private bool originalCanKidnap;
        private bool originalCanTimeoutOrFlee = true;
        private bool originalCanSteal;
        private bool originalUseAvoidGridSmart;
        private bool originalCanPickUpOpportunisticWeapons;
        private int retreatDuration = -1;
        private bool marching;
        private bool targetUnavailableLogged;

        public int TargetWorldTile => targetWorldTile.tileId;
        internal PlanetTile TargetTile => targetWorldTile;
        public string RaidIdentity => raidIdentity;
        public bool Marching => marching;

        public LordJob_SeamlessRaidMarch()
        {
        }

        public LordJob_SeamlessRaidMarch(Faction faction, int targetWorldTile, string raidIdentity,
            bool sappers, bool breachers, bool canKidnap, bool canTimeoutOrFlee, bool canSteal, bool marching)
            : this(faction, new PlanetTile(targetWorldTile, -1), raidIdentity,
                sappers, breachers, canKidnap, canTimeoutOrFlee, canSteal, marching)
        {
        }

        internal LordJob_SeamlessRaidMarch(Faction faction, PlanetTile targetWorldTile, string raidIdentity,
            bool sappers, bool breachers, bool canKidnap, bool canTimeoutOrFlee, bool canSteal, bool marching,
            bool useAvoidGridSmart = false, bool canPickUpOpportunisticWeapons = false)
            : base(faction, canKidnap, canTimeoutOrFlee,
                sappers: !marching && sappers, useAvoidGridSmart: useAvoidGridSmart, canSteal: canSteal,
                breachers: !marching && breachers, canPickUpOpportunisticWeapons: canPickUpOpportunisticWeapons)
        {
            this.targetWorldTile = targetWorldTile;
            this.raidIdentity = raidIdentity;
            originalSappers = sappers;
            originalBreachers = breachers;
            originalCanKidnap = canKidnap;
            originalCanTimeoutOrFlee = canTimeoutOrFlee;
            originalCanSteal = canSteal;
            originalUseAvoidGridSmart = useAvoidGridSmart;
            originalCanPickUpOpportunisticWeapons = canPickUpOpportunisticWeapons;
            this.marching = marching;
        }

        public LordJob_SeamlessRaidMarch ForArrival(Faction faction, bool arrivalIsTarget)
        {
            return new LordJob_SeamlessRaidMarch(faction, targetWorldTile, raidIdentity,
                originalSappers, originalBreachers, originalCanKidnap,
                originalCanTimeoutOrFlee, originalCanSteal, marching: !arrivalIsTarget,
                originalUseAvoidGridSmart, originalCanPickUpOpportunisticWeapons) { retreatDuration = retreatDuration };
        }

        internal LordJob_SeamlessRaidMarch ForDestination(Faction faction, PlanetTile destination)
        {
            return marching && destination.Valid && destination == targetWorldTile
                ? ForArrival(faction, arrivalIsTarget: true) : this;
        }

        internal void PreserveRetreatDuration(StateGraph original)
        {
            var timer = SeamlessRaidContinuity.RetreatTimer(original);
            if (timer != null) retreatDuration = SeamlessRaidContinuity.RetreatDuration(timer);
        }

        public override StateGraph CreateGraph()
        {
            var graph = base.CreateGraph();
            var timer = SeamlessRaidContinuity.RetreatTimer(graph);
            if (retreatDuration >= 0 && timer != null) SeamlessRaidContinuity.RetreatDuration(timer) = retreatDuration;
            return graph;
        }

        public Job TryMakeMarchJob(Pawn pawn)
        {
            if (!marching || pawn?.Map == null) return null;
            if (!SeamlessTileGraph.TryGetMapByWorldTile(targetWorldTile, out var targetMap)
                || !SeamlessCrossMapOrders.CanBridgeTo(pawn, targetMap))
            {
                if (!targetUnavailableLogged)
                {
                    targetUnavailableLogged = true;
                    Log.Warning($"[RimExodus] Raid march {raidIdentity}: target world tile {targetWorldTile} "
                        + "is unavailable or cannot be bridged; vanilla assault timeout remains active.");
                }
                return null;
            }

            var fromTile = SeamlessTileRegistry.GetMapWorldTile(pawn.Map);
            if (!SeamlessViewProjection.TryProject(pawn.Map, pawn.Position, targetMap, out var projected))
                return null;
            var spotDef = SeamlessTransferGrants.EnterSpotDef;
            if (spotDef == null) return null;
            Thing best = null;
            var bestDistance = int.MaxValue;
            foreach (var spot in targetMap.listerThings.ThingsOfDef(spotDef))
            {
                var comp = spot.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null || comp.TargetTile != fromTile) continue;
                var distance = projected.DistanceToSquared(spot.Position);
                if (distance >= bestDistance) continue;
                best = spot;
                bestDistance = distance;
            }
            if (best == null) return null;
            targetUnavailableLogged = false;
            var job = JobMaker.MakeJob(JobDefOf.Goto, best);
            job.checkOverrideOnExpire = true;
            job.collideWithPawns = true;
            return job;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            SeamlessTileScribe.Look(ref targetWorldTile, "rimExodusTargetWorldTile");
            Scribe_Values.Look(ref raidIdentity, "rimExodusRaidIdentity");
            Scribe_Values.Look(ref originalSappers, "rimExodusOriginalSappers");
            Scribe_Values.Look(ref originalBreachers, "rimExodusOriginalBreachers");
            Scribe_Values.Look(ref originalCanKidnap, "rimExodusOriginalCanKidnap");
            Scribe_Values.Look(ref originalCanTimeoutOrFlee, "rimExodusOriginalCanTimeoutOrFlee", true);
            Scribe_Values.Look(ref originalCanSteal, "rimExodusOriginalCanSteal");
            Scribe_Values.Look(ref originalUseAvoidGridSmart, "rimExodusOriginalUseAvoidGridSmart");
            Scribe_Values.Look(ref originalCanPickUpOpportunisticWeapons, "rimExodusOriginalCanPickUpOpportunisticWeapons");
            Scribe_Values.Look(ref retreatDuration, "rimExodusRetreatDuration", -1);
            Scribe_Values.Look(ref marching, "rimExodusMarching");
            Scribe_Values.Look(ref targetUnavailableLogged, "rimExodusTargetUnavailableLogged");
        }
    }
}
