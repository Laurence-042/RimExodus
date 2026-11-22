#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal sealed class EndlessHorizonGeometry
    {
        internal readonly Map Map;
        internal readonly int Reach;
        internal readonly List<Vector2> Polygon;
        internal readonly List<Vector2> DrawDomain;
        internal readonly List<IntVec3> EdgeCells = new();
        private readonly List<OwnershipPlane> ownership = new();
        private readonly int width;
        private readonly int right;
        private readonly int top;
        private readonly int[] nearest;
        private readonly List<SeamlessTileGraph.NeighborInfo> neighbors;

        private readonly struct OwnershipPlane
        {
            internal readonly Vector2 Normal;
            internal readonly float Limit;
            internal OwnershipPlane(Vector2 normal, float limit)
            {
                Normal = normal;
                Limit = limit;
            }
        }

        internal EndlessHorizonGeometry(Map map, PlanetTile tile, int reach, List<SeamlessTileGraph.NeighborInfo> neighbors)
        {
            Map = map;
            Reach = reach;
            this.neighbors = new(neighbors);
            Polygon = SeamlessPolygonGeometry.BuildPolygonVertices(tile, map.Size.x);
            if (Polygon.Count < 3) throw new InvalidOperationException("Missing seamless outline for Endless Horizon.");
            width = map.Size.x + 2 * reach;
            right = map.Size.x + reach;
            top = map.Size.z + reach;
            nearest = new int[width * (map.Size.z + 2 * reach)];
            Array.Fill(nearest, -1);
            var band = SeamlessPolygonGeometry.BuildSeamBand(tile, map.Size.x);
            var frontier = new Queue<int>();
            foreach (var cell in CellRect.WholeMap(map))
            {
                if (!band.Band.Contains(cell)
                    && !SeamlessPolygonGeometry.ContainsPointAt(Polygon, cell.x + .5f, cell.z + .5f)) continue;
                var index = Index(cell.x, cell.z);
                nearest[index] = index;
            }
            foreach (var cell in CellRect.WholeMap(map))
            {
                if (!Inside(cell)) continue;
                foreach (var direction in GenAdj.CardinalDirections)
                {
                    if (Inside(cell + direction)) continue;
                    frontier.Enqueue(Index(cell.x, cell.z));
                    EdgeCells.Add(cell);
                    break;
                }
            }
            while (frontier.Count > 0)
            {
                var index = frontier.Dequeue();
                var cell = Cell(index);
                foreach (var direction in GenAdj.AdjacentCells)
                {
                    var next = cell + direction;
                    if (!InGrid(next.x, next.z)) continue;
                    var nextIndex = Index(next.x, next.z);
                    if (nearest[nextIndex] >= 0) continue;
                    nearest[nextIndex] = nearest[index];
                    frontier.Enqueue(nextIndex);
                }
            }
            var center = new Vector2(map.Size.x * .5f, map.Size.z * .5f);
            foreach (var neighbor in this.neighbors)
            {
                var other = new Vector2(neighbor.map.Size.x * .5f + neighbor.offset.x,
                    neighbor.map.Size.z * .5f + neighbor.offset.z);
                var normal = other - center;
                if (normal.sqrMagnitude < .001f) continue;
                ownership.Add(new OwnershipPlane(normal, (other.sqrMagnitude - center.sqrMagnitude) * .5f));
            }
            DrawDomain = BuildDrawDomain(map.Size.x, map.Size.z);
        }

        internal bool Matches(int reach, List<SeamlessTileGraph.NeighborInfo> current)
        {
            if (reach != Reach || current.Count != neighbors.Count) return false;
            for (var i = 0; i < current.Count; i++)
                if (current[i].map != neighbors[i].map || current[i].offset != neighbors[i].offset) return false;
            return true;
        }

        internal bool Inside(IntVec3 cell)
        {
            if (!cell.InBounds(Map)) return false;
            var index = Index(cell.x, cell.z);
            return nearest[index] == index;
        }

        internal int Radius => Reach - 16;

        internal bool DrawCell(IntVec3 cell, int radius)
        {
            if (!InGrid(cell.x, cell.z)) return false;
            var index = Index(cell.x, cell.z);
            return nearest[index] >= 0 && nearest[index] != index
                && Owns(new Vector2(cell.x + .5f, cell.z + .5f))
                && SeamlessGridMath.ChebyshevDistance(Cell(nearest[index]), cell) <= radius;
        }

        internal bool Owns(Vector2 point)
        {
            const float epsilon = .001f;
            for (var i = 0; i < ownership.Count; i++)
                if (Vector2.Dot(ownership[i].Normal, point) > ownership[i].Limit + epsilon) return false;
            return true;
        }

        internal IntVec3 Nearest(IntVec3 cell)
        {
            var x = Mathf.Clamp(cell.x, -Reach, right - 1);
            var z = Mathf.Clamp(cell.z, -Reach, top - 1);
            var index = nearest[Index(x, z)];
            return index < 0 ? IntVec3.Invalid : Cell(index);
        }

        internal bool DrawChunk(CellRect rect, int radius)
        {
            foreach (var cell in rect)
                if (DrawCell(cell, radius)) return true;
            return false;
        }

        private bool InGrid(int x, int z) => x >= -Reach && z >= -Reach && x < right && z < top;
        private int Index(int x, int z) => (z + Reach) * width + x + Reach;
        private IntVec3 Cell(int index) => new(index % width - Reach, 0, index / width - Reach);

        private List<Vector2> BuildDrawDomain(int mapWidth, int mapHeight)
        {
            const float far = 2000f;
            var polygon = new List<Vector2>
            {
                new(-far, -far),
                new(mapWidth + far, -far),
                new(mapWidth + far, mapHeight + far),
                new(-far, mapHeight + far)
            };
            foreach (var plane in ownership)
            {
                var clipped = new List<Vector2>();
                if (polygon.Count == 0) break;
                var previous = polygon[polygon.Count - 1];
                var previousDistance = plane.Limit - Vector2.Dot(plane.Normal, previous);
                foreach (var point in polygon)
                {
                    var distance = plane.Limit - Vector2.Dot(plane.Normal, point);
                    if ((distance >= 0f) != (previousDistance >= 0f))
                    {
                        var t = previousDistance / (previousDistance - distance);
                        clipped.Add(Vector2.LerpUnclamped(previous, point, t));
                    }
                    if (distance >= 0f) clipped.Add(point);
                    previous = point;
                    previousDistance = distance;
                }
                polygon = clipped;
            }
            return polygon;
        }
    }
}
#endif
