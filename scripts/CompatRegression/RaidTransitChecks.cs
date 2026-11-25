#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

internal static class RaidTransitChecks
{
    internal static void Run(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var continuity = assembly.GetType("RimExodus.SeamlessRaidContinuity", true)!;
        var grants = assembly.GetType("RimExodus.SeamlessTransferGrants", true)!;
        var routing = assembly.GetType("RimExodus.SeamlessEvacuationRouting", true)!;
        var create = grants.GetMethod("Create", statics)!;
        var pursue = Enum.Parse(create.GetParameters()[1].ParameterType, "Pursue");
        var tryGet = grants.GetMethod("TryGet", statics)!;
        var bindFollow = grants.GetMethod("BindFollow", statics)!;
        var matchesPhase = grants.GetMethod("MatchesAiPhase", statics)!;
        var needsReplan = grants.GetMethod("NeedsReplan", statics)!
            .CreateDelegate<Func<Pawn, Dictionary<Pawn, int>, bool>>();
        var isOrdinaryExit = routing.GetMethod("IsOrdinaryExit", statics)!.CreateDelegate<Func<Job, bool>>();
        var canWithdraw = continuity.GetMethod("CanWithdrawAcross", statics)!.CreateDelegate<Func<Lord, bool>>();
        var previousGame = Current.Game;
        var previousGoto = JobDefOf.Goto;
        var previousFlee = JobDefOf.Flee;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        game.components = new List<GameComponent>();
        game.tickManager = new TickManager();
        game.uniqueIDsManager = new UniqueIDsManager();
        var pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
        pawn.thingIDNumber = 2001;
        pawn.def = new ThingDef { defName = "RimExodusRaidTransitFixturePawn" };
        pawn.jobs = (Pawn_JobTracker)RuntimeHelpers.GetUninitializedObject(typeof(Pawn_JobTracker));
        var lord = new Lord();
        pawn.lord = lord;
        AccessTools.Field(typeof(Lord), "curJob").SetValue(lord, new LordJob_AssistColony());
        AccessTools.Field(typeof(Lord), "curLordToil").SetValue(lord, new LordToil_ExitMap());

        var path = Path.Combine(Path.GetTempPath(), "RimExodusRaidTransit-" + Guid.NewGuid() + ".xml");
        var prefsField = AccessTools.Field(typeof(Prefs), "data");
        var previousPrefs = prefsField.GetValue(null);
        var fixtureMod = (ModContentPack)RuntimeHelpers.GetUninitializedObject(typeof(ModContentPack));
        fixtureMod.assemblies = new ModAssemblyHandler(fixtureMod);
        fixtureMod.assemblies.loadedAssemblies.Add(assembly);

        try
        {
            Current.Game = game;
            prefsField.SetValue(null, new PrefsData());
            LoadedModManager.RunningModsListForReading.Add(fixtureMod);
            GenTypes.ClearCache();
            JobDefOf.Goto = new JobDef();
            JobDefOf.Flee = new JobDef();
            var job = new Job { def = JobDefOf.Goto, loadID = 2010, targetA = new IntVec3(4, 0, 9), exitMapOnArrival = true };
            pawn.jobs.curJob = job;
            check(canWithdraw(lord) && isOrdinaryExit(job), "allied native withdrawal uses its live exit job");
            job.failIfCantJoinOrCreateCaravan = true;
            check(!isOrdinaryExit(job), "mandatory caravan joining stays native");
            job.failIfCantJoinOrCreateCaravan = false;
            job.playerForced = true;
            check(!isOrdinaryExit(job), "ordered player departure stays native");
            job.playerForced = false;
            job.def = JobDefOf.Flee;
            check(isOrdinaryExit(job), "an explicit flee exit follows the native exit contract");
            job.exitMapOnArrival = false;
            check(!isOrdinaryExit(job), "an ordinary flee does not cross");
            job.def = JobDefOf.Goto;
            job.exitMapOnArrival = true;

            var state = (GameComponent)Activator.CreateInstance(continuity, game)!;
            var permit = create.Invoke(null, new[] { pawn, pursue })!;
            bindFollow.Invoke(null, new[] { permit, pawn, pawn });
            check((bool)matchesPhase.Invoke(null, new[] { pawn, permit })!,
                "a follow bridge belongs to its source phase");
            AccessTools.Field(typeof(Lord), "curLordToil").SetValue(lord, new LordToil_ExitMap());
            check(!(bool)matchesPhase.Invoke(null, new[] { pawn, permit })!,
                "replacing the source phase invalidates a pending seam bridge");
            object?[] current = { pawn, null };
            check((bool)tryGet.Invoke(null, current)!, "fixture has a live runtime bridge");

            Scribe.saver.InitSaving(path, "raidFixture");
            Scribe_Deep.Look(ref state, "continuity", game);
            Scribe.saver.FinalizeSaving();

            var xml = XDocument.Load(path);
            var component = xml.Root!.Element("continuity")!;
            check(component.Element("rimExodusRaidTransit") == null, "saved squads contain no transit permits");
            component.Add(new XElement("rimExodusRaidTransit", new XElement("li",
                new XElement("pawn", pawn.GetUniqueLoadID()),
                new XElement("driverJobId", job.loadID),
                new XElement("permit", new XElement("kind", "Evacuation")))));
            xml.Save(path);

            Scribe.loader.InitLoading(path);
            GameComponent loaded = null!;
            Scribe_Deep.Look(ref loaded, "continuity", game);
            var references = (LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                .GetValue(Scribe.loader.crossRefs)!;
            references.RegisterLoaded(pawn);
            Scribe.loader.FinalizeLoading();
            var obsolete = (System.Collections.IList)continuity.GetField("pendingTransit", instance)!.GetValue(loaded)!;
            var entry = obsolete[0]!;
            check(obsolete.Count == 1 && ReferenceEquals(pawn, AccessTools.Field(entry.GetType(), "Pawn").GetValue(entry))
                && (int)AccessTools.Field(entry.GetType(), "DriverJobId").GetValue(entry)! == job.loadID,
                "legacy save resolves the original transit driver identity without restoring its permit");

            var legacyIds = new Dictionary<Pawn, int> { [pawn] = job.loadID };
            check(!needsReplan(pawn, legacyIds), "a native exit job remains valid across loading");
            job.dutyTag = "RimExodus.Transit";
            check(needsReplan(pawn, legacyIds), "a legacy transit job is sent back to its think tree");
            job.dutyTag = "RimExodus.AiTransit";
            check(needsReplan(pawn, new Dictionary<Pawn, int>()), "new AI transit can replan without serialized routing data");
            loaded.StartedNewGame();
            check(!(bool)tryGet.Invoke(null, new object?[] { pawn, null })!, "starting a game clears the live runtime bridge");
        }
        finally
        {
            if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
            LoadedModManager.RunningModsListForReading.Remove(fixtureMod);
            GenTypes.ClearCache();
            prefsField.SetValue(null, previousPrefs);
            Current.Game = previousGame;
            JobDefOf.Goto = previousGoto;
            JobDefOf.Flee = previousFlee;
            File.Delete(path);
        }
    }
}
