using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>SS chooses observers and sight rules; REF supplies cell ownership. The original SS
    /// algorithms still run. All maps finish calculating before any cross-map visibility is published.</summary>
    internal static class SeamlessSurveillanceCompat
    {
        private static Type componentType;
        private static MethodInfo recalculate, applyFog, circle, scopeActive, scopeMoved;
        private static FieldInfo visible, scopePawn, scopeKind, scopeRadius, scopeSeparation, lastScopeCell;
        private static bool installed, failed;
        [ThreadStatic] private static Batch active;
        [ThreadStatic] private static int periodicDepth;

        internal static void Register(Harmony harmony)
        {
            try
            {
                if (AccessTools.TypeByName("SurveillanceShift.SurveillanceShiftMod") == null
                    && AccessTools.TypeByName("SurveillanceShift.MapComponent_FogOfWar") == null) return;
                // SS component cctor creates materials. Binding must happen on the main thread after defs.
                LongEventHandler.ExecuteWhenFinished(() => Bind(harmony));
            }
            catch (Exception error) { Fail(error); }
        }
        internal static void Bind(Harmony harmony)
        {
            if (installed || failed) return;
            var bound = new List<KeyValuePair<MethodInfo, MethodInfo>>();
            try
            {
                componentType = AccessTools.TypeByName("SurveillanceShift.MapComponent_FogOfWar");
                var shadow = AccessTools.TypeByName("SurveillanceShift.ShadowCaster");
                var scope = AccessTools.TypeByName("SurveillanceShift.ScopeView");
                if (componentType == null || shadow == null || scope == null) throw new MissingMemberException("SS vision types");
                recalculate = Require(componentType, "RecalculateVisibility", typeof(bool), typeof(HashSet<IntVec3>));
                applyFog = Require(componentType, "ApplyFog");
                visible = Field(componentType, "visibleCells", typeof(bool[]));
                scopePawn = Field(scope, "activePawn", typeof(Pawn));
                scopeKind = AccessTools.Field(scope, "activeKind") ?? throw new MissingFieldException("SS ScopeView.activeKind");
                scopeRadius = Field(scope, "ScopeRadius", typeof(int));
                scopeSeparation = Field(scope, "BinocularSeparation", typeof(int));
                lastScopeCell = Field(scope, "lastScopeCell", typeof(IntVec3));
                scopeActive = Require(scope, "IsActiveOn", typeof(Map));
                scopeMoved = Require(scope, "ScopeMoved", typeof(Map));
                circle = Require(scope, "ApplyCircle", typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(int), typeof(bool[]));
                void Patch(MethodInfo target, string prefix, string postfix = null, string finalizer = null)
                {
                    MethodInfo Hook(string name) => name == null ? null : AccessTools.Method(typeof(SeamlessSurveillanceCompat), name);
                    var pre = Hook(prefix); var post = Hook(postfix); var fin = Hook(finalizer);
                    // Record before patching so even a partially failed binding can be removed.
                    foreach (var hook in new[] { pre, post, fin }) if (hook != null) bound.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, hook));
                    harmony.Patch(target, pre == null ? null : new HarmonyMethod(pre), post == null ? null : new HarmonyMethod(post),
                        finalizer: fin == null ? null : new HarmonyMethod(fin));
                }
                Patch(recalculate, nameof(RecalculatePrefix));
                Patch(applyFog, nameof(ApplyFogPrefix));
                Patch(Require(componentType, "MapComponentTick"), nameof(TickPrefix), finalizer: nameof(TickFinalizer));
                Patch(Require(shadow, "ComputeFieldOfView", typeof(IntVec3), typeof(int), typeof(Map), typeof(bool[]), typeof(bool)),
                    nameof(FieldPrefix), nameof(FieldPostfix));
                Patch(Require(shadow, "ComputeConeFieldOfView", typeof(IntVec3), typeof(int), typeof(Rot4), typeof(float), typeof(Map), typeof(bool[]), typeof(bool)),
                    nameof(ConePrefix), nameof(FieldPostfix));
                Patch(Require(scope, "ApplyFieldOfView", typeof(Map), typeof(bool[])), nameof(ScopePrefix));
                Patch(Require(scope, "ScopeCenter", typeof(Map)), null, nameof(ScopeCenterPostfix));
                Patch(Require(componentType, "RefreshScopeOverlay"), nameof(RefreshScopePrefix));
                installed = true;
                Log.Message("[RimExodus:Compat] Surveillance Shift: bound 8 vision methods (REF cell queries, both backends).");
            }
            catch (Exception error)
            {
                foreach (var pair in bound)
                    try { harmony.Unpatch(pair.Key, pair.Value); }
                    catch (Exception cleanup) { Log.Warning("[RimExodus:Compat] SS binding rollback failed: " + cleanup); }
                Fail(error);
            }
        }
        private static MethodInfo Require(Type type, string name, params Type[] args)
        {
            var method = AccessTools.DeclaredMethod(type, name, args);
            Type result = name == "IsActiveOn" || name == "ScopeMoved" ? typeof(bool)
                : name == "ScopeCenter" ? typeof(IntVec3) : typeof(void);
            if (method == null || method.ReturnType != result || method.IsStatic == typeof(MapComponent).IsAssignableFrom(type))
                throw new MissingMethodException(type.FullName, name);
            return method;
        }
        private static FieldInfo Field(Type type, string name, Type expected)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static void Fail(Exception error)
        {
            if (failed) return;
            failed = true;
            Log.Warning("[RimExodus:Compat] SS cross-map vision disabled; local SS behavior retained: " + error);
        }
        private static MapComponent Component(Map map)
        {
            foreach (var component in map.components) if (componentType.IsInstanceOfType(component)) return component;
            return null;
        }
        private static void TickPrefix(out bool __state) { __state = !failed; if (__state) periodicDepth++; }
        private static Exception TickFinalizer(Exception __exception, bool __state)
        { if (__state) periodicDepth--; return __exception; }
        private static bool ApplyFogPrefix(MapComponent __instance)
            => active == null || !active.Components.ContainsKey(__instance.map);

        private static bool RecalculatePrefix(MapComponent __instance, bool __0, HashSet<IntVec3> __1)
        {
            if (failed || active != null) return true;
            try
            {
                if (!SeamlessSpatialQueries.TryCapture(__instance.map, out var space) || space.Placements.Count < 2) return true;
                var state = Current.Game?.GetComponent<SeamlessSurveillanceState>();
                int tick = Find.TickManager.TicksGame;
                if (periodicDepth > 0 && !__0 && __1 == null && state != null
                    && state.LastPeriodic.TryGetValue(__instance.map, out int last) && last == tick) return false;
                return !RunBatch(__instance, space, __0, __1, state, tick);
            }
            catch (Exception error) { Fail(error); return true; }
        }
        private static bool RunBatch(MapComponent requested, MapCellSpace initial, bool reveal, HashSet<IntVec3> excluded,
            SeamlessSurveillanceState state, int tick)
        {
            var batch = new Batch();
            var queue = new Queue<MapCellSpace>();
            queue.Enqueue(initial);
            // Scheduling follows the live connected component; each observer's geometric query remains single-hop.
            while (queue.Count > 0)
            {
                var space = queue.Dequeue();
                if (batch.Components.ContainsKey(space.Reference)) continue;
                var component = Component(space.Reference);
                if (component == null) continue;
                batch.Components.Add(space.Reference, component);
                batch.Spaces.Add(space.Reference, space);
                foreach (var placement in space.Placements)
                    if (!batch.Components.ContainsKey(placement.Map) && Component(placement.Map) != null
                        && SeamlessSpatialQueries.TryCapture(placement.Map, out var next)) queue.Enqueue(next);
            }
            if (batch.Components.Count < 2) return false;
            return ExecuteBatch(batch, requested, reveal, excluded, state, tick);
        }
        private static bool ExecuteBatch(Batch batch, MapComponent requested, bool reveal, HashSet<IntVec3> excluded,
            SeamlessSurveillanceState state, int tick)
        {
            try
            {
                active = batch;
                foreach (var entry in batch.Components)
                {
                    batch.Source = entry.Key;
                    recalculate.Invoke(entry.Value, new object[] { entry.Value == requested && reveal, entry.Value == requested ? excluded : null });
                }
                batch.Merge(visible);
                active = null;
                foreach (var component in batch.Components.Values) applyFog.Invoke(component, null);
                if (state != null)
                {
                    // Keep no references to removed maps between batches; a new Game owns a new state.
                    var removed = new List<Map>();
                    foreach (var map in state.LastPeriodic.Keys) if (!Find.Maps.Contains(map)) removed.Add(map);
                    foreach (var map in removed) state.LastPeriodic.Remove(map);
                    foreach (var map in batch.Components.Keys) state.LastPeriodic[map] = tick;
                }
                return true;
            }
            catch (Exception error)
            {
                active = null;
                Fail(error);
                // No staged results survive failure. Re-run native local calculations to repair swapped arrays.
                foreach (var component in batch.Components.Values)
                    try { recalculate.Invoke(component, new object[] { component == requested && reveal, component == requested ? excluded : null }); }
                    catch (Exception repair) { Log.Warning("[RimExodus:Compat] SS local visibility repair failed: " + repair); }
                return true;
            }
            finally { active = null; }
        }

        private sealed class Batch
        {
            internal Map Source;
            internal readonly Dictionary<Map, MapComponent> Components = new Dictionary<Map, MapComponent>();
            internal readonly Dictionary<Map, MapCellSpace> Spaces = new Dictionary<Map, MapCellSpace>();
            private readonly Dictionary<Map, Dictionary<Map, HashSet<int>>> contributions = new Dictionary<Map, Dictionary<Map, HashSet<int>>>();
            private readonly HashSet<Map> opticalDestinations = new HashSet<Map>();
            internal void Add(MapCellAddress cell)
            {
                if (!Components.ContainsKey(cell.Map)) return;
                if (!contributions.TryGetValue(Source, out var destinations)) contributions[Source] = destinations = new Dictionary<Map, HashSet<int>>();
                if (!destinations.TryGetValue(cell.Map, out var cells)) destinations[cell.Map] = cells = new HashSet<int>();
                cells.Add(cell.Map.cellIndices.CellToIndex(cell.Cell));
            }
            internal void ClearSource()
            {
                contributions.Remove(Source);
                opticalDestinations.Add(Source);
            }
            internal void Merge(FieldInfo field)
            {
                foreach (var source in contributions.Values)
                    foreach (var destination in source)
                    {
                        // SS optical mode replaces all ordinary visibility on its source map, including
                        // incoming neighbors' contributions computed earlier or later in this batch.
                        if (opticalDestinations.Contains(destination.Key)) continue;
                        var cells = (bool[])field.GetValue(Components[destination.Key]);
                        foreach (int index in destination.Value) cells[index] = true;
                    }
            }
        }

        private sealed class Calculation
        {
            internal SeamlessSurveillanceGrid Grid;
            internal Map Source;
            internal bool[] SourceVisible;
            internal IntVec3 Origin;
            internal bool High;
        }
        private static Calculation Prepare(IntVec3 origin, int radius, Map map, bool[] cells, bool high)
        {
            if (active == null || active.Source != map || !active.Spaces.TryGetValue(map, out var space)) return null;
            // Budget failure abandons the batch and restores native behavior, never truncates a sight radius.
            int diameter = checked(radius * 2 + 3);
            var bounds = new CellRect(checked(origin.x - radius - 1), checked(origin.z - radius - 1), diameter, diameter);
            bool reachesNeighbor = false;
            foreach (var placement in space.Placements)
                if (placement.Map != map && bounds.Overlaps(new CellRect(placement.OriginInReference.x, placement.OriginInReference.z,
                    placement.Map.Size.x, placement.Map.Size.z))) { reachesNeighbor = true; break; }
            if (!reachesNeighbor) return null;
            var grid = new SeamlessSurveillanceGrid(space, bounds);
            return new Calculation { Grid = grid, Source = map, SourceVisible = cells, Origin = origin, High = high };
        }
        private static void FieldPrefix(ref IntVec3 __0, int __1, ref Map __2, ref bool[] __3, bool __4, out Calculation __state)
        {
            __state = Prepare(__0, __1, __2, __3, __4);
            if (__state == null) return;
            __0 = __state.Grid.Addresses.ToGrid(__0); __2 = __state.Grid.Data; __3 = __state.Grid.Visible;
        }
        private static void ConePrefix(ref IntVec3 __0, int __1, ref Map __4, ref bool[] __5, bool __6, out Calculation __state)
        {
            __state = Prepare(__0, __1, __4, __5, __6);
            if (__state == null) return;
            __0 = __state.Grid.Addresses.ToGrid(__0); __4 = __state.Grid.Data; __5 = __state.Grid.Visible;
        }
        private static void FieldPostfix(Calculation __state)
        {
            if (__state == null) return;
            __state.Grid.Publish(__state.Source, __state.SourceVisible, __state.Origin, __state.High, active.Add);
        }

        private static void ScopeCenterPostfix(Map map, ref IntVec3 __result)
        {
            if (failed || Current.Game?.CurrentMap != map) return;
            try
            {
                if (!SeamlessSpatialQueries.TryCapture(map, out var space) || space.Placements.Count < 2) return;
                // An optical view may look outside the source rectangle. The snapshot, not clamping, determines coverage.
                __result = Find.CameraDriver.MapPosition;
            }
            catch (Exception error) { Fail(error); }
        }
        private static bool ScopePrefix(Map __0, bool[] __1)
        {
            // Drawing scopes may override Find.CurrentMap; optical input belongs only to the actual focus.
            if (!failed && Current.Game != null && Current.Game.CurrentMap != __0) return false;
            if (active == null || active.Source != __0) return true;
            if (Current.Game?.CurrentMap != __0 || !(bool)scopeActive.Invoke(null, new object[] { __0 })) return true;
            var pawn = (Pawn)scopePawn.GetValue(null);
            var center = Find.CameraDriver.MapPosition;
            int radius = Math.Max(1, (int)scopeRadius.GetValue(null));
            int separation = Convert.ToInt32(scopeKind.GetValue(null)) == 0 ? Math.Max(0, (int)scopeSeparation.GetValue(null)) : 0;
            int left = center.x - (separation + 1) / 2;
            int minX = Math.Min(pawn.Position.x, left - radius) - 1, maxX = Math.Max(pawn.Position.x, left + separation + radius) + 1;
            int minZ = Math.Min(pawn.Position.z, center.z - radius) - 1, maxZ = Math.Max(pawn.Position.z, center.z + radius) + 1;
            var grid = new SeamlessSurveillanceGrid(active.Spaces[__0], new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1));
            Array.Clear(__1, 0, __1.Length);
            active.ClearSource(); // Optical mode replaces this source's ordinary FOV, including its cross-map part.
            lastScopeCell.SetValue(null, center);
            void Circle(int x) => circle.Invoke(null, new object[] { grid.Data, grid.Addresses.ToGrid(pawn.Position),
                grid.Addresses.ToGrid(new IntVec3(x, 0, center.z)), radius, grid.Visible });
            Circle(left);
            if (separation != 0) Circle(left + separation);
            grid.Publish(__0, __1, pawn.Position, false, active.Add);
            return false;
        }
        private static bool RefreshScopePrefix(MapComponent __instance)
        {
            if (failed || active != null) return true;
            if (Current.Game?.CurrentMap != __instance.map) return false;
            try
            {
                if (!(bool)scopeMoved.Invoke(null, new object[] { __instance.map })
                    || !SeamlessSpatialQueries.TryCapture(__instance.map, out var space) || space.Placements.Count < 2) return true;
                RunBatch(__instance, space, false, null, Current.Game.GetComponent<SeamlessSurveillanceState>(), Find.TickManager.TicksGame);
                return false;
            }
            catch (Exception error) { Fail(error); return true; }
        }
    }

    public sealed class SeamlessSurveillanceState : GameComponent
    {
        internal readonly Dictionary<Map, int> LastPeriodic = new Dictionary<Map, int>();
        public SeamlessSurveillanceState(Game game) { }
    }
}
