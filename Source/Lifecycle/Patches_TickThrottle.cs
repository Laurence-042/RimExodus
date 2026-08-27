using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 分级休眠的 thing 级门控（2026-08，<see cref="SeamlessTickThrottle"/>）。
    ///
    /// 原版 Thing tick 表按 TickerType 全局分桶、不按地图分组（Sleep 只能 RemoveAllFromMap 全摘）；
    /// 降频刻意**不动注册表**、在执行层跳 tick：<see cref="Thing.DoTick"/> 是三桶
    /// （Normal/Rare/Long）唯一的共同出口（TickList.Tick 逐 thing 调用它），单点覆盖。
    ///
    /// Prefix 热路径极简（<see cref="SeamlessTickThrottle.ShouldThingTick"/>：集合空早退 →
    /// Map 查集合 → 快速区白名单 → 相位取模），100%/无降频图时近似零成本。
    ///
    /// 注意：跳过 DoTick 对三桶语义一致——Rare/Long 桶本就数 tick 一跑（桶错相只是延迟累积），
    /// Normal 桶内 Thing 自带的 UpdateRateTicks/TickInterval(tickDelta) 机制（1.6 视野外降频）
    /// 在被跳的 tick 不累计 delta，与外层相位门控无冲突（外层粒度 ≥ 内层）。
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
    static class Patch_Thing_DoTick_Throttle
    {
        static bool Prefix(Thing __instance)
        {
            return SeamlessTickThrottle.ShouldThingTick(__instance);
        }
    }
}
