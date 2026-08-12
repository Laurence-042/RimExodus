using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 宿主地图上的无缝地块管理器。
    /// 负责生成/卸载无缝地块口袋地图，并维护接缝关系。
    ///
    /// 邻居表以 worldTile 为主键，offset 隐式编码方向（阶段4a 全面重构后无 direction/edgeAngle 字段）。
    /// 多边形裁切用内切圆顶点模型（顶点 = center + 0.5S × 方向）。
    /// </summary>
    public class SeamlessTileManager : MapComponent
    {
        /// <summary>
        /// 接缝重叠带宽度（格）。邻居多边形相对当前地图多叠这么多格。
        /// 目的：容纳投影扭曲——相邻 tile 各自用自己中心的切平面基投影多边形，
        /// 共享边在两端局部坐标系有旋转偏差（赤道→北极累积约 30°），2 格重叠带吸收此偏差，
        /// 保证接缝处两端都有非 void 可站立格，传送落点安全、不漏 void 缝隙。
        /// 传送点铺在各端自己的多边形边上，pawn 踩端 spot 经 offset 映射到对端时落在重叠带内。
        /// </summary>
        public const int SeamOverlap = 2;

        /// <summary>
        /// 锚点地图（家园 A）的直接邻居表。口袋地图的邻居表存于自身的 MapParent_SeamlessTile。
        /// 通过 <see cref="SeamlessTileGraph"/> 统一查询，屏蔽存储位置差异。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>是否已完成开档初始化（预铺传送点；void 已由 genStep 阶段铺设，不在此处）。阶段4a 后默认不自动生成邻居，除非 preloadAllNeighborsOnStart=true。</summary>
        private bool setupOnStartDone;

        /// <summary>
        /// 锚点地图的基础地形快照（阶段4 接缝覆写）：void 裁切前的完整矩形 topGrid。
        /// 在 GenStep_SeamlessTile（order=1802，Fog 之后）void 裁切之前备份（通过 BackupSnapshotAndApplyVoid 归一入口）。
        /// 供接缝覆写卷积混合读取。非序列化。
        /// </summary>
        public TerrainDef[] anchorBaseTerrainSnapshot;

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
            // 阶段4前置：基础地图后 IsPocketMap 恒 false，改用 Parent 类型判断是否为地块地图。
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
        /// **void 铺设不在此处**：锚点 void 已由 RimExodus_SeamlessTile genStep（order=1802，Fog 之后）铺设，
        /// 通过 XML patch 注入到 Base_Player，与邻接地块走完全相同的 genStep 链（不再依赖 Harmony Postfix）。
        /// 此前这里是 MapGenerated 后延迟 1 tick 的"后补"铺 void（RefreshMapVoid），会真实删除已生成的
        /// 岩石/植物/玩家建造（落石/切断建筑），已废弃。anchorBaseTerrainSnapshot 备份在 genStep 1802 完成。
        /// </summary>
        private void TrySetupOnStart()
        {
            var anchorWorldTile = map.Tile;
            if (anchorWorldTile < 0) return;

            // 预铺锚点 A 沿全部世界邻居边的传送点（对端 null）。
            PlaceEnterSpotsAllNeighbors(map, anchorWorldTile);

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
        /// 刷新指定地图的 void 铺设：六边形内（含边）非 void，六边形外 void。可重复调用。
        ///
        /// **当前无调用者**。void 铺设已统一在 RimExodus_SeamlessTile genStep（order=1802）完成——
        /// 通过 XML patch 注入到所有玩家可进入的 MapGeneratorDef（Base_Player / Base_Faction / Encounter），
        /// 锚点家园与邻接地块（邻居地块的 mapGenerator 也是 Base_Player）走同一条 genStep 链。
        /// 此前锚点靠本方法在 MapGenerated 后延迟 1 tick 后补铺 void，会删除已生成实体（落石/切断建筑），已废弃。
        /// 保留本方法供未来读档重建或幂等兜底场景备用。
        /// </summary>
        internal static void RefreshMapVoid(Map map)
        {
            if (map == null) return;

            int worldTile;
            if (map.Parent is MapParent_SeamlessTile pocketParent)
            {
                worldTile = pocketParent.worldTile;
            }
            else
            {
                worldTile = map.Tile;
            }
            if (worldTile < 0) return;

            SeamlessTerrainFill.ApplyPolygonTerrain(map, worldTile);
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
            // 这样 map.TileInfo 自动读 Find.WorldGrid[Tile]（含真实 biome/hilliness/mutators/rivers），
            // 原生 Coast/River/Delta 等 TileMutator 自然生效，无需 InjectRealTileInfo。
            mapParent.Tile = new PlanetTile(newWorldTile);
            var anchorMap = SeamlessTileGraph.GetAnchorMap(map) ?? map;
            var hostOffset = ComputeNeighborOffset(sourceWorldTile, newWorldTile, map);
            var sourceWorldTileCapture = sourceWorldTile;
            var sourceMapCapture = map;

            // 计算 new tile 在全局平面坐标系的原点（阶段4 接缝覆写预留）。
            // tileOrigin = 源 tile 的 tileOrigin + hostOffset（源→新的平面偏移）。
            // 源是锚点 tile（非 MapParent_SeamlessTile）→ tileOrigin = (0,0)。
            // 源是口袋 tile → 读 sourceMapParent.tileOrigin（沿邻居链累加）。
            var sourceTileOrigin = (map.Parent is MapParent_SeamlessTile sourcePocket)
                ? sourcePocket.tileOrigin
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
                    // sky/weather 共享：基础地图原生会自建独立 manager。
                    // 原型阶段尝试共享锚点 manager（若运行时异常则注释掉，让各地块天气独立）。
                    interiorMap.skyManager = anchorMap.skyManager;
                    interiorMap.weatherDecider = anchorMap.weatherDecider;
                    interiorMap.weatherManager = anchorMap.weatherManager;
                    RegisterNeighborBidirectional(sourceMapCapture, mapParent, sourceWorldTileCapture, newWorldTile, hostOffset);
                    // 不刷新 sourceMapCapture 的 void——锚点 map 的 void 在 TrySetupOnStart 时已铺好，
                    // void 只看自己的多边形（不因邻居关系变化而变）。每次生成邻居都 RefreshMapVoid(锚点)
                    // 会重新清锚点 void 格上玩家游戏期间生长的植物/掉落物（耗时 12-23 秒）。
                    PlaceEnterSpotsAllNeighbors(interiorMap, newWorldTile);
                    PlaceEnterSpotsAllNeighbors(sourceMapCapture, sourceWorldTileCapture);
                    RefreshEnterSpotArrivals(sourceMapCapture);
                    RefreshEnterSpotArrivals(interiorMap);
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
        /// 当目标 worldTile 已有地图但尚未与 sourceMap 建立直接邻居关系时（多跳间隙，如 C↔A 隔着 B），
        /// 补登记双向邻居表 + 补铺两端传送点 + 互绑。使 C 可以直接走到 A 而非生成 A 的副本。
        /// 若已是直接邻居则跳过（幂等）。
        /// </summary>
        private static void EnsureNeighborRegistered(Map sourceMap, int sourceWorldTile, Map existingMap, int existingWorldTile)
        {
            if (sourceMap == null || existingMap == null || sourceMap == existingMap) return;

            // 若已是直接邻居则无需补登记。
            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(sourceMap, existingWorldTile, out _))
            {
                return;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] EnsureNeighborRegistered: linking source map {sourceMap.uniqueID}(wt={sourceWorldTile}) " +
                    $"with existing map {existingMap.uniqueID}(wt={existingWorldTile}) as direct neighbors.");

            // 计算 offset（existing 相对 source）。两端须在世界网格上互为邻居。
            var offset = ComputeNeighborOffset(sourceWorldTile, existingWorldTile, sourceMap);
            var existingParent = existingMap.info.parent;

            // 双向登记邻居表（复用 RegisterNeighborBidirectional 逻辑）。
            RegisterNeighborBidirectional(sourceMap, existingParent, sourceWorldTile, existingWorldTile, offset);

            // 补铺两端传送点（幂等）。
            PlaceEnterSpotsAllNeighbors(sourceMap, sourceWorldTile);
            PlaceEnterSpotsAllNeighbors(existingMap, existingWorldTile);

            // void 不需要刷新：void 几何只取决于地图自己的六边形（与世界邻居关系无关），
            // 且两端地图的 void 在它们各自生成时（genStep 阶段）已铺好。此处再调 RefreshMapVoid 会
            // 重新触发 ClearThingsOnCells，删除地图上已生成的实体（玩家建造/植物/掉落物）。

            // 新铺的传送点需刷新对端坐标缓存（RegisterNeighborBidirectional 内已刷一次，
            // 但补铺的 spot 在其之后，需再刷一次覆盖到它们）。
            RefreshEnterSpotArrivals(sourceMap);
            RefreshEnterSpotArrivals(existingMap);
        }

        /// <summary>
        /// 计算从 sourceWorldTile 到 newWorldTile，新地块相对源地块的偏移。
        /// offset = round(2 × (边中点 - 中心) - SeamOverlap × 方向单位向量)，边中点取自源地块多边形（内切圆模型）。
        /// 边由 newWorldTile 在源地块邻居表中的位置确定。
        /// 沿 offset 方向收缩 <see cref="SeamOverlap"/> 格，使邻居多边形相对源地图多叠 2 格（接缝重叠带），
        /// 容纳投影扭曲。
        /// </summary>
        internal static IntVec3 ComputeNeighborOffset(int sourceWorldTile, int newWorldTile, Map sourceMap)
        {
            var sourceSize = sourceMap.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(sourceWorldTile, sourceSize.x);
            if (verts.Count == 0) return IntVec3.Zero;

            var edgeIdx = WorldTileGeometry.FindNeighborIndex(sourceWorldTile, newWorldTile);
            if (edgeIdx < 0) return IntVec3.Zero;

            var n = verts.Count;
            var center = new Vector2(sourceSize.x * 0.5f, sourceSize.z * 0.5f);
            var mid = (verts[edgeIdx] + verts[(edgeIdx + 1) % n]) * 0.5f;
            var offsetVec = 2f * (mid - center);
            // 沿 offset 方向收缩 SeamOverlap 格，形成接缝重叠带（容纳投影扭曲）。
            var mag = offsetVec.magnitude;
            if (mag > 1e-6f)
            {
                offsetVec -= offsetVec / mag * SeamOverlap;
            }
            // 方向校准已验证正确（heading 真值对比），诊断日志移除保持干净。
            return new IntVec3(Mathf.RoundToInt(offsetVec.x), 0, Mathf.RoundToInt(offsetVec.y));
        }

        /// <summary>
        /// 双向登记两个地块的邻居关系（支持任意组合：锚点-口袋、口袋-口袋、口袋-锚点）。
        /// 源地块 → 新地块：用源地块多边形上指向 newWorldTile 的边角度。
        /// 新地块 → 源地块：偏移 = -offset。
        /// 存储位置由 SetNeighborOnMap 统一屏蔽（锚点存 Manager.neighbors，口袋存 MapParent_SeamlessTile.neighbors）。
        /// </summary>
        private static void RegisterNeighborBidirectional(Map sourceMap, MapParent newParent,
            int sourceWorldTile, int newWorldTile, IntVec3 offset)
        {
            var sourceParent = sourceMap.info.parent;
            var newMap = newParent.Map;

            SetNeighborOnMap(sourceMap, newWorldTile, newParent, offset);
            if (newMap != null)
            {
                SetNeighborOnMap(newMap, sourceWorldTile, sourceParent, -offset);
            }

            // offset 在此确定且不再变：刷新两端所有传送点的对端坐标缓存，供传送/寻路 O(1) 读取。
            RefreshEnterSpotArrivals(sourceMap);
            if (newMap != null)
            {
                RefreshEnterSpotArrivals(newMap);
            }
        }

        /// <summary>
        /// 遍历 map 上所有无缝传送点，按各自的 targetWorldTile 查邻居表得 offset，算出并缓存对端坐标。
        /// 在邻居关系建立（<see cref="RegisterNeighborBidirectional"/>）后调用一次。
        /// 幂等：可重复调用（每次重新查 offset 并覆盖缓存）。
        /// </summary>
        public static void RefreshEnterSpotArrivals(Map map)
        {
            if (map == null) return;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            foreach (var thing in map.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                comp?.ComputeAndCacheArrival(map);
            }
        }

        /// <summary>在 map 上登记一条邻居连接（锚点存 Manager.neighbors，口袋存 MapParent_SeamlessTile.neighbors）。</summary>
        private static void SetNeighborOnMap(Map map, int worldTile, MapParent neighbor, IntVec3 offset)
        {
            if (map == null || neighbor == null) return;
            if (map.Parent is MapParent_SeamlessTile tileParent)
            {
                tileParent.SetNeighbor(worldTile, neighbor, offset);
            }
            else
            {
                map.GetComponent<SeamlessTileManager>()?.SetNeighbor(worldTile, neighbor, offset);
            }
        }

        /// <summary>
        /// 沿地图全部世界邻居边预铺单端传送点（阶段4a 预铺 + 阶段4b 传送机制重构）。
        /// 枚举"接缝带"——到最近 void 格的切比雪夫距离 ∈ {1, 2} 的非 void 格（即紧贴 void 的
        /// <see cref="SeamOverlap"/> 格宽环形带：最外圈 + 次外圈）。每个格按"最近多边形边 j"
        /// 分组确定 <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/>（= 该边对应的世界邻居 tile）。
        /// spot 预铺时 hasArrival 默认 false；邻居加载后由 <see cref="RefreshEnterSpotArrivals"/>
        /// 用 offset 算对端坐标并缓存到 spot（cachedArrivalCell），废弃了旧的互绑模式。
        ///
        /// 幂等：已存在同位置 spot 不重复铺。锚点和口袋都适用（不依赖 MapParent 类型）。
        ///
        /// **接缝带宽度 = SeamOverlap（2）格（关键设计）**：两端各有 2 格宽的 spot 带，通过
        /// <see cref="ComputeNeighborOffset"/> 的 offset 重叠时，实际接缝落在两端 2 格带的中线上——
        /// 接缝上两端都有 spot。投影必然扭曲（相邻 tile 切平面基有旋转，赤道→北极累积约 30°），
        /// 2 格宽的 spot 带互相覆盖吸收此偏移：即使两端 spot 因投影旋转错开 ≤2 格，落点仍能落在
        /// 对端 spot 带内，不会漏到无 spot 的内部或 void。这正是"传送点带本身 SeamOverlap 格宽"
        /// 的含义（旧的"沿边 Bresenham 单线 / 不铺两层"描述已废弃）。
        ///
        /// **几何一致性**：spot 带直接由 terrainGrid 里的 void 边界决定（平移法：把每个 void 格
        /// 的 (2·SeamOverlap+1)² 邻域内的非 void 格标为带内，等价于"void 向外膨胀 SeamOverlap 格"，
        /// 也等价于"本格非 void 且到 void 的切比雪夫距离 ∈ {1..SeamOverlap}"），与
        /// <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 铺 void 用的是同一套格角检测几何，
        /// 杜绝"spot 几何 vs void 边界"两套口径错配。复杂度 O(N² + void格数·(2r+1)²)。
        ///
        /// **调用时机**：必须在 <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 之后调用——
        /// 本方法直接读 terrainGrid 判定 void。ApplyPolygonTerrain 会清空 void 格上的实体，
        /// 若在它之前铺 spot，spot 会被清空逻辑销毁。GenerateTileMap 内部保证此顺序（GenStep 含 ApplyPolygonTerrain
        /// 在 MapGenerator.GenerateMap 内执行，之后才调本方法）。
        /// </summary>
        public static void PlaceEnterSpotsAllNeighbors(Map targetMap, int worldTile)
        {
            if (targetMap == null || worldTile < 0) return;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null)
            {
                Log.Error("[RimExodus] ThingDef RimExodus_SeamlessEnterSpot not found.");
                return;
            }

            // void 地形 Def：从 terrainGrid 读本格/邻格是否 void。
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");

            var mapSize = targetMap.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize.x);
            if (verts.Count == 0) return;

            // 取世界邻居列表（顺序与多边形顶点环绕一致，边 j ↔ 邻居 j）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            if (worldNeighbors.Count == 0) return;

            // 预解析每个边 j 对应的世界邻居 tileId（避免内层循环重复访问）。
            var edgeNeighborTiles = new int[verts.Count];
            for (var j = 0; j < verts.Count; j++)
            {
                edgeNeighborTiles[j] = j < worldNeighbors.Count ? worldNeighbors[j].tileId : -1;
            }

            var terrainGrid = targetMap.terrainGrid.topGrid;
            var cellIndices = targetMap.cellIndices;
            var sx = mapSize.x;
            var sz = mapSize.z;
            var totalCells = sx * sz;
            // 接缝带切比雪夫半径 = SeamOverlap（到 void 的切比雪夫距离 ∈ {1..SeamOverlap}）。
            var bandRadius = SeamOverlap;

            // ---- 平移法构建接缝带掩码（比每格扫 5×5 邻域高效且直观）----
            // 第 1 遍：标记所有 void 格。
            // 第 2 遍：对每个 void 格，把它 (2r+1)×(2r+1) 邻域内的非 void 格标为带内。
            // 等价于"把 void 向外膨胀 bandRadius 格"，即边缘 void 上下左右平移 ≤bandRadius 格的并集。
            var isVoid = new bool[totalCells];
            if (voidDef != null)
            {
                for (var i = 0; i < totalCells; i++) isVoid[i] = terrainGrid[i] == voidDef;
            }
            var inBand = new bool[totalCells];
            for (var z = 0; z < sz; z++)
            {
                for (var x = 0; x < sx; x++)
                {
                    if (!isVoid[z * sx + x]) continue;
                    var xMin = x - bandRadius; if (xMin < 0) xMin = 0;
                    var xMax = x + bandRadius; if (xMax >= sx) xMax = sx - 1;
                    var zMin = z - bandRadius; if (zMin < 0) zMin = 0;
                    var zMax = z + bandRadius; if (zMax >= sz) zMax = sz - 1;
                    for (var nz = zMin; nz <= zMax; nz++)
                    {
                        var rowBase = nz * sx;
                        for (var nx = xMin; nx <= xMax; nx++)
                        {
                            var ni = rowBase + nx;
                            // 只标非 void 格为带内（void 本身保持 false，不铺 spot）。
                            if (!isVoid[ni]) inBand[ni] = true;
                        }
                    }
                }
            }

            var placed = 0;

            for (var x = 0; x < sx; x++)
            {
                for (var z = 0; z < sz; z++)
                {
                    var idx = z * sx + x;
                    if (!inBand[idx]) continue;

                    var cell = new IntVec3(x, 0, z);

                    // 最近多边形边 j → 该边对应的世界邻居 tile（targetWorldTile）。
                    var edgeIdx = SeamlessPolygonGeometry.FindClosestEdgeIndex(verts, cell.x + 0.5f, cell.z + 0.5f);
                    var neighborWorldTile = edgeIdx >= 0 && edgeIdx < edgeNeighborTiles.Length ? edgeNeighborTiles[edgeIdx] : -1;
                    if (neighborWorldTile < 0) continue;

                    // 幂等查重：该格已有同 def spot 则跳过。
                    var existing = targetMap.thingGrid.ThingsListAtFast(cell);
                    var hasSpot = false;
                    for (var i = 0; i < existing.Count; i++)
                    {
                        if (existing[i].def == enterSpotDef) { hasSpot = true; break; }
                    }
                    if (hasSpot) continue;

                    var spot = ThingMaker.MakeThing(enterSpotDef);
                    var comp = spot.TryGetComp<CompSeamlessTileEnterSpot>();
                    var spawned = GenSpawn.Spawn(spot, cell, targetMap);
                    if (spawned != null && comp != null)
                    {
                        comp.targetWorldTile = neighborWorldTile;
                        // hasArrival 默认 false：预铺时不缓存对端坐标，待邻居加载、
                        // RegisterNeighborBidirectional → RefreshEnterSpotArrivals 时算出。
                        placed++;
                    }
                    else if (spawned != null)
                    {
                        spawned.DeSpawn();
                    }
                }
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] PlaceEnterSpotsAllNeighbors map={targetMap.uniqueID}(wt={worldTile}) placed {placed} single-end spots.");
        }

        /// <summary>卸载一个无缝地块基础地图，并清理邻居表中的双向引用。</summary>
        public void RemoveTileMap(MapParent_SeamlessTile parent)
        {
            if (parent == null) return;

            var interiorMap = parent.Map;
            if (interiorMap != null)
            {
                CleanupNeighborLinks(parent);
                // 阶段4前置：基础地图无 sourceMap，不在 pocketMaps 列表。
                // WorldObject 由 DeinitAndRemoveMap 触发 MapParent 销毁时清理。
                Current.Game.DeinitAndRemoveMap(interiorMap, false);
            }
        }

        /// <summary>移除 parent 与其所有邻居之间的双向邻居表引用，并刷新受影响剩余邻居的传送点缓存。</summary>
        private static void CleanupNeighborLinks(MapParent_SeamlessTile parent)
        {
            var linksToRemove = new List<NeighborLink>(parent.neighbors);
            parent.neighbors.Clear();

            // 收集受影响的剩余邻居 Map（去重），清理后需刷新其传送点缓存，
            // 否则指向被卸载地块的 spot 仍保留陈旧的 hasArrival/cachedArrivalCell。
            var affectedMaps = new HashSet<Map>();

            foreach (var link in linksToRemove)
            {
                if (link?.neighbor == null) continue;
                if (link.neighbor is MapParent_SeamlessTile neighborTile)
                {
                    neighborTile.neighbors.RemoveAll(n => n != null && n.neighbor == parent);
                }
                else if (link.neighbor.Map != null)
                {
                    link.neighbor.Map.GetComponent<SeamlessTileManager>()?.neighbors
                        .RemoveAll(n => n != null && n.neighbor == parent);
                }

                // 记录受影响的邻居 Map（neighbor.Map 在口袋被卸载场景下可能为 null，跳过）。
                if (link.neighbor.Map != null)
                {
                    affectedMaps.Add(link.neighbor.Map);
                }
            }

            // 刷新剩余邻居的传送点缓存：指向已卸载地块的 spot 会重算 hasArrival=false，缓存自然失效。
            foreach (var affectedMap in affectedMaps)
            {
                RefreshEnterSpotArrivals(affectedMap);
            }
        }
    }
}
