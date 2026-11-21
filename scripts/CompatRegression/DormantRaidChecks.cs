using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus;
using RimWorld;
using RimWorld.Planet;
using Verse;

// Exercise the actual timer and tick-registration methods, without a running map/Unity host.
internal static class DormantRaidChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var assembly = typeof(RimExodusMod).Assembly;
        var manager = typeof(SeamlessDormancyManager);
        var asleep = (HashSet<Map>)manager.GetField("dormantMaps", flags)!.GetValue(null)!;
        var observers = (IDictionary)manager.GetField("sleepObservers", flags)!.GetValue(null)!;
        var outer = assembly.GetType("RimExodus.SeamlessRaidOuterSpawn", true)!;
        int checks = 0;
        void Check(bool condition, string label)
        {
            checks++;
            if (!condition) throw new Exception(label);
        }

        var previousGame = Current.Game;
        var harmony = new Harmony("RE.Regression.DormantRaids");
        try
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            var maps = new List<Map>();
            AccessTools.Field(typeof(Game), "maps").SetValue(game, maps);
            game.tickManager = new TickManager();
            Current.Game = game;
            var parent = (MapParent_SeamlessTile)RuntimeHelpers.GetUninitializedObject(typeof(MapParent_SeamlessTile));
            parent.worldTile = 123;
            AccessTools.Field(typeof(WorldObject), "tile").SetValue(parent, new PlanetTile(123, -1));
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.info = new MapInfo { parent = parent };
            maps.Add(map);
            var pawn = (Pawn)RuntimeHelpers.GetUninitializedObject(typeof(Pawn));
            pawn.def = new ThingDef { tickerType = TickerType.Normal };
            pawn.health = (Pawn_HealthTracker)RuntimeHelpers.GetUninitializedObject(typeof(Pawn_HealthTracker));
            AccessTools.Field(typeof(Pawn_HealthTracker), "healthState").SetValue(pawn.health, PawnHealthState.Mobile);
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);

            harmony.CreateClassProcessor(assembly.GetType("RimExodus.Patches_Dormancy+Patch_TimedDetectionRaids_Dormancy", true)!).Patch();
            Check(harmony.GetPatchedMethods().Count() == 1, "timer patch binds without global spawn or registration hooks");

            var timer = new TimedDetectionRaids { parent = parent };
            var remaining = AccessTools.Field(typeof(TimedDetectionRaids), "ticksLeftToSendRaid");
            var notification = AccessTools.Field(typeof(TimedDetectionRaids), "ticksLeftTillNotifyPlayer");
            remaining.SetValue(timer, 1200);
            notification.SetValue(timer, 600);
            asleep.Add(map);
            for (int i = 0; i < 20; i++) timer.CompTickInterval(100);
            Check(timer.TicksLeftToSendRaids == 1200 && (int)notification.GetValue(timer)! == 600,
                "sleep preserves both countdowns even beyond the original due time");
            Check(timer.RaidsSentCount == 0, "sleep does not consume a raid or create an arrival");

            asleep.Remove(map);
            timer.CompTickInterval(100);
            Check(timer.TicksLeftToSendRaids == 1100 && (int)notification.GetValue(timer)! == 500,
                "wake resumes the actual vanilla timer with no catch-up");
            maps.Remove(map);
            timer.CompTickInterval(100);
            Check(timer.TicksLeftToSendRaids == -1 && (int)notification.GetValue(timer)! == -1,
                "no-map branch retains vanilla countdown cleanup");
            maps.Add(map);

            var normal = (TickList)AccessTools.Field(typeof(TickManager), "tickListNormal").GetValue(game.tickManager)!;
            var registrations = (IList)AccessTools.Field(typeof(TickList), "thingsToRegister").GetValue(normal)!;
            var deregistrations = (IList)AccessTools.Field(typeof(TickList), "thingsToDeregister").GetValue(normal)!;
            // Keep the fixture neutral, so the real dormant branch freezes the group without creating UI.
            harmony.Patch(AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer)),
                new HarmonyMethod(typeof(DormantRaidChecks), nameof(NoPlayerFaction)));
            harmony.Patch(AccessTools.Method(typeof(GenHostility), nameof(GenHostility.HostileTo),
                new[] { typeof(Thing), typeof(Faction) }),
                new HarmonyMethod(typeof(DormantRaidChecks), nameof(NoHostility)));
            asleep.Add(map);
            game.tickManager.RegisterAllTickabilityFor(pawn);
            Check(registrations.Count == 1, "vanilla spawn registers the new pawn normally");
            var parms = new IncidentParms { target = map };
            var keepLocal = outer.GetMethod("KeepDormantRaidLocal", flags)!;
            Check((bool)keepLocal.Invoke(null, new object[] { parms, new List<Pawn> { pawn } })!,
                "relocation branch handles a sleeping target directly");
            Check(deregistrations.Count == 1, "relocation branch buffers deregistration of the new pawn");
            normal.Tick();
            var buckets = (List<List<Thing>>)AccessTools.Field(typeof(TickList), "thingLists").GetValue(normal)!;
            Check(buckets.All(bucket => bucket.Count == 0), "new pawn is removed before it can tick");
            asleep.Remove(map);
            game.tickManager.RegisterAllTickabilityFor(pawn);
            Check(registrations.Count == 1, "wake permits the normal registration path");
            Check(!(bool)keepLocal.Invoke(null, new object[] { parms, new List<Pawn> { pawn } })!,
                "active target bypasses dormant handling");
            registrations.Clear();

            asleep.Add(map);
            observers[map] = "Traveler";
            Check((string)manager.GetMethod("GetSleepObserver", flags)!.Invoke(null, new object[] { map })! == "Traveler",
                "letter witness uses the name captured at sleep time");

            outer.GetMethod("BeginIncident", flags)!.Invoke(null,
                new object[] { parms, true, null, null });
            var plan = outer.GetField("Current", flags | BindingFlags.Public)!.GetValue(null)!;
            Check(plan != null && plan.GetType().GetField("CandidateMaps")!.GetValue(plan) == null,
                "sleeping target retains only the existing generation window, with no cross-map candidates");
            outer.GetMethod("NotifyWindowPawnGenerated", flags)!.Invoke(null, new object[] { pawn });
            var captured = (IList)outer.GetField("WindowPawns", flags | BindingFlags.Public)!.GetValue(null)!;
            Check(captured.Count == 1, "sleeping incidents reuse the existing group capture");
            Check(!(bool)outer.GetMethod("TryRelocateToHost", flags)!.Invoke(null,
                new object[] { parms, new List<Pawn>() })!, "sleeping target never relocates the group");
            Check(deregistrations.Count == 0, "MakeLords relocation guard leaves cleanup to incident completion");
            var completion = assembly.GetType(
                "RimExodus.Patches_RaidOuterSpawn+Patch_IncidentWorker_Raid_TryExecuteWorker_RaidOuterSpawn", true)!;
            completion.GetMethod("Postfix", flags)!.Invoke(null, new object[] { parms });
            Check(deregistrations.Count == 1,
                "raid completion freezes arrivals even when no base MakeLords callback ran");
            Check(captured.Count == 0 && outer.GetField("Current", flags | BindingFlags.Public)!.GetValue(null) == null,
                "incident completion clears the capture without leaving a queue to poll");

            manager.GetMethod("ResetForGame", flags)!.Invoke(null, null);
            Check(asleep.Count == 0 && observers.Count == 0, "switching games clears sleep state and witness names");
            Console.WriteLine($"{checks} dormant-raid checks passed against real game methods. No game launched.");
        }
        finally
        {
            asleep.Clear();
            manager.GetMethod("ResetForGame", flags)!.Invoke(null, null);
            outer.GetMethod("End", flags)!.Invoke(null, null);
            harmony.UnpatchAll(harmony.Id);
            Current.Game = previousGame;
        }
    }

    private static bool NoPlayerFaction(ref Faction __result) { __result = null; return false; }
    private static bool NoHostility(ref bool __result) { __result = false; return false; }
}
