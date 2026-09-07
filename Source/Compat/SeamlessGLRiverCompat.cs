using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static RimWorld.Planet.SurfaceTile;

namespace RimExodus
{
    /// <summary>
    /// GL (Geological Landforms) 河流接缝适配层（2026-09；**v9 = Path 树钉位，主线在
    /// <see cref="GLRiverWarp"/>（patch PathTracer.Trace Prefix）**，勿回退 v1"offset 注入"、
    /// v2-v7"末端地形场补丁"或 v8"Named 函数场 warp"路线）。
    ///
    /// 【背景】GL 1.7 起河流走自有地貌系统：tile 选中带 OutputWaterFlow 的 landform 时
    /// TileMutatorsCustomization 移除原版全部五个河流 mutator，河道由节点图生成。六种河流
    /// landform（River/Confluence/Delta/Island/Source/RiverTerrain）共享同一条管线，最终全部
    /// 汇聚成唯一的河地形场（riverFunction），由 TileMutatorWorker_Landform.GeneratePostTerrain
    /// 逐格 SetTerrain——这是全形态共用 的唯一出口。
    ///
    /// 【v9 根因链（2026-09-03~04 五轮实测调查定案）】GL 河系统是"公共路径 → 多层派生"：主图
    /// Path: Trace 从 **Path 树**（Segment 列表）trace 出河，逐帧烘焙进 MainGrid/DistanceGrid/
    /// SideGrid/ValueGrid/OffsetGrid；这些 grid 经 Named IO 注入 RiverTerrain layer——biome（Soil
    /// 100% 带）/岸（Riverbank 140% 带）/水/海拔（河谷）/洞穴全部层从这批 Named Input 取值。
    /// v2-v7 只在 GeneratePostTerrain Postfix（链末端）平移最终水地形 → 三层分离（新河有水无岸、
    /// 旧河残留 Soil+Riverbank、河谷留旧位）；v8 在 Named 函数层包装平移采样代理 → 离散 eff 场
    /// 在位移跳变处切割河（"水平/竖直切割"）、两端 target 不同时中段混合拧碎——离散场缺陷结构性
    /// 修不掉（详见 GLRiverWarp 类注释 v8 段）。v9 回到源头：patch PathTracer.Trace Prefix，
    /// 把 Path 树的 Root 进场锚与出场 TraceParams.Target 钉到 <see cref="SeamlessPolygonGeometry.
    /// SeamCrossingPoint"/> 对称哈希穿越点——河形与全部派生层由 GL 自己的 A* 连续寻路生成，
    /// 自动一致、旧河不生成、零离散。**GeneratePostTerrain Prefix/Postfix 保留**（void 还原 +
    /// SeamlessRiverCells 登记——Eval 此刻即新位置实况）。
    ///
    /// 【v1 教训（2026-09-03 游戏内实测"完全没对齐"后解码 LandformRiver.xml 真实连线定案）】
    /// 官方河流地貌图**零消费 IRiverData 的 Offset 输出**：pathOrigin 的起点 = polarRect(Angle=
    /// InflowAngle, **Offset=常量 0**)（InflowOffset/TributaryOffset/TertiaryOffset 输出口全部无
    /// 消费者）；outflow 侧终点 = extendTowards 目标点 ← **Dynamic Random（种子随机）** ←
    /// ValidatePosition。因此"覆写 GetOrCreateTileLinkData 的 Offset 字段"对官方地貌是几何空操作
    /// （该 patch 保留为**自定义地貌辅助**——玩家在 GL 编辑器手动接 Offset 口的地图仍然受益，
    /// 且与钉位同穿越点约定）；outflow 侧随机、inflow 侧钉中线，两侧没有共同目标点。
    /// （references/ 的 landform XML 副本曾误判为"无连线数据"——连线在文件尾部 &lt;Connections&gt; 段，
    /// portID 对连接；workshop 正式版与此副本逐行一致。）
    ///
    /// 【void 还原 + 修改格登记（原版 RiverTerrainAt/BankVoidSkip 的 GL 等价物，v1 起保留）】
    /// GL 在 genStep 220 把河地形写进全图（含将来 void 区），389 清 void 前备份的基础快照会
    /// 携带河水 → 对端照抄时盖掉新图地面（v2 教训在 GL 路径的重现面）。修法 = Prefix 快照
    /// topGrid，Postfix 把将来 void 格的河写入还原 pre-worker 地形——与原版"河生成不进 void
    /// 区"同语义，快照天然干净，389 无需任何快照侧特判。河走廊格记入 SeamlessRiverCells
    /// （BuildRoadGuard 混合保护；按实际最终走廊登记）。
    ///
    /// 【已知限制（观察项）】①进场角度（RelAngle）不校正（与原版 v3 接受项同族）；②Root 钉位
    /// 平移整树而 cost 场（海拔）不平移 → 河形与 GL 原生有偏差（A* 自己绕障，特性级）；③带内
    /// 无 river-link 边 → 不钉（世界数据权威性）；④riverFlowMap 不重算（纯视觉）；⑤MapPreview
    /// 预览显示未钉位的河（IsGeneratingPreview 门控跳过）。
    ///
    /// 【绑定纪律】GL 可选依赖：全部软反射 + 手动绑定（离线验证器不覆盖——游戏内权威确认 =
    /// 启动日志 "GL river compat: bound" 三行）；类型漂移降级 Warning 不杀 mod；patch 体全程
    /// try/catch 吞异常放行 GL 原值（调用链上有 GL 世界 UI/编辑器/MapPreview 后台线程）。
    /// GL 缺失时 Register 直接短路，零介入零开销。
    /// </summary>
    internal static class SeamlessGLRiverCompat
    {
        // ===== Patch A 反射缓存（GeologicalLandforms.WorldTileInfo；自定义地貌辅助）=====
        private static Type _worldTileInfoType;
        private static FieldInfo _tileIdField;
        private static MethodInfo _riverPositionToOffsetMethod;

