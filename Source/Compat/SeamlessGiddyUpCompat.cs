using System;
using System.Reflection;
using HarmonyLib;
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

                SeamlessTransferAssociations.Register(Capture);
                Log.Message("[RimExodus] Giddy-Up 2 compat: mounted-pair transfer bridge registered.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Giddy-Up 2 compat unavailable; RimExodus continues without it: {ex}");
            }
        }

        private static SeamlessTransferAssociations.ISession Capture(Pawn rider)
        {
            var mountedCache = mountedCacheField.GetValue(null);
            if (mountedCache == null) return null;
            if (!(bool)mountedCacheContainsMethod.Invoke(mountedCache, new object[] { rider.thingIDNumber })) return null;

            var storage = singletonProperty.GetValue(null, null);
            if (storage == null) return null;
            var data = getDataMethod.Invoke(storage, new object[] { rider });
            var mount = mountProperty.GetValue(data, null) as Pawn;
            if (mount == null || mount == rider || mount.Dead || !mount.Spawned || mount.Map != rider.Map)
                return null;
            return new MountedPairSession(rider, mount);
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
}
