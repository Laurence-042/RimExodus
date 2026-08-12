using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写：相邻地块地图在接缝带做 terrainDef 过渡混合。
    ///
    /// 正常用原版噪声生成地形（各地块独立 Perlin 场），只在接缝带（多边形边缘内侧环形带）
    /// 用距离权重混合两端 terrainDef，让接缝处地形视觉/通行连续。
    ///
    /// 【混合规则】terrainDef 离散，不能线性混合。按权重 w（靠边→1 取对面，靠内→0 取本端）选：
    /// - 相同 terrainDef → 不变。
    /// - w > 0.5 → 取对面 terrainDef。
    /// - w ≤ 0.5 → 保持本端 terrainDef。
    /// 这在带宽中间形成切换线，两侧分别是本端和对面的地形。
    ///
    /// 【双向覆写】后生成的 tile C 的 genStep 跑时，对面邻居 B 已加载。C 覆写自身侧带（参考 B），
    /// 同时覆写 B 侧带（参考 C），因为 B 生成时 C 还不存在。仿照 PlaceEnterSpotsAllNeighbors 双端模式。
    /// </summary>
    public static class SeamlessSeamOverride
    {
        /// <summary>
        /// 对 map 的所有已加载邻居做双向接缝覆写。
        /// 在 GenStep_SeamOverride.Generate 里调用（order=212，void 裁切之后、Plants 之前）。
        /// </summary>
        public static void ApplyBidirectional(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;
            var ratio = RimExodusMod.Settings?.seamOverrideRatio ?? 0.25f;
            if (ratio <= 0f) return; // 0 = 关闭接缝覆写。

            var mapSize = map.Size.x;
            var bandWidth = Mathf.Max(1, Mathf.RoundToInt(ratio * mapSize * 0.5f));

            // 本端多边形顶点 + 世界邻居列表（顺序与顶点环绕一致）。
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            // 本端混合带：格 → 最近边对应的邻居 worldTile。
            var band = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeEdgeBand(verts, mapSize, bandWidth, neighborWorldTiles, band);

            // 对每个已加载邻居做双向覆写。
            foreach (var neighborTile in neighborWorldTiles)
            {
                if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile, out var neighborMap)) continue;
                if (neighborMap == null || neighborMap.Disposed || neighborMap == map) continue;

                // offset：本端 cell = 邻居 cell + offset（NeighborLink 契约）。故邻居 cell = 本端 cell - offset。
                var offset = SeamlessTileManager.ComputeNeighborOffset(worldTile, neighborTile, map);
                if (offset == IntVec3.Zero) continue;

                // 覆写本端侧带（参考邻居）。
                OverrideOneSide(map, worldTile, band, neighborTile, neighborMap, offset, verts, bandWidth);

                // 覆写邻居侧带（参考本端）。邻居的混合带需在邻居 Map 上重算。
                OverrideNeighborSide(map, neighborTile, neighborMap, worldTile, offset, bandWidth);
            }
        }

        /// <summary>
        /// 覆写本端侧带：本端接缝带格的 terrainDef 按距离权重参考邻居对应格。
        /// </summary>
        private static void OverrideOneSide(Map map, int worldTile, Dictionary<IntVec3, int> band,
            int neighborTile, Map neighborMap, IntVec3 offset, List<Vector2> verts, int bandWidth)
        {
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid;
            var mapDrawer = map.mapDrawer;
            var neighborIndices = neighborMap.cellIndices;
            var neighborTopGrid = neighborMap.terrainGrid.topGrid;

            foreach (var kv in band)
            {
                if (kv.Value != neighborTile) continue; // 只处理指向当前邻居的格。
                var cell = kv.Key;

                // 本端 terrainDef。
                var localIdx = cellIndices.CellToIndex(cell);
                var localTerrain = topGrid[localIdx];
                if (localTerrain == null || (voidDef != null && localTerrain == voidDef)) continue;

                // 邻居对应格 = 本端 cell - offset。
                var neighborCell = cell - offset;
                if (!neighborCell.InBounds(neighborMap)) continue;
                var neighborTerrain = neighborTopGrid[neighborIndices.CellToIndex(neighborCell)];
                if (neighborTerrain == null || (voidDef != null && neighborTerrain == voidDef)) continue;

                // 相同 terrainDef → 不变。
                if (neighborTerrain == localTerrain) continue;

                // 算权重 w：靠边→1（取对面），靠内→0（取本端）。
                var cellCenter = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
                var distFromEdge = MinDistanceToEdge(cellCenter, verts);
                var w = 1f - Mathf.Clamp01(distFromEdge / bandWidth);

                // w > 0.5 → 取对面 terrainDef。
                if (w > 0.5f)
                {
                    topGrid[localIdx] = neighborTerrain;
                    mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
                }
            }
        }

        /// <summary>
        /// 覆写邻居侧带：邻居接缝带格的 terrainDef 按距离权重参考本端对应格。
        /// 用于处理"邻居生成时本端还不存在"的情况——本端生成时回补邻居侧。
        /// </summary>
        private static void OverrideNeighborSide(Map map, int neighborWorldTile,
            Map neighborMap, int selfTile, IntVec3 offset, int bandWidth)
        {
            // 邻居的混合带：在邻居 Map 上重算。
            var neighborSize = neighborMap.Size.x;
            var neighborVerts = SeamlessPolygonGeometry.BuildPolygonVertices(neighborWorldTile, neighborSize);
            if (neighborVerts.Count < 3) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(neighborWorldTile, worldNeighbors);
            var neighborNeighborTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborNeighborTiles.Add(nt.tileId);

            var neighborBand = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeEdgeBand(neighborVerts, neighborSize, bandWidth, neighborNeighborTiles, neighborBand);

            // 邻居 offset（指向本端）：邻居 cell + neighborOffset = 本端 cell。
            // 但我们已经有了 self→neighbor 的 offset（本端 cell = 邻居 cell + offset），
            // 所以 neighborOffset = -offset，本端 cell = 邻居 cell + offset。
            // 邻居侧覆写：邻居带格参考本端对应格（本端 cell = 邻居 cell + offset）。
            // OverrideOneSide 的逻辑反过来：以邻居为主，参考本端。
            OverrideOneSide(neighborMap, neighborWorldTile, neighborBand, selfTile, map, new IntVec3(-offset.x, 0, -offset.z), neighborVerts, bandWidth);
        }

        /// <summary>格中心到多边形最近边的距离（遍历所有边取最小）。</summary>
        private static float MinDistanceToEdge(Vector2 p, List<Vector2> verts)
        {
            var n = verts.Count;
            var min = float.MaxValue;
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                var d = SeamlessPolygonGeometry.DistanceToEdge(p, v0, v1);
                if (d < min) min = d;
            }
            return min;
        }
    }
}
