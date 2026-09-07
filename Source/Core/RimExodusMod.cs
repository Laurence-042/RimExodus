using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// RimExodus 主入口。
    /// 继承 Mod 以承载 <see cref="RimExodusSettings"/>（游戏内 Mod 设置菜单），
    /// 并在构造时应用所有 Harmony patch。
    /// </summary>
    /// <remarks>
    /// 不再使用 [StaticConstructorOnStartup]：Mod 子类的构造器由 ModHandler 在加载阶段调用，
    /// 时序早于 StaticConstructorOnStartup，且此时 ContentParser 已加载 Def，适合做 PatchAll。
    /// </remarks>
    public class RimExodusMod : Mod
    {
        /// <summary>全局 RimExodus 设置实例（由 GetSettings 提供，自动持久化）。</summary>
        public static RimExodusSettings Settings { get; private set; }

        public RimExodusMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimExodusSettings>();

            var harmony = new Harmony("RimExodus.SeamlessWorld");
            harmony.PatchAll();

            // GL 白名单注册（2026-08）：无派系的 RimExodus_SeamlessTileMap parent 会被 GL 的
            // CheckWorldObject 当外来 site 过滤掉该 tile 全部 landform（营地图生成时缺失且被
            // CommitDirectly 毒化永久失效；地块图存在期间 MapPreview 预览丢 GL）。必须在任何
            // GL 判定/预览线程之前注册——本构造器时点全部 mod assembly 已加载，够早。
            // 成功标志（日志）= "GL compat: registered RimExodus_SeamlessTileMap in IgnoredWorldObjects (verified...)"。
            SeamlessLandformsCompat.RegisterIgnoredWorldObject();

            // PerspectiveShift 兼容（2026-08）：PS 的 WASD 自由移动绕过 job/pather，挂
            // Avatar.ProcessMovement Postfix 把"边界带预加载 + 踩传送点跨缝传送"补回 avatar。
            // 软检测未装短路；手动绑定全程 try/catch（签名变更降级 Warning，绝不杀死 mod）。
            SeamlessPerspectiveShiftCompat.Register(harmony);

            // Vehicle Framework 兼容（2026-08）：VF 载具的移动被 vehiclePather 整体接管，
            // 挂在 Pawn_PathFollower 上的传送/跨图桥接触发器对载具失明——补绑 VF 侧四个 patch
            // （逐格传送 / StartPath 桥接 / 右键 GoHere 桥接 / 可达性灰显放行）。
            // 软检测未装短路；手动绑定全程 try/catch（签名变更降级 Warning，绝不杀死 mod）。
            SeamlessVehiclesCompat.Register(harmony);

            // GL 河流接缝适配（2026-09 v9）：GL 的河流地貌替换原版河流 mutator 后，原版河四 patch
            // 全部失效（河在接缝处各 tile 种子随机错位 + 河水污染外条带快照）。补绑三个 GL 侧
            // patch（GetOrCreateTileLinkData 穿越点对齐 + GeneratePostTerrain void 还原/河格登记 +
            // PathTracer.Trace Prefix/Postfix Path 树钉位与偏差诊断——河路本体层，水/岸/biome/海拔
            // 全层自动一致）。软检测未装短路；手动绑定全程 try/catch；成功标志 =
            // "GL river compat: bound" 三行。
            SeamlessGLRiverCompat.Register(harmony);

            // VEF ObjectSpawns 位置过滤（2026-09）：VEF 的地图物体刷出（VVE 载具残骸等）逐格判据
            // 不知道六边形裁切，系统性落进方形边缘 void（不可达不可修）。Postfix 挂其格级
            // CanSpawnAt，把 void ∪ 接缝带判为不可刷。软检测未装短路；手动绑定全程 try/catch；
            // 成功标志 = "VEF compat: bound ObjectSpawns cell filter"（增量分帧路径的覆盖由
            // GenerateMapPostfixReplay 复放机制提供，见 IncrementalMapGenerator.FinishGeneration）。
            SeamlessVEFCompat.Register(harmony);

            // Giddy-Up 2 compatibility: mounted riders and animals are separate spawned pawns. Register a
            // generic transfer-association provider which moves the mount through the normal state-preserving
            // transfer path and asks Giddy-Up to rebuild its own relationship/job after arrival.
            SeamlessGiddyUpCompat.Register();

            // 绑定核对（2026-08，配合离线验证器甄别 CLR 伪迹 vs 死代码）：GetPatchedMethods 列出
            // 本 harmony 实例实际绑定的方法（真实 Mono 运行时结果，不受离线 CLR 伪迹影响）。
            // 离线验证器的 9 个伪迹失败 patch（GetClearRects/SelectInternal/SetTerrain/MapPreTick·MapUpdate/
            // RecalculateAllPerceivedPathCosts/SkyManagerUpdate）若不在此列 = 游戏内也没绑上 = 真死代码。
            var patchedCount = 0;
            var verbose = RimExodusLog.Enabled(RimExodusLogModule.Core);
            foreach (var method in harmony.GetPatchedMethods())
            {
                patchedCount++;
                if (verbose)
                    Log.Message("[RimExodus]   patched: " + method.DeclaringType?.FullName + "::" + method.Name);
            }
            Log.Message($"[RimExodus] Harmony patches applied to {patchedCount} methods"
                + (verbose ? " (list above)" : " (enable the Core log module in settings for the list)."));

            // 显式 patch CellFinder.TryFindRandomEdgeCellWith 的 4 参数重载（out 参数需 MakeByRefType()，
            // [HarmonyPatch] 特性无法声明，故在 PatchAll 之外手动绑定）。
            var target = AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith),
                new[] { typeof(System.Predicate<Verse.IntVec3>), typeof(Verse.Map), typeof(float), typeof(Verse.IntVec3).MakeByRefType() });
            var prefix = AccessTools.Method(typeof(Patch_CellFinder_TryFindRandomEdgeCellWith), nameof(Patch_CellFinder_TryFindRandomEdgeCellWith.Prefix));
            if (target != null && prefix != null)
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            else
                Log.Error($"[RimExodus] Failed to bind Patch_CellFinder_TryFindRandomEdgeCellWith (target={target}, prefix={prefix}).");

            // 显式 patch CellFinder.TryFindRandomEdgeCellWith 的 5 参数（Rot4）重载——接缝化 Prefix
            // （Patch_CellFinder_TryFindRandomEdgeCellWithRot4，Patches_CellFinder.cs）。组队路线规划
            // AvailableExitTilesAt / TryFindExitSpot 的底层出口原语：原版候选格钉死方形边，倾斜六边形图
            // 上整圈 void → 六方向全败 → "你的远行队无法离开此区域"。
            // 历史（勿再回退）：首版落地后曾以"未倾斜图方向枚举正常（4/6 tiles）"回退为纯诊断——
            // 那只是六边形角点恰好轴对齐触到方形边、原版靠接触格侥幸通过；2026-08 倾斜图实测复现后
            // 重新落地。
            // 绑定必须 try/catch（2026-08 实测教训）：按名绑定的参数拼错（方向参数是 **dir** 不是 rot）
            // = 构造器抛异常 = mod 实例化失败、道路/河流等后续手动绑定全部丢失（离线验证器只扫
            // [HarmonyPatch] 特性类，不覆盖手动绑定，拦不住此类错误）；绑定失败 = 5 参维持原版行为
            // （即回退期现状），绝不杀死 mod。
            try
            {
                var targetRot4 = AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith),
                    new[] { typeof(System.Predicate<Verse.IntVec3>), typeof(Verse.Map), typeof(Verse.Rot4), typeof(float), typeof(Verse.IntVec3).MakeByRefType() });
                var rot4Prefix = AccessTools.Method(typeof(Patch_CellFinder_TryFindRandomEdgeCellWithRot4), nameof(Patch_CellFinder_TryFindRandomEdgeCellWithRot4.Prefix));
                if (targetRot4 != null && rot4Prefix != null)
                    harmony.Patch(targetRot4, prefix: new HarmonyMethod(rot4Prefix));
                else
                    Log.Error($"[RimExodus] Failed to bind Patch_CellFinder_TryFindRandomEdgeCellWithRot4 (target={targetRot4}, prefix={rot4Prefix}).");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] Binding Patch_CellFinder_TryFindRandomEdgeCellWithRot4 failed (mod continues, 5-arg overload stays vanilla): {ex}");
            }

            // 显式 patch GenStep_Roads.FindRoadExitCell（private 方法 + ref RoadPathingDef 参数，
            // [HarmonyPatch] 特性无法声明 private 方法名，故手动绑定）。道路穿越点对齐到接缝锚点。
            var roadTarget = AccessTools.Method(typeof(RimWorld.GenStep_Roads), "FindRoadExitCell",
                new[] { typeof(Verse.Map), typeof(float), typeof(Verse.IntVec3), typeof(RimWorld.RoadPathingDef).MakeByRefType() });
            var roadPrefix = AccessTools.Method(typeof(Patch_GenStep_Roads_FindRoadExitCell), nameof(Patch_GenStep_Roads_FindRoadExitCell.Prefix));
            if (roadTarget != null && roadPrefix != null)
                harmony.Patch(roadTarget, prefix: new HarmonyMethod(roadPrefix));
            else
                Log.Error($"[RimExodus] Failed to bind Patch_GenStep_Roads_FindRoadExitCell (target={roadTarget}, prefix={roadPrefix}).");

            // 显式 patch GenStep_Roads.ApplyDistanceField（private，嵌套类型 DistanceElement[,] 参数）。
            // 接缝锚点附近强制补铺路面材质（原版双重随机抽签在土路中线留 ~11-14% 断格）。
            var fillTarget = AccessTools.Method(typeof(RimWorld.GenStep_Roads), "ApplyDistanceField");
            var fillPostfix = AccessTools.Method(typeof(Patch_GenStep_Roads_ApplyDistanceField), nameof(Patch_GenStep_Roads_ApplyDistanceField.Postfix));
            if (fillTarget != null && fillPostfix != null)
                harmony.Patch(fillTarget, postfix: new HarmonyMethod(fillPostfix));
            else
                Log.Error($"[RimExodus] Failed to bind Patch_GenStep_Roads_ApplyDistanceField (target={fillTarget}, postfix={fillPostfix}).");

            // 显式 patch GenStep_Roads.RefinePath（private instance，返回 List<IntVec3>）。
            // 道路末端垂直化：接缝锚点端的折线末段替换为沿边内法线的直线（两侧共线对接，2026-08-31）。
            var refineTarget = AccessTools.Method(typeof(RimWorld.GenStep_Roads), "RefinePath");
            var refinePostfix = AccessTools.Method(typeof(Patch_GenStep_Roads_RefinePath), nameof(Patch_GenStep_Roads_RefinePath.Postfix));
            if (refineTarget != null && refinePostfix != null)
            {
                harmony.Patch(refineTarget, postfix: new HarmonyMethod(refinePostfix));
                var refineInfo = Harmony.GetPatchInfo(refineTarget);
                var refineBound = refineInfo != null && refineInfo.Postfixes != null &&
                                  refineInfo.Postfixes.Any(p => p.owner == harmony.Id);
                Log.Message($"[RimExodus] GenStep_Roads.RefinePath manual bind: target=OK postfix={(refinePostfix != null)} postfixRegistered={refineBound}.");
            }
            else
                Log.Error($"[RimExodus] Failed to bind Patch_GenStep_Roads_RefinePath (target={refineTarget}, postfix={refinePostfix}).");

            // 显式 patch TileMutatorWorker_River.GetMapEdgeNodes（protected，元组返回值需显式绑定）。
            // 河端点从"随机直线图外交点"替换为接缝边中点锚点，消除河在接缝处的随机错位。
            var riverTarget = AccessTools.Method(typeof(RimWorld.TileMutatorWorker_River), "GetMapEdgeNodes");
            var riverPrefix = AccessTools.Method(typeof(Patch_TileMutatorWorker_River_GetMapEdgeNodes), nameof(Patch_TileMutatorWorker_River_GetMapEdgeNodes.Prefix));
            if (riverTarget != null && riverPrefix != null)
            {
                harmony.Patch(riverTarget, prefix: new HarmonyMethod(riverPrefix));
                // 诊断（2026-08-30 河断排查）：无条件确认绑定成功——手动绑定不在离线验证器覆盖内，
                // 游戏内权威确认 = 本行 + PatchTools.GetPatchInfo（前缀已挂）。
                var patchInfo = Harmony.GetPatchInfo(riverTarget);
                var prefixBound = patchInfo != null && patchInfo.Prefixes != null &&
                                  patchInfo.Prefixes.Any(p => p.owner == harmony.Id);
                Log.Message($"[RimExodus] River GetMapEdgeNodes manual bind: target=OK prefix={(riverPrefix != null)} prefixRegistered={prefixBound}.");
            }
            else
                Log.Error($"[RimExodus] Failed to bind Patch_TileMutatorWorker_River_GetMapEdgeNodes (target={riverTarget}, prefix={riverPrefix}).");

            Log.Message("[RimExodus] Harmony patches applied.");
        }

        /// <summary>在 Mod 设置菜单中暴露可调节字段。</summary>
        public override string SettingsCategory()
        {
            return "RimExodus";
        }

        /// <summary>设置窗口当前 tab（2026-08 分割线改 tab，GL 同款 TabDrawer——分割线不够醒目）。</summary>
        private SettingsTab settingsTab = SettingsTab.Generation;

        private enum SettingsTab { Generation, Dormancy, Combat, Advanced }

        /// <summary>设置窗口滚动位置（个别 tab 内容超出窗口时兜底，2026-08）。</summary>
        private Vector2 settingsScrollPosition = Vector2.zero;

        /// <summary>
        /// 设置内容实测高度缓存。GUI.BeginScrollView 在 Begin 时即用传入 viewRect 定死滚动范围，
        /// 事后改高度对滚动条无效——用上一帧实测高度喂本帧（1 帧滞后，不可感知；切 tab 时同理）。
        /// </summary>
        private float settingsContentHeight = 400f;

        /// <summary>
        /// 绘制 Mod 设置窗口内容（2026-08 重构：tab 分页（地图生成/滚动休眠/跨图战斗/高级诊断）+ 滚动兜底，
        /// 全部条目经 Keyed 翻译键（1.6/Languages/{English,ChineseSimplified}/Keyed/RimExodus.xml），
        /// 带面向零基础玩家的 tooltip）。
        /// </summary>
        public override void DoSettingsWindowContents(Rect inRect)
        {
            // tab 行画在内容区上沿（TabDrawer 约定：baseRect = 内容区，tab 贴其 yMin 之上占 30px）。
            var tabs = new List<TabRecord>
            {
                new TabRecord("RimExodus_SettingsGroupGeneration".Translate(),
                    () => settingsTab = SettingsTab.Generation, settingsTab == SettingsTab.Generation),
                new TabRecord("RimExodus_SettingsGroupDormancy".Translate(),
                    () => settingsTab = SettingsTab.Dormancy, settingsTab == SettingsTab.Dormancy),
                new TabRecord("RimExodus_SettingsGroupCombat".Translate(),
                    () => settingsTab = SettingsTab.Combat, settingsTab == SettingsTab.Combat),
                new TabRecord("RimExodus_SettingsGroupAdvanced".Translate(),
                    () => settingsTab = SettingsTab.Advanced, settingsTab == SettingsTab.Advanced),
            };
            // tab 行画进内容区顶部（2026-08-27 修复标题被挡）：TabDrawer 会把 tab 画在传入 rect 的
            // yMin **之上**（占 32px）——原样传 inRect 时 tab 行侵入 Dialog_ModSettings 顶部 40px 的
            // 标题区把"RimExodus"盖住。传下移 32px 的 rect，tab 占内容区顶行，标题让位不删
            // （SettingsCategory 不能返回空：Dialog_Options 用它过滤"有设置的 mod"，空串会让整个
            // 设置入口从列表消失）。
            TabDrawer.DrawTabs(new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f), tabs);
            inRect.yMin += 32f;

            var viewRect = new Rect(0f, 0f, inRect.width - 16f, settingsContentHeight);
            Widgets.BeginScrollView(inRect, ref settingsScrollPosition, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            var s = Settings;

            switch (settingsTab)
            {
                case SettingsTab.Generation:
                    // ===== tab：地图生成 =====
                    SliderRow(listing, "RimExodus_SettingsBorderPreloadLabel", "RimExodus_SettingsBorderPreloadTip",
                        s.borderPreloadDistance, 0, 50, v => s.borderPreloadDistance = (int)v);
                    SliderRow(listing, "RimExodus_SettingsBorderNoBuildLabel", "RimExodus_SettingsBorderNoBuildTip",
                        s.borderNoBuildDistance, 0, 10, v => s.borderNoBuildDistance = (int)v);
                    CheckRow(listing, "RimExodus_SettingsIncrementalLabel", "RimExodus_SettingsIncrementalTip",
                        v => s.incrementalGenerationEnabled = v, s.incrementalGenerationEnabled);
                    SliderRow(listing, "RimExodus_SettingsBatchSizeLabel", "RimExodus_SettingsBatchSizeTip",
                        s.generationBatchSize, 16, 512, v => s.generationBatchSize = (int)v);
                    break;

                case SettingsTab.Dormancy:
                    // ===== tab：地图滚动休眠（性能） =====
                    // 总开关 tooltip 明确机制目的与效果（休眠=保留但不模拟 / 删除=彻底删除）+ 建议保持开启；
                    // 两个距离滑条 tooltip 首句注明"需总开关开启才生效"+ 备注默认值；两者取值范围一致（2-8，
                    // 同值滑块位置相同，避免 UX 错位感）。
                    CheckRow(listing, "RimExodus_SettingsDormancyLabel", "RimExodus_SettingsDormancyTip",
                        v => s.dormancyEnabled = v, s.dormancyEnabled);
                    SliderRow(listing, "RimExodus_SettingsSleepLabel", "RimExodus_SettingsSleepTip",
                        s.dormancySleepHops, 2, 8, v => s.dormancySleepHops = (int)v);
                    SliderRow(listing, "RimExodus_SettingsDeleteLabel", "RimExodus_SettingsDeleteTip",
                        s.dormancyDeleteHops, 2, 8, v => s.dormancyDeleteHops = (int)v);
                    // 间隔滑条以秒为单位展示（存储为 ticks）；每轮现读设置，拖动即时生效。
                    SliderRow(listing, "RimExodus_SettingsSweepIntervalLabel", "RimExodus_SettingsSweepIntervalTip",
                        s.dormancySweepIntervalTicks / 60f, 1f, 60f,
                        v => s.dormancySweepIntervalTicks = Mathf.Max((int)(v * 60f), 60), "{0:F0}");
                    // 威胁保活复查（2026-09，与扫描间隔相邻摆放——同族"间隔"设置；机制刻意独立：
                    // 复查平时名单空零成本、有敌人时较高频执行以保证敌人行为正常，与"平时一直
                    // 低频跑"的休眠扫描节奏本质不同，勿合并——2026-09 曾误合并被用户撤销）。
                    SliderRow(listing, "RimExodus_SettingsThreatPollLabel", "RimExodus_SettingsThreatPollTip",
                        s.threatKeepalivePollIntervalTicks / 60f, 1f, 30f,
                        v => s.threatKeepalivePollIntervalTicks = Mathf.Max((int)(v * 60f), 60), "{0:F0}");
                    // 分级休眠中间档（2026-08）：百分比/快速区半径每 tick 现读（SeamlessTickThrottle），
                    // 拖动即时生效；百分比变更后快速区在下一轮 Sweep 幂等重入时按新值重建。
                    SliderRow(listing, "RimExodus_SettingsThrottlePercentLabel", "RimExodus_SettingsThrottlePercentTip",
                        s.dormancyThrottlePercent, 0, 100, v => s.dormancyThrottlePercent = (int)v);
                    SliderRow(listing, "RimExodus_SettingsThrottleRadiusLabel", "RimExodus_SettingsThrottleRadiusTip",
                        s.throttleSeamFastRadius, 0, 50, v => s.throttleSeamFastRadius = (int)v);
                    // 前哨保留（2026-09 封存/重放）：数值输入行（不用滑条——阈值/权重跨越数量级，
                    // 且允许负值权重）；弹窗开关。语义唯一出处 = SeamlessMapModificationTracker。
                    listing.Gap(6f);
                    listing.Label("RimExodus_SettingsPreserveHeader".Translate(),
                        -1f, new TipSignal("RimExodus_SettingsPreserveHeaderTip".Translate()));
                    NumericRow(listing, "RimExodus_SettingsPreserveThresholdLabel", "RimExodus_SettingsPreserveThresholdTip",
                        s.dormancyPreserveHomeAreaThreshold, 0, 500,
                        v => s.dormancyPreserveHomeAreaThreshold = v, ref preserveThresholdBuf, "RimExodus_FieldPreserveThreshold");
                    NumericRow(listing, "RimExodus_SettingsPreserveCountLabel", "RimExodus_SettingsPreserveCountTip",
                        s.dormancyPreserveCount, 0, 999,
                        v => s.dormancyPreserveCount = v, ref preserveCountBuf, "RimExodus_FieldPreserveCount");
                    NumericRow(listing, "RimExodus_SettingsPreserveWeightHomeLabel", "RimExodus_SettingsPreserveWeightHomeTip",
                        s.dormancyPreserveWeightHome, -100, 100,
                        v => s.dormancyPreserveWeightHome = v, ref preserveWeightHomeBuf, "RimExodus_FieldPreserveWeightHome");
                    NumericRow(listing, "RimExodus_SettingsPreserveWeightAgeLabel", "RimExodus_SettingsPreserveWeightAgeTip",
                        s.dormancyPreserveWeightAge, -100, 100,
                        v => s.dormancyPreserveWeightAge = v, ref preserveWeightAgeBuf, "RimExodus_FieldPreserveWeightAge");
                    // 极性勿再接反（2026-09-04 首轮回归 bug）：标签语义是"询问"，字段语义是"禁用询问"
                    // ——显示与写入都必须取反；首版直连导致默认显示未勾选、勾选反而关闭询问。
                    CheckRow(listing, "RimExodus_SettingsPreservePromptLabel", "RimExodus_SettingsPreservePromptTip",
                        v => s.dormancyPreservePromptDisabled = !v, !s.dormancyPreservePromptDisabled);
                    break;

                case SettingsTab.Combat:
                    // ===== tab：跨图战斗 =====
                    CheckRow(listing, "RimExodus_SettingsCombatLabel", "RimExodus_SettingsCombatTip",
                        v => s.crossMapCombatEnabled = v, s.crossMapCombatEnabled);
                    // 袭击外缘生成（2026-09）：步行袭击从外侧（对侧未被看见的）接缝进场。
                    CheckRow(listing, "RimExodus_SettingsRaidOuterSpawnLabel", "RimExodus_SettingsRaidOuterSpawnTip",
                        v => s.raidOuterSpawnEnabled = v, s.raidOuterSpawnEnabled);
                    break;

                case SettingsTab.Advanced:
                    // ===== tab：高级 / 诊断 =====
                    SliderRow(listing, "RimExodus_SettingsNoiseLabel", "RimExodus_SettingsNoiseTip",
                        s.seamOverrideNoiseAmplitude, 0f, 0.5f, v => s.seamOverrideNoiseAmplitude = v, "{0:F2}");
                    // 分模块诊断日志（2026-09，取代单一 verbose 总开关）：总控 + 9 模块逐项。
                    // 默认全关；开启后对应模块输出详细日志（Warn/Error 与常开心跳不受控）。
                    listing.Gap(6f);
                    listing.Label("RimExodus_SettingsLogModulesLabel".Translate(),
                        -1f, new TipSignal("RimExodus_SettingsLogModulesTip".Translate()));
                    var allLogsOn = s.logGeneration && s.logTransfer && s.logCaravanExit && s.logWeather &&
                                    s.logDormancy && s.logCombat && s.logCompat && s.logSettlement && s.logCore;
                    var tmpAll = allLogsOn;
                    listing.CheckboxLabeled("RimExodus_SettingsLogAllLabel".Translate(), ref tmpAll,
                        "RimExodus_SettingsLogAllTip".Translate());
                    if (tmpAll != allLogsOn)
                    {
                        s.logGeneration = s.logTransfer = s.logCaravanExit = s.logWeather = s.logDormancy =
                            s.logCombat = s.logCompat = s.logSettlement = s.logCore = tmpAll;
                    }
                    CheckRow(listing, "RimExodus_SettingsLogGenerationLabel", "RimExodus_SettingsLogGenerationTip",
                        v => s.logGeneration = v, s.logGeneration);
                    CheckRow(listing, "RimExodus_SettingsLogTransferLabel", "RimExodus_SettingsLogTransferTip",
                        v => s.logTransfer = v, s.logTransfer);
                    CheckRow(listing, "RimExodus_SettingsLogCaravanExitLabel", "RimExodus_SettingsLogCaravanExitTip",
                        v => s.logCaravanExit = v, s.logCaravanExit);
                    CheckRow(listing, "RimExodus_SettingsLogWeatherLabel", "RimExodus_SettingsLogWeatherTip",
                        v => s.logWeather = v, s.logWeather);
                    CheckRow(listing, "RimExodus_SettingsLogDormancyLabel", "RimExodus_SettingsLogDormancyTip",
                        v => s.logDormancy = v, s.logDormancy);
                    CheckRow(listing, "RimExodus_SettingsLogCombatLabel", "RimExodus_SettingsLogCombatTip",
                        v => s.logCombat = v, s.logCombat);
                    CheckRow(listing, "RimExodus_SettingsLogCompatLabel", "RimExodus_SettingsLogCompatTip",
                        v => s.logCompat = v, s.logCompat);
                    CheckRow(listing, "RimExodus_SettingsLogSettlementLabel", "RimExodus_SettingsLogSettlementTip",
                        v => s.logSettlement = v, s.logSettlement);
                    CheckRow(listing, "RimExodus_SettingsLogCoreLabel", "RimExodus_SettingsLogCoreTip",
                        v => s.logCore = v, s.logCore);
                    CheckRow(listing, "RimExodus_SettingsTickProfileLabel", "RimExodus_SettingsTickProfileTip",
                        v => s.tickProfilingEnabled = v, s.tickProfilingEnabled);
                    // 汇报间隔以游戏秒展示（存储为 ticks，60-3000）；每轮现读，拖动即时生效。
                    SliderRow(listing, "RimExodus_SettingsTickProfileIntervalLabel", "RimExodus_SettingsTickProfileIntervalTip",
                        s.tickProfileIntervalTicks / 60f, 1f, 50f,
                        v => s.tickProfileIntervalTicks = System.Math.Max((int)(v * 60f), 60), "{0:F0}");
                    // 三层快照序列化开关（2026-08 卸载恢复支撑）：关闭只影响"新数据不入档"，
                    // 内存快照总保留、不再弹"删除已有快照"提示（2026-08-31 语义收拢）。
                    SnapshotToggleRow(listing, s);
                    // 精简快照重生成（2026-08-31）：读档后源图缺快照时生成邻图前自动补齐，
                    // 接缝混合参考恢复。
                    CheckRow(listing, "RimExodus_SettingsRegenSnapshotLabel", "RimExodus_SettingsRegenSnapshotTip",
                        v => s.regenerateMissingSnapshots = v, s.regenerateMissingSnapshots);
                    // 卸载前恢复原版兼容模式（2026-08）：一次性动作，二次确认后执行。
                    if (listing.ButtonText("RimExodus_UninstallRestoreButton".Translate(),
                            "RimExodus_UninstallRestoreButtonTip".Translate()))
                    {
                        if (Current.ProgramState != ProgramState.Playing)
                        {
                            Messages.Message("RimExodus_UninstallRestoreNoGame".Translate(),
                                MessageTypeDefOf.RejectInput, false);
                        }
                        else
                        {
                            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                                "RimExodus_UninstallRestoreConfirm".Translate(), SeamlessUninstallRestore.RunRestore,
                                destructive: true));
                        }
                    }
                    break;
            }

            listing.End();
            Widgets.EndScrollView();
            // 实测高度存缓存，下一帧的 BeginScrollView 用它定滚动范围（见字段注释）。
            settingsContentHeight = Mathf.Max(listing.CurHeight, inRect.height);
        }

        /// <summary>带 tooltip 的滑条行：标签行显示当前值（格式化串支持 {0:F2} 等），下一行是滑条。</summary>
        private static void SliderRow(Listing_Standard listing, string labelKey, string tipKey,
            float value, float min, float max, Action<float> set, string valueFormat = "{0}")
        {
            listing.Label(string.Format(labelKey.Translate(), string.Format(valueFormat, value)),
                -1f, new TipSignal(tipKey.Translate()));
            set(listing.Slider(value, min, max));
            listing.Gap(2f);
        }

        /// <summary>带 tooltip 的开关行。</summary>
        private static void CheckRow(Listing_Standard listing, string labelKey, string tipKey,
            Action<bool> set, bool current)
        {
            var tmp = current;
            listing.CheckboxLabeled(labelKey.Translate(), ref tmp, tipKey.Translate());
            set(tmp);
        }

        // TextFieldNumeric 的输入缓冲（跨帧持久——字段为空时 Widgets 自初始化为当前值）。
        private static string preserveThresholdBuf;
        private static string preserveCountBuf;
        private static string preserveWeightHomeBuf;
        private static string preserveWeightAgeBuf;

        /// <summary>
        /// 带 tooltip 的数值输入行（2026-09 前哨保留设置）：标签行显示当前值，下一行是数字输入框。
        /// 输入缓冲必须跨帧持久（否则每帧丢中间输入）；**未聚焦时强制镜像当前值**——静态 buffer
        /// 跨存档存活，读新档后残留上一档的文本会误显并被当作编辑基线写回（2026-09-04 首轮回归
        /// bug）；聚焦中不干预（保住输入过程，失焦后下一帧自然归一）。
        /// </summary>
        private static void NumericRow(Listing_Standard listing, string labelKey, string tipKey,
            int value, int min, int max, Action<int> set, ref string buffer, string controlName)
        {
            listing.Label(string.Format(labelKey.Translate(), value), -1f, new TipSignal(tipKey.Translate()));
            var rect = listing.GetRect(30f);
            GUI.SetNextControlName(controlName);
            var tmp = value;
            Widgets.TextFieldNumeric(rect, ref tmp, ref buffer, min, max);
            if (GUI.GetNameOfFocusedControl() != controlName && buffer != tmp.ToString())
            {
                buffer = null; // 下帧由 TextFieldNumeric 按当前值重建显示
            }
            set(tmp);
            listing.Gap(2f);
        }

        /// <summary>
        /// 三层快照序列化开关行（2026-08；2026-08-31 语义收拢）：开→关需一次确认（警告关闭后
        /// 卸载恢复只能依赖读档时精简重生成/就地复制降级）；关闭后**不再弹"删除已有快照"提示**——
        /// 已捕获数据照存、内存快照总保留，读档后缺失由 <see cref="RimExodusSettings.regenerateMissingSnapshots"/>
        /// 自动精简重生成补齐（显式清除只剩卸载恢复流程）。CheckboxLabeled 直接写 ref 本地变量——
        /// 不落 Settings 即可拦截（取消确认时下帧重绘回勾选态）。
        /// </summary>
        private static void SnapshotToggleRow(Listing_Standard listing, RimExodusSettings s)
        {
            var tmp = s.serializeBaseSnapshots;
            listing.CheckboxLabeled("RimExodus_SettingsSnapshotLabel".Translate(), ref tmp,
                "RimExodus_SettingsSnapshotTip".Translate());
            if (tmp == s.serializeBaseSnapshots) return; // 无变化。
            if (tmp)
            {
                s.serializeBaseSnapshots = true; // 关→开无风险，直接生效。
                return;
            }
            // 开→关：一次确认，取消 = 保持开（不写 s，下帧重绘回勾选态）。
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimExodus_SettingsSnapshotDisableConfirm".Translate(), delegate
                {
                    s.serializeBaseSnapshots = false;
                }, destructive: true));
        }
    }
}
