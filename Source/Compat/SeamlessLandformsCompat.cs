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
        private static bool available;
        private static MethodInfo prepareMethod;
        private static MethodInfo cleanupMethod;
        private static PropertyInfo generatingTileProp;
        private static PropertyInfo generatingLandformsProp;
        private static PropertyInfo customGenStepsProp;
        private static PropertyInfo nodeGenStepDefProp;
        private static Type biomeVariantsStepType;
        private static FieldInfo biomeVariantsDefField;
        private static Type biomeGridType;
        private static PropertyInfo biomeGridPrimaryProp;
        private static MethodInfo entrySetMethod;
        private static MethodInfo refreshAllEntriesMethod;
        private static FieldInfo ignoredWorldObjectsField;
        private static bool glPresent;

        /// <summary>本轮增量生成是否已成功 Prepare（控制 EnsureContextAlive 是否重试；Cleanup 时复位）。</summary>
        private static bool prepared;

        /// <summary>
        /// TryPrepare 时记录的 GeneratingTile 实例（EnsureContextAlive 的归属判据：引用相同 ⟺
        /// 上下文仍是我们 Prepare 的那个、未被任何外部 Prepare 替换）。
        /// </summary>
        private static object preparedContext;

        /// <summary>"GL 在 mod 列表但类型解析失败"的一次性告警旗标（版本漂移检测，防刷屏）。</summary>
        private static bool warnedGlExpectedMissing;

        static SeamlessLandformsCompat()
        {
            EnsureResolved();
        }

        /// <summary>
        /// 解析 GL 反射面（幂等，未成功前可重试）。首跑 = 静态构造器（RimExodusMod ctor 触达）；
        /// 生成期由 <see cref="EnsureRegisteredForGeneration"/> 兜底重试。
        ///
        /// 【LunarLoader 懒加载教训（2026-09-16"邻图没生成河流"定案，勿回退为一次性 ctor 解析）】
        /// GL 1.7.13 起工坊包 1.6/Assemblies 里只有 LunarLoader.dll，真程序集在 Lunar/Components/
        /// 由 LunarFramework **延迟入域**——RimExodusMod 构造器时点 TypeByName 拿不到类型，
        /// glPresent 恒 false，本兼容层（含白名单与 GL 河流 patch 注册）整层静默 no-op：GL 的
        /// Tile.Mutators getter patch 照常移除河流 tile 的原版河 mutator，分帧路径却无人 Prepare
        /// 上下文 → GL worker 守卫早退 → 两边都没河（症状：MutatorPostTerrain 0ms、无
        /// BiomeVariants 注入步、无 River edge match 日志）。曾因测试列表里 RimExodus 排在 GL 之后
        /// （懒加载恰好先完成）侥幸通过——ctor 时点解析对 mod 顺序敏感，不是可靠时点。
        /// </summary>
        internal static void EnsureResolved()
        {
            if (glPresent) return; // 已解析成功。结构半残（available=false）不重扫：同一程序集重试无意义。
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
        /// 生成入口兜底注册（原生 Prefix 与 IncrementalMapGenerator.Start 两路径都经
        /// <see cref="SeamlessSnapshotRegenerator.EnsureNeighborSnapshots"/> 漏斗到达此处；预览线程
        /// 与非表面层已被其守卫滤除，此处必为主线程正式生成）。GL 的 LunarLoader 懒加载可能晚于
        /// 任何启动时点（含 StaticConstructorOnStartup），本入口保证：任何地图正式生成（原生/分帧）
        /// 之前，白名单、GL 河流 patch 与本层上下文复刻能力在 GL 在场时必然已就绪。幂等——解析
        /// 成功/注册完成后全为常数级早退，每张图的调用开销可忽略。
        /// </summary>
        internal static void EnsureRegisteredForGeneration()
        {
            EnsureResolved();
            RegisterIgnoredWorldObject();
            SeamlessGLRiverCompat.Register(RimExodusMod.HarmonyInstance);

            // 版本漂移哨兵：到生成时 GL 程序集必已入域（其 Mutators getter patch 已在生效），
            // 此时仍解析不到类型 = GL 改了结构——出告警提醒适配层需要跟进，不再静默。
            if (glPresent || warnedGlExpectedMissing) return;
            foreach (var mod in LoadedModManager.RunningMods)
            {
                var id = mod.PackageIdPlayerFacing;
                if (id == null || id.ToLower() != "m00nl1ght.geologicallandforms") continue;
                warnedGlExpectedMissing = true;
                Log.Warning("[RimExodus] GL compat: Geological Landforms is active but its types did not resolve "
                            + "(version drift?) — GL landforms/rivers stay unadapted on seamless maps.");
                break;
            }
        }

        /// <summary>
        /// 把 RimExodus_SeamlessTileMap 注册进 GL 的 NodeUIWorldTileReq.IgnoredWorldObjects 白名单
        /// （private static readonly List&lt;string&gt;——readonly 只锁引用、内容可变）。根因见类注释
        /// 【白名单根因】段：白名单命中后 CheckWorldObject 首过，营地图 landform 恢复且
        /// CommitDirectly 烧正确数据（不再毒化），地块图存在期间的预览/运行时 Mutators 查询全链恢复。
        ///
        /// 挂点 = RimExodusMod 构造器（首次尝试）+ <see cref="EnsureRegisteredForGeneration"/>（生成
        /// 入口兜底重试——GL 经 LunarLoader 懒加载入域，ctor 时点可能尚未完成，见 EnsureResolved
        /// 注释的 2026-09-16 教训）。List 非线程安全，但两个挂点均为主线程且注册早于预览线程存在。
        /// GL 缺失时静默 no-op（glPresent=false）；GL 改结构（字段取不到）降级 Warning。幂等
        /// （Contains 检查）。
        /// </summary>
        public static void RegisterIgnoredWorldObject()
        {
            EnsureResolved();
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
            EnsureResolved();
            if (!available || map == null) return;
            try
            {
                prepareMethod.Invoke(null, new object[] { map });
                prepared = true;
                preparedContext = generatingTileProp.GetValue(null);
                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
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
