using System;
using System.Collections.Generic;
using HarmonyLib;
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

            // 绑定核对（2026-08，配合离线验证器甄别 CLR 伪迹 vs 死代码）：GetPatchedMethods 列出
            // 本 harmony 实例实际绑定的方法（真实 Mono 运行时结果，不受离线 CLR 伪迹影响）。
            // 离线验证器的 9 个伪迹失败 patch（GetClearRects/SelectInternal/SetTerrain/MapPreTick·MapUpdate/
            // RecalculateAllPerceivedPathCosts/SkyManagerUpdate）若不在此列 = 游戏内也没绑上 = 真死代码。
            var patchedCount = 0;
            var verbose = Settings?.verboseLogging ?? false;
            foreach (var method in harmony.GetPatchedMethods())
            {
                patchedCount++;
                if (verbose)
                    Log.Message("[RimExodus]   patched: " + method.DeclaringType?.FullName + "::" + method.Name);
            }
            Log.Message($"[RimExodus] Harmony patches applied to {patchedCount} methods"
                + (verbose ? " (list above)" : " (enable verbose logging for the list)."));

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

            // 显式 patch TileMutatorWorker_River.GetMapEdgeNodes（protected，元组返回值需显式绑定）。
            // 河端点从"随机直线图外交点"替换为接缝边中点锚点，消除河在接缝处的随机错位。
            var riverTarget = AccessTools.Method(typeof(RimWorld.TileMutatorWorker_River), "GetMapEdgeNodes");
            var riverPrefix = AccessTools.Method(typeof(Patch_TileMutatorWorker_River_GetMapEdgeNodes), nameof(Patch_TileMutatorWorker_River_GetMapEdgeNodes.Prefix));
            if (riverTarget != null && riverPrefix != null)
                harmony.Patch(riverTarget, prefix: new HarmonyMethod(riverPrefix));
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
                    // 两个距离滑条 tooltip 首句注明"需总开关开启才生效"+ 备注默认值；两者取值范围一致（1-8，
                    // 同值滑块位置相同，避免 UX 错位感）。
                    CheckRow(listing, "RimExodus_SettingsDormancyLabel", "RimExodus_SettingsDormancyTip",
                        v => s.dormancyEnabled = v, s.dormancyEnabled);
                    SliderRow(listing, "RimExodus_SettingsSleepLabel", "RimExodus_SettingsSleepTip",
                        s.dormancySleepHops, 1, 8, v => s.dormancySleepHops = (int)v);
                    SliderRow(listing, "RimExodus_SettingsDeleteLabel", "RimExodus_SettingsDeleteTip",
                        s.dormancyDeleteHops, 1, 8, v => s.dormancyDeleteHops = (int)v);
                    // 间隔滑条以秒为单位展示（存储为 ticks）；每轮现读设置，拖动即时生效。
                    SliderRow(listing, "RimExodus_SettingsSweepIntervalLabel", "RimExodus_SettingsSweepIntervalTip",
                        s.dormancySweepIntervalTicks / 60f, 1f, 60f,
                        v => s.dormancySweepIntervalTicks = Mathf.Max((int)(v * 60f), 60), "{0:F0}");
                    // 分级休眠中间档（2026-08）：百分比/快速区半径每 tick 现读（SeamlessTickThrottle），
                    // 拖动即时生效；百分比变更后快速区在下一轮 Sweep 幂等重入时按新值重建。
                    SliderRow(listing, "RimExodus_SettingsThrottlePercentLabel", "RimExodus_SettingsThrottlePercentTip",
                        s.dormancyThrottlePercent, 0, 100, v => s.dormancyThrottlePercent = (int)v);
                    SliderRow(listing, "RimExodus_SettingsThrottleRadiusLabel", "RimExodus_SettingsThrottleRadiusTip",
                        s.throttleSeamFastRadius, 0, 50, v => s.throttleSeamFastRadius = (int)v);
                    break;

                case SettingsTab.Combat:
                    // ===== tab：跨图战斗 =====
                    CheckRow(listing, "RimExodus_SettingsCombatLabel", "RimExodus_SettingsCombatTip",
                        v => s.crossMapCombatEnabled = v, s.crossMapCombatEnabled);
                    break;

                case SettingsTab.Advanced:
                    // ===== tab：高级 / 诊断 =====
                    SliderRow(listing, "RimExodus_SettingsNoiseLabel", "RimExodus_SettingsNoiseTip",
                        s.seamOverrideNoiseAmplitude, 0f, 0.5f, v => s.seamOverrideNoiseAmplitude = v, "{0:F2}");
                    CheckRow(listing, "RimExodus_SettingsVerboseLabel", "RimExodus_SettingsVerboseTip",
                        v => s.verboseLogging = v, s.verboseLogging);
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
    }
}
