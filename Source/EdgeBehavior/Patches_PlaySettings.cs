using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝带沉浸开关（2026-08）：在原版 PlaySettings 全局控制条（右下角地图视图 toggle 行）追加
    /// 一个 toggle，控制 <see cref="RimExodusSettings.seamExitBandEnabled"/>。
    ///
    /// 语义（用户定夺，勿回退）：
    /// - 开（默认）= 现状：浅绿撤离带 + 接缝中心划线显示，允许从接缝带撤离成大地图远行队
    ///   （征召踩带、JobGiver_ExitMap、组队界面出口）。
    /// - 关 = 沉浸模式（拍视频/截图等）：隐藏全部接缝带视觉，并**同步禁用一切经接缝带的原生
    ///   离场成远行队**——隐藏后若仅藏视觉，玩家误点接缝带仍会被撤离，比"看不见边线"严重得多。
    ///   跨缝步行/跨图下令/传送**不受影响**；NPC 撤离链（逃窜动物/游荡兜底/追击）不门控
    ///   （门控会造成追兵消失、动物滞留）。关闭时组队组不出队属预期（裁切图原生出口全 void），
    ///   此模式不为正常游玩设计。
    ///
    /// 实现门控的消费点统一走 <see cref="SeamExitBandGating.Enabled"/>：
    /// - <see cref="Patches_ExitMapGrid"/>（MapUsesExitGrid / Rebuild / SeamOutline）；
    /// - <see cref="Patches_RCellFinder"/> 三个出口重定向 Prefix。
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    [StaticConstructorOnStartup]
    static class Patch_PlaySettings_SeamBandToggle
    {
        private static Texture2D _icon;

        // 资产字段必须经 StaticConstructorOnStartup 加载（主线程约束 + 消 Verse 启动分析器
        // 警告）。首版把赋值放无特性且无人引用的嵌套 StaticInit 里 = _icon 恒 null、
        // toggle 从未显示过的潜伏 bug（2026-08 修警告时发现）。
        static Patch_PlaySettings_SeamBandToggle()
        {
            _icon = ContentFinder<Texture2D>.Get("UI/Commands/RimExodus_SeamBandToggle", reportFailure: false);
        }

        static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null) return;
            if (RimExodusMod.Settings == null) return;
            // 只在无缝地图上有意义（普通图无接缝带可藏）。
            var map = Find.CurrentMap;
            if (map == null || !SeamlessEdgeCells.HasSeamEdge(map)) return;

            if (_icon == null) return; // 资源缺失时不出按钮，不红字。
            row.ToggleableIcon(
                ref RimExodusMod.Settings.seamExitBandEnabled,
                _icon,
                "RimExodus_SeamBandToggleTooltip".Translate(),
                SoundDefOf.Mouseover_ButtonToggle);
        }
    }

    /// <summary>
    /// 接缝带沉浸开关的门控读取口（seamExitBandEnabled，默认 true）。
    /// 设置对象缺失（极早期调用）时按默认开启处理。
    /// </summary>
    internal static class SeamExitBandGating
    {
        internal static bool Enabled => RimExodusMod.Settings?.seamExitBandEnabled ?? true;
    }
}
