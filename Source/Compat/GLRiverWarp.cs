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
    ///
    /// 【v10（2026-09-17 四轮实测定案）】v9 只钉端点时河仍在缝旁错开 ≤29 格，主因是 A\* 的
    /// 提前接受（TargetAcceptRadius=StepSize + PlanarTargetFallback=3·StepSize 垂直平面接受）——
    /// 它从不精确抵达目标。修法（纯数据 + 终端单点，不与碰撞系统对抗）：
    /// ①FindPath Prefix 对钉位目标收紧接受（两字段都是 PathFinder public double，反射直写），
    /// 最后一档重试（HeuristicDistanceWeight≥9）恢复 vanilla 洪量防 null→梯度跟随降级；
    /// ②v9 钉位时放宽叶段 AngleTenacity（转向预算升到地貌作者自设 AngleLimitAbs 上限内）；
    /// ③FindPath Postfix 终端收口（v10d）：截尾到缝内侧最后节点，接上恰在缝线穿越点上的节点
    /// 与缝外垂直延伸节点——中心线按构造只在穿越点过缝一次；
    /// ④v10f/g 安全阀：钉位 trace 碰撞重试上限钳 12；中段偏差（12,40] 的制造腿须通过**余量
    /// 走廊检查**（沿线圆盘采样，与一切已成形河带保持 CorridorClearance 净空——只查本体时
    /// 腿贴余量带通过仍烧碰撞循环、把无辜兄弟支流 stub 成 NO-CROSS，北支路死亡链）+ 锚点
    /// 回溯（node_k 向下最多 6 个找净空直线）；全被占则放弃收口保留 GL 路线。
    /// 【到此为止（用户定夺 2026-09-17）】合流/分流地貌"分支点贴边无扭转空间"是结构性事实，
    /// 走廊被同图河带阻挡的支流保留 GL 原生走向；实测个别支流（v10g 后的北支路）仍被 GL
    /// 碰撞处理器截断、在合流图一侧整体缺失——剩余碰撞来自 GL 原生拓扑与钉位目标的冲突，
    /// 非我方制造腿，已接受为已知边界。继续深入 node 操作等于重写 GL 河流生成，不做。
    /// 【v10a 全路径节点位移——已撤销，勿回退】曾对 A* 结果整表 smoothstep 位移（末端误差前馈
    /// 抵消）。实测死因：烘焙期碰撞系统（value/offset 差检测河带压叠）与碰撞处理器的避让调整
    /// （divert/simplify/…/stub 阶梯）互搏——位移每轮把叶子河带拉回穿越点方向、恰好撤销处理器
    /// 的避让，误差逐轮放大（26→33→41→49）直至 stub 截断、河流整体消失（registered=0）。
    /// 教训：**不得整体位移 A\* 结果**——结果的合法性（避让/碰撞/成本）只在 A\* 决策内成立；
    /// 终端单点收口（v10d）是唯一允许的事后修改。亦勿用 GridValueSupplier 委托包装（Vector2d
    /// 无编译引用需 emit）。
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

        // ===== v10：FindPath Prefix 接受收紧（可选解析——失败只禁收紧，不影响 v9 钉位）=====
        private static bool _acceptTightenDisabled;
        private static bool _warnedTightenFailure;
        private static FieldInfo _finderTracerField;       // PathFinder._tracer (private readonly PathTracer)
        private static FieldInfo _finderStepField;         // PathFinder.FullStepDistance (public double)
        private static FieldInfo _finderAcceptField;       // PathFinder.TargetAcceptRadius (public double)
        private static FieldInfo _finderPlanarField;       // PathFinder.PlanarTargetFallback (public double)
        private static FieldInfo _finderHeuristicField;    // PathFinder.HeuristicDistanceWeight (public float，重试阶梯档位)
        private static FieldInfo _angleTenacityField;      // TraceParams.AngleTenacity

        // ===== v10d：FindPath Postfix 终端收口（穿越点单点插入；可选解析——失败只禁收口）=====
        private static bool _terminalPinDisabled;
        private static bool _warnedTerminalFailure;
        private static ConstructorInfo _nodeCtor;          // PathFinder.Node(Vector2d, Vector2d, int, int, Node)
        private static FieldInfo _nodePositionField;       // Node.Position (public readonly Vector2d)

        /// <summary>收口节点与相邻节点的最小间距（格）——过近会使烘焙帧推进弦长→0 而停摆。</summary>
        private const float MinNodeSpacing = 2f;

        /// <summary>末端已距穿越点多近时视为已对齐（不收口）。</summary>
        private const float TerminalNoOpDist = 1.2f;

        /// <summary>
        /// 收口的最大许可末端偏差（格）：≤ 此值无条件收口。超过 = A* 以放宽洪量兜底接受的结果。
        /// </summary>
        private const float TerminalPinMaxOffset = 12f;

        /// <summary>
        /// 收口的硬上限（格）：兜底结果偏差 ∈ (12, 40] 时先做走廊检查（见 v10g）——干净则收口
        /// （截掉兜底路线的肇事斜段、精确过缝），脏则放弃（保留 GL 路线）。>40 一律放弃。
        /// </summary>
        private const float TerminalPinCorridorMaxOffset = 40f;

        /// <summary>
        /// 走廊净空余量（格，v10g）：中段偏差收口的制造腿必须与任何已成形河带（MainGrid&gt;0）
        /// 保持至少这一切比雪夫距离——烘焙碰撞检测在河带附近的余量带内就会触发（value/offset
        /// 差），v10f 只查本体时制造腿贴边通过、烧满碰撞循环把无辜的兄弟支流 stub 成 NO-CROSS
        /// （2026-09-17 北支路死亡链）。过严的后果只是少对齐一条支流，无稳定性风险。
        /// </summary>
        private const int CorridorClearance = 12;

        /// <summary>锚点回溯的最大步数（v10g）：从 node_k 向下最多再试 6 个锚点找净空直线。</summary>
        private const int AnchorBacktrackSteps = 6;

        /// <summary>
        /// 钉位 trace 的碰撞重试上限（GL 默认 50）：钉位把出场目标横移数十格后，合流树的碰撞
        /// 循环可能无法收敛（A* 每轮重新瞄准钉位、撤销处理器的避让），50 轮全树重烘焙 = 严重
        /// 卡顿。钳到 12 轮兜底性能；未钉位的 trace 不动。
        /// </summary>
        private const int MaxTraceAttempts = 12;

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
        /// 【v10f】钉位过的 trace 把碰撞重试上限钳到 <see cref="MaxTraceAttempts"/>：钉位横移
        /// 出场目标后合流树的碰撞循环可能不收敛（A* 每轮重新瞄准钉位、撤销处理器避让），
        /// GL 默认 50 轮全树重烘焙 = 严重卡顿（2026-09-17 实测）。
        /// </summary>
        internal static void OnTracePrefix(object tracer, object path, ref int maxAttempts)
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

                var pinned = ShiftPathTree(tracer, path, edges, tileId);
                if (pinned > 0 && maxAttempts > MaxTraceAttempts) maxAttempts = MaxTraceAttempts;
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

        /// <summary>对一棵 Path 树做 Root/Target 钉位。天然幂等（重钉增量恒 0）。返回钉位计数。</summary>
        private static int ShiftPathTree(object tracer, object path, List<EdgeBand> edges, int tileId)
        {
            var segments = _segmentsProp.GetValue(path) as System.Collections.IEnumerable;
            if (segments == null) return 0;

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
                    // 出场端不能只放在缝线上：PathFinder 可在接受半径内提前结束。沿外法线外推，
                    // 但不改 target lat；这样两图仍在同一哈希穿越点相交，且河心确实越过缝线。
                    // v10c 外推量从 16 格（step+2·margin+1）缩到 接受半径+3（≈5.5 格）：
                    // ①斜向接近的过缝横向偏移 ∝ 外推量·tan(接近角)（实测 d=-13 = 16·tan39°），
                    // 缩短直接减三倍；②旧 NO-CROSS（tile 20771）的根因是 planar 提前接受 15 格，
                    // v10b 已清零——河心停在缝外 ≥3 格 + 河半宽 ~10，MainGrid 在缝线上必然 >0。
                    // 与 OnFindPathPrefix 的 lead 匹配式必须同步（gate 靠它对上 targetPos）。
                    var stepSize = Mathf.Max(1f, (float)(double)_stepSizeField.GetValue(tpBoxed));
                    var outwardLead = Mathf.Max(4f, stepSize * 0.5f) + 3f;
                    var pinnedTarget = tBand.Target - tBand.Inward * outwardLead;
                    _targetField.SetValue(tpBoxed, MakeVec2d(pinnedTarget.x, pinnedTarget.y));
                    // v10：放宽叶段转向预算（只降不升）——AngleLimit(width, tenacity) 随之升到
                    // 地貌作者自设的 AngleLimitAbs 上限内，末端弯折由 A* 自己在成本场内完成
                    // （Overlap 避让与烘焙碰撞规则全保留）。宽河 tenacity 0.3→0.1 ≈ 2°/格→5°/格。
                    if (_angleTenacityField != null)
                    {
                        var tenacityBoxed = _angleTenacityField.GetValue(tpBoxed);
                        if (tenacityBoxed != null && System.Convert.ToDouble(tenacityBoxed) > 0.1)
                            _angleTenacityField.SetValue(tpBoxed,
                                System.Convert.ChangeType(0.1, tenacityBoxed.GetType()));
                    }
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
            return pinnedRoots + pinnedTargets;
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
                    // 两端各留 15 格顶点歧义区（2026-09-17 实测教训）：入场河带在共享顶点附近会
                    // 斜切过本边线段形成窄条，把"最宽连续段"骗到几十格外（236191 e2 假 d=-65）。
                    for (var l = 15f; l <= e.Len - 15f; l += 1f)
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

        /// <summary>
        /// PathFinder.FindPath Prefix（v10 主线之二，由 SeamlessGLRiverCompat.Patch_PathFinderFindPath
        /// 转发）。对钉位目标**收紧接受判定**：vanilla 的 TargetAcceptRadius=StepSize(5) +
        /// PlanarTargetFallback=3·StepSize(15，垂直平面接受、±10 横向无界) 使 A* 从不精确
        /// 抵达——这是缝旁错开的主体（实测 d=-29 与两半径同量级）。钉位时接受半径收到
        /// 0.5·步长（≥2 格，不低于半步以免跨步脱靶）、平面接受清零，逼 A* 真正走到目标；
        /// 路线本身仍由 A* 在成本场内决策（避让/碰撞/风格全部保留——与碰撞处理器不对抗，
        /// v10a 节点扭曲的教训）。PathFinder 每段任务新建、用后即弃，无需复原。
        /// 门控：targetPos 与 v9 钉过的某条边目标同式重合（容差 1.5 格）；非钉位目标零介入。
        /// </summary>
        internal static void OnFindPathPrefix(object __instance, object targetPos)
        {
            try
            {
                if (_acceptTightenDisabled || targetPos == null) return;
                if (!Resolve() || _finderAcceptField == null) return;
                if ((bool)_isGeneratingPreviewProp.GetValue(null)) return;
                if (_edges == null || _edges.Count == 0) return;

                var tracer = _finderTracerField.GetValue(__instance);
                var step = (double)_finderStepField.GetValue(__instance);
                var margin = tracer != null ? ReadVec2d(_gridMarginField.GetValue(tracer)) : Vector2.zero;
                var lead = (float)(System.Math.Max(4.0, 0.5 * step) + 3.0); // 与 v9 钉位同式外推量（v10c）

                var tp = ReadVec2d(targetPos);
                var matched = false;
                var bestDist = 1.5f;
                var bandIdx = -1;
                for (var i = 0; i < _edges.Count; i++)
                {
                    var e = _edges[i];
                    var d = Vector2.Distance(tp, e.Target - e.Inward * lead + margin);
                    if (d < bestDist) { bestDist = d; bandIdx = i; matched = true; }
                }
                if (!matched) return;

                // 收紧接受（只减不增；实例随任务丢弃，无需复原）。半径 = max(2, 0.75·StepSize)：
                // 不小于半个多步长——A* 节点按步长推进，半径过小会"跨过"目标无节点命中（planar 兜底
                // 已清零，命中不了就迭代到上限返回 null → 段降级为梯度跟随，比错开更糟）。
                // 垂直平面接受（PlanarTargetFallback，±10 横向无界）是错开的主体，直接清零。
                // 兜底放宽只在最后一档（hw=9）：中途放宽会以 vanilla 洪量接受出 28 格偏的结果。
                //（v10e 曾把 IterationLimit 抬到 50000——实测救不回合流图支路的 null，纯亏性能，已撤。）
                var accept = (double)_finderAcceptField.GetValue(__instance);
                var tightAccept = System.Math.Max(2.0, 0.75 * step);
                var hw = (float)_finderHeuristicField.GetValue(__instance);
                var relaxed = hw >= 9f;
                if (relaxed)
                {
                    if (accept < step) _finderAcceptField.SetValue(__instance, step);
                    _finderPlanarField.SetValue(__instance, 3d * step);
                }
                else
                {
                    if (accept > tightAccept) _finderAcceptField.SetValue(__instance, tightAccept);
                    _finderPlanarField.SetValue(__instance, 0d);
                }

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                {
                    var e2 = _edges[bandIdx];
                    RimExodusLog.Message(RimExodusLogModule.Compat,
                        $"GL river accept tighten: tile={_edgesTile} e{e2.EdgeIdx}->t{e2.NeighborTile} " +
                        $"accept={accept:F1}->{(relaxed ? step : tightAccept):F1} planar->{(relaxed ? (3d * step) : 0d):F0} hw={hw:F0}{(relaxed ? " (last-resort fallback)" : "")}.");
                }
            }
            catch (Exception ex)
            {
                _acceptTightenDisabled = true;
                if (!_warnedTightenFailure)
                {
                    _warnedTightenFailure = true;
                    var stackHead = ex.StackTrace == null ? "" : " @ " + ex.StackTrace.Split('\n')[0];
                    Log.Warning($"[RimExodus] GL river warp: FindPath prefix failed once (accept tightening disabled for this session, v9 pinning stays): {ex.GetType().Name}: {ex.Message}{stackHead}");
                }
            }
        }

        /// <summary>
        /// PathFinder.FindPath Postfix（v10d 终端收口；由 SeamlessGLRiverCompat.Patch_PathFinderFindPath
        /// 转发）。在 A* 结果的末节点前**插入一个恰在缝线穿越点上的节点**——跨缝段以它为端点，
        /// 中心线按构造精确穿过穿越点（d→0）；越缝延伸由原末节点（钉位目标在缝外）与 marginHead
        /// 保证。位移局限于终段一格步长（v10b 收紧使 A* 末端本就落在穿越点附近，折角很小），
        /// 终段位于接缝带边缘、远离根段与兄弟支流的河带——不触发 v10a 式碰撞互搏（那是全路径
        /// 位移扫过他人河带）。防停摆守卫：收口点与倒数第二节点间距不足时改为把末节点替换到
        /// 缝外 2.5 格（lat 仍精确）；末端已在穿越点 1.2 格内则不收口。
        /// </summary>
        internal static void OnFindPathPostfix(object __instance, ref object __result, object targetPos)
        {
            try
            {
                if (_terminalPinDisabled || __result == null || targetPos == null) return;
                if (!Resolve() || _nodeCtor == null) return;
                if ((bool)_isGeneratingPreviewProp.GetValue(null)) return;
                if (_edges == null || _edges.Count == 0) return;

                var nodes = __result as System.Collections.IEnumerable;
                if (nodes == null) { _terminalPinDisabled = true; return; }

                var tracer = _finderTracerField.GetValue(__instance);
                var step = (double)_finderStepField.GetValue(__instance);
                var margin = tracer != null ? ReadVec2d(_gridMarginField.GetValue(tracer)) : Vector2.zero;
                var lead = (float)(System.Math.Max(4.0, 0.5 * step) + 3.0);

                // 门控：与收紧 Prefix 同式匹配钉位目标。
                var tp = ReadVec2d(targetPos);
                var band = default(EdgeBand);
                var matched = false;
                var bestDist = 1.5f;
                for (var i = 0; i < _edges.Count; i++)
                {
                    var e = _edges[i];
                    var d = Vector2.Distance(tp, e.Target - e.Inward * lead + margin);
                    if (d < bestDist) { bestDist = d; band = e; matched = true; }
                }
                if (!matched) return;

                var positions = new List<Vector2>();
                foreach (var node in nodes)
                    positions.Add(ReadVec2d(_nodePositionField.GetValue(node)));
                var n = positions.Count;
                if (n < 2) return;

                // 收口点 = 缝线上的穿越点（grid 坐标）。
                var pin = band.Target + margin;
                var end0 = positions[n - 1];
                var endOffset = Vector2.Distance(end0, pin);
                if (endOffset < TerminalNoOpDist) return;

                var outward = -band.Inward;

                // 截尾：找最后一个仍在缝线内侧（沿外法线深度 ≤0）的节点 k，k 之后的越缝段丢弃
                // ——否则旧斜向交线（错误 lat）与收口点各过缝一次，河带在缝上并成宽带、中心不
                // 归零；截尾后中心线只在收口点过缝一次（按构造精确）。
                int k = n - 1;
                while (k >= 1 && Vector2.Dot(positions[k] - pin, outward) > 0f) k--;
                // 间距守卫：收口点与 node_k 距离不足会零弦长停摆，再回退一节点。
                while (k >= 1 && Vector2.Distance(positions[k], pin) < MinNodeSpacing) k--;
                if (k < 1) return; // 异常：连起点都判在外侧（几何错乱），放行原路径。

                // 【v10g 分级门控】≤12 无条件收口；(12,40] 做**余量走廊检查 + 锚点回溯**——
                // 从 node_k 向下最多试 6 个锚点，取首个到 pin 直线与一切已成形河带保持
                /// CorridorClearance 净空的 j（优先大 j = 短腿）。全部被占 → 放弃（拉腿会进入
                // 碰撞余量带引发循环、烧死无辜兄弟支流，v10f 教训）；>40 一律放弃（宁可不齐，
                // 不毁河）。
                int anchor = k;
                if (endOffset > TerminalPinMaxOffset)
                {
                    if (endOffset > TerminalPinCorridorMaxOffset || tracer == null)
                    {
                        if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                            RimExodusLog.Message(RimExodusLogModule.Compat,
                                $"GL river terminal pin SKIPPED: tile={_edgesTile} e{band.EdgeIdx}->t{band.NeighborTile} " +
                                $"endOffset={endOffset:F1} > {TerminalPinCorridorMaxOffset:F0} (keeping GL route).");
                        return;
                    }

                    anchor = -1;
                    var j = k;
                    for (var tried = 0; tried <= AnchorBacktrackSteps && j >= 1; tried++, j--)
                    {
                        if (Vector2.Distance(positions[j], pin) < MinNodeSpacing) continue;
                        if (!CorridorClear(tracer, positions[j], pin)) continue;
                        anchor = j;
                        break;
                    }
                    if (anchor < 0)
                    {
                        if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                            RimExodusLog.Message(RimExodusLogModule.Compat,
                                $"GL river terminal pin SKIPPED: tile={_edgesTile} e{band.EdgeIdx}->t{band.NeighborTile} " +
                                $"endOffset={endOffset:F1} (corridor blocked within {CorridorClearance} clearance, keeping GL route).");
                        return;
                    }
                }

                // 越缝延伸端点：永远沿外法线垂直外推（不沿用原末端的横向偏差——缝上河带不再斜偏，
                // 两侧同规则方向连续；缝外为 void 区，无形态顾虑）。
                var endPos = pin + outward * (lead + 2.5f);

                var pinDirV = pin - positions[anchor];
                var pinDir = pinDirV.sqrMagnitude > 1e-4f ? pinDirV.normalized : outward;
                var endDirV = endPos - pin;
                var endDir = endDirV.sqrMagnitude > 1e-4f ? endDirV.normalized : outward;

                var newList = (System.Collections.IList)Activator.CreateInstance(__result.GetType());
                var idx = 0;
                foreach (var node in nodes)
                {
                    idx++;
                    if (idx > anchor + 1) break; // 截尾：只保留 node0..node_anchor
                    newList.Add(node);
                }
                newList.Add(_nodeCtor.Invoke(new object[]
                    { MakeVec2d(pin.x, pin.y), MakeVec2d(pinDir.x, pinDir.y), 0, 0, null }));
                newList.Add(_nodeCtor.Invoke(new object[]
                    { MakeVec2d(endPos.x, endPos.y), MakeVec2d(endDir.x, endDir.y), 0, 0, null }));
                __result = newList;

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    RimExodusLog.Message(RimExodusLogModule.Compat,
                        $"GL river terminal pin: tile={_edgesTile} e{band.EdgeIdx}->t{band.NeighborTile} " +
                        $"nodes={n}->{newList.Count} keep=0..{anchor} pin=({pin.x:F0},{pin.y:F0}) endOffset={Vector2.Distance(end0, pin):F1}.");
            }
            catch (Exception ex)
            {
                _terminalPinDisabled = true;
                if (!_warnedTerminalFailure)
                {
                    _warnedTerminalFailure = true;
                    var stackHead = ex.StackTrace == null ? "" : " @ " + ex.StackTrace.Split('\n')[0];
                    Log.Warning($"[RimExodus] GL river warp: terminal pin failed once (disabled for this session, pinning/tightening stay): {ex.GetType().Name}: {ex.Message}{stackHead}");
                }
            }
        }

        /// <summary>
        /// 走廊检查（v10g 余量版）：from→to 线段（grid 坐标，每 ~2 格采样）沿途每点，其切比雪夫
        /// 半径 <see cref="CorridorClearance"/> 的圆盘内不得存在已成形河带（MainGrid&gt;0）。
        /// v10f 只查线段本体——制造腿贴着河带**碰撞余量带**边缘通过仍会触发烘焙碰撞循环、
        /// 烧死无辜兄弟支流（2026-09-17 北支路 stub 死亡链）。只用已缓存的 _mainGridField，
        /// 不依赖 _distanceGrid 的初始化语义。读不到 MainGrid 时保守返回 false（=不收口）。
        /// </summary>
        private static bool CorridorClear(object tracer, Vector2 from, Vector2 to)
        {
            try
            {
                var grid = _mainGridField.GetValue(tracer) as double[,];
                if (grid == null) return false;
                var maxX = grid.GetLength(0);
                var maxZ = grid.GetLength(1);
                var steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / 2f));
                for (var i = 0; i <= steps; i++)
                {
                    var p = Vector2.Lerp(from, to, (float)i / steps);
                    var cx = (int)p.x;
                    var cz = (int)p.y;
                    for (var dx = -CorridorClearance; dx <= CorridorClearance; dx++)
                    {
                        var gx = cx + dx;
                        if (gx < 0 || gx >= maxX) continue;
                        for (var dz = -CorridorClearance; dz <= CorridorClearance; dz++)
                        {
                            var gz = cz + dz;
                            if (gz < 0 || gz >= maxZ) continue;
                            if (grid[gx, gz] > 0.0) return false;
                        }
                    }
                }
                return true;
            }
            catch
            {
                return false;
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

                // v10 接受收紧 + 角限放宽（可选——解析失败只禁用对应项，v9 钉位不受影响）。
                var pathFinderType = AccessTools.TypeByName("TerrainGraph.Flow.PathFinder");
                if (pathFinderType != null)
                {
                    _finderTracerField = AccessTools.Field(pathFinderType, "_tracer");
                    _finderStepField = AccessTools.Field(pathFinderType, "FullStepDistance");
                    _finderAcceptField = AccessTools.Field(pathFinderType, "TargetAcceptRadius");
                    _finderPlanarField = AccessTools.Field(pathFinderType, "PlanarTargetFallback");
                    _finderHeuristicField = AccessTools.Field(pathFinderType, "HeuristicDistanceWeight");
                    if (_finderTracerField == null || _finderStepField == null
                        || _finderAcceptField == null || _finderPlanarField == null
                        || _finderHeuristicField == null)
                        _acceptTightenDisabled = true;
                }
                else _acceptTightenDisabled = true;
                _angleTenacityField = AccessTools.Field(traceParamsType, "AngleTenacity");

                // v10d 终端收口（可选——解析失败只禁用收口，钉位/收紧不受影响）。
                var nodeType = AccessTools.TypeByName("TerrainGraph.Flow.PathFinder+Node");
                if (nodeType != null)
                {
                    _nodeCtor = AccessTools.Constructor(nodeType, new[] { vec2dType, vec2dType, typeof(int), typeof(int), nodeType });
                    _nodePositionField = AccessTools.Field(nodeType, "Position");
                    if (_nodeCtor == null || _nodePositionField == null) _terminalPinDisabled = true;
                }
                else _terminalPinDisabled = true;

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
