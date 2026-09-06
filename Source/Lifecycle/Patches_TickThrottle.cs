using HarmonyLib;
using Verse;
using Verse.AI;

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
    /// 在被跳的 tick 不累计 delta，速率型逻辑（需求/计时，delta 补偿）仍正确。
    /// **例外 = 移动**（2026-08-27 实测教训）：pawn 移动是纯累加器
    /// （nextCellCostLeft -= CostToPayThisTick()，每 tick 减、无差值补偿），跳 tick 直接慢 N 倍
    /// ——原版视野外降频不慢移动纯因它从不跳 Tick()（PatherTick 在 Tick() 里每 tick 跑）。
    /// 补偿见 <see cref="Patch_PatherFollower_CostToPayThisTick_Throttle"/>。
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
    static class Patch_Thing_DoTick_Throttle
    {
        static bool Prefix(Thing __instance)
        {
            var run = SeamlessTickThrottle.ShouldThingTick(__instance);
            SeamlessTickProfiler.BeginThing(__instance, run); // 剖析器关闭时 no-op。
            return run;
        }

        // 计入实际 DoTick 耗时（放行）/ skipped 计数（门控跳过）；剖析器关闭时首行早退。
        static void Postfix(Thing __instance)
        {
            SeamlessTickProfiler.EndThing(__instance);
        }
    }

    /// <summary>
    /// pawn 移动速率补偿（2026-08-27"降频图动物变慢"修复）：放行 tick 缴 N 倍移动成本，
    /// 平均格推进速率回到满速（机制见 <see cref="SeamlessTickThrottle.MoveCostMultiplier"/>）。
    /// 只在 <c>PatherTick</c> 内被调（放行 tick 才会跑），快速区/非降频图返回 1 倍零影响。
    /// 副作用（接受）：cachedMovePercentage 仅在放行 tick 刷新，邻图背景渲染中 pawn 移动动画
    /// 按 N tick 一拍阶梯推进（50% 档基本不可感知）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToPayThisTick")]
    static class Patch_PatherFollower_CostToPayThisTick_Throttle
    {
        /// <summary>pawn 字段 protected，缓存 FieldRef 读取（热路径：每个移动中 pawn 每放行 tick 一次）。</summary>
        private static readonly HarmonyLib.AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        static void Postfix(Pawn_PathFollower __instance, ref float __result)
        {
            var pawn = PawnRef(__instance);
            if (pawn != null)
            {
                __result *= SeamlessTickThrottle.MoveCostMultiplier(pawn);
            }
        }
    }

    /// <summary>
    /// 剖析器 unaccounted 拆桶（2026-09）：World tick 与 GameComponent tick 是 DoSingleTick 里
    /// 两段与地图数量无关、但可能被 mod 环境（第三方 GameComponent 遍历全部地图等）放大的大头。
    /// 单独计时使剖析报告的"剩余未归账"可归因（DoSingleTick 本体是 BurstCompile 勿 patch——
    /// 这两个被调方法是普通方法，安全）。剖析器关闭时挂点 no-op；每 tick 四次 patch 调度
    /// 的固定成本 ~2µs，忽略不计。
    /// </summary>
    [HarmonyPatch(typeof(RimWorld.Planet.World), nameof(RimWorld.Planet.World.WorldTick))]
    static class Patch_World_WorldTick_Profiler
    {
        static void Prefix()
        {
            SeamlessTickProfiler.BeginWorld();
        }

        static void Postfix()
        {
            SeamlessTickProfiler.EndWorld();
        }
    }

    [HarmonyPatch(typeof(GameComponentUtility), nameof(GameComponentUtility.GameComponentTick))]
    static class Patch_GameComponentUtility_GameComponentTick_Profiler
    {
        static void Prefix()
        {
            SeamlessTickProfiler.BeginGameComponents();
        }

        static void Postfix()
        {
            SeamlessTickProfiler.EndGameComponents();
        }
    }
}
