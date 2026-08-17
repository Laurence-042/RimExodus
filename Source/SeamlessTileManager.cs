using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 锚点地图（家园 A）上的无缝地块管理器。
    /// 负责生成/卸载无缝地块地图，并维护接缝关系。
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
        /// 锚点地图（家园 A）的直接邻居表。口袋地图的邻居表存于自身的 MapParent_SeamlessTile。
        /// 通过 <see cref="SeamlessTileGraph"/> 统一查询，屏蔽存储位置差异。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>是否已完成开档初始化（预铺传送点；void 已由 genStep 阶段铺设，不在此处）。阶段4a 后默认不自动生成邻居，除非 preloadAllNeighborsOnStart=true。</summary>
        private bool setupOnStartDone;

        /// <summary>
        /// 锚点地图的基础地形快照（阶段4 接缝覆写）：void 裁切前的完整矩形 topGrid。
        /// 在 GenStep_SeamlessTile（order=1400，Fog 之前）void 裁切之前备份（通过 BackupSnapshotAndApplyVoid 归一入口）。
        /// 供接缝条带快照捕获读取。非序列化（生成期临时数据）。
        /// </summary>
        public TerrainDef[] anchorBaseTerrainSnapshot;

        /// <summary>
        /// 锚点地图的原生建筑快照（与 <see cref="anchorBaseTerrainSnapshot"/> 同点位备份、非序列化）。
        /// 见 <see cref="MapParent_SeamlessTile.baseBuildingSnapshot"/>。
        /// </summary>
        public ThingDef[] anchorBaseBuildingSnapshot;

        /// <summary>
        /// 锚点地图的原生屋顶快照（与 <see cref="anchorBaseBuildingSnapshot"/> 同点位备份、非序列化）。
        /// 见 <see cref="MapParent_SeamlessTile.baseRoofSnapshot"/>。
        /// </summary>
        public RoofDef[] anchorBaseRoofSnapshot;

        /// <summary>
        /// 锚点地图的接缝条带快照（见 <see cref="SeamStripData"/>）。锚点=家园常驻不卸载，
        /// 挂 MapComponent 即可（地块图的快照挂 WorldObject 以存活于地图卸载）。
        /// </summary>
        public SeamStripData anchorSeamStrip;

        /// <summary>延迟开档初始化的 tick 计数（MapGenerated 时 mapBeingGenerated 可能仍非空，需延迟到下一 tick 调 TrySetupOnStart）。</summary>
        private int pendingAutoGenerateTicks = -1;

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
            Scribe_Deep.Look(ref anchorSeamStrip, "anchorSeamStrip");

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
                    TrySetupOnStart();
                }
            }
            // 消费异步预加载队列（全局静态队列，任意图块 tick 触发消费，幂等）。
            // 不限锚点地图：玩家聚焦口袋地图时也能及时消费（避免饥饿延迟）。
            SeamlessTilePreloader.ConsumeQueued();
        }

        public override void MapGenerated()
        {
            base.MapGenerated();
            // 仅锚点地图（家园 A）触发开档初始化，便于原型测试。
            // 地块地图（MapParent_SeamlessTile）的邻居生成不通过 MapGenerated 自动级联（避免生成风暴）。
            // 基础地图用 Parent 类型判断是否为地块地图（非 IsPocketMap）。
            if (map.Parent is MapParent_SeamlessTile) return;
            if (!setupOnStartDone)
            {
                setupOnStartDone = true;
                pendingAutoGenerateTicks = 1;
            }
        }

        /// <summary>
        /// 开档初始化（阶段4a：预铺传送点 + 可选加载所有邻居）。
        /// 在锚点地图 A 上沿全部世界邻居边预铺单端传送点（对端 null）。
        /// 若 ModSettings.preloadAllNeighborsOnStart 为 true，则额外加载全部世界邻居地块（高配玩家流畅体验）。
        /// 否则不生成邻居，等 pawn 接近边界时事件驱动加载。
        ///
        /// **void 铺设不在此处**：锚点 void 已由 RimExodus_SeamlessTile genStep（order=1400，Fog 之前）铺设，
        /// 通过 XML patch 注入到 Base_Player，与邻接地块走完全相同的 genStep 链（不再依赖 Harmony Postfix）。
        /// 此前这里是 MapGenerated 后延迟 1 tick 的"后补"铺 void（RefreshMapVoid），会真实删除已生成的
        /// 岩石/植物/玩家建造（落石/切断建筑），已废弃。anchorBaseTerrainSnapshot 备份在 genStep 1400 完成。
        /// </summary>
        private void TrySetupOnStart()
        {
            var anchorWorldTile = map.Tile;
            if (anchorWorldTile < 0) return;

            // 预铺锚点 A 沿全部世界邻居边的传送点（对端 null）。
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, anchorWorldTile);

            // 可选：开档加载全部世界邻居。
            var preloadAll = RimExodusMod.Settings?.preloadAllNeighborsOnStart ?? false;
            if (!preloadAll) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(anchorWorldTile, worldNeighbors);
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

            // 防递归守卫。
            if (MapGenerator.mapBeingGenerated != null)
            {
                Log.Warning($"[RimExodus] Cannot preload seamless tile map during map generation (mapBeingGenerated={MapGenerator.mapBeingGenerated.uniqueID}).");
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
            if (MapGenerator.mapBeingGenerated != null)
            {
                Log.Warning("[RimExodus] Cannot generate seamless tile map during map generation.");
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
            // 源是锚点 tile（非 MapParent_SeamlessTile）→ tileOrigin = (0,0)。
            // 源是地块 tile → 读 originTile.tileOrigin（沿邻居链累加）。
            var sourceTileOrigin = (map.Parent is MapParent_SeamlessTile originTile)
                ? originTile.tileOrigin
                : UnityEngine.Vector2.zero;
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
                    if (!Find.World.worldObjects.Contains(interiorMap.Parent))
                    {
                        Find.World.worldObjects.Add(interiorMap.Parent);
                    }
                    // 天气共享：按群系连通域绑定（全局天气状态注册机制——同群系邻接连通的图共享
                    // 一个天气源，宿主=域内最小 tileId 图；无锚点特殊论，家园图不特殊）。
                    SeamlessWeatherClusterManager.BindMap(interiorMap);
                    SeamlessNeighborRegistry.RegisterNeighborBidirectional(originMapCapture, mapParent, sourceWorldTileCapture, newWorldTile, hostOffset);
                    // 不刷新 originMapCapture 的 void——锚点 map 的 void 在 TrySetupOnStart 时已铺好，
                    // void 只看自己的多边形（不因邻居关系变化而变）。每次生成邻居都 RefreshMapVoid(锚点)
                    // 会重新清锚点 void 格上玩家游戏期间生长的植物/掉落物（耗时 12-23 秒）。
                    SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(originMapCapture, sourceWorldTileCapture);
                    SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(interiorMap, newWorldTile);
                    SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(originMapCapture);
                    SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(interiorMap);
                    AutoConnectWorldNeighbors(interiorMap, newWorldTile);

                    if (RimExodusMod.Settings?.verboseLogging ?? false)
                        Log.Message($"[RimExodus] Incremental generation post-config done for tile {newWorldTile} map {interiorMap.uniqueID}.");

                    // 清理防重入锁（分帧生成完成）。
                    ClearGeneratingTile(newWorldTile);
                });

            if (!started)
            {
                Log.Warning($"[RimExodus] IncrementalMapGenerator.Start failed for tile {newWorldTile}.");
                return null;
            }
            return mapParent;
        }

        /// <summary>
        /// 遍历 map 对应 worldTile 的世界邻居列表，对每个已加载（TryGetMapByWorldTile 命中）但尚未与 map 建立直接邻居关系的地图，
        /// 调 EnsureNeighborRegistered 补登记。使新地块加载时自动与所有相邻的已加载地块连接（渲染/寻路即用）。
        /// </summary>
        private static void AutoConnectWorldNeighbors(Map map, int worldTile)
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
        /// "完全卸载 = 从未出现过"：WorldObject（含接缝条带快照）一并销毁，不留休眠数据，
        /// 同 tile 再次预加载将全新生成。（与未来"滚动卸载保留 WorldObject"的休眠语义二分，
        /// 届时只需跳过 Destroy 并让 <see cref="SeamlessTileGraph"/> 的 WorldObject 查询路径接管参考数据。）
        /// </summary>
        public void RemoveTileMap(MapParent_SeamlessTile parent)
        {
            if (parent == null) return;

            var interiorMap = parent.Map;
            if (interiorMap != null)
            {
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

        /// <summary>本图被移除时重算天气域（覆盖家园图被原版销毁的场景，如 gravship 起飞——无锚点善后）。</summary>
        public override void MapRemoved()
        {
            base.MapRemoved();
            SeamlessWeatherClusterManager.RebindAll();
        }
    }
}
