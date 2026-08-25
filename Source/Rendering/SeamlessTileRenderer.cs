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
        /// 邻居地形/建筑/光照遮罩/战雾在原版当前地图之前提交，绘制完成后只清除深度，
        /// 从而让当前地图可靠覆盖重叠带，同时保留当前地图边界外的邻居颜色。
        /// 对称性：聚焦任意图块时，其所有直接邻居（原生 parent 图与地块图）都会被绘制。
        /// 光照天色分层（2026-08）：LightOverlay shader 的天色染色走材质 color alpha、
        /// glow/roof 走顶点色，两通道独立（实验验证）——邻图 overlay 用 (1,1,1,0) 克隆材质
        /// 做纯数据层，天色由"当前图 overlay（自己方形内）+ 全零顶点 quad（邻图非重叠 L 形区）"
        /// 各管一块，消除 void 透明圈 sky² 双染暗带。
    /// </summary>
    [StaticConstructorOnStartup] // 静态 AccessTools FieldRef 初始化走启动期；Material/Mesh 保持惰性构建，加特性消 Verse 启动分析器警告
    public class SeamlessTileRenderer : MapComponent
    {
        private const string CommandBufferName = "RimExodus Seamless Neighbor Terrain";

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> layersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        /// <summary>
        /// 邻图光照"纯数据层"材质：克隆 LightOverlay 后 color=(1,1,1,0)。
        /// 材质 alpha 只关天色染色，顶点色的 glow 加光与岩顶黑暗独立保留
        /// （2026-08 游戏内实验验证：灯晕在/岩顶黑在/户外不随昼夜变暗；
        /// 原版先例 SkyManager.cs:49 disableSkyLighting 群设置同款开关）。
        /// </summary>
        private static Material neighborGlowOnlyMat;

        /// <summary>(0,0,0,0) 顶点色的单位 quad——顶点零值在 LightOverlay shader 里的语义
        /// 不是透传而是"按材质色染天色"（原版 MapDrawLayer_ExteriorLightingOverlay 同款手法，
        /// 专门用来给图外区域染昼夜色）。天色分层的天色 quad 用它。</summary>
        private static Mesh skyTintQuadMesh;

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

            // 收集当前地图的所有直接邻居（对称：原生图/地块图都遍历）。复用缓存列表避免每帧分配。
            cachedNeighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, cachedNeighbors);
            if (cachedNeighbors.Count == 0)
            {
                // 零邻居（新档首图生成 / 孤岛地块 / 邻居全部卸载 / 读档邻居未再生成）时
                // 仍要执行全屏清色：void 地形画半透明 ShadowMask 不写不透明色，主相机只清
                // 深度——不清色的话 void 带保留上一帧像素，平移相机时残影累积成红色拖影。
                // 不画任何邻居 mesh，只做"清色-only"帧。同时 commandBuffer.Clear() 清掉
                // 陈旧命令——否则邻居全部卸载后，挂在相机上的旧 buffer 会逐帧重放对已
                // Dispose 地图 mesh 的 DrawMesh（void 带冻结显示旧邻居画面）。
                EnsureCommandBuffer();
                commandBuffer.Clear();
                commandBuffer.ClearRenderTarget(true, true, Color.clear, 1f);
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
                DrawNeighborProjectiles(neighbor.map, neighbor.offset.ToVector3(), hostViewRect);
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

        /// <summary>
        /// 邻居地图上的弹丸（阶段5 跨缝交接的对侧段 / 邻图射手发射的本侧段）同样不会被
        /// 当前地图绘制——与 DrawNeighborPawns 同法：DrawPos + offset 平移后立即绘制。
        /// 交接瞬间两侧绘制坐标一致（统一坐标连续），视觉无跳变。
        /// 弹丸列表来自 listerThings 的 ThingRequestGroup.Projectile 缓存（零分配迭代）。
        /// </summary>
        private static void DrawNeighborProjectiles(Map neighborMap, Vector3 offset, CellRect hostViewRect)
        {
            var projectiles = neighborMap.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            for (var i = 0; i < projectiles.Count; i++)
            {
                var thing = projectiles[i];
                if (thing.Destroyed) continue;
                try
                {
                    var drawPos = thing.DrawPos + offset;
                    if (!hostViewRect.Contains(new IntVec3(
                            Mathf.FloorToInt(drawPos.x), 0, Mathf.FloorToInt(drawPos.z))))
                    {
                        continue;
                    }

                    thing.DrawNowAt(drawPos);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce($"[RimExodus] Failed to draw seamless neighbor projectile {thing}: {ex}", thing.thingIDNumber ^ 0x5eaf01);
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
        /// 收集邻居地图的地形层（SectionLayer_Terrain）、静态物层（SectionLayer_ThingsGeneral）、
        /// 光照遮罩层（SectionLayer_LightingOverlay）和战雾层（SectionLayer_FogOfWar），
        /// 用 offset 平移矩阵提交到 CommandBuffer。
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
                    CollectLayer(section, matrix, typeof(SectionLayer_LightingOverlay), NeighborGlowOnlyMaterial);
                    CollectLayer(section, matrix, typeof(SectionLayer_FogOfWar));
                }
            }

            // 天色分层（见类头注释）：邻图 overlay 已换 (1,1,1,0) 材质（只出 glow/roof），
            // 非重叠区（邻图方形 − 当前图方形，L 形 ≤4 个矩形）的天色由全零顶点 quad 补染。
            CollectSkyTintRects(neighborMap, offsetInt, offsetVec);
        }

        private static Material NeighborGlowOnlyMaterial
        {
            get
            {
                if (neighborGlowOnlyMat == null)
                {
                    neighborGlowOnlyMat = new Material(MatBases.LightOverlay)
                    {
                        name = "RimExodus_NeighborGlowOnly",
                        color = new Color(1f, 1f, 1f, 0f)
                    };
                }

                return neighborGlowOnlyMat;
            }
        }

        private static Mesh SkyTintQuadMesh
        {
            get
            {
                if (skyTintQuadMesh == null)
                {
                    skyTintQuadMesh = new Mesh { name = "RimExodus_SkyTintQuad" };
                    skyTintQuadMesh.vertices = new[]
                    {
                        new Vector3(-0.5f, 0f, -0.5f),
                        new Vector3(-0.5f, 0f, 0.5f),
                        new Vector3(0.5f, 0f, 0.5f),
                        new Vector3(0.5f, 0f, -0.5f)
                    };
                    var clear = new Color32(0, 0, 0, 0);
                    skyTintQuadMesh.colors32 = new[] { clear, clear, clear, clear };
                    skyTintQuadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                }

                return skyTintQuadMesh;
            }
        }

        /// <summary>
        /// 天色 quad：邻图方形 − 当前图方形的 L 形区域（最多 4 个矩形），每矩形一个全零顶点
        /// quad 配共享 MatBases.LightOverlay（每帧被当前图 SkyManager 染天色，与当前图恒一致）。
        /// 与邻图 glow 数据层、当前图 overlay 三者分工：当前图方形内的天色由当前图 overlay 独担
        /// （含 void 透明圈），邻图非重叠区由本 quad 独担——任意像素天色恰好一层，消除 void
        /// 透明圈的 sky² 双染暗带（2026-08 分层修复）。
        /// </summary>
        private void CollectSkyTintRects(Map neighborMap, IntVec3 offsetInt, Vector3 offsetVec)
        {
            var size = neighborMap.Size;

            // 当前图方形平移到邻图坐标（hostCell = neighborCell + offset → 原点 = -offset），
            // 与邻图方形求交得 overlap（两同尺寸方形平移交集，矩形）。
            var overlapMinX = Math.Max(0, -offsetInt.x);
            var overlapMinZ = Math.Max(0, -offsetInt.z);
            var overlapMaxX = Math.Min(size.x, size.x - offsetInt.x);
            var overlapMaxZ = Math.Min(size.z, size.z - offsetInt.z);
            if (overlapMinX >= overlapMaxX || overlapMinZ >= overlapMaxZ)
            {
                return; // 方形不相交（防御；中点对齐下必然相交）。
            }

            var y = AltitudeLayer.LightingOverlay.AltitudeFor();
            // L 形拆矩形：左右余条全高，上下余条限 overlap 的 x 范围（避免重复覆盖角部）。
            AddSkyTintQuad(0, 0, overlapMinX, size.z, y, offsetVec);
            AddSkyTintQuad(overlapMaxX, 0, size.x - overlapMaxX, size.z, y, offsetVec);
            AddSkyTintQuad(overlapMinX, 0, overlapMaxX - overlapMinX, overlapMinZ, y, offsetVec);
            AddSkyTintQuad(overlapMinX, overlapMaxZ, overlapMaxX - overlapMinX, size.z - overlapMaxZ, y, offsetVec);
        }

        private void AddSkyTintQuad(int minX, int minZ, int width, int height, float y, Vector3 offsetVec)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var center = new Vector3(minX + width / 2f + offsetVec.x, y, minZ + height / 2f + offsetVec.z);
            drawCommands.Add(new TerrainDrawCommand
            {
                mesh = SkyTintQuadMesh,
                material = MatBases.LightOverlay,
                matrix = Matrix4x4.TRS(center, Quaternion.identity, new Vector3(width, 1f, height)),
                renderQueue = MatBases.LightOverlay.renderQueue,
                sequence = nextSequence++
            });
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
        /// 收集指定精确类型的 SectionLayer 的 submesh。materialOverride 非空时替换 subMesh 材质提交。
        /// 必须用精确类型匹配：
        /// - SectionLayer_Watergen 继承自 SectionLayer_Terrain，但只能绘制到水深子相机，绝不能提交主相机。
        /// - SectionLayer_FogOfWar 现在收集：fog 层 mesh 顶点是绝对世界坐标（同 Terrain），可被 offset 矩阵正确平移；
        ///   全探索 section 的 fog submesh 被 Regenerate 设 disabled，下方 disabled 检查会跳过，零开销。
        /// - SectionLayer_LightingOverlay 必须收集，但材质必须换成 NeighborGlowOnlyMaterial（color=(1,1,1,0)，
        ///   只保留顶点色的 glow 加光与岩顶黑暗、天色染色归零）：原版亮度 = albedo × 该层顶点色 × 材质天色。
        ///   当前图 overlay 的 mesh 覆盖全图方形（含 void 格——其 (0,0,0,0) 顶点的语义是"按天色染色"而非
        ///   透传），若邻图 overlay 也用原材质，void 透明圈会被两层天色各染一次 = sky² 双染暗带。分层后：
        ///   天色染色只由"当前图 overlay（自己方形内）+ 天色 quad（邻图非重叠 L 形区）"各管一块，
        ///   glow/roof 数据层不受影响——重叠区两图 glow 叠加 = 跨缝照明（物理合理，接受）；void 格无
        ///   roof，岩顶不会双份。glow/roof 脏标记（Roofs|GroundGlow）由 EnsureSectionsGenerated 的
        ///   RegenerateAllLayers 连带重建消化。可见性开关 DebugViewSettings.drawLightingOverlay 经
        ///   下方 layer.Visible 检查自动尊重。
        /// - 仍不收集 SunShadows/Gas 等有 shadow/grid 依赖且不适合偏移绘制的层。
        /// </summary>
        private void CollectLayer(Section section, Matrix4x4 matrix, Type layerType, Material materialOverride = null)
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
                        material = materialOverride ?? subMesh.material,
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
