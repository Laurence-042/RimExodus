using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>Controls outer-edge placement at each stage of raid selection.</summary>
    public class RaidOuterSpawnIncidentDialog : Window
    {
        public override Vector2 InitialSize => new Vector2(690f, 650f);

        private Vector2 scroll;
        private RaidEntryScope scope;
        private readonly List<Def> incidents;
        private readonly List<Def> strategies;
        private readonly List<Def> arrivals;

        public RaidOuterSpawnIncidentDialog()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            incidents = DefDatabase<IncidentDef>.AllDefs
                .Where(d => IsOuterSpawnCandidate(d.workerClass) || d.category == IncidentCategoryDefOf.ThreatBig
                    || d.category == IncidentCategoryDefOf.ThreatSmall || d.category == IncidentCategoryDefOf.DeepDrillInfestation)
                .OrderBy(d => d.LabelCap.ToString())
                .Cast<Def>()
                .ToList();
            strategies = DefDatabase<RaidStrategyDef>.AllDefs
                .OrderBy(d => d.LabelCap.ToString()).Cast<Def>().ToList();
            arrivals = DefDatabase<PawnsArrivalModeDef>.AllDefs
                .OrderBy(d => d.LabelCap.ToString()).Cast<Def>()
                .ToList();
        }

        internal static bool IsOuterSpawnCandidate(System.Type workerType)
        {
            return workerType != null
                && (typeof(IncidentWorker_Raid).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_AggressiveAnimals).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_GhoulAttack).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_ChimeraAssault).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_EntitySwarm).IsAssignableFrom(workerType));
        }

        public override void DoWindowContents(Rect inRect)
        {
            var settings = RimExodusMod.Settings;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f),
                "RimExodus_RaidOuterSpawnIncidentsTitle".Translate());
            Text.Font = GameFont.Small;
            var hintRect = new Rect(0f, 38f, inRect.width, 58f);
            Widgets.Label(hintRect, "RimExodus_RaidOuterSpawnIncidentsHint".Translate());
            TooltipHandler.TipRegion(hintRect, "RimExodus_RaidOuterSpawnIncidentsHintTip".Translate());

            const float top = 125f;
            const float rowHeight = 49f;
            var listRect = new Rect(0f, top, inRect.width, inRect.height - top - 51f);
            TabDrawer.DrawTabs(listRect, new List<TabRecord>
            {
                Tab(RaidEntryScope.Incident, "RimExodus_RaidEntryEvents"),
                Tab(RaidEntryScope.Strategy, "RimExodus_RaidEntryStrategies"),
                Tab(RaidEntryScope.Arrival, "RimExodus_RaidEntryArrivals")
            });
            var defs = scope == RaidEntryScope.Incident ? incidents
                : scope == RaidEntryScope.Strategy ? strategies : arrivals;
            var viewRect = new Rect(0f, 0f, listRect.width - 16f, defs.Count * rowHeight);
            Widgets.BeginScrollView(listRect, ref scroll, viewRect);
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / rowHeight));
            int last = Mathf.Min(defs.Count, Mathf.CeilToInt((scroll.y + listRect.height) / rowHeight));
            for (int i = first; i < last; i++)
                DrawRow(new Rect(0f, i * rowHeight, viewRect.width, rowHeight), defs[i], settings);
            Widgets.EndScrollView();

            if (Widgets.ButtonText(new Rect(0f, inRect.height - 38f, 220f, 36f),
                    "RimExodus_RaidEntryReset".Translate()))
                SeamlessRaidOuterSpawn.ResetEntryPreferences(settings);
        }

        private TabRecord Tab(RaidEntryScope tab, string label) => new TabRecord(label.Translate(), () =>
        {
            scope = tab;
            scroll = Vector2.zero;
        }, scope == tab);

        private void DrawRow(Rect row, Def def, RimExodusSettings settings)
        {
            bool nativeOnly = def is IncidentDef incident && !IsOuterSpawnCandidate(incident.workerClass)
                || def is PawnsArrivalModeDef arrival && !arrival.walkIn;
            bool localEdgeOnly = def is IncidentDef scopedIncident && SeamlessRaidOuterSpawn.IsStructurallyLocalWorker(scopedIncident.workerClass)
                || def is RaidStrategyDef strategy && SeamlessRaidOuterSpawn.IsStrategyMapBound(strategy);
            var preference = SeamlessRaidOuterSpawn.EntryPreference(settings, scope, def.defName);
            var native = "RimExodus_RaidEntryNative".Translate();
            var outer = (localEdgeOnly ? "RimExodus_RaidEntryLocalEdge" : "RimExodus_RaidEntryOuter").Translate();
            var defaultLabel = "RimExodus_RaidEntryDefault".Translate(localEdgeOnly || nativeOnly ? native : outer);

            const float buttonWidth = 213f;
            var labelRect = new Rect(6f, row.y + 3f, row.width - buttonWidth - 16f, 43f);
            Widgets.Label(labelRect, $"{def.LabelCap}\n{def.defName}");
            var button = new Rect(row.xMax - buttonWidth - 2f, row.y + 7f, buttonWidth, 34f);
            var shown = nativeOnly || preference == RaidEntryPreference.Native ? native
                : preference == RaidEntryPreference.Outer ? outer : defaultLabel;
            TooltipHandler.TipRegion(row, (nativeOnly ? "RimExodus_RaidEntryNativeOnlyTip"
                : localEdgeOnly ? "RimExodus_RaidEntryLocalEdgeTip" : "RimExodus_RaidOuterSpawnIncidentsHintTip").Translate());
            if (Widgets.ButtonText(button, shown, active: !nativeOnly))
            {
                var selectedScope = scope;
                FloatMenuOption Option(string text, RaidEntryPreference choice) => new FloatMenuOption(text,
                    () => SeamlessRaidOuterSpawn.SetEntryPreference(settings, selectedScope, def.defName, choice));
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    Option(defaultLabel, RaidEntryPreference.Default),
                    Option(native, RaidEntryPreference.Native),
                    Option(outer, RaidEntryPreference.Outer)
                }));
            }
        }
    }
}
