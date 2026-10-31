using System;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Compiled into RimExodus.Bootstrap.dll without a REF reference. RimWorld sorts assembly
    /// filenames before loading, so this helper remains usable if RimExodus.dll cannot load.
    /// </summary>
    public sealed class RimExodusMissingFrameworkNotice : Mod
    {
        private static string requiredFrameworkId;
        private static bool shown;

        public RimExodusMissingFrameworkNotice(ModContentPack content) : base(content)
        {
            requiredFrameworkId = content.PackageIdPlayerFacing.EndsWith(".Dev", StringComparison.OrdinalIgnoreCase)
                ? "RimExodus.Framework.dev" : "RimExodus.Framework";
            try
            {
                var target = AccessTools.Method(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init));
                var postfix = AccessTools.Method(typeof(RimExodusMissingFrameworkNotice), nameof(AfterEntryInit));
                if (target == null || postfix == null)
                {
                    Log.Warning("[RimExodus] Missing-REF notice: UIRoot_Entry.Init binding unavailable.");
                    return;
                }
                new Harmony("RimExodus.MissingFrameworkNotice").Patch(target, postfix: new HarmonyMethod(postfix));
                Log.Message("[RimExodus] Missing-REF notice: bound UIRoot_Entry.Init.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimExodus] Missing-REF notice binding failed: " + ex);
            }
        }

        private static void AfterEntryInit()
        {
            if (shown || requiredFrameworkId == null || ModsConfig.IsActive(requiredFrameworkId)
                || Find.WindowStack == null) return;
            Find.WindowStack.Add(new Dialog_MessageBox(
                "RimExodus_RefTransitionNoticeBody".Translate(),
                "RimExodus_AnnouncementsAcknowledge".Translate(),
                title: "RimExodus_RefTransitionNoticeTitle".Translate()));
            shown = true;
        }
    }
}
