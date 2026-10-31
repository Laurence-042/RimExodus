using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// VMF redirects Targeter.CurrentTargetUnderMouse from GenUI.TargetsAtMouse to its own method.
    /// Apply the existing neighbor picker at that alternate entry; same-map and vehicle-map clicks
    /// continue through VMF's original implementation.
    /// </summary>
    internal static class SeamlessVMFTargetingCompat
    {
        private static bool warned;
        private static Action<Thing, Map> setTargetMap;

        internal static void Register(Harmony harmony)
        {
            try
            {
                var vmfType = AccessTools.TypeByName("VehicleMapFramework.GenUIOnVehicle");
                if (vmfType == null) return;
                try
                {
                    var setter = AccessTools.Method(AccessTools.TypeByName("VehicleMapFramework.TargetMapUtility"),
                        "set_TargetMap", new[] { typeof(Thing), typeof(Map) });
                    if (setter != null)
                        setTargetMap = (Action<Thing, Map>)Delegate.CreateDelegate(typeof(Action<Thing, Map>), setter);
                }
                catch (Exception error)
                {
                    Log.Warning("[RimExodus] VMF target-map context unavailable; neighbor picking still enabled: " + error);
                }
                SeamlessPatchBinding.TryBind(harmony, "VMF GenUIOnVehicle.TargetsAtMouse neighbor targeting",
                    () => AccessTools.Method(vmfType,
                        "TargetsAtMouse", new[] { typeof(TargetingParameters), typeof(bool), typeof(ITargetingSource) }),
                    typeof(SeamlessVMFTargetingCompat), nameof(Prefix));
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus] VMF neighbor targeting registration unavailable; initialization continues: " + error);
            }
        }

        public static bool Prefix(TargetingParameters __0, bool __1, ITargetingSource __2,
            ref IEnumerable<LocalTargetInfo> __result)
        {
            try
            {
                var runOriginal = Patch_GenUI_TargetsAtMouse_CrossMapCombat.Prefix(
                    __0, __1, __2, ref __result);
                if (!runOriginal && __2?.Caster != null) setTargetMap?.Invoke(__2.Caster, Find.CurrentMap);
                return runOriginal;
            }
            catch (Exception error)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[RimExodus] VMF neighbor targeting unavailable; VMF targeting continues: " + error);
                }
                return true;
            }
        }
    }
}
