using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 本图各 river-link 共享边的实际穿越点记录（本图 2D 连续坐标，键 = 对端世界 tile id）。
    /// 非序列化——生成期确定性数据：先侧由对称哈希定，后侧读本记录（经 offset 映射），
    /// 两侧独立生成时哈希同值亦一致（闭环，不入存档，读档重生成不变）。
    /// </summary>
    public class SeamlessRiverCrossings : MapComponent
    {
        internal readonly Dictionary<int, Vector2> crossings = new();

        public SeamlessRiverCrossings(Map map) : base(map) { }

        internal static SeamlessRiverCrossings Ensure(Map map)
        {
            var comp = map.GetComponent<SeamlessRiverCrossings>();
            if (comp == null)
            {
                comp = new SeamlessRiverCrossings(map);
                map.components.Add(comp);
            }
            return comp;
        }
    }

    /// <summary>
    /// 本图被河流实际修改的格集合（水 + 河岸，<see cref="Patch_TileMutatorWorker_River_TerrainVoidSkip"/>
    /// 在两个地形判定方法返回非 null 时记录）。非序列化，生成期数据。
    /// 供 SeamOverride 混合跳过——跨地块结构（河流）不可被混合侵犯
    /// （用户定夺 2026-08-31 抽象：地面类型/岩壁可混合调整，河/路等跨地块结构不可）。
    /// </summary>
    public class SeamlessRiverCells : MapComponent
    {
        internal readonly HashSet<IntVec3> cells = new();

        public SeamlessRiverCells(Map map) : base(map) { }

        internal static SeamlessRiverCells Ensure(Map map)
        {
            var comp = map.GetComponent<SeamlessRiverCells>();
            if (comp == null)
            {
                comp = new SeamlessRiverCells(map);
                map.components.Add(comp);
            }
            return comp;
        }
    }

    /// <summary>
    /// 河流接缝对齐 v3（2026-08-31 用户定夺，**勿回退 v1/v2 方案**）：
    ///
    /// 【中线 = 两穿越点连线，延伸到方形边界】河端点不再是接缝锚点/边中点，而是
    /// "两个穿越点的连线与方形 rect（±25 越界余量，对齐原版）的交点"。中线严格过两个穿越点：
    /// 方向由穿越点位置自然决定（河链直则两侧共线、与世界河链一致；弯折时折角与世界一致），
    /// **不掰曲线**（v2 的垂直接近段已删——直线中线无法在两端同时独立指定位置+方向，
    /// 且深度场 t∈[0,1] 截断面 ⟂ 弦的斜切楔口靠"截断面推出方形边外"根治）。
    ///
    /// 【穿越点三级来源】（优先级递减；全部确定性、不入存档）：
    /// ① 邻图记录：对侧已生成且其 <see cref="SeamlessRiverCrossings"/> 有本共享边记录 →
    ///    crossing = 邻记录 + offset（<see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/>，
    ///    纯几何 newLocal + offset = sourceLocal，不依赖邻居表/休眠过滤——数据查询非邻接交互）。
    /// ② 邻图求交：对侧已生成但无记录（它 fallback 过 vanilla）→ 对侧 riverGraph 各 node 直线
    ///    映射到本图坐标后与共享边线段求交（跟随对侧随机河的实际穿越位置）。
    /// ③ 对称哈希：<see cref="SeamlessPolygonGeometry.SeamCrossingPoint"/>（对侧未生成；两侧同值）。
    ///
    /// 【void 跳过 + 免混合】见 <see cref="Patch_TileMutatorWorker_River_TerrainVoidSkip"/>：
    /// 河生成时不向将来 void 格铺水/岸（外条带快照干净，根治快照互盖），实际修改格记入
    /// <see cref="SeamlessRiverCells"/> 供 SeamOverride 跳过。
    ///
    /// 【守卫】<c>worldTile &lt; 0</c> 是防御性放行（防异常态/MapPreview 预览图），正常地图一律
    /// 走接缝对齐（见 AGENTS.md 铁律）。**GL 1.7 河流地貌 = 本 patch 的盲区**：GL 用自有地貌系统
    /// 替换原版河流 mutator，GetMapEdgeNodes 不会被调用——需在 GL 设置禁用全部河流地貌（详见 README）。
    /// </summary>
    static class Patch_TileMutatorWorker_River_GetMapEdgeNodes
    {
        /// <summary>方形 rect 越界余量（格，对齐原版 GetMapEdgeNodes 的 Oversample=25 窗口）。</summary>
        private const float RectMargin = 25f;

        /// <summary>
        /// 每条已锚定河弦的上下文（端点 + 两个穿越点的弦参位置），供
        /// <see cref="Patch_TileMutatorWorker_River_GetDisplacedPoint"/> 做缝附近蜿蜒归零。
        /// 生成期单图顺序执行，static 安全；Confluence 多次调用累加（每 node 一条），
        /// worldTile 变更时清空。MapPreview 预览图（worldTile&lt;0 fallback）不写入。
        /// </summary>
        internal static readonly List<(Vector2 s, Vector2 e, float tA, float tB)> riverChords = new();
        internal static int chordsTile = -1;

        internal static bool Prefix(TileMutatorWorker_River __instance, Map map, float angle,
            ref (Vector3 start, Vector3 end) __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return true; // 防御性：异常态/预览图放行原版

            var mapSize = map.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return true; // 几何异常放行

            // 下游边 + 上游边：统一接缝边匹配（SeamLink.River = 匹配池缩到 river-link 邻居）。
            // angle = far→near 流向 ≈ me→near → 下游邻居；angle+180 → 上游。
            // **匹配池只遍历有 river link 的邻居（GetRiverDef != null）**——angle 只是流向近似、
            // 河链可弯折，全邻居池最近角匹配会锚到错误边（与道路同款纪律）。
            var downEdge = SeamlessPolygonGeometry.FindSeamEdge(worldTile, angle, SeamlessPolygonGeometry.SeamLink.River);
            var upEdge = SeamlessPolygonGeometry.FindSeamEdge(worldTile, angle + 180f, SeamlessPolygonGeometry.SeamLink.River);
            if (RimExodusMod.Settings?.verboseLogging ?? false)
            {
                var cand = new List<string>();
                var nb = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(worldTile, nb);
                for (var j = 0; j < nb.Count; j++)
                {
                    if (Find.WorldGrid.GetRiverDef(worldTile, nb[j]) == null) continue;
                    var heading = Find.WorldGrid.GetHeadingFromTo(worldTile, nb[j]);
                    cand.Add($"edge{j}->tile{nb[j]} heading={heading:F1}° diff={GenGeo.AngleDifferenceBetween(heading, angle):F1}°");
                }
                Log.Message($"[RimExodus] River edge match: map={map.uniqueID} tile={worldTile} angle={angle:F1}° " +
                            $"candidates=[{string.Join("; ", cand)}] => downEdge={downEdge} upEdge={upEdge}");
            }
            if (downEdge < 0 || upEdge < 0 || downEdge >= verts.Count || upEdge >= verts.Count)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] River GetMapEdgeNodes: FALLBACK vanilla (edge match failed: " +
                                $"downEdge={downEdge} upEdge={upEdge} angle={angle:F1}°).");
                return true;
            }
            if (downEdge == upEdge)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] River GetMapEdgeNodes: FALLBACK vanilla (degenerate downEdge==upEdge={downEdge}).");
                return true;
            }

            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, neighbors);
            if (downEdge >= neighbors.Count || upEdge >= neighbors.Count) return true;

            // 两端穿越点（三级来源）。
            var downCrossing = ResolveCrossing(map, worldTile, mapSize, verts, downEdge, neighbors[downEdge].tileId, out var downSource);
            var upCrossing = ResolveCrossing(map, worldTile, mapSize, verts, upEdge, neighbors[upEdge].tileId, out var upSource);
            if (downCrossing == null || upCrossing == null)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] River GetMapEdgeNodes: FALLBACK vanilla (crossing unresolved: down={downCrossing} up={upCrossing}).");
                return true;
            }

            // 中线 = 两穿越点连线，延伸到方形 rect（±RectMargin）边界。
            var dir = upCrossing.Value - downCrossing.Value;
            if (dir.sqrMagnitude < 1e-6f)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message("[RimExodus] River GetMapEdgeNodes: FALLBACK vanilla (degenerate line).");
                return true;
            }
            var (endA, endB) = ExtendToRect(downCrossing.Value, dir, mapSize);
            // 近 up 穿越点者为 up 端。返回 (start=up, end=down)：调用方 IsFlowingAToB 决定流向标记，
            // 河形由对称 bell 决定不受交换影响。
            var upEnd = Vector2.Distance(endA, upCrossing.Value) <= Vector2.Distance(endB, upCrossing.Value) ? endA : endB;
            var downEnd = upEnd == endA ? endB : endA;

            // 记录本图穿越点（键 = 对端 tile id，幂等；供后生成邻图读取）。
            var comp = SeamlessRiverCrossings.Ensure(map);
            comp.crossings[neighbors[downEdge].tileId] = downCrossing.Value;
            comp.crossings[neighbors[upEdge].tileId] = upCrossing.Value;

            // 登记弦上下文（端点 + 穿越点弦参），供 GetDisplacedPoint Postfix 做缝附近蜿蜒归零。
            if (chordsTile != worldTile) { chordsTile = worldTile; riverChords.Clear(); }
            Patch_TileMutatorWorker_River_GetDisplacedPoint.loggedNodes.Clear(); // verbose 日志随图重置
            var chord = upEnd - downEnd;
            var chordLenSqr = chord.sqrMagnitude;
            float CrossT(Vector2 x) => Vector2.Dot(x - downEnd, chord) / chordLenSqr;
            riverChords.Add((downEnd, upEnd, CrossT(downCrossing.Value), CrossT(upCrossing.Value)));

            __result = (new Vector3(upEnd.x, 0f, upEnd.y), new Vector3(downEnd.x, 0f, downEnd.y));
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] River GetMapEdgeNodes: SEAM-ANCHORED tile={worldTile} " +
                            $"downEdge={downEdge} crossing=({downCrossing.Value.x:F1},{downCrossing.Value.y:F1}) src={downSource} " +
                            $"upEdge={upEdge} crossing=({upCrossing.Value.x:F1},{upCrossing.Value.y:F1}) src={upSource} " +
                            $"endpoints=({downEnd.x:F0},{downEnd.y:F0})/({upEnd.x:F0},{upEnd.y:F0}) start=up end=down.");
            return false; // 跳过原版（不再用随机直线交点）
        }

        /// <summary>
        /// 解析一条共享边的穿越点（本图坐标），三级来源（见类注释）。
        /// 返回 null = 无法解析（调用方放行原版）。
        /// </summary>
        private static Vector2? ResolveCrossing(Map map, int worldTile, int mapSize, List<Vector2> verts,
            int edgeIdx, int neighborTileId, out string source)
        {
            // ③ 兜底：对称哈希（两侧同值）。
            var hashCrossing = SeamlessPolygonGeometry.SeamCrossingPoint(worldTile, edgeIdx, mapSize,
                SeamlessPolygonGeometry.SeamLink.River);
            source = "hash";

            // 找对侧图（数据查询：直接遍历 Find.Maps 按 worldTile 匹配，绕过 SeamlessTileGraph
            // 的休眠过滤——休眠图的 riverGraph/crossings 组件照常可读，图量 ≤37）。
            Map neighborMap = null;
            foreach (var m in Find.Maps)
            {
                if (m == map) continue;
                if (SeamlessTileRegistry.GetMapWorldTile(m) == neighborTileId) { neighborMap = m; break; }
            }
            if (neighborMap == null) return hashCrossing; // ③ 对侧未生成

            // offset：newLocal + offset = sourceLocal（source = 本图，new = 邻图；纯几何，无表依赖）。
            var offsetV2 = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTileId, map, neighborMap);
            var offset = new Vector2(offsetV2.x, offsetV2.z);

            // ① 邻图记录。
            var rec = neighborMap.GetComponent<SeamlessRiverCrossings>();
            if (rec != null && rec.crossings.TryGetValue(worldTile, out var recorded))
            {
                source = "neighbor-record";
                return recorded + offset;
            }

            // ② 邻图 riverGraph 直线求交（对侧 fallback vanilla 时跟随其实际穿越位置）。
            var graph = neighborMap.waterInfo?.riverGraph;
            if (graph != null && graph.Count > 0)
            {
                var v0 = verts[edgeIdx];
                var v1 = verts[(edgeIdx + 1) % verts.Count];
                Vector2? best = null;
                var bestDist = float.MaxValue;
                foreach (var node in graph)
                {
                    var a = new Vector2(node.start.x, node.start.z) + offset;
                    var b = new Vector2(node.end.x, node.end.z) + offset;
                    if (SegmentLineIntersection(v0, v1, a, b, out var hit))
                    {
                        var d = Vector2.Distance(hit, hashCrossing);
                        if (d < bestDist) { bestDist = d; best = hit; }
                    }
                }
                if (best != null)
                {
                    source = "neighbor-intersect";
                    return best;
                }
                // 对侧河与共享边无交（斜出别边）→ 回落哈希（该缝不齐，不劣于现状）。
            }
            return hashCrossing;
        }

        /// <summary>线段 v0→v1 与直线 a→b 求交；返回交点且须落在线段内（t∈[0,1]）。</summary>
        private static bool SegmentLineIntersection(Vector2 v0, Vector2 v1, Vector2 a, Vector2 b, out Vector2 hit)
        {
            hit = default;
            var seg = v1 - v0;
            var dir = b - a;
            var denom = seg.x * dir.y - seg.y * dir.x;
            if (Mathf.Abs(denom) < 1e-9f) return false; // 平行
            var diff = a - v0;
            var t = (diff.x * dir.y - diff.y * dir.x) / denom; // 沿线段参数
            if (t < 0f || t > 1f) return false;
            hit = v0 + seg * t;
            return true;
        }

        /// <summary>直线（起点 p、方向 d）延伸到方形 rect（±<see cref="RectMargin"/>）边界的两个交点。</summary>
        private static (Vector2 a, Vector2 b) ExtendToRect(Vector2 p, Vector2 d, int mapSize)
        {
            var lo = -RectMargin;
            var hi = mapSize + RectMargin;
            var tMin = float.MinValue;
            var tMax = float.MaxValue;

            // slab 法：对 x/z 两轴求参数区间（d 分量为 0 时该轴必须在界内）。
            // t 按未归一化 d 参数化（p + d·t）——返回必须同单位（p + d·tMin/tMax），
            // 勿混用 d.normalized（首版单位混用致端点塌缩到穿越点 ±1 格、弦长 ~2 格，
            // 河形态全坏，2026-08-31 日志定案）。
            Slab(p.x, d.x, lo, hi, ref tMin, ref tMax);
            Slab(p.y, d.y, lo, hi, ref tMin, ref tMax);

            return (p + d * tMin, p + d * tMax);

            void Slab(float o, float dd, float l, float h, ref float t0, ref float t1)
            {
                if (Mathf.Abs(dd) < 1e-9f) return; // 平行轴：p 已在界内（p 是图内穿越点）
                var ta = (l - o) / dd;
                var tb = (h - o) / dd;
                if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
            }
        }
    }

    /// <summary>
    /// 河流 void 跳过 + 修改格记录（2026-08-31 v3 抽象的落地）：
    /// Prefix 挂原版两个 private 地形判定方法——<c>RiverTerrainAt</c>（水）/ <c>RiverBankTerrainAt</c>
    /// （河岸）——cell 属将来 void 区（<see cref="SeamlessPolygonGeometry.IsVoidCell"/>，与 389 同口径、
    /// 进程缓存）时返回 null：**河生成不进 void 区**。效果：389 清 void 前捕获的外条带快照
    /// 不再携带河水/岸，根治"旧图延伸河经快照复刻盖掉新图地面"（v2 大外延教训）。
    ///
    /// Postfix 同两方法：返回非 null 时把 cell 记入 <see cref="SeamlessRiverCells"/>（实际修改格），
    /// 供 SeamOverride 混合跳过（跨地块结构不可被混合侵犯）。记录口径含"返回非 null 但被
    /// edifice 挡住未实际 SetTerrain 的岸格"——略宽，保护更保守，无害。
    ///
    /// 守卫：worldTile &lt; 0（异常态/MapPreview 预览图）放行原版且不记录。
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_River), "RiverTerrainAt")]
    static class Patch_TileMutatorWorker_River_TerrainVoidSkip
    {
        internal static bool Prefix(TileMutatorWorker_River __instance, IntVec3 cell, Map map, ref TerrainDef __result)
        {
            if (!IsFutureVoidCell(map, cell)) return true;
            __result = null;
            return false; // void 格不铺水
        }

        internal static void Postfix(IntVec3 cell, Map map, TerrainDef __result)
        {
            if (__result != null) SeamlessRiverCells.Ensure(map).cells.Add(cell);
        }

        internal static bool IsFutureVoidCell(Map map, IntVec3 cell)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return false;
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x);
            return SeamlessPolygonGeometry.IsVoidCell(band, worldTile, map.Size.x, cell);
        }
    }

    [HarmonyPatch(typeof(TileMutatorWorker_River), "RiverBankTerrainAt")]
    static class Patch_TileMutatorWorker_River_BankVoidSkip
    {
        internal static bool Prefix(IntVec3 cell, Map map, ref TerrainDef __result)
        {
            if (!Patch_TileMutatorWorker_River_TerrainVoidSkip.IsFutureVoidCell(map, cell)) return true;
            __result = null;
            return false; // void 格不铺岸
        }

        internal static void Postfix(IntVec3 cell, Map map, TerrainDef __result)
        {
            if (__result != null) SeamlessRiverCells.Ensure(map).cells.Add(cell);
        }
    }

    /// <summary>
    /// 缝附近蜿蜒归零窗口（2026-08-31 v3 补丁，修"穿越点对齐但曲线没贴着穿越点走"）：
    /// v3 端点延伸到方形边界后，穿越点位于弦参 t≈0.1-0.2，而原版 Perlin 蜿蜒的 bell 包络在该处
    /// 已达 0.36-0.64（峰值 1 在中点）——河道在缝附近被蜿蜒推离弦线十几格，且两侧图的噪声种子
    /// 各自随机（Rand.Int），推的方向/幅度不同 → 缝两侧水带各偏各的（实测 tAvg 差数格）。
    ///
    /// 【做法】不掰方向：中线 = 弦点 + 蜿蜒位移×W(t)。弦点由 node 端点算出（s+t·d），
    /// 蜿蜒位移 = 原结果 − 弦点；W 在每个穿越点处为 0、向图内 <see cref="BendFreeCells"/> 格
    /// smootherstep（C2）升到 1。效果：缝处曲线精确过穿越点、切向=弦向（两侧弦向差 = 河链
    /// 本身的折角，与世界地图一致）；图中部蜿蜒原样保留。与 v2"垂直接近段"的本质区别：
    /// 本 patch 不注入任何目标方向，只是把随机蜿蜒从缝边衰减掉。
    ///
    /// 匹配：node 端点与登记弦上下文比对（容忍调用方 IsFlowingAToB 交换 start/end，t 取 1−t）；
    /// 无匹配（fallback vanilla / 未锚定端）不处理。生成期单线程安全（预览图 fallback 不登记）。
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_River), "GetDisplacedPoint")]
    static class Patch_TileMutatorWorker_River_GetDisplacedPoint
    {
        /// <summary>穿越点两侧的蜿蜒归零区长度（格，沿弦）。覆盖接缝带 3 圈 + 过渡余量。</summary>
        private const float BendFreeCells = 25f;

        /// <summary>verbose 日志去重：每 node 只记一次（避免深度场逐格刷屏）。worldTile 变更时清。</summary>
        internal static readonly HashSet<RiverNode> loggedNodes = new();

        internal static void Postfix(RiverNode riverNode, float t, ref Vector2 __result)
        {
            if (Patch_TileMutatorWorker_River_GetMapEdgeNodes.riverChords.Count == 0) return;

            var s = new Vector2(riverNode.start.x, riverNode.start.z);
            var e = new Vector2(riverNode.end.x, riverNode.end.z);
            var d = e - s;
            var lenSqr = d.sqrMagnitude;
            if (lenSqr < 1e-6f) return;

            // 找本 node 的弦上下文（端点匹配，容忍交换）。
            float tA = 0f, tB = 0f;
            bool swapped = false;
            var found = false;
            foreach (var c in Patch_TileMutatorWorker_River_GetMapEdgeNodes.riverChords)
            {
                if (Approx(c.s, s) && Approx(c.e, e)) { tA = c.tA; tB = c.tB; found = true; break; }
                if (Approx(c.s, e) && Approx(c.e, s)) { tA = 1f - c.tA; tB = 1f - c.tB; swapped = true; found = true; break; }
            }
            if (!found) return;
            if (swapped) t = 1f - t;

            // 蜿蜒位移 = 原结果 − 弦点；W 按到最近穿越点的沿弦距离衰减。
            var chordPoint = s + d * t;
            var bend = __result - chordPoint;
            var len = Mathf.Sqrt(lenSqr);
            var distCells = Mathf.Min(Mathf.Abs(t - tA), Mathf.Abs(t - tB)) * len;
            var w = SmootherStep(distCells / BendFreeCells);
            __result = chordPoint + bend * w;

            if (RimExodusMod.Settings?.verboseLogging ?? false && loggedNodes.Add(riverNode))
                Log.Message($"[RimExodus] River bend-free window: node width={riverNode.width:F0} " +
                            $"tA={tA:F3} tB={tB:F3} len={len:F0} first-t={t:F3} w={w:F2} bendMag={bend.magnitude:F1}");
        }

        private static bool Approx(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 1f;

        private static float SmootherStep(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * x * (x * (6f * x - 15f) + 10f);
        }
    }

    /// <summary>
    /// RiverIsland 自己 override 了 GetDisplacedPoint，基类 patch 不会覆盖该虚方法实现。
    /// 复用同一接缝弯曲修正，保持岛屿河流与普通河流一致。
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_RiverIsland), "GetDisplacedPoint")]
    static class Patch_TileMutatorWorker_RiverIsland_GetDisplacedPoint
    {
        internal static void Postfix(RiverNode riverNode, float t, ref Vector2 __result)
        {
            Patch_TileMutatorWorker_River_GetDisplacedPoint.Postfix(riverNode, t, ref __result);
        }
    }
}
