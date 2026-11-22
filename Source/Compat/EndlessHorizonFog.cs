#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    internal static partial class SeamlessEndlessHorizonCompat
    {
        private static FieldInfo fogWidth, fogHeight, fogWarpedDistance, fogSolid, fogWeatherBoost, fogMesh;

        private static void BindFog(Type fog)
        {
            fogWidth = Field(fog, "texW");
            fogHeight = Field(fog, "texH");
            fogWarpedDistance = Field(fog, "warpedDist");
            fogSolid = Field(fog, "solid");
            fogWeatherBoost = Field(fog, "weatherBoost");
            fogMesh = Field(fog, "mesh");
        }

        private static void FogDistancePrefix(object __instance)
        {
            if (drawing == null || (int)fogWeatherBoost.GetValue(__instance) != -1) return;
            var width = (int)fogWidth.GetValue(__instance);
            var height = (int)fogHeight.GetValue(__instance);
            var distances = (float[])fogWarpedDistance.GetValue(__instance);
            var solid = (float[])fogSolid.GetValue(__instance);
            var map = drawing.Map;
            var reach = drawing.Reach;
            var radius = drawing.Radius;
            var seed = Gen.HashCombineInt(Find.World.info.Seed, map.Tile.GetHashCode()) ^ 0x464F47;
            var warp = new Perlin(.035, 2.0, .5, 3, seed, QualityMode.Medium);
            // Keep the upstream texture sampling and opacity ramp. Replace their distance from the rectangle.
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var px = -reach + x * 2 + 1f;
                    var pz = -reach + z * 2 + 1f;
                    var cell = new IntVec3((int)px, 0, (int)pz);
                    var source = drawing.Nearest(cell);
                    var distance = Mathf.Max(.5f, Vector2.Distance(new(px, pz), new(source.x + .5f, source.z + .5f)));
                    var i = z * width + x;
                    distances[i] = distance + (float)warp.GetValue(px, 0, pz) * 9f;
                    var t = Mathf.Clamp01((distance - radius + 14f) / 11f);
                    solid[i] = t * t * (3f - 2f * t);
                }
            }
        }

        private static void FogMeshPostfix(object __instance)
        {
            if (drawing == null) return;
            var mesh = (Mesh)fogMesh.GetValue(__instance);
            var size = drawing.Map.Size;
            var reach = drawing.Reach;
            var textureWidth = 2f * (int)fogWidth.GetValue(__instance);
            var textureHeight = 2f * (int)fogHeight.GetValue(__instance);
            var altitude = AltitudeLayer.Weather.AltitudeFor();
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var colors = new List<Color32>();
            var triangles = new List<int>();
            foreach (var point in drawing.DrawDomain)
            {
                vertices.Add(new Vector3(point.x, altitude, point.y));
                uv.Add(new Vector2((point.x + reach) / textureWidth, (point.y + reach) / textureHeight));
                colors.Add(new Color32(255, 255, 255, 255));
            }
            for (var i = 1; i + 1 < vertices.Count; i++)
            {
                triangles.Add(0);
                triangles.Add(i + 1);
                triangles.Add(i);
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            if (vertices.Count > 0)
            {
                var min = vertices[0];
                var max = vertices[0];
                for (var i = 1; i < vertices.Count; i++)
                {
                    min = Vector3.Min(min, vertices[i]);
                    max = Vector3.Max(max, vertices[i]);
                }
                mesh.bounds = new Bounds((min + max) * .5f, max - min + new Vector3(0f, 1f, 0f));
            }
        }
    }
}
#endif
