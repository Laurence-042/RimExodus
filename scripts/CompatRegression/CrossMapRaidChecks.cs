#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus;
using RimWorld;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

internal sealed class CrossMapRaidChecks : IDisposable
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
    private static CrossMapRaidChecks? active;

    private readonly Assembly assembly;
    private readonly Action<bool, string> check;
    private readonly Harmony engine = new("RE.Regression.CrossMapRaidEngine");
    private readonly Game previousGame = Current.Game;
    private readonly JobDef previousGoto = JobDefOf.Goto;
    private readonly JobDef previousMelee = JobDefOf.AttackMelee;
    private readonly DutyDef previousAssault = DutyDefOf.AssaultColony;
    private readonly DutyDef previousHunt = DutyDefOf.HuntEnemiesIndividual;
    private readonly DutyDef previousExit = DutyDefOf.ExitMapBest;
    private readonly LoadedLanguage previousLanguage = LanguageDatabase.activeLanguage;
    private readonly LoadedLanguage previousDefaultLanguage = LanguageDatabase.defaultLanguage;
    private readonly object? previousPrefs = AccessTools.Field(typeof(Prefs), "data").GetValue(null);
    private string? modsConfig;
    private readonly NativeArray<int> tileOffsets = new(3, Allocator.Persistent);
    private readonly List<PathGrid> pathGrids = new();
    private readonly Dictionary<MapPawns, List<Pawn>> spawned = new();
    private readonly Dictionary<Reachability, Map> reachMaps = new();
    private readonly HashSet<Pawn_JobTracker> trackers = new();
    private readonly List<Map> maps = new();
    private readonly List<(Pawn pawn, Map map, IntVec3 cell)> placements = new();
    private readonly HashSet<Pawn_PathFollower> nativeSteps = new();
    private readonly MethodInfo enterNextCell = AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell");
    private readonly Faction raiders = new() { Name = "Fixture raiders", def = new FactionDef { autoFlee = false, pawnsPlural = "raiders" } };
    private readonly Faction colony = new() { Name = "Fixture colony", def = new FactionDef { isPlayer = true } };
    private readonly Faction allies = new() { Name = "Fixture allies", def = new FactionDef { autoFlee = false, pawnsPlural = "allies" } };
    private readonly TerrainDef dry = new() { defName = "CrossMapFixtureDryGround" };
    private readonly ThingDef pawnDef = new() { defName = "CrossMapFixturePawn", race = new RaceProperties { intelligence = Intelligence.Humanlike } };
    private readonly ThingDef spotDef;
    private readonly bool addedSpotDef;
    private readonly PlanetLayerDef layerDef = new() { defName = "CrossMapRaidFixtureLayer" };
    private readonly Map home;
    private readonly Map host;
    private readonly Thing hostExit;
    private readonly Thing farExit;
    private Map? blockedMap;
    private int id;

    internal static void Run(Assembly assembly, bool shared, Action<bool, string> check)
    {
        using var fixture = new CrossMapRaidChecks(assembly, shared, check);
        fixture.DirectRaid();
        fixture.Pursuit();
        Console.WriteLine($"Cross-map raid and pursuit fixtures passed ({(shared ? "REF" : "legacy")}).");
    }

    private CrossMapRaidChecks(Assembly assembly, bool shared, Action<bool, string> check)
    {
        this.assembly = assembly;
        this.check = check;
        var existingSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
        spotDef = existingSpotDef ?? new ThingDef
        {
            defName = "RimExodus_SeamlessEnterSpot",
            thingClass = typeof(ThingWithComps),
            comps = new List<CompProperties> { new CompProperties_SeamlessTileEnterSpot() }
        };
        try
        {
            if (existingSpotDef == null)
            {
                DefDatabase<ThingDef>.Add(spotDef);
                addedSpotDef = true;
            }
            JobDefOf.Goto = new JobDef { defName = "FixtureGoto" };
            JobDefOf.AttackMelee = new JobDef { defName = "FixtureAttackMelee" };
            DutyDefOf.AssaultColony = new DutyDef
            {
                defName = "FixtureAssaultDuty", index = 0, thinkNode = new JobGiver_AIGotoNearestHostile()
            };
            DutyDefOf.HuntEnemiesIndividual = new DutyDef { defName = "FixtureHuntDuty" };
            DutyDefOf.ExitMapBest = new DutyDef { defName = "FixtureExitDuty" };
            var language = new LoadedLanguage("English");
            Set(language, "workerInt", new LanguageWorker_English());
            Set(language, "dataIsLoaded", true);
            LanguageDatabase.activeLanguage = LanguageDatabase.defaultLanguage = language;
            AccessTools.Field(typeof(Prefs), "data").SetValue(null, new PrefsData());
            modsConfig = Path.GetTempFileName();
            File.WriteAllText(modsConfig, "<ModsConfigData />");

            var game = Allocate<Game>();
            game.signalManager = new SignalManager();
            game.uniqueIDsManager = new UniqueIDsManager();
            game.tickManager = new TickManager();
            game.components = new List<GameComponent>();
            Set(game, "maps", maps);
            var world = Allocate<World>();
            world.grid = Allocate<WorldGrid>();
            world.worldObjects = new WorldObjectsHolder();
            world.factionManager = new FactionManager();
            Set(world.factionManager, "ofPlayer", colony);
            world.factionManager.AllFactionsListForReading.AddRange(new[] { colony, raiders, allies });
            Relations(raiders).Add(new FactionRelation { other = colony, kind = FactionRelationKind.Hostile });
            Relations(raiders).Add(new FactionRelation { other = allies, kind = FactionRelationKind.Hostile });
            Relations(colony).Add(new FactionRelation { other = raiders, kind = FactionRelationKind.Hostile });
            Relations(colony).Add(new FactionRelation { other = allies, kind = FactionRelationKind.Ally });
            Relations(allies).Add(new FactionRelation { other = raiders, kind = FactionRelationKind.Hostile });
            Relations(allies).Add(new FactionRelation { other = colony, kind = FactionRelationKind.Ally });
            Set(game, "worldInt", world);
            Current.Game = game;

            var root = Allocate<SurfaceLayer>();
            Set<PlanetLayer>(root, "isRootSurface", true);
            var layer = Allocate<SurfaceLayer>();
            Set<PlanetLayer>(layer, "def", layerDef);
            Set<PlanetLayer>(layer, "layerId", 9);
            Set<PlanetLayer>(layer, "tileIDToNeighbors_offsets", tileOffsets);
            Set<PlanetLayer>(layer, "tiles", Enumerable.Range(0, 3)
                .Select(i => new Tile(new PlanetTile(i, layer)) { PrimaryBiome = new BiomeDef { canExitMap = true } })
                .ToList());
            Set(world.grid, "surface", root);
            Set(world.grid, "planetLayers", new Dictionary<int, PlanetLayer> { [0] = root, [9] = layer });
            check(SeamlessMapProviders.Register(layerDef, new RaidLayer()), "direct raid fixture registers a three-tile layer");

            game.components.Add((GameComponent)Activator.CreateInstance(Type("SeamlessRaidContinuity"), game)!);
            home = MakeMap(new PlanetTile(0, layer));
            host = MakeMap(new PlanetTile(1, layer));
            home.GetComponent<SeamlessTileManager>().SetNeighbor(1, host.Parent, new IntVec3(24, 0, 0));
            host.GetComponent<SeamlessTileManager>().SetNeighbor(0, home.Parent, new IntVec3(-24, 0, 0));
            AddSpot(home, new IntVec3(27, 0, 16), 1);
            hostExit = AddSpot(host, new IntVec3(4, 0, 16), 0);
            farExit = AddSpot(host, new IntVec3(27, 0, 16), 2);
            active = this;
            InstallEngineAdapters();
            if (shared)
            {
                game.components.Add(new RimExodus.Maps.MapRuntime(game));
                game.components.Add((GameComponent)Activator.CreateInstance(Type("SeamlessFrameworkState"), game)!);
                // Audit.Run has already bound the REF hooks under its own Harmony ID.
                var installed = AccessTools.Field(typeof(RimExodus.Maps.MapCombatModule), "installed");
                var previousInstalled = installed.GetValue(null);
                installed.SetValue(null, true);
                try
                {
                    foreach (var map in maps) Static("SeamlessFramework", "RegisterMap", map);
                }
                finally { installed.SetValue(null, previousInstalled); }
            }
            check(SeamlessCombatCoords.TryGetCombatLink(host, home, out var link)
                && link.offset == new IntVec3(-24, 0, 0), "raid host has the real combat link back to the attacked map");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void DirectRaid()
    {
        var arrivalDef = new PawnsArrivalModeDef
        {
            defName = "FixtureEdgeWalkIn", workerClass = typeof(PawnsArrivalModeWorker_EdgeWalkIn), walkIn = true
        };
        var strategy = new RaidStrategyDef { defName = "FixtureAssault", workerClass = typeof(RaidStrategyWorker_ImmediateAttack) };
        var native = new IntVec3(26, 0, 16);
        var parms = new IncidentParms
        {
            target = home, faction = raiders, raidArrivalMode = arrivalDef, raidStrategy = strategy,
            spawnCenter = native, canTimeoutOrFlee = false, questTag = "cross-map-fixture", inSignalEnd = "fixture-leave"
        };
        BeginRaid(parms);
        var outer = Type("SeamlessRaidOuterSpawn");
        var plan = outer.GetField("Current", Statics)!.GetValue(null);
        check(plan != null, "the enemy incident opens an actual outer-entry plan");
        check((bool)Static("SeamlessRaidDirectSpawn", "TryResolveSpawnCenter", arrivalDef.Worker, parms)!
            && AccessTools.Field(plan!.GetType(), "CrossHostMap").GetValue(plan) == host
            && parms.target == home && parms.spawnCenter.x > host.Center.x,
            "direct raid resolves the native anchor to the far side of the actual neighboring host");

        var group = new List<Pawn> { NewPawn(raiders), NewPawn(raiders) };
        Static("SeamlessRaidDirectSpawn", "Arrive", arrivalDef.Worker, group, parms);
        check(parms.target == home && placements.Count == group.Count
            && placements.All(p => p.map == host && p.cell == parms.spawnCenter),
            "direct raid invokes the real arrival worker on the host only and restores the incident target");
        strategy.Worker.MakeLords(parms, group);
        var authority = group[0].GetLord();
        check(parms.target == home && authority.Map == host && group[1].GetLord() == authority
            && authority.CurLordToil is LordToil_AssaultColony
            && authority.inSignalLeave == "fixture-leave" && authority.questTags.Contains("cross-map-fixture"),
            "direct raid creates one original native assault Lord on the host with its quest state");

        var homeTarget = new FixtureAttackTarget { def = new ThingDef { defName = "FixtureHomeTarget" } };
        Set<Thing>(homeTarget, "factionInt", colony);
        SetLocation(homeTarget, home, new IntVec3(15, 0, 16));
        home.attackTargetsCache.Notify_ThingSpawned(homeTarget);
        var pawn = group[0];
        var duty = new ThinkNode_Duty();
        ((List<ThinkNode>)AccessTools.Field(typeof(ThinkNode), "subNodes").GetValue(duty)!).Add(DutyDefOf.AssaultColony.thinkNode);
        var launchJob = duty.TryIssueJobPackage(pawn, default).Job;
        check(launchJob?.targetA.Thing == homeTarget && launchJob.def == JobDefOf.Goto && launchJob.lord == authority,
            "the active backend's assault JobGiver chooses a real enemy target on the attacked map");
        QueueBridge(pawn, launchJob!);
        check(!Step(pawn, hostExit.Position) && pawn.Map == home
            && pawn.Position == hostExit.TryGetComp<CompSeamlessTileEnterSpot>().cachedArrivalCell,
            "a directly spawned raider crosses the selected seam into the attacked map");
        check(pawn.GetLord().Map == home && group[1].GetLord() == authority
            && pawn.mindState.duty.def == DutyDefOf.AssaultColony
            && pawn.CurJob.targetA.Thing == homeTarget && authority.numPawnsLostViolently == 0,
            "arrival continues the selected assault job, restores local duty and counts no casualty");

        // The same real planner must keep the entire second raid local if native reachability rejects its host.
        parms.spawnCenter = native;
        blockedMap = host;
        BeginRaid(parms);
        check((bool)Static("SeamlessRaidDirectSpawn", "TryResolveSpawnCenter", arrivalDef.Worker, parms)!
            && parms.spawnCenter == native, "a host with no route to the attacked seam falls back to the native anchor");
        var fallback = new List<Pawn> { NewPawn(raiders) };
        Static("SeamlessRaidDirectSpawn", "Arrive", arrivalDef.Worker, fallback, parms);
        check(fallback[0].Map == home && parms.target == home, "the rejected host receives no fallback raider");
        blockedMap = null;
        Static("SeamlessRaidOuterSpawn", "End");
        home.attackTargetsCache.Notify_ThingDespawned(homeTarget);
    }

    private void Pursuit()
    {
        var target = NewPawn(raiders);
        Place(target, host, new IntVec3(15, 0, 15));
        var hunters = Enumerable.Range(0, 5).Select(_ => NewPawn(allies)).ToArray();
        foreach (var pawn in hunters) Place(pawn, host, new IntVec3(15, 0, 15));
        var lord = LordMaker.MakeNewLord(allies, new LordJob_AssistColony(allies, host.Center), host, hunters);
        check(hunters.All(pawn => pawn.mindState.duty.def == DutyDefOf.HuntEnemiesIndividual),
            "the allied Lord assigns its native hunt duty before a hostile flees to the next map");

        var pursuer = hunters[0];
        var original = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
        original.lord = lord;
        original.startTick = GenTicks.TicksGame;
        original.expiryInterval = 400;
        original.canBashDoors = true;
        pursuer.jobs.curJob = original;
        Despawn(target);
        Place(target, home, new IntVec3(15, 0, 15));
        QueueBridge(pursuer, original);
        check(pursuer.GetLord() == lord && ReferenceEquals(
            AccessTools.Field(ReadBridge(pursuer)!.GetType(), "PursuitTarget").GetValue(ReadBridge(pursuer)), target),
            $"the bridge binds the allied Lord and its live pursuit target (forced={original.playerForced}, exit={original.exitMapOnArrival})");
        check(pursuer.CurJob.canBashDoors, "the pursuit bridge inherits the actual attack job's door permission");
        check(Step(pursuer, farExit.Position) && pursuer.Map == host, "a pursuit cannot use a different seam than its job selected");
        Set(Find.TickManager, "ticksGameInt", GenTicks.TicksGame + 50);
        check(!Step(pursuer, hostExit.Position) && pursuer.Map == home && !HasBridge(pursuer),
            "a live melee pursuit crosses its own seam and consumes its bridge");
        var localLord = pursuer.GetLord();
        check(localLord?.Map == home && pursuer.mindState.duty?.def == DutyDefOf.HuntEnemiesIndividual
            && pursuer.mindState.duty.focusSecond.Cell == LocalRallyCell(home),
            $"the allied pursuer receives its native hunt duty from a local Lord (lord={localLord?.LordJob?.GetType().Name}, phase={localLord?.CurLordToil?.GetType().Name}, duty={pursuer.mindState.duty?.def?.defName})");
        var resumed = pursuer.CurJob;
        check(resumed?.def == JobDefOf.AttackMelee && resumed.targetA.Thing == target
            && resumed.expiryInterval == 350 && resumed.lord == localLord,
            $"arrival resumes the allied melee job for the same target and deadline under the local Lord (job={resumed?.def?.defName})");

        var lost = hunters[1];
        QueueBridge(lost, JobMaker.MakeJob(JobDefOf.AttackMelee, target));
        Despawn(target);
        check(Step(lost, hostExit.Position) && lost.Map == host && lost.GetLord() == lord && !HasBridge(lost),
            "a despawned target cancels its in-flight pursuit before the pawn leaves the source squad");
        Place(target, home, new IntVec3(15, 0, 15));

        var immediate = hunters[2];
        immediate.jobs.curJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
        check(!HasBridge(immediate) && !Step(immediate, hostExit.Position)
            && immediate.Map == home && immediate.CurJob.targetA.Thing == target,
            "an existing melee job acquires its own pursuit when it steps into the target's seam");

        var marked = hunters[4];
        var fleeing = NewPawn(raiders);
        Place(fleeing, host, host.Center);
        marked.jobs.curJob = JobMaker.MakeJob(JobDefOf.AttackMelee, fleeing);
        fleeing.jobs.curJob = JobMaker.MakeJob(JobDefOf.Goto, hostExit.Position);
        fleeing.CurJob.exitMapOnArrival = true;
        check(!Step(fleeing, hostExit.Position) && fleeing.Map == home && HasBridge(marked)
            && marked.CurJob.def == JobDefOf.Goto && marked.CurJob.targetA.Cell == hostExit.Position,
            "a fleeing pawn's actual seam transfer queues its current melee pursuer");
        check(!Step(marked, hostExit.Position) && marked.Map == home && marked.GetLord() == localLord
            && marked.CurJob.targetA.Thing == fleeing,
            "the pursuer marked at departure rejoins the same allied detachment and resumes melee");

        var interrupted = hunters[3];
        QueueBridge(interrupted, JobMaker.MakeJob(JobDefOf.AttackMelee, target));
        lord.GotoToil(lord.Graph.lordToils.First(toil => toil is LordToil_ExitMap));
        check(Step(interrupted, hostExit.Position) && interrupted.Map == host
            && interrupted.GetLord() == lord && !HasBridge(interrupted)
            && pursuer.mindState.duty?.def == DutyDefOf.ExitMapBest && localLord?.CurLordToil is LordToil_ExitMap,
            "withdrawal invalidates an in-flight pursuit and keeps the pawn with its current team phase");
    }

    private void QueueBridge(Pawn pawn, Job job)
    {
        pawn.jobs.curJob = job;
        pawn.pather.StartPath(job.targetA, PathEndMode.Touch);
        check(pawn.CurJob.def == JobDefOf.Goto && pawn.CurJob.targetA.Cell == hostExit.Position
            && HasBridge(pawn), "a real cross-map job is bridged to a source-map movement driver");
    }

    private bool HasBridge(Pawn pawn) => ReadBridge(pawn) != null;

    private object? ReadBridge(Pawn pawn)
    {
        object?[] args = { pawn, null };
        return (bool)Type("SeamlessTransferGrants").GetMethod("TryGet", Statics)!.Invoke(null, args)! ? args[1] : null;
    }

    private bool Step(Pawn pawn, IntVec3 next)
    {
        pawn.pather.nextCell = next;
        enterNextCell.Invoke(pawn.pather, null);
        return nativeSteps.Remove(pawn.pather);
    }

    private void BeginRaid(IncidentParms parms) =>
        Static("SeamlessRaidOuterSpawn", "BeginIncident", parms, true, "CrossMapRaidFixture", typeof(IncidentWorker_RaidEnemy));

    private Map MakeMap(PlanetTile tile)
    {
        var parent = Allocate<MapParent>();
        Set<WorldObject>(parent, "tile", tile);
        var parents = (List<MapParent>)AccessTools.Field(typeof(WorldObjectsHolder), "mapParents")
            .GetValue(Find.WorldObjects)!;
        parents.Add(parent);
        var map = Allocate<Map>();
        map.info = new MapInfo { parent = parent, Size = new IntVec3(32, 1, 32) };
        map.uniqueID = maps.Count + 71;
        map.events = new MapEvents(map);
        map.lordManager = new LordManager(map);
        map.deferredSpawner = new DeferredSpawner(map);
        map.attackTargetsCache = new AttackTargetsCache(map);
        map.cellIndices = new CellIndices(map);
        map.thingGrid = new ThingGrid(map);
        map.terrainGrid = new TerrainGrid(map);
        Set(map.terrainGrid, "topGrid", Enumerable.Repeat(dry, map.cellIndices.NumGridCells).ToArray());
        map.roofGrid = new RoofGrid(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.reachability = new Reachability(map);
        reachMaps.Add(map.reachability, map);
        map.mapPawns = new MapPawns(map);
        spawned.Add(map.mapPawns, new List<Pawn>());
        map.pathing = new Pathing(map);
        var grid = new PathGrid(map, new PathGridDef());
        pathGrids.Add(grid);
        var context = new PathingContext(map, grid);
        Set(map.pathing, "normal", context);
        Set(map.pathing, "fenceBlocked", context);
        var manager = new SeamlessTileManager(map);
        Set(manager, "ProviderMapPrepared", true);
        map.components = new List<MapComponent> { manager };
        maps.Add(map);
        return map;
    }

    private Pawn NewPawn(Faction faction)
    {
        var pawn = Allocate<Pawn>();
        pawn.def = pawnDef;
        pawn.kindDef = new PawnKindDef { race = pawnDef, combatPower = 100f };
        pawn.thingIDNumber = ++id;
        pawn.health = Allocate<Pawn_HealthTracker>();
        Set(pawn.health, "healthState", PawnHealthState.Mobile);
        pawn.mindState = new Pawn_MindState(pawn);
        pawn.jobs = new Pawn_JobTracker(pawn);
        pawn.pather = new Pawn_PathFollower(pawn);
        Set<Thing>(pawn, "factionInt", faction);
        Set<Thing>(pawn, "mapIndexOrState", (sbyte)-1);
        trackers.Add(pawn.jobs);
        return pawn;
    }

    private Thing AddSpot(Map map, IntVec3 cell, int target)
    {
        var spot = new ThingWithComps { def = spotDef, thingIDNumber = ++id };
        spot.InitializeComps();
        SetLocation(spot, map, cell);
        map.thingGrid.Register(spot);
        var lists = (Dictionary<ThingDef, List<Thing>>)AccessTools.Field(typeof(ListerThings), "listsByDef")
            .GetValue(map.listerThings)!;
        if (!lists.TryGetValue(spotDef, out var spots))
            lists.Add(spotDef, spots = new List<Thing>());
        spots.Add(spot);
        var comp = spot.TryGetComp<CompSeamlessTileEnterSpot>();
        comp.targetWorldTile = target;
        comp.ComputeAndCacheArrival(map);
        return spot;
    }

    private void SetLocation(Thing thing, Map map, IntVec3 cell)
    {
        Set<Thing>(thing, "positionInt", cell);
        Set<Thing>(thing, "mapIndexOrState", (sbyte)maps.IndexOf(map));
    }

    private void Place(Pawn pawn, Map map, IntVec3 cell)
    {
        SetLocation(pawn, map, cell);
        spawned[map.mapPawns].Add(pawn);
    }

    private void Despawn(Pawn pawn)
    {
        spawned[pawn.Map.mapPawns].Remove(pawn);
        Set<Thing>(pawn, "mapIndexOrState", (sbyte)-1);
        pawn.jobs.curJob = null;
    }

    // The production incident, job giver, bridge, seam trigger and Lord transitions all run.
    // These adapters supply the engine's map mutation, region and UI boundaries in the headless process.
    private void InstallEngineAdapters()
    {
        void Patch(MethodBase method, string prefix, int priority = Priority.Last) =>
            engine.Patch(method, new HarmonyMethod(typeof(CrossMapRaidChecks), prefix) { priority = priority });

        Patch(AccessTools.PropertyGetter(typeof(GenFilePaths), nameof(GenFilePaths.ModsConfigFilePath)), nameof(FixtureModsConfig));
        Patch(AccessTools.Method(typeof(ModLister), nameof(ModLister.RebuildModList)), nameof(SkipModScan));
        Patch(AccessTools.Method(typeof(Pawn), "TicksPerMove"), nameof(FixtureMoveTicks), Priority.First);
        Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.TryGetAttackVerb)), nameof(NoRangedVerb), Priority.First);
        Patch(AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomSpotJustOutsideColony),
            new[] { typeof(Pawn), typeof(IntVec3).MakeByRefType() }), nameof(FixtureRallyCell), Priority.First);
        Patch(AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath)), nameof(SkipNativePath));
        Patch(enterNextCell, nameof(FixtureEnterCell));
        Patch(AccessTools.Method(typeof(Reachability), nameof(Reachability.CanReach),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) }), nameof(FixtureReachability), Priority.First);
        Patch(AccessTools.Method(typeof(CellFinder), nameof(CellFinder.RandomClosewalkCellNear),
            new[] { typeof(IntVec3), typeof(Map), typeof(int), typeof(Predicate<IntVec3>) }), nameof(FixtureArrivalCell));
        Patch(AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
            new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) }), nameof(FixtureSpawn), Priority.First);
        Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.DeSpawn), new[] { typeof(DestroyMode) }), nameof(FixtureDespawn), Priority.First);
        Patch(AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawns)), nameof(FixturePawns));
        Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.Selector)), nameof(NoSelector));
        Patch(typeof(Pawn_JobTracker).GetMethods().Single(method => method.Name == nameof(Pawn_JobTracker.StartJob)),
            nameof(FixtureStartJob));
    }

    private static bool FixtureReachability(Reachability __instance, IntVec3 start, LocalTargetInfo dest, ref bool __result)
    {
        if (!active!.reachMaps.TryGetValue(__instance, out var map)) return true;
        __result = map != active.blockedMap && start.InBounds(map) && dest.Cell.InBounds(map)
            && (!dest.HasThing || dest.Thing.Map == map) && map.pathing.Normal.pathGrid.Walkable(dest.Cell);
        return false;
    }

    private static bool FixtureArrivalCell(IntVec3 root, ref IntVec3 __result)
    {
        __result = root;
        return false;
    }

    private static bool FixtureSpawn(Thing newThing, IntVec3 loc, Map map, ref Thing __result)
    {
        if (!(newThing is Pawn pawn) || !active!.trackers.Contains(pawn.jobs)) return true;
        active.Place(pawn, map, loc);
        active.placements.Add((pawn, map, loc));
        __result = pawn;
        return false;
    }

    private static bool FixtureDespawn(Pawn __instance)
    {
        if (!active!.trackers.Contains(__instance.jobs)) return true;
        active.Despawn(__instance);
        return false;
    }

    private static bool FixturePawns(MapPawns __instance, ref List<Pawn> __result)
    {
        if (!active!.spawned.TryGetValue(__instance, out var pawns)) return true;
        __result = pawns;
        return false;
    }

    private static bool NoSelector(ref Selector? __result)
    {
        __result = null;
        return false;
    }

    private static bool FixtureModsConfig(ref string? __result)
    {
        __result = active!.modsConfig;
        return false;
    }

    private static bool SkipModScan() => false;

    private static IntVec3 LocalRallyCell(Map map) => map.Center + new IntVec3(2, 0, 3);

    private static bool FixtureRallyCell(Pawn searcher, ref IntVec3 result, ref bool __result)
    {
        if (!active!.trackers.Contains(searcher.jobs)) return true;
        result = LocalRallyCell(searcher.Map);
        __result = true;
        return false;
    }

    private static bool NoRangedVerb(Pawn __instance, ref Verb? __result)
    {
        if (!active!.trackers.Contains(__instance.jobs)) return true;
        __result = null;
        return false;
    }

    private static bool FixtureMoveTicks(Pawn __instance, bool diagonal, ref float __result)
    {
        if (!active!.trackers.Contains(__instance.jobs)) return true;
        __result = diagonal ? 18f : 13f;
        return false;
    }

    private static bool SkipNativePath(Pawn ___pawn) => !active!.trackers.Contains(___pawn.jobs);

    private static bool FixtureEnterCell(Pawn_PathFollower __instance, Pawn ___pawn)
    {
        if (!active!.trackers.Contains(___pawn.jobs)) return true;
        active.nativeSteps.Add(__instance);
        return false;
    }

    private static bool FixtureStartJob(Pawn_JobTracker __instance, Job newJob)
    {
        if (!active!.trackers.Contains(__instance)) return true;
        __instance.curJob = newJob;
        return false;
    }

    private Type Type(string name) => assembly.GetType("RimExodus." + name, throwOnError: true)!;

    private object? Static(string type, string method, params object?[] args) =>
        Type(type).GetMethod(method, Statics)!.Invoke(null, args);

    private static List<FactionRelation> Relations(Faction faction) =>
        (List<FactionRelation>)AccessTools.Field(typeof(Faction), "relations").GetValue(faction)!;

    private static T Allocate<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set<T>(T instance, string field, object value) =>
        AccessTools.Field(typeof(T), field).SetValue(instance, value);

    public void Dispose()
    {
        engine.UnpatchAll(engine.Id);
        Static("SeamlessRaidOuterSpawn", "End");
        Static("SeamlessTransferGrants", "Reset");
        foreach (var map in maps) Static("SeamlessBridgeCluster", "NotifyMapRemoved", map);
        foreach (var grid in pathGrids) grid.Dispose();
        tileOffsets.Dispose();
        var providers = (Dictionary<PlanetLayerDef, SeamlessMapProvider>)AccessTools.Field(
            typeof(SeamlessMapProviders), "Providers").GetValue(null)!;
        providers.Remove(layerDef);
        if (addedSpotDef)
        {
            AccessTools.Method(typeof(DefDatabase<ThingDef>), "Remove").Invoke(null, new object[] { spotDef });
            AccessTools.Field(Type("SeamlessEdgeCells"), "_cachedEnterSpotDef").SetValue(null, null);
            AccessTools.Field(Type("SeamlessTransferGrants"), "_enterSpotDef").SetValue(null, null);
        }
        if (modsConfig != null) File.Delete(modsConfig);
        JobDefOf.Goto = previousGoto;
        JobDefOf.AttackMelee = previousMelee;
        DutyDefOf.AssaultColony = previousAssault;
        DutyDefOf.HuntEnemiesIndividual = previousHunt;
        DutyDefOf.ExitMapBest = previousExit;
        LanguageDatabase.activeLanguage = previousLanguage;
        LanguageDatabase.defaultLanguage = previousDefaultLanguage;
        AccessTools.Field(typeof(Prefs), "data").SetValue(null, previousPrefs);
        Current.Game = previousGame;
        active = null;
    }

    private sealed class FixtureAttackTarget : Thing, IAttackTarget
    {
        public Thing Thing => this;
        public LocalTargetInfo TargetCurrentlyAimingAt => LocalTargetInfo.Invalid;
        public float TargetPriorityFactor => 1f;
        public bool ThreatDisabled(IAttackTargetSearcher disabledFor) => false;
    }

    private sealed class RaidLayer : SeamlessMapProvider
    {
        public override bool Accepts(PlanetTile tile, MapParent parent) => true;
        public override WorldObjectDef GetMapParentDef(PlanetTile tile) => null!;
        public override IReadOnlyList<Vector2> GetFootprint(PlanetTile tile, int mapSize) =>
            new[] { new Vector2(4, 4), new Vector2(28, 4), new Vector2(28, 28), new Vector2(4, 28) };
        public override SeamlessMapEdge GetEdge(PlanetTile tile, int edge, PlanetTile nativeNeighbor) => edge switch
        {
            1 when tile.tileId < 2 => new SeamlessMapEdge(new PlanetTile(tile.tileId + 1, tile.Layer), new IntVec3(24, 0, 0)),
            3 when tile.tileId > 0 => new SeamlessMapEdge(new PlanetTile(tile.tileId - 1, tile.Layer), new IntVec3(-24, 0, 0)),
            _ => default
        };
    }
}
