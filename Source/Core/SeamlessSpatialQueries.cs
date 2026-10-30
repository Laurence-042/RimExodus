using System;
using System.Collections.Generic;
using RimExodus.Maps;
using Verse;

namespace RimExodus
{
    /// <summary>Simulation geometry is independent of both backend and drawing settings.</summary>
    internal static class SeamlessSpatialQueries
    {
        private static readonly Func<Map, MapCellSpace> Provider = Capture;
        internal static void Configure() => MapRuntime.Current?.Spatial.SetProvider(SeamlessFramework.Owner, Provider);
        internal static bool Available(Map map) => map != null && !map.Disposed
            && SeamlessTileRegistry.GetMapWorldTile(map) >= 0
            && !SeamlessDormancyManager.IsDormant(map) && !IncrementalMapGenerator.IsGenerating(map);

        internal static bool TryCapture(Map map, out MapCellSpace space)
        {
            space = null;
            var runtime = MapRuntime.Current;
            if (runtime == null) return false;
            runtime.Spatial.SetProvider(SeamlessFramework.Owner, Provider);
            return runtime.Spatial.TryCapture(SeamlessFramework.Owner, map, out space);
        }
        private static MapCellSpace Capture(Map reference)
        {
            if (!Available(reference)) return null;
            var entries = new List<MapCellPlacement> { Placement(reference, IntVec3.Zero) };
            foreach (var neighbor in SeamlessTileGraph.GetAllNeighbors(reference))
                if (Available(neighbor.map) && SeamlessViewProjection.TryProject(neighbor.map, IntVec3.Zero, reference, out var origin))
                    entries.Add(Placement(neighbor.map, origin));
            return new MapCellSpace(reference, entries);
        }
        private static MapCellPlacement Placement(Map map, IntVec3 origin)
        {
            int size = map.Size.x;
            var polygon = SeamlessPolygonGeometry.BuildPolygonVertices(SeamlessTileRegistry.GetMapWorldTile(map), size);
            // Same reference-first ownership rule as SeamlessTileRegistry, including boundary ties.
            return new MapCellPlacement(map, origin, cell => SeamlessPolygonGeometry.ContainsPoint(polygon, size, cell));
        }
    }
}
