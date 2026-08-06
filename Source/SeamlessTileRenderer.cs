using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 宿主地图上的渲染组件。
    /// 负责把宿主地图的所有无缝地块口袋地图（ChildPocketMaps）绘制到宿主地图上。
    ///
    /// 渲染方式（计划书 1.10 结论 1）：
    /// 把口袋地图的 SectionLayer 网格用 Graphics.DrawMesh 绘制到宿主地图坐标，
    /// 绕开 Map.MapUpdate 的 Find.CurrentMap == this 限制和 MapDrawer 的 ViewRect 裁剪。
    /// 静态地块地图无旋转，drawPos = hostOffset 对应的世界位置，rot = Quaternion.identity。
    /// </summary>
    public class SeamlessTileRenderer : MapComponent
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> layersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        public SeamlessTileRenderer(Map map) : base(map)
        {
        }

        public override void MapComponentDraw()
        {
            if (!WorldRendererUtility.DrawingMap)
            {
                return;
            }

            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile parent)
                {
                    continue;
                }
                if (parent.sourceMap != map)
                {
                    continue;
                }

                var pocketMapInstance = parent.Map;
                if (pocketMapInstance == null || pocketMapInstance.Disposed)
                {
                    continue;
                }

                DrawPocketMap(pocketMapInstance, parent);
            }
        }

        private void DrawPocketMap(Map pocketMap, MapParent_SeamlessTile parent)
        {
            // 确保口袋地图的 SectionLayer 已生成。
            // 注意：不能依赖 MapMeshDrawerUpdate_First 的 ViewRect 逻辑，
            // 因为口袋地图的 section 可能不在宿主地图的 ViewRect 内。
            // 因此直接对每个 section 调用 RegenerateAllLayers()。
            var sections = sectionsRef(pocketMap.mapDrawer);
            if (sections == null)
            {
                return;
            }

            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var section = sections[x, z];
                    if (section == null)
                    {
                        continue;
                    }
                    if (section.dirtyFlags != 0)
                    {
                        section.RegenerateAllLayers();
                        // 关键：RegenerateAllLayers() 不会清除 dirtyFlags（只有 TryUpdate 会）。
                        // 若不手动清零，只要 dirtyFlags != 0，每帧都会重建整个口袋地图的所有
                        // SectionLayer 网格（地形/建筑/植物/光照），导致大量内存分配和 GC 卡顿。
                        // TryUpdate 不能直接用：它依赖 bounds.Overlaps(view)（宿主 ViewRect），
                        // 口袋地图在宿主边界外，flag2 恒为 false，不会真正重建。
                        section.dirtyFlags = 0uL;
                    }
                }
            }

            var drawPos = parent.hostOffset.ToVector3();
            var rot = Quaternion.identity;

            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var section = sections[x, z];
                    if (section == null)
                    {
                        continue;
                    }
                    DrawSection(section, drawPos, rot);
                }
            }
        }

        private void DrawSection(Section section, Vector3 drawPos, Quaternion rot)
        {
            var layers = layersRef(section);
            if (layers == null)
            {
                return;
            }

            foreach (var layer in layers)
            {
                if (!layer.Visible)
                {
                    continue;
                }
                foreach (var subMesh in layer.subMeshes)
                {
                    if (subMesh.finalized && !subMesh.disabled)
                    {
                        Graphics.DrawMesh(subMesh.mesh, drawPos, rot, subMesh.material, subMesh.renderLayer);
                    }
                }
            }
        }
    }
}
