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
    /// 阶段3：邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 多边形裁切用内切圆顶点模型（顶点 = center + 0.5S × 方向）。
    /// 详见阶段3计划。
    /// </summary>
    public class SeamlessTileManager : MapComponent
    {
        /// <summary>
        /// 锚点地图（家园 A）的直接邻居表。口袋地图的邻居表存于自身的 MapParent_SeamlessTile。
        /// 通过 <see cref="SeamlessTileGraph"/> 统一查询，屏蔽存储位置差异。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>是否已自动生成首个邻居地块（原型阶段：开档即生成北侧，便于测试）。</summary>
        private bool setupOnStartDone;

        /// <summary>延迟生成首个邻居地块的 tick 计数（MapGenerated 时 mapBeingGenerated 仍非空，需延迟到下一 tick）。</summary>
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
            // 口袋地块的邻居生成不通过 MapGenerated 自动级联（避免生成风暴）。
            if (map.IsPocketMap) return;
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
        /// </summary>
        private void TrySetupOnStart()
        {
            var anchorWorldTile = map.Tile;
            if (anchorWorldTile < 0) return;

            // 锚点 A 是原生地图，不走 RimExodus GenStep，必须在此显式铺 void（六边形外 = void）。
            // 阶段3 此调用依赖 GenerateTileMap 内的 RefreshMapVoid 顺带触发，
            // 阶段4a 默认不生成邻居，故必须独立调用。
            RefreshMapVoid(map);

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
        /// 刷新指定地块地图的 void 铺设：六边形内（含边）非 void，六边形外 void。
        /// 可重复调用。锚点 A（原生地图，不走 RimExodus GenStep）和口袋地块都适用。
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
            // 异步生成：LongEventHandler 在独立线程跑 GenerateTileMap（含 genSteps + FinalizeInit，整体完成）。
            // 显示"Generating map"进度画面（ForcePause 几秒）。这是必要的——拆分式异步（AddMap 后 genSteps 前让主线程 tick）
            // 会导致 RegionAndRoomUpdater 未初始化时主线程 tick 访问 map，海量 "RegionAndRoomUpdater is disabled" 警告 +
            // 寻路失效（pawn 卡接缝）。AddMap→genSteps→FinalizeInit 必须在主线程恢复前连续完成，LongEventHandler 保证这一点。
            LongEventHandler.QueueLongEvent(() =>
            {
                try
                {
                    GenerateTileMap(sourceWorldTile, targetWorldTile, mapSize);
                }
                finally
                {
                    ClearGeneratingTile(targetWorldTile);
                }
            }, "GeneratingMap", doAsynchronously: true, ex => ClearGeneratingTile(targetWorldTile));
            Log.Message($"[RimExodus] Queued async preload for world tile {targetWorldTile} (source={sourceWorldTile}).");
            return true;
        }

        /// <summary>异步生成完成后清理防重入锁。</summary>
        private static void ClearGeneratingTile(int worldTile)
        {
            foreach (var m in Find.Maps)
            {
                m.GetComponent<SeamlessTileManager>()?.generatingTiles?.Remove(worldTile);
            }
        }

        /// <summary>
        /// 同步生成无缝地块口袋地图（供 Dev 命令 / 开档加载全部邻居使用）。
        /// 预加载路径（TryPreloadNeighbor）用 LongEventHandler 在独立线程调本方法。
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
            mapParent.mapGenerator = def.mapGenerator;
            mapParent.worldTile = newWorldTile;
            mapParent.Tile = 0;
            var anchorMap = SeamlessTileGraph.GetAnchorMap(map) ?? map;
            mapParent.sourceMap = anchorMap;
            var hostOffset = ComputeNeighborOffset(sourceWorldTile, newWorldTile, map);

            var interiorMap = MapGenerator.GenerateMap(mapSize, mapParent, mapParent.MapGeneratorDef,
                mapParent.ExtraGenStepDefs, generatedMap => InjectRealTileInfo(generatedMap, newWorldTile), isPocketMap: true);

            Find.World.pocketMaps.Add(mapParent);
            if (!Find.World.worldObjects.Contains(interiorMap.Parent))
            {
                Find.World.worldObjects.Add(interiorMap.Parent);
            }
            interiorMap.skyManager = anchorMap.skyManager;
            interiorMap.weatherDecider = anchorMap.weatherDecider;
            interiorMap.weatherManager = anchorMap.weatherManager;
            RegisterNeighborBidirectional(map, mapParent, sourceWorldTile, newWorldTile, hostOffset);
            RefreshMapVoid(map);
            PlaceEnterSpotsAllNeighbors(interiorMap, newWorldTile);
            PlaceEnterSpotsAllNeighbors(map, sourceWorldTile);
            BindNewTileWithExistingNeighbors(interiorMap, newWorldTile);
            AutoConnectWorldNeighbors(interiorMap, newWorldTile);
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

            // 刷新两端 void（邻居关系变化后，虽然 void 只看自己多边形，但保险刷新）。
            RefreshMapVoid(sourceMap);
            RefreshMapVoid(existingMap);

            // 互绑两端传送点。
            SeamlessEnterSpotBinder.BindUnboundSpotsBetween(sourceMap, sourceWorldTile, existingMap, existingWorldTile, offset);
        }

        /// <summary>
        /// 把真实 worldTile 的 TileInfo（biome/hilliness/elevation/rainfall/temperature/swampiness/pollution）
        /// 注入到口袋地图的 pocketTileInfo，使原版地形 GenStep 按真实地块特性生成地形。
        /// 在 MapGenerator.GenerateMap 的 extraInitBeforeContentGen 回调中调用（genSteps 执行前，pocketTileInfo 已构造）。
        /// </summary>
        private static void InjectRealTileInfo(Map generatedMap, int worldTile)
        {
            if (generatedMap == null || !generatedMap.IsPocketMap) return;
            if (worldTile < 0) return;

            var grid = Find.WorldGrid;
            if (grid == null) return;

            var realTile = grid[worldTile];
            var pocketTile = generatedMap.pocketTileInfo;
            if (pocketTile == null) return;

            // 注入真实地块特性。PrimaryBiome 已由 MapGenerator 从 pocketMapProperties 设为 BorealForest（占位），这里覆盖为真实 biome。
            pocketTile.PrimaryBiome = realTile.PrimaryBiome;
            pocketTile.hilliness = realTile.hilliness;
            pocketTile.elevation = realTile.elevation;
            pocketTile.rainfall = realTile.rainfall;
            pocketTile.temperature = realTile.temperature;
            pocketTile.swampiness = realTile.swampiness;
            pocketTile.pollution = realTile.pollution;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] InjectRealTileInfo worldTile={worldTile}: biome={pocketTile.PrimaryBiome?.defName}, " +
                    $"hilliness={pocketTile.hilliness}, elevation={pocketTile.elevation}, rainfall={pocketTile.rainfall}.");
        }

        /// <summary>
        /// 计算从 sourceWorldTile 到 newWorldTile，新地块相对源地块的偏移。
        /// offset = round(2 × (边中点 - 中心))，边中点取自源地块多边形（内切圆模型）。
        /// 边由 newWorldTile 在源地块邻居表中的位置确定。
        /// </summary>
        private static IntVec3 ComputeNeighborOffset(int sourceWorldTile, int newWorldTile, Map sourceMap)
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
        /// 沿地图全部世界邻居边预铺单端传送点（阶段4a：预铺 + 延迟绑定）。
        /// 每条边 j 用 Bresenham 划线枚举格，对可站立且无同 def spot 的格铺一个单端 spot：
        /// <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/> = 该边对应的世界邻居 tile，<see cref="CompSeamlessTileEnterSpot.CounterpartSpot"/> = null。
        /// 邻居加载后由 <see cref="SeamlessEnterSpotBinder"/> 按 targetWorldTile + 坐标校验互绑。
        ///
        /// 幂等：已存在同位置 spot 不重复铺。锚点和口袋都适用（不依赖 MapParent 类型）。
        ///
        /// **调用时机**：必须在 <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 之后调用——
        /// 传送点铺在多边形边格上，边格须为非 void（Walkable）。ApplyPolygonTerrain 会清空 void 格上的实体，
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

            var mapSize = targetMap.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize.x);
            if (verts.Count == 0) return;

            // 取世界邻居列表（顺序与多边形顶点环绕一致，边 j ↔ 邻居 j）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            if (worldNeighbors.Count == 0) return;

            var placed = 0;
            for (var j = 0; j < verts.Count && j < worldNeighbors.Count; j++)
            {
                var neighborWorldTile = worldNeighbors[j].tileId;
                foreach (var cell in SeamlessPolygonGeometry.EnumerateEdgeCells(verts, j, mapSize.x))
                {
                    if (!cell.InBounds(targetMap)) continue;
                    if (!cell.Walkable(targetMap)) continue; // 虚空/自然地形阻挡：不铺（pawn 站不上去）。

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
                        comp.CounterpartSpot = null; // 预铺：对端留空，待邻居加载后绑定。
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

        /// <summary>
        /// 将新加载的地块与其所有已存在的邻居之间的未绑定传送点互相绑定（多跳支持）。
        /// 遍历新地块的邻居表，对每个已加载邻居调用 <see cref="SeamlessEnterSpotBinder.BindUnboundSpotsBetween"/>。
        /// </summary>
        private static void BindNewTileWithExistingNeighbors(Map newMap, int newWorldTile)
        {
            if (newMap == null) return;
            var neighbors = SeamlessTileGraph.GetAllNeighbors(newMap);
            foreach (var info in neighbors)
            {
                if (info.map == null || info.map.Disposed) continue;
                // info.offset 满足 NeighborLink 契约：cellNeighbor + info.offset = cellNew（从 newMap 查邻居）。
                // 故 cellNew - cellNeighbor = info.offset。以 newMap 为 A、neighbor 为 B，
                // cellAMinusCellB = cellA - cellB = cellNew - cellNeighbor = info.offset。
                SeamlessEnterSpotBinder.BindUnboundSpotsBetween(newMap, newWorldTile, info.map, info.worldTile, info.offset);
            }
        }

        /// <summary>卸载一个无缝地块口袋地图，并清理邻居表中的双向引用。</summary>
        public void RemoveTileMap(MapParent_SeamlessTile parent)
        {
            if (parent == null) return;

            var interiorMap = parent.Map;
            if (interiorMap != null)
            {
                CleanupNeighborLinks(parent);
                parent.sourceMap = null;
                Find.World.pocketMaps.Remove(parent);
                Current.Game.DeinitAndRemoveMap(interiorMap, false);
            }
        }

        /// <summary>移除 parent 与其所有邻居之间的双向邻居表引用。</summary>
        private static void CleanupNeighborLinks(MapParent_SeamlessTile parent)
        {
            var linksToRemove = new List<NeighborLink>(parent.neighbors);
            parent.neighbors.Clear();

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
            }
        }
    }
}
