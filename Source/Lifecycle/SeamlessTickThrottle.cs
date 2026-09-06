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
    /// 【0% 凝固 = 摘 thing tick 表（2026-09 性能修复，勿回退为"只靠 DoTick 门控"）】实测
    /// 6 邻图全凝固时每 tick 仍有 ~1ms 花在"跳过判定"本身：DoTick 的 Harmony Prefix+Postfix
    /// 调度与 <see cref="ShouldThingTick"/> 对被跳过的 thing 照跑（6 图 × ~100 thing/tick ≈ 620 次
    /// 空转调用，落在剖析器 unaccounted 桶）。0% 语义本就是"DoTick 一个不跑"——直接
    /// RemoveAllFromMap 摘表是纯等价变换（显示/邻接口径不动，仍走活跃图路径），把空转也省掉。
    /// 摘/注册簿记与 Sleep/Wake 精确互斥（TickList.RegisterThing 无去重，双注册 = 活物双 tick）：
    /// ①摘表来源唯一标记 = <see cref="tickSuspendedMaps"/>；②恢复注册唯一实现 =
    /// <see cref="RestoreThingTicks"/>（Wake 的重注册段收口于此，含影子陈旧条目的 Map 过滤）；
    /// ③Sleep 调 Unthrottle 时传 restoreTicks:false（图即将全摘，恢复无意义且会与 Wake 双注册）；
    /// ④Unthrottle 恢复前查 IsDormant（防御路径：图已休眠则注册归 Wake 独责）。
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
    /// 【设置缓存（2026-09 热路径收口，勿回退为逐判定现读）】百分比/半径/N 由 governor
    /// GameComponentTick 每 tick 刷新一次进 static 缓存（<see cref="RefreshSettingsCache"/>）——
    /// 滑条即时性损失 ≤1 tick，换来 ShouldThingTick/MoveCostMultiplier（每 thing 每 tick）不再
    /// 各自做两次设置属性读 + Clamp。Sweep 的周期应用与即时解除入口本就经 Throttle/Unthrottle
    /// 低频路径，不受影响。
    ///
    /// 【不序列化】与休眠状态同口径：纯运行时性能状态，读档后 governor 首轮 Sweep 重新收敛。
    /// 状态集合每轮 Sweep 全量重算（Throttle/Unthrottle 幂等）。
    /// </summary>
    public static class SeamlessTickThrottle
    {
        private class MapThrottleState
        {
            public HashSet<IntVec3> fastCells;
            public int radius;
        }

        /// <summary>降频中的地图与其快速区（运行时状态，刻意不序列化——见类注释）。</summary>
        private static readonly Dictionary<Map, MapThrottleState> throttled = new Dictionary<Map, MapThrottleState>();

        /// <summary>0% 凝固时已从全局 tick 表摘除 thing 的图（摘表来源标记——恢复注册的唯一依据，
        /// 与 Sleep 的摘表（dormant 集合即其标记）互斥去重）。</summary>
        private static readonly HashSet<Map> tickSuspendedMaps = new HashSet<Map>();

        /// <summary>百分比 ≤0 时的相位哨兵：永不放行（= 可见的凝固，等价休眠但保留显示与邻接）。</summary>
        private const int NeverTick = int.MaxValue;

        // 设置缓存（每 tick 由 governor 刷新一次；初值 = 各设置默认值，首个 tick 内校正）。
        private static int cachedPercent = 50;
        private static int cachedN = 2;
        private static int cachedFastRadius = 15;

        /// <summary>降频功能是否开启（百分比 &lt; 100 即开启；100 = 原生行为，活跃圈空图全速）。</summary>
        public static bool ThrottleEnabled => cachedPercent < 100;

        /// <summary>每 tick 刷新设置缓存（governor GameComponentTick 最前调用；热路径读缓存见类注释）。</summary>
        internal static void RefreshSettingsCache()
        {
            cachedPercent = Mathf.Clamp(RimExodusMod.Settings?.dormancyThrottlePercent ?? 50, 0, 100);
            cachedN = cachedPercent >= 100 ? 1
                : cachedPercent <= 0 ? NeverTick
                : System.Math.Max(2, Mathf.CeilToInt(100f / cachedPercent));
            cachedFastRadius = Mathf.Clamp(RimExodusMod.Settings?.throttleSeamFastRadius ?? 15, 0, 50);
        }

        /// <summary>map 是否处于降频（null/Disposed 安全返回 false）。</summary>
        public static bool IsThrottled(Map map)
        {
            return map != null && !map.Disposed && throttled.ContainsKey(map);
        }

        /// <summary>是否处于 0% 凝固（throttled 且永不放行）——"等价休眠"判定（天气域激活接任等消费）。</summary>
        internal static bool IsFrozen(Map map)
        {
            return map != null && throttled.ContainsKey(map) && cachedN == NeverTick;
        }

        /// <summary>该降频图快速区格数（0 = 无快速区/未降频；剖析报告用——0% 档下 calls 即来自这些格）。</summary>
        public static int FastRegionCellCount(Map map)
        {
            return map != null && throttled.TryGetValue(map, out var st) && st.fastCells != null ? st.fastCells.Count : 0;
        }

        /// <summary>
        /// 本 tick 该图是否放行图级模拟（MapPreTick/MapPostTick 门控）。非降频图恒 true。
        /// 与 thing 门控同相位（同一谓词），但不含快速区白名单——图级系统不分区（已知限制）。
        /// </summary>
        public static bool MapSimulatesThisTick(Map map)
        {
            if (throttled.Count == 0 || !throttled.ContainsKey(map)) return true;
            var n = cachedN;
            return n <= 1 || (n != NeverTick && (GenTicks.TicksGame + map.uniqueID) % n == 0);
        }

        /// <summary>
        /// 本 tick 该 thing 是否放行 DoTick（三桶 Normal/Rare/Long 全走 DoTick，一处覆盖）。
        /// 热路径刻意极简（2026-09 两轮收口）：集合空即早退（100%/无降频图时零成本）；降频图
        /// 单次字典查询拿到快速区；相位 N 读 static 缓存（旧版逐次现读设置 ×2 + Clamp）。
        /// </summary>
        public static bool ShouldThingTick(Thing thing)
        {
            if (throttled.Count == 0) return true;
            var map = thing.Map;
            if (map == null || !throttled.TryGetValue(map, out var st)) return true;
            if (st.fastCells != null && st.fastCells.Contains(thing.Position)) return true;
            var n = cachedN;
            if (n <= 1) return true;
            if (n == NeverTick) return false;
            // Rare/Long 桶按调用序号门控（2026-08-27 两轮修正，勿删）：这两个桶的 DoTick 只在
            // (TicksGame % 250/2000)==hash 的 tick 被调用——第一版相位门控 (TicksGame+mapId)%N==0
            // 与它 gcd 混叠（同 hash 分野的 thing 永不命中 = 整个冻死）；第二版整体豁免又被实测
            // 否决（0%+fastZone 0 下 calls 不降反稳：野外图数万植物全是 Rare 桶，~15k calls/window，
            // 违背"0% = 全图凝固"预期）。正解 = 对 t/bucketInterval（调用序号，每次调用 +1）取模
            // ——序号严格递增，无混叠可言，每个 thing 恒好每 N 次调用放行 1 次。
            var tt = thing.def.tickerType;
            if (tt == TickerType.Normal) return (GenTicks.TicksGame + map.uniqueID) % n == 0;
            var bucket = tt == TickerType.Long ? 2000 : 250;
            return ((GenTicks.TicksGame / bucket) + map.uniqueID) % n == 0;
        }

        /// <summary>
        /// pawn 移动成本倍率（2026-08-27 动物变慢修复）：pawn 移动是纯累加器式
        /// （<c>Pawn_PathFollower.nextCellCostLeft -= CostToPayThisTick()</c>，每 tick 减、减穿换格，
        /// 无 TicksGame 差值补偿）——跳 tick 后放行 tick 也只缴 1 份成本 = 移动慢 N 倍（实测确认）。
        /// 补偿 = 放行 tick 缴 N 份（N 个真实 tick 只跑一次、一次补齐 N-1 个跳过 tick 的份额），
        /// 平均格推进速率回到满速。快速区内 pawn 每 tick 都跑 → 必须 1 倍（否则 N 倍速）；
        /// 非降频图同理 1 倍。0%（NeverTick）时 thing tick 已整表摘除，本倍率无人消费。
        /// </summary>
        public static float MoveCostMultiplier(Thing thing)
        {
            if (throttled.Count == 0) return 1f;
            var map = thing.Map;
            if (map == null || !throttled.TryGetValue(map, out var st)) return 1f;
            if (st.fastCells != null && st.fastCells.Contains(thing.Position)) return 1f;
            return cachedN <= 1 ? 1f : cachedN; // NeverTick 无人调用（表已摘），返回原值无害。
        }

        /// <summary>
        /// 让地图进入降频（幂等）。governor Sweep 每轮对合资格图重入：半径**或摘表态**变更时
        /// 完整重建（后者覆盖"0% → 拖回 50%"的设置变更路径——恢复 thing tick 注册再按新相位跑）；
        /// 有效相位为"永不"（0%）时**摘除全局 thing tick 表**（见类注释【0% 凝固】节）并结束该图
        /// 声音 sustainer（防凝固图幽灵音——MapPostTick 被跳、天气 ambientSustainer 无人推进；
        /// 相位恢复后 AmbientSoundsTick 在放行 tick 自愈重启）。
        /// </summary>
        public static void Throttle(Map map, string reason)
        {
            if (map == null || map.Disposed) return;
            var suspend = cachedN == NeverTick;
            if (throttled.TryGetValue(map, out var existing))
            {
                // 幂等重入：半径与摘表态均未变 = 纯 no-op（避免每轮 Sweep 重算几何/重摘表）。
                if (existing.radius == cachedFastRadius && tickSuspendedMaps.Contains(map) == suspend) return;
                if (tickSuspendedMaps.Remove(map)) RestoreThingTicks(map);
                throttled.Remove(map); // 走下方完整重建路径。
            }

            throttled[map] = new MapThrottleState { fastCells = BuildFastRegion(map, cachedFastRadius), radius = cachedFastRadius };

            if (suspend)
            {
                SuspendThingTicks(map);
            }

            Log.Message($"[RimExodus] Throttle ON: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) " +
                        $"at {cachedPercent}% (N={cachedN}, fastRadius={cachedFastRadius}, thingTicks={(suspend ? "suspended" : "registered")}) — {reason}");
        }

        /// <summary>
        /// 解除降频（幂等；对未降频图无操作）。Sleep/Wake/玩家落图/设置关闭等一切升档入口调用。
        /// restoreTicks = false 仅供 Sleep（图即将整表全摘，恢复注册无意义且会与 Wake 的重注册
        /// 双注册——TickList.RegisterThing 无去重）；其余入口默认恢复。图已休眠（防御路径）时
        /// 注册归 Wake 独责，本方法只清标记。
        /// </summary>
        public static void Unthrottle(Map map, string reason, bool restoreTicks = true)
        {
            if (map == null || !throttled.Remove(map)) return;
            if (tickSuspendedMaps.Remove(map))
            {
                if (restoreTicks && !SeamlessDormancyManager.IsDormant(map))
                {
                    RestoreThingTicks(map);
                }
            }

            // 天气状态校准（2026-09 形态 B）：降频期本图 curWeatherAge 以 1/N 速度落后于激活图，
            // 恢复全速前补齐（被动成员从激活图同步 cur/last/age）。
            SeamlessWeatherClusterManager.OnMapResumed(map);
            Log.Message($"[RimExodus] Throttle OFF: map {map.uniqueID} — {reason}");
        }

        /// <summary>销毁前清引用（<see cref="SeamlessDormancyManager.Forget"/> 桥接），防集合持已 Dispose Map。</summary>
        internal static void Forget(Map map)
        {
            if (map == null) return;
            throttled.Remove(map);
            tickSuspendedMaps.Remove(map); // 图将销毁，只清标记不注册。
        }

        /// <summary>
        /// 摘除该图全部 thing 的全局 tick 注册（0% 凝固用；幂等——标记已存在则无操作，
        /// 防 Sleep/Throttle 双摘交错）。配套结束声音 sustainer 与天气域停摆通知
        /// （0% = 等价休眠的两侧语义）。
        /// </summary>
        private static void SuspendThingTicks(Map map)
        {
            if (!tickSuspendedMaps.Add(map)) return;
            Find.TickManager.RemoveAllFromMap(map);
            map.weatherManager.EndAllSustainers(); // 本图自己的实例（形态 B），无跨图影响。
            Find.SoundRoot.sustainerManager.EndAllInMap(map);
            // 0% 凝固 = 等价休眠（2026-09-02 天气域注册制事件②）：若本图是域激活图 → 接任。
            SeamlessWeatherClusterManager.NotifySimulationSuspended(map, "tick rate 0 (frozen)");
        }

        /// <summary>
        /// 恢复该图 thing 的全局 tick 注册——Wake 重注册段的收口实现（Sleep 摘表与 0% 凝固摘表
        /// 两个来源共用；调用方负责保证只调一次——Sleep 摘的由 Wake 调、0% 摘的由 Unthrottle 调，
        /// 经 tickSuspendedMaps/dormant 标记互斥）。
        /// Map 守卫（2026-08 持有链审计）：innerList 可能残留"已活在别图"的陈旧条目（影子注入期
        /// DeSpawn 的 Remove 静默失败残留），重注册会造成 TickList 双注册 = 活人双 tick。
        /// 影子系统的 60t 轮询清扫为主，此处按 Map 过滤兜底。
        /// </summary>
        internal static void RestoreThingTicks(Map map)
        {
            var spawned = map.spawnedThings;
            for (var i = 0; i < spawned.Count; i++)
            {
                if (spawned[i].Map != map) continue;
                Find.TickManager.RegisterAllTickabilityFor(spawned[i]);
            }
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
