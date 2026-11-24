using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>Consumer selection only; the native update/draw scopes belong to REF.</summary>
    public sealed class SeamlessNativeRendering : GameComponent
    {
        private readonly List<SeamlessTileGraph.NeighborInfo> neighbors = new List<SeamlessTileGraph.NeighborInfo>();
#if RIMWORLD_1_7
        private readonly Dictionary<Map, CellRect> endlessHorizonViews = new Dictionary<Map, CellRect>();
        private readonly HashSet<Map> separatedSkyMaps = new HashSet<Map>();
        private readonly List<MapRenderPlan> endlessHorizonPlans = new List<MapRenderPlan>();
        private int endlessHorizonViewFrame = -1;
#endif
        private static Color focusedSkyTint = Color.white;
        private static int focusedSkyTintFrame = int.MinValue;
        private bool registered;
        private bool wasEnabled;
        public static bool Enabled => RimExodusMod.Settings?.nativeNeighborRendering == true;
#if RIMWORLD_1_7
        private static readonly System.Func<MapDrawCommand, bool?> ExcludeHorizon = _ =>
            SeamlessEndlessHorizonCompat.Drawing ? false : null;
#endif
        public SeamlessNativeRendering(Game game) { }

        private void PrepareFrame()
        {
#if RIMWORLD_1_7
            separatedSkyMaps.Clear();
#endif
            var rendering = MapRuntime.Current.Rendering;
            if (!registered)
            {
                rendering.SetProvider(SeamlessFramework.Owner, CollectViews);
                registered = true;
            }
            if (wasEnabled != Enabled)
            {
                rendering.Clear();
                SeamlessTileRenderer.ReleaseForRenderPathChange();
                wasEnabled = Enabled;
            }
        }
        private void CollectViews(Map host, List<MapRenderPlan> output)
        {
#if RIMWORLD_1_7
            endlessHorizonViews.Clear();
            endlessHorizonPlans.Clear();
            endlessHorizonViewFrame = Time.frameCount;
#endif
            if (!Enabled) return;
            var hostTile = SeamlessTileRegistry.GetMapWorldTile(host);
            if (!hostTile.Valid) return;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(host, neighbors);
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
#if RIMWORLD_1_7
            // REF clips LocalView to the source rectangle. EH's public external-draw API needs
            // the uncut source-local camera rect so its ring remains visible beyond that rectangle.
            endlessHorizonViews[host] = view;
            endlessHorizonPlans.Add(CreateView(host, hostTile, Matrix4x4.identity, view));
#endif
            foreach (var neighbor in neighbors)
            {
                if (neighbor.map == null || neighbor.map.Disposed || IncrementalMapGenerator.IsGenerating(neighbor.map)
                    || SeamlessDormancyManager.IsDormant(neighbor.map)
                    || !SeamlessViewProjection.TryProject(neighbor.map, Vector3.zero, host, out var offset)) continue;
                var transform = Matrix4x4.Translate(offset);
#if RIMWORLD_1_7
                endlessHorizonPlans.Add(CreateView(neighbor.map, neighbor.map.Tile, transform, view));
#else
                var localView = new MapTransform(transform).LocalView(view);
                var visibleBounds = CellRect.WholeMap(neighbor.map);
                if (!localView.Overlaps(visibleBounds)) continue;
                output.Add(CreateView(neighbor.map, neighbor.map.Tile, transform, view));
#endif
            }
#if RIMWORLD_1_7
            // Every EH domain must be derived from the same frozen host-space plan set. Pairwise
            // source offsets are not transitive under the world projection, so independently
            // calculated neighbor regions can overlap or leave cracks between two outer maps.
            SeamlessEndlessHorizonCompat.PrepareCompositeRegions(host, endlessHorizonPlans);
            for (var i = 0; i < endlessHorizonPlans.Count; i++)
            {
                var plan = endlessHorizonPlans[i];
                var localView = plan.Transform.LocalView(view);
                if (plan.Source != host)
                {
                    var visibleBounds = CellRect.WholeMap(plan.Source)
                        .ExpandedBy(SeamlessEndlessHorizonCompat.VisibilityPadding(plan.Source));
                    if (!localView.Overlaps(visibleBounds)) continue;
                    endlessHorizonViews[plan.Source] = localView;
                }
                // The focused map's full-rectangle mod overlays must not cover neighboring polygons either.
                output.Add(plan);
            }
#else
            // The focused map's full-rectangle mod overlays must not cover the neighboring polygons either.
            output.Insert(0, CreateView(host, hostTile, Matrix4x4.identity, view));
#endif
        }

#if RIMWORLD_1_7
        internal static bool TryGetEndlessHorizonView(Map map, out CellRect view)
        {
            view = default;
            var component = Current.Game?.GetComponent<SeamlessNativeRendering>();
            return component != null && component.endlessHorizonViewFrame == Time.frameCount
                && component.endlessHorizonViews.TryGetValue(map, out view);
        }

#endif

        internal static bool TryGetSeparatedSkyTint(Map map, out Color tint)
        {
#if RIMWORLD_1_7
            tint = Color.white;
            if (!Enabled || map == null || !WorldRendererUtility.DrawingMap) return false;
            var component = Current.Game?.GetComponent<SeamlessNativeRendering>();
            if (component == null || !component.separatedSkyMaps.Contains(map)) return false;
            tint = map.Biome != null && map.Biome.disableSkyLighting
                ? new Color(1f, 1f, 1f, 0f)
                : map.skyManager.CurSky.colors.sky;
            return true;
#else
            tint = Color.white;
            return false;
#endif
        }

        internal static bool TryGetFocusedSkyTint(out Color color)
        {
            color = focusedSkyTint;
            return focusedSkyTintFrame >= Time.frameCount - 1;
        }

        private static MapRenderPlan CreateView(Map map, PlanetTile tile, Matrix4x4 transform, CellRect view)
            => new MapRenderPlan(map, transform, view, new MapRenderOptions
            {
                OverlayFootprint = SeamlessPolygonGeometry.GetCachedPolygonVertices(tile, map.Size.x),
#if RIMWORLD_1_7
                OverlayClipSelector = ExcludeHorizon
#else
                OverlayClipSelector = null
#endif
            });
        [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
        private static class PreparePatch
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyBefore("RimExodus.Maps")]
            private static void Prefix() => Current.Game.GetComponent<SeamlessNativeRendering>().PrepareFrame();
        }

        [HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
        private static class NeighborSkyPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                if (!Enabled || Current.Game == null) return;
                var view = MapRenderPlan.CurrentNativeView;
                if (view == null) return;

                var sourceTint = MatBases.LightOverlay.color;

                // CurrentNativeView includes the focused identity plan as well as neighbor plans.
                // Capture the source map's own tint before either the EH split path removes the sky
                // channel or the ordinary native path normalizes neighbors to the focused tint.
                if (view.Source == Current.Game.CurrentMap)
                {
                    focusedSkyTint = sourceTint;
                    focusedSkyTintFrame = Time.frameCount;
                }
#if RIMWORLD_1_7
                // EH and the map both submit LightOverlay meshes. Keep those meshes as the
                // glow/roof channel only, then draw one sky plane over their shared DrawArea.
                // This also neutralizes EH's rectangular far-light frame without identifying
                // or patching any internal EH type.
                if (SeamlessEndlessHorizonCompat.TryPrepareSeparatedLighting(view.Source))
                {
                    Current.Game.GetComponent<SeamlessNativeRendering>().separatedSkyMaps.Add(view.Source);
                    MatBases.LightOverlay.color = new Color(1f, 1f, 1f, 0f);
                    return;
                }
#endif
                else MatBases.LightOverlay.color = TryGetFocusedSkyTint(out var tint)
                    ? tint
                    : new Color(1f, 1f, 1f, 0f);
            }
        }
    }
}
