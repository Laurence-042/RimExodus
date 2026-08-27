using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// tick 花费剖析器（2026-08-27，诊断分级休眠收益用）：分桶统计每张图的耗时
    /// （thing DoTick / MapPreTick / MapPostTick / MapUpdate[每帧]）+ 每 tick 墙钟（governor
    /// GameComponentTick 心跳测相邻调用差，吞吐口径——刻意不 patch BurstCompile 的
    /// DoSingleTick，见 OnGameComponentTick 注释），每 600 ticks 输出一次汇总日志。
    /// 设置页"高级"tab 开关（tickProfilingEnabled），关闭时全部钩子首行早退。
    ///
    /// 【用法与判读】0% 档下若 thing/post 桶接近零而性能仍无起色 → 剩余大头在帧侧
    /// （MapUpdate 每帧照跑 + 邻图背景渲染，与 tick 降频无关，是下一个优化目标）；
    /// 若 throttled 图的 thing 桶仍显著 → 门控未生效（看 skipped 计数）。
    ///
    /// 【时序存储】单线程 + 各挂点无重入（DoTick/MapPreTick/MapPostTick/MapUpdate 均不嵌套调用），
    /// 桶间共用一个静态时间戳即可；skipped 用"前缀置零标记 + 后缀计数"传递。
    /// </summary>
    public static class SeamlessTickProfiler
    {
        /// <summary>汇报间隔（ticks，每轮现读设置即时生效；clamp [60,3000]——三档速度每现实秒
        /// 最多 ~360 ticks，下限 60 防三档下每现实秒多条刷屏）。</summary>
        private static int ReportIntervalTicks =>
            System.Math.Clamp(RimExodusMod.Settings?.tickProfileIntervalTicks ?? 600, 60, 3000);

        private class MapStats
        {
            public double ThingMs;
            public int ThingCalls;
            public int ThingSkipped;
            public double PreMs;
            public double PostMs;
            public double UpdateMs;
            public int UpdateFrames;
        }

        private static readonly Dictionary<Map, MapStats> stats = new Dictionary<Map, MapStats>();

        private static double tickMs;
        private static int tickCount;

        // 桶间传递时间戳（无重入，见类注释）。0 = 本次调用被门控跳过（供后缀计 skipped）。
        private static long thingStamp;
        private static long preStamp;
        private static long postStamp;
        private static long updateStamp;

        private static int nextReportTick = -1;

        public static bool Enabled => RimExodusMod.Settings?.tickProfilingEnabled ?? false;

        private static MapStats Stats(Map map)
        {
            if (!stats.TryGetValue(map, out var s))
            {
                s = new MapStats();
                stats[map] = s;
            }
            return s;
        }

        // ===== 挂点（各 patch 的 Prefix/Postfix 调用；Enabled 关闭时全部 no-op 化） =====

        public static void BeginThing(Thing thing, bool willRun)
        {
            thingStamp = willRun && Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public static void EndThing(Thing thing)
        {
            if (!Enabled) return;
            var map = thing.Map;
            if (map == null) return; // 未 spawn thing（inventory 内等）无图归属，不计数。
            var s = Stats(map);
            if (thingStamp == 0) { s.ThingSkipped++; return; }
            s.ThingCalls++;
            s.ThingMs += ElapsedMs(thingStamp);
        }

        public static void BeginPre(Map map)
        {
            preStamp = Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public static void EndPre(Map map)
        {
            if (Enabled && map != null) Stats(map).PreMs += ElapsedMs(preStamp);
        }

        public static void BeginPost(Map map)
        {
            postStamp = Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public static void EndPost(Map map)
        {
            if (Enabled && map != null) Stats(map).PostMs += ElapsedMs(postStamp);
        }

        public static void BeginUpdate(Map map)
        {
            updateStamp = Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public static void EndUpdate(Map map)
        {
            if (!Enabled || map == null || updateStamp == 0) return;
            var s = Stats(map);
            s.UpdateMs += ElapsedMs(updateStamp);
            s.UpdateFrames++;
        }

        private static long lastBeatStamp;
        private static bool beating;

        /// <summary>
        /// 心跳（governor 的 GameComponentTick 每游戏 tick 恒调，2026-08-27）：相邻两次调用的
        /// 墙钟差 = 每 tick 墙钟耗时（吞吐口径，含帧间隔；分桶看 CPU 归属）。刻意不 patch
        /// TickManager.DoSingleTick 取 CPU 口径——该方法 BurstCompile，离线验证器 ECall 伪迹 +
        /// Stance_Warmup 前科（伪迹但游戏内真炸），不值得赌。
        /// </summary>
        public static void OnGameComponentTick()
        {
            if (!Enabled)
            {
                beating = false;
                return;
            }
            var now = Stopwatch.GetTimestamp();
            if (beating)
            {
                tickMs += ElapsedMs(lastBeatStamp);
                tickCount++;
            }
            lastBeatStamp = now;
            beating = true;
            MaybeReport();
        }

        // ===== 汇报 =====

        private static double ElapsedMs(long stamp)
        {
            return (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
        }

        private static void MaybeReport()
        {
            var now = Find.TickManager?.TicksGame ?? 0;
            if (nextReportTick < 0) nextReportTick = now + ReportIntervalTicks;
            if (now < nextReportTick) return;
            nextReportTick = now + ReportIntervalTicks;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[RimExodus] Tick profile over last {ReportIntervalTicks} ticks: " +
                $"game tick wall {tickMs:F0}ms total, {tickMs / System.Math.Max(tickCount, 1):F2}ms/tick " +
                $"(≈{1000f / System.Math.Max(tickMs / System.Math.Max(tickCount, 1), 0.01f):F0} TPS if tick-bound); per map:");
            double thingSum = 0, preSum = 0, postSum = 0, updateSum = 0;
            foreach (var m in Find.Maps)
            {
                if (m == null || !stats.TryGetValue(m, out var s)) continue;
                var state = SeamlessDormancyManager.IsDormant(m) ? "dormant"
                    : SeamlessTickThrottle.IsThrottled(m) ? "throttled" : "active";
                var wt = SeamlessTileRegistry.GetMapWorldTile(m);
                var fast = SeamlessTickThrottle.IsThrottled(m)
                    ? $", fastZone {SeamlessTickThrottle.FastRegionCellCount(m)} cells" : "";
                sb.Append($"\n  map {m.uniqueID} (wt={wt}, {state}): things {s.ThingMs:F0}ms " +
                    $"({s.ThingCalls} calls, {s.ThingSkipped} skipped{fast}), pre {s.PreMs:F0}ms, post {s.PostMs:F0}ms, " +
                    $"update {s.UpdateMs:F0}ms over {s.UpdateFrames} frames");
                thingSum += s.ThingMs;
                preSum += s.PreMs;
                postSum += s.PostMs;
                updateSum += s.UpdateMs;
            }
            // 汇总 + 未归账缺口：wall − (things+pre+post) = World tick / 非地图 thing / TickList 与
            // GameComponent 自身开销等剖析器覆盖不到的部分（update 是每帧口径、不参与 tick 账）。
            var accounted = thingSum + preSum + postSum;
            sb.Append($"\n  totals: things {thingSum:F0}ms + pre {preSum:F0}ms + post {postSum:F0}ms = {accounted:F0}ms " +
                $"({accounted / System.Math.Max(tickMs, 0.01) * 100f:F0}% of tick wall), " +
                $"unaccounted {System.Math.Max(tickMs - accounted, 0):F0}ms (World/non-map things/TickList etc.); " +
                $"map updates {updateSum:F0}ms are per-frame, not part of tick wall");
            Log.Message(sb.ToString());

            // 复位（保留字典骨架，Map 引用定期清防泄漏——删除的图下次汇报前清掉）。
            tickMs = 0;
            tickCount = 0;
            stats.Clear();
        }
    }
}
