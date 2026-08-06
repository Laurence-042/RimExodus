using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 在原版四块地图边缘裁剪平面上，仅挖出口袋地图实际占用的 footprint。
    /// footprint 以外仍由裁剪平面逐帧覆盖，避免只清深度的主相机保留上一帧颜色。
    /// </summary>
    [HarmonyPatch(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers))]
    public static class Patch_MapEdgeClipDrawer_DrawClippers
    {
        private const float ClipSize = 500f;
        private const float VerticalClipWidth = 1000f;

        private static readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

        public static bool Prefix(Map map)
        {
            if (!map.DrawMapClippers)
            {
                return true;
            }

            var footprints = SeamlessTileRegistry.GetFootprintsOnHost(map);
            if (footprints.Count == 0)
            {
                return true;
            }

            DrawClippersWithFootprintHoles(map, footprints);
            return false;
        }

        private static void DrawClippersWithFootprintHoles(Map map, List<CellRect> footprints)
        {
            var size = map.Size;
            var halfVerticalWidth = VerticalClipWidth / 2f;
            var horizontalCenter = size.x / 2f;

            var clipRects = new List<Rect>
            {
                Rect.MinMaxRect(-ClipSize, 0f, 0f, size.z),
                Rect.MinMaxRect(size.x, 0f, size.x + ClipSize, size.z),
                Rect.MinMaxRect(horizontalCenter - halfVerticalWidth, -ClipSize,
                    horizontalCenter + halfVerticalWidth, 0f),
                Rect.MinMaxRect(horizontalCenter - halfVerticalWidth, size.z,
                    horizontalCenter + halfVerticalWidth, size.z + ClipSize)
            };

            var footprintRects = new List<Rect>(footprints.Count);
            foreach (var footprint in footprints)
            {
                footprintRects.Add(new Rect(
                    footprint.minX,
                    footprint.minZ,
                    footprint.Width,
                    footprint.Height));
            }

            foreach (var clipRect in clipRects)
            {
                var remaining = new List<Rect> { clipRect };
                foreach (var footprintRect in footprintRects)
                {
                    remaining = SubtractFromAll(remaining, footprintRect);
                    if (remaining.Count == 0)
                    {
                        break;
                    }
                }

                foreach (var rect in remaining)
                {
                    DrawPlane(map.MapEdgeMaterial, rect);
                }
            }
        }

        private static List<Rect> SubtractFromAll(List<Rect> sources, Rect cut)
        {
            var result = new List<Rect>(sources.Count * 2);
            foreach (var source in sources)
            {
                Subtract(source, cut, result);
            }
            return result;
        }

        private static void Subtract(Rect source, Rect cut, List<Rect> result)
        {
            var intersectionMinX = Mathf.Max(source.xMin, cut.xMin);
            var intersectionMaxX = Mathf.Min(source.xMax, cut.xMax);
            var intersectionMinZ = Mathf.Max(source.yMin, cut.yMin);
            var intersectionMaxZ = Mathf.Min(source.yMax, cut.yMax);

            if (intersectionMinX >= intersectionMaxX || intersectionMinZ >= intersectionMaxZ)
            {
                result.Add(source);
                return;
            }

            AddIfNonEmpty(result, Rect.MinMaxRect(
                source.xMin, source.yMin, source.xMax, intersectionMinZ));
            AddIfNonEmpty(result, Rect.MinMaxRect(
                source.xMin, intersectionMaxZ, source.xMax, source.yMax));
            AddIfNonEmpty(result, Rect.MinMaxRect(
                source.xMin, intersectionMinZ, intersectionMinX, intersectionMaxZ));
            AddIfNonEmpty(result, Rect.MinMaxRect(
                intersectionMaxX, intersectionMinZ, source.xMax, intersectionMaxZ));
        }

        private static void AddIfNonEmpty(List<Rect> result, Rect rect)
        {
            if (rect.width > 0f && rect.height > 0f)
            {
                result.Add(rect);
            }
        }

        private static void DrawPlane(Material material, Rect rect)
        {
            var scale = new Vector3(rect.width, 1f, rect.height);
            var center = new Vector3(rect.center.x, 0f, rect.center.y);

            propertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
            propertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, center);

            var matrix = Matrix4x4.TRS(
                center.WithYOffset(AltitudeLayer.WorldClipper.AltitudeFor()),
                Quaternion.identity,
                scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0, null, 0, propertyBlock);
        }
    }
}
