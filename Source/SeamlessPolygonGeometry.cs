using System.Collections.Generic;
using RimWorld;
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
        /// 多边形顶点进程级缓存。worldTile 的世界网格几何（顶点/邻居/中心）在单局游戏内不变，
        /// 开新档是新进程，故缓存无需主动失效。键 = (worldTile, mapSize)。
        /// 消除 TryGetOwnerNeighbor/GenStep/ComputeNeighborOffset 等重复的世界网格查询 + 切平面基计算。
        /// </summary>
        private static readonly Dictionary<(int worldTile, int mapSize), List<Vector2>> polygonCache = new();

        /// <summary>
        /// 构造地块 worldTile 在 mapSize 地图内的多边形顶点（局部坐标，Vector2：x=东向格，y=北向格）。
        /// 顶点 = center + 0.5S × 顶点方向（内切圆模型）。
        /// 顺序与 WorldTileGeometry.ComputeVertexDirections 一致。
        /// 结果经进程级缓存，单局内重复调用零重算。
        /// </summary>
        public static List<Vector2> BuildPolygonVertices(int worldTile, int mapSize)
        {
            var result = new List<Vector2>();
            PopulatePolygonVerticesCached(worldTile, mapSize, result);
            return result;
        }

        /// <summary>填入版（带缓存）。调用方负责 Clear。命中缓存直接填充，未命中计算后存拷贝。</summary>
        public static void PopulatePolygonVerticesCached(int worldTile, int mapSize, List<Vector2> result)
        {
            if (result == null) return;
            var key = (worldTile, mapSize);
            if (polygonCache.TryGetValue(key, out var cached))
            {
                result.Clear();
                result.AddRange(cached);
                return;
            }
            PopulatePolygonVertices(worldTile, mapSize, result);
            // 存一份拷贝，防调用方修改污染缓存。
            polygonCache[key] = new List<Vector2>(result);
        }

        /// <summary>填入版（无缓存，实际计算）。调用方负责 Clear。</summary>
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
        /// 点在凸多边形内判定（格中心采样）。
        /// cell 是否在多边形内（含边界）。
        /// </summary>
        public static bool ContainsPoint(List<Vector2> verts, int mapSize, IntVec3 cell)
        {
            return ContainsPointAt(verts, cell.x + 0.5f, cell.z + 0.5f);
        }

        /// <summary>
        /// 判定点 (px, py) 是否在凸多边形内（含边界）。用叉积符号一致性。
        /// 供格中心/格角检测复用。
        /// </summary>
        public static bool ContainsPointAt(List<Vector2> verts, float px, float py)
        {
            var n = verts.Count;
            if (n < 3) return false;

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
        /// 判定格是否在多边形内（用于 void 铺设，保证连续性，消除孤岛）。
        /// 格中心在多边形内，**或** 格的 4 个角中任一在多边形内 → 非 void。
        /// 凸性保证：外部格的 4 角都不在多边形内，内部格（含边附近）至少一个角在内。
        /// 这消除了"格中心恰好在边外侧但格面积大部分在内"的边界判定问题（RoundToInt/Bresenham 圆整误差根源）。
        /// </summary>
        public static bool IsCellInPolygon(List<Vector2> verts, int mapSize, IntVec3 cell)
        {
            // 格中心。
            if (ContainsPointAt(verts, cell.x + 0.5f, cell.z + 0.5f)) return true;
            // 4 个角。
            if (ContainsPointAt(verts, cell.x, cell.z)) return true;
            if (ContainsPointAt(verts, cell.x + 1f, cell.z)) return true;
            if (ContainsPointAt(verts, cell.x, cell.z + 1f)) return true;
            if (ContainsPointAt(verts, cell.x + 1f, cell.z + 1f)) return true;
            return false;
        }

        /// <summary>
        /// 沿多边形边 j（连接顶点 j 与顶点 j+1）用 Bresenham 枚举线经过的格子。
        /// 用于传送点划线满铺接缝。浮点顶点先 RoundToInt 成整数端点，再走标准整数 Bresenham。
        /// 注：void 判定已改用 IsCellInPolygon 格角检测（不依赖此划线），故不做边格补偿——
        /// 边附近的 void 孤岛由格角检测消除，传送点只需覆盖边线经过的主体格。
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
        /// 找点 (px, py) 最近的多边形边索引（到边的垂直距离最小）。
        /// 用于把一个格映射到它最接近的那条边对应的邻居（如传送点 targetWorldTile 分组）。
        /// </summary>
        public static int FindClosestEdgeIndex(List<Vector2> verts, float px, float py)
        {
            var n = verts.Count;
            if (n == 0) return -1;
            var bestEdge = 0;
            var bestDist = float.MaxValue;
            var p = new Vector2(px, py);
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                var dist = DistanceToEdge(p, v0, v1);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestEdge = j;
                }
            }
            return bestEdge;
        }

        /// <summary>
        /// 构建"距 void 边界 ≤ bandWidth 格"的环形带（所有"距边界 N 格"语义的唯一实现）。
        ///
        /// 带 = 到最近 void 格的切比雪夫距离 ∈ {1..bandWidth} 的非 void 格（void 本身不入带）。
        /// 每个 result 格映射到它最近的多边形边 j 对应的邻居 worldTile（<paramref name="neighborWorldTiles"/>[j]）。
        ///
        /// **算法（多轮膨胀）**：以 void 格为种子，每轮把已标记层的 8-邻格（含自身，切比雪夫距离）并入，
        /// 共 <paramref name="bandWidth"/> 轮。等价于"void 向外膨胀 bandWidth 格"。复杂度 O(N²·bandWidth)，
        /// 远优于"对每个 void 格扫 (2r+1)² 邻域"的平移法（void 格数·(2r+1)²）。
        ///
        /// **语义统一（关键）**：直接读 <see cref="TerrainGrid.topGrid"/> 判定 void，与
        /// <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 铺 void 用的格角检测同源——
        /// 杜绝旧 <c>ComputeEdgeBand</c>（浮点多边形边距离）与 void 边界（格角检测）两套口径错配
        /// 导致的"最外圈擦边格漏出带外"问题（SeamOverride 泥土带、传送点缺失的同类根因）。
        ///
        /// **调用时机**：必须在 <see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 之后调用——
        /// 本方法直接读 terrainGrid 判定 void。SeamOverride（genStep 212，void 在 211 铺好）、
        /// BorderLookup（MapGenerated 延迟 1 tick / 首次 tick，genStep 已完成）均满足。
        /// </summary>
        /// <param name="map">地图（需已铺 void）。</param>
        /// <param name="bandWidth">带宽度（切比雪夫格距离）。</param>
        /// <param name="neighborWorldTiles">各边对应的世界邻居 tile id（index = 多边形边索引，顺序与 verts 环绕一致）。</param>
        /// <param name="result">输出：带内格 → 最近边对应的邻居 worldTile。</param>
        public static void ComputeVoidBand(Map map, int bandWidth, List<int> neighborWorldTiles, Dictionary<IntVec3, int> result, Dictionary<IntVec3, int> distances = null)
        {
            if (result == null || map == null || bandWidth <= 0) return;
            result.Clear();
            distances?.Clear();

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null) return;

            var sx = map.Size.x;
            var sz = map.Size.z;
            var total = sx * sz;
            var topGrid = map.terrainGrid.topGrid;
            var verts = BuildPolygonVertices(SeamlessTileRegistry.GetMapWorldTile(map), sx);
            if (verts.Count < 3) return;

            // 用 int 数组记录每格到 void 的切比雪夫距离（0=void 或非带内，k=到最近 void 的切比雪夫距离）。
            // BFS 式多轮膨胀：distArr[idx] = 0 表示 void（种子）或未标记；每轮把上一轮标记格的 8-邻域（非 void）
            // 标为 上一轮值+1。最终 distArr > 0 的格 = 带内，值 = 切比雪夫距离。
            var distArr = new int[total];
            var isVoid = new bool[total];
            for (var i = 0; i < total; i++) { isVoid[i] = topGrid[i] == voidDef; if (isVoid[i]) distArr[i] = 0; else distArr[i] = -1; }

            for (var k = 1; k <= bandWidth; k++)
            {
                for (var z = 0; z < sz; z++)
                {
                    var zMin = z - 1; if (zMin < 0) zMin = 0;
                    var zMax = z + 1; if (zMax >= sz) zMax = sz - 1;
                    for (var x = 0; x < sx; x++)
                    {
                        var idx = z * sx + x;
                        if (isVoid[idx]) continue;
                        if (distArr[idx] >= 0) continue; // 已标记（更小距离），保持。
                        // 8-邻域内是否有距离 = k-1 的标记格（上一轮）。
                        var hit = false;
                        var xMin = x - 1; if (xMin < 0) xMin = 0;
                        var xMax = x + 1; if (xMax >= sx) xMax = sx - 1;
                        for (var nz = zMin; nz <= zMax && !hit; nz++)
                            for (var nx = xMin; nx <= xMax; nx++)
                                if (distArr[nz * sx + nx] == k - 1) { hit = true; break; }
                        if (hit) distArr[idx] = k;
                    }
                }
            }

            for (var z = 0; z < sz; z++)
            {
                for (var x = 0; x < sx; x++)
                {
                    var idx = z * sx + x;
                    var d = distArr[idx];
                    if (d <= 0) continue; // void 或带外。
                    var edgeIdx = FindClosestEdgeIndex(verts, x + 0.5f, z + 0.5f);
                    var wt = edgeIdx >= 0 && edgeIdx < neighborWorldTiles.Count ? neighborWorldTiles[edgeIdx] : -1;
                    if (wt >= 0)
                    {
                        var cell = new IntVec3(x, 0, z);
                        result[cell] = wt;
                        if (distances != null) distances[cell] = d;
                    }
                }
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
