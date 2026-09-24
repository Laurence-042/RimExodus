using System;
using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨图战斗/袭击热路径的临时性能探针（2026-09 诊断用，master 诊断分支）：
    /// 在未加缓存的原版热路径上采样"调用次数 / 累计耗时 / 峰值耗时 / 附加计数"，用于定位
    /// shambler 大军场景的实际消耗分布。每 600 ticks 输出一次窗口汇总并清零。
    ///
    /// 开关门 = DevMode（开发模式开着才记录与输出），关闭时 Record 仅一次 bool 检查即返回。
    /// 采样用 Stopwatch.GetTimestamp（~20ns/次），对被测路径的失真可忽略。
    /// 输出经 GameComponentTick 驱动（SeamlessDormancyGovernor 每 tick 调 ReportIfDue）。
    /// 本类是**有意为之的临时诊断设施**：定位完成后随诊断提交一起回退，勿扩展成长期功能。
    /// </summary>
    internal static class RimExodusPerfProbe
    {
        // 探针位点 id（数组下标）。
        internal const int BestAttackTargetPostfix = 0;   // Postfix 总入口（含早退）
        internal const int BestAttackTargetScan = 1;      // 过早退后真正进扫描的部分
        internal const int MeleeSearch = 2;               // TryFindCrossMapMeleeTarget
        internal const int ManhunterPrefix = 3;           // JobGiver_Manhunter Prefix（含本图探测）
        internal const int GotoNearestHostilePostfix = 4; // JobGiver_AIGotoNearestHostile Postfix
        internal const int CanBridgeTo = 5;               // 含 TryFindNearestReachableBridgeSpot
        internal const int BridgeSpotFlood = 6;           // TryFindBestBridgeSpot（双侧泛洪）
        internal const int CombatStepProbe = 7;           // 无许可战斗 pawn 每跨格探测
        internal const int RaidSpawnPostfix = 8;          // Patches_RaidOuterSpawn Postfix
        internal const int NeighborCompositeDraw = 9;     // SeamlessTileRenderer.MapComponentDraw（邻图合成，每帧）
        internal const int GameUpdatePlay = 10;            // Verse.Game.UpdatePlay 全量（tick+渲染调度+UI）
        internal const int TickManagerUpdate = 11;         // Verse.TickManager.TickManagerUpdate（tick 循环本体）
        internal const int ThrottleGate = 12;              // ShouldThingTick 门判定本体（每 thing 每 tick）
        internal const int ProbeCount = 13;

        private static readonly string[] Names =
        {
            "BestAttackTarget.postfix",
            "BestAttackTarget.scan",
            "MeleeSearch",
            "Manhunter.prefix",
            "GotoNearestHostile.postfix",
            "CanBridgeTo",
            "BridgeSpotFlood",
            "CombatStepProbe",
            "RaidSpawn.postfix",
            "NeighborCompositeDraw",
            "GameUpdatePlay",
            "TickManagerUpdate",
            "ThrottleGate",
        };

        private const long TicksPerSecondFallback = 10_000_000; // Stopwatch.Frequency 保险值
        private const int ReportIntervalTicks = 600;

        private static readonly long[] calls = new long[ProbeCount];
        private static readonly double[] totalMs = new double[ProbeCount];
        private static readonly double[] maxMs = new double[ProbeCount];
        private static readonly long[] extras = new long[ProbeCount];
        private static int nextReportTick = -1;

        // 帧侧归因（窗口级，非逐调用）：wall/frames/GC 计数差。
        private static long lastReportTimestamp;
        private static int lastFrameCount = -1;
        private static int lastGen0 = -1, lastGen1 = -1, lastGen2 = -1;

        /// <summary>MapComponentUpdate 按图聚合（ms）：裁决当前图 MapUpdate 大头在"组件段"还是
        /// "非组件段（drawer/section/fog）"——与 tick 剖析器的 per-map update 列相减即得。</summary>
        private static readonly Dictionary<Map, double> mapCompUpdateMs = new Dictionary<Map, double>();

        internal static bool Enabled => Prefs.DevMode;

        /// <summary>采样起点（Record 的配对调用；Enabled 为 false 时返回 0，Record 会跳过）。</summary>
        internal static long Now()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <summary>记录一次耗时；extra 累加到该位点的附加计数（如候选迭代数）。</summary>
        internal static void Record(int id, long start, long extra = 0)
        {
            if (start == 0L || !Enabled) return;
            var end = Stopwatch.GetTimestamp();
            var ms = (end - start) * 1000.0 / (Stopwatch.IsHighResolution ? Stopwatch.Frequency : TicksPerSecondFallback);
            calls[id]++;
            totalMs[id] += ms;
            if (ms > maxMs[id]) maxMs[id] = ms;
            extras[id] += extra;
        }

        /// <summary>只累加附加计数不计时（如候选迭代数）。</summary>
        internal static void Count(int id, long extra)
        {
            if (!Enabled || extra == 0) return;
            calls[id]++;
            extras[id] += extra;
        }

        /// <summary>只计数不计时（如入口调用次数，含早退）。</summary>
        internal static void Mark(int id)
        {
            if (!Enabled) return;
            calls[id]++;
        }

        /// <summary>MapComponentUpdate 按图累计（仅诊断启用时；start==0 表示关闭，直接返回）。</summary>
        internal static void RecordMapComponentUpdate(Map map, long start)
        {
            if (start == 0L || map == null || map.Disposed) return;
            var end = Stopwatch.GetTimestamp();
            mapCompUpdateMs[map] = (mapCompUpdateMs.TryGetValue(map, out var acc) ? acc : 0)
                + (end - start) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>每窗口输出汇总（由 GameComponentTick 驱动）。</summary>
        internal static void ReportIfDue()
        {
            if (!Enabled) return;
            var now = Find.TickManager.TicksGame;
            if (nextReportTick < 0) nextReportTick = now + ReportIntervalTicks;
            if (now < nextReportTick) return;
            nextReportTick = now + ReportIntervalTicks;

            var sb = new System.Text.StringBuilder();
            sb.Append("[RimExodus] PerfProbe window ").Append(ReportIntervalTicks).AppendLine(" ticks:");

            // 帧侧归因行：wall/帧数/fps/GC 代际回收次数差 + 当前图。fps 低 + tick 位点总耗远小于
            // wall = 渲染/GC 绑定而非 tick 绑定（tick 剖析器的 "tick wall" 是心跳墙钟差，含帧时间）。
            var stampNow = Stopwatch.GetTimestamp();
            var frameNow = UnityEngine.Time.frameCount;
            if (lastReportTimestamp != 0 && lastFrameCount >= 0)
            {
                var wall = (stampNow - lastReportTimestamp) * 1000.0 / Stopwatch.Frequency;
                var frames = frameNow - lastFrameCount;
                var fps = frames > 0 ? 1000.0 / (wall / frames) : 0.0;
                sb.Append("  frame: wall=").Append(wall.ToString("F0")).Append("ms frames=").Append(frames)
                    .Append(" fps=").Append(fps.ToString("F0"))
                    .Append(" current=map").Append(Find.CurrentMap?.uniqueID ?? -1)
                    .Append(" gcGen0=+").Append(GC.CollectionCount(0) - Math.Max(lastGen0, 0))
                    .Append(" gen1=+").Append(GC.CollectionCount(1) - Math.Max(lastGen1, 0))
                    .Append(" gen2=+").Append(GC.CollectionCount(2) - Math.Max(lastGen2, 0))
                    .AppendLine();
            }
            if (mapCompUpdateMs.Count > 0)
            {
                sb.Append("  mapCompUpdate:");
                foreach (var pair in mapCompUpdateMs)
                    if (!pair.Key.Disposed)
                        sb.Append(" map").Append(pair.Key.uniqueID).Append('=').Append(pair.Value.ToString("F0")).Append("ms");
                sb.AppendLine();
                mapCompUpdateMs.Clear();
            }
            lastReportTimestamp = stampNow;
            lastFrameCount = frameNow;
            lastGen0 = GC.CollectionCount(0);
            lastGen1 = GC.CollectionCount(1);
            lastGen2 = GC.CollectionCount(2);

            var any = false;
            for (var i = 0; i < ProbeCount; i++)
            {
                if (calls[i] == 0) continue;
                any = true;
                sb.Append("  ").Append(Names[i])
                    .Append(": calls=").Append(calls[i])
                    .Append(" total=").Append(totalMs[i].ToString("F1")).Append("ms")
                    .Append(" avg=").Append((totalMs[i] * 1000.0 / calls[i]).ToString("F0")).Append("us")
                    .Append(" max=").Append(maxMs[i].ToString("F1")).Append("ms");
                if (extras[i] > 0) sb.Append(" extra=").Append(extras[i]);
                sb.AppendLine();
            }
            if (!any) sb.AppendLine("  (no samples)");
            Log.Message(sb.ToString());
            Array.Clear(calls, 0, ProbeCount);
            Array.Clear(totalMs, 0, ProbeCount);
            Array.Clear(maxMs, 0, ProbeCount);
            Array.Clear(extras, 0, ProbeCount);
        }
    }
}
