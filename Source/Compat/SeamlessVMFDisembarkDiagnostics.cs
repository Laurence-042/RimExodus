using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// VMF recalculates the ground cell after its exit animation, then resumes the job. If that cell
    /// has become unwalkable, vanilla StartPath silently repairs the pawn's position. Keep the cell
    /// state from VMF's spawn and report it only when that recovery actually occurs.
    /// </summary>
    internal static class SeamlessVMFDisembarkDiagnostics
    {
        private sealed class Landing
        {
            internal int tick;
            internal string before;
            internal string after;
        }

        private static readonly ConditionalWeakTable<Pawn, Landing> RecentLandings =
            new ConditionalWeakTable<Pawn, Landing>();
        private static readonly FieldInfo PatherPawn = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
        private static Type vmfDriverType;

        internal static void Register(Harmony harmony)
        {
            try
            {
                vmfDriverType = AccessTools.TypeByName("VehicleMapFramework.JobDriverAcrossMaps");
                if (vmfDriverType == null) return;
                Func<MethodBase> spawn = () => AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn),
                    new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4),
                        typeof(WipeMode), typeof(bool), typeof(bool) });
                SeamlessPatchBinding.TryBind(harmony, "VMF ground landing before spawn", spawn,
                    typeof(SeamlessVMFDisembarkDiagnostics), nameof(SpawnPrefix));
                SeamlessPatchBinding.TryBind(harmony, "VMF ground landing after spawn", spawn,
                    typeof(SeamlessVMFDisembarkDiagnostics), nameof(SpawnPostfix), postfix: true);
                SeamlessPatchBinding.TryBind(harmony, "VMF ground landing unwalkable recovery",
                    () => AccessTools.Method(typeof(Pawn_PathFollower),
                        nameof(Pawn_PathFollower.TryRecoverFromUnwalkablePosition), new[] { typeof(bool) }),
                    typeof(SeamlessVMFDisembarkDiagnostics), nameof(RecoveryPrefix));
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus] VMF disembark diagnostics unavailable; initialization continues: " + error);
            }
        }

        private static void SpawnPrefix(Thing newThing, IntVec3 loc, Map map, out Landing __state)
        {
            __state = null;
            try
            {
                if (!(newThing is Pawn pawn) || !IsVmfTraversal(pawn) || !IsSurface(map)) return;
                __state = new Landing
                {
                    tick = Find.TickManager.TicksGame,
                    before = Describe("planned", pawn, map, loc)
                };
            }
            catch (Exception error)
            {
                Log.WarningOnce("[RimExodus] VMF landing diagnostic capture failed: " + error, 79103101);
            }
        }

        private static void SpawnPostfix(Thing newThing, Map map, Landing __state)
        {
            if (__state == null || !(newThing is Pawn pawn)) return;
            try
            {
                __state.after = Describe("spawned", pawn, pawn.Map, pawn.Position);
                RecentLandings.Remove(pawn);
                RecentLandings.Add(pawn, __state);
                RimExodusLog.Message(RimExodusLogModule.Transfer,
                    $"VMF exit landing {pawn.LabelShort} id={pawn.thingIDNumber}: {__state.before}; {__state.after}");
            }
            catch (Exception error)
            {
                Log.WarningOnce("[RimExodus] VMF landing diagnostic completion failed: " + error, 79103102);
            }
        }

        private static void RecoveryPrefix(Pawn_PathFollower __instance)
        {
            try
            {
                var pawn = PatherPawn?.GetValue(__instance) as Pawn;
                if (pawn == null || !IsSurface(pawn.Map)) return;
                var hasLanding = RecentLandings.TryGetValue(pawn, out var landing)
                    && Find.TickManager.TicksGame - landing.tick <= 120;
                if (!hasLanding && !IsVmfTraversal(pawn)) return;
                var current = Describe("before recovery", pawn, pawn.Map, pawn.Position);
                Log.Warning($"[RimExodus] VMF exit landing diagnostic {pawn.LabelShort} id={pawn.thingIDNumber} "
                    + $"job={pawn.CurJob?.def?.defName ?? "none"} driver={pawn.jobs?.curDriver?.GetType().FullName ?? "none"} "
                    + $"tick={Find.TickManager.TicksGame}: "
                    + (hasLanding ? landing.before + "; " + landing.after + "; " : "no recent VMF spawn; ")
                    + current);
                if (hasLanding) RecentLandings.Remove(pawn);
            }
            catch (Exception error)
            {
                Log.WarningOnce("[RimExodus] VMF landing diagnostic recovery capture failed: " + error, 79103103);
            }
        }

        private static bool IsVmfTraversal(Pawn pawn)
            => vmfDriverType != null && vmfDriverType.IsInstanceOfType(pawn.jobs?.curDriver);

        private static bool IsSurface(Map map)
            => map != null && !map.Disposed && SeamlessTileRegistry.GetMapWorldTile(map) >= 0;

        private static string Describe(string stage, Pawn pawn, Map map, IntVec3 cell)
        {
            if (map == null) return $"{stage}: map=null cell={cell}";
            var header = $"{stage}: map={map.uniqueID} tile={SeamlessTileRegistry.GetMapWorldTile(map)} cell={cell}";
            if (!cell.InBounds(map)) return header + " inBounds=false";
            var door = cell.GetDoor(map);
            var edifice = cell.GetEdifice(map);
            return header + $" terrain={cell.GetTerrain(map)?.defName ?? "null"}"
                + $" standable={cell.Standable(map)} pathWalkable={map.pathing.For(pawn).pathGrid.Walkable(cell)}"
                + $" pawnWalkable={cell.WalkableBy(map, pawn)}"
                + $" edifice={edifice?.def?.defName ?? "none"}"
                + (door == null ? " door=none" :
                    $" door={door.def.defName}(open={door.Open},pawnCanOpen={door.PawnCanOpen(pawn)})");
        }
    }
}
