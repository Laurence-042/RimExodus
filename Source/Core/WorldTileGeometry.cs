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
    /// 多边形顶点 = 地图中心 + 缩放半径 × 顶点方向（等边模型，边长归一 0.5S，见 SeamlessPolygonGeometry）。
    ///
    /// 所有方法统一遍历 N 个顶点（N=5 五边形 / N=6 六边形），不区分具体边数。
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
            var verts = new List<Vector3>();
            grid.GetTileVertices(worldTile, verts);
            if (verts.Count == 0) return;

            // 地块中心。
            var center = grid.GetTileCenter(worldTile);

            // 建立地块中心的切平面基（first / second 是切平面的两个正交单位向量）。
            // 只要同一地块所有顶点用同一组基投影，2D 方向就一致；绝对朝向不影响多边形形状。
            WorldRendererUtility.GetTangentsToPlanet(center, out var first, out var second);

            // first/second 的绝对朝向难纯推理（依赖 LookRotation + 球面视角），
            // 此处用 -Dot(second) / Dot(first) 校准（实测 180 度旋转 bug 后的经验值）。
            foreach (var vert in verts)
            {
                var d = vert - center;
                var dx = -Vector3.Dot(d, second);
                var dz = Vector3.Dot(d, first);
                var dir = new Vector2(dx, dz);
                dir.Normalize();
                result.Add(dir);
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
