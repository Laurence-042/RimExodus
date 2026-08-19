using System.Diagnostics;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// verbose 分段计时器（2026-08 生成收尾段用时统计）：StartIf(false) 返回 null，
    /// 调用方一行取段耗时（t = timer?.Section() ?? 0），未启用时零开销。
    /// </summary>
    internal sealed class SectionTimer
    {
        private readonly Stopwatch sw;
        private long markMs;

        private SectionTimer(Stopwatch sw)
        {
            this.sw = sw;
        }

        public static SectionTimer StartIf(bool enabled)
        {
            return enabled ? new SectionTimer(Stopwatch.StartNew()) : null;
        }

        /// <summary>返回自上一分段点以来的毫秒数并推进分段点。</summary>
        public long Section()
        {
            var now = sw.ElapsedMilliseconds;
            var delta = now - markMs;
            markMs = now;
            return delta;
        }
    }

    /// <summary>
    /// 【诊断】生成收尾段计时 patch（2026-08）：genStep 链跑完后的 FinishGeneration 单帧
    /// 是"地图加载长尾"的历史黑盒，其中 map.FinalizeInit()（Verse/Map.cs:801）内部的
    /// 三个全图级重活在此拆解：
    /// - Pathing.RecalculateAllPerceivedPathCosts：391/392 genStep 期各调一次 + FinalizeInit
    ///   第三次，三处对比直接可见；
    /// - RegionAndRoomUpdater.RebuildAllRegionsAndRooms：全图 region/room 重建；
    /// - MapDrawer.RegenerateEverythingNow：mesh 全量重建——增量路径下生成由 MapComponentTick
    ///   驱动、无进行中 LongEvent，FinalizeInit 里的 LongEventHandler.ExecuteWhenFinished 会
    ///   立即同步执行（LongEventHandler.cs:298-305），即它也发生在 FinishGeneration 帧内。
    /// 门控 verboseLogging + IsAnyGenerating（FinishGeneration 全程 current 非空，三处全命中；
    /// MapPreview 后台预览线程不经过 IncrementalMapGenerator，天然不会触发跨线程日志）。
    /// 非生成期（读档/运行时触发的同款调用）零日志零负担。
    /// </summary>
    public static class Patches_MapGenTiming
    {
        private static bool ShouldLog()
        {
            return (RimExodusMod.Settings?.verboseLogging ?? false) && IncrementalMapGenerator.IsAnyGenerating;
        }

        [HarmonyPatch(typeof(Pathing), nameof(Pathing.RecalculateAllPerceivedPathCosts))]
        static class Patch_Pathing_RecalculateAllPerceivedPathCosts
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = ShouldLog() ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                __state.Stop();
                Log.Message($"[RimExodus] [GenTiming] Pathing.RecalculateAllPerceivedPathCosts {__state.ElapsedMilliseconds}ms");
            }
        }

        [HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.RebuildAllRegionsAndRooms))]
        static class Patch_RegionAndRoomUpdater_RebuildAllRegionsAndRooms
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = ShouldLog() ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                __state.Stop();
                Log.Message($"[RimExodus] [GenTiming] RegionAndRoomUpdater.RebuildAllRegionsAndRooms {__state.ElapsedMilliseconds}ms");
            }
        }

        [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow))]
        static class Patch_MapDrawer_RegenerateEverythingNow
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = ShouldLog() ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                __state.Stop();
                Log.Message($"[RimExodus] [GenTiming] MapDrawer.RegenerateEverythingNow {__state.ElapsedMilliseconds}ms");
            }
        }
    }
}
