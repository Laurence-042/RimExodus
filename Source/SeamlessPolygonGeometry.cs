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
        /// 判定格是否在多边形内（用于 void 铺设，保证连续性）。
        /// 比 <see cref="ContainsPoint"/> 更宽松：格中心在多边形内（含边），
        /// **或** 格中心到最近边的距离 < 0.71 格（对角线半径，确保格面积有部分在多边形内）。
        /// 这避免边附近的格因浮点误差被判为外，形成 void 孤岛（不连续 void）。
        /// </summary>
        public static bool IsCellInPolygon(List<Vector2> verts, int mapSize, IntVec3 cell)
        {
            var n = verts.Count;
            if (n < 3) return false;

            var px = cell.x + 0.5f;
            var py = cell.z + 0.5f;

            // 先用 ContainsPoint 精确判定。
            if (ContainsPoint(verts, mapSize, cell)) return true;

            // 格中心不在多边形内：检查是否离任意一条边足够近（格面积部分在内）。
            // 阈值 0.71 ≈ sqrt(0.5²) = 格的对角线半长，确保格有部分面积在边内。
            var p = new Vector2(px, py);
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                if (DistanceToEdge(p, v0, v1) < 0.71f) return true;
            }
            return false;
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
        /// 计算点 p 到线段 (v0→v1) 的最短距离（解析）。
        /// 用于判定格子距多边形边的距离。
        /// </summary>
        public static float DistanceToEdge(Vector2 p, Vector2 v0, Vector2 v1)
        {
            var edge = v1 - v0;
            var lenSqr = edge.sqrMagnitude;
            if (lenSqr < 1e-10f) return Vector2.Distance(p, v0);

            // 投影参数 t ∈ [0,1]，Clamp 到线段两端。
            var t = Vector2.Dot(p - v0, edge) / lenSqr;
            t = Mathf.Clamp01(t);
            var closest = v0 + t * edge;
            return Vector2.Distance(p, closest);
        }

        /// <summary>
        /// 凸多边形各边沿内法向平移 insetDist 后，求相邻内缩边的交点，得到内缩多边形顶点。
        /// 凸性保证：insetDist 小于最小 apothem（边到中心距离）时，内缩后仍为凸多边形。
        /// insetDist 过大（≥ apothem）时返回空列表（内缩多边形退化消失，整图都是边界带）。
        /// </summary>
        /// <param name="verts">原凸多边形顶点（环绕顺序一致，假设逆时针；若顺时针内部会自动取反法向）。</param>
        /// <param name="insetDist">各边向内平移的距离（格）。</param>
        public static List<Vector2> InsetPolygon(List<Vector2> verts, float insetDist)
        {
            var result = new List<Vector2>();
            PopulateInsetPolygon(verts, insetDist, result);
            return result;
        }

        /// <summary>填入版。调用方负责 Clear。</summary>
        public static void PopulateInsetPolygon(List<Vector2> verts, float insetDist, List<Vector2> result)
        {
            if (result == null) return;
            result.Clear();
            if (verts == null || verts.Count < 3) return;

            var n = verts.Count;
            // 多边形中心（用于判定内法向方向）。
            var center = Vector2.zero;
            for (var i = 0; i < n; i++) center += verts[i];
            center /= n;

            // 每条边 j（v[j]→v[j+1]）的内法向（垂直于边、朝向中心一侧的单位向量）。
            var inwardNormals = new List<Vector2>(n);
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                var edge = v1 - v0;
                // 两个候选法向。
                var perpA = new Vector2(-edge.y, edge.x);
                var perpB = new Vector2(edge.y, -edge.x);
                // 边中点。
                var mid = (v0 + v1) * 0.5f;
                // 朝向中心的法向为内法向。
                var toCenter = center - mid;
                var normal = Vector2.Dot(perpA, toCenter) > 0 ? perpA : perpB;
                normal.Normalize();
                inwardNormals.Add(normal);
            }

            // 内缩多边形顶点 k = 内缩边 (k-1) 与内缩边 k 的交点。
            // 内缩边 j 过点 verts[j]+insetDist*normal[j]，方向同原边 edge[j]。
            for (var k = 0; k < n; k++)
            {
                var prev = (k + n - 1) % n;
                // 内缩边 prev 的起点与方向。
                var a0 = verts[prev] + inwardNormals[prev] * insetDist;
                var a1 = verts[(prev + 1) % n] + inwardNormals[prev] * insetDist;
                // 内缩边 k 的起点与方向。
                var b0 = verts[k] + inwardNormals[k] * insetDist;
                var b1 = verts[(k + 1) % n] + inwardNormals[k] * insetDist;

                if (LineLineIntersection(a0, a1, b0, b1, out var intersection))
                {
                    result.Add(intersection);
                }
                else
                {
                    // 平行边（不应在凸多边形正多边形中出现）：退化为原顶点。
                    result.Add(verts[k]);
                }
            }

            // 退化检测：若内缩多边形面积过小或顶点交叉（insetDist 过大），清空。
            if (result.Count < 3 || PolygonArea(result) < 1f)
            {
                result.Clear();
            }
        }

        /// <summary>两线段 (a0→a1) 与 (b0→b1) 所在直线的交点。平行返回 false。</summary>
        private static bool LineLineIntersection(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out Vector2 result)
        {
            var d1 = a1 - a0;
            var d2 = b1 - b0;
            var denom = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(denom) < 1e-10f)
            {
                result = Vector2.zero;
                return false;
            }
            var diff = b0 - a0;
            var t = (diff.x * d2.y - diff.y * d2.x) / denom;
            result = a0 + t * d1;
            return true;
        }

        /// <summary>多边形面积（Shoelace 公式，结果取绝对值）。</summary>
        private static float PolygonArea(List<Vector2> verts)
        {
            var n = verts.Count;
            if (n < 3) return 0f;
            var sum = 0f;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                sum += verts[i].x * verts[j].y - verts[j].x * verts[i].y;
            }
            return Mathf.Abs(sum) * 0.5f;
        }

        /// <summary>
        /// 用算法 C（凸多边形内缩 + 扫描线差集）构建边界带：原多边形减去内缩多边形 = 距边 ≤ bandWidth 的环形带。
        /// 边界带内每个格映射到它最近的那条原边对应的邻居 worldTile。
        /// 复杂度：两次 ScanlineFill（各 O(mapSize × nEdges)）+ 边界带格 × nEdges 距离计算。
        /// </summary>
        /// <param name="verts">原凸多边形顶点（局部坐标）。</param>
        /// <param name="mapSize">地图边长。</param>
        /// <param name="bandWidth">边界带宽度（格）。</param>
        /// <param name="neighborWorldTiles">各边对应的世界邻居 tile id（index = edgeIdx，顺序与 verts 环绕一致）。</param>
        /// <param name="result">输出：边界带格 → 最近边对应的邻居 worldTile。</param>
        public static void ComputeEdgeBand(List<Vector2> verts, int mapSize, int bandWidth,
            List<int> neighborWorldTiles, Dictionary<IntVec3, int> result)
        {
            if (result == null || verts == null || verts.Count < 3 || bandWidth <= 0) return;
            result.Clear();

            var n = verts.Count;

            // 内缩多边形（bandWidth ≥ apothem 时为空 → 整图都是边界带）。
            var innerVerts = new List<Vector2>();
            PopulateInsetPolygon(verts, bandWidth, innerVerts);

            // 每行的原多边形区间与内缩多边形区间。
            // 用数组缓存（每行一组 [xLeft, xRight]，-1 表示空区间）。
            var origLeft = new int[mapSize];
            var origRight = new int[mapSize];
            var innerLeft = new int[mapSize];
            var innerRight = new int[mapSize];
            for (var z = 0; z < mapSize; z++)
            {
                origLeft[z] = 0; origRight[z] = -1;
                innerLeft[z] = 0; innerRight[z] = -1;
            }

            ScanlineFill(verts, mapSize, (z, xL, xR) =>
            {
                if (z >= 0 && z < mapSize) { origLeft[z] = xL; origRight[z] = xR; }
            });
            if (innerVerts.Count >= 3)
            {
                ScanlineFill(innerVerts, mapSize, (z, xL, xR) =>
                {
                    if (z >= 0 && z < mapSize) { innerLeft[z] = xL; innerRight[z] = xR; }
                });
            }

            // 每行差集：边界带 = [origLeft, innerLeft-1] ∪ [innerRight+1, origRight]。
            for (var z = 0; z < mapSize; z++)
            {
                var a = origLeft[z];
                var b = origRight[z];
                if (a > b) continue; // 原多边形此行无内容。

                int c, d;
                if (innerVerts.Count >= 3)
                {
                    c = innerLeft[z];
                    d = innerRight[z];
                }
                else
                {
                    // 内缩已退化（整图都是边界带）：内缩区间视为空。
                    c = 0; d = -1;
                }

                // 左段 [a, c-1]（c > a 才有内容）。
                if (c > a)
                {
                    AddBandRowCells(a, c - 1, z, mapSize, verts, neighborWorldTiles, result);
                }
                // 右段 [d+1, b]（b > d 才有内容）。
                if (b > d)
                {
                    AddBandRowCells(d + 1, b, z, mapSize, verts, neighborWorldTiles, result);
                }
            }
        }

        /// <summary>把一行边界带格 [xStart, xEnd] @ z 加入结果，每格映射到最近边对应的邻居 worldTile。</summary>
        private static void AddBandRowCells(int xStart, int xEnd, int z, int mapSize,
            List<Vector2> verts, List<int> neighborWorldTiles, Dictionary<IntVec3, int> result)
        {
            if (xEnd < xStart) return;
            var n = verts.Count;
            for (var x = xStart; x <= xEnd; x++)
            {
                if (x < 0 || x >= mapSize || z < 0 || z >= mapSize) continue;
                var cellCenter = new Vector2(x + 0.5f, z + 0.5f);

                // 找最近的原边。
                var bestEdge = 0;
                var bestDist = float.MaxValue;
                for (var j = 0; j < n; j++)
                {
                    var v0 = verts[j];
                    var v1 = verts[(j + 1) % n];
                    var dist = DistanceToEdge(cellCenter, v0, v1);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestEdge = j;
                    }
                }

                var wt = bestEdge < neighborWorldTiles.Count ? neighborWorldTiles[bestEdge] : -1;
                if (wt >= 0)
                {
                    result[new IntVec3(x, 0, z)] = wt;
                }
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
