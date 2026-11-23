using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// Soft Giddy-Up 2 bridge. Its mounted pair consists of two independently spawned pawns;
    /// transfer the mount with the rider and let Giddy-Up rebuild its own mounted job/data.
    /// No Giddy-Up type is referenced at compile time.
    /// </summary>
    internal static class SeamlessGiddyUpCompat
    {
        private const string PackageId = "MemeGoddess.GiddyUp";
        private static PropertyInfo singletonProperty;
        private static FieldInfo mountedCacheField;
        private static MethodInfo mountedCacheContainsMethod;
        private static MethodInfo getDataMethod;
        private static PropertyInfo mountProperty;
        private static MethodInfo goMountMethod;
        private static object instantGiveJobMethod;
        private static bool available;

        internal static void Register()
        {
            if (!ModsConfig.IsActive(PackageId)) return;

            try
            {
                var storageType = AccessTools.TypeByName("GiddyUp.ExtendedDataStorage");
                var dataType = AccessTools.TypeByName("GiddyUp.ExtendedPawnData");
                var utilityType = AccessTools.TypeByName("GiddyUp.MountUtility");
                var giveJobMethodType = AccessTools.TypeByName("GiddyUp.MountUtility+GiveJobMethod");
                singletonProperty = AccessTools.Property(storageType, "Singleton");
                mountedCacheField = AccessTools.Field(storageType, "isMounted");
                mountedCacheContainsMethod = AccessTools.Method(mountedCacheField?.FieldType, "Contains", new[] { typeof(int) });
                getDataMethod = AccessTools.Method(storageType, "GetExtendedPawnData", new[] { typeof(Pawn) });
                mountProperty = AccessTools.Property(dataType, "Mount");
                goMountMethod = AccessTools.Method(utilityType, "GoMount");
                instantGiveJobMethod = Enum.Parse(giveJobMethodType, "Instant");

                if (singletonProperty == null || mountedCacheField == null || mountedCacheContainsMethod == null ||
                    getDataMethod == null || mountProperty == null ||
                    goMountMethod == null || instantGiveJobMethod == null)
                    throw new MissingMemberException("one or more Giddy-Up mounting members were not found");

                available = true;
                SeamlessTransferAssociations.Register(Capture);
                Log.Message("[RimExodus] Giddy-Up 2 compat: mounted-pair transfer and caravan departure bridges registered.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Giddy-Up 2 compat unavailable; RimExodus continues without it: {ex}");
            }
        }

        private static SeamlessTransferAssociations.ISession Capture(Pawn rider)
        {
            if (!TryGetActiveMount(rider, out var mount)) return null;
            return new MountedPairSession(rider, mount);
        }

        /// <summary>
        /// Mirrors Giddy-Up's WalkTheWorld leaving-pawn compatibility: when vanilla creates a caravan from
        /// one pawn stepping onto an exit cell, include that rider's active mount in the initial pawn set.
        /// This keeps the pair atomic instead of relying on the mount's later auto-join ExitMap job, which a
        /// seamless map can legitimately interpret as an evacuation across the loaded neighboring tile.
        /// </summary>
        internal static void IncludeMountedAnimals(ref IEnumerable<Pawn> pawns)
        {
            if (!available || pawns == null) return;

            try
            {
                var leavingPawns = pawns.ToList();
                if (leavingPawns.Count == 0) return;

                var originalCount = leavingPawns.Count;
                var transferredPawnIds = new HashSet<int>();
                for (var i = 0; i < originalCount; i++)
                    if (leavingPawns[i] != null)
                        transferredPawnIds.Add(leavingPawns[i].thingIDNumber);

                for (var i = 0; i < originalCount; i++)
                {
                    if (!TryGetActiveMount(leavingPawns[i], out var mount)
                        || !transferredPawnIds.Add(mount.thingIDNumber))
                        continue;
                    leavingPawns.Add(mount);
                }

                if (leavingPawns.Count == originalCount) return;
                pawns = leavingPawns;
                RimExodusLog.Message(RimExodusLogModule.Compat,
                    $"Giddy-Up 2 caravan departure added {leavingPawns.Count - originalCount} mounted animal(s).");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Giddy-Up 2 caravan departure bridge failed; vanilla caravan formation continues: {ex}");
            }
        }

        private static bool TryGetActiveMount(Pawn rider, out Pawn mount)
        {
            mount = null;
            if (!available || rider == null) return false;
            var mountedCache = mountedCacheField.GetValue(null);
            if (mountedCache == null) return false;
            if (!(bool)mountedCacheContainsMethod.Invoke(mountedCache, new object[] { rider.thingIDNumber })) return false;

            var storage = singletonProperty.GetValue(null, null);
            if (storage == null) return false;
            var data = getDataMethod.Invoke(storage, new object[] { rider });
            mount = mountProperty.GetValue(data, null) as Pawn;
            if (mount == null || mount == rider || mount.Dead || !mount.Spawned || mount.Map != rider.Map)
            {
                mount = null;
                return false;
            }
            return true;
        }

        private sealed class MountedPairSession : SeamlessTransferAssociations.ISession
        {
            private readonly Pawn rider;
            private readonly Pawn mount;

            internal MountedPairSession(Pawn rider, Pawn mount)
            {
                this.rider = rider;
                this.mount = mount;
            }

            public void TransferAndRestore(Func<Pawn, bool> transferAssociatedPawn)
            {
                if (!transferAssociatedPawn(mount))
                {
                    Log.Warning($"[RimExodus] Giddy-Up 2 compat could not transfer mount {mount.LabelShort} with rider {rider.LabelShort}.");
                    return;
                }

                // Mirrors Giddy-Up's own WalkTheWorld compatibility: Instant rebuilds both
                // ExtendedPawnData links, the mounted cache, and the mount's Mounted job. The old driver caches
                // its source map, so finish it first; its own finish action clears the old relationship cleanly.
                mount.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false, canReturnToPool: false);
                goMountMethod.Invoke(null, new[] { (object)rider, mount, instantGiveJobMethod, null, null });
                RimExodusLog.Message(RimExodusLogModule.Compat,
                    $"Giddy-Up 2 mounted pair restored: {rider.LabelShort} / {mount.LabelShort}.");
            }
        }
    }

    /// <summary>
    /// Base-game chokepoint used by Pawn.ExitMap(true) when the first pawn creates an auto-joinable caravan.
    /// The patch contains no Giddy-Up types and is a no-op until the soft compatibility bridge registers.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_CaravanExitMapUtility_ExitMapAndCreateCaravan_GiddyUp
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CaravanExitMapUtility),
                nameof(CaravanExitMapUtility.ExitMapAndCreateCaravan),
                new[]
                {
                    typeof(IEnumerable<Pawn>), typeof(Faction), typeof(PlanetTile), typeof(PlanetTile),
                    typeof(PlanetTile), typeof(bool)
                });
        }

        private static void Prefix(ref IEnumerable<Pawn> pawns)
        {
            SeamlessGiddyUpCompat.IncludeMountedAnimals(ref pawns);
        }
    }
}
