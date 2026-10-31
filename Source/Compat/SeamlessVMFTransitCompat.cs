using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// A VMF exit job keeps its selected ground map, but its final spawn cell is recalculated from
    /// the vehicle's current ground map. If RE transfers the vehicle before that exit finishes, VMF
    /// can spawn the passenger on the new map and continue pathing to a cell from the old map.
    /// Cancel unfinished cross-map routes from this vehicle interior before its ground map changes;
    /// the passenger remains safely aboard and can receive a fresh order after arrival.
    /// </summary>
    internal static class SeamlessVMFTransitCompat
    {
        private static Type vehicleWithMapType;
        private static Type acrossMapsDriverType;
        private static FieldInfo interiorMapField;
        private static PropertyInfo destMapProperty;

        internal static void Register()
        {
            try
            {
                vehicleWithMapType = AccessTools.TypeByName("VehicleMapFramework.VehiclePawnWithMap");
                if (vehicleWithMapType == null) return;
                acrossMapsDriverType = AccessTools.TypeByName("VehicleMapFramework.JobDriverAcrossMaps");
                interiorMapField = AccessTools.Field(vehicleWithMapType, "interiorMap");
                destMapProperty = acrossMapsDriverType == null ? null : AccessTools.Property(acrossMapsDriverType, "DestMap");
                if (acrossMapsDriverType == null || interiorMapField == null || destMapProperty == null)
                    Log.Warning("[RimExodus] VMF vehicle-transfer exit guard unavailable: VMF members changed.");
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus] VMF vehicle-transfer exit guard unavailable; initialization continues: " + error);
                vehicleWithMapType = null;
            }
        }

        internal static void BeforeVehicleTransfer(Pawn vehicle, Map departureMap)
        {
            if (vehicleWithMapType == null || acrossMapsDriverType == null || interiorMapField == null
                || destMapProperty == null || vehicle == null || departureMap == null) return;
            try
            {
                if (!vehicleWithMapType.IsInstanceOfType(vehicle)) return;
                var interior = interiorMapField.GetValue(vehicle) as Map;
                if (interior == null || interior.Disposed) return;
                var passengers = interior.mapPawns.AllPawnsSpawned.ToArray();
                for (var i = 0; i < passengers.Length; i++)
                {
                    try
                    {
                        var passenger = passengers[i];
                        if (passenger == null || !passenger.Spawned || passenger.Map != interior) continue;
                        var driver = passenger.jobs?.curDriver;
                        if (driver == null || !acrossMapsDriverType.IsInstanceOfType(driver)) continue;
                        if (destMapProperty.GetValue(driver) is not Map destination || destination == interior) continue;
                        passenger.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                        RimExodusLog.Message(RimExodusLogModule.Transfer,
                            $"VMF cross-map order stopped for {passenger.LabelShort}: vehicle {vehicle.LabelShort} left map {departureMap.uniqueID} before disembark completed.");
                    }
                    catch (Exception error)
                    {
                        Log.WarningOnce("[RimExodus] VMF vehicle-transfer exit guard failed for one passenger: " + error,
                            79103104);
                    }
                }
            }
            catch (Exception error)
            {
                Log.WarningOnce("[RimExodus] VMF vehicle-transfer exit guard failed; vehicle transfer continues: " + error,
                    79103105);
            }
        }
    }
}
