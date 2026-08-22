using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝方向揭雾（order 1505，Fog(1500) 之后；2026-08 用户设计"从生成方向算无fog范围"）。
    ///
    /// 原版 <c>GenStep_Fog</c> 对所有图全雾后，仅从 <c>MapGenerator.PlayerStartSpot</c>（我们 392
    /// 预设 = 六边形中心）做 <c>FloodFillerFog.FloodUnfog</c>——洪水会被**围墙**挡住：无墙的地块图
    /// 洪水铺满全图（历史无感），有墙的据点图（Settlement 基地）只剩中心房间，连 void 带/接缝带
    /// 都是雾 → 跨缝看邻图全黑、雾中的贸易商不可点击（2026-08 实测）。
    ///
    /// 本步：对每条**对端已有活跃地图**（<see cref="SeamlessTileGraph.TryGetMapByWorldTile"/> 命中，
    /// 含休眠过滤）的共享边，把六边形内距该边 ≤ <see cref="UnfogDepth"/> 格的带状区（含 void 格——
    /// 跨缝视野要透过它）直接逐格揭雾。玩家未到过的其他边保持雾，探索感保留；无活跃邻居（首图/
    /// 世界图入口生成）时为 no-op，揭雾由原版中心洪水兜底。边索引 = 世界邻居序号（与
    /// <c>SeamlessNeighborRegistry.ComputeNeighborOffset</c> 同款映射）。
    /// </summary>
    public class GenStep_SeamOriginUnfog : GenStep
    {
        public override int SeedPart => 743197811;

        /// <summary>距共享边的揭雾深度（与预加载带 borderPreloadDistance 同宽的可见范围）。</summary>
        private const float UnfogDepth = 15f;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts == null || verts.Count < 3) return;

            // 收集"对端活跃"的共享边；对端无活跃图 = 玩家未到过的方向，保持雾。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(new PlanetTile(worldTile), worldNeighbors);
            var edges = new List<Vector2[]>();
            foreach (var neighborTile in worldNeighbors)
            {
                if (neighborTile.tileId == worldTile) continue;
                if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile.tileId, out _)) continue;
                var edgeIdx = WorldTileGeometry.FindNeighborIndex(worldTile, neighborTile.tileId);
                if (edgeIdx < 0 || edgeIdx >= verts.Count) continue;
                edges.Add(new[] { verts[edgeIdx], verts[(edgeIdx + 1) % verts.Count] });
            }
            if (edges.Count == 0) return;

            var unfogged = 0;
            foreach (var cell in map.AllCells)
            {
                var p = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
                for (var i = 0; i < edges.Count; i++)
                {
                    if (DistToSegment(p, edges[i][0], edges[i][1]) <= UnfogDepth)
                    {
                        map.fogGrid.Unfog(cell);
                        unfogged++;
                        break;
                    }
                }
            }
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] SeamOriginUnfog: map={map.uniqueID}(wt={worldTile}) activeEdges={edges.Count} unfogged {unfogged} cells.");
        }

        /// <summary>点到线段距离（2D 地图坐标）。</summary>
        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var sqrLen = ab.sqrMagnitude;
            if (sqrLen < 1e-6f) return Vector2.Distance(p, a);
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLen);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
