using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块多边形几何工具（阶段3：多边形裁切）。
    /// 基于等边顶点模型（2026-08，勿回退为外接圆半径恒 0.5S）：目标边长 = 0.5S，
    /// 半径按相邻顶点方向平均弦长反算（正六边形下精确回落 0.5S；五边形收缩为 ≈0.4253S，
    /// 边长与六边形相等——世界网格上各边球面等长，旧半径恒定模型五边形边长长 ~17.6% 且五-六共享边两图投影不等长）。
    /// 所有方法统一遍历 N 个顶点（N=5 或 6），不区分五边形/六边形。
    ///
    /// 提供多边形顶点构造、点在多边形内判定、
    /// 距 void 边界环形带（ComputeVoidBand，传送点/混合带/边界带共用）等能力，
    /// 供 GenStep 铺虚空、ComputeNeighborOffset 算偏移、PlaceEnterSpotsAllNeighbors 放传送点、
    /// Registry 归属判定复用。
    /// </summary>
    public static class SeamlessPolygonGeometry
    {
        /// <summary>
        /// 条带快照的 B∪T（最终实况区）存储限深（格，切比雪夫）：接缝带 B 全收 + 过渡带 T
        /// 收到这个深度。T 区数据只服务"错位 ≤3 格时对端照抄格的浅层参考"，限深即可。
        /// **外条带（原生参考区）不限深**——存到 A 的方形边（权重衰减参考数据全深，
        /// 见 SeamlessSeamOverride 权重公式）。
        /// </summary>
        public const int SeamStripInnerDepth = 6;

        /// <summary>
        /// 多边形顶点进程级缓存。worldTile 的世界网格几何（顶点/邻居/中心）在单局游戏内不变，
        /// 键 = (worldTile, mapSize)。
        /// 消除 TryGetOwnerNeighbor/GenStep/ComputeNeighborOffset 等重复的世界网格查询 + 切平面基计算。
        /// 生命周期（2026-09 收口，勿回退为"永不失效"）：图删除时经 <see cref="ReleaseTileCaches"/> 释放、
        /// 同进程换档时 <see cref="ClearAllCaches"/> 全清——旧假设"开新档是新进程"不成立（主菜单开新档同进程）。
        /// </summary>
        private static readonly Dictionary<(int worldTile, int mapSize), List<Vector2>> polygonCache = new();

        /// <summary>
        /// 接缝带几何信息进程级缓存（键 = (worldTile, mapSize)，生命周期同 polygonCache：
        /// 图删除释放 + 换档全清）。每条 <see cref="SeamBandInfo"/> 把全图格装进 7 个集合
        /// （250 图推算 ≈4 MB/tile，2026-09 内存盘点），不随图删释放会长会话无界累积。
        /// </summary>
        private static readonly Dictionary<(int worldTile, int mapSize), SeamBandInfo> seamBandCache = new();

        /// <summary>
        /// 接缝带几何信息（权威定义见 doc/接缝带定义.md）。以离散边为锚的方向词：
        /// 接缝带内 = 核心侧，接缝带外 = void 侧。不用"六边形外/内"指格集合（离散边格横跨连续边，内外归属歧义）；
        /// "格中心在多边形内/外"仅作为无歧义的标量判定（<see cref="ContainsPointAt"/>）。
        ///
        /// 结构（从核心区向外的圈层顺序）：
        /// 核心区 → 带内圈 → 离散边圈 → 带外圈 → void。三圈合称接缝带 B。
        /// </summary>
        public sealed class SeamBandInfo
        {
            /// <summary>离散边圈 D：格方块 [x,x+1]×[z,z+1] 与任一连续边线段相交（含角点接触）的格集合。横跨连续边，不参与内外分类。</summary>
            public readonly HashSet<IntVec3> DiscreteEdge = new();

            /// <summary>接缝带 B = Cheb(D,1)（离散边 + 切比雪夫内外各一圈，约 3 圈厚；对角接触被切比雪夫膨胀填满，无洞）。</summary>
            public readonly HashSet<IntVec3> Band = new();

            /// <summary>带外圈：B−D 中格中心在多边形外的格（void 侧一圈；实地形，属传送圈）。</summary>
            public readonly HashSet<IntVec3> OuterRing = new();

            /// <summary>带内圈：B−D 中格中心在多边形内的格（核心侧一圈；无传送点）。</summary>
            public readonly HashSet<IntVec3> InnerRing = new();

            /// <summary>传送圈 = 离散边圈 ∪ 带外圈（外侧 2 圈，铺传送点；带内圈无传送点）。</summary>
            public readonly HashSet<IntVec3> TransportRing = new();

            /// <summary>过渡带 T：接缝带内一侧（核心侧）Chebyshev 距 B ≥1 的全部格（膨胀到核心区穷尽）。SeamOverride 混合枚举范围（数据命中过滤）。</summary>
            public readonly HashSet<IntVec3> TransitionBand = new();

            /// <summary>过渡带每格深度（距 B 的切比雪夫距离）。</summary>
            public readonly Dictionary<IntVec3, int> TransitionDepth = new();

            /// <summary>外条带：接缝带外一侧（void 侧）全部格（膨胀到方形边穷尽）。原生 snapshot 参考区。</summary>
            public readonly HashSet<IntVec3> OuterStrip = new();

            /// <summary>外条带每格深度（距 B 的切比雪夫距离）。供权重衰减用。</summary>
            public readonly Dictionary<IntVec3, int> OuterStripDepth = new();
        }

        /// <summary>
        /// 构造 worldTile 的接缝带几何信息（进程级缓存）。N=5/6 通用（遍历全部连续边）。
        /// 消费方：void 铺设（SeamlessTerrainFill）、清岩（Patch_GenStepRocksFromGrid）、
        /// 传送圈铺设（SeamlessEnterSpotPlacer）、SeamOverride 混合范围与邻居参考判定、Dev 探针。
        /// </summary>
        public static SeamBandInfo BuildSeamBand(int worldTile, int mapSize)
        {
            var key = (worldTile, mapSize);
            if (seamBandCache.TryGetValue(key, out var cached)) return cached;

            var verts = BuildPolygonVertices(worldTile, mapSize);
            var info = new SeamBandInfo();
            if (verts.Count >= 3)
            {
                // 离散边圈：每条连续边的 bbox（膨胀 1 格容接触）内做精确线段-格方块相交测试。
                for (var j = 0; j < verts.Count; j++)
                {
                    var v0 = verts[j];
                    var v1 = verts[(j + 1) % verts.Count];
                    var x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.x, v1.x)) - 1);
                    var x1 = Mathf.Min(mapSize - 1, Mathf.CeilToInt(Mathf.Max(v0.x, v1.x)));
                    var z0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.y, v1.y)) - 1);
                    var z1 = Mathf.Min(mapSize - 1, Mathf.CeilToInt(Mathf.Max(v0.y, v1.y)));
                    for (var x = x0; x <= x1; x++)
                    {
                        for (var z = z0; z <= z1; z++)
                        {
                            if (SegmentIntersectsSquare(v0, v1, x, z))
                                info.DiscreteEdge.Add(new IntVec3(x, 0, z));
                        }
                    }
                }

                // 接缝带 = 离散边的 8 邻膨胀并（Cheb(D,1)，含 D）。8 邻遍历 = GenAdj.AdjacentCells
                // （SeamlessGridMath 统一口径，勿手搓 dx/dz）。
                foreach (var c in info.DiscreteEdge)
                {
                    info.Band.Add(c);
                    var ring = GenAdj.AdjacentCells;
                    for (var i = 0; i < ring.Length; i++)
                    {
                        var n = new IntVec3(c.x + ring[i].x, 0, c.z + ring[i].z);
                        if (n.x >= 0 && n.x < mapSize && n.z >= 0 && n.z < mapSize)
                            info.Band.Add(n);
                    }
                }

                // 三圈分类：B−D 按格中心在多边形内/外分带内圈/带外圈。
                foreach (var c in info.Band)
                {
                    if (info.DiscreteEdge.Contains(c)) continue;
                    if (ContainsPointAt(verts, c.x + 0.5f, c.z + 0.5f)) info.InnerRing.Add(c);
                    else info.OuterRing.Add(c);
                }
                info.TransportRing.UnionWith(info.DiscreteEdge);
                info.TransportRing.UnionWith(info.OuterRing);

                // 过渡带（核心侧，膨胀到核心区穷尽——对向膨胀在中心相遇自然终止）与
                // 外条带（void 侧，膨胀到方形边穷尽）——多轮切比雪夫膨胀，按中心在内/外分流入带。
                // 两者都穷尽存储：混合范围数据驱动（strip 命中过滤），权重按 a 的位置衰减
                // （接近源六边形高 → 源方形边 0，见 SeamlessSeamOverride）。
                var frontier = new List<IntVec3>(info.Band);
                var visitedCore = new HashSet<IntVec3>(info.Band);
                var visitedOuter = new HashSet<IntVec3>(info.Band);
                var k = 0;
                while (frontier.Count > 0)
                {
                    k++;
                    var next = new List<IntVec3>();
                    foreach (var c in frontier)
                    {
                        var ring = GenAdj.AdjacentCells;
                        for (var i = 0; i < ring.Length; i++)
                        {
                            var n = new IntVec3(c.x + ring[i].x, 0, c.z + ring[i].z);
                            if (n.x < 0 || n.x >= mapSize || n.z < 0 || n.z >= mapSize) continue;
                            var centerIn = ContainsPointAt(verts, n.x + 0.5f, n.z + 0.5f);
                            if (centerIn)
                            {
                                if (!visitedCore.Add(n)) continue;
                                info.TransitionBand.Add(n);
                                info.TransitionDepth[n] = k;
                                next.Add(n);
                            }
                            else
                            {
                                if (!visitedOuter.Add(n)) continue;
                                info.OuterStrip.Add(n);
                                info.OuterStripDepth[n] = k;
                                next.Add(n);
                            }
                        }
                    }
                    frontier = next;
                }
            }

            seamBandCache[key] = info;
            return info;
        }

        /// <summary>
        /// 释放 tile 的接缝带几何与多边形顶点缓存（图删除时调用，2026-09 泄漏收口）。
        /// 纯几何、随时可由 <see cref="BuildSeamBand"/>/BuildPolygonVertices 重算：删除后无消费者
        /// （几何查询全在活图上）、同 tile 再生成时自然重建，释放零语义风险。键按 worldTile 匹配
        /// （同一 tile 只有一种 mapSize，按 tile 匹配可覆盖假设外的尺寸残留）。
        /// </summary>
        public static void ReleaseTileCaches(int worldTile)
        {
            if (worldTile < 0) return;
            RemoveTileKeys(polygonCache, worldTile);
            RemoveTileKeys(seamBandCache, worldTile);
        }

        /// <summary>清空全部几何进程缓存（同进程换档时调用——旧档图不经 DeinitAndRemoveMap 整体丢弃，MapRemoved 不触发）。</summary>
        public static void ClearAllCaches()
        {
            polygonCache.Clear();
            seamBandCache.Clear();
        }

        private static void RemoveTileKeys<TKey>(Dictionary<(int worldTile, int mapSize), TKey> cache, int worldTile)
        {
            List<(int, int)> stale = null;
            foreach (var key in cache.Keys)
            {
                if (key.worldTile != worldTile) continue;
                stale ??= new List<(int, int)>();
                stale.Add(key);
            }
            if (stale == null) return;
            foreach (var key in stale) cache.Remove(key);
        }

        /// <summary>
        /// void 判定（权威）：格 ∈ 接缝带 B → 非 void；否则格中心在多边形外 → void。
        /// 凸多边形下与旧"中心或 4 角任一在内"角检测防孤岛等价：角在内的格必属 {中心在内} ∪ 离散边圈（⊆ B）。
        /// </summary>
        public static bool IsVoidCell(SeamBandInfo band, int worldTile, int mapSize, IntVec3 cell)
        {
            if (band.Band.Contains(cell)) return false;
            return !ContainsPointAt(BuildPolygonVertices(worldTile, mapSize), cell.x + 0.5f, cell.z + 0.5f);
        }

        /// <summary>
        /// 线段 (v0→v1) 与格方块 [x,x+1]×[z,z+1] 相交判定（slab 法，含边界/角点接触）。
        /// "恰好经过四个格的公共角"时 4 个格都命中——离散边定义要求角点接触也算覆盖。
        /// </summary>
        internal static bool SegmentIntersectsSquare(Vector2 v0, Vector2 v1, int x, int z)
        {
            var minX = (float)x;
            var maxX = x + 1f;
            var minZ = (float)z;
            var maxZ = z + 1f;

            var tMin = 0f;
            var tMax = 1f;
            var dx = v1.x - v0.x;
            var dz = v1.y - v0.y;

            if (Mathf.Abs(dx) < 1e-12f)
            {
                if (v0.x < minX || v0.x > maxX) return false;
            }
            else
            {
                var t1 = (minX - v0.x) / dx;
                var t2 = (maxX - v0.x) / dx;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }

            if (Mathf.Abs(dz) < 1e-12f)
            {
                if (v0.y < minZ || v0.y > maxZ) return false;
            }
            else
            {
                var t1 = (minZ - v0.y) / dz;
                var t2 = (maxZ - v0.y) / dz;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }

            return tMin <= tMax;
        }

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

            // 等边模型：目标边长 = 0.5S（世界网格上五-五/五-六/六-六边球面等长，
            // 外接圆半径恒定的旧模型会让五边形边长比六边形长 ~17.6%，五-六共享边两图投影不等长）。
            // 半径 = 目标边长 / 相邻顶点方向的平均弦长；正六边形平均弦长 = 1，radius 精确回落 0.5S（零回归）。
            var avgEdge = 0f;
            for (var i = 0; i < dirs.Count; i++)
            {
                avgEdge += (dirs[(i + 1) % dirs.Count] - dirs[i]).magnitude;
            }
            avgEdge /= dirs.Count;
            var radius = avgEdge > 1e-4f ? (mapSize * 0.5f) / avgEdge : mapSize * 0.5f;

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
        /// 构建"距 void 边界 ≤ bandWidth 格"的环形带（读 terrainGrid void 实况）。
        ///
        /// 带 = 到最近 void 格的切比雪夫距离 ∈ {1..bandWidth} 的非 void 格（void 本身不入带）。
        /// 每个 result 格映射到它最近的多边形边 j 对应的邻居 worldTile（<paramref name="neighborWorldTiles"/>[j]）。
        ///
        /// **算法（多轮膨胀）**：以 void 格为种子，每轮把已标记层的 8-邻格（含自身，切比雪夫距离）并入，
        /// 共 <paramref name="bandWidth"/> 轮。等价于"void 向外膨胀 bandWidth 格"。复杂度 O(N²·bandWidth)。
        ///
        /// **消费方（3 圈接缝带重构后）**：仅剩 <see cref="SeamlessBorderLookup"/> 的预加载带
        /// （borderPreloadDistance，默认 15）与禁建带（borderNoBuildDistance，默认 3）——两者读
        /// terrainGrid void 实况即可，自动适应新 void 形状（void 边界退到接缝带外）。传送点带
        /// 已改用 <see cref="BuildSeamBand"/> 传送圈（纯几何），SeamOverride 已改用接缝条带快照。
        ///
        /// **调用时机**：必须在 void 铺设（ApplyPolygonTerrain）之后调用——本方法直接读 terrainGrid 判定 void。
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
                    for (var x = 0; x < sx; x++)
                    {
                        var idx = z * sx + x;
                        if (isVoid[idx]) continue;
                        if (distArr[idx] >= 0) continue; // 已标记（更小距离），保持。
                        // 8-邻域（GenAdj.AdjacentCells，SeamlessGridMath 统一口径）内是否有
                        // 距离 = k-1 的标记格（上一轮）。越界格跳过（原 clamp 语义等价）。
                        var hit = false;
                        var ring = GenAdj.AdjacentCells;
                        for (var i = 0; i < ring.Length && !hit; i++)
                        {
                            var nx = x + ring[i].x;
                            var nz = z + ring[i].z;
                            if (nx < 0 || nx >= sx || nz < 0 || nz >= sz) continue;
                            if (distArr[nz * sx + nx] == k - 1) hit = true;
                        }
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
        /// 按世界图方向角找对应的多边形边索引（精确邻居身份映射，替代旧的本地边中点角度近似）。
        ///
        /// 【为什么不用本地边中点角度匹配】六边形边中点方向离散（间隔 60°）+ 切平面投影扭曲：
        /// 世界图上"正南 180°"的邻居方向，本地边中点方向可能是 150°/210°（该 tile 没有朝正南的边），
        /// 角度最近匹配会锚到 SE/SW 边——而**河/路 link 的真实边**（与那个邻居共享的边）被 30° 偏差
        /// 挤掉，导致"南北河被画成东北-西南、下方地图连不上"。
        ///
        /// 【做法】枚举世界邻居算 <c>GetHeadingFromTo(me, neighbor)</c>（世界图真值，无投影扭曲），
        /// 找与 <paramref name="worldAngle"/> 方向差最小的邻居，再用邻居在 <c>GetTileNeighbors</c>
        /// 中的索引直接映射到边索引——"边 j ↔ 邻居 j"是传送点系统（ComputeVoidBand 的
        /// neighborWorldTiles[edgeIdx]）依赖的既有架构事实。
        ///
        /// 调用方语义：road 的 angle 即 me→link邻居 heading（精确命中自身）；river 的 angle 是
        /// far→near 流向（≈ me→near），上游端用 angle+180° 再调一次。
        ///
        /// 【匹配池过滤（2026-08 河流错边根因修复，道路/河流共用的唯一实现）】
        /// 传入的 angle 不可全信：road 侧原版 CalculateNeededRoads 对多条路加向量平均偏置（两条路
        /// 夹角 60° 时偏置可达 60°）；river 侧 angle 只是流向近似（河链可弯折）。全邻居池最近角匹配
        /// 会锚到错误边。调用方必须传 <paramref name="linkFilter"/> 把匹配池缩到合法 link 邻居
        /// （road：GetRoadDef != null；river：GetRiverDef != null）——偏置再大也命中正确 link。
        /// 过滤后匹配池为空返回 -1，调用方放行原版（与道路同款失败语义）。
        /// </summary>
        /// <returns>边索引（=-1 无邻居/匹配失败）。</returns>
        internal static int FindEdgeByWorldHeading(int worldTile, float worldAngle,
            Func<PlanetTile, bool> linkFilter = null)
        {
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, neighbors);
            if (neighbors.Count == 0) return -1;

            var bestIdx = -1;
            var bestDiff = float.MaxValue;
            for (var j = 0; j < neighbors.Count; j++)
            {
                if (linkFilter != null && !linkFilter(neighbors[j])) continue; // 匹配池过滤
                var heading = Find.WorldGrid.GetHeadingFromTo(worldTile, neighbors[j]);
                var diff = GenGeo.AngleDifferenceBetween(heading, worldAngle);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = j;
                }
            }
            return bestIdx;
        }

        /// <summary>接缝 link 种类（河/路共用同一套边匹配/穿越点/锚点服务，仅此枚举不同）。</summary>
        internal enum SeamLink
        {
            River = 0,
            Road = 1,
        }

        /// <summary>
        /// 统一接缝边匹配（2026-08-31 河/路收编）：link 种类决定匹配池过滤
        /// （River → GetRiverDef != null；Road → GetRoadDef != null），其余逻辑全同
        /// <see cref="FindEdgeByWorldHeading"/>。河的上下游 = 两次调用（angle / angle+180°），
        /// 路的单次调用。过滤后匹配池空返回 -1，调用方放行原版。
        /// </summary>
        internal static int FindSeamEdge(int worldTile, float worldAngle, SeamLink linkKind)
        {
            return linkKind == SeamLink.River
                ? FindEdgeByWorldHeading(worldTile, worldAngle, n => Find.WorldGrid.GetRiverDef(worldTile, n) != null)
                : FindEdgeByWorldHeading(worldTile, worldAngle, n => Find.WorldGrid.GetRoadDef(worldTile, n) != null);
        }

        /// <summary>
        /// 真·边法线（外向）：边向量 (v1−v0) 的垂直向量，取指向多边形外的一侧（用顶点质心判定）。
        /// 比"边中点 − 地图中心"射线准确——后者在五边形/不规则投影下与真实边法线有偏差，
        /// 是道路锚点"些许偏移"的来源之一（2026-08-31 修正）。等边模型下两者理论重合。
        /// </summary>
        internal static Vector2 EdgeOutwardNormal(List<Vector2> verts, int edgeIdx)
        {
            var n = verts.Count;
            var v0 = verts[edgeIdx];
            var v1 = verts[(edgeIdx + 1) % n];
            var centroid = Vector2.zero;
            for (var i = 0; i < n; i++) centroid += verts[i];
            centroid /= n;

            var dir = v1 - v0;
            var perp = new Vector2(-dir.y, dir.x);
            if (Vector2.Dot(perp, (v0 + v1) * 0.5f - centroid) < 0f) perp = -perp;
            return perp.normalized;
        }

        /// <summary>
        /// 确定性接缝穿越点（2026-08-31，河/路共用）：同一世界共享边在两侧地图上算出**同一空间点**，
        /// 摆脱"恒边中点"限制。两侧一致性靠两点：
        /// ① 对称哈希——t = 0.5 + u·0.25，u∈[−1,1) 由 SymHash(min(tileA,tileB), max(...), salt(linkKind))
        ///   派生，两侧输入相同（tile 对 + link 种类），salt 区分河/路（同一条边上路和河错开）；
        /// ② 参考端点对称化——t 的计量起点必须两侧一致。边 j 的两个世界 3D 顶点（GetTileVertices
        ///   与 2D 投影逐索引对应）用对称谓词（四舍五入坐标字典序）选出"参考顶点"，本侧参考端是
        ///   本地 v0 还是 v1 决定 t 折算为 t 或 1−t——不依赖任何绕序/投影方向假设。
        /// </summary>
        /// <returns>穿越点（本地 2D 连续坐标，落在边上）；几何异常返回边中点。</returns>
        internal static Vector2 SeamCrossingPoint(int worldTile, int edgeIdx, int mapSize, SeamLink linkKind)
        {
            var verts = BuildPolygonVertices(worldTile, mapSize);
            if (edgeIdx < 0 || edgeIdx >= verts.Count) return Vector2.zero;
            var n = verts.Count;
            var v0 = verts[edgeIdx];
            var v1 = verts[(edgeIdx + 1) % n];

            // 对端邻居（穿越点是"我↔邻居"这条共享边的属性，哈希输入必须两侧同值）。
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, neighbors);
            if (edgeIdx >= neighbors.Count) return (v0 + v1) * 0.5f;
            var other = neighbors[edgeIdx].tileId;

            // 对称哈希 → u ∈ [−1,1)。
            // **固定 FNV-1a，勿换 System.HashCode**——后者带进程随机种子，跨会话/读档后再生成
            // 会得出不同穿越点（2026-08-31 用户实测"读档后端点错位"的根因）。此哈希是跨存档
            // 契约：算法与常数永不更改。
            var salt = (int)linkKind * 7919 + 49297;
            var a = (uint)Math.Min(worldTile, other);
            var b = (uint)Math.Max(worldTile, other);
            var h = 2166136261u;
            foreach (var v in new[] { a, b, (uint)salt })
            {
                h ^= v;
                h *= 16777619u;
                h ^= v >> 11; h *= 16777619u;
                h ^= v << 7; h *= 16777619u;
            }
            var u = (int)(h % 2000) - 1000;
            var t = 0.5f + u / 1000f * 0.25f; // ±边长 1/4

            // 参考端点对称化：3D 顶点对称谓词（四舍五入坐标字典序，两条端点坐标必不同）。
            var verts3D = new List<Vector3>();
            Find.WorldGrid.GetTileVertices(worldTile, verts3D);
            if (edgeIdx >= verts3D.Count) return Vector2.Lerp(v0, v1, t);
            var w0 = verts3D[edgeIdx];
            var w1 = verts3D[(edgeIdx + 1) % verts3D.Count];
            var refIsFirst = SymLess(w0, w1);
            var localT = refIsFirst ? t : 1f - t;

            return Vector2.Lerp(v0, v1, localT);
        }

        /// <summary>对称谓词：两个世界顶点的全序比较（先四舍五入坐标字典序，浮点构造顶点坐标必不同）。</summary>
        private static bool SymLess(Vector3 a, Vector3 b)
        {
            var ax = Mathf.Round(a.x * 8f); var bx = Mathf.Round(b.x * 8f);
            if (ax != bx) return ax < bx;
            var ay = Mathf.Round(a.y * 8f); var by = Mathf.Round(b.y * 8f);
            if (ay != by) return ay < by;
            var az = Mathf.Round(a.z * 8f); var bz = Mathf.Round(b.z * 8f);
            if (az != bz) return az < bz;
            return false; // 同一顶点（不应发生——边端点必不同）
        }

        /// <summary>
        /// 统一接缝锚点工具：指定边的锚点 = 穿越点沿**真·边外法线**（<see cref="EdgeOutwardNormal"/>，
        /// 2026-08-31 起替代"中点−中心"射线近似）步进到的**最外非 void 格**（= 新 void 边界
        /// 内侧第一格，通常在带外圈），再向内偏移 <paramref name="offsetCells"/> 格。
        ///
        /// 基于 <see cref="BuildSeamBand"/> / <see cref="IsVoidCell"/>（唯一 void 判定口径），
        /// **与带宽无关**——旧实现"边中点固定内偏 N 格"是旧抽象（void 边界 = 连续边）的写死
        /// 范围，接缝带定义变更后锚点与新 void 边界脱节（路出口距地图边缘 3-4 格，跨缝断路，
        /// 2026-08 用户实测）。道路出口锚点 offsetCells=0（贴 void 边界，跨缝两侧路相接）。
        ///
        /// <paramref name="crossingOverride"/>：穿越点（默认边中点）。河/路传
        /// <see cref="SeamCrossingPoint"/> 的确定性偏移穿越点——两侧沿各自法线步进，
        /// 渲染 offset 中点对齐后穿越点在 overlap 空间重合。
        /// </summary>
        internal static IntVec3 ComputeSeamCellForEdge(List<Vector2> verts, int edgeIdx, int mapSize, Map map, int offsetCells,
            Vector2? crossingOverride = null)
        {
            var n = verts.Count;
            var v0 = verts[edgeIdx];
            var v1 = verts[(edgeIdx + 1) % n];
            var crossing = crossingOverride ?? (v0 + v1) * 0.5f; // 穿越点（在连续边上）
            var outward = EdgeOutwardNormal(verts, edgeIdx); // 真·边外法线

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return IntVec3.Invalid;
            var band = BuildSeamBand(worldTile, mapSize);

            // 从穿越点向外步进，记录最后一个非 void 格（新 void 边界内侧第一格）。
            IntVec3 anchor = IntVec3.Invalid;
            for (var i = 0; i <= 20; i++)
            {
                var p = crossing + outward * (i * 0.5f);
                var cell = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.y));
                if (!cell.InBounds(map)) break;
                if (IsVoidCell(band, worldTile, mapSize, cell)) break; // 第一个 void 格：停
                anchor = cell;
            }
            if (!anchor.IsValid) return IntVec3.Invalid;

            // 向内偏移 offsetCells 格（0 = 贴 void 边界）。
            if (offsetCells > 0)
            {
                var inward = new Vector2(anchor.x + 0.5f, anchor.z + 0.5f) - outward * offsetCells;
                var cell = new IntVec3(Mathf.RoundToInt(inward.x), 0, Mathf.RoundToInt(inward.y));
                if (!cell.InBounds(map)) return anchor;
                anchor = cell;
            }
            return anchor;
        }

        /// <summary>
        /// 计算 cell 中心点到多边形最近边的浮点垂直距离。
        /// 供 SeamOverride 权重衰减（距 A 边深度）与建筑选址边距判定（Scatterer patch）共用。
        /// </summary>
        public static float DistanceToNearestEdge(List<Vector2> verts, IntVec3 cell)
        {
            var px = cell.x + 0.5f;
            var py = cell.z + 0.5f;
            var minDist = float.MaxValue;
            var n = verts.Count;
            for (var j = 0; j < n; j++)
            {
                var d = DistanceToEdge(new Vector2(px, py), verts[j], verts[(j + 1) % n]);
                if (d < minDist) minDist = d;
            }
            return minDist;
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
    }
}
