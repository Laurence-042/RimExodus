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
        internal const int ProbeCount = 9;

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
        };

        private const long TicksPerSecondFallback = 10_000_000; // Stopwatch.Frequency 保险值
        private const int ReportIntervalTicks = 600;

        private static readonly long[] calls = new long[ProbeCount];
        private static readonly double[] totalMs = new double[ProbeCount];
        private static readonly double[] maxMs = new double[ProbeCount];
        private static readonly long[] extras = new long[ProbeCount];
        private static int nextReportTick = -1;

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
