#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

internal static class RaidTransitChecks
{
    internal static void Run(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var continuity = assembly.GetType("RimExodus.SeamlessRaidContinuity", true)!;
        var grants = assembly.GetType("RimExodus.SeamlessTransferGrants", true)!;
        var create = grants.GetMethod("Create", statics)!;
        var pursue = Enum.Parse(create.GetParameters()[1].ParameterType, "Pursue");
        var evacuation = Enum.Parse(create.GetParameters()[1].ParameterType, "Evacuation");
        var bind = grants.GetMethod("BindPursuit", statics)!;
        var tryGet = grants.GetMethod("TryGet", statics)!;
        var isWithdrawal = continuity.GetMethod("IsWithdrawalPhase", statics)!;
        check((bool)isWithdrawal.Invoke(null, new object[] {
            RuntimeHelpers.GetUninitializedObject(typeof(LordToil_PanicFlee)) })!, "native panic flee uses the raid withdrawal handoff");

        var previousGame = Current.Game;
        var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        game.components = new List<GameComponent>();
        game.tickManager = new TickManager();
        game.uniqueIDsManager = new UniqueIDsManager();
        var maps = new List<Map> { new Map(), new Map() };
        foreach (var map in maps)
        {
            map.events = new MapEvents(map);
            map.lordManager = new LordManager(map);
        }
        AccessTools.Field(typeof(Game), "maps").SetValue(game, maps);

        var jobDef = new JobDef { defName = "RimExodusRaidTransitFixture" };
        DefDatabase<JobDef>.Add(jobDef);
        var hunter = NewPawn(0, 2001);
        var victim = NewPawn(1, 2002);
        var evacuee = NewPawn(0, 2003);
        var lord = new Lord { lordManager = maps[0].lordManager };
        AccessTools.Field(typeof(Lord), "curJob").SetValue(lord, new LordJob_AssaultColony(null, canTimeoutOrFlee: true));
        AccessTools.Field(typeof(Lord), "curLordToil").SetValue(lord,
            RuntimeHelpers.GetUninitializedObject(typeof(LordToil_PanicFlee)));
        evacuee.lord = lord;
        evacuee.jobs.curJob = new Job { def = jobDef, loadID = 2012, exitMapOnArrival = true };
        hunter.jobs.curJob = new Job { def = jobDef, loadID = 2010, dutyTag = "RimExodus.Transit" };
        var original = new Job { def = jobDef, loadID = 2011, targetA = victim,
            expiryInterval = 90, startTick = 20, canBashDoors = true };
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
            var state = (GameComponent)Activator.CreateInstance(continuity, game)!;
            var permit = create.Invoke(null, new[] { hunter, pursue })!;
            var spotField = permit.GetType().GetField("BoundSpot", fields)!;
            var spot = new IntVec3(4, 0, 9);
            spotField.SetValue(permit, spot);
            bind.Invoke(null, new[] { permit, hunter, victim, original });

            var exitPermit = create.Invoke(null, new[] { evacuee, evacuation })!;
            var visitedField = exitPermit.GetType().GetField("VisitedTiles", fields)!;
            var origin = new PlanetTile(18, -1);
            visitedField.SetValue(exitPermit, new HashSet<PlanetTile> { origin });

            Scribe.saver.InitSaving(path, "raidFixture");
            Scribe.saver.loadIDsErrorsChecker.RegisterDeepSaved(hunter, "hunter");
            Scribe.saver.loadIDsErrorsChecker.RegisterDeepSaved(victim, "victim");
            Scribe.saver.loadIDsErrorsChecker.RegisterDeepSaved(evacuee, "evacuee");
            Scribe_Deep.Look(ref state, "continuity", game);
            Scribe.saver.FinalizeSaving();

            GameComponent Load()
            {
                Scribe.loader.InitLoading(path);
                GameComponent loaded = null!;
                Scribe_Deep.Look(ref loaded, "continuity", game);
                if (loaded == null)
                    throw new InvalidOperationException("Could not load the raid fixture.\n"
                        + string.Join("\n", Log.Messages.TakeLast(6).Select(message => message.text)));
                var references = (LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs)!;
                references.RegisterLoaded(hunter);
                references.RegisterLoaded(victim);
                references.RegisterLoaded(evacuee);
                Scribe.loader.FinalizeLoading();
                loaded.LoadedGame();
                return loaded;
            }

            var loaded = Load();
            object?[] get = { hunter, null };
            check((bool)tryGet.Invoke(null, get)! && !ReferenceEquals(get[1], permit),
                "Scribe restores a pursuit permit for the exact live transit job");
            var restored = get[1]!;
            var next = (Job)restored.GetType().GetField("NextJob", fields)!.GetValue(restored)!;
            check(Equals(spotField.GetValue(restored), spot) && next.targetA.Thing == victim
                && next.canBashDoors && Equals(restored.GetType().GetField("PursuitJobDeadline", fields)!
                    .GetValue(restored), 110), "raid transit round-trips the seam, target, job settings and original deadline");
            object?[] getExit = { evacuee, null };
            check((bool)tryGet.Invoke(null, getExit)! && !ReferenceEquals(getExit[1], exitPermit)
                && ((HashSet<PlanetTile>)visitedField.GetValue(getExit[1])!).SetEquals(new[] { origin }),
                "even a raid's first panic exit survives saving with its visited tiles");

            hunter.jobs.curJob.loadID++;
            loaded = Load();
            check(!(bool)tryGet.Invoke(null, new object?[] { hunter, null })!,
                "loading cannot grant a different job permission to cross a seam");
            loaded.StartedNewGame();
        }
        finally
        {
            if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop();
            LoadedModManager.RunningModsListForReading.Remove(fixtureMod);
            GenTypes.ClearCache();
            prefsField.SetValue(null, previousPrefs);
            Current.Game = previousGame;
            File.Delete(path);
        }
    }

    private static Pawn NewPawn(sbyte mapIndex, int id)
    {
        var pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
        pawn.thingIDNumber = id;
        pawn.def = new ThingDef { defName = "RimExodusRaidFixturePawn" };
        pawn.jobs = (Pawn_JobTracker)RuntimeHelpers.GetUninitializedObject(typeof(Pawn_JobTracker));
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, mapIndex);
        return pawn;
    }
}
