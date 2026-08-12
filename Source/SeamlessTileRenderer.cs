using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 将当前地图的直接邻居地块作为背景绘制（对称架构）。
    /// 邻居地形/建筑在原版当前地图之前提交，绘制完成后只清除深度，
    /// 从而让当前地图可靠覆盖重叠带，同时保留当前地图边界外的邻居颜色。
    /// 对称性：聚焦任意图块时，其所有直接邻居（含锚点和口袋）都会被绘制。
    /// </summary>
    public class SeamlessTileRenderer : MapComponent
    {
        private const string CommandBufferName = "RimExodus Seamless Neighbor Terrain";

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> layersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static SeamlessTileRenderer activeRenderer;

        private readonly List<TerrainDrawCommand> drawCommands = new List<TerrainDrawCommand>();
        private readonly List<SeamlessTileGraph.NeighborInfo> cachedNeighbors = new List<SeamlessTileGraph.NeighborInfo>();

        private CommandBuffer commandBuffer;
        private Camera attachedCamera;
        private int nextSequence;

        private struct TerrainDrawCommand
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4 matrix;
            public int renderQueue;
            public int sequence;
        }

        public SeamlessTileRenderer(Map map) : base(map)
        {
        }

        public override void MapComponentDraw()
        {
            if (Find.CurrentMap != map || !WorldRendererUtility.DrawingMap)
            {
                return;
            }

            // 收集当前地图的所有直接邻居（对称：锚点/口袋都遍历）。复用缓存列表避免每帧分配。
            cachedNeighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, cachedNeighbors);
            if (cachedNeighbors.Count == 0)
            {
                return;
            }

            EnsureCommandBuffer();
            commandBuffer.Clear();
            drawCommands.Clear();
            nextSequence = 0;

            // 开头清除色缓冲和深度缓冲：邻居 mesh 画在干净背景上，避免移动相机时上一帧残留
            // 在 void 区域（ShadowMask 半透明不写不透明色）累积成红色残影。
            // 当前地图随后在 ForwardOpaque 阶段正常覆盖它该出现的像素。
            commandBuffer.ClearRenderTarget(true, true, Color.clear, 1f);

            // 宿主相机视区（cell 坐标，ExpandedBy(1) 镜像原版 MapDrawer.ViewRect，所有邻居共用）。
            // 此处 Find.CurrentMap == map 已由方法开头守卫保证，CurrentViewRect 即宿主视区。
            var hostViewRect = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);

            foreach (var neighbor in cachedNeighbors)
            {
                CollectNeighborLayers(neighbor.map, neighbor.offset, neighbor.offset.ToVector3(), hostViewRect);
                DrawNeighborPawns(neighbor.map, neighbor.offset.ToVector3(), hostViewRect);
            }

            if (drawCommands.Count == 0)
            {
                return;
            }

            drawCommands.Sort(CompareDrawCommands);
            foreach (var drawCommand in drawCommands)
            {
                commandBuffer.DrawMesh(drawCommand.mesh, drawCommand.matrix, drawCommand.material);
            }

            // 保留邻居地图已经写入的颜色，但清除它写入的深度。
            // 随后进入原版主相机渲染流程的当前地图不会再受邻居地图深度遮挡。
            // 注意：这使重叠带由"聚焦地图"覆盖（非完全对称）；完全对称留待连续地形阶段的归属裁剪。
            commandBuffer.ClearRenderTarget(true, false, Color.clear, 1f);
        }

        /// <summary>
        /// 邻居地图上的 Pawn 不会被当前地图的 DynamicDrawManager 绘制，
        /// 因此需要手动以 offset 平移后的位置立即绘制一遍。
        /// Thing.DrawNowAt 接受显式坐标，绕开 DrawPos/Position。
        /// 视区裁剪：只绘制平移后落在宿主相机视区内的 pawn。
        /// </summary>
        private static void DrawNeighborPawns(Map neighborMap, Vector3 offset, CellRect hostViewRect)
        {
            foreach (var pawn in neighborMap.mapPawns.AllPawnsSpawned)
            {
                try
                {
                    var drawPos = pawn.DrawPos + offset;
                    // 宿主坐标系判定：pawn 平移后的世界坐标转 cell 坐标后是否在视区内。
                    if (!hostViewRect.Contains(new IntVec3(
                            Mathf.FloorToInt(drawPos.x), 0, Mathf.FloorToInt(drawPos.z))))
                    {
                        continue;
                    }

                    pawn.DrawNowAt(drawPos);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce($"[RimExodus] Failed to draw seamless neighbor pawn {pawn}: {ex}", pawn.thingIDNumber ^ 0x5eaf00d);
                }
            }
        }

        public override void MapComponentUpdate()
        {
            if (activeRenderer == this &&
                (Find.CurrentMap != map || !WorldRendererUtility.DrawingMap))
            {
                ReleaseCommandBuffer();
            }
        }

        public override void MapRemoved()
        {
            ReleaseCommandBuffer();
        }

        private void EnsureCommandBuffer()
        {
            var camera = Find.Camera;
            if (commandBuffer != null && attachedCamera == camera)
            {
                return;
            }

            if (activeRenderer != null && activeRenderer != this)
            {
                activeRenderer.ReleaseCommandBuffer();
            }

            ReleaseCommandBuffer();

            commandBuffer = new CommandBuffer
            {
                name = CommandBufferName
            };
            attachedCamera = camera;
            attachedCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, commandBuffer);
            activeRenderer = this;
        }

        private void ReleaseCommandBuffer()
        {
            if (attachedCamera != null && commandBuffer != null)
            {
                attachedCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, commandBuffer);
            }

            commandBuffer?.Release();
            commandBuffer = null;
            attachedCamera = null;

            if (activeRenderer == this)
            {
                activeRenderer = null;
            }
        }

        /// <summary>
        /// 收集邻居地图的地形层（SectionLayer_Terrain）、静态物层（SectionLayer_ThingsGeneral）
        /// 和战雾层（SectionLayer_FogOfWar），用 offset 平移矩阵提交到 CommandBuffer。
        /// 视区裁剪：只提交与当前相机视区相交的 section（镜像 MapDrawer.DrawMapMesh 的做法），
        /// 邻居地图通常只有约一半可见，跳过不可见 section 显著减少 draw call。
        /// </summary>
        private void CollectNeighborLayers(Map neighborMap, IntVec3 offsetInt, Vector3 offsetVec, CellRect hostViewRect)
        {
            var sections = sectionsRef(neighborMap.mapDrawer);
            if (sections == null)
            {
                return;
            }

            EnsureSectionsGenerated(sections);

            // 把宿主视区平移到邻居坐标系：邻居本地坐标 + offset = 宿主坐标，
            // 故邻居视区 = 宿主视区 - offset。ClipInsideMap 防越界误判。
            var neighborView = hostViewRect.MovedBy(-offsetInt).ClipInsideMap(neighborMap);

            var matrix = Matrix4x4.TRS(offsetVec, Quaternion.identity, Vector3.one);

            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var section = sections[x, z];
                    if (section == null)
                    {
                        continue;
                    }

                    // section 粒度裁剪。Bounds 含 fog 几何范围（fog 用标准 cell 网格建几何，不膨胀），
                    // 一次判定覆盖 terrain/things/fog 三层。
                    if (!neighborView.Overlaps(section.Bounds))
                    {
                        continue;
                    }

                    CollectLayer(section, matrix, typeof(SectionLayer_Terrain));
                    CollectLayer(section, matrix, typeof(SectionLayer_ThingsGeneral));
                    CollectLayer(section, matrix, typeof(SectionLayer_FogOfWar));
                }
            }
        }

        private static void EnsureSectionsGenerated(Section[,] sections)
        {
            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var section = sections[x, z];
                    if (section == null || section.dirtyFlags == 0)
                    {
                        continue;
                    }

                    section.RegenerateAllLayers();

                    // RegenerateAllLayers 不会像 TryUpdate 一样清除 dirtyFlags。
                    // 邻居 section 不在当前地图 ViewRect 内，不能依赖 TryUpdate。
                    section.dirtyFlags = 0uL;
                }
            }
        }

        /// <summary>
        /// 收集指定精确类型的 SectionLayer 的 submesh。
        /// 必须用精确类型匹配：
        /// - SectionLayer_Watergen 继承自 SectionLayer_Terrain，但只能绘制到水深子相机，绝不能提交主相机。
        /// - SectionLayer_FogOfWar 现在收集：fog 层 mesh 顶点是绝对世界坐标（同 Terrain），可被 offset 矩阵正确平移；
        ///   全探索 section 的 fog submesh 被 Regenerate 设 disabled，下方 disabled 检查会跳过，零开销。
        /// - 仍不收集 SunShadows/Gas 等有 shadow/grid 依赖且不适合偏移绘制的层。
        /// </summary>
        private void CollectLayer(Section section, Matrix4x4 matrix, Type layerType)
        {
            var layers = layersRef(section);
            if (layers == null)
            {
                return;
            }

            foreach (var layer in layers)
            {
                if (layer.GetType() != layerType || !layer.Visible)
                {
                    continue;
                }

                foreach (var subMesh in layer.subMeshes)
                {
                    if (!subMesh.finalized || subMesh.disabled || subMesh.material == null)
                    {
                        continue;
                    }

                    drawCommands.Add(new TerrainDrawCommand
                    {
                        mesh = subMesh.mesh,
                        material = subMesh.material,
                        matrix = matrix,
                        renderQueue = subMesh.material.renderQueue,
                        sequence = nextSequence++
                    });
                }
            }
        }

        private static int CompareDrawCommands(TerrainDrawCommand left, TerrainDrawCommand right)
        {
            var queueComparison = left.renderQueue.CompareTo(right.renderQueue);
            return queueComparison != 0
                ? queueComparison
                : left.sequence.CompareTo(right.sequence);
        }
    }
}
