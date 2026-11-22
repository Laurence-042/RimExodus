using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR") ?? @"D:\SteamLibrary\steamapps\common\RimWorld";
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(game, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, name.Name + ".dll")) : null;
AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony"
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("HARMONY_PATH")
        ?? @"D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll") : null;
// The game host is already loaded during real startup; the offline console must load it explicitly.
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "RimWorld.dll"));
if (args.Contains("dormant-raids")) DormantRaidChecks.Run();
else if (args.Length == 2 && args[0] == "endless-horizon") EndlessHorizonChecks.Run(args[1]);
else Audit.Run(args.Contains("preview-drift"), args.Contains("ref-backend"));

public static class Audit
{
    public static void BestAttackTarget(int value) { }
    public static void BestAttackTarget(string value) { }
    public static bool Global, OnThread, ThrowGetter;
    public static bool ReadGlobal() => ThrowGetter ? throw new InvalidOperationException("Injected getter failure") : Global;
    public static bool ReadThread() => OnThread;
    public static void Run(bool drift, bool shared)
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
        var assembly = typeof(RimExodus.RimExodusMod).Assembly;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        CheckSeamlessLayerTopology(assembly, statics, Check);
        var preview = assembly.GetType("RimExodus.SeamlessMapPreviewCompat", true)!;
        if (!drift)
        {
            CheckRaidContinuity(assembly, statics, Check);
            CheckCastGateComposition(shared, Check);
        }
        bool Read(string name) => (bool)preview.GetProperty(name, statics)!.GetValue(null)!;
        Check(!Read("IsPreviewInFlight") && !Read("IsGeneratingPreviewOnCurrentThread"), "absent MapPreview is inert");
        var harmony = new Harmony("RE.Regression.Compat");
        try
        {
            foreach (string name in new[] { "SeamlessAchtungCompat", "SeamlessPerspectiveShiftCompat", "SeamlessVehiclesCompat", "SeamlessVMFTargetingCompat", "SeamlessVMFDisembarkDiagnostics", "SeamlessVEFCompat", "SeamlessDMSCompat", "SeamlessGLRiverCompat", "SeamlessSurveillanceCompat" })
            {
                assembly.GetType("RimExodus." + name, true)!.GetMethod("Register", statics)!.Invoke(null, new object[] { harmony });
                Check(!harmony.GetPatchedMethods().Any(), "absent adapter is inert: " + name);
            }
            var vmfTransit = assembly.GetType("RimExodus.SeamlessVMFTransitCompat", true)!;
            vmfTransit.GetMethod("Register", statics)!.Invoke(null, null);
            vmfTransit.GetMethod("BeforeVehicleTransfer", statics)!.Invoke(null, new object[] { null, null });
            Check(!harmony.GetPatchedMethods().Any(), "absent VMF transfer guard is inert");
            var refPreview = typeof(RimExodus.Maps.MapModule).Assembly.GetType("RimExodus.Maps.MapPreviewGenerationPatch", true)!;
            void RegisterPreview() => refPreview.GetMethod("Register", statics)!.Invoke(null, new object[] { harmony });
            RegisterPreview();
            Check(!harmony.GetPatchedMethods().Any(), "absent preview installs no hook");
            var target = LoadPreviewFixture(drift);
            Global = OnThread = true;
            if (drift)
            {
                Check(!Read("IsPreviewInFlight"), "signature drift is contained");
                Check(!Read("IsGeneratingPreviewOnCurrentThread"), "drift remains disabled");
                Console.WriteLine($"{checks} MapPreview signature-drift checks passed (synthetic API fixture).");
                return;
            }
            Check(Read("IsPreviewInFlight") && Read("IsGeneratingPreviewOnCurrentThread"), "late load resolves previous absence");
            OnThread = false;
            Check(Read("IsPreviewInFlight") && !Read("IsGeneratingPreviewOnCurrentThread"), "global and thread status stay distinct");
            Global = false; OnThread = true;
            Check(!Read("IsGeneratingPreviewOnCurrentThread"), "thread-only flag is insufficient");
            RegisterPreview(); RegisterPreview();
            var info = Harmony.GetPatchInfo(target);
            Check(info.Prefixes.Count(p => p.owner == harmony.Id) == 1 && info.Finalizers.Count(p => p.owner == harmony.Id) == 1,
                "manual preview registration retries absence and is idempotent");
            ThrowGetter = true;
            Check(!Read("IsPreviewInFlight"), "getter failure contained");
            ThrowGetter = false; Global = true;
            Check(!Read("IsPreviewInFlight"), "failed getter does not flood retries");

            // Real PatchAll, not a mirror of Harmony's discovery rules. Manual helpers must be ignored.
            if (shared)
            {
                assembly.GetType("RimExodus.SeamlessFramework", true)!.GetField("<Enabled>k__BackingField", statics)!.SetValue(null, true);
                typeof(RimExodus.Maps.MapModule).GetMethod("InstallCorePatches", statics)!.Invoke(null, new object[] { harmony });
                typeof(RimExodus.Maps.MapCombatModule).GetMethod("InstallCombatPatches", statics)!.Invoke(null, new object[] { harmony });
            }
            harmony.PatchAll(assembly);
            var projectileOwner = shared ? typeof(RimExodus.Maps.MapModule).Assembly : assembly;
            Check(harmony.GetPatchedMethods().Any(method => method.DeclaringType == typeof(Projectile)
                    && method.Name == "TickInterval" && Harmony.GetPatchInfo(method).Prefixes
                        .Any(patch => patch.owner == harmony.Id && patch.PatchMethod.DeclaringType!.Assembly == projectileOwner)),
                "vanilla mortar projectiles retain the active backend's seam handoff");
            CheckSplitRaidDefeat(assembly, Check);
            RaidTransitChecks.Run(assembly, Check);
            if (shared)
            {
                var callbacks = harmony.GetPatchedMethods().Select(Harmony.GetPatchInfo).SelectMany(p =>
                    p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers))
                    .Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod.DeclaringType).ToHashSet();
                Check(!assembly.GetTypes().Where(t => t.GetMethod("UseLegacyBackend", statics) != null).Any(callbacks.Contains),
                    "REF mode excludes legacy combat callbacks");
                Check(callbacks.Any(t => t!.GetCustomAttributes(false).Any(a => a.GetType().Name == "CombatPatchAttribute")),
                    "REF mode installs shared combat callbacks");
            }
            Check(!harmony.GetPatchedMethods().Select(Harmony.GetPatchInfo).SelectMany(p =>
                p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers))
                .Any(p => p.owner == harmony.Id && p.PatchMethod.DeclaringType != refPreview
                    && !Attribute.IsDefined(p.PatchMethod.DeclaringType!, typeof(HarmonyPatch))), "automatic scan excludes unannotated helpers");
            typeof(RimExodus.RimExodusMod).GetMethod("RegisterManualPatches", statics)!.Invoke(null, new object[] { harmony });
            foreach (string name in new[] { "FindRoadExitCell", "ApplyDistanceField", "RefinePath", "GetMapEdgeNodes" })
                Check(harmony.GetPatchedMethods().Any(m => m.Name == name), "manual hook bound: " + name);
            RiverSeamChecks.Run(assembly, harmony, Check);
            Check(harmony.GetPatchedMethods().Count(m => m.Name == "TryFindRandomEdgeCellWith") == 2, "both manual edge overloads bound");
            var binder = assembly.GetType("RimExodus.SeamlessPatchBinding", true)!.GetMethod("TryBind", statics)!;
            bool Attempt(Func<MethodBase> lookup) => (bool)binder.Invoke(null, new object[] {
                harmony, "injected missing target", lookup, typeof(Audit), nameof(ReadThread), false })!;
            Check(!Attempt(() => null!), "missing manual target contained");
            Check(!Attempt(() => throw new AmbiguousMatchException("Injected lookup drift")), "lookup failure contained before Patch");
            CheckAchtungHook(assembly, harmony, Check);
            CheckVmfTargetingHook(assembly, harmony, Check);
            foreach (var adapter in new[] { assembly.GetType("RimExodus.SeamlessCombatExtendedCompat", true)!,
                typeof(RimExodus.Maps.MapModule).Assembly.GetType("RimExodus.Maps.CombatExtendedBridge", true)! })
            {
                var ready = adapter.GetField("initialized", statics)!;
                object previous = ready.GetValue(null)!;
                int before = harmony.GetPatchedMethods().Count();
                try
                {
                    ready.SetValue(null, true);
                    // Ambiguous finder lookup throws before TryPatch. Execute the real deferred guard.
                    adapter.GetMethod("RegisterDeferredHooks", statics)!.Invoke(null, new object[] { harmony, null!, null!, typeof(Audit) });
                    Check(harmony.GetPatchedMethods().Count() == before, "deferred CE signature failure contained: " + adapter.Name);
                }
                finally { ready.SetValue(null, previous); }
            }
            int gameMethods = harmony.GetPatchedMethods().Count(m => m.Module.Assembly != target.Module.Assembly);
            Console.WriteLine($"{checks} compatibility checks passed ({(shared ? "REF" : "legacy")}); automatic/manual hooks bound {gameMethods} game methods plus one preview fixture. No game launched.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void CheckRaidContinuity(Assembly assembly, BindingFlags statics, Action<bool, string> check)
    {
        var continuity = assembly.GetType("RimExodus.SeamlessRaidContinuity", true)!;
        var canPursue = continuity.GetMethod("IsPursuitPhase", statics)!.CreateDelegate<Func<Lord, bool>>();
        var toilField = AccessTools.Field(typeof(Lord), "curLordToil");
        var jobField = AccessTools.Field(typeof(Lord), "curJob");
        var lord = new Lord();
        var original = new LordJob_AssaultColony(null, canTimeoutOrFlee: false);
        jobField.SetValue(lord, original);
        toilField.SetValue(lord, new LordToil_AssaultColony());
        check(canPursue(lord), "an active assault is portable without a faction");

        toilField.SetValue(lord, new LordToil_AssaultColonySappers());
        check(!canPursue(lord), "active construction targets anchor sappers to their map");
        toilField.SetValue(lord, new LordToil_AssaultColonyBreaching());
        check(!canPursue(lord), "an active breaching grid anchors the squad to its map");
        jobField.SetValue(lord, new LordJob_Siege());
        toilField.SetValue(lord, new LordToil_Siege(IntVec3.Zero, 60f));
        check(!canPursue(lord), "an active siege remains attached to its emplacements");
        var attack = new LordToil_AssaultColony();
        toilField.SetValue(lord, attack);
        check(canPursue(lord), "a siege squad can pursue after switching to the assault subgraph");

        jobField.SetValue(lord, new LordJob_ChimeraAssault());
        toilField.SetValue(lord, new LordToil_ChimeraAttack());
        check(canPursue(lord), "a chimera's combat phase is portable");
        toilField.SetValue(lord, new LordToil_ChimeraStalk());
        check(!canPursue(lord), "stalking cannot issue a pursuit grant");

        jobField.SetValue(lord, RuntimeHelpers.GetUninitializedObject(typeof(LordJob_PsychicRitualRepeating)));
        toilField.SetValue(lord, new LordToil_DefendPoint(IntVec3.Zero, 10f));
        check(!canPursue(lord), "ritual defenders remain on the ritual map");
        toilField.SetValue(lord, new LordToil_AssaultColony());
        check(canPursue(lord), "post-ritual assault is portable while its original controller stays anchored");
        jobField.SetValue(lord, RuntimeHelpers.GetUninitializedObject(typeof(LordJob_HateChant)));
        toilField.SetValue(lord, new LordToil_HateChant());
        check(!canPursue(lord), "active chants cannot issue pursuit grants");
        toilField.SetValue(lord, new LordToil_AssaultColony());
        check(canPursue(lord), "chanters can pursue after the chant has ended");

        var outerSpawn = assembly.GetType("RimExodus.SeamlessRaidOuterSpawn", true)!;
        var usesNativeEntry = outerSpawn
            .GetMethod("UsesNativeEntry", statics)!.CreateDelegate<Func<RaidStrategyDef, bool>>();
        foreach (var worker in new[] { typeof(RaidStrategyWorker_SiegeMechanoid),
            typeof(RaidStrategyWorker_PsychicRitualSiege) })
            check(usesNativeEntry(new RaidStrategyDef { workerClass = worker }), worker.Name + " keeps native entry");
        check(!usesNativeEntry(new RaidStrategyDef { workerClass = typeof(RaidStrategyWorker_Siege) }),
            "ordinary walking siege can choose a neighboring camp");
        check(!usesNativeEntry(new RaidStrategyDef { workerClass = typeof(RaidStrategyWorker_StageThenAttack) }),
            "staged walking raids can select a neighboring arrival map");
        check(!usesNativeEntry(new RaidStrategyDef { workerClass = typeof(RaidStrategyWorker_ImmediateAttackBreaching) }),
            "breachers can march to their attacked map");
        CheckRaidEntryPreferences(outerSpawn, statics, usesNativeEntry, check);

        var forDestination = typeof(RimExodus.LordJob_SeamlessRaidMarch).GetMethod("ForDestination",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        const int attackedTile = 123;
        var target = new PlanetTile(attackedTile, -1);
        var neighbor = new PlanetTile(124, -1);
        foreach (var sappers in new[] { true, false })
        {
            var march = new RimExodus.LordJob_SeamlessRaidMarch(null, attackedTile, "raid-fixture",
                sappers, !sappers, false, false, false, marching: true);
            check(forDestination.Invoke(march, new object[] { null!, neighbor }) == march
                && march.CreateGraph().StartingToil is LordToil_AssaultColony, "march uses regular combat on the host map");
            var attackJob = (RimExodus.LordJob_SeamlessRaidMarch)forDestination.Invoke(march, new object[] { null!, target })!;
            var expectedPhase = sappers ? typeof(LordToil_AssaultColonySappers) : typeof(LordToil_AssaultColonyBreaching);
            check(!attackJob.Marching && attackJob.CreateGraph().StartingToil.GetType() == expectedPhase,
                "only arrival on the attacked tile activates " + expectedPhase.Name);
            check(forDestination.Invoke(attackJob, new object[] { null!, neighbor }) == attackJob
                && forDestination.Invoke(attackJob, new object[] { null!, target }) == attackJob,
                "an operational squad does not reset when moving away or back");
        }

        var previousGame = Current.Game;
        try
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            Current.Game = game;
            var component = Activator.CreateInstance(continuity, game)!;
            Map MakeMap()
            {
                var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
                map.events = new MapEvents(map);
                map.lordManager = new LordManager(map);
                return map;
            }
            var source = MakeMap();
            var destination = MakeMap();
            lord.lordManager = source.lordManager;
            source.lordManager.lords.Add(lord);
            var stagingJob = new LordJob_StageThenAttack(null, new IntVec3(4, 0, 7), 259);
            var stagingToil = new LordToil_Stage(new IntVec3(4, 0, 7));
            var graph = new StateGraph();
            graph.AddToil(stagingToil);
            graph.AddToil(attack);
            AccessTools.Field(typeof(Lord), "graph").SetValue(lord, graph);
            jobField.SetValue(lord, stagingJob);
            toilField.SetValue(lord, stagingToil);
            lord.ticksInToil = 167;
            lord.numPawnsLostViolently = 2;
            lord.numPawnsEverGained = 7;
            lord.questTags = new List<string> { "raid-quest" };
            lord.inSignalLeave = "raid-leave";
            lord.loadID = 211;

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            var canStage = outerSpawn.GetMethod("CanStageOnHost", statics)!.CreateDelegate<Func<Lord, bool>>();
            check(canStage(lord) && !canPursue(lord), "a staged raid can relocate at spawn without allowing a premature pursuit");
            var newStage = new IntVec3(26, 0, 31);
            outerSpawn.GetMethod("SetStagingPoint", statics)!.Invoke(null, new object[] { lord, newStage });
            continuity.GetMethod("RelocateSpawnedSquad", instance)!
                .Invoke(component, new object[] { lord, destination, new List<Pawn>(), null! });
            check(source.lordManager.lords.Count == 0 && destination.lordManager.lords.Single() == lord,
                "whole-squad handoff keeps the original Lord object");
            check(lord.LordJob == stagingJob && lord.Graph == graph && lord.CurLordToil == stagingToil && lord.ticksInToil == 167,
                "handoff retains the running graph, exact phase and elapsed time");
            check(stagingToil.FlagLoc == newStage
                && (IntVec3)AccessTools.Field(typeof(LordJob_StageThenAttack), "stageLoc").GetValue(stagingJob)! == newStage
                && (int)AccessTools.Field(typeof(LordJob_StageThenAttack), "raidSeed").GetValue(stagingJob)! == 259,
                "only the running and saved staging coordinates change; the native raid seed survives");
            check(lord.numPawnsLostViolently == 2 && lord.numPawnsEverGained == 7,
                "handoff preserves the original squad casualty denominator and losses");
            check(lord.loadID == 211 && lord.questTags.Single() == "raid-quest" && lord.inSignalLeave == "raid-leave",
                "the original quest receiver keeps its identity, tags and leave signal");
            toilField.SetValue(lord, attack);
            check(!canStage(lord) && canPursue(lord), "the staged squad can pursue after switching to native assault");

            var siegeSource = MakeMap();
            var siegeHost = MakeMap();
            var siegeLord = new Lord { lordManager = siegeSource.lordManager, loadID = 212, ticksInToil = 50 };
            siegeSource.lordManager.lords.Add(siegeLord);
            var oldCamp = new IntVec3(12, 0, 14);
            const float budget = 96f;
            var siegeJob = new LordJob_Siege(null, oldCamp, budget);
            var travel = new LordToil_Travel(oldCamp);
            var siege = new LordToil_Siege(oldCamp, budget);
            var siegeGraph = new StateGraph();
            siegeGraph.AddToil(travel);
            siegeGraph.AddToil(siege);
            AccessTools.Field(typeof(Lord), "graph").SetValue(siegeLord, siegeGraph);
            jobField.SetValue(siegeLord, siegeJob);
            toilField.SetValue(siegeLord, travel);

            var preparing = outerSpawn.GetMethod("PreparingSiege", statics)!.CreateDelegate<Func<Lord, LordToilData_Siege>>();
            var siegeData = (LordToilData_Siege)siege.data;
            check(preparing(siegeLord) == siegeData && !canPursue(siegeLord),
                "siege camp can move before construction without allowing premature pursuit");
            var newCamp = new IntVec3(62, 0, 54);
            outerSpawn.GetMethod("SetSiegePoint", statics)!.Invoke(null, new object[] { siegeLord, siegeData, newCamp });
            continuity.GetMethod("RelocateSpawnedSquad", instance)!
                .Invoke(component, new object[] { siegeLord, siegeHost, new List<Pawn>(), null! });
            check(siegeSource.lordManager.lords.Count == 0 && siegeHost.lordManager.lords.Single() == siegeLord
                && siegeLord.Graph == siegeGraph && siegeLord.CurLordToil == travel && siegeLord.ticksInToil == 50,
                "neighbor siege retains the original Lord, graph and travel clock");
            check(travel.FlagLoc == newCamp && siege.FlagLoc == newCamp && siegeData.blueprintPoints == budget
                && (IntVec3)AccessTools.Field(typeof(LordJob_Siege), "siegeSpot").GetValue(siegeJob)! == newCamp
                && (float)AccessTools.Field(typeof(LordJob_Siege), "blueprintPoints").GetValue(siegeJob)! == budget,
                "travel, artillery construction and saved job use the same camp without rerolling its budget");
            toilField.SetValue(siegeLord, siege);
            check(preparing(siegeLord) == null && !canPursue(siegeLord), "an established siege cannot move its camp");
        }
        finally { Current.Game = previousGame; }
    }

    private static void CheckRaidEntryPreferences(Type outerSpawn, BindingFlags statics,
        Func<RaidStrategyDef, bool> usesNativeEntry, Action<bool, string> check)
    {
        var set = outerSpawn.GetMethod("SetEntryPreference", statics)!;
        var read = outerSpawn.GetMethod("EntryPreference", statics)!;
        var scopeType = set.GetParameters()[1].ParameterType;
        var preferenceType = set.GetParameters()[3].ParameterType;
        var settingsField = AccessTools.Field(typeof(RimExodus.RimExodusMod), "<Settings>k__BackingField");
        var previous = settingsField.GetValue(null);
        var settings = new RimExodus.RimExodusSettings();
        settings.raidOuterSpawnLocalIncidents.Add("fixture");
        settingsField.SetValue(null, settings);
        string Read(string scope) => read.Invoke(null, new[] { settings, Enum.Parse(scopeType, scope), "fixture" })!.ToString()!;
        void Set(string scope, string value) => set.Invoke(null, new[] { settings, Enum.Parse(scopeType, scope),
            "fixture", Enum.Parse(preferenceType, value) });
        try
        {
            var strategy = new RaidStrategyDef { defName = "fixture", workerClass = typeof(RaidStrategyWorker_SiegeMechanoid) };
            var isMapBound = outerSpawn.GetMethod("IsStrategyMapBound", statics)!.CreateDelegate<Func<RaidStrategyDef, bool>>();
            var nativeReason = outerSpawn.GetMethod("ResolvedNativeReason", statics)!.CreateDelegate<Func<IncidentParms, string>>();
            var arrival = new PawnsArrivalModeDef { defName = "fixture", walkIn = true };
            var parms = new IncidentParms { raidStrategy = strategy, raidArrivalMode = arrival };
            check(Read("Incident") == "Native" && Read("Strategy") == "Default", "old incident preferences keep their scope");
            Set("Strategy", "Outer");
            check(!usesNativeEntry(strategy) && isMapBound(strategy) && Read("Incident") == "Native",
                "a strategy override permits edge selection while keeping the siege bound to its map");
            Set("Arrival", "Native");
            check(Read("Arrival") == "Native" && nativeReason(parms)?.Contains("configured") == true,
                "a resolved walk-in arrival can independently request vanilla placement and name the reason");
            Set("Arrival", "Outer");
            arrival.walkIn = false;
            check(nativeReason(parms)?.Contains("requires native placement") == true,
                "non-walking arrivals keep native placement");
            settings.logCombat = true;
            check(RimExodus.RimExodusLog.Enabled(RimExodus.RimExodusLogModule.Combat)
                && !RimExodus.RimExodusLog.Enabled(RimExodus.RimExodusLogModule.CombatExtended),
                "raid messages use the cross-map combat switch independently of the CE switch");
            outerSpawn.GetMethod("ResetEntryPreferences", statics)!.Invoke(null, new object[] { settings });
            check(usesNativeEntry(strategy) && Read("Incident") == "Default" && Read("Arrival") == "Default",
                "reset restores each raid entry default and clears legacy overrides");
            strategy.workerClass = typeof(RaidStrategyWorker_Siege);
            Set("Strategy", "Native");
            check(usesNativeEntry(strategy), "players can opt ordinary siege back into native entry");
            outerSpawn.GetMethod("ResetEntryPreferences", statics)!.Invoke(null, new object[] { settings });
            check(!usesNativeEntry(strategy), "reset restores neighboring entry for ordinary siege");
        }
        finally { settingsField.SetValue(null, previous); }
    }

    private static void CheckSplitRaidDefeat(Assembly assembly, Action<bool, string> check)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var continuity = assembly.GetType("RimExodus.SeamlessRaidContinuity", true)!;
        var detach = continuity.GetMethod("DetachForHandoff", instance)!;
        var arrive = continuity.GetMethod("Arrive", instance)!;
        var previousGame = Current.Game;
        try
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            game.signalManager = new SignalManager();
            game.uniqueIDsManager = new UniqueIDsManager();
            game.tickManager = new TickManager();
            game.components = new List<GameComponent>();
            Current.Game = game;
            var component = (GameComponent)Activator.CreateInstance(continuity, game)!;
            var receiver = new RaidSignalReceiver();
            game.signalManager.RegisterReceiver(receiver);

            Map MakeMap()
            {
                var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
                map.events = new MapEvents(map);
                map.lordManager = new LordManager(map);
                map.deferredSpawner = new DeferredSpawner(map);
                map.attackTargetsCache = new AttackTargetsCache(map);
                return map;
            }
            Pawn MakePawn(int id)
            {
                var pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
                pawn.def = new ThingDef { defName = "RaidFixturePawn" };
                pawn.kindDef = new PawnKindDef { combatPower = 140f };
                pawn.thingIDNumber = id;
                pawn.mindState = (Pawn_MindState)RuntimeHelpers.GetUninitializedObject(typeof(Pawn_MindState));
                return pawn;
            }

            var source = MakeMap();
            var destination = MakeMap();
            var localPawn = MakePawn(1);
            var pursuer = MakePawn(2);
            var secondPursuer = MakePawn(3);
            var lord = LordMaker.MakeNewLord(new Faction { def = new FactionDef { autoFlee = false } },
                new LordJob_GorehulkAssault(), source, new[] { localPawn, pursuer, secondPursuer });
            lord.questTags = new List<string> { "raid-fixture" };
            lord.ticksInToil = 514;
            var graph = lord.Graph;
            var originalLootThreshold = StealAIUtility.StartStealingMarketValueThreshold(lord);
            var squad = detach.Invoke(component, new object[] { lord, pursuer, true });
            arrive.Invoke(component, new[] { squad, pursuer, destination, lord.LordJob, (object)0, true });
            detach.Invoke(component, new object[] { lord, secondPursuer, true });
            arrive.Invoke(component, new[] { squad, secondPursuer, destination, lord.LordJob, (object)0, true });
            var local = pursuer.GetLord();
            check(local != lord && local == secondPursuer.GetLord() && local.Map == destination
                && local.LordJob.GetType() == lord.LordJob.GetType(),
                "split raid preserves native local orders under the original squad");
            check(StealAIUtility.StartStealingMarketValueThreshold(lord) == originalLootThreshold,
                "the native loot threshold still counts the full active squad");
            local.LordTick();
            check(local.ticksInToil == lord.ticksInToil && lord.ticksInToil == 514 && lord.numPawnsEverGained == 3,
                "a detachment uses the squad clock and does not count transfers as reinforcements");
            local.Notify_PawnLost(secondPursuer, PawnLostCondition.Killed);
            check(lord.numPawnsLostViolently == 1 && receiver.Defeats == 0,
                "a detachment reports real casualties to the original squad");

            lord.Notify_PawnLost(localPawn, PawnLostCondition.Killed);
            check(receiver.Defeats == 0 && source.lordManager.lords.Contains(lord),
                "the original map clearing does not emit defeat while a detachment remains");
            component.GameComponentTick();
            check(pursuer.GetLord() == lord && lord.Map == destination && lord.Graph == graph
                && lord.numPawnsLostViolently == 2, "remaining members regain the same authority and casualty count");
            lord.Notify_PawnLost(pursuer, PawnLostCondition.Killed);
            check(receiver.Defeats == 1, "real squad defeat emits the original quest signal exactly once");
        }
        finally { Current.Game = previousGame; }
    }

    private sealed class RaidSignalReceiver : ISignalReceiver
    {
        internal int Defeats;

        public void Notify_SignalReceived(Signal signal)
        {
            if (signal.tag == "raid-fixture.AllEnemiesDefeated") Defeats++;
        }
    }

    internal static void CheckSeamlessLayerTopology(Assembly assembly, BindingFlags statics, Action<bool, string> check,
        bool checkHorizon = false)
    {
        var previous = Current.Game;
        T Allocate<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        void Set<T>(T instance, string field, object value) where T : class
            => AccessTools.Field(typeof(T), field).SetValue(instance, value);

        Map MakeMap(PlanetTile tile, MapParent parent)
        {
            Set<WorldObject>(parent, "tile", tile);
            var map = Allocate<Map>();
            map.info = new MapInfo { parent = parent, Size = new IntVec3(32, 1, 32) };
            var manager = new RimExodus.SeamlessTileManager(map);
            map.components = new List<MapComponent> { manager };
            Set(manager, "ProviderMapPrepared", true);
            return map;
        }

        try
        {
            var game = Allocate<Game>();
            var world = Allocate<World>();
            var grid = Allocate<WorldGrid>();
            var surface = Allocate<SurfaceLayer>();
            var seabed = Allocate<SurfaceLayer>();
            var layerDef = new PlanetLayerDef { defName = "Regression_Seabed" };
            using var offsets = new NativeArray<int>(2, Allocator.Persistent);
            Set<PlanetLayer>(seabed, "tileIDToNeighbors_offsets", offsets);
            Set<PlanetLayer>(surface, "isRootSurface", true);
            Set<PlanetLayer>(seabed, "layerId", 2);
            Set<PlanetLayer>(seabed, "def", layerDef);
            Set(grid, "surface", surface);
            Set(grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = surface, [2] = seabed });
            world.grid = grid;
            world.worldObjects = new WorldObjectsHolder();
            Set(game, "worldInt", world);
            Current.Game = game;

            var surfaceMap = MakeMap(new PlanetTile(1, surface), Allocate<RimExodus.MapParent_SeamlessTile>());
            var source = MakeMap(new PlanetTile(0, seabed), Allocate<MapParent>());
            var target = MakeMap(new PlanetTile(1, seabed), Allocate<MapParent>());
            Set(game, "maps", new List<Map> { surfaceMap, source, target });
            var provider = new RegressionLayerProvider();
            check(RimExodus.SeamlessMapProviders.Register(layerDef, provider), "custom layer can register");
            source.GetComponent<RimExodus.SeamlessTileManager>().SetNeighbor(1, target.Parent, provider.Offset);

            var parse = assembly.GetType("RimExodus.SeamlessTileScribe", true)!.GetMethod("Parse", statics)!
                .CreateDelegate<Func<string, PlanetTile>>();
            check(parse("1") == surfaceMap.Tile && parse("1,2") == target.Tile, "released integer and native layered addresses load");
            var findLayeredMap = typeof(RimExodus.SeamlessTileGraph).GetMethod("TryGetMapByWorldTile", statics,
                null, new[] { typeof(PlanetTile), typeof(Map).MakeByRefType() }, null)!;
            var lookup = new object[] { target.Tile, null };
            check(RimExodus.SeamlessTileGraph.TryGetMapByWorldTile(1, out var root) && root == surfaceMap
                && (bool)findLayeredMap.Invoke(null, lookup)! && (Map)lookup[1] == target,
                "global tile lookup distinguishes equal indices on separate layers");
            check(RimExodus.SeamlessTileGraph.TryGetNeighborLink(source, target, out var link)
                && link.map == target && link.offset == provider.Offset
                && !RimExodus.SeamlessTileGraph.TryGetNeighborLink(source, surfaceMap, out _),
                "a two-map query resolves exactly the linked neighbor");
            check(RimExodus.SeamlessTileGraph.TryGetNeighborLinkByWorldTile(source, 1, out link)
                && link.map == target && link.worldTile == 1, "legacy neighbor indices are scoped to the source layer");

            source.info.isPocketMap = true;
            check(!RimExodus.SeamlessTileGraph.TryGetNeighborLink(source, target, out _), "a pocket map cannot expose outgoing links");
            source.info.isPocketMap = false;
            target.info.isPocketMap = true;
            check(!(bool)findLayeredMap.Invoke(null, lookup)!
                && !RimExodus.SeamlessTileGraph.TryGetNeighborLink(source, target, out _)
                && RimExodus.SeamlessTileGraph.GetAllNeighbors(source).Count == 0
                && !RimExodus.SeamlessViewProjection.TryProject(target, IntVec3.Zero, source, out _),
                "a stale pocket destination cannot be looked up, enumerated or projected");
            target.info.isPocketMap = false;

            var topology = assembly.GetType("RimExodus.SeamlessMapTopology", true)!;
            var edges = topology.GetMethod("Edges", statics)!.CreateDelegate<Func<PlanetTile, int, List<RimExodus.SeamlessMapEdge>>>();
            var tryOffset = topology.GetMethod("TryOffset", statics)!;
            var connection = new object[] { source.Tile, 32, target.Tile, 32, IntVec3.Zero };
            check(edges(source.Tile, 32)[1].Destination == target.Tile && (bool)tryOffset.Invoke(null, connection)!
                && (IntVec3)connection[4] == provider.Offset, "an aligned edge is advertised and resolves to its offset");
            provider.Offset = new IntVec3(100, 0, 0);
            check(!edges(source.Tile, 32)[1].Destination.Valid && !edges(target.Tile, 32)[3].Destination.Valid
                && !(bool)tryOffset.Invoke(null, connection)!, "a misaligned reciprocal edge is closed at both ends and cannot link");
            if (checkHorizon)
            {
                provider.Offset = new IntVec3(24, 0, 0);
                provider.Hexagonal = true;
                RimExodus.SeamlessPolygonGeometry.ClearAllCaches();
                EndlessHorizonChecks.CheckGeometry(source, check);
            }
        }
        finally
        {
            RimExodus.SeamlessPolygonGeometry.ClearAllCaches();
            Current.Game = previous;
        }
    }

    private sealed class RegressionLayerProvider : RimExodus.SeamlessMapProvider
    {
        internal IntVec3 Offset = new IntVec3(24, 0, 0);
        internal bool Hexagonal;

        public override bool Accepts(PlanetTile tile, MapParent parent) => true;
        public override WorldObjectDef GetMapParentDef(PlanetTile tile) => null!;
        public override IReadOnlyList<Vector2> GetFootprint(PlanetTile tile, int mapSize)
            => Hexagonal
                ? new[] { new Vector2(4, 10), new Vector2(16, 4), new Vector2(28, 10), new Vector2(28, 22), new Vector2(16, 28), new Vector2(4, 22) }
                : new[] { new Vector2(4, 4), new Vector2(28, 4), new Vector2(28, 28), new Vector2(4, 28) };

        public override RimExodus.SeamlessMapEdge GetEdge(PlanetTile tile, int edge, PlanetTile nativeNeighbor)
            => (tile.tileId, edge) switch
            {
                (0, 1) => new RimExodus.SeamlessMapEdge(new PlanetTile(1, tile.Layer), Offset),
                (1, 3) => new RimExodus.SeamlessMapEdge(new PlanetTile(0, tile.Layer), -Offset),
                _ => default
            };
    }

    private static void CheckAchtungHook(Assembly assembly, Harmony harmony, Action<bool, string> check)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Achtung.ControllerFixture"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("fixture");
        var type = module.DefineType("AchtungMod.Controller", TypeAttributes.Public);
        var method = type.DefineMethod("MouseDown", MethodAttributes.Public,
            typeof(bool), new[] { typeof(UnityEngine.Vector3), typeof(int), typeof(bool) });
        var il = method.GetILGenerator();
        var cell = il.DeclareLocal(typeof(IntVec3));
        il.Emit(OpCodes.Ldloca_S, cell);
        il.Emit(OpCodes.Initobj, typeof(IntVec3));
        il.Emit(OpCodes.Ldloc, cell);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Standable),
            new[] { typeof(IntVec3), typeof(Map) })!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, cell);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable),
            new[] { typeof(IntVec3), typeof(Map) })!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, cell);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged),
            new[] { typeof(IntVec3), typeof(Map) })!);
        il.Emit(OpCodes.Ret);
        var target = type.CreateType()!.GetMethod("MouseDown")!;

        var adapter = assembly.GetType("RimExodus.SeamlessAchtungCompat", true)!;
        adapter.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { harmony });
        var transpiler = adapter.GetMethod("MouseDownTranspiler", BindingFlags.Static | BindingFlags.NonPublic)!;
        check(Harmony.GetPatchInfo(target)?.Transpilers
            .Any(p => p.owner == harmony.Id && p.PatchMethod == transpiler) == true,
            "Achtung MouseDown REF cell-space hooks bind against expected signature and IL anchors");
        harmony.Unpatch(target, transpiler);
    }

    private static void CheckVmfTargetingHook(Assembly assembly, Harmony harmony, Action<bool, string> check)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VMF.TargetingFixture"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("fixture");
        var type = module.DefineType("VehicleMapFramework.GenUIOnVehicle", TypeAttributes.Public);
        var method = type.DefineMethod("TargetsAtMouse", MethodAttributes.Public | MethodAttributes.Static,
            typeof(IEnumerable<LocalTargetInfo>), new[] { typeof(TargetingParameters), typeof(bool), typeof(ITargetingSource) });
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret);
        var target = type.CreateType()!.GetMethod("TargetsAtMouse")!;
        var vmfDriver = module.DefineType("VehicleMapFramework.JobDriverAcrossMaps", TypeAttributes.Public);
        var destMapGetter = vmfDriver.DefineMethod("get_DestMap", MethodAttributes.Public | MethodAttributes.SpecialName,
            typeof(Map), Type.EmptyTypes);
        var destIl = destMapGetter.GetILGenerator();
        destIl.Emit(OpCodes.Ldnull); destIl.Emit(OpCodes.Ret);
        vmfDriver.DefineProperty("DestMap", PropertyAttributes.None, typeof(Map), Type.EmptyTypes)
            .SetGetMethod(destMapGetter);
        vmfDriver.CreateType();
        var vmfVehicle = module.DefineType("VehicleMapFramework.VehiclePawnWithMap", TypeAttributes.Public);
        vmfVehicle.DefineField("interiorMap", typeof(Map), FieldAttributes.Private);
        vmfVehicle.CreateType();
        check(AccessTools.TypeByName("VehicleMapFramework.GenUIOnVehicle") != null,
            "late VMF targeting fixture resolves by type name");
        check(AccessTools.Method(AccessTools.TypeByName("VehicleMapFramework.GenUIOnVehicle"),
            "TargetsAtMouse", new[] { typeof(TargetingParameters), typeof(bool), typeof(ITargetingSource) }) == target,
            "VMF alternate Targeter signature resolves");
        var adapter = assembly.GetType("RimExodus.SeamlessVMFTargetingCompat", true)!;
        adapter.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { harmony });
        var callback = adapter.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public)!;
        check(Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == callback) == true,
            "VMF alternate Targeter entry binds when its API is present");
        harmony.Unpatch(target, callback);

        var diagnostics = assembly.GetType("RimExodus.SeamlessVMFDisembarkDiagnostics", true)!;
        diagnostics.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { harmony });
        var genSpawn = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
            new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4),
                typeof(WipeMode), typeof(bool), typeof(bool) })!;
        var recovery = AccessTools.Method(typeof(Verse.AI.Pawn_PathFollower),
            nameof(Verse.AI.Pawn_PathFollower.TryRecoverFromUnwalkablePosition), new[] { typeof(bool) })!;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var before = diagnostics.GetMethod("SpawnPrefix", flags)!;
        var after = diagnostics.GetMethod("SpawnPostfix", flags)!;
        var recover = diagnostics.GetMethod("RecoveryPrefix", flags)!;
        check(Harmony.GetPatchInfo(genSpawn)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == before) == true
            && Harmony.GetPatchInfo(genSpawn)?.Postfixes.Any(p => p.owner == harmony.Id && p.PatchMethod == after) == true
            && Harmony.GetPatchInfo(recovery)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == recover) == true,
            "VMF disembark diagnostic hooks bind when its API is present");
        harmony.Unpatch(genSpawn, before);
        harmony.Unpatch(genSpawn, after);
        harmony.Unpatch(recovery, recover);

        var transit = assembly.GetType("RimExodus.SeamlessVMFTransitCompat", true)!;
        transit.GetMethod("Register", flags)!.Invoke(null, null);
        check(transit.GetField("vehicleWithMapType", flags)!.GetValue(null) != null
            && transit.GetField("acrossMapsDriverType", flags)!.GetValue(null) != null
            && transit.GetField("interiorMapField", flags)!.GetValue(null) != null
            && transit.GetField("destMapProperty", flags)!.GetValue(null) != null,
            "VMF vehicle-transfer guard resolves late-loaded VMF members");
    }

    private static void CheckCastGateComposition(bool shared, Action<bool, string> check)
    {
        var owner = shared ? typeof(RimExodus.Maps.MapModule).Assembly : typeof(RimExodus.RimExodusMod).Assembly;
        var name = shared ? "RimExodus.Maps.Patch_Verb_LaunchProjectile_TryCastShot"
            : "RimExodus.Patch_Verb_LaunchProjectile_TryCastShot";
        var transpiler = owner.GetType(name, true)!.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static)!;
        var original = AccessTools.Method(typeof(Verb_LaunchProjectile), "TryCastShot", Type.EmptyTypes)!;
        var getThing = AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Thing));
        var getMap = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("VMF.CastGateFixture"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("fixture");
        var type = module.DefineType("VehicleMapFramework.VehicleMapUtility", TypeAttributes.Public);
        var baseMap = type.DefineMethod("BaseMap", MethodAttributes.Public | MethodAttributes.Static,
            typeof(Map), new[] { typeof(Thing) });
        var il = baseMap.GetILGenerator();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ret);
        var vmfMap = type.CreateType()!.GetMethod("BaseMap")!;
        List<CodeInstruction> Original() => PatchProcessor.GetOriginalInstructions(original);
        List<CodeInstruction> Apply(List<CodeInstruction> codes)
            => ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { codes })!).ToList();
        int TargetRead(List<CodeInstruction> codes)
        {
            for (int i = 0; i + 1 < codes.Count; i++)
                if (codes[i].Calls(getThing)
                    && (codes[i + 1].Calls(getMap) || codes[i + 1].Calls(vmfMap))) return i;
            return -1;
        }
        void FoldToVmf(List<CodeInstruction> codes)
        {
            int at = TargetRead(codes);
            check(at >= 0, "original target-map read found");
            codes[at + 1].opcode = OpCodes.Call;
            codes[at + 1].operand = vmfMap;
        }
        void CheckGate(List<CodeInstruction> codes, bool vmfFirst)
        {
            int at = TargetRead(codes);
            check(at >= 0, "VMF target-map read retained");
            check(codes[at + 1].Calls(vmfMap), "VMF BaseMap retained");
            check(codes[at + 2].opcode == OpCodes.Ldarg_0
                && codes[at + 3].operand is MethodInfo gate
                && gate.Name == "TargetMapForCastGate"
                && gate.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Map), typeof(Verb) }),
                "cast gate receives VMF-normalized map " + (vmfFirst ? "after VMF" : "before VMF"));
        }
        var afterVmf = Original(); FoldToVmf(afterVmf); CheckGate(Apply(afterVmf), true);
        var beforeVmf = Apply(Original()); FoldToVmf(beforeVmf); CheckGate(beforeVmf, false);
    }

    private static MethodInfo LoadPreviewFixture(bool drift)
    {
        // Fixtures test loader timing and failure handling, not third-party version compatibility.
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MapPreview.RegressionFixture"), AssemblyBuilderAccess.Run).DefineDynamicModule("fixture");
        var request = module.DefineType("MapPreview.MapPreviewRequest", TypeAttributes.Public).CreateType()!;
        var result = module.DefineType("MapPreview.MapPreviewResult", TypeAttributes.Public).CreateType()!;
        void Property(TypeBuilder type, string name, MethodInfo getter)
        {
            var returnType = getter == null ? typeof(string) : typeof(bool);
            var property = type.DefineProperty(name, PropertyAttributes.None, returnType, Type.EmptyTypes);
            var method = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, returnType, Type.EmptyTypes);
            var il = method.GetILGenerator();
            if (getter == null) il.Emit(OpCodes.Ldstr, "changed signature"); else il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Ret); property.SetGetMethod(method);
        }
        var api = module.DefineType("MapPreview.MapPreviewAPI", TypeAttributes.Public);
        Property(api, "IsGeneratingPreview", drift ? null : typeof(Audit).GetMethod(nameof(ReadGlobal)));
        api.CreateType();
        var generator = module.DefineType("MapPreview.MapPreviewGenerator", TypeAttributes.Public);
        Property(generator, "IsGeneratingOnCurrentThread", typeof(Audit).GetMethod(nameof(ReadThread)));
        var generate = generator.DefineMethod("GeneratePreview", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { request, result });
        generate.GetILGenerator().Emit(OpCodes.Ret);
        return generator.CreateType()!.GetMethod("GeneratePreview")!;
    }
}
