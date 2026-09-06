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
    [StaticConstructorOnStartup] // 静态 MaterialPropertyBlock 字段——加特性消 Verse 启动分析器警告
    public static class Patch_MapEdgeClipDrawer_DrawClippers
    {
        private const float ClipSize = 500f;
        private const float VerticalClipWidth = 1000f;

        private static readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

        // 每帧复用缓冲：DrawClippers 只对当前图每帧跑一次，无并发面。footprint 挖洞
        // 计算原本每帧新建 6-10 个 List，是渲染热路径上的常驻 GC 底噪（2026-09 顺带修复）。
        // DrawPlane 把 rect 值拷进 matrix/propertyBlock，buffer 内容不被引用，复用安全。
        private static readonly List<Rect> clipRectBuffer = new List<Rect>(4);
        private static readonly List<Rect> footprintRectBuffer = new List<Rect>();
        private static readonly List<Rect> remainingBuffer = new List<Rect>();
        private static readonly List<Rect> subtractBuffer = new List<Rect>();

        public static bool Prefix(Map map)
        {
            if (!map.DrawMapClippers)
            {
                return true;
            }

            var footprints = SeamlessTileRegistry.GetNeighborFootprints(map);
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

            clipRectBuffer.Clear();
            clipRectBuffer.Add(Rect.MinMaxRect(-ClipSize, 0f, 0f, size.z));
            clipRectBuffer.Add(Rect.MinMaxRect(size.x, 0f, size.x + ClipSize, size.z));
            clipRectBuffer.Add(Rect.MinMaxRect(horizontalCenter - halfVerticalWidth, -ClipSize,
                horizontalCenter + halfVerticalWidth, 0f));
            clipRectBuffer.Add(Rect.MinMaxRect(horizontalCenter - halfVerticalWidth, size.z,
                horizontalCenter + halfVerticalWidth, size.z + ClipSize));

            footprintRectBuffer.Clear();
            foreach (var footprint in footprints)
            {
                footprintRectBuffer.Add(new Rect(
                    footprint.minX,
                    footprint.minZ,
                    footprint.Width,
                    footprint.Height));
            }

            foreach (var clipRect in clipRectBuffer)
            {
                remainingBuffer.Clear();
                remainingBuffer.Add(clipRect);
                foreach (var footprintRect in footprintRectBuffer)
                {
                    SubtractFromAll(remainingBuffer, footprintRect);
                    if (remainingBuffer.Count == 0)
                    {
                        break;
                    }
                }

                for (var i = 0; i < remainingBuffer.Count; i++)
                {
                    DrawPlane(map.MapEdgeMaterial, remainingBuffer[i]);
                }
            }
        }

        private static void SubtractFromAll(List<Rect> sources, Rect cut)
        {
            subtractBuffer.Clear();
            foreach (var source in sources)
            {
                Subtract(source, cut, subtractBuffer);
            }

            sources.Clear();
            sources.AddRange(subtractBuffer);
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
