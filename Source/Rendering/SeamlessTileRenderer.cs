using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimExodus
{
    /// <summary>
        /// 将当前地图的直接邻居地块作为背景绘制（对称架构）。
        /// 邻居地形/建筑/光照遮罩/战雾在原版当前地图之前提交。1.6 命令缓冲
        /// 在绘制后只清深度，使当前地图覆盖重叠带；1.7 UnityStubs 改用即时提交。
        /// 对称性：聚焦任意图块时，其所有直接邻居（原生 parent 图与地块图）都会被绘制。
        /// 光照天色分层（2026-08）：LightOverlay shader 的天色染色走材质 color alpha、
        /// glow/roof 走顶点色，两通道独立（实验验证）——邻图 overlay 用 (1,1,1,0) 克隆材质
        /// 做纯数据层，天色由"当前图 overlay（自己方形内）+ 全零顶点 quad（所有邻图方形并集
        /// 减去当前图方形）"各管一块，消除 void 透明圈与多邻图交界的 sky² 双染暗带。
    /// </summary>
    [StaticConstructorOnStartup] // 静态 AccessTools FieldRef 初始化走启动期；Material/Mesh 保持惰性构建，加特性消 Verse 启动分析器警告
    public class SeamlessTileRenderer : MapComponent
    {
        private const string CommandBufferName = "RimExodus Seamless Neighbor Terrain";
        private const string WaterDepthCommandBufferName = "RimExodus Seamless Neighbor Water Depth";

        // 1.7 Waylith 的 UnityStubs.CommandBuffer/Camera.AddCommandBuffer 是只告警的桩。
        // 其 MapDrawLayer 和 Watergen 都经 Graphics.DrawMesh 提交到对应 render layer。
        private static readonly bool useImmediateRendering =
            string.Equals(typeof(CommandBuffer).Assembly.GetName().Name, "UnityStubs", StringComparison.Ordinal);

        internal static bool IsWaylithRuntime => useImmediateRendering;

        private static readonly Type watergenLayerType = AccessTools.TypeByName("Verse.SectionLayer_Watergen");
        private static readonly int useWaterOffsetId = Shader.PropertyToID("_UseWaterOffset");

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
        private static readonly Dictionary<Material, Material> stillWaterDepthMats = new Dictionary<Material, Material>();

        /// <summary>(0,0,0,0) 顶点色的单位 quad——顶点零值在 LightOverlay shader 里的语义
        /// 不是透传而是"按材质色染天色"（原版 MapDrawLayer_ExteriorLightingOverlay 同款手法，
        /// 专门用来给图外区域染昼夜色）。天色分层的天色 quad 用它。</summary>
        private static Mesh skyTintQuadMesh;

        private static SeamlessTileRenderer activeRenderer;

        private readonly List<TerrainDrawCommand> drawCommands = new List<TerrainDrawCommand>();
        private readonly List<SeamlessTileGraph.NeighborInfo> cachedNeighbors = new List<SeamlessTileGraph.NeighborInfo>();
        private readonly List<Rect> skyTintRects = new List<Rect>();
        private readonly List<Rect> pendingSkyTintRects = new List<Rect>();
        private readonly List<Rect> skyTintSubtractBuffer = new List<Rect>();

        private CommandBuffer commandBuffer;
        private Camera attachedCamera;
        private CommandBuffer waterDepthCommandBuffer;
        private Camera attachedWaterDepthCamera;
        private int nextSequence;
        private RimExodus.Maps.MapDrawBackend frameworkTerrain;
        private RimExodus.Maps.MapDrawBackend frameworkWater;
        private readonly List<(RimExodus.Maps.MapDrawCommand command, int sequence)> frameworkCommands =
            new List<(RimExodus.Maps.MapDrawCommand, int)>();
        private void CollectFrameworkCommand(RimExodus.Maps.MapDrawCommand command)
            => frameworkCommands.Add((command, frameworkCommands.Count));

        private struct TerrainDrawCommand
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4 matrix;
            public int renderQueue;
            public int renderLayer;
            public int sequence;
        }

        public SeamlessTileRenderer(Map map) : base(map)
        {
        }

        public override void MapComponentDraw()
        {
            var probeT0 = RimExodusPerfProbe.Now();
            try
            {
                MapComponentDrawInner();
            }
            finally
            {
                RimExodusPerfProbe.Record(RimExodusPerfProbe.NeighborCompositeDraw, probeT0);
            }
        }

        private void MapComponentDrawInner()
        {
            if (Find.CurrentMap != map || !WorldRendererUtility.DrawingMap)
            {
                return;
            }

            // 表面层守卫（与 GetMapWorldTile 收口同族，勿删）：太空/轨道图（renderWorld
            // 模式下 DrawingMap 恒 true，上文守卫拦不住）的背景由世界相机（星空+星球层，
            // depth 0/1）先画好、主地图相机只清深度叠加——本 renderer 的全屏
            // ClearRenderTarget 会把星球背景整屏擦成黑（2026-08 太空背景变黑实测根因）。
            // 口袋图（VMF 载具内部图）同理排除。
            if (SeamlessTileRegistry.GetMapWorldTile(map) < 0)
            {
                return;
            }

            // 收集当前地图的所有直接邻居（对称：原生图/地块图都遍历）。复用缓存列表避免每帧分配。
            cachedNeighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(map, cachedNeighbors);
            if (SeamlessFramework.Enabled)
            {
                EnsureFrameworkBuffers();
                frameworkCommands.Clear();
                frameworkTerrain.BeginFrame(true, Color.clear);
                frameworkTerrain.BeginGroup();
                frameworkWater?.BeginFrame();
                frameworkWater?.BeginGroup();
            }
            if (cachedNeighbors.Count == 0)
            {
                if (SeamlessFramework.Enabled) return;
                // 1.7 的 CommandBuffer 清屏也是桩；即时路径没有待清理的相机命令。
                if (useImmediateRendering) return;
                // 零邻居（新档首图生成 / 孤岛地块 / 邻居全部卸载 / 读档邻居未再生成）时
                // 仍要执行全屏清色：void 地形画半透明 ShadowMask 不写不透明色，主相机只清
                // 深度——不清色的话 void 带保留上一帧像素，平移相机时残影累积成红色拖影。
                // 不画任何邻居 mesh，只做"清色-only"帧。同时 commandBuffer.Clear() 清掉
                // 陈旧命令——否则邻居全部卸载后，挂在相机上的旧 buffer 会逐帧重放对已
                // Dispose 地图 mesh 的 DrawMesh（void 带冻结显示旧邻居画面）。
                EnsureCommandBuffer();
                commandBuffer.Clear();
                waterDepthCommandBuffer?.Clear();
                commandBuffer.ClearRenderTarget(true, true, Color.clear, 1f);
                return;
            }

            if (!SeamlessFramework.Enabled && !useImmediateRendering)
            {
                EnsureCommandBuffer();
                commandBuffer.Clear();
                waterDepthCommandBuffer?.Clear();
                // 1.6 路径先清色与深度，避免 void 带残留上一帧内容。
                commandBuffer.ClearRenderTarget(true, true, Color.clear, 1f);
            }
            drawCommands.Clear();
            nextSequence = 0;

            // 宿主相机视区（cell 坐标，ExpandedBy(1) 镜像原版 MapDrawer.ViewRect，所有邻居共用）。
            // 此处 Find.CurrentMap == map 已由方法开头守卫保证，CurrentViewRect 即宿主视区。
            var hostViewRect = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);

            foreach (var neighbor in cachedNeighbors)
            {
                // 即时绘制与当前图进入同一个队列；略降邻图高度以缓解深度竞争。
                // 1.7 透明重叠带已实测，1.6 仍由命令缓冲清深度保证覆盖顺序。
                var offsetVec = neighbor.offset.ToVector3();
                if (useImmediateRendering && !SeamlessFramework.Enabled) offsetVec.y -= 0.01f;
                CollectNeighborLayers(neighbor.map, neighbor.offset, offsetVec, hostViewRect);
                if (SeamlessFramework.Enabled)
                    RimExodus.Maps.MapDrawCapture.Collect(() =>
                    {
                        // These RE draw selectors already supply destination coordinates, including Mote.
                        DrawNeighborPawns(neighbor.map, hostViewRect);
                        DrawNeighborProjectiles(neighbor.map, hostViewRect);
                        DrawNeighborRealtimeThings(neighbor.map, neighbor.offset.ToVector3(), hostViewRect);
                    }, Matrix4x4.identity, CollectFrameworkCommand);
                else
                {
                    DrawNeighborPawns(neighbor.map, hostViewRect);
                    DrawNeighborProjectiles(neighbor.map, hostViewRect);
                    DrawNeighborRealtimeThings(neighbor.map, neighbor.offset.ToVector3(), hostViewRect);
                }
            }

            CollectSkyTintRects();

            if (SeamlessFramework.Enabled)
            {
                foreach (var command in drawCommands)
                    CollectFrameworkCommand(new RimExodus.Maps.MapDrawCommand(command.mesh,
                        command.matrix, command.material, layer: command.renderLayer));
                frameworkCommands.Sort((a, b) =>
                {
                    int queue = a.command.RenderQueue.CompareTo(b.command.RenderQueue);
                    return queue != 0 ? queue : a.sequence.CompareTo(b.sequence);
                });
                foreach (var command in frameworkCommands) frameworkTerrain.Draw(command.command);
                frameworkTerrain.Finish();
                frameworkCommands.Clear(); // Native property ranges are valid only during this frame.
                return;
            }

            if (drawCommands.Count == 0)
            {
                return;
            }

            drawCommands.Sort(CompareDrawCommands);
            foreach (var drawCommand in drawCommands)
            {
                if (useImmediateRendering)
                    Graphics.DrawMesh(drawCommand.mesh, drawCommand.matrix, drawCommand.material, drawCommand.renderLayer);
                else
                    commandBuffer.DrawMesh(drawCommand.mesh, drawCommand.matrix, drawCommand.material);
            }

            // 1.6 命令缓冲保留邻图颜色后清深度，让当前图覆盖重叠带。
            // 1.7 即时路径没有可用的清深度命令，靠上述小幅高度差处理重叠。
            if (!useImmediateRendering)
                commandBuffer.ClearRenderTarget(true, false, Color.clear, 1f);
        }

        /// <summary>
        /// 邻居地图上的 Pawn 不会被当前地图的 DynamicDrawManager 绘制，
        /// 因此需要手动以 offset 平移后的位置立即绘制一遍。
        /// Thing.DrawNowAt 接受显式坐标，绕开 DrawPos/Position。
        /// 视区裁剪：只绘制平移后落在宿主相机视区内的 pawn。
        /// </summary>
        private static void DrawNeighborPawns(Map neighborMap, CellRect hostViewRect)
        {
            foreach (var pawn in neighborMap.mapPawns.AllPawnsSpawned)
            {
                try
                {
                    if (!SeamlessViewProjection.TryProject(neighborMap, pawn.DrawPos, Find.CurrentMap, out var drawPos))
                    {
                        continue;
                    }
                    // 宿主坐标系判定：pawn 平移后的世界坐标转 cell 坐标后是否在视区内。
                    if (!hostViewRect.Contains(new IntVec3(
                            Mathf.FloorToInt(drawPos.x), 0, Mathf.FloorToInt(drawPos.z))))
                    {
                        continue;
                    }

                    // 雾过滤镜像 DynamicDrawManager：原版靠"雾中不画"而非 fog mesh 遮挡隐藏雾中动态绘制物，
                    // 立即模式绘制绕过了该管理器，必须自带此判定（seeThroughFog 豁免同原版）。
                    if (!pawn.def.seeThroughFog && neighborMap.fogGrid.IsFogged(pawn.Position))
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
        private static void DrawNeighborProjectiles(Map neighborMap, CellRect hostViewRect)
        {
            var projectiles = neighborMap.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            for (var i = 0; i < projectiles.Count; i++)
            {
                var thing = projectiles[i];
                if (thing.Destroyed) continue;
                try
                {
                    if (!SeamlessViewProjection.TryProject(neighborMap, thing.DrawPos, Find.CurrentMap, out var drawPos))
                    {
                        continue;
                    }
                    if (!hostViewRect.Contains(new IntVec3(
                            Mathf.FloorToInt(drawPos.x), 0, Mathf.FloorToInt(drawPos.z))))
                    {
                        continue;
                    }

                    if (!thing.def.seeThroughFog && neighborMap.fogGrid.IsFogged(thing.Position))
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

        /// <summary>
        /// 邻图上的 RealtimeOnly thing（2026-09 AncientHeatVent 邻图背景不可见定案修复）：
        /// 这类 thing 的**唯一**绘制路径是聚焦图的 DynamicDrawManager（原版 Map.MapUpdate 的
        /// CurrentMap 门内），静态 mesh 印刷被 SectionLayer_Things.Regenerate 的 RealtimeOnly
        /// 过滤结构性排除——四个收集层 + 旧的两类手画（pawn/弹丸）都不覆盖它，跨缝看整只缺失。
        /// 与 pawn/弹丸同法平移绘制（DrawNowAt + 视区裁剪 + 雾过滤镜像 DynamicDrawManager）。
        /// **只挑 drawerType == RealtimeOnly**：MapMeshAndRealTime 的本体已在 mesh 里（再画双画）；
        /// pawn / Projectile 各有专门通道（再画双画），显式排除。列表来自
        /// DynamicDrawManager.DrawThings（公开只读属性，即聚焦图动态管线自己的登记表）。
        /// 已知边界：thing 的自定义 DrawAt 若读 Find.CurrentMap 全局态（罕见 mod 写法）会画错
        /// 位置——与 pawn 通道同级的既有风险面，try/catch 防崩。
        /// </summary>
        private static void DrawNeighborRealtimeThings(Map neighborMap, Vector3 drawOffset, CellRect hostViewRect)
        {
            var drawThings = neighborMap.dynamicDrawManager.DrawThings;
            for (var i = 0; i < drawThings.Count; i++)
            {
                var thing = drawThings[i];
                if (thing == null || thing.Destroyed) continue;
                if ((thing is not Mote && thing.def.drawerType != DrawerType.RealtimeOnly)
                    || thing is Pawn || thing is Projectile || SeamlessDirectFireSupport.IsExternalProjectile(thing))
                {
                    continue;
                }

                try
                {
                    if (!SeamlessViewProjection.TryProject(neighborMap, thing.DrawPos, Find.CurrentMap, out var drawPos))
                    {
                        continue;
                    }
                    // 宿主坐标系判定：平移后的世界坐标转 cell 坐标后是否在视区内。
                    if (!hostViewRect.Contains(new IntVec3(
                            Mathf.FloorToInt(drawPos.x), 0, Mathf.FloorToInt(drawPos.z))))
                    {
                        continue;
                    }

                    // 雾过滤镜像 DynamicDrawManager（同 DrawNeighborPawns 的判据）。
                    if (!thing.def.seeThroughFog && neighborMap.fogGrid.IsFogged(thing.Position))
                    {
                        continue;
                    }

                    if (thing is Mote)
                    {
                        using (SeamlessCompositeDrawContext.Push(drawOffset)) thing.DrawNowAt(thing.DrawPos);
                    }
                    else
                    {
                        thing.DrawNowAt(drawPos);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce($"[RimExodus] Failed to draw seamless neighbor realtime thing {thing}: {ex}", thing.thingIDNumber ^ 0x5eaf02);
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
            var waterDepthCamera = Current.SubcameraDriver?.GetSubcamera(SubcameraDefOf.WaterDepth);
            if (commandBuffer != null && attachedCamera == camera &&
                attachedWaterDepthCamera == waterDepthCamera)
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

            if (waterDepthCamera != null)
            {
                waterDepthCommandBuffer = new CommandBuffer
                {
                    name = WaterDepthCommandBufferName
                };
                attachedWaterDepthCamera = waterDepthCamera;
                attachedWaterDepthCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, waterDepthCommandBuffer);
            }
            activeRenderer = this;
        }

        private void EnsureFrameworkBuffers()
        {
            if (activeRenderer != null && activeRenderer != this) activeRenderer.ReleaseCommandBuffer();
            if (frameworkTerrain == null)
                frameworkTerrain = new RimExodus.Maps.MapDrawBackend(false, CommandBufferName);
            frameworkTerrain.Attach(Find.Camera);
            var water = Current.SubcameraDriver?.GetSubcamera(SubcameraDefOf.WaterDepth);
            if (water != null || useImmediateRendering)
            {
                if (frameworkWater == null)
                    frameworkWater = new RimExodus.Maps.MapDrawBackend(false, WaterDepthCommandBufferName, preserveRenderLayer: true);
                frameworkWater.Attach(water);
            }
            else { frameworkWater?.Dispose(); frameworkWater = null; }
            activeRenderer = this;
        }

        private void ReleaseCommandBuffer()
        {
            frameworkTerrain?.Dispose(); frameworkTerrain = null;
            frameworkWater?.Dispose(); frameworkWater = null;
            if (attachedCamera != null && commandBuffer != null)
            {
                attachedCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, commandBuffer);
            }

            commandBuffer?.Release();
            commandBuffer = null;
            attachedCamera = null;

            if (attachedWaterDepthCamera != null && waterDepthCommandBuffer != null)
            {
                attachedWaterDepthCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, waterDepthCommandBuffer);
            }

            waterDepthCommandBuffer?.Release();
            waterDepthCommandBuffer = null;
            attachedWaterDepthCamera = null;

            if (activeRenderer == this)
            {
                activeRenderer = null;
            }
        }

        /// <summary>
        /// 收集邻居地图的地形层（SectionLayer_Terrain）、静态物层（SectionLayer_ThingsGeneral）、
        /// 光照遮罩层（SectionLayer_LightingOverlay）和战雾层（SectionLayer_FogOfWar），
        /// 用 offset 平移矩阵收集；1.6 提交到 CommandBuffer，1.7 即时提交。
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

            // 把宿主视区平移到邻居坐标系：邻居本地坐标 + offset = 宿主坐标，
            // 故邻居视区 = 宿主视区 - offset。ClipInsideMap 防越界误判。
            var neighborView = hostViewRect.MovedBy(-offsetInt).ClipInsideMap(neighborMap);
            // CurrentViewRect 每帧首次读取后缓存，而相机可能在稍后继续移动。WaterDepth 若只按该
            // 矩形提交，新露出的 RT 侧边会 Clamp 成纵向水波。深度层多留当前缩放的半屏余量；
            // 相机单帧移动不可能跨过整个半屏，且仅多覆盖边缘少量 section，无额外缓存/回调。
            var waterDepthView = hostViewRect.ExpandedBy(Mathf.CeilToInt(Find.CameraDriver.ZoomRootSize) + 1)
                .MovedBy(-offsetInt).ClipInsideMap(neighborMap);

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
                    var inColorView = neighborView.Overlaps(section.Bounds);
                    var inWaterDepthView = waterDepthView.Overlaps(section.Bounds);
                    if (!inColorView && !inWaterDepthView)
                    {
                        continue;
                    }

                    // 视区内的脏 section 复用原版 Section.TryUpdate(view)（2026-09 性能修复，勿回退为
                    // "任意脏 → RegenerateAllLayers + 清零"的全图全层扫描）：TryUpdate 是参数化视区方法
                    // （原版 MapMeshDrawerUpdate_First 只对 CurrentMap 调它，但方法体无全局态依赖——
                    // 传平移后的 neighborView 即可），只重建 dirtyFlags 命中的层、视区外层保留 Dirty。
                    // 旧实现在暴雨/暴雪期间是每帧重建风暴：SteadyEnvironmentEffects 每 tick 采样
                    // Area×0.0006 格持续标脏 Snow|Things（降雪 AddDepth / 融雪 / 雨冲污物），降频邻图
                    // 照常积脏，而邻图的脏标记唯一消费者是本渲染器 → 任意脏 = 全层重建（Terrain 顶点
                    // 重排 + ThingsGeneral 全量重印刷 + LightingOverlay 逐格 glow 采样）× 无视区 ×
                    // 无上限，单帧数 ms~数十 ms。
                    // Snow 位命中我们不收集的雪层也照常重建（勿剥位省性能）：dirtyFlags 清零后无人
                    // 再标记，邻图转正为 CurrentMap 时雪层 mesh 将永久 stale——正确性不可换性能。
                    // 视区外脏 section 的 dirtyFlags 保留：stale mesh 本就不被收集（同一视区判定），
                    // 点击拾取走 thingGrid/thing collider 不依赖 section mesh；相机平移后新进视区的
                    // section 当帧重建，邻图被切为 CurrentMap 时由原版路径闭环（layer.Dirty 保留正是
                    // TryUpdate 留给 DrawSection 兜底的原生语义）。
                    if (section.dirtyFlags != 0)
                    {
                        section.TryUpdate(waterDepthView);
                    }

                    if (inColorView)
                    {
                        CollectLayer(section, matrix, typeof(SectionLayer_Terrain));
                        CollectLayer(section, matrix, typeof(SectionLayer_ThingsGeneral));
                        CollectLayer(section, matrix, typeof(SectionLayer_LightingOverlay), NeighborGlowOnlyMaterial);
                        CollectLayer(section, matrix, typeof(SectionLayer_FogOfWar));
                    }

                    if (inWaterDepthView) CollectNeighborWaterDepth(section, matrix);
                }
            }

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
        /// 天色 quad：所有邻图方形的并集 − 当前图方形，拆成互不重叠的矩形，每块一个全零顶点
        /// quad 配共享 MatBases.LightOverlay（每帧被当前图 SkyManager 染天色，与当前图恒一致）。
        /// 与邻图 glow 数据层、当前图 overlay 三者分工：当前图方形内的天色由当前图 overlay 独担
        /// （含 void 透明圈），邻图非重叠区由本 quad 独担——两张邻图在当前图外相交时也只染一次。
        /// </summary>
        private void CollectSkyTintRects()
        {
            if (!DebugViewSettings.drawLightingOverlay) return;

            skyTintRects.Clear();
            var host = Rect.MinMaxRect(0f, 0f, map.Size.x, map.Size.z);
            foreach (var neighbor in cachedNeighbors)
            {
                if (sectionsRef(neighbor.map.mapDrawer) == null) continue;
                var offset = neighbor.offset;
                var size = neighbor.map.Size;
                pendingSkyTintRects.Clear();
                pendingSkyTintRects.Add(Rect.MinMaxRect(offset.x, offset.z,
                    offset.x + size.x, offset.z + size.z));

                // 先排除当前图方形，再排除已由先前邻图负责的区域。余下矩形加入并集；
                // 每帧最多六个邻图，复用三个 List 避免热路径分配。
                SubtractSkyTintRect(host);
                for (var i = 0; i < skyTintRects.Count && pendingSkyTintRects.Count > 0; i++)
                    SubtractSkyTintRect(skyTintRects[i]);
                skyTintRects.AddRange(pendingSkyTintRects);
            }

            var y = AltitudeLayer.LightingOverlay.AltitudeFor();
            foreach (var rect in skyTintRects) AddSkyTintQuad(rect, y);
        }

        private void SubtractSkyTintRect(Rect cut)
        {
            skyTintSubtractBuffer.Clear();
            foreach (var source in pendingSkyTintRects)
            {
                var minX = Mathf.Max(source.xMin, cut.xMin);
                var maxX = Mathf.Min(source.xMax, cut.xMax);
                var minZ = Mathf.Max(source.yMin, cut.yMin);
                var maxZ = Mathf.Min(source.yMax, cut.yMax);
                if (minX >= maxX || minZ >= maxZ)
                {
                    skyTintSubtractBuffer.Add(source);
                    continue;
                }

                // 半开矩形差集：上下余条全宽，左右余条仅占交集高度，互不重叠。
                AddSkyTintRect(source.xMin, source.yMin, source.xMax, minZ);
                AddSkyTintRect(source.xMin, maxZ, source.xMax, source.yMax);
                AddSkyTintRect(source.xMin, minZ, minX, maxZ);
                AddSkyTintRect(maxX, minZ, source.xMax, maxZ);
            }

            pendingSkyTintRects.Clear();
            pendingSkyTintRects.AddRange(skyTintSubtractBuffer);
        }

        private void AddSkyTintRect(float minX, float minZ, float maxX, float maxZ)
        {
            if (minX < maxX && minZ < maxZ)
                skyTintSubtractBuffer.Add(Rect.MinMaxRect(minX, minZ, maxX, maxZ));
        }

        private void AddSkyTintQuad(Rect rect, float y)
        {
            var center = new Vector3(rect.center.x, y, rect.center.y);
            drawCommands.Add(new TerrainDrawCommand
            {
                mesh = SkyTintQuadMesh,
                material = MatBases.LightOverlay,
                matrix = Matrix4x4.TRS(center, Quaternion.identity, new Vector3(rect.width, 1f, rect.height)),
                renderQueue = MatBases.LightOverlay.renderQueue,
                renderLayer = 0,
                sequence = nextSequence++
            });
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
        ///   天色染色只由"当前图 overlay（自己方形内）+ 天色 quad（邻图方形并集的剩余区域）"各管一块，
        ///   glow/roof 数据层不受影响——重叠区两图 glow 叠加 = 跨缝照明（物理合理，接受）；void 格无
        ///   roof，岩顶不会双份。glow/roof 脏标记（Roofs|GroundGlow）由收集循环内的 Section.TryUpdate
        ///   视区重建消化。可见性开关 DebugViewSettings.drawLightingOverlay 经
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
                        renderLayer = subMesh.renderLayer,
                        sequence = nextSequence++
                    });
                }
            }
        }

        /// <summary>
        /// TerrainWater 的主相机颜色 pass 必须与 WaterDepth 子相机里的同位置 Watergen 几何配对。
        /// 1.6 经子相机 buffer 提交，1.7 经原版 WaterDepth render layer 提交；
        /// 两路都复用原版相机与 RT，不创建每图水体目标。
        /// </summary>
        private void CollectNeighborWaterDepth(Section section, Matrix4x4 matrix)
        {
            if ((SeamlessFramework.Enabled ? frameworkWater == null
                : !useImmediateRendering && waterDepthCommandBuffer == null) || watergenLayerType == null) return;
            var layers = layersRef(section);
            if (layers == null) return;

            foreach (var layer in layers)
            {
                if (layer.GetType() != watergenLayerType || !layer.Visible) continue;
                foreach (var subMesh in layer.subMeshes)
                {
                    if (!subMesh.finalized || subMesh.disabled || subMesh.material == null) continue;
                    var material = StillWaterDepthMaterial(subMesh.material);
                    if (SeamlessFramework.Enabled)
                        frameworkWater.Draw(new RimExodus.Maps.MapDrawCommand(subMesh.mesh, matrix,
                            material, layer: SubcameraDefOf.WaterDepth.LayerId));
                    else if (useImmediateRendering)
                        Graphics.DrawMesh(subMesh.mesh, matrix, material, SubcameraDefOf.WaterDepth.LayerId);
                    else
                        waterDepthCommandBuffer.DrawMesh(subMesh.mesh, matrix, material);
                }
            }
        }

        /// <summary>邻图不复合各自河流 flow texture，故只在 Watergen pass 关闭流向偏移。</summary>
        private static Material StillWaterDepthMaterial(Material material)
        {
            if (!material.HasProperty(useWaterOffsetId) || Mathf.Approximately(material.GetFloat(useWaterOffsetId), 0f))
                return material;
            if (stillWaterDepthMats.TryGetValue(material, out var cached)) return cached;
            cached = new Material(material);
            cached.SetFloat(useWaterOffsetId, 0f);
            stillWaterDepthMats.Add(material, cached);
            return cached;
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
