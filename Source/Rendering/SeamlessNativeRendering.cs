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
        private int endlessHorizonViewFrame = -1;
        private static Color focusedSkyTint = Color.white;
        private static int focusedSkyTintFrame = int.MinValue;
#endif
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
            endlessHorizonViewFrame = Time.frameCount;
#endif
            if (!Enabled) return;
            var hostTile = SeamlessTileRegistry.GetMapWorldTile(host);
            if (!hostTile.Valid) return;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(host, neighbors);
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            // The focused map's full-rectangle mod overlays must not cover the neighboring polygons either.
            output.Add(CreateView(host, hostTile, Matrix4x4.identity, view));
            foreach (var neighbor in neighbors)
            {
                if (neighbor.map == null || neighbor.map.Disposed || IncrementalMapGenerator.IsGenerating(neighbor.map)
                    || SeamlessDormancyManager.IsDormant(neighbor.map)
                    || !SeamlessViewProjection.TryProject(neighbor.map, Vector3.zero, host, out var offset)) continue;
                var transform = Matrix4x4.Translate(offset);
                var localView = new MapTransform(transform).LocalView(view);
#if RIMWORLD_1_7
                var visibleBounds = CellRect.WholeMap(neighbor.map)
                    .ExpandedBy(SeamlessEndlessHorizonCompat.VisibilityPadding(neighbor.map));
#else
                var visibleBounds = CellRect.WholeMap(neighbor.map);
#endif
                if (!localView.Overlaps(visibleBounds)) continue;
#if RIMWORLD_1_7
                endlessHorizonViews[neighbor.map] = localView;
#endif
                output.Add(CreateView(neighbor.map, neighbor.map.Tile, transform, view));
            }
        }

#if RIMWORLD_1_7
        internal static bool TryGetEndlessHorizonView(Map map, out CellRect view)
        {
            view = default;
            var component = Current.Game?.GetComponent<SeamlessNativeRendering>();
            return component != null && component.endlessHorizonViewFrame == Time.frameCount
                && component.endlessHorizonViews.TryGetValue(map, out view);
        }

        internal static bool TryGetFocusedSkyTint(out Color color)
        {
            color = focusedSkyTint;
            return focusedSkyTintFrame >= Time.frameCount - 1;
        }
#endif

        private static MapRenderPlan CreateView(Map map, PlanetTile tile, Matrix4x4 transform, CellRect view)
            => new MapRenderPlan(map, transform, view, new MapRenderOptions
            {
                OverlayFootprint = SeamlessPolygonGeometry.GetCachedPolygonVertices(tile, map.Size.x),
#if RIMWORLD_1_7
                OverlayClipSelector = SelectOverlayClip(tile)
#else
                OverlayClipSelector = SeamlessGravTideCompat.GetOverlayClipSelector(tile)
#endif
            });

#if RIMWORLD_1_7
        private static System.Func<MapDrawCommand, bool?> SelectOverlayClip(PlanetTile tile)
        {
            var select = SeamlessGravTideCompat.GetOverlayClipSelector(tile);
            return select == null ? ExcludeHorizon : command => SeamlessEndlessHorizonCompat.Drawing ? false : select(command);
        }
#endif
        private void DrawSkyTint()
        {
            if (!Enabled || !WorldRendererUtility.DrawingMap || LongEventHandler.ShouldWaitForEvent) return;
            var host = Find.CurrentMap;
            if (host == null || !SeamlessTileRegistry.GetMapWorldTile(host).Valid) return;
            host.GetComponent<SeamlessTileRenderer>()?.DrawNativeSkyTint();
        }
        [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
        private static class PreparePatch
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyBefore("RimExodus.Maps")]
            private static void Prefix() => Current.Game.GetComponent<SeamlessNativeRendering>().PrepareFrame();

            // All original MapUpdate calls have finished; no REF map scope is active here.
            private static void Postfix() => Current.Game.GetComponent<SeamlessNativeRendering>().DrawSkyTint();
        }

        [HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
        private static class NeighborSkyPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                // The native neighbor callback draws its own lighting meshes. Keep their glow/roof
                // vertex data, but let the focused map's overlay and one exterior quad own sky tint.
                if (Enabled && MapRenderPlan.IsNativeView)
                    MatBases.LightOverlay.color = new Color(1f, 1f, 1f, 0f);
#if RIMWORLD_1_7
                else if (Enabled && Current.Game != null && Find.CurrentMap == Current.Game.CurrentMap)
                {
                    focusedSkyTint = MatBases.LightOverlay.color;
                    focusedSkyTintFrame = Time.frameCount;
                }
#endif
            }
        }
    }
}
