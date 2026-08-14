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
        /// 枚举"接缝带"——到最近 void 格的切比雪夫距离 ∈ {1, 2} 的非 void 格（即紧贴 void 的
        /// <see cref="SeamlessTileManager.SeamOverlap"/> 格宽环形带：最外圈 + 次外圈）。每个格按"最近多边形边 j"
        /// 分组确定 <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/>（= 该边对应的世界邻居 tile）。
        /// spot 预铺时 hasArrival 默认 false；邻居加载后由 <see cref="RefreshEnterSpotArrivals"/>
        /// 用 offset 算对端坐标并缓存到 spot（cachedArrivalCell），废弃了旧的互绑模式。
        ///
        /// 幂等：已存在同位置 spot 不重复铺。锚点和地块都适用（不依赖 MapParent 类型）。
        ///
        /// **接缝带宽度 = SeamOverlap（2）格（关键设计）**：两端各有 2 格宽的 spot 带，通过
        /// <see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/> 的 offset 重叠时，实际接缝落在两端 2 格带的中线上——
        /// 接缝上两端都有 spot。投影必然扭曲（相邻 tile 切平面基有旋转，赤道→北极累积约 30°），
        /// 2 格宽的 spot 带互相覆盖吸收此偏移：即使两端 spot 因投影旋转错开 ≤2 格，落点仍能落在
        /// 对端 spot 带内，不会漏到无 spot 的内部或 void。这正是"传送点带本身 SeamOverlap 格宽"
        /// 的含义（旧的"沿边 Bresenham 单线 / 不铺两层"描述已废弃）。
        ///
        /// **几何一致性**：spot 带复用 <see cref="SeamlessPolygonGeometry.ComputeVoidBand"/>（多轮膨胀，
        /// "距 void 边界 ≤ SeamOverlap 格"的唯一实现），与 SeamOverride 混合带、BorderLookup 边界带
        /// 同一实现、同一 void 边界口径（<see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 铺 void 用的
        /// 格角检测），杜绝"spot 几何 vs void 边界"两套口径错配。
        ///
        /// **调用时机**：必须在 <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 之后调用——
        /// 本方法通过 ComputeVoidBand 读 terrainGrid 判定 void。ApplyPolygonTerrain 会清空 void 格上的实体，
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

            // 取世界邻居列表（顺序与多边形顶点环绕一致，边 j ↔ 邻居 j）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            if (worldNeighbors.Count == 0) return;

            // 预解析每个边 j 对应的世界邻居 tileId（避免内层循环重复访问）。
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            // 接缝带 = 到最近 void 格的切比雪夫距离 ∈ {1..SeamOverlap} 的非 void 格。
            // 复用 ComputeVoidBand（与 SeamOverride 混合带、BorderLookup 边界带同一实现）——
            // 消除旧的自建平移法 inBand 掩码，保证"距 void 边界 N 格"语义唯一实现。
            // ComputeVoidBand 直接读 terrainGrid 判 void，返回 cell → 最近边对应的邻居 worldTile。
            var band = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeVoidBand(targetMap, SeamlessTileManager.SeamOverlap, neighborWorldTiles, band);

            if (band.Count == 0) return;

            var placed = 0;
            foreach (var kv in band)
            {
                var cell = kv.Key;
                var neighborWorldTile = kv.Value;
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
