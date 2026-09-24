using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 帧侧归因探针（2026-09 临时诊断，配 RimExodusPerfProbe）：tick 剖析器的"tick wall"是
    /// 心跳墙钟差（含渲染/UI/GC），行军期"unaccounted"桶 10-15s/窗 需要拆出帧侧与 tick 侧。
    /// Game.UpdatePlay ≈ 全部游戏主线程（tick 循环 + 地图渲染调度 + UI）；TickManagerUpdate ≈
    /// tick 循环本体。wall − UpdatePlay ≈ 引擎侧渲染；UpdatePlay − TickManagerUpdate − 渲染调度
    /// 等。两个目标方法均普通 C#（非 Burst），与剖析器"不 patch DoSingleTick"的约束不冲突。
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
    internal static class Patch_Game_UpdatePlay_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.GameUpdatePlay, stamp);
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    internal static class Patch_TickManager_TickManagerUpdate_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.TickManagerUpdate, stamp);
        }
    }
}
