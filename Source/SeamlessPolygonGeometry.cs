using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块多边形几何工具（阶段3：多边形裁切）。
    /// 基于内切圆顶点模型：多边形顶点 = 地图中心 + 0.5S × 顶点方向单位向量。
    /// 所有方法统一遍历 N 个顶点（N=5 或 6），不区分五边形/六边形。
    ///
    /// 提供多边形顶点构造、凸多边形扫描线填充、点在多边形内判定、
    /// 边 Bresenham 枚举（传送点划线）等共用能力，
    /// 供 GenStep 铺虚空、ComputeNeighborOffset 算偏移、EnumerateSeamCells 放传送点、
    /// Registry 归属判定复用。
    /// </summary>
    public static class SeamlessPolygonGeometry
    {
        /// <summary>
        /// 构造地块 worldTile 在 mapSize 地图内的多边形顶点（局部坐标，Vector2：x=东向格，y=北向格）。
        /// 顶点 = center + 0.5S × 顶点方向（内切圆模型）。
        /// 顺序与 WorldTileGeometry.ComputeVertexDirections 一致。
        /// </summary>
        public static List<Vector2> BuildPolygonVertices(int worldTile, int mapSize)
        {
            var result = new List<Vector2>();
            PopulatePolygonVertices(worldTile, mapSize, result);
            return result;
        }

        /// <summary>填入版。调用方负责 Clear。</summary>
        public static void PopulatePolygonVertices(int worldTile, int mapSize, List<Vector2> result)
        {
            if (result == null) return;
            result.Clear();

            var dirs = new List<Vector2>();
            WorldTileGeometry.PopulateVertexDirections(worldTile, dirs);
            if (dirs.Count == 0) return;

            var center = new Vector2(mapSize * 0.5f, mapSize * 0.5f);
            var radius = mapSize * 0.5f;

            foreach (var dir in dirs)
            {
                result.Add(center + dir * radius);
            }
        }

        /// <summary>
        /// 凸多边形扫描线填充：遍历每行 z，输出 [xLeft, xRight] 填充区间（格中心坐标系）。
        /// 利用凸性：每行与多边形边界最多 2 交点。
        /// 调用方据此铺设可通行地形（区间内）与虚空地形（区间外）。
        /// </summary>
        public static void ScanlineFill(List<Vector2> verts, int mapSize, System.Action<int, int, int> onRow)
        {
            // onRow(z, xLeftInclusive, xRightInclusive)：z 行的可通行区间（闭区间格坐标，可能 xLeft>xRight 表示整行虚空）。
            // 当整行无交集时，xLeft=0, xRight=-1（空区间）。
            var n = verts.Count;
            if (n < 3)
            {
                // 退化多边形 → 整图虚空。
                for (var z = 0; z < mapSize; z++) onRow(z, 0, -1);
                return;
            }

            for (var z = 0; z < mapSize; z++)
            {
                var scanY = z + 0.5f; // 格中心
                var xs = SimplePool<List<float>>.Get();
                xs.Clear();

                for (var j = 0; j < n; j++)
                {
                    var v0 = verts[j];
                    var v1 = verts[(j + 1) % n];
                    // 边跨越该行：两端 y 在 scanY 两侧。
                    var below0 = v0.y <= scanY;
                    var below1 = v1.y <= scanY;
                    if (below0 == below1) continue;

                    var t = (scanY - v0.y) / (v1.y - v0.y);
                    xs.Add(v0.x + t * (v1.x - v0.x));
                }

                xs.Sort();
                if (xs.Count >= 2)
                {
                    var xLeft = xs[0];
                    var xRight = xs[xs.Count - 1];
                    // 转为闭区间格坐标：格 x 中心为 x+0.5，落在 [xLeft, xRight] 内即可通行。
                    var xLeftCell = Mathf.CeilToInt(xLeft - 0.5f);
                    var xRightCell = Mathf.FloorToInt(xRight - 0.5f);
                    // 截断到地图范围。
                    xLeftCell = Mathf.Clamp(xLeftCell, 0, mapSize - 1);
                    xRightCell = Mathf.Clamp(xRightCell, -1, mapSize - 1);
                    onRow(z, xLeftCell, xRightCell);
                }
                else
                {
                    // 该行无交集 → 整行虚空。
                    onRow(z, 0, -1);
                }

                xs.Clear();
                SimplePool<List<float>>.Return(xs);
            }
        }

        /// <summary>
        /// 点在凸多边形内判定（格中心采样）。
        /// cell 是否在多边形内（含边界）。
        /// </summary>
        public static bool ContainsPoint(List<Vector2> verts, int mapSize, IntVec3 cell)
        {
            var n = verts.Count;
            if (n < 3) return false;

            var px = cell.x + 0.5f;
            var py = cell.z + 0.5f;

            // 凸多边形：点在所有边的内侧（与顶点环绕方向一致的同一侧）。
            // 用叉积符号一致性判定。
            var sign = 0;
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                var cross = (v1.x - v0.x) * (py - v0.y) - (v1.y - v0.y) * (px - v0.x);
                if (Mathf.Abs(cross) < 1e-6f) continue; // 在边延长线上，视为在内
                var s = cross > 0 ? 1 : -1;
                if (sign == 0) sign = s;
                else if (sign != s) return false;
            }
            return true;
        }

        /// <summary>
        /// 沿多边形边 j（连接顶点 j 与顶点 j+1）用 Bresenham 枚举线经过的格子。
        /// 用于传送点划线满铺接缝。
        /// 四格交点补偿：线恰好经过整数格交点时，向内侧（多边形内部方向）补一格，防斜向空缺。
        /// </summary>
        public static IEnumerable<IntVec3> EnumerateEdgeCells(List<Vector2> verts, int edgeIdx, int mapSize)
        {
            var n = verts.Count;
            if (n == 0) yield break;

            var v0 = verts[edgeIdx];
            var v1 = verts[(edgeIdx + 1) % n];

            // Bresenham（浮点端点版本，逐格步进）。
            var x0 = Mathf.RoundToInt(v0.x);
            var y0 = Mathf.RoundToInt(v0.y);
            var x1 = Mathf.RoundToInt(v1.x);
            var y1 = Mathf.RoundToInt(v1.y);

            foreach (var cell in BresenhamLine(x0, y0, x1, y1, mapSize))
            {
                yield return cell;
            }
        }

        /// <summary>
        /// 整数 Bresenham 直线（含四格交点补偿：整数交点处向 +x 方向补一格）。
        /// 输出经过的所有格（截断到地图范围）。
        /// </summary>
        private static IEnumerable<IntVec3> BresenhamLine(int x0, int y0, int x1, int y1, int mapSize)
        {
            var dx = Mathf.Abs(x1 - x0);
            var dy = Mathf.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            var x = x0;
            var y = y0;

            while (true)
            {
                if (x >= 0 && x < mapSize && y >= 0 && y < mapSize)
                {
                    yield return new IntVec3(x, 0, y);
                }
                if (x == x1 && y == y1) break;
                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }
    }
}
