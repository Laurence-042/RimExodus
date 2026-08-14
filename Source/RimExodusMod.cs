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

            // 显式 patch CellFinder.TryFindRandomEdgeCellWith 的 4 参数重载（out 参数需 MakeByRefType()，
            // [HarmonyPatch] 特性无法声明，故在 PatchAll 之外手动绑定）。
            var target = AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith),
                new[] { typeof(System.Predicate<Verse.IntVec3>), typeof(Verse.Map), typeof(float), typeof(Verse.IntVec3).MakeByRefType() });
            var prefix = AccessTools.Method(typeof(Patch_CellFinder_TryFindRandomEdgeCellWith), nameof(Patch_CellFinder_TryFindRandomEdgeCellWith.Prefix));
            if (target != null && prefix != null)
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            else
                Log.Error($"[RimExodus] Failed to bind Patch_CellFinder_TryFindRandomEdgeCellWith (target={target}, prefix={prefix}).");

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

            // 权重上限（1.0=关闭，0.9=默认）
            listing.Label($"Seam override weight cap: {s.seamOverrideWeightCap:F2}");
            s.seamOverrideWeightCap = listing.Slider(s.seamOverrideWeightCap, 0.5f, 1.0f);

            listing.End();
        }
    }
}
