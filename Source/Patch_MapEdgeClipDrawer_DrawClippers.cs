using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 方案 B：patch MapEdgeClipDrawer.DrawClippers，跳过与对端地图 footprint 相交的裁剪平面。
    ///
    /// 计划书 1.10 结论 2 的关键设计点：
    /// 跳过范围必须覆盖对端地图的整个可见 footprint，而不仅仅是接缝那一小条。
    /// 因为当玩家 Pawn 移动到当前地图边界时，对端地图的大部分区域位于宿主边界之外，
    /// 若只跳过接缝，对端地图在宿主边界外的部分仍会被宿主裁剪平面涂黑。
    ///
    /// 判定标准：该裁剪平面是否与任何已加载/可见的对端地图 footprint 相交。
    /// </summary>
    [HarmonyPatch(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers))]
    public static class Patch_MapEdgeClipDrawer_DrawClippers
    {
        public static bool Prefix(Map map)
        {
            if (!map.DrawMapClippers)
            {
                return true;
            }

            // 收集宿主地图上所有无缝地块口袋地图的 footprint（宿主坐标矩形）
            var footprints = SeamlessTileRegistry.GetFootprintsOnHost(map);
            if (footprints.Count == 0)
            {
                return true;
            }

            // 手动绘制裁剪平面，跳过与 footprint 相交的边
            DrawClippersSkippingFootprints(map, footprints);
            return false;
        }

        private static void DrawClippersSkippingFootprints(Map map, System.Collections.Generic.List<CellRect> footprints)
        {
            var size = map.Size;
            var clipAltitude = AltitudeLayer.WorldClipper.AltitudeFor();
            var material = map.MapEdgeMaterial;

            var horPropertyBlock = new MaterialPropertyBlock();
            var vertPropertyBlock = new MaterialPropertyBlock();

            // 西边 (x = -250, 覆盖 x<0)
            if (!EdgeIntersectsFootprint(footprints, new CellRect(-500, 0, 500, size.z)))
            {
                var scale = new Vector3(500f, 1f, size.z);
                var center = new Vector3(-250f, 0f, size.z / 2f);
                horPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
                horPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
                DrawPlane(material, center, scale, clipAltitude, horPropertyBlock);
            }

            // 东边 (x = size.x + 250, 覆盖 x>size.x)
            if (!EdgeIntersectsFootprint(footprints, new CellRect(size.x, 0, 500, size.z)))
            {
                var scale = new Vector3(500f, 1f, size.z);
                var center = new Vector3(size.x + 250f, 0f, size.z / 2f);
                horPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
                horPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
                DrawPlane(material, center, scale, clipAltitude, horPropertyBlock);
            }

            // 南边 (z = -250, 覆盖 z<0)
            if (!EdgeIntersectsFootprint(footprints, new CellRect(0, -500, size.x, 500)))
            {
                var scale = new Vector3(1000f, 1f, 500f);
                var center = new Vector3(size.x / 2f, 0f, -250f);
                vertPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
                vertPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
                DrawPlane(material, center, scale, clipAltitude, vertPropertyBlock);
            }

            // 北边 (z = size.z + 250, 覆盖 z>size.z)
            if (!EdgeIntersectsFootprint(footprints, new CellRect(0, size.z, size.x, 500)))
            {
                var scale = new Vector3(1000f, 1f, 500f);
                var center = new Vector3(size.x / 2f, 0f, size.z + 250f);
                vertPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
                vertPropertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, center);
                DrawPlane(material, center, scale, clipAltitude, vertPropertyBlock);
            }
        }

        private static bool EdgeIntersectsFootprint(System.Collections.Generic.List<CellRect> footprints, CellRect edgeRect)
        {
            foreach (var footprint in footprints)
            {
                if (footprint.Overlaps(edgeRect))
                {
                    return true;
                }
            }
            return false;
        }

        private static void DrawPlane(Material material, Vector3 center, Vector3 scale, float altitude, MaterialPropertyBlock propertyBlock)
        {
            var matrix = default(Matrix4x4);
            matrix.SetTRS(center.WithYOffset(altitude), Quaternion.identity, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0, null, 0, propertyBlock);
        }
    }
}
