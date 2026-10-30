using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>Manual engine hooks: isolate lookup and patch failures, and report actual ownership.</summary>
    internal static class SeamlessPatchBinding
    {
        internal static bool TryBind(Harmony harmony, string label, Func<MethodBase> resolve,
            Type patchType, string callback, bool postfix = false)
        {
            try
            {
                var target = resolve() ?? throw new MissingMethodException(label);
                var method = AccessTools.Method(patchType, callback)
                    ?? throw new MissingMethodException(patchType.FullName, callback);
                var hook = new HarmonyMethod(method);
                if (postfix) harmony.Patch(target, postfix: hook);
                else harmony.Patch(target, prefix: hook);
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !(postfix ? info.Postfixes : info.Prefixes)
                    .Any(p => p.owner == harmony.Id && p.PatchMethod == method))
                    throw new InvalidOperationException("Harmony did not register the requested callback.");
                Log.Message("[RimExodus] Manual patch bound: " + label);
                return true;
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus] Manual patch unavailable: " + label + "; initialization continues. " + error);
                return false;
            }
        }
    }
}
