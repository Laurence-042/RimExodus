using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 将宿主地图的无缝地块口袋地图作为主相机的背景地形绘制。
    /// 口袋地形在原版宿主地图之前提交，绘制完成后只清除深度，
    /// 从而让宿主地图可靠覆盖重叠带，同时保留宿主边界外的口袋地图颜色。
    /// </summary>
    public class SeamlessTileRenderer : MapComponent
    {
        private const string CommandBufferName = "RimExodus Seamless Pocket Terrain";

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> layersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static SeamlessTileRenderer activeRenderer;

        private readonly List<TerrainDrawCommand> drawCommands = new List<TerrainDrawCommand>();

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
            if (map.IsPocketMap || Find.CurrentMap != map || !WorldRendererUtility.DrawingMap)
            {
                return;
            }

            EnsureCommandBuffer();
            commandBuffer.Clear();
            drawCommands.Clear();
            nextSequence = 0;

            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile parent || parent.sourceMap != map)
                {
                    continue;
                }

                var pocketMapInstance = parent.Map;
                if (pocketMapInstance == null || pocketMapInstance.Disposed)
                {
                    continue;
                }

                CollectPocketTerrain(pocketMapInstance, parent);
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

            // 保留口袋地图已经写入的颜色，但清除它写入的深度。
            // 随后进入原版主相机渲染流程的宿主地图不会再受口袋地图深度遮挡。
            commandBuffer.ClearRenderTarget(true, false, Color.clear, 1f);
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

        private void CollectPocketTerrain(Map pocketMap, MapParent_SeamlessTile parent)
        {
            var sections = sectionsRef(pocketMap.mapDrawer);
            if (sections == null)
            {
                return;
            }

            EnsureSectionsGenerated(sections);

            var matrix = Matrix4x4.TRS(
                parent.hostOffset.ToVector3(),
                Quaternion.identity,
                Vector3.one);

            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var section = sections[x, z];
                    if (section == null)
                    {
                        continue;
                    }

                    CollectMainTerrainLayer(section, matrix);
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
                    // 口袋地图 section 不在宿主 ViewRect 内，不能依赖 TryUpdate。
                    section.dirtyFlags = 0uL;
                }
            }
        }

        private void CollectMainTerrainLayer(Section section, Matrix4x4 matrix)
        {
            var layers = layersRef(section);
            if (layers == null)
            {
                return;
            }

            foreach (var layer in layers)
            {
                // 必须使用精确类型。SectionLayer_Watergen 继承自 SectionLayer_Terrain，
                // 但只能绘制到水深子相机，绝不能提交给主相机。
                if (layer.GetType() != typeof(SectionLayer_Terrain) || !layer.Visible)
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
