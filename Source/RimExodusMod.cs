using System;
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

        /// <summary>绘制 Mod 设置窗口内容。</summary>
        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            var s = Settings;
            // borderPreloadDistance（0-50）
            listing.Label($"Border preload distance: {s.borderPreloadDistance}");
            s.borderPreloadDistance = (int)listing.Slider(s.borderPreloadDistance, 0, 50);
            listing.Gap();

            // borderNoBuildDistance（0-10）
            listing.Label($"Border no-build distance: {s.borderNoBuildDistance}");
            s.borderNoBuildDistance = (int)listing.Slider(s.borderNoBuildDistance, 0, 10);
            listing.Gap();

            listing.CheckboxLabeled("Preload all neighbors on start", ref s.preloadAllNeighborsOnStart);
            listing.Gap();

            listing.CheckboxLabeled("Verbose logging (diagnostics)", ref s.verboseLogging);
            listing.Gap();

            // 权重噪声幅度（0=关闭噪声，0.15=默认）
            listing.Label($"Seam override noise amplitude: {s.seamOverrideNoiseAmplitude:F2}");
            s.seamOverrideNoiseAmplitude = listing.Slider(s.seamOverrideNoiseAmplitude, 0f, 0.5f);
            listing.Gap();

            // 地图滚动休眠（2026-08）：距离策略见 SeamlessDormancyGovernor。
            listing.CheckboxLabeled("Map dormancy (rolling sleep/delete)", ref s.dormancyEnabled);
            listing.Gap();

            listing.Label($"Dormancy sleep hops: {s.dormancySleepHops} (maps ≥ this many hops from all player pawns sleep)");
            s.dormancySleepHops = (int)listing.Slider(s.dormancySleepHops, 2, 6);
            listing.Gap();

            listing.Label($"Dormancy delete hops: {s.dormancyDeleteHops} (tile maps ≥ this many hops get deleted)");
            s.dormancyDeleteHops = (int)listing.Slider(s.dormancyDeleteHops, 3, 8);
            listing.Gap();

            // 跨图索敌与射击（阶段5）：总开关，false 时战斗语义回到原版。
            listing.CheckboxLabeled("Cross-map targeting & shooting", ref s.crossMapCombatEnabled);

            listing.End();
        }
    }
}
