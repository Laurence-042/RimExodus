using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal static class SeamlessMapTopology
    {
        private sealed class ExitLayout
        {
            internal readonly List<IntVec3> Band = new();
            internal readonly List<IntVec3> Ring = new();
        }

        private static readonly ConditionalWeakTable<Map, ExitLayout> ExitLayouts = new();

        internal static List<SeamlessMapEdge> Edges(PlanetTile key, int mapSize)
        {
            var edges = DescribeEdges(key, mapSize);
            if (SeamlessMapProviders.For(key) == null) return edges;
            var described = edges.ToArray();

            for (int i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                if (!edge.Destination.Valid) continue;
                var destination = edge.Destination;
                int destinationSize = Find.WorldObjects.MapParentAt(destination)?.Map?.Size.x ?? mapSize;
                if (!TryResolveOffset(key, mapSize, described, destination, destinationSize, out _))
                    edges[i] = default;
            }
            return edges;
        }

        private static List<SeamlessMapEdge> DescribeEdges(PlanetTile tile, int mapSize)
        {
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(tile, neighbors);
            var provider = SeamlessMapProviders.For(tile);
            var footprint = SeamlessMapProviders.Invoke(tile, p => p.GetFootprint(tile, mapSize), null);
            var count = footprint?.Count ?? neighbors.Count;
            var edges = new List<SeamlessMapEdge>(count);
            for (int i = 0; i < count; i++)
            {
                var index = i;
                var native = i < neighbors.Count ? neighbors[i] : PlanetTile.Invalid;
                var fallback = new SeamlessMapEdge(PlanetTile.Invalid, allowVanillaExit: false);
                var edge = provider == null ? new SeamlessMapEdge(native)
                    : SeamlessMapProviders.Invoke(tile, p => p.GetEdge(tile, index, native), fallback);
                if (provider != null && ((edge.Offset.HasValue && edge.Offset.Value.y != 0)
                    || (edge.Destination.Valid && !IsDestination(tile, edge.Destination))))
                    edge = fallback;
                edges.Add(edge);
            }
            return edges;
        }

        private static bool IsDestination(PlanetTile source, PlanetTile target)
        {
            try
            {
                return target != source && target.Layer == source.Layer && target.Layer.InBounds(target)
                    && SeamlessMapProviders.Accepts(target, Find.WorldObjects.MapParentAt(target));
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        internal static List<PlanetTile> NeighborTiles(PlanetTile tile, int mapSize)
        {
            var result = new List<PlanetTile>();
            PopulateNeighborTiles(tile, mapSize, result);
            return result;
        }

        internal static void PopulateNeighborTiles(PlanetTile tile, int mapSize, List<PlanetTile> result)
        {
            result.Clear();
            if (SeamlessMapProviders.For(tile) == null)
            {
                Find.WorldGrid.GetTileNeighbors(tile, result);
                return;
            }
            foreach (var edge in Edges(tile, mapSize)) result.Add(edge.Destination);
        }

        internal static int FindEdge(IReadOnlyList<SeamlessMapEdge> edges, PlanetTile destination)
        {
            int found = -1;
            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].Destination != destination) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        internal static bool TryOffset(PlanetTile source, int sourceSize, PlanetTile target, int targetSize, out IntVec3 offset)
        {
            offset = IntVec3.Zero;
            if (!source.Valid || !target.Valid || source == target) return false;
            return TryResolveOffset(source, sourceSize, DescribeEdges(source, sourceSize), target, targetSize, out offset);
        }

        private static bool TryResolveOffset(PlanetTile source, int sourceSize, IReadOnlyList<SeamlessMapEdge> sourceEdges,
            PlanetTile target, int targetSize, out IntVec3 offset)
        {
            offset = IntVec3.Zero;
            var sourceEdge = FindEdge(sourceEdges, target);
            if (sourceEdge < 0) return false;
            var targetEdges = DescribeEdges(target, targetSize);
            var targetEdge = FindEdge(targetEdges, source);
            if (targetEdge < 0) return false;
            var a = sourceEdges[sourceEdge].Offset;
            var b = targetEdges[targetEdge].Offset;
            if (a.HasValue && b.HasValue && a.Value != -b.Value) return false;
            var from = SeamlessPolygonGeometry.BuildPolygonVertices(source, sourceSize);
            var to = SeamlessPolygonGeometry.BuildPolygonVertices(target, targetSize);
            if (sourceEdge >= from.Count || targetEdge >= to.Count) return false;
            var delta = ((from[sourceEdge] + from[(sourceEdge + 1) % from.Count])
                - (to[targetEdge] + to[(targetEdge + 1) % to.Count])) * 0.5f;
            var explicitOffset = a ?? (b.HasValue ? -b.Value : (IntVec3?)null);
            if (explicitOffset is IntVec3 translation)
            {
                if (translation.y != 0 || (delta - new Vector2(translation.x, translation.z)).sqrMagnitude > 4f)
                    return false;
                offset = translation;
            }
            else
            {
                offset = new IntVec3(Mathf.RoundToInt(delta.x), 0, Mathf.RoundToInt(delta.y));
            }
            return true;
        }

        internal static List<IntVec3> ExitCells(Map map, bool entireBand)
        {
            var key = SeamlessTileRegistry.GetMapWorldTile(map);
            if (!key.Valid || SeamlessMapProviders.For(map.Tile) == null) return null;
            var layout = ExitLayouts.GetValue(map, source => BuildExitLayout(source, key));
            return entireBand ? layout.Band : layout.Ring;
        }

        internal static void InvalidateExitCells(Map map) => ExitLayouts.Remove(map);

        private static ExitLayout BuildExitLayout(Map map, PlanetTile key)
        {
            var band = SeamlessPolygonGeometry.BuildSeamBand(key, map.Size.x);
            var vertices = SeamlessPolygonGeometry.BuildPolygonVertices(key, map.Size.x);
            var edges = Edges(key, map.Size.x);
            var layout = new ExitLayout();
            foreach (var cell in band.Band)
            {
                var edge = SeamlessPolygonGeometry.FindClosestEdgeIndex(vertices, cell.x + 0.5f, cell.z + 0.5f);
                if (edge < 0 || edge >= edges.Count || !edges[edge].AllowVanillaExit) continue;
                layout.Band.Add(cell);
                if (band.TransportRing.Contains(cell)) layout.Ring.Add(cell);
            }
            return layout;
        }
    }
}
