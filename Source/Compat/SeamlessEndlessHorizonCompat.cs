#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal static partial class SeamlessEndlessHorizonCompat
    {
        internal const string PackageId = "jongsh.endlesshorizon";
        private const string TestedVersion = "1.0.0.3hf";
        private static readonly ConditionalWeakTable<Map, EndlessHorizonGeometry> geometries = new();
        private static readonly List<SeamlessTileGraph.NeighborInfo> neighbors = new();
        [ThreadStatic] private static EndlessHorizonGeometry drawing;
        private static Func<Map, bool> isActive;
        private static Func<Map, object> getState;
        private static Action<Map> invalidate;
        private static MethodInfo draw;
        private static FieldInfo settings, ringWidth, showFog, hideFog, hideRing, coastEnabled;
        private static ulong surfaceMarks, boundaryMarks;
        private static bool ready;

        internal static bool Drawing => drawing != null;

        internal static void Register()
        {
            if (!ModsConfig.IsActive(PackageId)) return;
            var harmony = new Harmony("RimExodus.EndlessHorizon");
            var patched = new List<MethodBase>();
            try
            {
                var mod = Type("EndlessHorizonMod");
                var version = ReadVersion(mod);
                var registry = Type("HorizonRegistry");
                isActive = Method(registry, "IsActive").CreateDelegate<Func<Map, bool>>();
                getState = Method(registry, "Get").CreateDelegate<Func<Map, object>>();
                invalidate = Method(registry, "Invalidate").CreateDelegate<Action<Map>>();
                draw = Method(Type("HorizonState"), "Draw");
                settings = Field(mod, "Settings");
                ringWidth = Field(settings.FieldType, "ringWidth");
                showFog = Field(settings.FieldType, "fog");
                coastEnabled = Field(settings.FieldType, "useCoastNoise");
                hideFog = Field(Type("HorizonDebug"), "HideFog");
                hideRing = Field(Type("HorizonDebug"), "HideRing");
                surfaceMarks = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings;
                boundaryMarks = surfaceMarks | (ulong)MapMeshFlagDefOf.FogOfWar;

                foreach (var patch in BindPatches())
                {
                    patched.Add(patch.Target);
                    harmony.Patch(patch.Target, prefix: patch.Prefix, postfix: patch.Postfix, transpiler: patch.Transpiler);
                }
                ready = true;
                var baseline = string.Equals(version, TestedVersion, StringComparison.Ordinal)
                    ? string.Empty
                    : $"; tested baseline {TestedVersion}";
                Log.Message($"[RimExodus] Bound Endless Horizon {version} seam rendering "
                    + $"({patched.Count} methods; runtime signatures validated{baseline}).");
            }
            catch (Exception ex)
            {
                foreach (var target in patched) harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
                Log.Warning($"[RimExodus] Endless Horizon seam rendering unavailable: {ex.GetBaseException().Message}");
            }
        }

        private static string ReadVersion(Type mod)
        {
            try
            {
                var field = AccessTools.Field(mod, "Version");
                if (field == null) return "unknown version";
                var value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                return value?.ToString() ?? "unknown version";
            }
            catch
            {
                // The version is diagnostic only. Runtime structure is the compatibility gate.
                return "unknown version";
            }
        }

        /// <summary>
        /// Own only the world-clipper phase. Horizon terrain is submitted later from
        /// <see cref="SeamlessTileRenderer.MapComponentDraw"/>, outside the clipper callback.
        /// </summary>
        internal static bool TryDrawClippers(Map map, Action<Map, int> drawClippers)
        {
            if (!ready || !SeamlessSpatialQueries.Available(map) || !SeamlessEdgeCells.HasSeamEdge(map)) return false;
            try
            {
                if (!isActive(map) || (bool)hideRing.GetValue(null)) return false;
                var nativeNeighbor = MapRenderPlan.IsNativeView;
                if (!nativeNeighbor && Current.Game.CurrentMap != map) return false;
                if (!TryPrepareGeometry(map, out var geometry, out var radius, out var fog)) return false;

                // REF owns the neighbor's display transform. Suppress its rectangular world
                // clipper, but retain the freshly prepared geometry for MapComponentDraw.
                if (nativeNeighbor) return true;

                drawing = geometry;
                // Like upstream EH, opaque far fog owns the exterior when enabled. A second
                // rectangular WorldClipper would cover neighboring maps after REF projection.
                if (!fog) drawClippers(map, radius);
                return true;
            }
            catch (Exception ex)
            {
                ready = false;
                geometries.Remove(map);
                try { invalidate(map); }
                catch (Exception cleanup) { Log.Warning($"[RimExodus] Endless Horizon cache cleanup failed: {cleanup.GetBaseException().Message}"); }
                Log.Warning($"[RimExodus] Endless Horizon seam rendering stopped: {ex.GetBaseException()}");
                return false;
            }
            finally
            {
                drawing = null;
            }
        }

        /// <summary>
        /// Submit Endless Horizon's terrain, water and atmospheric layers as map background.
        /// Loaded neighbors are already excluded by <see cref="EndlessHorizonGeometry"/> and
        /// remain owned by REF's real-map rendering path.
        /// </summary>
        internal static void DrawBackground(Map map)
        {
            if (!ready || map == null || !WorldRendererUtility.DrawingMap) return;
            var nativeView = MapRenderPlan.CurrentNativeView;
            if (MapRenderPlan.IsNativeView)
            {
                if (nativeView?.Source != map) return;
            }
            else if (Current.Game?.CurrentMap != map) return;
            if (!geometries.TryGetValue(map, out var geometry)) return;
            var restoreLight = false;
            var previousLight = default(Color);
            try
            {
                if (!isActive(map) || (bool)hideRing.GetValue(null)) return;
                if (MapRenderPlan.IsNativeView && SeamlessNativeRendering.TryGetFocusedSkyTint(out var focusedSkyTint))
                {
                    previousLight = MatBases.LightOverlay.color;
                    MatBases.LightOverlay.color = focusedSkyTint;
                    restoreLight = true;
                }
                drawing = geometry;
                draw.Invoke(getState(map), null);
            }
            catch (Exception ex)
            {
                ready = false;
                geometries.Remove(map);
                try { invalidate(map); }
                catch (Exception cleanup) { Log.Warning($"[RimExodus] Endless Horizon cache cleanup failed: {cleanup.GetBaseException().Message}"); }
                Log.Warning($"[RimExodus] Endless Horizon seam rendering stopped: {ex.GetBaseException()}");
            }
            finally
            {
                drawing = null;
                if (restoreLight) MatBases.LightOverlay.color = previousLight;
            }
        }

        internal static int VisibilityPadding(Map map)
        {
            if (!ready || map == null) return 0;
            try
            {
                if (!isActive(map) || (bool)hideRing.GetValue(null)) return 0;
                var options = settings.GetValue(null);
                var radius = (int)ringWidth.GetValue(options);
                var fog = (bool)showFog.GetValue(options) && !(bool)hideFog.GetValue(null);
                return fog ? radius + 16 : radius;
            }
            catch (Exception ex)
            {
                Log.WarningOnce($"[RimExodus] Endless Horizon visibility query failed: {ex.GetBaseException().Message}",
                    0x52454856);
                return 0;
            }
        }

        private static bool TryPrepareGeometry(Map map, out EndlessHorizonGeometry geometry,
            out int radius, out bool fog)
        {
            geometry = null;
            radius = 0;
            fog = false;
            var options = settings.GetValue(null);
            radius = (int)ringWidth.GetValue(options);
            fog = (bool)showFog.GetValue(options) && !(bool)hideFog.GetValue(null);
            neighbors.Clear();
            try
            {
                SeamlessTileGraph.PopulateNeighbors(map, neighbors);
                neighbors.RemoveAll(info => !SeamlessSpatialQueries.Available(info.map));
                if (!geometries.TryGetValue(map, out geometry) || !geometry.Matches(radius + 16, neighbors))
                {
                    geometry = new EndlessHorizonGeometry(map, map.Tile, radius + 16, neighbors);
                    invalidate(map);
                    geometries.Remove(map);
                    geometries.Add(map, geometry);
                }
                return true;
            }
            finally
            {
                neighbors.Clear();
            }
        }

        // HorizonState.Draw normally consumes REF's map-clipped LocalView. EH alone needs the
        // uncut source-local camera rect so chunks beyond a neighboring map's rectangle can draw.
        private static CellRect HorizonView(CameraDriver camera)
        {
            if (drawing != null && MapRenderPlan.IsNativeView
                && SeamlessNativeRendering.TryGetEndlessHorizonView(drawing.Map, out var view)) return view;
            return camera.CurrentViewRect;
        }

        private static Type Type(string name) => AccessTools.TypeByName("EndlessHorizon." + name)
            ?? throw new TypeLoadException("EndlessHorizon." + name);
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
            ?? throw new MissingFieldException(type.FullName, name);
        private static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name)
            ?? throw new MissingMethodException(type.FullName, name);
        private static MethodInfo Iterator(Type type, string name) => Method(
            Method(type, name).GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType
                ?? throw new MissingMethodException(type.FullName, name + ".MoveNext"), "MoveNext");
        private static HarmonyMethod Hook(string name) => name == null ? null
            : new HarmonyMethod(Method(typeof(SeamlessEndlessHorizonCompat), name));

        private static bool OutsideFieldCell(IntVec3 cell, Map map) => drawing != null && drawing.Map == map
            ? !drawing.DrawCell(cell, drawing.Radius) : cell.InBounds(map);
        private static bool PlayableCell(IntVec3 cell, Map map) => drawing != null && drawing.Map == map
            ? drawing.Inside(cell) : cell.InBounds(map);

        private static bool ClampPrefix(IntVec3 c, ref IntVec3 __result)
        {
            if (drawing == null) return true;
            __result = drawing.Nearest(c);
            return false;
        }

        private static void CoversPostfix(IntVec3 c, ref bool __result)
        {
            if (__result && drawing != null)
                __result = drawing.Inside(c) || drawing.DrawCell(c, drawing.Radius);
        }

        private static bool OutsideFogPrefix(IntVec3 c, ref bool __result)
        {
            if (drawing == null) return true;
            __result = drawing.DrawCell(c, drawing.Radius) && drawing.Nearest(c).Fogged(drawing.Map);
            return false;
        }

        private static void MeshDirtyPostfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            const int distance = 3;
            if (!ready || (dirtyFlags & boundaryMarks) == 0 || (bool)meshDirtySuppressed.GetValue(null)) return;
            var map = (Map)drawerMap.GetValue(__instance);
            if (map == null || !geometries.TryGetValue(map, out var geometry) || !geometry.Inside(loc)) return;
            var size = map.Size;
            // The original postfix already processes the rectangular edge.
            if (loc.x <= 2 || loc.z <= 2 || loc.x >= size.x - 3 || loc.z >= size.z - 3) return;
            foreach (var point in CellRect.CenteredOn(loc, distance))
            {
                if (geometry.Inside(point)) continue;
                var state = peek.Invoke(null, new object[] { map });
                if (state != null) markDirty.Invoke(state, new object[] { loc, (dirtyFlags & surfaceMarks) != 0 });
                break;
            }
        }
    }
}
#endif
