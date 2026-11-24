#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld.Planet;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Endless Horizon 1.0.0.4+ public API adapter. RimExodus supplies its playable seam region,
    /// splits neighboring draw domains, and owns when each visible map submits its ring.
    /// Endless Horizon remains the sole owner of horizon generation and rendering.
    /// </summary>
    internal static class SeamlessEndlessHorizonCompat
    {
        internal const string PackageId = "jongsh.endlesshorizon";
        private const string Owner = "rimexodus.seamlessworld";
        private const int RequiredApiVersion = 1;

        private delegate bool SetRegionDelegate(Map map, string owner, Predicate<IntVec3> playable,
            IReadOnlyList<Vector2> drawArea);

        private static readonly ConditionalWeakTable<Map, MapRegion> regions = new();
        private static readonly List<SeamlessTileGraph.NeighborInfo> neighbors = new();
        private static readonly Dictionary<Map, IReadOnlyList<Vector2>> compositeDrawAreas = new();
        private static readonly List<CompositePlanKey> compositePlans = new();
        private static int compositeDrawAreaFrame = -1;
        private static Map compositeDrawAreaHost;
        private static Material skyTintMaterial;
        [ThreadStatic] private static bool drawing;

        private static Func<Map, bool> isActive;
        private static Func<Map, bool> fogShown;
        private static Func<Map, int> ringWidth;
        private static Func<Map, int> drawExtent;
        private static SetRegionDelegate setRegion;
        private static Func<Map, string, bool> clearRegion;
        private static Func<Map, string, bool, bool> setExternalDrawing;
        private static Func<Map, CellRect, bool> draw;
        private static bool ready;

        internal static bool Drawing => drawing;

        /// <summary>
        /// Enters the public-API region before the map submits its lighting sections. When true,
        /// the caller must submit those sections as glow/roof data only and let <see cref="DrawSkyTint"/>
        /// own the sky channel for this map's complete EH draw domain.
        /// </summary>
        internal static bool TryPrepareSeparatedLighting(Map map) => EnsureConfigured(map);

        /// <summary>
        /// Freeze one host-space Voronoi partition for every map that can participate in this
        /// composite view. The same display-space bisector is transformed back into each source,
        /// so outer neighbors cannot derive conflicting seams from their own integer link offsets.
        /// </summary>
        internal static void PrepareCompositeRegions(Map host, IReadOnlyList<MapRenderPlan> plans)
        {
            compositeDrawAreaFrame = Time.frameCount;
            if (CompositePlansMatch(host, plans)) return;
            compositeDrawAreaHost = host;
            compositeDrawAreas.Clear();
            compositePlans.Clear();
            if (host == null || plans == null || plans.Count == 0) return;
            for (var i = 0; i < plans.Count; i++)
                compositePlans.Add(new CompositePlanKey(plans[i].Source, plans[i].Transform.Matrix));
            foreach (var pair in BuildCompositeDrawAreas(host, plans))
                compositeDrawAreas[pair.Key] = pair.Value;
        }

        private static bool CompositePlansMatch(Map host, IReadOnlyList<MapRenderPlan> plans)
        {
            if (host != compositeDrawAreaHost || plans == null || plans.Count != compositePlans.Count) return false;
            for (var i = 0; i < plans.Count; i++)
                if (plans[i].Source != compositePlans[i].Map
                    || !plans[i].Transform.Matrix.Equals(compositePlans[i].Transform)) return false;
            return true;
        }

        private static Dictionary<Map, IReadOnlyList<Vector2>> BuildCompositeDrawAreas(Map host,
            IReadOnlyList<MapRenderPlan> plans)
        {
            const float far = 2000f;
            var result = new Dictionary<Map, IReadOnlyList<Vector2>>();
            var centers = new Vector2[plans.Count];
            for (var i = 0; i < plans.Count; i++)
            {
                var map = plans[i].Source;
                var point = plans[i].Transform.ToDisplay(new Vector3(map.Size.x * .5f, 0f, map.Size.z * .5f));
                centers[i] = new Vector2(point.x, point.z);
            }
            for (var i = 0; i < plans.Count; i++)
            {
                var display = new List<Vector2>
                {
                    new(-far, -far),
                    new(host.Size.x + far, -far),
                    new(host.Size.x + far, host.Size.z + far),
                    new(-far, host.Size.z + far)
                };
                for (var j = 0; j < plans.Count && display.Count >= 3; j++)
                {
                    if (i == j) continue;
                    var normal = centers[j] - centers[i];
                    if (normal.sqrMagnitude < .001f) continue;
                    display = Clip(display, normal,
                        (centers[j].sqrMagnitude - centers[i].sqrMagnitude) * .5f);
                }
                var local = new List<Vector2>(display.Count);
                foreach (var point in display)
                {
                    var mapped = plans[i].Transform.ToLocal(new Vector3(point.x, 0f, point.y));
                    local.Add(new Vector2(mapped.x, mapped.z));
                }
                result[plans[i].Source] = local;
            }
            return result;
        }

        internal static void Register()
        {
            if (!ModsConfig.IsActive(PackageId)) return;
            try
            {
                var version = BindApi();
                ready = true;
                Log.Message($"[RimExodus] Bound Endless Horizon API v{version} seam rendering "
                    + "(public API; activates with native neighbor rendering; no Harmony patches).");
            }
            catch (Exception ex)
            {
                ready = false;
                Log.Warning("[RimExodus] Endless Horizon seam rendering unavailable: API v1 "
                    + $"(Endless Horizon 1.0.0.4+) is required. {ex.GetBaseException().Message}");
            }
        }

        private static int BindApi()
        {
            var info = ApiType("HorizonInfo");
            var space = ApiType("HorizonSpace");
            var view = ApiType("HorizonView");
            var extensions = ApiType("HorizonExtensions");
            var version = (int)(info.GetProperty("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? throw new MissingMemberException(info.FullName, "Version"));
            if (version < RequiredApiVersion)
                throw new NotSupportedException($"Endless Horizon API v{version}; expected v{RequiredApiVersion} or later");

            var boundIsActive = Bind<Func<Map, bool>>(info, "IsActive", typeof(Map));
            var boundFogShown = Bind<Func<Map, bool>>(info, "FogShown", typeof(Map));
            var boundRingWidth = Bind<Func<Map, int>>(info, "RingWidth", typeof(Map));
            var boundDrawExtent = Bind<Func<Map, int>>(space, "DrawExtent", typeof(Map));
            var boundSetRegion = Bind<SetRegionDelegate>(space, "SetRegion", typeof(Map), typeof(string),
                typeof(Predicate<IntVec3>), typeof(IReadOnlyList<Vector2>));
            var boundClearRegion = Bind<Func<Map, string, bool>>(space, "ClearRegion", typeof(Map), typeof(string));
            var boundSetExternal = Bind<Func<Map, string, bool, bool>>(view, "SetExternalDrawing",
                typeof(Map), typeof(string), typeof(bool));
            var boundDraw = Bind<Func<Map, CellRect, bool>>(view, "Draw", typeof(Map), typeof(CellRect));
            var boundRegisterDraw = Bind<Func<string, string, Action<Map, CellRect>, bool>>(extensions,
                "RegisterDraw", typeof(string), typeof(string), typeof(Action<Map, CellRect>));
            if (!boundRegisterDraw(Owner, "beforeFog", DrawSkyTintStage))
                throw new InvalidOperationException("Endless Horizon refused the beforeFog sky-tint stage.");

            // Publish only after the complete frozen-v1 surface has bound successfully.
            isActive = boundIsActive;
            fogShown = boundFogShown;
            ringWidth = boundRingWidth;
            drawExtent = boundDrawExtent;
            setRegion = boundSetRegion;
            clearRegion = boundClearRegion;
            setExternalDrawing = boundSetExternal;
            draw = boundDraw;
            return version;
        }

        /// <summary>
        /// Own only the world-clipper phase. Horizon meshes are submitted later from
        /// <see cref="SeamlessTileRenderer.MapComponentDraw"/> through HorizonView.Draw.
        /// </summary>
        internal static bool TryDrawClippers(Map map, Action<Map, int> drawClippers)
        {
            if (!EnsureConfigured(map)) return false;
            try
            {
                // A neighboring map's rectangular clipper would cover another map's API-clipped horizon.
                // Opaque EH fog closes every draw domain; without fog the focused map keeps the old fallback.
                if (!MapRenderPlan.IsNativeView && Current.Game?.CurrentMap == map && !fogShown(map))
                    drawClippers(map, ringWidth(map));
                return true;
            }
            catch (Exception ex)
            {
                Warn(map, "clipper", ex);
                return false;
            }
        }

        /// <summary>Submit one map's API-owned ring in that map's local view.</summary>
        internal static void DrawBackground(Map map)
        {
            if (!ready || map == null || !WorldRendererUtility.DrawingMap) return;

            CellRect view;
            var native = MapRenderPlan.CurrentNativeView;
            if (native != null)
            {
                if (native.Source != map) return;
                if (!SeamlessNativeRendering.TryGetEndlessHorizonView(map, out view)) view = native.LocalView;
            }
            else
            {
                if (Current.Game?.CurrentMap != map) return;
                view = Find.CameraDriver.CurrentViewRect;
            }

            if (!EnsureConfigured(map)) return;
            try
            {
                drawing = true;
                if (!draw(map, view) && isActive(map))
                    Log.WarningOnce($"[RimExodus] Endless Horizon refused external drawing for map {map.uniqueID}.",
                        0x52454844 ^ map.uniqueID);
            }
            catch (Exception ex)
            {
                Warn(map, "draw", ex);
            }
            finally
            {
                drawing = false;
            }
        }

        /// <summary>
        /// Submit exactly one sky-tint plane for this map, clipped to the same public-API draw
        /// domain as its EH terrain and fog. Native and EH lighting meshes have already been
        /// reduced to glow/roof data, so this plane is the sole sky channel on either side of
        /// the playable/void boundary. Neighbor domains share one bisector and cannot overlap.
        /// </summary>
        internal static void DrawSkyTint(Map map, Color tint)
        {
            if (!DebugViewSettings.drawLightingOverlay || map == null
                || !regions.TryGetValue(map, out var region) || region.SkyTintMesh == null) return;

            var previousDrawing = drawing;
            try
            {
                drawing = true; // The mesh is already clipped to DrawArea; REF must only transform it.
                var material = SkyTintMaterial;
                material.color = tint;
                Graphics.DrawMesh(region.SkyTintMesh, Matrix4x4.identity, material, 0);
            }
            catch (Exception ex)
            {
                Warn(map, "sky tint", ex);
            }
            finally
            {
                drawing = previousDrawing;
            }
        }

        private static Material SkyTintMaterial
        {
            get
            {
                if (skyTintMaterial != null) return skyTintMaterial;
                // The sky pass is the base beneath native/EH glow and roof vertex data. Reusing
                // the exact LightOverlay material/queue left coplanar focused-map submissions at
                // the mercy of Waylith's batching order, while the unobstructed horizon still drew.
                skyTintMaterial = new Material(MatBases.LightOverlay)
                {
                    name = "RimExodus_EndlessHorizonSkyTint",
                    renderQueue = Math.Max(0, MatBases.LightOverlay.renderQueue - 1)
                };
                return skyTintMaterial;
            }
        }

        private static void DrawSkyTintStage(Map map, CellRect view)
        {
            if (SeamlessNativeRendering.TryGetSeparatedSkyTint(map, out var tint)) DrawSkyTint(map, tint);
        }

        internal static int VisibilityPadding(Map map)
        {
            if (!EnsureConfigured(map)) return 0;
            try { return Math.Max(0, drawExtent(map)); }
            catch (Exception ex)
            {
                Warn(map, "visibility", ex);
                return 0;
            }
        }

        private static bool EnsureConfigured(Map map)
        {
            if (!ready || map == null) return false;
            // EH's multi-map recipe requires every visible map to receive Draw. Only REF's native
            // rendering path provides those callbacks; the retiring legacy compositor must remain
            // entirely outside this compatibility adapter.
            if (!SeamlessNativeRendering.Enabled)
            {
                Release(map);
                return false;
            }
            if (!SeamlessSpatialQueries.Available(map) || !SeamlessEdgeCells.HasSeamEdge(map))
            {
                Release(map);
                return false;
            }
            try
            {
                if (!isActive(map))
                {
                    Release(map);
                    return false;
                }
                var tile = SeamlessTileRegistry.GetMapWorldTile(map);
                if (!tile.Valid) return false;

                neighbors.Clear();
                SeamlessTileGraph.PopulateNeighbors(map, neighbors);
                neighbors.RemoveAll(info => !SeamlessSpatialQueries.Available(info.map));
                var drawArea = compositeDrawAreaFrame == Time.frameCount
                    && compositeDrawAreaHost == Current.Game?.CurrentMap
                    && compositeDrawAreas.TryGetValue(map, out var prepared)
                        ? prepared
                        : null;
                regions.TryGetValue(map, out var existing);
                if (existing != null && existing.Matches(tile, neighbors, drawArea)) return true;

                var next = new MapRegion(map, tile, neighbors, drawArea);
                if (!setRegion(map, Owner, next.Playable, next.DrawArea))
                {
                    next.Dispose();
                    Log.WarningOnce($"[RimExodus] Endless Horizon rejected the seamless region for map {map.uniqueID}.",
                        0x52454852 ^ map.uniqueID);
                    return false;
                }
                if (!setExternalDrawing(map, Owner, true))
                {
                    next.Dispose();
                    clearRegion(map, Owner);
                    Log.WarningOnce($"[RimExodus] Endless Horizon external drawing is already owned for map {map.uniqueID}.",
                        0x52454845 ^ map.uniqueID);
                    return false;
                }
                existing?.Dispose();
                regions.Remove(map);
                regions.Add(map, next);
                return true;
            }
            catch (Exception ex)
            {
                Warn(map, "region", ex);
                return false;
            }
            finally
            {
                neighbors.Clear();
            }
        }

        private static void Release(Map map)
        {
            if (!regions.TryGetValue(map, out var region)) return;
            try { setExternalDrawing(map, Owner, false); }
            catch (Exception ex) { Warn(map, "release drawing", ex); }
            try { clearRegion(map, Owner); }
            catch (Exception ex) { Warn(map, "release region", ex); }
            region.Dispose();
            regions.Remove(map);
        }

        private static void Warn(Map map, string phase, Exception ex)
        {
            var id = map?.uniqueID ?? -1;
            Log.WarningOnce($"[RimExodus] Endless Horizon API {phase} failed for map {id}: "
                + ex.GetBaseException().Message, 0x52454800 ^ id ^ phase.GetHashCode());
        }

        private static Type ApiType(string name)
        {
            var fullName = "EndlessHorizon.Api." + name;
            var type = GenTypes.GetTypeInAnyAssembly(fullName);
            if (type != null) return type;
            // GenTypes caches assembly scans. This fallback also supports a late-loaded EH assembly
            // without reaching past the documented public API namespace.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            throw new TypeLoadException(fullName);
        }

        private static T Bind<T>(Type type, string name, params Type[] parameters) where T : Delegate
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null)
                ?? throw new MissingMethodException(type.FullName, name);
            return (T)Delegate.CreateDelegate(typeof(T), method);
        }

        /// <summary>Immutable inputs supplied to EH's frozen v1 region contract.</summary>
        private sealed class MapRegion : IDisposable
        {
            private readonly PlanetTile tile;
            private readonly int sizeX;
            private readonly int sizeZ;
            private readonly SeamlessPolygonGeometry.SeamBandInfo band;
            private readonly List<NeighborKey> joined;

            internal readonly IReadOnlyList<Vector2> DrawArea;
            internal readonly Mesh SkyTintMesh;

            internal MapRegion(Map map, PlanetTile tile, List<SeamlessTileGraph.NeighborInfo> neighbors,
                IReadOnlyList<Vector2> drawArea = null)
            {
                this.tile = tile;
                sizeX = map.Size.x;
                sizeZ = map.Size.z;
                band = SeamlessPolygonGeometry.BuildSeamBand(tile, sizeX);
                joined = new List<NeighborKey>(neighbors.Count);
                foreach (var neighbor in neighbors) joined.Add(new NeighborKey(neighbor.map, neighbor.offset));
                DrawArea = drawArea ?? BuildDrawArea(map, neighbors);
                SkyTintMesh = BuildSkyTintMesh(DrawArea);
            }

            internal bool Playable(IntVec3 cell) => cell.x >= 0 && cell.z >= 0 && cell.x < sizeX && cell.z < sizeZ
                && !SeamlessPolygonGeometry.IsVoidCell(band, tile, sizeX, cell);

            internal bool Matches(PlanetTile currentTile, List<SeamlessTileGraph.NeighborInfo> current,
                IReadOnlyList<Vector2> drawArea)
            {
                if (currentTile != tile || current.Count != joined.Count) return false;
                for (var i = 0; i < current.Count; i++)
                    if (current[i].map != joined[i].Map || current[i].offset != joined[i].Offset) return false;
                if (drawArea == null) return true;
                if (drawArea.Count != DrawArea.Count) return false;
                for (var i = 0; i < drawArea.Count; i++)
                    if ((drawArea[i] - DrawArea[i]).sqrMagnitude > .000001f) return false;
                return true;
            }

            private static IReadOnlyList<Vector2> BuildDrawArea(Map map,
                List<SeamlessTileGraph.NeighborInfo> neighbors)
            {
                const float far = 2000f;
                var polygon = new List<Vector2>
                {
                    new(-far, -far),
                    new(map.Size.x + far, -far),
                    new(map.Size.x + far, map.Size.z + far),
                    new(-far, map.Size.z + far)
                };
                var center = new Vector2(map.Size.x * .5f, map.Size.z * .5f);
                foreach (var neighbor in neighbors)
                {
                    if (!SeamlessViewProjection.TryProject(neighbor.map, Vector3.zero, map, out var offset)) continue;
                    var other = new Vector2(neighbor.map.Size.x * .5f + offset.x,
                        neighbor.map.Size.z * .5f + offset.z);
                    var normal = other - center;
                    if (normal.sqrMagnitude < .001f) continue;
                    polygon = Clip(polygon, normal, (other.sqrMagnitude - center.sqrMagnitude) * .5f);
                }
                return polygon;
            }

            private static Mesh BuildSkyTintMesh(IReadOnlyList<Vector2> polygon)
            {
                if (polygon == null || polygon.Count < 3) return null;
                // Keep the base tint just behind the coplanar native/EH glow meshes. The dedicated
                // lower render queue supplies deterministic ordering; the small altitude bias also
                // prevents equal-depth rejection on the focused map without moving to another layer.
                var y = AltitudeLayer.LightingOverlay.AltitudeFor(-0.1f);
                var vertices = new List<Vector3>(polygon.Count);
                var colors = new List<Color32>(polygon.Count);
                var triangles = new List<int>((polygon.Count - 2) * 3);
                var clear = new Color32(0, 0, 0, 0);
                var min = new Vector3(float.MaxValue, y, float.MaxValue);
                var max = new Vector3(float.MinValue, y, float.MinValue);
                for (var i = 0; i < polygon.Count; i++)
                {
                    var point = new Vector3(polygon[i].x, y, polygon[i].y);
                    vertices.Add(point);
                    colors.Add(clear);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
                // BuildDrawArea is counter-clockwise; reverse each fan triangle for the top face.
                for (var i = 1; i + 1 < polygon.Count; i++)
                {
                    triangles.Add(0);
                    triangles.Add(i + 1);
                    triangles.Add(i);
                }
                var mesh = new Mesh { name = "RimExodus_EndlessHorizonSkyTint" };
                mesh.SetVertices(vertices);
                mesh.SetColors(colors);
                mesh.SetTriangles(triangles, 0);
                mesh.bounds = new Bounds((min + max) * .5f, max - min + new Vector3(0f, 1f, 0f));
                return mesh;
            }

            public void Dispose()
            {
                if (SkyTintMesh != null) UnityEngine.Object.Destroy(SkyTintMesh);
            }

            private readonly struct NeighborKey
            {
                internal readonly Map Map;
                internal readonly IntVec3 Offset;
                internal NeighborKey(Map map, IntVec3 offset)
                {
                    Map = map;
                    Offset = offset;
                }
            }
        }

        private static List<Vector2> Clip(List<Vector2> polygon, Vector2 normal, float limit)
        {
            const float epsilon = .001f;
            var clipped = new List<Vector2>();
            if (polygon.Count == 0) return clipped;
            var previous = polygon[polygon.Count - 1];
            var previousDistance = limit - Vector2.Dot(normal, previous);
            foreach (var point in polygon)
            {
                var distance = limit - Vector2.Dot(normal, point);
                if ((distance >= -epsilon) != (previousDistance >= -epsilon))
                {
                    var t = previousDistance / (previousDistance - distance);
                    clipped.Add(Vector2.LerpUnclamped(previous, point, t));
                }
                if (distance >= -epsilon) clipped.Add(point);
                previous = point;
                previousDistance = distance;
            }
            return clipped;
        }

        private readonly struct CompositePlanKey
        {
            internal readonly Map Map;
            internal readonly Matrix4x4 Transform;
            internal CompositePlanKey(Map map, Matrix4x4 transform)
            {
                Map = map;
                Transform = transform;
            }
        }
    }
}
#endif
