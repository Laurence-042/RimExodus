#if RIMWORLD_1_7
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal static partial class SeamlessEndlessHorizonCompat
    {
        private static FieldInfo riverCrossings, crossingWidth, crossingDeepFraction, crossingDeep, crossingShallow,
            crossingStart, crossingDirection, crossingTarget;
        private static FieldInfo fieldGenerator, fieldCells, fieldSamples, fieldRawSamples, coastSnapshot,
            fieldGenRock, fieldGenRockDef, fieldGenRoof;

        private static void BindRiver(Type river)
        {
            var crossing = Type("RiverCrossing");
            riverCrossings = Field(river, "Crossings");
            crossingWidth = Field(crossing, "width");
            crossingDeepFraction = Field(crossing, "deepFraction");
            crossingDeep = Field(crossing, "deep");
            crossingShallow = Field(crossing, "shallow");
            crossingStart = Field(crossing, "start");
            crossingDirection = Field(crossing, "outDir");
            crossingTarget = Field(crossing, "targetDir");
        }

        private static bool FindCrossingsPrefix(object __instance, Rot4 side)
        {
            if (drawing == null) return true;
            if (side != Rot4.North) return false;
            var crossings = (IList)riverCrossings.GetValue(__instance);
            var polygon = drawing.Polygon;
            var byEdge = new List<(float Along, IntVec3 Cell, TerrainDef Terrain)>[polygon.Count];
            foreach (var cell in drawing.EdgeCells)
            {
                var terrain = drawing.Map.terrainGrid.BaseTerrainAt(cell);
                if (terrain?.IsRiver != true) continue;
                var point = new Vector2(cell.x + .5f, cell.z + .5f);
                var edge = SeamlessPolygonGeometry.FindClosestEdgeIndex(polygon, point.x, point.y);
                var tangent = (polygon[(edge + 1) % polygon.Count] - polygon[edge]).normalized;
                (byEdge[edge] ??= new()).Add((Vector2.Dot(point, tangent), cell, terrain));
            }

            for (var edge = 0; edge < byEdge.Length; edge++)
            {
                var cells = byEdge[edge];
                if (cells == null) continue;
                cells.Sort((a, b) => a.Along.CompareTo(b.Along));
                var tangent = (polygon[(edge + 1) % polygon.Count] - polygon[edge]).normalized;
                var outward = SeamlessPolygonGeometry.EdgeOutwardNormal(polygon, edge);
                for (int start = 0, end = 1; end <= cells.Count; end++)
                {
                    if (end < cells.Count && cells[end].Along - cells[end - 1].Along <= 2.1f) continue;
                    var run = cells.GetRange(start, end - start);
                    start = end;
                    var midpoint = run[run.Count / 2];
                    var position = new Vector2(midpoint.Cell.x + .5f, midpoint.Cell.z + .5f);
                    var width = Mathf.Max(1f, run[run.Count - 1].Along - run[0].Along + 1f);
                    var direction = TraceRiverDirection(position, tangent, outward, width);
                    width *= Mathf.Max(.35f, Vector2.Dot(outward, direction));
                    var deep = midpoint.Terrain;
                    var shallow = run[0].Terrain;
                    var crossing = Activator.CreateInstance(crossingStart.DeclaringType);
                    crossingStart.SetValue(crossing, position + outward * .5f);
                    crossingWidth.SetValue(crossing, width);
                    crossingDeep.SetValue(crossing, deep);
                    crossingShallow.SetValue(crossing, shallow);
                    crossingDeepFraction.SetValue(crossing, run.Count(cell => cell.Terrain == deep) / (float)run.Count);
                    crossingDirection.SetValue(crossing, direction);
                    crossingTarget.SetValue(crossing, direction);
                    crossings.Add(crossing);
                }
            }
            return false;
        }

        private static Vector2 TraceRiverDirection(Vector2 position, Vector2 tangent, Vector2 outward, float width)
        {
            var previous = position;
            for (var depth = 2; depth <= 16; depth += 2)
            {
                var origin = previous - 2f * outward;
                var sum = Vector2.zero;
                var count = 0;
                var radius = Mathf.CeilToInt(width / 2f) + 2;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sample = origin + tangent * offset;
                    var cell = new IntVec3(Mathf.FloorToInt(sample.x), 0, Mathf.FloorToInt(sample.y));
                    if (!drawing.Inside(cell) || drawing.Map.terrainGrid.BaseTerrainAt(cell)?.IsRiver != true) continue;
                    sum += sample;
                    count++;
                }
                if (count == 0) break;
                previous = sum / count;
            }
            return previous == position ? outward : (position - previous).normalized;
        }

        private static void BindCoast(Type field)
        {
            fieldGenerator = Field(field, "Generator");
            fieldCells = Field(field, "cells");
            fieldSamples = Field(field, "samples");
            fieldRawSamples = Field(field, "rawSamples");
            fieldGenRock = Field(field, "genRock");
            fieldGenRockDef = Field(field, "genRockDef");
            fieldGenRoof = Field(field, "genRoofLevel");
            coastSnapshot = Field(field, "coastBoundarySnapshot");
        }

        private static bool SampleSeamPrefix(object __instance, IntVec3 c, ref int sample, ref int rawSample,
            ref TerrainDef __result)
        {
            if (drawing == null || fieldGenerator.GetValue(__instance) != null) return true;
            var map = drawing.Map;
            var edge = drawing.Nearest(c);
            var mirrored = edge + (edge - c);
            var source = drawing.Inside(mirrored) ? mirrored : edge;
            var terrain = map.terrainGrid.BaseTerrainAt(source);
            if (terrain?.IsRiver == true)
            {
                foreach (var offset in GenAdj.AdjacentCells)
                {
                    var candidate = source + offset;
                    if (!drawing.Inside(candidate)) continue;
                    var bank = map.terrainGrid.BaseTerrainAt(candidate);
                    if (bank == null || bank.IsWater) continue;
                    terrain = bank;
                    source = candidate;
                    break;
                }
            }
            // RiverExtension paints the actual channel after the base sample.
            if (terrain?.IsRiver == true) terrain = map.Biome?.riverbankTerrain ?? TerrainDefOf.Sand;
            __result = terrain ?? TerrainDefOf.Sand;
            sample = rawSample = map.cellIndices.CellToIndex(source);
            return false;
        }

        private static bool CoastPrefix(object __instance)
        {
            if (drawing == null) return true;
            if (!(bool)coastEnabled.GetValue(settings.GetValue(null))) return false;
            var map = drawing.Map;
            var cells = (TerrainDef[])fieldCells.GetValue(__instance);
            var samples = (int[])fieldSamples.GetValue(__instance);
            var rawSamples = (int[])fieldRawSamples.GetValue(__instance);
            var rocks = (bool[])fieldGenRock.GetValue(__instance);
            var rockDefs = (ThingDef[])fieldGenRockDef.GetValue(__instance);
            var roofs = (byte[])fieldGenRoof.GetValue(__instance);
            var radius = drawing.Radius;
            var size = map.Size;
            var width = size.x + radius * 2;
            foreach (var cell in new CellRect(-radius, -radius, width, size.z + radius * 2))
            {
                var index = (cell.z + radius) * width + cell.x + radius;
                var predicted = cells[index];
                if (predicted == null || predicted.IsRiver || !drawing.DrawCell(cell, radius)) continue;
                var edge = drawing.Nearest(cell);
                var shore = map.terrainGrid.BaseTerrainAt(edge);
                if (shore == null || shore.IsRiver || shore.IsOcean == predicted.IsOcean) continue;
                var distance = SeamlessGridMath.ChebyshevDistance(cell, edge);
                // Taper the observed shoreline into the procedural coast within the same 13-cell margin.
                var transition = 4 + (int)((uint)Gen.HashCombineInt(edge.x, edge.z) % 10);
                if (distance > transition) continue;
                cells[index] = shore;
                samples[index] = rawSamples[index] = map.cellIndices.CellToIndex(edge);
                if (shore.IsWater && rocks != null)
                {
                    rocks[index] = false;
                    rockDefs[index] = null;
                    if (roofs != null) roofs[index] = 0;
                }
            }
            return false;
        }

        private static bool CaptureBoundaryPrefix(object __instance)
        {
            if (drawing == null) return true;
            var map = drawing.Map;
            var snapshot = (Dictionary<int, TerrainDef>)coastSnapshot.GetValue(__instance);
            foreach (var cell in drawing.EdgeCells)
                snapshot[map.cellIndices.CellToIndex(cell)] = map.terrainGrid.BaseTerrainAt(cell);
            return false;
        }
    }
}
#endif
