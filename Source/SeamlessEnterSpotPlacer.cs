using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝传送点铺设工具（静态，无状态）。
    /// 封装"沿接缝带预铺单端传送点 + 按 offset 缓存对端坐标"，供 <see cref="SeamlessTileManager"/> 生成流程
    /// 和 <see cref="SeamlessNeighborRegistry"/> 邻居登记调用。
    /// </summary>
    internal static class SeamlessEnterSpotPlacer
    {
        /// <summary>
        /// 沿地图全部世界邻居边预铺单端传送点（阶段4a 预铺 + 阶段4b 传送机制重构）。
        /// 铺设范围 = **传送圈**（接缝带的外侧 2 圈 = 离散边圈 ∪ 带外圈，权威定义见
        /// doc/接缝带定义.md 与 <see cref="SeamlessPolygonGeometry.BuildSeamBand"/>）。
        /// 每个格按"最近多边形边 j"分组确定 <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/>
        /// （= 该边对应的世界邻居 tile）。spot 预铺时 hasArrival 默认 false；邻居加载后由
        /// <see cref="RefreshEnterSpotArrivals"/> 用 offset 算对端坐标并缓存到 spot（cachedArrivalCell）。
        ///
        /// 幂等：已存在同位置 spot 不重复铺。原生 parent 图和地块图都适用（不依赖 MapParent 类型）。
        ///
        /// 【传送圈为何只有外侧 2 圈（带内圈无传送点）】落点 = spot − offset 的取整残差 ±1 格
        /// 会被对侧 3 圈接缝带吸收：pawn 由内向外正常移动必先踩离散边圈 spot（更靠内），
        /// 落点理想在对侧离散边圈，偏移最多落到对侧带内圈/带外圈——均为实地形，不进 void。
        /// 若带内圈也有传送点，站带内圈传送的落点偏移可能越过对侧带外圈落进 void。
        ///
        /// **几何口径**：纯接缝带几何（BuildSeamBand，不读 terrainGrid 的 void 实况），
        /// 与 void 铺设（ApplyPolygonTerrain 同一带缓存）天然同口径。
        /// **无任何可通行性过滤（用户定夺 2026-08，勿回退）**：spot 是纯逻辑连接设施，铺满
        /// 传送圈——能不能走由地形运行时决定（不能走 pawn 自然绕路，与地图中央的深水/岩石
        /// 挡路同构），地形变化（挖岩石/铺桥/水位）后 spot 已在、即时可用。
        ///
        /// **调用时机**：任意时刻可调（不读 terrainGrid）；现状调用点在生成完成后（幂等）。
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

            // 取世界邻居列表（顺序与多边形顶点环绕一致，边 j ↔ 邻居 j）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            if (worldNeighbors.Count == 0) return;

            // 预解析每个边 j 对应的世界邻居 tileId（避免内层循环重复访问）。
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            var mapSize = targetMap.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;

            // 传送圈 = 离散边圈 ∪ 带外圈（接缝带几何缓存）。
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
            if (band.TransportRing.Count == 0) return;

            var placed = 0;
            foreach (var cell in band.TransportRing)
            {
                // 按最近多边形边分组确定目标邻居。
                var edgeIdx = SeamlessPolygonGeometry.FindClosestEdgeIndex(verts, cell.x + 0.5f, cell.z + 0.5f);
                var neighborWorldTile = edgeIdx >= 0 && edgeIdx < neighborWorldTiles.Count ? neighborWorldTiles[edgeIdx] : -1;
                if (neighborWorldTile < 0) continue;

                // **不做任何可通行性预判（勿加 Standable/地形 passability 过滤）**：
                // 传送点只是连接两个地图的逻辑设施，能不能走由地形运行时决定——不能走 pawn
                // 自然绕路，与地图中央出现深水/岩石挡路同构（用户定夺 2026-08）。任何预判
                // 都在重复地形系统的职责，且地形变化（挖岩石/铺桥/水位）后会造成"该格能走
                // 但没 spot"的永久断裂。传送执行处的 Walkable 检查是最后防线。

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

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] PlaceEnterSpotsAllNeighbors map={targetMap.uniqueID}(wt={worldTile}) placed {placed} single-end spots.");
        }

        /// <summary>
        /// 遍历 map 上所有无缝传送点，按各自的 targetWorldTile 查邻居表得 offset，算出并缓存对端坐标。
        /// 在邻居关系建立（<see cref="SeamlessNeighborRegistry.RegisterNeighborBidirectional"/>）后调用一次。
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
    }
}
