using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>Colony Groups draws its own bar and never consumes the vanilla ColonistBar entry cache.</summary>
    internal static class SeamlessColonyGroupsCompat
    {
        private const string PackageId = "DerekBickley.LTOColonyGroupsFinal";
        private static FieldInfo entriesField, groupsField, finderField, scaleField;
        private static PropertyInfo drawLocsProperty;
        private static FieldInfo entryPawnField, entryMapField, entryGroupField, entryCaravanGroupField, entryColonyGroupField;
        private static ConstructorInfo entryConstructor;
        private static MethodInfo calculateDrawLocs;

        internal static void Register(Harmony harmony)
        {
            if (!ModsConfig.IsActive(PackageId)) return;
            try
            {
                var bar = AccessTools.TypeByName("TacticalGroups.TacticalColonistBar");
                var entry = AccessTools.TypeByName("TacticalGroups.TacticalColonistBar+Entry");
                var method = AccessTools.Method(bar, "CheckRecacheEntries");
                entriesField = AccessTools.Field(bar, "cachedEntries");
                groupsField = AccessTools.Field(bar, "cachedReorderableGroups");
                finderField = AccessTools.Field(bar, "drawLocsFinder");
                scaleField = AccessTools.Field(bar, "cachedScale");
                drawLocsProperty = AccessTools.Property(bar, "DrawLocs");
                entryPawnField = AccessTools.Field(entry, "pawn");
                entryMapField = AccessTools.Field(entry, "map");
                entryGroupField = AccessTools.Field(entry, "group");
                entryCaravanGroupField = AccessTools.Field(entry, "caravanGroup");
                entryColonyGroupField = AccessTools.Field(entry, "colonyGroup");
                entryConstructor = entry?.GetConstructor(new[] { typeof(Pawn), typeof(Map), typeof(int),
                    AccessTools.TypeByName("TacticalGroups.CaravanGroup"), AccessTools.TypeByName("TacticalGroups.ColonyGroup") });
                var finder = AccessTools.TypeByName("TacticalGroups.ColonistBarDrawLocsFinder");
                calculateDrawLocs = AccessTools.Method(finder, "CalculateDrawLocs", new[] {
                    drawLocsProperty?.PropertyType, typeof(float).MakeByRefType() });
                if (method == null || entriesField == null || groupsField == null || finderField == null ||
                    scaleField == null || drawLocsProperty == null || entryPawnField == null ||
                    entryMapField == null || entryGroupField == null || entryCaravanGroupField == null ||
                    entryColonyGroupField == null || entryConstructor == null || calculateDrawLocs == null)
                    throw new MissingMemberException("TacticalColonistBar cache layout changed");
                harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(SeamlessColonyGroupsCompat), nameof(RecachePostfix))));
                Log.Message("[RimExodus] Colony Groups compat: bound TacticalColonistBar.CheckRecacheEntries filter.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimExodus] Colony Groups compat unavailable; bar filtering remains vanilla only: " + ex);
            }
        }

        private static void RecachePostfix(object __instance)
        {
            try
            {
                var entries = entriesField.GetValue(__instance) as IList;
                if (entries == null) return;
                var removed = false;
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    var map = entryMapField.GetValue(entries[i]) as Map;
                    if (map == null || !ShouldHide(map)) continue;
                    entries.RemoveAt(i);
                    removed = true;
                }
                if (!removed) return;

                // Entry is a struct whose drag callbacks capture its group at construction time.
                var oldGroup = int.MinValue;
                var newGroup = -1;
                for (var i = 0; i < entries.Count; i++)
                {
                    var original = entries[i];
                    var group = (int)entryGroupField.GetValue(original);
                    if (group != oldGroup) { oldGroup = group; newGroup++; }
                    entries[i] = entryConstructor.Invoke(new[] { entryPawnField.GetValue(original),
                        entryMapField.GetValue(original), (object)newGroup,
                        entryCaravanGroupField.GetValue(original), entryColonyGroupField.GetValue(original) });
                }

                var groups = groupsField.GetValue(__instance) as IList;
                groups?.Clear();
                if (groups != null)
                    for (var i = 0; i < entries.Count; i++) groups.Add(-1);
                var args = new object[] { drawLocsProperty.GetValue(__instance), 1f };
                calculateDrawLocs.Invoke(finderField.GetValue(__instance), args);
                scaleField.SetValue(__instance, args[1]);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[RimExodus] Colony Groups bar recache failed: " + ex, 0x5B7C2A1);
            }
        }

        private static bool ShouldHide(Map map)
        {
            return SeamlessDormancyManager.IsDormant(map) ||
                (HideEmptyColonistBarGroups.Enabled && !map.IsPlayerHome &&
                 map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count == 0);
        }
    }
}
