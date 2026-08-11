using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 世界地块真实几何读取工具。
    /// 从 RimWorld 的 WorldGrid（Odyssey 版 PlanetLayer API）读取地块顶点，
    /// 投影到地块中心的切平面，得到每个顶点在局部地图内的 2D 方向单位向量。
    ///
    /// 用途：构造局部地图的多边形可活动区域。
    /// 多边形顶点 = 地图中心 + 0.5S × 顶点方向（内切圆模型，详见阶段3计划）。
    ///
    /// 所有方法统一遍历 N 个顶点（N=5 五边形 / N=6 六边形），不区分具体边数。
    ///
    /// 坐标投影统一委托 <see cref="TileProjection"/>（唯一的切平面基来源 + 缓存）。
    /// </summary>
    public static class WorldTileGeometry
    {
        /// <summary>
        /// 计算地块 worldTile 每个【顶点】的方向单位向量（2D，切平面投影）。
        /// 返回值的 x 分量对应切平面 second 基取负（局部"东"），y 分量对应 first 基（局部"北"）。
        /// 方向忠实于地块在世界球面上的真实朝向（含 flat-top / pointy-top 旋转）。
        /// 顺序与 <see cref="WorldGrid.GetTileVertices"/> 返回的顶点环绕顺序一致。
        /// </summary>
        public static List<Vector2> ComputeVertexDirections(int worldTile)
        {
            var result = new List<Vector2>();
            PopulateVertexDirections(worldTile, result);
            return result;
        }

        /// <summary>填入版（避免调用方分配）。调用方负责 Clear。</summary>
        public static void PopulateVertexDirections(int worldTile, List<Vector2> result)
        {
            if (result == null) return;
            result.Clear();

            var grid = Find.WorldGrid;
            if (grid == null) return;

            // 取地块顶点（球面世界坐标，环绕顺序）。
            var verts = new List<UnityEngine.Vector3>();
            grid.GetTileVertices(worldTile, verts);
            if (verts.Count == 0) return;

            // 统一从 TileProjection 取切平面基（带缓存，全游戏唯一来源）。
            var basis = TileProjection.GetBasis(worldTile);

            foreach (var vert in verts)
            {
                result.Add(TileProjection.VertexToDir2D(basis, vert));
            }
        }

        /// <summary>
        /// 在邻居列表中查找等于 targetWorldTile 的邻居索引。
        /// 返回该邻居在 GetTileNeighbors 中的序号（对应边 j），找不到返回 -1。
        /// </summary>
        public static int FindNeighborIndex(int worldTile, int targetWorldTile)
        {
            var grid = Find.WorldGrid;
            if (grid == null) return -1;

            var neighbors = new List<PlanetTile>();
            grid.GetTileNeighbors(worldTile, neighbors);
            for (var j = 0; j < neighbors.Count; j++)
            {
                if (neighbors[j].tileId == targetWorldTile) return j;
            }
            return -1;
        }
    }
}
