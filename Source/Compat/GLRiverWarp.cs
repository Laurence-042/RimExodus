using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// GL 河流接缝 warp v9：Path 树钉位（patch <see cref="PathTracer"/> 本体，v8 函数场路线的替代）。
    ///
    /// 【v8 失败机理（2026-09-04 四轮实测定案，勿回退）】v8 在 Named 函数层包装平移采样代理 +
    /// 格网粒度 eff 位移场：eff 离散查表在位移跳变处（BFS 域混合边界/膨胀域边缘）使采样位跳变
    /// → 河沿轴向等跳变线裂开（实测"水平/竖直切割"）；同一条河两端 target 不同时中段加权平均
    /// 把河拧碎（map2 shift=+56/+14 混合）。离散场缺陷结构性修不掉。
    ///
    /// 【v9 机制（真正 patch river path 本体）】TerrainGraph 的河 = PathTracer.Trace(Path) 从
    /// **Path 树**（Segment 列表，树/图）逐段 trace，每段起点 = 父帧.pos + s.RelPosition（Root
    /// 段相对 trace 原点，即 RelPosition 就是 map 域进场锚）、出场方向由段的 TraceParams.Target
    /// 驱动 A*；trace 逐帧把河烘焙进 MainGrid/DistanceGrid/SideGrid/ValueGrid/OffsetGrid——
    /// **改树即改一切**：全部派生 grid 从新河形生成，Named 输出原样传递，biome（Soil 带）/岸
    /// （Riverbank 带）/水/海拔（河谷）/洞穴全层自动一致，旧河道根本不生成。
    ///
    /// patch Trace Prefix（手动绑定，GL 在场；object 参数 + 反射遍历，零 emit 零接口实现）：
    /// ①Root 段钉位——锚（RelPosition）在某条 river-link 边的进场带内 → RelPosition += 边哈希
    /// target − 锚（进场口精确钉到两侧一致的 <see cref="SeamlessPolygonGeometry.SeamCrossingPoint"/>）；
    /// ②Target 段钉位——只处理 Path 树的叶段；叶段 TraceParams.Target 在某条 river-link 边带内
    /// （带宽放宽到 60，覆盖
    /// Confluence 支流 margin 0.2≈50 格的端点）→ 沿哈希 target 所在边法线推到六边形外。
    /// A* 允许在 StepSize 半径内提前结束；如果 Target 正好放在缝线上，河心可在边内停下，
    /// 即使再算 leaf marginHead 也不保证 MainGrid 真正跨过边线。外推距离 = StepSize +
    /// 2·TraceInnerMargin + 1，确保河心伸出边界；沿边坐标仍是同一哈希 target，
    /// 因此两侧缝上对齐（含混合管线）。中间河形由 GL
    /// 自己的 A* 连续寻路——无离散场、无切割、无混合平均。
    ///
    /// 只钉叶段是 TerrainGraph 的结构性约束：若把同一出场链上多个携带 Target 的中间段
    /// 也压到同一缝点，父段结束与子段目标会重合；PathFinder 此时只返回起点一个节点，
    /// PathTracer.TryTrace 却固定读第 2 个节点，导致 List 越界并中止整张地图生成。
    ///
    /// 【幂等性（天然）】重跑时锚已在 target → 增量 0：Path 实例复用安全；collision retry 在
    /// Trace 内部不重进 Prefix；唯一残余 = 复用树被 collision handler 调整后再钉会撤销调整
    /// （无害，接受）。TraceParams 是 struct，Target 改动须装箱读-改-写回。
    ///
    /// 【门控】MapPreviewAPI.IsGeneratingPreview 跳过（预览原样）；无 river-link 边带命中 →
    /// 不钉（世界数据权威性）；全程 try/catch 安全失败 = 不钉（原 GL 行为，勿杀 Trace）。
    /// </summary>
    internal static class GLRiverWarp
    {
        /// <summary>Root 进场带深度（格）：锚须在此带内才认为是该边的进场段（河源图中央 Root 天然出局）。</summary>
        private const float RootBandDepth = 40f;

        /// <summary>出场/支流端带深度（格）：放宽到 60 覆盖 Confluence 支流 margin 0.2≈50 格的 Target 端点。</summary>
        private const float TargetBandDepth = 60f;

        /// <summary>带内横向余量（格）：锚/Target 的 lat 允许略超边端（多边形顶点投影误差）。</summary>
        private const float BandLatMargin = 12f;

        // ===== 反射缓存（懒解析；失败置 _disabled，后续零介入）=====
        private static bool _resolved;
        private static bool _disabled;
        private static bool _warnedFailure;

        private static PropertyInfo _segmentsProp;        // Path.Segments (IReadOnlyList<Segment>)
        private static FieldInfo _relPositionField;       // Segment.RelPosition (Vector2d)
        private static PropertyInfo _parentIdsProp;       // Segment.ParentIds (IReadOnlyList<int>)
        private static PropertyInfo _branchIdsProp;       // Segment.BranchIds (IReadOnlyList<int>)
        private static FieldInfo _traceParamsField;       // Segment.TraceParams (struct)
        private static FieldInfo _targetField;            // TraceParams.Target (Vector2d?)
        private static FieldInfo _stepSizeField;          // TraceParams.StepSize (double)
        private static PropertyInfo _generatingTileProp;  // Landform.GeneratingTile (static)
        private static PropertyInfo _generatingMapSizeProp; // Landform.GeneratingMapSize (static IntVec2)
        private static PropertyInfo _isGeneratingPreviewProp; // MapPreview.MapPreviewAPI.IsGeneratingPreview

        /// <summary>Vector2d 构造器 (double, double)——RelPosition/Target 装箱值重建用。</summary>
        private static ConstructorInfo _vec2dCtor;

        // ===== 量化诊断（Trace Postfix：实测缝线河带中心 vs 钉位 target 的偏差）=====
        private static FieldInfo _mainGridField;       // PathTracer._mainGrid (internal double[,])
        private static FieldInfo _gridMarginField;     // PathTracer.GridMargin (public readonly Vector2d)
        private static FieldInfo _traceInnerMarginField; // PathTracer.TraceInnerMargin (public readonly double)
        private static bool _warnedCheckFailure;

        // ===== 边带缓存（单槽；生成串行）=====
        private static List<EdgeBand> _edges;
        private static int _edgesTile = -1;
        private static int _edgesSize = -1;

        private struct EdgeBand
        {
            public Vector2 V0;        // 边起点（map 域）
            public Vector2 Dir;       // 沿边单位向量
            public Vector2 Inward;    // 内法线
            public float Len;
            public Vector2 Target;    // 缝上哈希穿越点（map 域）
            public int EdgeIdx;
            public int NeighborTile;
        }

        /// <summary>
        /// PathTracer.Trace Prefix 入口（由 SeamlessGLRiverCompat.Patch_PathTracerTrace 转发）。
        /// 在 trace 开始前钉位 Path 树——之后河形/全部 grid/全部消费层自动一致。
        /// </summary>
        internal static void OnTracePrefix(object tracer, object path)
        {
            try
            {
                if (path == null || !Resolve()) return;
                if ((bool)_isGeneratingPreviewProp.GetValue(null)) return; // 预览原样（高频不打日志）

                var tileId = CurrentTileId();
                if (tileId < 0 || Find.World?.grid == null) return;

                var mapSizeVec = (IntVec2)_generatingMapSizeProp.GetValue(null);
                if (mapSizeVec.x != mapSizeVec.z || mapSizeVec.x < 16) return;
                var mapSize = mapSizeVec.x;

                var edges = EnsureEdges(tileId, mapSize);
                if (edges == null || edges.Count == 0) return; // 无 river-link 边：零介入

                ShiftPathTree(tracer, path, edges, tileId);
            }
            catch (Exception ex)
            {
                if (!_warnedFailure)
                {
                    _warnedFailure = true;
                    var stackHead = ex.StackTrace == null ? "" : " @ " + ex.StackTrace.Split('\n')[0];
                    Log.Warning($"[RimExodus] GL river warp: Trace prefix failed once (GL path kept as-is): {ex.GetType().Name}: {ex.Message}{stackHead}");
                }
            }
        }

        /// <summary>对一棵 Path 树做 Root/Target 钉位。天然幂等（重钉增量恒 0）。</summary>
        private static void ShiftPathTree(object tracer, object path, List<EdgeBand> edges, int tileId)
        {
            var segments = _segmentsProp.GetValue(path) as System.Collections.IEnumerable;
            if (segments == null) return;

            var sb = new StringBuilder();
            var pinnedRoots = 0;
            var pinnedTargets = 0;
            var segmentCount = 0;
            var diagEnabled = RimExodusLog.Enabled(RimExodusLogModule.Compat);
            foreach (var seg in segments)
            {
                segmentCount++;
                // Root 段：进场锚钉位（RelPosition 即 map 域进场位置——相对 originFrame，无换算）。
                var parentIds = _parentIdsProp.GetValue(seg) as System.Collections.IEnumerable;
                var isRoot = true;
                if (parentIds != null)
                    foreach (var _ in parentIds) { isRoot = false; break; }

                if (isRoot)
                {
                    var anchorBoxed = _relPositionField.GetValue(seg);
                    if (anchorBoxed == null) continue;
                    var anchor = ReadVec2d(anchorBoxed);
                    if (TryFindBand(edges, anchor, RootBandDepth, out var band, out var d, out var l))
                    {
                        // lat-only 钉位（2026-09-04 诊断定案）：进场段宽度沿程渐增（ramp），GL 把
                        // 进场锚放图外 40+ 格就是让 ramp 在图外完成、满宽进图——直接钉到缝上会把
                        // 零宽起点搬到缝线（实测四张 River 图进场端全部 NO-CROSS），两岸各自在缝内
                        // 几格才出现成形河 + 进场角度不同 = "错开一整个宽度"。改为只对齐沿边分量
                        // （lat），深度分量保留原值（图外）——ramp 照常在图外完成，河满宽穿过缝线
                        // 于对齐的 lat（map5 Confluence Root 满宽进场实测 d=0 的模式推广）。
                        var targetLat = Vector2.Dot(band.Target - band.V0, band.Dir);
                        var pinned = band.V0 + band.Dir * targetLat + band.Inward * d;
                        _relPositionField.SetValue(seg, MakeVec2d(pinned.x, pinned.y));
                        pinnedRoots++;
                        sb.Append($"root({anchor.x:F0},{anchor.y:F0})->e{band.EdgeIdx}lat={targetLat:F0} keepD={d:F0};");
                    }
                    else if (diagEnabled)
                    {
                        sb.Append($"root({anchor.x:F0},{anchor.y:F0}) NOMATCH");
                        DescribeNearestEdge(edges, anchor, sb);
                    }
                    continue;
                }

                // 出场/支流端：只钉叶段的 Target。ExtendWithParams 可以让同一链上的
                // 多个段保留 Target；全部钉到同一缝点会把相邻段压成零距离，触发
                // TerrainGraph PathTracer 对单节点 A* 路径的 List 越界。
                var branchIds = _branchIdsProp.GetValue(seg) as System.Collections.IEnumerable;
                if (branchIds != null)
                {
                    var isLeaf = true;
                    foreach (var _ in branchIds) { isLeaf = false; break; }
                    if (!isLeaf) continue;
                }

                var tpBoxed = _traceParamsField.GetValue(seg);
                if (tpBoxed == null) continue;
                var targetBoxed = _targetField.GetValue(tpBoxed);
                if (targetBoxed == null) continue; // 无 Target 的段：跟随树平移（刚体）

                var t = ReadVec2d(targetBoxed);
                if (TryFindBand(edges, t, TargetBandDepth, out var tBand, out var td, out var tl))
                {
                    // 出场端不能只放在缝线上：PathFinder 可在 StepSize 半径内提前接受，
                    // 2026-09-09 实测 tile 20771 e1 因此 NO-CROSS。沿外法线外推，但不改
                    // target lat；这样两图仍在同一哈希穿越点相交，且河带确实延伸出多边形。
                    var stepSize = Mathf.Max(1f, (float)(double)_stepSizeField.GetValue(tpBoxed));
                    var traceInnerMargin = tracer != null
                        ? Mathf.Max(0f, (float)(double)_traceInnerMarginField.GetValue(tracer))
                        : 3f;
                    var outwardLead = stepSize + 2f * traceInnerMargin + 1f;
                    var pinnedTarget = tBand.Target - tBand.Inward * outwardLead;
                    _targetField.SetValue(tpBoxed, MakeVec2d(pinnedTarget.x, pinnedTarget.y));
                    _traceParamsField.SetValue(seg, tpBoxed);
                    pinnedTargets++;
                    sb.Append($"tgt({t.x:F0},{t.y:F0})->e{tBand.EdgeIdx}t({tBand.Target.x:F0},{tBand.Target.y:F0})out={outwardLead:F0};");
                }
                else if (diagEnabled)
                {
                    sb.Append($"tgt({t.x:F0},{t.y:F0}) NOMATCH");
                    DescribeNearestEdge(edges, t, sb);
                }
            }

            if (diagEnabled)
                RimExodusLog.Message(RimExodusLogModule.Compat,
                    $"GL river warp pin: tile={tileId} segs={segmentCount} roots={pinnedRoots} targets={pinnedTargets} {sb}");
        }

        /// <summary>NOMATCH 诊断：附最近边的深度/lat 与候选边数，定位带判定失败原因。</summary>
        private static void DescribeNearestEdge(List<EdgeBand> edges, Vector2 p, StringBuilder sb)
        {
            var bestAbs = float.MaxValue;
            var bestIdx = -1;
            var bestDepth = 0f;
            var bestLat = 0f;
            foreach (var e in edges)
            {
                var local = p - e.V0;
                var depth = Vector2.Dot(local, e.Inward);
                var abs = Mathf.Abs(depth);
                if (abs >= bestAbs) continue;
                bestAbs = abs;
                bestIdx = e.EdgeIdx;
                bestDepth = depth;
                bestLat = Vector2.Dot(local, e.Dir);
            }
            sb.Append(bestIdx < 0
                ? "(no-edges);"
                : $"(near e{bestIdx} d={bestDepth:F0} lat={bestLat:F0});");
        }

        /// <summary>点是否落在某条 river-link 边的深度带内。深度下界放宽到 −130：GL 的进场锚/
        /// Target 按**方形边缘外**的 polarRect margin 放置（图外 20~50 格，实测 z=-25/z=300/x=293）、
        /// 不知道六边形——相对六边形边深度最深实测 −87（六边形内切 17~53 + 图外量），旧下界
        /// −4/−60 相继拒绝过角区锚与出场目标（"一端生效一端没有"的根因）。归属安全由 lat 带宽
        /// + |depth| 最小保证（对面边深度差 150+ 格，结构性不串）。</summary>
        private static bool TryFindBand(List<EdgeBand> edges, Vector2 p, float bandDepth, out EdgeBand found, out float bestDepth, out float bestLat)
        {
            found = default;
            bestDepth = float.MaxValue;
            bestLat = 0f;
            var hit = false;
            var bestAbs = float.MaxValue;
            for (var i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                var local = p - e.V0;
                var depth = Vector2.Dot(local, e.Inward);
                if (depth < -130f || depth > bandDepth) continue;
                var lat = Vector2.Dot(local, e.Dir);
                if (lat < -BandLatMargin || lat > e.Len + BandLatMargin) continue;
                var abs = Mathf.Abs(depth);
                if (abs < bestAbs)
                {
                    bestAbs = abs;
                    bestDepth = depth;
                    bestLat = lat;
                    found = e;
                    hit = true;
                }
            }
            return hit;
        }

        /// <summary>
        /// 量化诊断（Trace Postfix）：读本次 trace 烘焙的 MainGrid（河宽度场），在每条 river-link
        /// 边的缝线上实测河带中心，与钉位 target 对比输出 delta——验证"起止点应该精确但实测不
        /// 精确"的机制性偏差源（进场端 RelShift 横移 / 出场端 marginHead 过冲 + A* 接受半径）。
        /// </summary>
        internal static void OnTracePostfix(object tracer)
        {
            try
            {
                if (tracer == null || _disabled || _mainGridField == null || _gridMarginField == null) return;
                if (_edges == null || _edges.Count == 0) return;

                var grid = _mainGridField.GetValue(tracer) as double[,];
                var marginBoxed = _gridMarginField.GetValue(tracer);
                if (grid == null || marginBoxed == null) return;
                var margin = ReadVec2d(marginBoxed);
                var maxX = grid.GetLength(0);
                var maxZ = grid.GetLength(1);

                var tileId = _edgesTile;
                var sb = new StringBuilder();
                for (var i = 0; i < _edges.Count; i++)
                {
                    var e = _edges[i];
                    var targetLat = Vector2.Dot(e.Target - e.V0, e.Dir);

                    // 缝线扫描（inner + GridMargin → grid 坐标），MainGrid>0 的连续段取最宽段中心。
                    var inSeg = false;
                    var lo = 0f;
                    var hi = 0f;
                    var bestLo = 0f;
                    var bestHi = -1f;
                    for (var l = 0f; l <= e.Len; l += 1f)
                    {
                        var p = e.V0 + e.Dir * l + new Vector2(margin.x, margin.y);
                        var gx = (int)p.x;
                        var gz = (int)p.y;
                        var hit = gx >= 0 && gz >= 0 && gx < maxX && gz < maxZ && grid[gx, gz] > 0.0;
                        if (hit)
                        {
                            if (!inSeg) { inSeg = true; lo = l; }
                            hi = l;
                        }
                        else if (inSeg)
                        {
                            inSeg = false;
                            if (hi - lo > bestHi - bestLo) { bestLo = lo; bestHi = hi; }
                        }
                    }
                    if (inSeg && hi - lo > bestHi - bestLo) { bestLo = lo; bestHi = hi; }

                    if (bestHi < bestLo)
                    {
                        sb.Append($"e{e.EdgeIdx}->t{e.NeighborTile}:NO-CROSS;");
                    }
                    else
                    {
                        var measuredLat = (bestLo + bestHi) * 0.5f;
                        sb.Append($"e{e.EdgeIdx}->t{e.NeighborTile}:d={measuredLat - targetLat:+0;-0;0} span={bestHi - bestLo:F0};");
                    }
                }

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    RimExodusLog.Message(RimExodusLogModule.Compat,
                        $"GL river warp check: tile={tileId} {sb}");
            }
            catch (Exception ex)
            {
                if (!_warnedCheckFailure)
                {
                    _warnedCheckFailure = true;
                    var stackHead = ex.StackTrace == null ? "" : " @ " + ex.StackTrace.Split('\n')[0];
                    Log.Warning($"[RimExodus] GL river warp: check postfix failed once: {ex.GetType().Name}: {ex.Message}{stackHead}");
                }
            }
        }

        /// <summary>本图全部 river-link 边带（含缝上哈希 target）；缓存 key=(tile,size)。无河图返回空表。</summary>
        private static List<EdgeBand> EnsureEdges(int tileId, int mapSize)
        {
            if (_edges != null && _edgesTile == tileId && _edgesSize == mapSize) return _edges;

            var result = new List<EdgeBand>();
            try
            {
                var neighbors = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(tileId, neighbors);
                var verts = SeamlessPolygonGeometry.BuildPolygonVertices(tileId, mapSize);
                if (verts.Count >= 3)
                {
                    for (var i = 0; i < neighbors.Count && i < verts.Count; i++)
                    {
                        if (Find.WorldGrid.GetRiverDef(tileId, neighbors[i]) == null) continue;

                        var v0 = verts[i];
                        var v1 = verts[(i + 1) % verts.Count];
                        var edgeVec = v1 - v0;
                        var edgeLen = edgeVec.magnitude;
                        if (edgeLen < 4f) continue;
                        var target = SeamlessPolygonGeometry.SeamCrossingPoint(tileId, i, mapSize,
                            SeamlessPolygonGeometry.SeamLink.River);
                        result.Add(new EdgeBand
                        {
                            V0 = v0,
                            Dir = edgeVec / edgeLen,
                            Inward = -SeamlessPolygonGeometry.EdgeOutwardNormal(verts, i),
                            Len = edgeLen,
                            Target = target,
                            EdgeIdx = i,
                            NeighborTile = neighbors[i].tileId,
                        });
                    }
                }
            }
            catch
            {
                result = new List<EdgeBand>(); // 构建失败 = 零介入（缓存空判定）
            }
            if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                RimExodusLog.Message(RimExodusLogModule.Compat,
                    result.Count == 0
                        ? $"GL river warp edges: tile={tileId} size={mapSize} NONE (no river-link edge or build failed) — pinning disabled for this tile."
                        : $"GL river warp edges: tile={tileId} size={mapSize} [" + string.Join(", ", result.ConvertAll(e => $"e{e.EdgeIdx}->t{e.NeighborTile} tgt=({e.Target.x:F0},{e.Target.y:F0})")) + "]");
            _edges = result;
            _edgesTile = tileId;
            _edgesSize = mapSize;
            return result;
        }

        private static Vector2 ReadVec2d(object boxed)
        {
            // Vector2d 的 x/z 是 public double 字段（TerrainGraph.Util，闭源 DLL 反射读）。
            return new Vector2(
                (float)(double)AccessTools.Field(boxed.GetType(), "x").GetValue(boxed),
                (float)(double)AccessTools.Field(boxed.GetType(), "z").GetValue(boxed));
        }

        private static object MakeVec2d(float x, float z)
        {
            return _vec2dCtor.Invoke(new object[] { (double)x, (double)z });
        }

        private static int CurrentTileId()
        {
            var tile = _generatingTileProp.GetValue(null);
            if (tile == null) return -1;
            try
            {
                // WorldTileInfo.TileId 是 internal readonly 字段（非属性——2026-09-04 实测
                // GetProperty 恒 null → tileId=-1 全跳过的教训）；字段优先、属性兜底。
                var type = tile.GetType();
                var field = AccessTools.Field(type, "TileId");
                if (field != null) return (int)field.GetValue(tile);
                return (int)(type.GetProperty("TileId")?.GetValue(tile) ?? -1);
            }
            catch
            {
                return -1;
            }
        }

        private static bool Resolve()
        {
            if (_resolved) return !_disabled;
            _resolved = true;
            try
            {
                var pathType = AccessTools.TypeByName("TerrainGraph.Flow.Path");
                var segmentType = AccessTools.TypeByName("TerrainGraph.Flow.Path+Segment");
                var vec2dType = AccessTools.TypeByName("TerrainGraph.Util.Vector2d");
                var traceParamsType = AccessTools.TypeByName("TerrainGraph.Flow.Path+TraceParams");
                var landformType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.Landform");
                var previewApi = AccessTools.TypeByName("MapPreview.MapPreviewAPI");
                var tracerType = AccessTools.TypeByName("TerrainGraph.Flow.PathTracer");
                if (pathType == null || segmentType == null || vec2dType == null || traceParamsType == null
                    || landformType == null || previewApi == null || tracerType == null) { _disabled = true; return false; }

                _segmentsProp = AccessTools.Property(pathType, "Segments");
                _relPositionField = AccessTools.Field(segmentType, "RelPosition");
                _parentIdsProp = AccessTools.Property(segmentType, "ParentIds");
                _branchIdsProp = AccessTools.Property(segmentType, "BranchIds");
                _traceParamsField = AccessTools.Field(segmentType, "TraceParams");
                _targetField = AccessTools.Field(traceParamsType, "Target");
                _stepSizeField = AccessTools.Field(traceParamsType, "StepSize");
                _generatingTileProp = AccessTools.Property(landformType, "GeneratingTile");
                _generatingMapSizeProp = AccessTools.Property(landformType, "GeneratingMapSize");
                _isGeneratingPreviewProp = AccessTools.Property(previewApi, "IsGeneratingPreview");
                _vec2dCtor = AccessTools.Constructor(vec2dType, new[] { typeof(double), typeof(double) });
                // 量化诊断（可选——解析失败只禁用 check，不禁用钉位）。
                _mainGridField = AccessTools.Field(tracerType, "_mainGrid");
                _gridMarginField = AccessTools.Field(tracerType, "GridMargin");
                _traceInnerMarginField = AccessTools.Field(tracerType, "TraceInnerMargin");

                if (_segmentsProp == null || _relPositionField == null || _parentIdsProp == null || _branchIdsProp == null
                    || _traceParamsField == null || _targetField == null || _stepSizeField == null
                    || _traceInnerMarginField == null || _generatingTileProp == null
                    || _generatingMapSizeProp == null || _isGeneratingPreviewProp == null || _vec2dCtor == null)
                {
                    _disabled = true;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _disabled = true;
                Log.Warning($"[RimExodus] GL river warp: reflection init failed (pin disabled, GL rivers stay vanilla): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