        /// <summary>TerrainGraph.IGridFunction&lt;TerrainDef&gt; 的 ValueAt（闭源 DLL，接口方法）。</summary>
        private static MethodInfo _gridValueAtMethod;

        // TileLinkData 三个 Offset 字段的 setter（private 嵌套类，属性 setter 为 internal）。
        private static MethodInfo _setInflowOffset;
        private static MethodInfo _setTributaryOffset;
        private static MethodInfo _setTertiaryOffset;

        // ===== Patch B 反射缓存 =====
        private static Type _landformType;
        private static FieldInfo _workerLandformField;
        private static PropertyInfo _outputWaterFlowProp;
        private static MethodInfo _getRiverTerrainMethod;
        private static MethodInfo _transformIntoMapSpaceMethod;
        private static PropertyInfo _generatingLandformsProp;

        /// <summary>
        /// 穿越点归一化基准尺寸。SeamCrossingPoint 的多边形几何随 mapSize 线性缩放，
        /// 归一化结果（crossing / mapSize）与尺寸无关——取任意常数即可，与 GL 默认
        /// GridFullSize=250 对齐纯为可读性。
        /// </summary>
        private const float NormalizedSize = 250f;

        /// <summary>
        /// offset 合法包络（语义保全）：GL 原始分布在 position∈(0.3,0.7)² → |offset| ≤
        /// 0.2·√2 ≈ 0.283。越界说明几何异常，放行 GL 原值而不是硬掰。
        /// </summary>
        private const float MaxOffset = 0.3f;

        /// <summary>v8 的 warp 衰减/岸带常量已随函数场路线整体删除（v9 = Path 树钉位，常量在 GLRiverWarp）。</summary>

        /// <summary>Patch B 的 topGrid 快照（ThreadStatic：MapPreview 后台线程与主线程生成并发）。</summary>
        [ThreadStatic] private static TerrainDef[] _snapTopGrid;
        [ThreadStatic] private static Map _snapMap;

