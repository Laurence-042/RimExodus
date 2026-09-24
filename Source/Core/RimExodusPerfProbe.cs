using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        internal const int DrawMapMesh = 13;               // MapDrawer.DrawMapMesh（当前图每帧，section 提交/重建）
        internal const int DrawDynamicThings = 14;         // DynamicDrawManager.DrawDynamicThings（pawn 等动态物每帧）
        internal const int EdgeClippers = 15;              // MapEdgeClipDrawer.DrawClippers（边界裁剪平面）
        internal const int MaplessDoTick = 16;             // thing.Map==null 的 DoTick（世界 pawn 等，原落入未归账桶）
        internal const int OverlayDraw = 17;               // OverlayDrawer.DrawAllOverlays（覆盖图标，战斗期放大）
        internal const int FleckDraw = 18;                 // FleckManager.FleckManagerDraw（当前图粒子绘制）
        internal const int RegionRebuild = 19;             // RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms（每图每帧）
        internal const int GlowUpdate = 20;                // GlowGrid.GlowGridUpdate_First（每图每帧）
        internal const int WeatherUpdate = 21;             // WeatherManager.WeatherManagerUpdate（每图每帧）
        internal const int TickListDispatch = 22;          // TickList.Tick 三条全局 tick 表调度本体（该值−things=调度开销）
        // ===== 第四轮（2026-09-24）：Map.MapUpdate 剩余成员全量包装——第三轮数据显示 map4 的
        // MapUpdate 30.9ms/帧中已探针段只解释 8.9ms，剩余 ~22ms 在以下未包装成员里 =====
        internal const int SkyUpdate = 23;                 // SkyManager.SkyManagerUpdate（每图每帧）
        internal const int PowerNetsUpdate = 24;           // PowerNetManager.UpdatePowerNetsAndConnections_First（每图每帧）
        internal const int RegionClean = 25;               // RegionGrid.UpdateClean（每图每帧）
        internal const int LordUpdate = 26;                // LordManager.LordManagerUpdate（每图每帧）
        internal const int PostTickVisuals = 27;           // PostTickVisuals.ProcessPostTickVisuals（每图每帧，遍历全图 pawn）
        internal const int GlobalShaders = 28;             // GlobalRendererUtility.UpdateGlobalShadersParams（当前图）
        internal const int FallColors = 29;                // PlantFallColors.SetFallShaderGlobals（当前图）
        internal const int WaterTextures = 30;             // WaterInfo.SetTextures（当前图）
        internal const int MapMeshFirst = 31;              // MapDrawer.MapMeshDrawerUpdate_First（当前图，遍历全部 section）
        internal const int CondDraw = 32;                  // GameConditionManager.GameConditionManagerDraw（当前图）
        internal const int Designations = 33;              // DesignationManager.DrawDesignations（当前图）
        internal const int TempThingDraw = 34;             // TemporaryThingDrawer.Draw（当前图）
        internal const int AreaUpdate = 35;                // AreaManager.AreaManagerUpdate（每图每帧）
        internal const int FleckSim = 36;                  // FleckManager.FleckManagerUpdate（每图每帧，全系统模拟）
        // ===== 第五轮（2026-09-24）：PostTickVisuals 25.7ms/帧 拆解。第四轮定案它是 MapUpdate 缺口全部；
        // 指纹（视区无关、暂停零成本）指向 rotationTracker 无条件路径（含我方 UpdateRotation Postfix），
        // 同时拆 Drawer 子链六件 + DrawPos getter 横切热点 =====
        internal const int RotPTV = 37;                    // Pawn_RotationTracker.ProcessPostTickVisuals（无条件，每 pawn 每帧）
        internal const int UpdateRotation = 38;            // Pawn_RotationTracker.UpdateRotation（原版体 + 我方 Postfix 总账）
        internal const int JitterPTV = 39;                 // JitterHandler.ProcessPostTickVisuals
        internal const int FootprintPTV = 40;              // PawnFootprintMaker.ProcessPostTickVisuals（含足迹 mote spawn）
        internal const int BreathPTV = 41;                 // PawnBreathMoteMaker.ProcessPostTickVisuals（含呼吸 mote spawn）
        internal const int RipplePTV = 42;                 // PawnWaterRippleMaker.ProcessPostTickVisuals（含水波 fleck spawn）
        internal const int LeanPTV = 43;                   // PawnLeaner.ProcessPostTickVisuals
        internal const int RendererPTV = 44;               // PawnRenderer.ProcessPostTickVisuals（wiggler/renderTree）
        internal const int DrawPosGet = 45;                // Pawn_DrawTracker.DrawPos getter（横切，调用量大）
        internal const int CrossMapFacingPostfix = 46;     // 我方 UpdateRotation Postfix 自计时（免差分）
        internal const int ProbeCount = 47;

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
            "DrawMapMesh",
            "DrawDynamicThings",
            "EdgeClippers",
            "MaplessDoTick",
            "OverlayDraw",
            "FleckDraw",
            "RegionRebuild",
            "GlowUpdate",
            "WeatherUpdate",
            "TickListDispatch",
            "SkyUpdate",
            "PowerNetsUpdate",
            "RegionClean",
            "LordUpdate",
            "PostTickVisuals",
            "GlobalShaders",
            "FallColors",
            "WaterTextures",
            "MapMeshFirst",
            "CondDraw",
            "Designations",
            "TempThingDraw",
            "AreaUpdate",
            "FleckSim",
            "RotPTV",
            "UpdateRotation",
            "JitterPTV",
            "FootprintPTV",
            "BreathPTV",
            "RipplePTV",
            "LeanPTV",
            "RendererPTV",
            "DrawPosGet",
            "CrossMapFacingPostfix",
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

        /// <summary>MapDrawer.MapMeshDirty 脏源计数（按 flag 位拆分聚合）。dirty 可能在 MapPreview
        /// 后台生成线程触发，写入加锁（突发型调用，锁竞争可忽略）。</summary>
        private static readonly object dirtyLock = new object();
        private static readonly Dictionary<string, long> dirtyByFlag = new Dictionary<string, long>();
        private static long dirtyTotal;

        /// <summary>mapless DoTick 按 def 归因（[0]=ms、[1]=calls）：探针 id 位点给总量，这里给构成。</summary>
        private static readonly Dictionary<string, double[]> maplessByDef = new Dictionary<string, double[]>();

        /// <summary>当前图动态绘制物规模采样（窗口累计/峰值/采样数）——裁决 DrawDynamicThings
        /// 耗时高是"每 thing 贵"（放大嫌疑）还是"thing 数量多"（原版量级）。</summary>
        private static int dynThingsObs, dynThingsMax;
        private static long dynThingsSum;

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

        /// <summary>MapMeshDirty 计数（按位拆分聚合；后台线程安全）。仅诊断启用时计数。</summary>
        internal static void CountMapMeshDirty(ulong dirtyFlags)
        {
            if (!Enabled) return;
            lock (dirtyLock)
            {
                dirtyTotal++;
                var v = dirtyFlags;
                while (v != 0)
                {
                    var bit = v & (ulong)-(long)v; // 取最低置位
                    v ^= bit;
                    var key = DirtyFlagName(bit);
                    dirtyByFlag.TryGetValue(key, out var n);
                    dirtyByFlag[key] = n + 1;
                }
            }
        }

        /// <summary>flag 位 → 名称。mask 由 Def 索引在运行时分配（FlagDefUtility），位值不恒定，
        /// 故用已知 MapMeshFlagDefOf 字段现比（隐式 ulong 转换）；未知位（第三方注册的 flag）回退位号。</summary>
        private static string DirtyFlagName(ulong bit)
        {
            if ((ulong)RimWorld.MapMeshFlagDefOf.Things == bit) return "Things";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Terrain == bit) return "Terrain";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Roofs == bit) return "Roofs";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Buildings == bit) return "Buildings";
            if ((ulong)RimWorld.MapMeshFlagDefOf.BuildingsDamage == bit) return "BldgDmg";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Snow == bit) return "Snow";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Zone == bit) return "Zone";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Plan == bit) return "Plan";
            if ((ulong)RimWorld.MapMeshFlagDefOf.FogOfWar == bit) return "Fog";
            if ((ulong)RimWorld.MapMeshFlagDefOf.GroundGlow == bit) return "Glow";
            if ((ulong)RimWorld.MapMeshFlagDefOf.PowerGrid == bit) return "Power";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Gas == bit) return "Gas";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Pollution == bit) return "Pollution";
            if ((ulong)RimWorld.MapMeshFlagDefOf.Sand == bit) return "Sand";
            return "bit_" + bit.ToString("X");
        }

        /// <summary>mapless DoTick 按 def 归因累计（与 MaplessDoTick 位点 Record 并行调用）。</summary>
        internal static void RecordMaplessDoTick(string defKey, long start)
        {
            if (start == 0L || !Enabled || defKey == null) return;
            var end = Stopwatch.GetTimestamp();
            var ms = (end - start) * 1000.0 / Stopwatch.Frequency;
            if (!maplessByDef.TryGetValue(defKey, out var slot)) maplessByDef[defKey] = slot = new double[2];
            slot[0] += ms;
            slot[1] += 1;
        }

        /// <summary>动态绘制物规模采样（仅诊断启用时）。</summary>
        internal static void ObserveDynamicDrawThings(DynamicDrawManager manager)
        {
            if (!Enabled) return;
            var n = manager?.DrawThings?.Count ?? 0;
            dynThingsObs++;
            dynThingsSum += n;
            if (n > dynThingsMax) dynThingsMax = n;
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
                    .Append(" current=map").Append(Find.CurrentMap?.uniqueID ?? -1);
                // 视区尺寸（绘制段成本随缩放/视区放大，判读 drawSplit 必需）。
                try
                {
                    var vr = Find.CameraDriver?.CurrentViewRect;
                    if (vr.HasValue) sb.Append(" view=").Append(vr.Value.Width).Append('x').Append(vr.Value.Height);
                }
                catch { /* 诊断输出容错 */ }
                sb.Append(" gcGen0=+").Append(GC.CollectionCount(0) - Math.Max(lastGen0, 0))
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
            if (dirtyTotal > 0)
            {
                // 脏源分布（按 MapMeshFlag）：行军/战斗期持续脏化 = DrawMapMesh 每帧 section 重建的元凶。
                sb.Append("  mapMeshDirty: total=").Append(dirtyTotal);
                lock (dirtyLock)
                {
                    foreach (var pair in dirtyByFlag)
                        if (pair.Value > 0) sb.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
                    dirtyByFlag.Clear();
                    dirtyTotal = 0;
                }
                sb.AppendLine();
            }
            if (maplessByDef.Count > 0)
            {
                sb.Append("  maplessDoTick top:");
                foreach (var pair in maplessByDef.OrderByDescending(p => p.Value[0]).Take(6))
                    sb.Append(' ').Append(pair.Key).Append('(').Append(pair.Value[0].ToString("F0")).Append("ms/")
                        .Append((long)pair.Value[1]).Append(')');
                sb.AppendLine();
                maplessByDef.Clear();
            }
            if (dynThingsObs > 0)
            {
                sb.Append("  dynThings: avg=").Append(dynThingsSum / dynThingsObs)
                    .Append(" max=").Append(dynThingsMax).Append(" over ").Append(dynThingsObs).Append(" frames");
                sb.AppendLine();
                dynThingsObs = 0;
                dynThingsSum = 0;
                dynThingsMax = 0;
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
