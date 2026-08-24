using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Geological Landforms（GL）软依赖兼容层（2026-08）。
    ///
    /// 【根因】GL 1.6 的主 hook 挂在 <see cref="MapGenerator.GenerateContentsIntoMap"/> 的 Harmony
    /// Prefix（Landform.Prepare 建立进程级静态生成上下文 + 运行时注入 GenStep_BiomeVariants 与各
    /// landform 的 CustomGenSteps + BiomeGrid 初始化），Postfix 做 CleanUp。而
    /// <see cref="IncrementalMapGenerator"/> 内联复刻了 genStep 组装、从不调用该方法本体——方法级
    /// patch 全部失效 → GL worker（TileMutatorWorker_Landform.GeneratePostElevationFertility）首行
    /// 守卫"GeneratingLandforms 含本 landform"不成立即早退 → 地块图上 landform 静默缺失。
    /// Settlement 走原生同步生成路径（GenerateMap → 被 patch 的 GenerateContentsIntoMap）不受影响。
    ///
    /// 【修法】在增量路径的关键时点软反射复刻 GL Prefix/Postfix 语义：
    /// Start 组装 genSteps 前 TryPrepare + InitBiomeGrid + GetExtraGenSteps 注入；
    /// RunOneGenStep 每步前 EnsureContextAlive（分帧跨数百帧持有进程级静态，GL 自己的 MapPreview
    /// 后台线程或交错的原生生成都可能中途 CleanUp 它——丢失则重 Prepare，幂等）；
    /// FinishGeneration 开头与 CleanupFailedGeneration 里 Cleanup。
    ///
    /// 【GL API（1.7.x 本机 DLL 反射核实）】
    /// GeologicalLandforms.GraphEditor.Landform：static Prepare(Map)（种子 = 世界⊕tile 确定性派生，
    /// 不消费全局 Rand——增量路径 RNG 流零扰动；三参重载首行 CleanUp，重复调用幂等）、
    /// static CleanUp()、static GeneratingTile（**上下文存活判据必须用它**：Prepare 对无 landform
    /// 的 tile 早退时 GeneratingLandforms==null 而 GeneratingTile 非 null，AnyGenerating 会把
    /// "该 tile 无 landform"误判为"上下文丢失"导致每帧重 Prepare）、static GeneratingLandforms
    /// （元素实例属性 CustomGenSteps : List&lt;NodeRunGenStep&gt;，节点有 GenStepDef 属性）。
    /// GeologicalLandforms.GenStep_BiomeVariants（原版 GenStep 基类的 public def 字段）。
    /// GeologicalLandforms.BiomeGrid : MapComponent（Primary.Set(BiomeDef, null) +
    /// RefreshAllEntries(IWorldTileInfo)）。
    ///
    /// 【绑定纪律】全软反射 + 全 try/catch（软绑定绝不杀死 mod）：GL 缺失时所有入口 no-op
    /// （available=false 常量短路，零开销零红字）；GL 更新改签名时 Invoke 抛异常被吞并降级
    /// （landform 缺失但不崩，出一条 Warning 便于发现）。
    ///
    /// 【刻意不做】LandformData.CommitDirectly（把选中 landform 落世界数据供 MapPreview/跨尺寸
    /// 重生成锁定）——Prepare 的选择本身确定性（同世界同 tile 同结果），Commit 的收益限于预览
    /// 指示与跨尺寸一致性，反射面大收益小，跳过。
    ///
    /// 【白名单根因（2026-08 第二轮，已用"同 tile 预览正常/营地生成 None 且 Topology 正常"的
    /// 基线对照坐实）】GL 的 NodeUIWorldTileReq.CheckWorldObject 对 tile 上"非玩家派系、defName
    /// 不在 IgnoredWorldObjects 白名单（原版仅 Camp/AbandonedCamp）、非 Settlement"的 MapParent
    /// 把该 tile 全部 landform 的 commonness 归零——无派系的 RimExodus_SeamlessTileMap parent 命中：
    /// ①营地链在生成前就 WorldObjects.Add（营地图 landform 全缺 + GL Prefix 的 CommitDirectly 把
    /// 空列表烧进 LandformData 世界数据 → 该 tile 永久失效）；②地块图 parent 常驻（onComplete
    /// Add）期间 MapPreview 预览线程与运行时 Tile.Mutators 查询同被过滤（图销毁 parent 移除后
    /// 自愈）。增量路径原本正常纯因 parent 推迟到 onComplete 才 Add。修复 = 启动时把 defName
    /// 反射注册进白名单（RegisterIgnoredWorldObject）；旧档已毒化 tile（LandformData 存了
    /// Landforms==null）不自动清理（用户定夺 2026-08：与"程序化判定合法无 landform"不可区分，
    /// 需 GL 工具手动 Reset）。
    /// </summary>
    internal static class SeamlessLandformsCompat
    {
        private static readonly bool available;
        private static readonly MethodInfo prepareMethod;
        private static readonly MethodInfo cleanupMethod;
        private static readonly PropertyInfo generatingTileProp;
        private static readonly PropertyInfo generatingLandformsProp;
        private static readonly PropertyInfo customGenStepsProp;
        private static readonly PropertyInfo nodeGenStepDefProp;
        private static readonly Type biomeVariantsStepType;
        private static readonly FieldInfo biomeVariantsDefField;
        private static readonly Type biomeGridType;
        private static readonly PropertyInfo biomeGridPrimaryProp;
        private static readonly MethodInfo entrySetMethod;
        private static readonly MethodInfo refreshAllEntriesMethod;
        private static readonly FieldInfo ignoredWorldObjectsField;
        private static readonly bool glPresent;

        /// <summary>本轮增量生成是否已成功 Prepare（控制 EnsureContextAlive 是否重试；Cleanup 时复位）。</summary>
        private static bool prepared;

        /// <summary>
        /// TryPrepare 时记录的 GeneratingTile 实例（EnsureContextAlive 的归属判据：引用相同 ⟺
        /// 上下文仍是我们 Prepare 的那个、未被任何外部 Prepare 替换）。
        /// </summary>
        private static object preparedContext;

        static SeamlessLandformsCompat()
        {
            try
            {
                var landformType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.Landform");
                if (landformType == null) return;
                glPresent = true;
                prepareMethod = AccessTools.Method(landformType, "Prepare", new[] { typeof(Map) });
                cleanupMethod = AccessTools.Method(landformType, "CleanUp");
                generatingTileProp = landformType.GetProperty("GeneratingTile", BindingFlags.Public | BindingFlags.Static);
                generatingLandformsProp = landformType.GetProperty("GeneratingLandforms", BindingFlags.Public | BindingFlags.Static);
                customGenStepsProp = landformType.GetProperty("CustomGenSteps", BindingFlags.Public | BindingFlags.Instance);
                if (prepareMethod == null || cleanupMethod == null || generatingTileProp == null
                    || generatingLandformsProp == null || customGenStepsProp == null) return;

                var nodeType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.NodeRunGenStep");
                if (nodeType != null)
                    nodeGenStepDefProp = nodeType.GetProperty("GenStepDef", BindingFlags.Public | BindingFlags.Instance);

                // 反射类型名教训（2026-08 首版笔误）：GL 的 NodeUIWorldTileReq 文件路径是
                // GraphEditor/Nodes/UI/ 但 namespace 只有 GeologicalLandforms.GraphEditor（目录 ≠
                // namespace），写全名时按路径拼了 Nodes.UI 段 → TypeByName 恒 null → 白名单从未
                // 注册且无红字。修正 = 正确全名 + 按简单名扫 GL 程序集兜底（对 namespace 变化免疫）。
                var worldTileReqType = AccessTools.TypeByName("GeologicalLandforms.GraphEditor.NodeUIWorldTileReq")
                    ?? landformType.Assembly.GetTypes().FirstOrDefault(t => t.Name == "NodeUIWorldTileReq");
                if (worldTileReqType != null)
                    ignoredWorldObjectsField = AccessTools.Field(worldTileReqType, "IgnoredWorldObjects");

                biomeVariantsStepType = AccessTools.TypeByName("GeologicalLandforms.GenStep_BiomeVariants");
                if (biomeVariantsStepType != null)
                    biomeVariantsDefField = AccessTools.Field(biomeVariantsStepType, "def");

                biomeGridType = AccessTools.TypeByName("GeologicalLandforms.BiomeGrid");
                if (biomeGridType != null)
                {
                    biomeGridPrimaryProp = biomeGridType.GetProperty("Primary");
                    var entryType = biomeGridType.GetNestedType("Entry");
                    if (entryType != null)
                        entrySetMethod = AccessTools.Method(entryType, "Set");
                    refreshAllEntriesMethod = AccessTools.Method(biomeGridType, "RefreshAllEntries");
                }

                available = true;
            }
            catch
            {
                // GL 结构不符（版本升级等）：全部保持 null，available=false，零介入。
            }
        }

        /// <summary>
        /// 把 RimExodus_SeamlessTileMap 注册进 GL 的 NodeUIWorldTileReq.IgnoredWorldObjects 白名单
        /// （private static readonly List&lt;string&gt;——readonly 只锁引用、内容可变）。根因见类注释
        /// 【白名单根因】段：白名单命中后 CheckWorldObject 首过，营地图 landform 恢复且
        /// CommitDirectly 烧正确数据（不再毒化），地块图存在期间的预览/运行时 Mutators 查询全链恢复。
        ///
        /// 挂点 = RimExodusMod 构造器（mod 加载阶段，全部 assembly 已加载，早于任何 GL 判定与
        /// MapPreview 后台线程启动）。List 非线程安全，但注册发生在预览线程存在之前、此后全程
        /// 只读。GL 缺失时静默 no-op（glPresent=false）；GL 改结构（字段取不到）降级 Warning。
        /// 幂等（Contains 检查）。
        /// </summary>
        public static void RegisterIgnoredWorldObject()
        {
            if (!glPresent) return;
            try
            {
                var list = ignoredWorldObjectsField?.GetValue(null) as List<string>;
                if (list == null)
                {
                    Log.Warning("[RimExodus] GL compat: IgnoredWorldObjects unavailable (GL version changed?), " +
                                "seamless tile maps will suppress landforms on their tiles.");
                    return;
                }
                const string defName = "RimExodus_SeamlessTileMap";
                if (list.Contains(defName)) return;
                list.Add(defName);
                // 注册后读回自检（首版 namespace 笔误的教训：注册失败无红字、症状与未修复一模一样，
                // 必须让成功路径在日志上可判别）。
                Log.Message(list.Contains(defName)
                    ? "[RimExodus] GL compat: registered " + defName + " in IgnoredWorldObjects (verified, landforms stay active on seamless tile maps and in MapPreview)."
                    : "[RimExodus] GL compat: IgnoredWorldObjects registration FAILED verification (list did not contain the defName after Add).");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GL compat: IgnoredWorldObjects registration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 复刻 GL Prefix 的 Landform.Prepare(map)。GL 缺失/反射失败返回 false，调用方零介入。
        /// </summary>
        public static void TryPrepare(Map map)
        {
            if (!available || map == null) return;
            try
            {
                prepareMethod.Invoke(null, new object[] { map });
                prepared = true;
                preparedContext = generatingTileProp.GetValue(null);
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                {
                    var landforms = generatingLandformsProp.GetValue(null) as IEnumerable;
                    var n = 0;
                    if (landforms != null) foreach (var _ in landforms) n++;
                    Log.Message($"[RimExodus] GL compat: Landform.Prepare done for map {map.uniqueID} (landforms={n}).");
                }
            }
            catch (Exception ex)
            {
                prepared = false;
                Log.Warning($"[RimExodus] GL compat: Landform.Prepare failed, landforms disabled for this map: {ex.Message}");
            }
        }

        /// <summary>
        /// 分帧竞争守卫：每个 genStep 执行前检查 GL 静态上下文是否仍存活且归属本图。
        /// 归属判据（2026-08 收紧）= <see cref="preparedContext"/> 引用比对——GL 中 GeneratingTile
        /// 仅在 Prepare 赋值、CleanUp 置 null，引用相同 ⟺ 仍是我们的 Prepare 且未被替换。旧判据
        /// （非 null 即活）有漏洞：MapPreview 预览线程/交错原生生成的 Prepare 会装上**别的 tile**
        /// 的上下文（非空），本图 genStep 会消费别人的 landform 数据。识别替换后重 Prepare 抢回
        /// （幂等，首行自带 CleanUp——会把预览线程正在用的上下文清掉、该次预览丢 GL；正式图质量
        /// 优先，且增量启动已避让在飞预览，见 SeamlessTileManager.GenerateTileMap 忙判据）。
        /// </summary>
        public static void EnsureContextAlive(Map map)
        {
            if (!available || !prepared || map == null) return;
            try
            {
                if (ReferenceEquals(generatingTileProp.GetValue(null), preparedContext)) return;
                prepareMethod.Invoke(null, new object[] { map });
                preparedContext = generatingTileProp.GetValue(null);
                Log.Warning($"[RimExodus] GL compat: static context replaced mid-generation (MapPreview/native gen interleaved?), re-Prepare map {map.uniqueID}.");
            }
            catch (Exception ex)
            {
                prepared = false;
                Log.Warning($"[RimExodus] GL compat: EnsureContextAlive re-Prepare failed, landforms disabled for this map: {ex.Message}");
            }
        }

        /// <summary>复刻 GL Postfix 的 Landform.CleanUp()。幂等，失败静默降级。</summary>
        public static void Cleanup()
        {
            prepared = false;
            preparedContext = null;
            if (!available) return;
            try { cleanupMethod.Invoke(null, null); }
            catch { /* CleanUp 失败仅留下静态残留，下次 Prepare 首行会清 */ }
        }

        /// <summary>
        /// 复刻 GL Prefix 的 genStep 注入：GenStep_BiomeVariants（无条件，order 225，运行时构造
        /// GenStepDef——GL 原版同款做法，不进 DefDatabase）+ 各 landform 的 CustomGenSteps
        /// （非预览路径全量包含；GL 的 order&lt;230 过滤仅预览线程生效）。GL 缺失返回 null。
        /// </summary>
        public static List<GenStepWithParams> GetExtraGenSteps(Map map)
        {
            if (!available || map == null) return null;
            var result = new List<GenStepWithParams>();

            try
            {
                if (biomeVariantsStepType != null && biomeVariantsDefField != null)
                {
                    var bvDef = new GenStepDef { order = 225f };
                    var bvStep = Activator.CreateInstance(biomeVariantsStepType);
                    bvDef.genStep = (GenStep)bvStep;
                    biomeVariantsDefField.SetValue(bvStep, bvDef);
                    result.Add(new GenStepWithParams(bvDef, default));
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GL compat: GenStep_BiomeVariants injection failed (skipped): {ex.Message}");
            }

            try
            {
                var landforms = generatingLandformsProp.GetValue(null) as IEnumerable;
                if (landforms != null && nodeGenStepDefProp != null)
                {
                    foreach (var landform in landforms)
                    {
                        var nodes = customGenStepsProp.GetValue(landform) as IEnumerable;
                        if (nodes == null) continue;
                        foreach (var node in nodes)
                        {
                            var def = nodeGenStepDefProp.GetValue(node) as GenStepDef;
                            if (def != null) result.Add(new GenStepWithParams(def, default));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GL compat: CustomGenSteps collection failed (skipped): {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 复刻 GL Prefix 的 BiomeGrid 初始化（等价其 map.BiomeGrid() GetOrAdd 扩展）：
        /// components 里找 GL 的 BiomeGrid，没有则构造加入（FinishGeneration 会对其调
        /// MapComponentUtility.MapGenerated，生命周期与原生组件一致），然后 Primary.Set + RefreshAllEntries。
        /// </summary>
        public static void InitBiomeGrid(Map map)
        {
            if (!available || map == null || biomeGridType == null) return;
            try
            {
                object grid = null;
                foreach (var c in map.components)
                {
                    if (c != null && c.GetType() == biomeGridType) { grid = c; break; }
                }
                if (grid == null)
                {
                    grid = Activator.CreateInstance(biomeGridType, map);
                    map.components.Add((MapComponent)grid);
                }

                var entry = biomeGridPrimaryProp?.GetValue(grid);
                if (entry != null && entrySetMethod != null)
                    entrySetMethod.Invoke(entry, new object[] { map.Biome, null });

                var tile = generatingTileProp.GetValue(null);
                if (tile != null && refreshAllEntriesMethod != null)
                    refreshAllEntriesMethod.Invoke(grid, new object[] { tile });
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GL compat: BiomeGrid init failed (skipped): {ex.Message}");
            }
        }
    }
}