        /// <summary>Warning 限流（Patch A 反查/写回异常只报一次，防世界 UI 高频查询刷屏）。</summary>
        private static bool _warnedAlignFailure;

        /// <summary>
        /// 绑定入口（RimExodusMod 构造器调用，PatchAll 之后）。GL 不在场直接短路；
        /// 类型/方法解析失败降级 Warning 返回（不完整绑定的字段保持 null，patch 体自守卫）。
        /// </summary>
        public static void Register(Harmony harmony)
        {
            try
            {
                _worldTileInfoType = AccessTools.TypeByName("GeologicalLandforms.WorldTileInfo");
                _landformType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.Landform");
                if (_worldTileInfoType == null || _landformType == null) return; // GL 未装

                _tileIdField = AccessTools.Field(_worldTileInfoType, "TileId");
                _riverPositionToOffsetMethod = AccessTools.Method(AccessTools.TypeByName("GeologicalLandforms.WorldTileUtils"), "RiverPositionToOffset");

                var gridInterfaceDef = AccessTools.TypeByName("TerrainGraph.IGridFunction`1");
                if (gridInterfaceDef != null)
                    _gridValueAtMethod = AccessTools.Method(gridInterfaceDef.MakeGenericType(typeof(TerrainDef)), "ValueAt");

                _workerLandformField = AccessTools.Field(AccessTools.TypeByName("GeologicalLandforms.TileMutatorWorker_Landform"), "Landform");
                _outputWaterFlowProp = _landformType.GetProperty("OutputWaterFlow", BindingFlags.Public | BindingFlags.Instance);
                // 目录 ≠ namespace（GraphEditor/Nodes/Output/ 下的类在 GeologicalLandforms.GraphEditor），
                // 按简单名扫 GL 程序集兜底，对 namespace 变化免疫（白名单注册首版教训同款）。
                var waterFlowType = FirstOrDefault(_landformType.Assembly, "NodeOutputWaterFlow");
                if (waterFlowType != null)
                    _getRiverTerrainMethod = AccessTools.Method(waterFlowType, "GetRiverTerrain");
                _transformIntoMapSpaceMethod = AccessTools.Method(_landformType, "TransformIntoMapSpace");
                _generatingLandformsProp = _landformType.GetProperty("GeneratingLandforms", BindingFlags.Public | BindingFlags.Static);

                // ===== Patch A：GetOrCreateTileLinkData Postfix（自定义地貌辅助，见类注释 v1 教训）=====
                var linkDataTarget = AccessTools.Method(_worldTileInfoType, "GetOrCreateTileLinkData");
                var linkDataPostfix = AccessTools.Method(typeof(Patch_GetOrCreateTileLinkData), nameof(Patch_GetOrCreateTileLinkData.Postfix));
                if (linkDataTarget != null && linkDataPostfix != null && _tileIdField != null && _riverPositionToOffsetMethod != null)
                {
                    harmony.Patch(linkDataTarget, postfix: new HarmonyMethod(linkDataPostfix));
                    LogBindConfirmed(harmony, linkDataTarget, "GetOrCreateTileLinkData");
                }
                else
                {
                    Log.Warning("[RimExodus] GL river compat: alignment patch NOT bound " +
                                $"(target={linkDataTarget}, postfix={linkDataPostfix}, tileId={_tileIdField != null}, posToOffset={_riverPositionToOffsetMethod != null}) — custom landform river offsets stay GL-seeded.");
                }

                // ===== Patch B：GeneratePostTerrain Prefix+Postfix（warp + void 还原 + 河格登记）=====
                var postTerrainTarget = AccessTools.Method(AccessTools.TypeByName("GeologicalLandforms.TileMutatorWorker_Landform"), "GeneratePostTerrain");
                var postTerrainPrefix = AccessTools.Method(typeof(Patch_GeneratePostTerrain), nameof(Patch_GeneratePostTerrain.Prefix));
                var postTerrainPostfix = AccessTools.Method(typeof(Patch_GeneratePostTerrain), nameof(Patch_GeneratePostTerrain.Postfix));
                if (postTerrainTarget != null && postTerrainPrefix != null && postTerrainPostfix != null
                    && _workerLandformField != null && _outputWaterFlowProp != null
                    && _getRiverTerrainMethod != null && _transformIntoMapSpaceMethod != null && _gridValueAtMethod != null)
                {
                    harmony.Patch(postTerrainTarget,
                        prefix: new HarmonyMethod(postTerrainPrefix),
                        postfix: new HarmonyMethod(postTerrainPostfix));
                    LogBindConfirmed(harmony, postTerrainTarget, "GeneratePostTerrain");
                }
                else
                {
                    Log.Warning("[RimExodus] GL river compat: warp/void-guard patch NOT bound " +
                                $"(target={postTerrainTarget}, landformField={_workerLandformField != null}, waterFlow={_outputWaterFlowProp != null}, " +
                                $"getRiverTerrain={_getRiverTerrainMethod != null}, transform={_transformIntoMapSpaceMethod != null}, valueAt={_gridValueAtMethod != null}) — " +
                                "GL rivers stay unaligned and will contaminate outer-strip snapshots.");
                }

                // ===== Patch C：PathTracer.Trace Prefix（v9 Path 树钉位——主线，
                // 逻辑在 GLRiverWarp；Trace(Path, int) 是唯一 public 入口，object 参数绑定）=====
                var pathTracerType = AccessTools.TypeByName("TerrainGraph.Flow.PathTracer");
                var tracePathType = AccessTools.TypeByName("TerrainGraph.Flow.Path");
                var traceTarget = AccessTools.Method(pathTracerType, "Trace", new[] { tracePathType, typeof(int) })
                                  ?? AccessTools.Method(pathTracerType, "Trace", new[] { tracePathType });
                var tracePrefix = AccessTools.Method(typeof(Patch_PathTracerTrace), nameof(Patch_PathTracerTrace.Prefix));
                var tracePostfix = AccessTools.Method(typeof(Patch_PathTracerTrace), nameof(Patch_PathTracerTrace.Postfix));
                if (traceTarget != null && tracePrefix != null && tracePostfix != null)
                {
                    harmony.Patch(traceTarget,
                        prefix: new HarmonyMethod(tracePrefix),
                        postfix: new HarmonyMethod(tracePostfix));
                    LogBindConfirmed(harmony, traceTarget, "PathTracer.Trace");
                }
                else
                {
                    Log.Warning("[RimExodus] GL river compat: path-pin patch NOT bound " +
                                $"(target={traceTarget}, prefix={tracePrefix}, postfix={tracePostfix}) — GL rivers stay unaligned across seams.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GL river compat: Register failed (mod continues, GL rivers stay vanilla GL behavior): {ex.Message}");
            }
        }

        private static Type FirstOrDefault(Assembly assembly, string simpleName)
        {
            try
            {
                foreach (var t in assembly.GetTypes())
                    if (t.Name == simpleName) return t;
            }
            catch { /* ReflectionTypeLoadException 等按整体不可用处理 */ }
            return null;
        }

        /// <summary>手动绑定成功确认（常开：手动绑定不在离线验证器覆盖内，游戏内权威判据）。</summary>
        private static void LogBindConfirmed(Harmony harmony, MethodBase target, string name)
        {
            var info = Harmony.GetPatchInfo(target);
            var bound = info != null && ((info.Prefixes != null && info.Prefixes.Any(p => p.owner == harmony.Id))
                                         || (info.Postfixes != null && info.Postfixes.Any(p => p.owner == harmony.Id)));
            Log.Message($"[RimExodus] GL river compat: bound {name} registered={(bound ? "OK" : "FAILED")}.");
        }

        // =====================================================================================
        // Patch C：Path 树钉位（PathTracer.Trace Prefix，v9 主线）
        // =====================================================================================

        /// <summary>
        /// 河路本体拦截：trace 开始前把 Path 树的 Root 进场锚与出场 Target 钉到接缝哈希
        /// 穿越 <see cref="SeamlessPolygonGeometry.SeamCrossingPoint"/>——之后河形/全部派生
        /// grid/全部消费层（biome/岸/水/海拔/洞穴）由 GL 自己连续生成，自动一致。全部逻辑
        /// （门控/边带/钉位/日志）在 <see cref="GLRiverWarp"/>。
        /// </summary>
        internal static class Patch_PathTracerTrace
        {
            internal static void Prefix(object path)
            {
                GLRiverWarp.OnTracePrefix(path);
            }

            /// <summary>量化诊断：实测缝线河带中心 vs 钉位 target 的逐边偏差（GLRiverWarp.OnTracePostfix）。</summary>
            internal static void Postfix(object __instance)
            {
                GLRiverWarp.OnTracePostfix(__instance);
            }
        }

        // =====================================================================================
        // Patch A：offset 注入（自定义地貌辅助——官方地貌不消费 Offset，见类注释 v1 教训）
        // =====================================================================================

        internal static class Patch_GetOrCreateTileLinkData
        {
            internal static void Postfix(object __instance, object __result)
            {
                if (__result == null) return;
                try
                {
                    var world = Find.World;
                    if (world?.grid == null) return;

                    var tileId = (int)_tileIdField.GetValue(__instance);
                    var rivers = world.grid[tileId].Rivers;
                    if (rivers == null || rivers.Count == 0) return;

                    TryAlign(__result, world.grid, tileId, rivers, "RiverInflow", ref _setInflowOffset);
                    TryAlign(__result, world.grid, tileId, rivers, "RiverTributary", ref _setTributaryOffset);
                    TryAlign(__result, world.grid, tileId, rivers, "RiverTertiary", ref _setTertiaryOffset);
                }
                catch (Exception ex)
                {
                    if (!_warnedAlignFailure)
                    {
                        _warnedAlignFailure = true;
                        Log.Warning($"[RimExodus] GL river compat: alignment postfix failed once (GL values preserved): {ex.Message}");
                    }
                }
            }

            /// <summary>
            /// 对一个入口字段（前缀名，如 "RiverInflow"）做穿越点对齐。任何一步解析失败都放行
            /// GL 原值——语义保全：宁可该缝不对齐，也不猜一个位置或抛异常。
            /// 目标点与 Patch B 的 warp 同约定（SeamCrossingPoint(River)），自定义地貌经此对齐后
            /// 与 warp 后的官方地貌在同一边汇于同一点。
            /// </summary>
            private static void TryAlign(object data, WorldGrid grid, int tileId, List<RiverLink> rivers,
                string fieldPrefix, ref MethodInfo setterCache)
            {
                var type = data.GetType();

                // Width ≤ 0 = GL 未选中该层 link（字段块未跑，Angle 是 default 0）——零改动。
                var width = AccessTools.Property(type, fieldPrefix + "Width")?.GetValue(data);
                if (width == null || (float)width <= 0f) return;

                var angleProp = AccessTools.Property(type, fieldPrefix + "Angle");
                if (angleProp == null) return;
                var angle = (float)angleProp.GetValue(data);

                // 反查邻居 link：inflow/tributary/tertiary 的角度都是裸 heading
                // （GetHeadingFromTo(neighbor, tileId)，无 clamp——GL 只 clamp Outflow），同式重算精确匹配。
                var neighborTile = -1;
                foreach (var link in rivers)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(grid.GetHeadingFromTo(link.neighbor, tileId), angle)) < 0.05f)
                    {
                        neighborTile = link.neighbor.tileId;
                        break;
                    }
                }
                if (neighborTile < 0) return; // 匹配失败（GL 分类变化？）：保留原值

