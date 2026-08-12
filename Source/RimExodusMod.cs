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

            // 接缝覆写混合带比例（0=关闭，0.25=默认 25% 半径）
            listing.Label($"Seam override ratio: {s.seamOverrideRatio:F2}");
            s.seamOverrideRatio = listing.Slider(s.seamOverrideRatio, 0f, 0.5f);
            listing.Label("(接缝带 terrainDef 过渡混合占 tile 半径比例, 0=关闭)");

            listing.End();
        }
    }
}
