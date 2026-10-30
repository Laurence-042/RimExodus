#if RIMWORLD_1_7
using System.Collections.Generic;
using RimExodus.Maps;
using RimWorld;
using Verse;

namespace RimExodus
{
    internal static class SeamlessRenderDiagnostics
    {
        internal static void DrawControls(Listing_Standard listing)
        {
            var diagnostics = MapRuntime.Current?.Rendering.Diagnostics;
            bool active = diagnostics?.Active == true;
            string label = active ? "RimExodus_RenderTraceStop" : "RimExodus_RenderTraceStart";
            if (listing.ButtonText(label.Translate(), "RimExodus_RenderTraceTip".Translate()))
            {
                if (active) diagnostics.StopCapture();
                else if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null
                    || !SeamlessNativeRendering.Enabled || diagnostics == null)
                    Messages.Message("RimExodus_RenderTraceUnavailable".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                {
                    string path = diagnostics.StartCapture();
                    if (path != null)
                        Messages.Message("RimExodus_RenderTraceStarted".Translate(path), MessageTypeDefOf.TaskCompletion, false);
                    else Messages.Message("RimExodus_RenderTraceFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }
            if (!string.IsNullOrEmpty(diagnostics?.LastPath))
                listing.Label("RimExodus_RenderTracePath".Translate(diagnostics.LastPath));
            if (diagnostics?.Active == true)
            {
                string sourceLabel = diagnostics.IsolatedSource == null
                    ? "RimExodus_RenderIsolationAll".Translate().ToString()
                    : "RimExodus_RenderIsolationMap".Translate(diagnostics.IsolatedSource.uniqueID).ToString();
                if (listing.ButtonText("RimExodus_RenderIsolationSelect".Translate(sourceLabel),
                    "RimExodus_RenderIsolationTip".Translate()))
                {
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("RimExodus_RenderIsolationAll".Translate(), () => diagnostics.TrySetIsolatedSource(null))
                    };
                    foreach (var source in diagnostics.Sources)
                    {
                        var selected = source;
                        string role = (source == Find.CurrentMap ? "RimExodus_RenderIsolationFocus" : "RimExodus_RenderIsolationNeighbor").Translate();
                        options.Add(new FloatMenuOption("RimExodus_RenderIsolationOption".Translate(source.uniqueID, role),
                            () => diagnostics.TrySetIsolatedSource(selected)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            listing.Gap(6f);
        }
    }
}
#endif
