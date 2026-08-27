using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 分级休眠的中间档：无玩家 pawn 但仍在活跃圈（BFS dist &lt; sleepHops）的地图**降频 tick**
    /// （2026-08，用户"狂野想法"落地）。
    ///
    /// 【模型】活跃（全速）→ 降频（本类）→ 休眠（<see cref="SeamlessDormancyManager"/>）三档。
    /// 降频图的 tick 注册表**原封不动**（区别于 Sleep 的 RemoveAllFromMap 全摘）——只在执行层
    /// 跳 tick：<see cref="Verse.Thing.DoTick"/> Prefix 与 Map.MapPreTick/MapPostTick Prefix 按
    /// 同一相位谓词放行（Pre/Post/ThingTick 必须同拍，防 haulables/thing 状态错位）。图仍作为
    /// 邻图可见、MapUpdate（每帧）照跑（动画插值冻结，接受）——比真休眠多付渲染与注册成本，
    /// 换"看得见的远处世界"。
    ///
    /// 【相位】每 N tick 放行 1 tick（N 由百分比派生：<c>ceil(100/percent)</c>，100=原生 N=1、
    /// 50=N=2、25=N=4；0=永不放行 = "可见的凝固"）。map.uniqueID 做相位偏移 stagger，防多张
    /// 降频图同拍集中放行造成周期性尖峰。
    ///
    /// 【空间分区】降频图内距接缝边 ≤ throttleSeamFastRadius 的格为"快速区"（HashSet<IntVec3>，
    /// <see cref="SeamlessPolygonGeometry.ComputeVoidBand"/> 构建，与预加载带/禁建带同源几何）：
    /// 快速区内 thing 全速 tick（跨缝战斗的 turret/投射物/追兵不吃降频），区外按相位跳。
    /// 图级系统（powerNet/天气/野生生成，走 MapPostTick）**不分区**——整图同相位（已知限制，
    /// 见 doc/地图滚动休眠.md）。
    ///
    /// 【语义】跳 N-1 跑 1 = "远处世界变慢"而非"省着跑"：TicksGame 差值型逻辑（腐烂/计时）在
    /// 放行 tick 自我校正速率，每 tick 累加型逻辑慢 N 倍。1.6 原版视野外降频
    /// （<c>GenTicks.GetCameraUpdateRate</c>，非当前图 thing UpdateRateTicks=15）是同族官方先例。
    /// Pawn 一并降频（用户定夺 2026-08）：pather 走走停停属预期远处语义。
    ///
    /// 【不序列化】与休眠状态同口径：纯运行时性能状态，读档后 governor 首轮 Sweep 重新收敛。
    /// 状态集合每轮 Sweep 全量重算（Throttle/Unthrottle 幂等），设置（百分比/半径）每次判定现读
    /// ——拖滑条即时生效。
    /// </summary>
    public static class SeamlessTickThrottle
    {
        /// <summary>降频中的地图集合（运行时状态，刻意不序列化——见类注释）。</summary>
        private static readonly HashSet<Map> throttledMaps = new HashSet<Map>();

        /// <summary>每张降频图的接缝快速区（cells）与构建时的半径（设置变更后 Sweep 重入会按新半径重建）。</summary>
        private struct FastRegion
        {
            public HashSet<IntVec3> cells;
            public int radius;
        }

        private static readonly Dictionary<Map, FastRegion> fastRegions = new Dictionary<Map, FastRegion>();

        /// <summary>百分比 ≤0 时的相位哨兵：永不放行（= 可见的凝固，等价休眠但保留显示与 tick 注册）。</summary>
        private const int NeverTick = int.MaxValue;

        /// <summary>降频功能是否开启（百分比 &lt; 100 即开启；100 = 原生行为，活跃圈空图全速）。</summary>
        public static bool ThrottleEnabled => Percent < 100;

        private static int Percent => Mathf.Clamp(RimExodusMod.Settings?.dormancyThrottlePercent ?? 50, 0, 100);

        private static int FastRadius => Mathf.Clamp(RimExodusMod.Settings?.throttleSeamFastRadius ?? 15, 0, 50);

        /// <summary>相位间隔 N：100→1（关闭）、0→NeverTick、其余 ceil(100/percent)（≥2）。</summary>
        private static int IntervalTicks
        {
            get
            {
                var p = Percent;
                if (p >= 100) return 1;
                if (p <= 0) return NeverTick;
                return System.Math.Max(2, Mathf.CeilToInt(100f / p));
            }
        }

        /// <summary>map 是否处于降频（null/Disposed 安全返回 false）。</summary>
        public static bool IsThrottled(Map map)
        {
            return map != null && !map.Disposed && throttledMaps.Contains(map);
        }

        /// <summary>该降频图快速区格数（0 = 无快速区/未降频；剖析报告用——0% 档下 calls 即来自这些格）。</summary>
        public static int FastRegionCellCount(Map map)
        {
            return map != null && fastRegions.TryGetValue(map, out var fr) && fr.cells != null ? fr.cells.Count : 0;
        }

        /// <summary>
        /// 本 tick 该图是否放行图级模拟（MapPreTick/MapPostTick 门控）。非降频图恒 true。
        /// 与 thing 门控同相位（同一谓词），但不含快速区白名单——图级系统不分区（已知限制）。
        /// </summary>
        public static bool MapSimulatesThisTick(Map map)
        {
            if (throttledMaps.Count == 0 || !throttledMaps.Contains(map)) return true;
            return PhaseAllowed(map);
        }

        /// <summary>
        /// 本 tick 该 thing 是否放行 DoTick（三桶 Normal/Rare/Long 全走 DoTick，一处覆盖）。
        /// 热路径刻意极简：集合空即早退（100%/无降频图时零成本）；快速区白名单在相位判定之前。
        /// </summary>
        public static bool ShouldThingTick(Thing thing)
        {
            if (throttledMaps.Count == 0) return true;
            var map = thing.Map;
            if (map == null || !throttledMaps.Contains(map)) return true;
            if (fastRegions.TryGetValue(map, out var fr) && fr.cells != null && fr.cells.Contains(thing.Position)) return true;
            // Rare/Long 桶按调用序号门控（2026-08-27 两轮修正，勿删）：这两个桶的 DoTick 只在
            // (TicksGame % 250/2000)==hash 的 tick 被调用——第一版相位门控 (TicksGame+mapId)%N==0
            // 与它 gcd 混叠（同 hash 分野的 thing 永不命中 = 整个冻死）；第二版整体豁免又被实测
            // 否决（0%+fastZone 0 下 calls 不降反稳：野外图数万植物全是 Rare 桶，~15k calls/window，
            // 违背"0% = 全图凝固"预期）。正解 = 对 t/bucketInterval（调用序号，每次调用 +1）取模
            // ——序号严格递增，无混叠可言，每个 thing 恒好每 N 次调用放行 1 次。
            var tt = thing.def.tickerType;
            if (tt != TickerType.Normal)
            {
                var bucket = tt == TickerType.Long ? 2000 : 250;
                var n = IntervalTicks;
                if (n <= 1) return true;
                if (n == NeverTick) return false;
                return ((GenTicks.TicksGame / bucket) + map.uniqueID) % n == 0;
            }
            return PhaseAllowed(map);
        }

        /// <summary>相位谓词：N=1 恒真（100%）；NeverTick 恒假（0%）；否则每 N tick 一拍，uniqueID stagger。</summary>
        private static bool PhaseAllowed(Map map)
        {
            var n = IntervalTicks;
            if (n <= 1) return true;
            if (n == NeverTick) return false;
            return (GenTicks.TicksGame + map.uniqueID) % n == 0;
        }

        /// <summary>
        /// pawn 移动成本倍率（2026-08-27 动物变慢修复）：pawn 移动是纯累加器式
        /// （<c>Pawn_PathFollower.nextCellCostLeft -= CostToPayThisTick()</c>，每 tick 减、减穿换格，
        /// 无 TicksGame 差值补偿）——跳 tick 后放行 tick 也只缴 1 份成本 = 移动慢 N 倍（实测确认）。
        /// 补偿 = 放行 tick 缴 N 份（N 个真实 tick 只跑一次、一次补齐 N-1 个跳过 tick 的份额），
        /// 平均格推进速率回到满速。快速区内 pawn 每 tick 都跑 → 必须 1 倍（否则 N 倍速）；
        /// 非降频图同理 1 倍。0%（NeverTick）时 pawn 不 tick，本倍率无人消费。
        /// </summary>
        public static float MoveCostMultiplier(Thing thing)
        {
            if (throttledMaps.Count == 0) return 1f;
            var map = thing.Map;
            if (map == null || !throttledMaps.Contains(map)) return 1f;
            if (fastRegions.TryGetValue(map, out var fr) && fr.cells != null && fr.cells.Contains(thing.Position)) return 1f;
            var n = IntervalTicks;
            return n <= 1 ? 1f : n; // NeverTick 无人调用（DoTick 恒跳），返回原值无害。
        }

        /// <summary>
        /// 让地图进入降频（幂等）。governor Sweep 每轮对合资格图重入：半径变更时重建快速区；
        /// 有效相位为"永不"（0%）时结束该图声音 sustainer（防凝固图幽灵音——MapPostTick 被跳、
        /// 天气 ambientSustainer 无人推进；相位恢复后 AmbientSoundsTick 在放行 tick 自愈重启）。
        /// </summary>
        public static void Throttle(Map map, string reason)
        {
            if (map == null || map.Disposed) return;
            var radius = FastRadius;
            if (throttledMaps.Contains(map))
            {
                // 幂等重入：仅当快速区半径设置变更时重建（避免每轮 Sweep 重算几何）。
                if (fastRegions.TryGetValue(map, out var fr) && fr.radius == radius) return;
                throttledMaps.Remove(map); // 走下方完整重建路径。
            }

            throttledMaps.Add(map);
            fastRegions[map] = new FastRegion { cells = BuildFastRegion(map, radius), radius = radius };

            if (IntervalTicks == NeverTick)
            {
                map.weatherManager.EndAllSustainers();
                Find.SoundRoot.sustainerManager.EndAllInMap(map);
            }

            Log.Message($"[RimExodus] Throttle ON: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) " +
                $"at {Percent}% (N={IntervalTicks}, fastRadius={radius}) — {reason}");
        }

        /// <summary>解除降频（幂等；对未降频图无操作）。Sleep/Wake/玩家落图/设置关闭等一切升档入口调用。</summary>
        public static void Unthrottle(Map map, string reason)
        {
            if (map == null || !throttledMaps.Remove(map)) return;
            fastRegions.Remove(map);
            Log.Message($"[RimExodus] Throttle OFF: map {map.uniqueID} — {reason}");
        }

        /// <summary>销毁前清引用（<see cref="SeamlessDormancyManager.Forget"/> 桥接），防集合持已 Dispose Map。</summary>
        internal static void Forget(Map map)
        {
            if (map == null) return;
            throttledMaps.Remove(map);
            fastRegions.Remove(map);
        }

        /// <summary>
        /// 构建接缝快速区：距 void 边界 ≤ radius 的带（与预加载带同源几何，全部世界邻居方向）。
        /// radius=0 返回 null（空间分区关闭，全图按相位跳）。
        /// </summary>
        private static HashSet<IntVec3> BuildFastRegion(Map map, int radius)
        {
            if (radius <= 0) return null;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return null;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors)
            {
                neighborWorldTiles.Add(nt.tileId);
            }

            var band = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeVoidBand(map, radius, neighborWorldTiles, band);
            var cells = new HashSet<IntVec3>(band.Count);
            foreach (var kv in band)
            {
                cells.Add(kv.Key);
            }
            return cells;
        }
    }
}
