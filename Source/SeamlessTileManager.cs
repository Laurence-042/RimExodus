using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块管理器（MapComponent，由 Map.FillComponents 自动挂到**每张图**——不止地块图）。
    /// 地块图上负责生成/删除与接缝维护；原生 parent 图（家园/原生家族 Settlement 等）上充当
    /// 数据存储载体（邻居表/条带快照/基础三层快照，原生 MapParent 挂不了我们的字段——
    /// 读写经 <see cref="SeamlessMapData"/>）与滚动删除入口。
    ///
    /// 邻居表以 worldTile 为主键，offset 隐式编码方向（阶段4a 全面重构后无 direction/edgeAngle 字段）。
    /// 多边形裁切用内切圆顶点模型（顶点 = center + 0.5S × 方向）。
    /// </summary>
    public class SeamlessTileManager : MapComponent
    {
        /// <summary>
        /// 道路接缝锚点内偏格数（锚点 = 新 void 边界内侧第一格，再向地图中心偏移这么多格）。
        /// 0 = 贴 void 边界（路铺到带外圈，跨缝两侧路相接）。锚点基位由
        /// <see cref="SeamlessPolygonGeometry.ComputeSeamCellForEdge"/> 按接缝带几何计算（与带宽无关）。
        ///
        /// 【历史】旧名 SeamOverlap（语义"offset 收缩 2 格形成重叠带"）→ 曾改 RoadAnchorInset=2
        /// （旧抽象下 = 贴旧 void 边）；接缝带定义变更后 void 边界退到带外圈外，固定内偏 2 格
        /// 使路出口距地图边缘 3-4 格跨缝断路（2026-08 用户实测），锚点改为按带几何计算后归 0。
        /// </summary>
        public const int RoadAnchorInset = 0;

        /// <summary>
        /// 原生 parent 图（家园/原生家族）的直接邻居表。地块图的邻居表存于自身的 MapParent_SeamlessTile。
        /// 通过 <see cref="SeamlessTileGraph"/> 统一查询，屏蔽存储位置差异。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>是否已完成开档接线（预铺传送点；void 已由 genStep 阶段铺设，不在此处）。阶段4a 后默认不自动生成邻居，除非 preloadAllNeighborsOnStart=true。</summary>
        private bool setupOnStartDone;

        /// <summary>
        /// 原生 parent 图的基础地形快照（阶段4 接缝覆写）：void 裁切前的完整矩形 topGrid。
        /// 在 GenStep_SeamlessTile（order=391）void 裁切之前备份（通过 BackupSnapshotAndApplyVoid 归一入口）。
        /// 供接缝条带快照捕获读取。非序列化（生成期临时数据）。读写经 <see cref="SeamlessMapData"/>。
        /// </summary>
        public TerrainDef[] baseTerrainSnapshot;

        /// <summary>
        /// 原生 parent 图的原生建筑快照（与 <see cref="baseTerrainSnapshot"/> 同点位备份、非序列化）。
        /// 见 <see cref="MapParent_SeamlessTile.baseBuildingSnapshot"/>。
        /// </summary>
        public ThingDef[] baseBuildingSnapshot;

        /// <summary>
        /// 原生 parent 图的原生屋顶快照（与 <see cref="baseBuildingSnapshot"/> 同点位备份、非序列化）。
        /// 见 <see cref="MapParent_SeamlessTile.baseRoofSnapshot"/>。
        /// </summary>
        public RoofDef[] baseRoofSnapshot;

        /// <summary>
        /// 原生 parent 图的接缝条带快照（见 <see cref="SeamStripData"/>）。随图组件序列化——
        /// 图在则数据在（软休眠不卸图）；图被滚动删除即失（再生成时由对端单侧照抄补缝连续）。
        /// 地块图的快照挂 WorldObject（卸图后存活）。读写经 <see cref="SeamlessMapData"/>。
        /// </summary>
        public SeamStripData seamStrip;

        /// <summary>延迟开档接线的 tick 计数（MapGenerated 时 mapBeingGenerated 可能仍非空，需延迟到下一 tick 调 SetupNativeParentMap）。</summary>
        private int pendingAutoGenerateTicks = -1;

        /// <summary>
        /// 正在经 RimExodus 预加载链原生生成 Settlement 据点图（try/finally 维护，勿手工置位）。
        /// 供 <c>Patch_Settlement_PostMapGenerate_SkipDetectionRaids</c> 判定跳过 TimedDetectionRaids
        /// 倒计时——原版该倒计时语义是"玩家闯入/进攻据点被发现的报复"，中立据点的预加载生成不该启动。
        /// </summary>
        internal static bool GeneratingSettlementSeamlessly;

        /// <summary>
        /// 当前邻接生成（预加载链）的源 worldTile（生成方向）。供 <c>Patch_GenStep_Fog_SeamOrigin</c>
        /// 的接缝洪水揭雾取根（"只从生成方向的接缝开始 unfog"）；-1 或非本图邻居（异常悬挂容错）
        /// 时回退"全部活跃边"。GenerateTileMap 两路径设置，onComplete/失败分支/finally 复位。
        /// </summary>
        internal static int NeighborGenerationSourceTile = -1;

        /// <summary>
        /// 正在生成中的 worldTile 集合（阶段4a：防重入）。
        /// 生成地图是重操作（MapGenerator.GenerateMap），生成过程中若再次请求同一 worldTile 的生成会被拒绝。
        /// RimWorld 单线程 tick，无需锁。
        /// </summary>
        private readonly HashSet<int> generatingTiles = new HashSet<int>();

        public SeamlessTileManager(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref setupOnStartDone, "setupOnStartDone");
            Scribe_Values.Look(ref pendingAutoGenerateTicks, "pendingAutoGenerateTicks", -1);
            Scribe_Deep.Look(ref seamStrip, "seamStrip");

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                neighbors?.RemoveAll(n => n == null || n.neighbor == null);
            }
            Scribe_Collections.Look(ref neighbors, "neighbors", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                neighbors ??= new List<NeighborLink>(6);
                neighbors.RemoveAll(n => n == null || n.neighbor == null);
            }
        }

        /// <summary>获取指向 worldTile 的邻居连接（null 表示无此邻居）。</summary>
        public NeighborLink GetNeighborByWorldTile(int targetWorldTile)
        {
            foreach (var link in neighbors)
            {
                if (link != null && link.worldTile == targetWorldTile && link.neighbor != null)
                {
                    return link;
                }
            }
            return null;
        }

        /// <summary>设置指向 worldTile 的邻居连接（覆盖或新增）。</summary>
        public void SetNeighbor(int worldTile, MapParent neighbor, IntVec3 offset)
        {
            neighbors.RemoveAll(n => n != null && n.worldTile == worldTile);
            if (neighbor == null) return;
            neighbors.Add(new NeighborLink
            {
                worldTile = worldTile,
                neighbor = neighbor,
                offset = offset
            });
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (pendingAutoGenerateTicks > 0)
            {
                pendingAutoGenerateTicks--;
                if (pendingAutoGenerateTicks == 0)
                {
                    pendingAutoGenerateTicks = -1;
                    SetupNativeParentMap();
                }
            }
            // 消费异步预加载队列（全局静态队列，任意图块 tick 触发消费，幂等）。
            // 不限地图类型：玩家聚焦口袋地图时也能及时消费（避免饥饿延迟）。
            SeamlessTilePreloader.ConsumeQueued();
        }

        public override void MapGenerated()
        {
            base.MapGenerated();
            // 原生 parent 图（家园/原生家族）统一触发开档接线（与 governor 管辖口径一致——
            // 见 SeamlessMapGovernance；2026-08 归一，此前注释自称"仅锚点图"但实际门是
            // 任意原生 parent，口径早已不一致）。
            // 地块图（MapParent_SeamlessTile）的接线在 GenerateTileMap 的 onComplete（不经
            // MapGenerated 自动级联，避免生成风暴）。
            if (map.Parent is MapParent_SeamlessTile) return;
            if (!setupOnStartDone)
            {
                setupOnStartDone = true;
                pendingAutoGenerateTicks = 1;
            }
        }

        /// <summary>
        /// 原生 parent 图统一接线（延迟 1 tick，MapGenerated 触发；家园与原生家族一视同仁——
        /// "一切地图对等"铁律）。步骤：
        /// ① 天气域绑定（<see cref="SeamlessWeatherClusterManager.BindMap"/>，幂等）；
        /// ② 沿全部世界邻居边预铺传送点（1490 已铺时幂等防御）；
        /// ③ AutoConnectWorldNeighbors——与已加载邻图补登记（2026-08 补的关键缺口：原生路径
        ///    （远行队进入据点/埋伏图）生成的图此前无人接线，图虽被 391/392/1490 裁切
        ///    （genStep 已 XML 注入 Base_Faction/Encounter），却接不进无缝网、无法跨缝互走）；
        /// ④ 刷新传送点对端缓存。
        ///
        /// **void 铺设不在此处**：void 已由 RimExodus_SeamlessTile genStep（order=391）铺设，
        /// 通过 XML patch 注入到 Base_Player/Base_Faction/Encounter，与地块图走完全相同的 genStep 链。
        /// baseTerrainSnapshot 备份在 genStep 391 完成。
        ///
        /// 可选级联预加载仅玩家家园（preloadAllNeighborsOnStart）——原生家族不级联
        /// （防生成风暴；家族图的"走近自动生成"是下一轮 Settlement 接入的任务）。
        /// </summary>
        private void SetupNativeParentMap()
        {
            // 表面层守卫（2026-08）：空间层图（SpaceMapParent 等）的 tileId 属轨道层，打进表面
            // WorldGrid 会读错地形/邻居数据（潜伏 bug 顺带修复）；口袋图 Tile 无效，同样早退。
            var worldTile = map.Tile;
            if (worldTile < 0 || worldTile.LayerDef != RimWorld.PlanetLayerDefOf.Surface) return;

            SeamlessWeatherClusterManager.BindMap(map);
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, worldTile);
            AutoConnectWorldNeighbors(map, worldTile);
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(map);

            // Settlement 无缝接入（2026-08）：原生入口（进攻/空投/dev）生成的据点图也补贸易商指定
            // （预加载路径在 GenerateTileMap 的 Settlement 分支内已即时指定）。
            if (map.Parent is Settlement nativeSettlement)
            {
                SeamlessSettlementTrader.EnsureTraderAssigned(nativeSettlement);
            }

            // 可选：仅玩家家园开档预加载全部邻居。
            if (!SeamlessMapGovernance.IsProtectedHome(map)) return;
            var preloadAll = RimExodusMod.Settings?.preloadAllNeighborsOnStart ?? false;
            if (!preloadAll) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            foreach (var neighborTile in worldNeighbors)
            {
                TryPreloadNeighbor(neighborTile.tileId);
            }
        }

        /// <summary>
        /// 预加载指定世界邻居地块（阶段4a：事件驱动预加载入口，带防重入与去重）。
        /// 在 this.map 上生成指向 targetWorldTile 的邻居地块。
        /// </summary>
        /// <returns>是否触发了生成（false = 已存在/正在生成/源无效）。</returns>
        public bool TryPreloadNeighbor(int targetWorldTile)
        {
            if (targetWorldTile < 0 || map == null) return false;

            // 防重入：正在生成中则拒绝。
            if (generatingTiles.Contains(targetWorldTile))
            {
                return false;
            }

            // 去重：该 worldTile 已是本地图直接邻居（已加载且已连接）则跳过。
            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, targetWorldTile, out _))
            {
                return false;
            }

            var sourceWorldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (sourceWorldTile < 0) return false;

            // 防递归守卫（分帧增量生成全程持有 mapBeingGenerated；IsAnyGenerating 兜底理论上的窗口差）。
            // MapPreview 预览在飞也避让（2026-08）：预览线程占用 MapGenerator.mapBeingGenerated 等
            // 进程级静态，此时启动增量会互踩（Start 覆盖预览的占用、预览收尾 finally 又清空我们的，
            // MapGenerator static/GL 上下文双向数据竞争）——MP 对原生 GenerateMap 有 WaitUntilIdle
            // 互斥但护不到自建增量流程，故在入口对等避让。
            if (MapGenerator.mapBeingGenerated != null || IncrementalMapGenerator.IsAnyGenerating
                || SeamlessMapPreviewCompat.IsPreviewInFlight)
            {
                // 忙态统一下一 tick 重排队（忙态必有尽头、重试有界；ConsumeQueued 消费前已清空
                // queuedHashes，重入队安全）：Settlement 走近下令不被静默吞（2026-08），普通 tile
                // 预加载意图同样保留（原"警告+丢弃"要玩家再次下令重触发，2026-08 一并改为重排队，
                // 预览/生成窗口短暂，1-2 tick 内消化）。
                SeamlessTilePreloader.QueuePreload(map, targetWorldTile);
                return false;
            }

            generatingTiles.Add(targetWorldTile);
            var mapSize = new IntVec3(map.Size.x, 1, map.Size.z);
            // 【实验分支】分帧增量生成：主线程每帧跑 1 genStep，不暂停 tick、无进度画面。
            // GenerateTileMap 内部启动 IncrementalMapGenerator（准备阶段同步，genStep 分帧，FinalizeInit 单帧）。
            // generating map 被 patch 跳过 MapPreTick/MapPostTick/MapUpdate，玩家可继续操作其他 map。
            var result = GenerateTileMap(sourceWorldTile, targetWorldTile, mapSize);
            if (result == null)
            {
                ClearGeneratingTile(targetWorldTile);
                return false;
            }
            // generatingTiles 防重入锁在分帧生成期间保持 true，由 GenerateTileMap 的 onComplete
            // 回调（FinishGeneration 完成后）调 ClearGeneratingTile 清理。
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Started incremental generation for world tile {targetWorldTile} (source={sourceWorldTile}).");
            return true;
        }

        /// <summary>分帧生成完成后清理防重入锁（被 GenerateTileMap 的 onComplete 回调调用）。</summary>
        private static void ClearGeneratingTile(int worldTile)
        {
            foreach (var m in Find.Maps)
            {
                m.GetComponent<SeamlessTileManager>()?.generatingTiles?.Remove(worldTile);
            }
        }

        /// <summary>
        /// 生成无缝地块口袋地图（分帧增量生成，主线程每帧跑 1 genStep，不暂停 tick）。
        /// 准备阶段（ConstructComponents→AddMap→组装 genSteps）同步完成，genStep 链分帧执行，
        /// FinalizeInit + 后续配置（邻居登记/传送点铺设/void 刷新）在最后帧的 onComplete 回调执行。
        /// 调用者：TryPreloadNeighbor（事件驱动预加载）、TrySetupOnStart（开档加载全部邻居）。
        /// </summary>
        public MapParent_SeamlessTile GenerateTileMap(int sourceWorldTile, int newWorldTile, IntVec3 mapSize)
        {
            // 入口防御（TryPreloadNeighbor 已带同款判据并重排队；此处兜底其他调用方——如
            // TrySetupOnStart）：预览在飞时启动增量同样互踩（见 TryPreloadNeighbor 注释）。
            if (MapGenerator.mapBeingGenerated != null || SeamlessMapPreviewCompat.IsPreviewInFlight)
            {
                Log.Warning("[RimExodus] Cannot generate seamless tile map during map generation or MapPreview preview.");
                return null;
            }

            // 防递归：该 worldTile 已有任意地图（含非直接邻居）则跳过，补登记邻居。
            if (SeamlessTileGraph.TryGetMapByWorldTile(newWorldTile, out var existingMap))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] World tile {newWorldTile} already has a map {existingMap.uniqueID}, skip generation.");
                EnsureNeighborRegistered(map, sourceWorldTile, existingMap, newWorldTile);
                return null;
            }

            // 休眠/占位守卫（2026-08 软休眠 + 同日类型通用化，勿删）：TryGetMapByWorldTile 已被
            // 休眠口径过滤，查不到休眠图；但休眠图的 Map 和 WorldObject 都还在（软休眠不卸载）——
            // 若不在此拦截，下面 MakeWorldObject 会造出同 tile 的第二个 parent（邻居表分裂、存档脏数据）。
            // 类型通用化（勿回退为 as MapParent_SeamlessTile）：玩家家园图的原生 parent 不是
            // SeamlessTile，旧转型令守卫失明——家园休眠时预加载家园 tile 会走完整生成链造出
            // 重复家园图（2026-08 实测，"没有特殊地图"铁律）。现认任意 MapParent：
            // 有活 Map（休眠图）→ 唤醒 + 补登记，不生成；MapParent_SeamlessTile 无 Map（历史
            // RemoveTileMap 残留孤儿）→ 销毁后继续生成（原防御）；Settlement 占位 → 原生生成
            // （2026-08 无缝接入，见下）；其他原生家族 parent 无 Map → 跳过（范围边界用户定夺）。
            var existingParent = Find.World.worldObjects.MapParentAt(new PlanetTile(newWorldTile));
            if (existingParent != null)
            {
                var liveMap = existingParent.Map;
                if (liveMap != null && !liveMap.Disposed)
                {
                    if (RimExodusMod.Settings?.verboseLogging ?? false)
                        Log.Message($"[RimExodus] World tile {newWorldTile} already has a live map {liveMap.uniqueID} "
                            + $"(parent {existingParent.def.defName}), waking/skipping instead of generating.");
                    SeamlessDormancyManager.Wake(liveMap, "generation guard (parent exists, prevent duplicate WorldObject)");
                    EnsureNeighborRegistered(map, sourceWorldTile, liveMap, newWorldTile);
                    return null;
                }
                if (existingParent is MapParent_SeamlessTile)
                {
                    // WorldObject 在但 Map 不在（软休眠下理论不可达；历史 RemoveTileMap bug 曾残留
                    // 隐形 WorldObject）——防御：销毁残留后继续走生成。
                    if (!existingParent.Destroyed)
                    {
                        Log.Warning($"[RimExodus] World tile {newWorldTile} has an orphan WorldObject without a map, destroying it before generation.");
                        existingParent.Destroy();
                    }
                }
                else if (existingParent is Settlement settlementParent)
                {
                    // Settlement 无缝接入（2026-08，用户定夺，勿回退为跳过）：原生 GetOrGenerateMap 生成据点图。
                    // 复用既有 Settlement WorldObject（不换 def）——据点建筑/驻军/重访重生成全原版语义；
                    // genStep 链走 Base_Faction/Base_Player（4 个裁切 genStep 已 XML 注入，生成期即被裁切+铺传送点）；
                    // 生命周期归原生家族滚动接管（≥2 眠 ≥3 删、删图留对象）。敌对据点同样生成——"玩家看到
                    // 全是敌人的据点，正常结束"；好感度可交易则随后指定贸易商。尺寸用调用方 mapSize（=源图尺寸，
                    // 接缝六边形几何按 mapSize 计算，必须与邻图一致）。方法入口的 mapBeingGenerated 守卫已防与
                    // 分帧增量生成交错（营地先例同款）；此处再查 IsAnyGenerating 兜底（TryPreloadNeighbor 已拦，
                    // 防御 Dev 直调路径）。
                    if (IncrementalMapGenerator.IsAnyGenerating)
                    {
                        Log.Warning("[RimExodus] Settlement native generation deferred: incremental generation in progress.");
                        return null;
                    }
                    // 生成标志（try/finally）：让 Settlement.PostMapGenerate 的 patch 撤销 TimedDetectionRaids
                    // 倒计时（中立据点预加载不该启动"被发现报复"计时，见 Patches_NativeMapFamily）。
                    Map settlementMap;
                    GeneratingSettlementSeamlessly = true;
                    NeighborGenerationSourceTile = sourceWorldTile; // 生成方向（Fog patch 接缝揭雾取根）
                    try
                    {
                        settlementMap = GetOrGenerateMapUtility.GetOrGenerateMap(new PlanetTile(newWorldTile), mapSize, null);
                    }
                    finally
                    {
                        GeneratingSettlementSeamlessly = false;
                        NeighborGenerationSourceTile = -1;
                    }
                    if (settlementMap != null)
                    {
                        // 源图↔据点图即时登记（双向邻居表 + 两端传送点补铺 + arrivals 刷新，幂等）；天气域绑定
                        // 与 AutoConnect 由 SetupNativeParentMap（MapGenerated 延迟 1 tick）幂等补齐其余接线。
                        EnsureNeighborRegistered(map, sourceWorldTile, settlementMap, newWorldTile);
                        SeamlessSettlementTrader.EnsureTraderAssigned(settlementParent);
                        Log.Message($"[RimExodus] Settlement at tile {newWorldTile} generated natively for seamless access " +
                                    $"(map {settlementMap.uniqueID}, def {existingParent.def.defName}).");
                    }
                    // 同步生成已完成；null 表示"未启动增量生成"，调用方按既有路径收尾（ClearGeneratingTile）。
                    return null;
                }
                else
                {
                    // 其他原生家族 def（Site/埋伏/遗迹/战场等）仍跳过——走近自动生成范围仅 Settlement（用户定夺）。
                    Log.Warning($"[RimExodus] World tile {newWorldTile} is occupied by {existingParent.def.defName} without a live map, skip generation.");
                    return null;
                }
            }

            var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("RimExodus_SeamlessTileMap");
            if (def == null)
            {
                Log.Error("[RimExodus] WorldObjectDef RimExodus_SeamlessTileMap not found.");
                return null;
            }

            var mapParent = (MapParent_SeamlessTile)WorldObjectMaker.MakeWorldObject(def);
            mapParent.worldTile = newWorldTile;
            // 阶段4前置：基础地图。mapParent.Tile 必须设为真实 PlanetTile，
            // 这样 map.TileInfo 自动读 Find.WorldGrid[Tile]（含真实 biome/hillness/mutators/rivers），
            // 原生 Coast/River/Delta 等 TileMutator 自然生效，无需 InjectRealTileInfo。
            mapParent.Tile = new PlanetTile(newWorldTile);
            var hostOffset = SeamlessNeighborRegistry.ComputeNeighborOffset(sourceWorldTile, newWorldTile, map);
            var sourceWorldTileCapture = sourceWorldTile;
            var originMapCapture = map;

            // 计算 new tile 在全局平面坐标系的原点（阶段4 接缝覆写预留）。
            // tileOrigin = 源 tile 的 tileOrigin + hostOffset（源→新的平面偏移）。
            // 源是原生 parent 图（家园等）→ tileOrigin = (0,0)；源是地块 tile → 读其 tileOrigin
            // （沿邻居链累加）。载体差异经 <see cref="SeamlessMapData.TileOrigin"/> 屏蔽。
            var sourceTileOrigin = SeamlessMapData.TileOrigin(map);
            mapParent.tileOrigin = sourceTileOrigin + new UnityEngine.Vector2(hostOffset.x, hostOffset.z);

            // 【实验分支】分帧增量生成：每帧跑 1 genStep，不暂停 tick（generating map 被 patch 跳过）。
            // 准备阶段（ConstructComponents→AddMap→组装 genSteps）同步完成，
            // genStep 链分帧执行，FinalizeInit + 后续配置在最后帧的 onComplete 执行。
            // 阶段4前置：基础地图的 TileInfo 自动读真实 WorldGrid 数据（含 mutators/rivers），
            // 无需 extraInitBeforeContentGen 注入，传 null。
            var started = IncrementalMapGenerator.Start(
                mapParent, mapSize, mapParent.MapGeneratorDef, mapParent.ExtraGenStepDefs,
                null,
                interiorMap =>
                {
                    // ===== 生成后配置（FinalizeInit 之后，主线程）=====
                    // 阶段4前置：基础地图，不加入 pocketMaps（那是口袋地图列表）。
                    // 基础地图由 Current.Game.AddMap（IncrementalMapGenerator 内部）加入 Find.Maps，
                    // 且 WorldObject 由 worldObjects.Add 注册到世界视图。
                    // 逐子步计时（verbose，2026-08）：本回调在 FinishGeneration 单帧内执行，
                    // 耗时计入 FinishGeneration timings 的 onComplete 段；此处拆出各子步。
                    var timer = SectionTimer.StartIf(RimExodusMod.Settings?.verboseLogging ?? false);
                    long tWorldAdd, tWeatherBind, tRegister, tPlaceOrigin, tRefreshOrigin, tRefreshNew, tAutoConnect;

                    if (!Find.World.worldObjects.Contains(interiorMap.Parent))
                    {
                        Find.World.worldObjects.Add(interiorMap.Parent);
                    }
                    tWorldAdd = timer?.Section() ?? 0;
                    // 天气共享：按群系连通域绑定（全局天气状态注册机制——同群系邻接连通的图共享
                    // 一个天气源，宿主=域内最小 tileId 图；家园图不特殊，任何图都可作为域宿主）。
                    SeamlessWeatherClusterManager.BindMap(interiorMap);
                    tWeatherBind = timer?.Section() ?? 0;
                    SeamlessNeighborRegistry.RegisterNeighborBidirectional(originMapCapture, mapParent, sourceWorldTileCapture, newWorldTile, hostOffset);
                    tRegister = timer?.Section() ?? 0;
                    // 不刷新 originMapCapture 的 void——void 只看自己的多边形（不因邻居关系变化而变），
                    // 且原生图（家园等）的 void 在其生成链 genStep 391 已铺好；每次生成邻居都刷新
                    // 会重新清 void 格上玩家游戏期间生长的植物/掉落物（耗时 12-23 秒）。
                    // interiorMap 自己的传送点由 GenStep_EnterSpots(1490) 在生成链内、Fog(1500) 之前铺
                    // （Fog 的 UnfogMapFromEdge fallback 依赖接缝语义 patch 在生成期生效，详见该 genStep
                    // 注释），此处不再重复；originMap 生成期 1490 已铺过全部邻居方向的点，此处补铺
                    // 纯防御（幂等）。
                    SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(originMapCapture, sourceWorldTileCapture);
                    tPlaceOrigin = timer?.Section() ?? 0;
                    SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(originMapCapture);
                    tRefreshOrigin = timer?.Section() ?? 0;
                    SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(interiorMap);
                    tRefreshNew = timer?.Section() ?? 0;
                    AutoConnectWorldNeighbors(interiorMap, newWorldTile);
                    tAutoConnect = timer?.Section() ?? 0;

                    if (timer != null)
                        Log.Message($"[RimExodus] Incremental generation post-config for tile {newWorldTile} map {interiorMap.uniqueID}: " +
                                    $"worldAdd={tWorldAdd}ms weatherBind={tWeatherBind}ms registerNeighbor={tRegister}ms " +
                                    $"placeOriginSpots={tPlaceOrigin}ms refreshOriginArrivals={tRefreshOrigin}ms refreshNewArrivals={tRefreshNew}ms " +
                                    $"autoConnect={tAutoConnect}ms.");

                    // 清理防重入锁（分帧生成完成）。
                    ClearGeneratingTile(newWorldTile);
                    NeighborGenerationSourceTile = -1;
                });

            if (!started)
            {
                Log.Warning($"[RimExodus] IncrementalMapGenerator.Start failed for tile {newWorldTile}.");
                NeighborGenerationSourceTile = -1;
                return null;
            }
            // 生成方向（Fog patch 接缝揭雾取根）——genStep 分帧期间保持，onComplete 复位。
            NeighborGenerationSourceTile = sourceWorldTile;
            return mapParent;
        }

        /// <summary>
        /// 遍历 map 对应 worldTile 的世界邻居列表，对每个已加载（TryGetMapByWorldTile 命中）但尚未与 map 建立直接邻居关系的地图，
        /// 调 EnsureNeighborRegistered 补登记。使新地块加载时自动与所有相邻的已加载地块连接（渲染/寻路即用）。
        /// </summary>
        internal static void AutoConnectWorldNeighbors(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            foreach (var neighborTile in worldNeighbors)
            {
                var neighborWorldTile = neighborTile.tileId;
                if (neighborWorldTile == worldTile) continue;
                // 已是直接邻居则跳过。
                if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, neighborWorldTile, out _)) continue;
                // 该世界邻居已有加载的地图则补登记。
                if (SeamlessTileGraph.TryGetMapByWorldTile(neighborWorldTile, out var existingMap))
                {
                    EnsureNeighborRegistered(map, worldTile, existingMap, neighborWorldTile);
                }
            }
        }

        /// <summary>
        /// 当目标 worldTile 已有地图但尚未与 originMap 建立直接邻居关系时（多跳间隙，如 C↔A 隔着 B），
        /// 补登记双向邻居表 + 补铺两端传送点 + 互绑。使 C 可以直接走到 A 而非生成 A 的副本。
        /// 若已是直接邻居则跳过（幂等）。
        /// </summary>
        private static void EnsureNeighborRegistered(Map originMap, int sourceWorldTile, Map existingMap, int existingWorldTile)
        {
            if (originMap == null || existingMap == null || originMap == existingMap) return;

            // 若已是直接邻居则无需补登记。
            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(originMap, existingWorldTile, out _))
            {
                return;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] EnsureNeighborRegistered: linking origin map {originMap.uniqueID}(wt={sourceWorldTile}) " +
                    $"with existing map {existingMap.uniqueID}(wt={existingWorldTile}) as direct neighbors.");

            // 计算 offset（existing 相对 origin）。两端须在世界网格上互为邻居。
            var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(sourceWorldTile, existingWorldTile, originMap, existingMap);
            var existingParent = existingMap.info.parent;

            // 双向登记邻居表（复用 RegisterNeighborBidirectional 逻辑）。
            SeamlessNeighborRegistry.RegisterNeighborBidirectional(originMap, existingParent, sourceWorldTile, existingWorldTile, offset);

            // 补铺两端传送点（幂等）。
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(originMap, sourceWorldTile);
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(existingMap, existingWorldTile);

            // void 不需要刷新：void 几何只取决于地图自己的六边形（与世界邻居关系无关），
            // 且两端地图的 void 在它们各自生成时（genStep 阶段）已铺好。此处再调 RefreshMapVoid 会
            // 重新触发 ClearThingsOnCells，删除地图上已生成的实体（玩家建造/植物/掉落物）。

            // 新铺的传送点需刷新对端坐标缓存（RegisterNeighborBidirectional 内已刷一次，
            // 但补铺的 spot 在其之后，需再刷一次覆盖到它们）。
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(originMap);
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(existingMap);
        }

        /// <summary>
        /// 卸载一个无缝地块地图：清理邻居表双向引用、移除地图、销毁 WorldObject。
        /// "删除 = 从未出现过"：WorldObject（含接缝条带快照）一并销毁，同 tile 再次预加载将全新生成。
        /// 与软休眠（<see cref="SeamlessDormancyManager"/>，2026-08）二分：休眠 = 一切保留只停模拟与显示；
        /// 删除 = 本方法（滚动距离策略的终点，governor 距离 ≥ deleteHops 时调用）。
        /// </summary>
        public void RemoveTileMap(MapParent_SeamlessTile parent)
        {
            if (parent == null) return;

            var interiorMap = parent.Map;
            if (interiorMap != null)
            {
                // 从休眠集合移除（图即将 Dispose，防引用泄漏；对活跃图无操作）。
                SeamlessDormancyManager.Forget(interiorMap);
                SeamlessNeighborRegistry.CleanupNeighborLinks(parent);
                // 第二参 false：DeinitAndRemoveMap 本身不销毁 WorldObject（原版行为），
                // 由下方显式 Destroy 统一处理（历史 bug：注释曾误以为它会清理 WorldObject，
                // 实际残留隐形 WorldObject，且下次生成同 tile 会产生重复对象）。
                Current.Game.DeinitAndRemoveMap(interiorMap, false);
            }
            if (!parent.Destroyed)
            {
                parent.Destroy();
            }
            // 天气域重算：被卸载的图可能是某域宿主，成员改绑新宿主。
            SeamlessWeatherClusterManager.RebindAll();
        }

        /// <summary>本图被移除时重算天气域（覆盖家园图被原版销毁的场景，如 gravship 起飞——无需额外善后）。</summary>
        public override void MapRemoved()
        {
            base.MapRemoved();
            SeamlessWeatherClusterManager.RebindAll();
        }

        /// <summary>
        /// 滚动删除统一入口（governor 距离 ≥ deleteHops / Dev Force Delete 调用，2026-08 归一）：
        /// 地块图 → <see cref="RemoveTileMap"/>（销毁 Map+WorldObject，"从未出现过"，同 tile 再进入走全新生成链）；
        /// 原生家族 → <see cref="RemoveNativeFamilyMap"/>（延迟执行原版删除偏好，见其注释）。
        /// 家园图不进本方法（governor 保活分支先行豁免；Dev 侧由
        /// <see cref="SeamlessMapGovernance.CanRollingDelete"/> 拦截）。
        /// </summary>
        public void RemoveRollingMap(MapParent parent)
        {
            if (parent is MapParent_SeamlessTile tileParent)
            {
                RemoveTileMap(tileParent);
                return;
            }
            RemoveNativeFamilyMap(parent);
        }

        /// <summary>
        /// 删除原生家族图（Settlement/Site/CaravansBattlefield/DestroyedSettlement，2026-08 用户定夺
        /// "延迟执行原版偏好"）：删除时机从原版"全员离开即删"（被动链已被
        /// <see cref="Patches_NativeMapFamily"/> 拦截）推迟到距离 ≥ deleteHops；删图时先跑原版
        /// <see cref="MapParent.ShouldRemoveMapNow"/> 取 alsoRemoveWorldObject——
        /// Settlement 删图留对象（世界图据点仍在、再访重生成驻军，与原版重访语义一致）、
        /// CaravansBattlefield/DestroyedSettlement 连对象删、Site 按 parts 语义（ConditionCauser/
        /// RaidSource 存活时保留对象）。原版判定 false（建筑/pawn 阻挡等）→ 本轮不删，
        /// 保持休眠待状态清除后下轮再评估（延迟语义 = 只改时机、不改谁决定）。
        /// </summary>
        private void RemoveNativeFamilyMap(MapParent parent)
        {
            if (parent == null || parent.Destroyed) return;

            var interiorMap = parent.Map;
            if (interiorMap == null) return; // 无图可删（governor/Dev 路径都带活图，纯防御）。

            // 原版延迟判定（未被 patch 的虚方法，无递归；须于删图前调用——其内部读 base.Map）。
            if (!parent.ShouldRemoveMapNow(out var alsoRemoveWorldObject))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Native family map wt={parent.Tile.tileId} ({parent.def.defName}) not removable by vanilla rules this sweep (blockers present), keeping.");
                return;
            }

            var tile = parent.Tile.tileId;
            var mapId = interiorMap.uniqueID;
            SeamlessDormancyManager.Forget(interiorMap);
            SeamlessNeighborRegistry.CleanupNeighborLinks(parent);
            // 第二参 false：DeinitAndRemoveMap 本身不销毁 WorldObject（原版行为），
            // 由下方按原版偏好统一处理（与 CheckRemoveMapNow 原方法体同式）。
            Current.Game.DeinitAndRemoveMap(interiorMap, false);
            if (!parent.Destroyed && (alsoRemoveWorldObject || parent.forceRemoveWorldObjectWhenMapRemoved))
            {
                parent.Destroy();
            }
            // 天气域重算：被卸载的图可能是某域宿主，成员改绑新宿主。
            SeamlessWeatherClusterManager.RebindAll();

            Log.Message($"[RimExodus] Dormancy DELETE (native family): map {mapId} wt={tile} parent={parent.def.defName} (alsoRemoveWorldObject={alsoRemoveWorldObject}) — governor rolling delete");
        }
    }
}
