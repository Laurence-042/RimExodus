using HarmonyLib;
using Verse;
using Verse.Sound;

namespace RimExodus
{
    /// <summary>
    /// PerTick sustainer 维护契约 × 降频跳 tick（2026-09 "Tried to maintain ended sustainer:
    /// AncientVent_Ambient" 每 tick 报错定案修复）。
    ///
    /// 原版契约：<see cref="Sustainer.SustainerUpdate"/> 对 <see cref="MaintenanceType.PerTick"/>
    /// 的 sustainer 要求**每游戏 tick** 被 <see cref="Sustainer.Maintain"/> 推进一次，断供超过
    /// 1 tick 即超时 End（Sustainer.cs:102-108）。Maintain 的调用方是 thing comp 的 CompTick
    /// （如 Odyssey CompAncientVent 的 AncientVent_Ambient）——被降频跳 tick 的图上这条链
    /// 周期性断供 → sustainer 超时死亡 → 而 CompAncientVent **只在字段为 null 时生成、不重生
    /// 已 Ended 的**（持有死引用继续 Maintain）→ 之后每个放行 tick 都触发原版
    /// "Tried to maintain ended sustainer" 红字（实测邻图降频 50% 时 AncientHeatVent 排放中
    /// 持续刷屏）。与渲染修复无关——DrawNowAt 不触碰音频，纯 tick 层缺陷，此前只是无人盯着
    /// 非聚焦图的排放孔看。
    ///
    /// 修复两件：
    /// ① **降频桥接**（ThrottleBridge）：降频图（非 0% 凝固）上每帧把 lastMaintainTick 推到
    ///    当前 tick——sustainer 不再超时死亡。与 Throttle 的既有设计意图一致：**只有 0% 凝固
    ///    才显式结束全图 sustainer 防幽灵音**（Throttle() 内 EndAllInMap），1-99% 降频本意
    ///    是让图活着变慢（原版非聚焦图也每 tick 维护），跳 tick 打断契约是实现缺口。
    ///    已结束的（含 0% 显式结束的）不桥接——Ended 早退。
    /// ② **Ended 静默**（EndedSilence）：Maintain 打在已结束 sustainer 上时跳过原方法（连同
    ///    红字）。休眠/0% 凝固期间超时死亡的 sustainer，唤醒/解冻后 comp 的死引用每 tick
    ///    Maintain——在多图架构里这是预期状态而非异常（原版 comp 不重生 Ended 是其设计缺口，
    ///    后果仅"该声源静默到下次 comp 侧重开"，无功能损害）；保留红字只会制造结构性噪音。
    /// </summary>
    [HarmonyPatch(typeof(Sustainer), nameof(Sustainer.SustainerUpdate))]
    static class Patch_Sustainer_SustainerUpdate_ThrottleBridge
    {
        private static readonly AccessTools.FieldRef<Sustainer, int> lastMaintainTickRef =
            AccessTools.FieldRefAccess<Sustainer, int>("lastMaintainTick");

        static void Prefix(Sustainer __instance)
        {
            // 热路径极简：绝大多数 sustainer（非 PerTick / 已结束 / OnCamera）两次比较即出。
            if (__instance.Ended || __instance.info.Maintenance != MaintenanceType.PerTick)
            {
                return;
            }

            var map = __instance.info.Maker.Map;
            if (map == null || !SeamlessTickThrottle.IsThrottled(map) || SeamlessTickThrottle.IsFrozen(map))
            {
                return;
            }

            // 0% 凝固刻意不桥接（防幽灵音语义维持：让超时死亡走原路）。
            lastMaintainTickRef(__instance) = Find.TickManager.TicksGame;
        }
    }

    [HarmonyPatch(typeof(Sustainer), nameof(Sustainer.Maintain))]
    static class Patch_Sustainer_Maintain_EndedSilence
    {
        static bool Prefix(Sustainer __instance)
        {
            // Ended → 跳过原方法（原版行为 = Log.Error 后什么都不做；跳过仅去掉红字）。
            return !__instance.Ended;
        }
    }
}
