using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    // The original Lord owns the state graph and quest signals. Each other map has a local
    // detachment so vanilla duties, reservations and target caches still see a local Lord.
    internal sealed class SeamlessRaidContinuity : GameComponent
    {
        private static readonly ConditionalWeakTable<Game, SeamlessRaidContinuity> Instances =
            new ConditionalWeakTable<Game, SeamlessRaidContinuity>();
        private List<Squad> squads = new List<Squad>();
        private List<SeamlessTransferGrants.SavedRaidTransit> pendingTransit;
        private readonly Dictionary<Lord, Squad> byLord = new Dictionary<Lord, Squad>();

        public SeamlessRaidContinuity(Game game)
        {
            Instances.Remove(game);
            Instances.Add(game, this);
        }

        internal static SeamlessRaidContinuity Current => Verse.Current.Game is Game game
            && Instances.TryGetValue(game, out var state) ? state : null;

        internal static bool IsPursuitPhase(Lord lord)
        {
            if (lord?.CurLordToil == null) return false;

            // Explicit native graph shapes. Unknown or map-anchored phases keep their local duties.
            var job = lord.LordJob.GetType();
            var phase = lord.CurLordToil.GetType();
            if (AuthorityStaysOnMap(lord.LordJob))
                return phase == typeof(LordToil_AssaultColony) && !CanReturnToDefence(lord);
            if (lord.ownedBuildings.Count != 0 || lord.ownedCorpses.Count != 0) return false;
            if (job == typeof(LordJob_AssaultColony) || job == typeof(LordJob_StageThenAttack)
                || job == typeof(LordJob_SeamlessRaidMarch) || job == typeof(LordJob_SeamlessAssaultDetachment))
                return phase == typeof(LordToil_AssaultColony);
            if (job == typeof(LordJob_ChimeraAssault)) return phase == typeof(LordToil_ChimeraAttack);
            if (job == typeof(LordJob_BossgroupAssaultColony)) return phase == typeof(LordToil_AssaultColonyBossgroup);
            if (job == typeof(LordJob_SlaveRebellion))
                return phase == typeof(LordToil_AssaultColony) || phase == typeof(LordToil_AssaultColonyPrisoners);
            return IsEntityAssault(job);
        }

        private static bool IsEntityAssault(Type job) =>
            job == typeof(LordJob_DevourerAssault) || job == typeof(LordJob_GorehulkAssault)
            || job == typeof(LordJob_FleshbeastAssault) || job == typeof(LordJob_SightstealerAssault);

        private static bool SupportsWithdrawal(LordJob job)
        {
            var type = job.GetType();
            return AuthorityStaysOnMap(job) || IsEntityAssault(type)
                || type == typeof(LordJob_AssaultColony) || type == typeof(LordJob_StageThenAttack)
                || type == typeof(LordJob_ChimeraAssault) || type == typeof(LordJob_BossgroupAssaultColony)
                || type == typeof(LordJob_SeamlessRaidMarch);
        }

        private static bool AuthorityStaysOnMap(LordJob job)
        {
            var type = job.GetType();
            return type == typeof(LordJob_Siege) || type == typeof(LordJob_HateChant)
                || type == typeof(LordJob_PsychicRitualRepeating) || type == typeof(LordJob_MechanoidsDefend)
                || type == typeof(LordJob_SleepThenMechanoidsDefend);
        }

        private static bool CanReturnToDefence(Lord lord)
        {
            if (!(lord.LordJob is LordJob_MechanoidDefendBase)) return false;
            return lord.Graph == null || lord.Graph.transitions.Exists(transition =>
                transition.sources.Contains(lord.CurLordToil) && transition.target is LordToil_DefendPoint);
        }

        internal static bool HasLocalEscortDuty(Lord lord)
        {
            return lord?.CurLordToil is LordToil_AssaultColonySappers
                || lord?.CurLordToil is LordToil_AssaultColonyBreaching
                || lord?.CurLordToil is LordToil_MoveInBossgroup;
        }

        internal Squad FindSquad(Lord lord)
        {
            if (lord != null && byLord.TryGetValue(lord, out var squad)) return squad;
            return null;
        }

        internal static bool HasRemoteMembers(Squad squad)
        {
            return squad.Detachments.Exists(local => IsRegistered(local) && local.ownedPawns.Count != 0);
        }

        internal bool NeedsActiveAuthority(Map map)
        {
            foreach (var squad in squads)
            {
                if (!IsRegistered(squad.Authority) || squad.Authority.Map != map) continue;
                foreach (var local in squad.Detachments)
                    if (IsRegistered(local) && local.ownedPawns.Count != 0 && !SeamlessDormancyManager.IsDormant(local.Map))
                        return true;
            }
            return false;
        }

        internal Squad DetachForHandoff(Lord lord, Pawn pawn, bool pursuing)
        {
            if (lord == null || !lord.ownedPawns.Contains(pawn)) return null;
            if (pursuing && (!IsPursuitPhase(lord) || lord.LordJob is LordJob_SlaveRebellion)) return null;
            if (!pursuing && !IsWithdrawalPhase(lord.CurLordToil)) return null;
            if (!byLord.TryGetValue(lord, out var squad))
            {
                if (!pursuing && !SupportsWithdrawal(lord.LordJob)) return null;
                squad = new Squad { Authority = lord, AttackedMap = lord.Map };
                squads.Add(squad);
                byLord.Add(lord, squad);
            }

            // ExitedMap is a real departure to vanilla. A tracked handoff changes the local
            // member list without firing the squad's loss, defeat or quest completion hooks.
            lord.RemovePawn(pawn);
            if (lord != squad.Authority && lord.ownedPawns.Count == 0) RemoveDetachment(squad, lord);
            return squad;
        }

        internal void RelocateSpawnedSquad(Lord lord, Map map, List<Pawn> pawns, LordJob replacement)
        {
            var squad = new Squad { Authority = lord, AttackedMap = lord.Map };
            squads.Add(squad);
            byLord.Add(lord, squad);
            MoveAuthority(squad, map, null);
            if (replacement != null)
            {
                var originalGraph = lord.Graph;
                if (replacement is LordJob_SeamlessRaidMarch march) march.PreserveRetreatDuration(originalGraph);
                lord.SetJob(replacement);
                lord.GotoToil(lord.Graph.StartingToil);
                CopyRetreatProgress(originalGraph, lord.Graph);
            }
            foreach (var pawn in pawns) AddHandoffPawn(lord, pawn);
        }

        internal static bool IsWithdrawalPhase(LordToil phase) => phase is LordToil_ExitMap || phase is LordToil_PanicFlee;

        internal bool TracksWithdrawal(Lord lord)
        {
            return IsWithdrawalPhase(lord?.CurLordToil)
                && (FindSquad(lord) != null || SupportsWithdrawal(lord.LordJob));
        }

        internal void Arrive(Squad squad, Pawn pawn, Map map, LordJob departureJob, int departurePhase, bool pursuing)
        {
            var authority = squad.Authority;
            departureJob ??= authority.LordJob;
            var arrivalJob = pursuing && departureJob is LordJob_SeamlessRaidMarch march
                ? march.ForDestination(authority.faction, SeamlessTileRegistry.GetMapWorldTile(map))
                : departureJob;
            bool startsSpecialistWork = arrivalJob != departureJob;
            if (startsSpecialistWork && authority.LordJob is LordJob_SeamlessRaidMarch { Marching: true })
            {
                // At first contact on the attacked map, the original Lord starts the native
                // specialist phase. The rear of the column keeps marching under local orders.
                if (authority.ownedPawns.Count != 0)
                {
                    var rear = MakeDetachment(squad, authority.Map, authority.LordJob, departurePhase);
                    MoveMembers(authority, rear);
                }
                var marchingGraph = authority.Graph;
                MoveAuthority(squad, map, null);
                authority.SetJob(arrivalJob);
                authority.GotoToil(authority.Graph.StartingToil);
                CopyRetreatProgress(marchingGraph, authority.Graph);
                AddHandoffPawn(authority, pawn);
                return;
            }
            var local = squad.LocalLord(map, arrivalJob);
            if (authority.ownedPawns.Count == 0 && map != authority.Map && !AuthorityStaysOnMap(authority.LordJob)
                && !UsesDifferentMarchGraph(authority.LordJob, arrivalJob))
            {
                MoveAuthority(squad, map, local);
                local = authority;
            }
            local ??= MakeDetachment(squad, map, arrivalJob, startsSpecialistWork ? -1 : departurePhase);
            AddHandoffPawn(local, pawn);
        }

        private static void AddHandoffPawn(Lord lord, Pawn pawn)
        {
            int originalCount = lord.numPawnsEverGained;
            lord.AddPawn(pawn);
            lord.numPawnsEverGained = originalCount;
        }

        private static bool UsesDifferentMarchGraph(LordJob first, LordJob second)
        {
            return first is LordJob_SeamlessRaidMarch a && second is LordJob_SeamlessRaidMarch b
                && a.Marching != b.Marching;
        }

        internal static bool UsesDifferentMarchGraph(Lord first, Lord second) =>
            UsesDifferentMarchGraph(first.LordJob, second.LordJob);

        private Lord MakeDetachment(Squad squad, Map map, LordJob job, int departurePhase)
        {
            var authority = squad.Authority;
            var local = new Lord
            {
                loadID = Find.UniqueIDsManager.GetNextLordID(),
                faction = authority.faction,
                lordManager = map.lordManager
            };
            map.lordManager.AddLord(local);
            local.SetJob(AuthorityStaysOnMap(authority.LordJob)
                ? new LordJob_SeamlessAssaultDetachment { AvoidGrid = authority.CurLordToil.useAvoidGrid }
                : CloneJob(job));
            var phase = squad.MatchingPhase(local);
            if (phase == null && departurePhase >= 0 && departurePhase < local.Graph.lordToils.Count)
                phase = local.Graph.lordToils[departurePhase];
            local.GotoToil(phase ?? local.Graph.StartingToil);
            if (UsesDifferentMarchGraph(authority, local)) CopyRetreatProgress(authority.Graph, local.Graph);
            else local.ticksInToil = authority.ticksInToil;
            squad.Detachments.Add(local);
            byLord.Add(local, squad);
            return local;
        }

        // Only the explicitly checked native jobs are cloned. Their job configuration is
        // separate from the Lord/StateGraph and their local toils are rebuilt by SetJob.
        private static readonly Func<object, object> Clone = AccessTools.MethodDelegate<Func<object, object>>(
            AccessTools.Method(typeof(object), "MemberwiseClone"));

        private static LordJob CloneJob(LordJob original)
        {
            var job = (LordJob)Clone(original);
            job.lord = null;
            return job;
        }

        internal static readonly AccessTools.FieldRef<Trigger_TicksPassed, int> RetreatDuration =
            AccessTools.FieldRefAccess<Trigger_TicksPassed, int>("duration");

        internal static Trigger_TicksPassed RetreatTimer(StateGraph graph)
        {
            foreach (var transition in graph.transitions)
            {
                if (!(transition.target is LordToil_ExitMap)) continue;
                foreach (var trigger in transition.triggers)
                    if (trigger.GetType() == typeof(Trigger_TicksPassed)) return (Trigger_TicksPassed)trigger;
            }
            return null;
        }

        private static void CopyRetreatProgress(StateGraph source, StateGraph destination)
        {
            var previous = RetreatTimer(source);
            var current = RetreatTimer(destination);
            if (previous != null && current != null && previous.data is TriggerData_TicksPassed from
                && current.data is TriggerData_TicksPassed to)
            {
                RetreatDuration(current) = RetreatDuration(previous);
                to.ticksPassed = from.ticksPassed;
            }
        }

        private void MoveAuthority(Squad squad, Map map, Lord local)
        {
            var authority = squad.Authority;
            var previous = authority.Map;
            // Keep the native graph, load ID and signal registration. RemoveLord would dispose them.
            previous.lordManager.lords.Remove(authority);
            previous.events.Notify_LordRemoved(authority);
            map.lordManager.lords.Add(authority);
            authority.lordManager = map.lordManager;
            map.events.Notify_LordAdded(authority);
            if (local == null) return;

            MoveMembers(local, authority);
            RemoveDetachment(squad, local);
        }

        private static void MoveMembers(Lord from, Lord to)
        {
            for (int i = from.ownedPawns.Count - 1; i >= 0; i--)
            {
                var member = from.ownedPawns[i];
                from.RemovePawn(member);
                AddHandoffPawn(to, member);
                if (member.CurJob?.lord == from) member.CurJob.lord = to;
            }
        }

        private void RemoveDetachment(Squad squad, Lord lord)
        {
            squad.Detachments.Remove(lord);
            byLord.Remove(lord);
            if (IsRegistered(lord)) lord.lordManager.RemoveLord(lord);
        }

        private static bool IsRegistered(Lord lord) => lord?.lordManager?.lords.Contains(lord) == true;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref squads, "rimExodusRaidContinuity", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.Saving) pendingTransit = SeamlessTransferGrants.CaptureRaidTransit(this);
            Scribe_Collections.Look(ref pendingTransit, "rimExodusRaidTransit", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.Saving) pendingTransit = null;
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            squads ??= new List<Squad>();
            byLord.Clear();
            foreach (var squad in squads)
            {
                if (squad.Authority == null) continue;
                byLord[squad.Authority] = squad;
                foreach (var local in squad.Detachments)
                    if (local != null) byLord[local] = squad;
            }
        }

        public override void LoadedGame()
        {
            SeamlessTransferGrants.RestoreRaidTransit(this, pendingTransit);
            pendingTransit = null;
        }

        public override void StartedNewGame() => SeamlessTransferGrants.RestoreRaidTransit(this, null);

        public override void GameComponentTick()
        {
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                var squad = squads[i];
                var authority = squad.Authority;
                if (!IsRegistered(authority))
                {
                    // Native quest orders (including inSignalLeave) may disband the authority.
                    // Disband the local pieces too, without creating extra quest receivers.
                    for (int j = squad.Detachments.Count - 1; j >= 0; j--)
                    {
                        var local = squad.Detachments[j];
                        if (local != null) RemoveDetachment(squad, local);
                    }
                    if (authority != null) byLord.Remove(authority);
                    squads.RemoveAt(i);
                    continue;
                }

                for (int j = squad.Detachments.Count - 1; j >= 0; j--)
                {
                    var local = squad.Detachments[j];
                    if (IsRegistered(local) && local.ownedPawns.Count != 0) continue;
                    if (local != null) RemoveDetachment(squad, local);
                    else squad.Detachments.RemoveAt(j);
                }

                if (authority.ownedPawns.Count == 0 && !AuthorityStaysOnMap(authority.LordJob))
                {
                    var local = squad.Detachments.Find(candidate => !UsesDifferentMarchGraph(authority, candidate));
                    if (local != null) MoveAuthority(squad, local.Map, local);
                }
                if (authority.ownedPawns.Count == 0 && squad.Detachments.Count == 0 && !NativeShouldExist(authority))
                {
                    EndEmptySquad(authority);
                    continue;
                }

                squad.SyncPhases();
            }
        }

        private static readonly Action<Lord> EndEmptySquad = AccessTools.MethodDelegate<Action<Lord>>(
            AccessTools.Method(typeof(Lord), "Destroy"));
        private static readonly Func<Lord, bool> NativeShouldExist = AccessTools.MethodDelegate<Func<Lord, bool>>(
            AccessTools.PropertyGetter(typeof(Lord), "ShouldExist"));

        public sealed class Squad : IExposable
        {
            internal Lord Authority;
            internal Map AttackedMap;
            internal List<Lord> Detachments = new List<Lord>();
            internal int LastTick = -1;

            internal Lord LocalLord(Map map, LordJob job)
            {
                if (Authority.Map == map && !UsesDifferentMarchGraph(Authority.LordJob, job)) return Authority;
                return Detachments.Find(lord => IsRegistered(lord) && lord.Map == map
                    && !UsesDifferentMarchGraph(lord.LordJob, job));
            }

            internal LordToil MatchingPhase(Lord local)
            {
                var phase = Authority.CurLordToil;
                if (phase == null) return null;
                var toils = local.Graph.lordToils;
                if (local.LordJob is LordJob_SeamlessAssaultDetachment && phase.GetType() == typeof(LordToil_AssaultColony))
                    return local.Graph.StartingToil;
                int index = Authority.Graph.lordToils.IndexOf(phase);
                if (!UsesDifferentMarchGraph(Authority, local) && index >= 0 && index < toils.Count
                    && toils[index].GetType() == phase.GetType()) return toils[index];
                if (!IsWithdrawalPhase(phase)) return null;
                // Native graphs append PanicFlee only on maps with a legal exit. Preserve the
                // withdrawal order in a local graph that only has the ordinary exit phase.
                return toils.Find(candidate => candidate.GetType() == phase.GetType())
                    ?? toils.Find(candidate => candidate is LordToil_ExitMap);
            }

            internal void SyncPhases()
            {
                foreach (var local in Detachments)
                {
                    if (!IsRegistered(local)) continue;
                    var phase = MatchingPhase(local);
                    if (phase != null && local.CurLordToil != phase)
                    {
                        local.GotoToil(phase);
                        local.ticksInToil = Authority.ticksInToil;
                    }
                }
            }

            internal List<Pawn> ActiveAssaultMembers()
            {
                List<Pawn> participants = null;
                foreach (var local in Detachments)
                {
                    if (!IsRegistered(local) || SeamlessDormancyManager.IsDormant(local.Map)
                        || UsesDifferentMarchGraph(Authority, local)) continue;
                    participants ??= new List<Pawn>(Authority.ownedPawns);
                    participants.AddRange(local.ownedPawns);
                }
                return participants ?? Authority.ownedPawns;
            }

            public void ExposeData()
            {
                Scribe_References.Look(ref Authority, "authority");
                Scribe_References.Look(ref AttackedMap, "attackedMap");
                Scribe_Collections.Look(ref Detachments, "detachments", LookMode.Reference);
                if (Scribe.mode == LoadSaveMode.PostLoadInit) Detachments ??= new List<Lord>();
            }
        }
    }

    // Both triggers choose candidates via each pawn's real map. Keep their native timing,
    // danger checks and threshold while letting the original Lord see both halves of the squad.
    [HarmonyPatch]
    internal static class Patch_RaidLord_AssaultOpportunity
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Trigger_KidnapVictimPresent), nameof(Trigger_KidnapVictimPresent.ActivateOn));
            yield return AccessTools.Method(typeof(Trigger_HighValueThingsAround), nameof(Trigger_HighValueThingsAround.ActivateOn));
            yield return AccessTools.Method(typeof(StealAIUtility), nameof(StealAIUtility.StartStealingMarketValueThreshold));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var ownedPawns = AccessTools.Field(typeof(Lord), nameof(Lord.ownedPawns));
            var groupMembers = AccessTools.Method(typeof(Patch_RaidLord_AssaultOpportunity), nameof(GroupMembers));
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(ownedPawns))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = groupMembers;
                }
                yield return instruction;
            }
        }

        private static List<Pawn> GroupMembers(Lord lord)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(lord);
            return squad?.Authority == lord ? squad.ActiveAssaultMembers() : lord.ownedPawns;
        }
    }

    // Anchored controllers keep their native graph and site references. Only the terminal
    // assault duty is represented on another map, with local exit duties for a later retreat.
    public sealed class LordJob_SeamlessAssaultDetachment : LordJob
    {
        internal bool AvoidGrid;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.AddToil(new LordToil_AssaultColony { useAvoidGrid = AvoidGrid });
            graph.AddToil(new LordToil_ExitMap(LocomotionUrgency.Jog, interruptCurrentJob: true) { useAvoidGrid = true });
            graph.AddToil(new LordToil_PanicFlee { useAvoidGrid = true });
            return graph;
        }

        public override bool AddFleeToil => false;

        public override void ExposeData() => Scribe_Values.Look(ref AvoidGrid, "avoidGrid");
    }

    [HarmonyPatch(typeof(Lord), "ShouldExist", MethodType.Getter)]
    internal static class Patch_RaidLord_ShouldExist
    {
        private static void Postfix(Lord __instance, ref bool __result)
        {
            if (__result) return;
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            __result = squad?.Authority == __instance && SeamlessRaidContinuity.HasRemoteMembers(squad);
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.LordTick))]
    internal static class Patch_RaidLord_Tick
    {
        private static bool Prefix(Lord __instance)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            if (squad == null) return true;
            if (squad.Authority != __instance)
            {
                if (SeamlessRaidContinuity.UsesDifferentMarchGraph(squad.Authority, __instance))
                    return true;
                // Local duties can use their own map. Group clocks and transitions run once.
                __instance.ticksInToil = squad.Authority.ticksInToil;
                __instance.CurLordToil.LordToilTick();
                return false;
            }
            int tick = GenTicks.TicksGame;
            if (squad.LastTick == tick) return false;
            squad.LastTick = tick;
            return true;
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.GotoToil))]
    internal static class Patch_RaidLord_PhaseChange
    {
        private static void Postfix(Lord __instance)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            if (squad?.Authority == __instance) squad.SyncPhases();
        }
    }

    [HarmonyPatch(typeof(Lord), "CheckTransitionOnSignal")]
    internal static class Patch_RaidLord_TransitionSignal
    {
        internal static readonly Func<Lord, TriggerSignal, bool> Dispatch =
            AccessTools.MethodDelegate<Func<Lord, TriggerSignal, bool>>(
                AccessTools.Method(typeof(Lord), "CheckTransitionOnSignal"));

        private static bool Prefix(Lord __instance, TriggerSignal signal, ref bool __result)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            if (squad == null || squad.Authority == __instance) return true;
            if (signal.type == TriggerSignalType.PawnDamaged && squad.Authority.lordManager.lords.Contains(squad.Authority))
                __result = Dispatch(squad.Authority, signal);
            return SeamlessRaidContinuity.UsesDifferentMarchGraph(squad.Authority, __instance);
        }
    }

    [HarmonyPatch(typeof(Trigger_TicksPassed), nameof(Trigger_TicksPassed.ActivateOn))]
    internal static class Patch_RaidLord_RetreatClock
    {
        private static bool Prefix(Trigger_TicksPassed __instance, Lord lord, TriggerSignal signal, ref bool __result)
        {
            if (signal.type != TriggerSignalType.Tick) return true;
            var squad = SeamlessRaidContinuity.Current?.FindSquad(lord);
            if (squad == null || squad.Authority == lord
                || !SeamlessRaidContinuity.UsesDifferentMarchGraph(squad.Authority, lord)
                || __instance != SeamlessRaidContinuity.RetreatTimer(lord.Graph))
                return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Trigger_FractionPawnsLost), nameof(Trigger_FractionPawnsLost.ActivateOn))]
    internal static class Patch_RaidLord_GroupLossThreshold
    {
        private static bool Prefix(Lord lord, ref bool __result)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(lord);
            if (squad == null || squad.Authority == lord) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    internal static class Patch_RaidLord_MemberLost
    {
        private static void Prefix(Lord __instance, Pawn pawn, out SeamlessRaidContinuity.Squad __state)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            __state = squad != null && squad.Authority != __instance && __instance.ownedPawns.Contains(pawn)
                ? squad : null;
        }

        private static void Postfix(Lord __instance, Pawn pawn, PawnLostCondition cond, DamageInfo? dinfo,
            SeamlessRaidContinuity.Squad __state)
        {
            if (__state == null || __instance.ownedPawns.Contains(pawn)) return;
            var authority = __state.Authority;
            if (authority?.lordManager?.lords.Contains(authority) != true) return;
            if (cond == PawnLostCondition.Incapped || cond == PawnLostCondition.Killed || cond == PawnLostCondition.MadePrisoner)
                authority.numPawnsLostViolently++;
            authority.LordJob.Notify_PawnLost(pawn, cond);
            var signal = new TriggerSignal(TriggerSignalType.PawnLost) { thing = pawn, condition = cond };
            if (dinfo.HasValue) signal.dinfo = dinfo.Value;
            Patch_RaidLord_TransitionSignal.Dispatch(authority, signal);
        }
    }

    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnDowned))]
    internal static class Patch_RaidLord_MemberDowned
    {
        private static bool Prefix(Lord __instance, Pawn pawn)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            if (squad == null || squad.Authority == __instance) return true;
            squad.Authority.LordJob.Notify_PawnDowned(pawn);
            return false;
        }
    }

    [HarmonyPatch(typeof(Lord), "Init")]
    internal static class Patch_RaidLord_InitialColonyHealth
    {
        private static void Postfix(Lord __instance)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(__instance);
            if (squad != null && squad.Authority == __instance && squad.AttackedMap != null && !squad.AttackedMap.Disposed)
                __instance.initialColonyHealthTotal = squad.AttackedMap.wealthWatcher.HealthTotal;
        }
    }

    [HarmonyPatch(typeof(Trigger_FractionColonyDamageTaken), nameof(Trigger_FractionColonyDamageTaken.ActivateOn))]
    internal static class Patch_RaidLord_ColonyDamage
    {
        private static bool Prefix(Lord lord, TriggerSignal signal, TriggerData ___data,
            float ___desiredColonyDamageFraction, float ___minDamage, ref bool __result)
        {
            var squad = SeamlessRaidContinuity.Current?.FindSquad(lord);
            if (squad == null) return true;
            if (squad.Authority != lord)
            {
                __result = false;
                return false;
            }
            if (squad.AttackedMap == lord.Map) return true;
            __result = signal.type == TriggerSignalType.Tick && squad.AttackedMap != null && !squad.AttackedMap.Disposed
                && ___data is TriggerData_FractionColonyDamageTaken progress
                && squad.AttackedMap.damageWatcher.DamageTakenEver > progress.startColonyDamage
                    + Math.Max(lord.initialColonyHealthTotal * ___desiredColonyDamageFraction, ___minDamage);
            return false;
        }
    }

    [HarmonyPatch(typeof(Trigger_FractionColonyDamageTaken), nameof(Trigger_FractionColonyDamageTaken.SourceToilBecameActive))]
    internal static class Patch_RaidLord_ColonyDamageBaseline
    {
        private static void Postfix(Transition transition, LordToil previousToil, TriggerData ___data)
        {
            var lord = transition.target.lord;
            var squad = SeamlessRaidContinuity.Current?.FindSquad(lord);
            if (squad != null && squad.Authority == lord && squad.AttackedMap != null && !squad.AttackedMap.Disposed
                && !transition.sources.Contains(previousToil) && ___data is TriggerData_FractionColonyDamageTaken progress)
                progress.startColonyDamage = squad.AttackedMap.damageWatcher.DamageTakenEver;
        }
    }
}