                // RimExodus 共享边几何：邻居 → 边索引（GetTileNeighbors 序 = 多边形顶点序）→ 对称穿越点。
                var neighbors = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(tileId, neighbors);
                var edgeIdx = -1;
                for (var i = 0; i < neighbors.Count; i++)
                {
                    if (neighbors[i].tileId == neighborTile) { edgeIdx = i; break; }
                }
                if (edgeIdx < 0) return;

                var crossing = SeamlessPolygonGeometry.SeamCrossingPoint(tileId, edgeIdx, (int)NormalizedSize,
                    SeamlessPolygonGeometry.SeamLink.River);

                // 归一化位置 → GL 自己的换算（保证量纲/符号与其 polarRect 消费端一致）。
                var position = new Vector3(crossing.x / NormalizedSize, 0f, crossing.y / NormalizedSize);
                var offset = (float)_riverPositionToOffsetMethod.Invoke(null, new object[] { position, angle });
                if (float.IsNaN(offset) || float.IsInfinity(offset) || Mathf.Abs(offset) > MaxOffset) return;

                if (setterCache == null)
                    setterCache = AccessTools.PropertySetter(type, fieldPrefix + "Offset");
                setterCache?.Invoke(data, new object[] { offset });

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    RimExodusLog.Message(RimExodusLogModule.Compat,
                        $"River offset (custom-landform aux): tile={tileId} {fieldPrefix} neighbor={neighborTile} edge={edgeIdx} " +
                        $"crossing=({crossing.x:F0},{crossing.y:F0}) angle={angle:F1}° offset={offset:F3}.");
            }
        }

        // =====================================================================================
        // Patch B：接缝 warp + void 还原 + 河格登记（TileMutatorWorker_Landform.GeneratePostTerrain）
        // =====================================================================================

        internal static class Patch_GeneratePostTerrain
        {
            /// <summary>
            /// 快照 topGrid（仅 RimExodus 图：worldTile &lt; 0 = MapPreview 预览图/异常态，不快照，
            /// Postfix 以槽位为空放行）。ThreadStatic 防主线程与预览后台线程互踩；下一次 Prefix
            /// 覆写旧槽，GL 方法体抛异常导致 Postfix 未跑时自愈。
            /// </summary>
            internal static void Prefix(Map map)
            {
                _snapTopGrid = null;
                _snapMap = null;
                try
                {
                    if (map == null || SeamlessTileRegistry.GetMapWorldTile(map) < 0) return;
                    _snapTopGrid = (TerrainDef[])map.terrainGrid.topGrid.Clone();
                    _snapMap = map;
                }
                catch
                {
                    _snapTopGrid = null; // 快照失败 → Postfix 整体跳过（GL 原行为）
                    _snapMap = null;
                }
            }

            /// <summary>
            /// v8：warp 已前移到公共河路函数层（<see cref="GLRiverWarp"/>），本 Postfix 只剩
            /// void 还原 + 河走廊登记——Eval 此刻是包装链 = 最终实况（新位置的河）。
            /// </summary>
            internal static void Postfix(object __instance, Map map)
            {
                var snap = _snapTopGrid;
                var snapMap = _snapMap;
                _snapTopGrid = null; // 先清槽（无论本帧成败，绝不留给下一次）
                _snapMap = null;
                if (snap == null || snapMap != map) return;

                try
                {
                    var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
                    if (worldTile < 0) return;

                    // 复刻 GL 首行守卫：GL 自己早退（上下文丢失/不含本 landform）时零介入。
                    var landform = _workerLandformField.GetValue(__instance);
                    if (landform == null) return;
                    var generating = _generatingLandformsProp?.GetValue(null) as System.Collections.IEnumerable;
                    if (generating == null || !Contains(generating, landform)) return;

                    // 与 GL 同款调用链取河地形函数（gridCache 承接，二次求值走缓存）。
                    var riverFnNodeSpace = GetRiverFunctionNodeSpace(landform);
                    if (riverFnNodeSpace == null) return; // 无河流图层（非河流 landform 的 worker）
                    var transform = _transformIntoMapSpaceMethod.MakeGenericMethod(typeof(TerrainDef));
                    var riverFn = transform.Invoke(null, new[] { riverFnNodeSpace });
                    if (riverFn == null) return;

                    var mapSize = map.Size.x;
                    var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
                    var riverCells = SeamlessRiverCells.Ensure(map).cells;
                    var topGrid = map.terrainGrid.topGrid;
                    // ValueAt 坐标参数类型自适应（TerrainGraph 闭源：int/int 或 double/double）。
                    var pars = _gridValueAtMethod.GetParameters();
                    var doubleCoords = pars.Length == 2 && pars[0].ParameterType == typeof(double);

                    TerrainDef Eval(int x, int z)
                    {
                        if ((uint)x >= (uint)mapSize || (uint)z >= (uint)mapSize) return null;
                        return doubleCoords
                            ? (TerrainDef)_gridValueAtMethod.Invoke(riverFn, new object[] { (double)x, (double)z })
                            : (TerrainDef)_gridValueAtMethod.Invoke(riverFn, new object[] { x, z });
                    }

                    // ===== 全图单遍：void 还原 + 河走廊登记（Eval = v8 包装链 = warp 后最终实况）=====
                    var voidRestored = 0;
                    var registered = 0;
                    for (var x = 0; x < mapSize; x++)
                    {
                        for (var z = 0; z < mapSize; z++)
                        {
                            if (Eval(x, z) == null) continue;
                            var cell = new IntVec3(x, 0, z);
                            if (SeamlessPolygonGeometry.IsVoidCell(band, worldTile, mapSize, cell))
                            {
                                // 将来 void 格：还原 pre-worker 地形（= 原版"河不进 void 区"语义，
                                // 389 快照不再携带 GL 河水）。
                                var idx = map.cellIndices.CellToIndex(cell);
                                var before = snap[idx];
                                if (before != null && topGrid[idx] != before)
                                {
                                    map.terrainGrid.SetTerrain(cell, before);
                                    voidRestored++;
                                }
                            }
                            else
                            {
                                riverCells.Add(cell); // 按包装链实况登记（v8：warp 已在函数层完成）
                                registered++;
                            }
                        }
                    }

                    // v8：warp 重写/旧河道清除/岸带拷贝/补洞/贴缝延长 pass 全部删除——warp 已在
                    // 公共河路函数层完成（GLRiverWarp），biome/岸/水/海拔按 warp 后的河生成，
                    // 旧河道根本不生成。

                    if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    {
                        RimExodusLog.Message(RimExodusLogModule.Compat,
                            $"GL river guard: map={map.uniqueID} tile={worldTile} registered={registered} voidRestored={voidRestored}.");
                    }
                }
                catch (Exception ex)
                {
                    var stackHead = ex.StackTrace == null ? "" : " @ " + ex.StackTrace.Split('\n')[0];
                    Log.Warning($"[RimExodus] GL river compat: void-guard postfix failed (GL terrain kept as-is): {ex.GetType().Name}: {ex.Message}{stackHead}");
                }
            }

            /// <summary>与 GL 同款调用取（node space 的）河地形函数；无 OutputWaterFlow 连接返回 null。
            /// v8：此时拿到的是包装链（Named Input 已被 GLRiverWarp 换成平移采样代理）。</summary>
            private static object GetRiverFunctionNodeSpace(object landform)
            {
                var waterFlow = _outputWaterFlowProp.GetValue(landform);
                if (waterFlow == null) return null;
                return _getRiverTerrainMethod.Invoke(waterFlow, null);
            }

            private static bool Contains(System.Collections.IEnumerable list, object value)
            {
                foreach (var item in list)
                    if (ReferenceEquals(item, value)) return true;
                return false;
            }

        }
    }
}
