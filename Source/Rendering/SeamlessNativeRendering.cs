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
        private bool registered;
        private bool wasEnabled;
        public static bool Enabled => RimExodusMod.Settings?.nativeNeighborRendering == true;
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
            if (!Enabled || SeamlessTileRegistry.GetMapWorldTile(host) < 0) return;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(host, neighbors);
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            // The focused map's full-rectangle mod overlays must not cover the neighboring polygons either.
            output.Add(new MapRenderPlan(host, Matrix4x4.identity, view, new MapRenderOptions
            {
                OverlayFootprint = SeamlessPolygonGeometry.BuildPolygonVertices(
                    SeamlessTileRegistry.GetMapWorldTile(host), host.Size.x)
            }));
            foreach (var neighbor in neighbors)
            {
                if (neighbor.map == null || neighbor.map.Disposed || IncrementalMapGenerator.IsGenerating(neighbor.map)
                    || SeamlessDormancyManager.IsDormant(neighbor.map)
                    || !SeamlessViewProjection.TryProject(neighbor.map, Vector3.zero, host, out var offset)) continue;
                var transform = Matrix4x4.Translate(offset);
                var localView = new MapTransform(transform).LocalView(view);
                if (!localView.Overlaps(CellRect.WholeMap(neighbor.map))) continue;
                output.Add(new MapRenderPlan(neighbor.map, transform, view, new MapRenderOptions
                {
                    OverlayFootprint = SeamlessPolygonGeometry.BuildPolygonVertices(
                        SeamlessTileRegistry.GetMapWorldTile(neighbor.map), neighbor.map.Size.x)
                }));
            }
        }
        private void DrawSkyTint()
        {
            if (!Enabled || !WorldRendererUtility.DrawingMap || LongEventHandler.ShouldWaitForEvent) return;
            var host = Find.CurrentMap;
            if (host == null || SeamlessTileRegistry.GetMapWorldTile(host) < 0) return;
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
            }
        }
    }
}
