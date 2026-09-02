using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 群系连通域天气共享——"决策集中 + 执行各图"模型 + **注册制激活**（2026-09-02 第三案定稿，
    /// 用户模型 v3：报到登记 + 三种变更事件；废除一切"重算时推断激活"）。
    ///
    /// 【模型】天气域 = 同群系（PrimaryBiome）连通区域（世界图 BFS，跨群系切断，穿越无图 tile）。
    /// 每图保留**自己的** WeatherManager/SkyManager 原生实例照常执行——物理天气效果（雨湿地/积雪/
    /// 毒性）、闪电事件、环境音、CurSkyGlow 全部 per-map 原生正确（旧共享实例模型的三类缺陷就此
    /// 根治，见 doc/地图滚动休眠.md 2.5 历史）。共享的是**决策与事件**：激活图的 decider tick 做
    /// 换天决策（被动成员被 <see cref="Patches_WeatherDomain"/> 以 Priority.First 短路，含第三方
    /// prefix——被动图没有决策过程，预期行为）；换天/禁雨经 TransitionTo 广播 / DisableRainFor
    /// 转发全域同 tick 执行（Priority.Last 让第三方取消型 prefix 先跑）。
    ///
    /// 【注册制（第三案，勿回退为重算解析激活）】激活控制器 = 一份持久记录（<see cref="activeTiles"/>，
    /// 每域一条，随档保存）。每张地图在生成完成时**报到**（<see cref="BindMap"/>——家园/地块图/
    /// 营地/原生据点全部经过它，生成串行保证报到有序）：
    /// - 区域**已有记录** → 新图被动（decider 门控）+ 从激活图同步天气状态；
    /// - 区域**无记录** → 登记区域、新图自己当选（区域第一张图天然当选，保留自身天气——无选择规则）。
    /// 记录只在三种事件变更，**其余一律不动**：
    /// ① 报到（见上）；② 激活图停摆（休眠/删除/0% 凝固 = 等价休眠）→ 还活着成员按最小编号接任
    /// （活源交接换天进度 duration/禁雨窗口；全停摆不动，首个唤醒者接任）；③ 强制天气注册到某图
    /// （RegisterCondition postfix 识别 ForcedWeather() != null）→ 该图接任（交接）。
    /// 读档重建 = 记录编号与重算区域做字面 in 判断；指向已删图的条目清除；无记录区域最小编号兜底
    /// + 全新随机窗口（防被动期陈旧 duration 立即误判轮换到期）。
    ///
    /// 【历史三案教训（皆由"推断激活"产生，注册制下结构性不可能，勿复活推断）】① 新图最小编号抢
    /// 已有域激活且零交接；② 新域首成时新图把随机 StartInitialWeather 复制给老图（10534 晴天冲掉
    /// 103177 暴雨——日志 Join "normalized N members from self" 定案）；③ 三源解析/首成特判补丁链。
    ///
    /// 【fail-open】不在域缓存中的图（口袋图/空间层图/第三方创建未报到图）一切 patch 放行原版。
    ///
    /// 【注册】Game.FillComponents 反射自动实例化所有 GameComponent 子类，无需 XML def。
    /// </summary>
    public class SeamlessWeatherClusterManager : GameComponent
    {
        /// <summary>一个天气域的运行时状态（成员 + 激活图）。单图区域也是域（自己激活 = 原版行为）。</summary>
        internal class DomainState
        {
            public readonly List<Map> members = new List<Map>();
            public Map activeMap;
            public int activeTile;
        }

        // 成员 → 域；TransitionTo 广播 / 接任 / 唤醒校准的入口查询。
        private static readonly Dictionary<Map, DomainState> domainOf = new Dictionary<Map, DomainState>();

        // decider 实例 → 域：WeatherDeciderTick 门控的 O(1) 查询（decider.map 私有，用反查表
        // 绕开字段注入；每次重算重建，实例随读档更换、重建总在其后）。
        private static readonly Dictionary<WeatherDecider, DomainState> deciderDomain =
            new Dictionary<WeatherDecider, DomainState>();

        // decider 私有字段（接任交接时拷贝，防陈旧 duration 立刻误判轮换到期）。
        private static readonly AccessTools.FieldRef<WeatherDecider, int> CurWeatherDurationRef =
            AccessTools.FieldRefAccess<WeatherDecider, int>("curWeatherDuration");
        private static readonly AccessTools.FieldRef<WeatherDecider, int> RainAllowedAgainRef =
            AccessTools.FieldRefAccess<WeatherDecider, int>("ticksWhenRainAllowedAgain");

        // TransitionTo 广播重入标志（正确性单点：转发对成员的 TransitionTo 会再进 prefix，
        // 必须放行原体不再广播——漏防 = 递归广播，见 Patches_WeatherDomain 注释）。
        private static bool broadcasting;

        // DisableRainFor 转发重入标志（同上）。
        private static bool forwardingRain;

        // 激活记录本体（2026-09-02 第三案）：每域一条激活 tile，运行时只在三种事件写入、随档保存。
        // 存档键 weatherDomainActiveTiles（沿用旧键，存档兼容）。
        private static HashSet<int> activeTiles = new HashSet<int>();

        // ExposeData 的 List 中转（Scribe_Collections 需要 ref List<int>）。
        private static List<int> savedActiveTiles = new List<int>();

        // 决策门控翻转日志的上次判定缓存（logWeather 诊断埋点）：只在判定变化时打一拍
        // （重算/接任时各一次，无每 tick spam）。弱键同幂等守卫先例，不阻碍 GC。
        private class BoolBox
        {
            public bool has;
            public bool v;
        }

        private static readonly ConditionalWeakTable<WeatherDecider, BoolBox> LastGateVerdictOf =
            new ConditionalWeakTable<WeatherDecider, BoolBox>();

        public SeamlessWeatherClusterManager(Game game)
        {
            // 每次游戏实例化（新档/读档）都清空：读档随后由 ExposeData 回填，新档从零登记。
            activeTiles = new HashSet<int>();
            savedActiveTiles = new List<int>();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                savedActiveTiles.Clear();
                savedActiveTiles.AddRange(activeTiles);
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Save: weatherDomainActiveTiles=[{string.Join(",", savedActiveTiles)}]");
            }
            Scribe_Collections.Look(ref savedActiveTiles, "weatherDomainActiveTiles", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                activeTiles = savedActiveTiles != null ? new HashSet<int>(savedActiveTiles) : new HashSet<int>();
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Load: weatherDomainActiveTiles=[{string.Join(",", activeTiles)}] (resolved at FinalizeInit)");
            }
        }

        public override void FinalizeInit()
        {
            // 读档/新档后：重算成员结构 + 记录落位（in 判断——记录编号落在哪片区域即哪片的激活）。
            RebindAll();
        }

        /// <summary>
        /// 报到（生成 onComplete / SetupNativeParentMap / 营地生成后调用；每图一次）。
        /// 区域已有记录 → 本图被动 + 从激活图同步天气（cur/last/age）；区域无记录 → 登记区域、
        /// 本图当选激活（保留自身天气——区域第一张图天然当选，注册制核心，勿加选择规则）。
        /// </summary>
        public static void BindMap(Map map)
        {
            if (map == null || map.Disposed) return;
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (tile < 0 || Find.WorldGrid == null) return;

            RebuildDomains(map);
            if (!domainOf.TryGetValue(map, out var d))
            {
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Join: wt={tile} excluded (no surface world tile) — vanilla semantics");
                return;
            }
            if (d.activeMap == map)
            {
                // 区域无记录时 RebuildDomains 已登记本图（reporting 分支）——区域第一张图当选。
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Join: wt={tile} registered as ACTIVE of its domain (keeps own weather " +
                    $"cur={map.weatherManager.curWeather?.defName} age={map.weatherManager.curWeatherAge})");
            }
            else
            {
                SyncWeatherState(d.activeMap, map);
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Join: wt={tile} passive, synced from active wt={d.activeTile} " +
                    $"(cur={d.activeMap.weatherManager.curWeather?.defName} age={d.activeMap.weatherManager.curWeatherAge})");
            }
        }

        /// <summary>重算成员结构 + 记录落位（读档后 / 任何图移除后）。成员归属是纯结构（现算），
        /// 激活只查记录、绝不推断。</summary>
        public static void RebindAll()
        {
            RebuildDomains(null);
        }

        /// <summary>
        /// 图恢复模拟（休眠唤醒 / 解除降频）时的校准：激活图有效 → 从其同步（休眠/降频期 age 落后，
        /// 换天广播归零自愈 + 两次换天间漂移在此补齐）；激活图失效（被删/仍在睡/仍凝固）→ 本图接任
        /// （保留自身状态——事件②的补充检测点，非新事件）。
        /// </summary>
        internal static void OnMapResumed(Map map)
        {
            if (map == null || map.Disposed || !domainOf.TryGetValue(map, out var d)) return;
            var a = d.activeMap;
            if (a == null || a.Disposed)
            {
                TransferActivation(d, map, "active unavailable, resumed member takes over", handover: false);
                return;
            }
            if (a != map && (SeamlessDormancyManager.IsDormant(a) || SeamlessTickThrottle.IsFrozen(a)))
            {
                TransferActivation(d, map, "active suspended, resumed member takes over", handover: false);
                return;
            }
            if (a != map)
            {
                SyncWeatherState(a, map);
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Resume: wt={SeamlessTileRegistry.GetMapWorldTile(map)} calibrated from active wt={d.activeTile} " +
                    $"(cur={a.weatherManager.curWeather?.defName} age={a.weatherManager.curWeatherAge})");
            }
        }

        /// <summary>
        /// 停摆通知（事件②入口）：休眠 / 降频 0% 凝固时调用——本图是激活图则换还活着的成员接任
        /// （非休眠且非凝固成员中最小编号；全停摆则不动，域冻结，首个唤醒者经 OnMapResumed 接任）。
        /// </summary>
        internal static void NotifySimulationSuspended(Map map, string reason)
        {
            if (map == null || map.Disposed || !domainOf.TryGetValue(map, out var d) || d.activeMap != map) return;

            Map best = null;
            var bestTile = int.MaxValue;
            for (int i = 0; i < d.members.Count; i++)
            {
                var m = d.members[i];
                if (m == map || m.Disposed || SeamlessDormancyManager.IsDormant(m) || SeamlessTickThrottle.IsFrozen(m)) continue;
                var t = SeamlessTileRegistry.GetMapWorldTile(m);
                if (t < bestTile)
                {
                    bestTile = t;
                    best = m;
                }
            }
            if (best == null)
            {
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Suspend: wt={SeamlessTileRegistry.GetMapWorldTile(map)} is ACTIVE but no live member " +
                    $"({reason}) — domain frozen until a member wakes");
                return;
            }
            TransferActivation(d, best, reason, handover: true);
        }

        /// <summary>decider tick 门控（Patch_WeatherDecider_WeatherDeciderTick 消费）：无域/激活图放行。
        /// 判定翻转时打一拍诊断日志（只在重算/接任时发生；非域内 decider 静默——避免单图域刷屏）。</summary>
        internal static bool ShouldRunDeciderTick(WeatherDecider decider)
        {
            deciderDomain.TryGetValue(decider, out var d);
            var run = d == null || d.activeMap == null || d.activeMap.weatherDecider == decider;

            var box = LastGateVerdictOf.GetOrCreateValue(decider);
            if (!box.has || box.v != run)
            {
                box.has = true;
                box.v = run;
                if (d != null && d.activeMap != null)
                {
                    // 本图 tile：成员中 decider == 本实例者（翻转时才跑一次小循环，非热路径）。
                    var own = -1;
                    for (int i = 0; i < d.members.Count; i++)
                    {
                        if (d.members[i].weatherDecider == decider)
                        {
                            own = SeamlessTileRegistry.GetMapWorldTile(d.members[i]);
                            break;
                        }
                    }
                    RimExodusLog.Message(RimExodusLogModule.Weather,
                        $"Gate: wt={own} decider {(run ? "ACTIVE" : "PASSIVE")} (domain active wt={d.activeTile})");
                }
            }
            return run;
        }

        /// <summary>
        /// 换天广播（Patch_WeatherManager_TransitionTo 消费，Priority.Last——第三方取消型 prefix
        /// 先于我们执行，取消时本方法不会被调，全域一致地不换）。对域内其余成员同 tick 各调一次
        /// TransitionTo(同一 WeatherDef)；重入（转发的成员调用）直接返回放行原体。
        /// </summary>
        internal static void BroadcastTransitionIfShared(WeatherManager origin, WeatherDef newWeather)
        {
            if (broadcasting) return;
            var map = origin.map; // WeatherManager.map 是 public 字段（实例构造时钉死 = 本图，形态 B 下不被替换）。
            if (map == null || map.Disposed) return;
            if (!domainOf.TryGetValue(map, out var d)) return;

            var log = RimExodusLog.Enabled(RimExodusLogModule.Weather);
            var sb = log ? new StringBuilder() : null;
            if (log) sb.Append($"Broadcast: origin wt={SeamlessTileRegistry.GetMapWorldTile(map)} weather={newWeather?.defName} -> [");

            broadcasting = true;
            try
            {
                var first = true;
                for (int i = 0; i < d.members.Count; i++)
                {
                    var m = d.members[i];
                    if (m == map || m.Disposed || m.weatherManager == origin)
                    {
                        if (log && m != map) sb.Append($"{(first ? "" : ",")}(skip wt={SeamlessTileRegistry.GetMapWorldTile(m)}: disposed)");
                        continue;
                    }
                    m.weatherManager.TransitionTo(newWeather);
                    if (log)
                    {
                        sb.Append($"{(first ? "" : ",")}{SeamlessTileRegistry.GetMapWorldTile(m)}");
                        first = false;
                    }
                }
            }
            finally
            {
                broadcasting = false;
            }
            if (log) RimExodusLog.Message(RimExodusLogModule.Weather, sb.Append("]").ToString());
        }

        /// <summary>
        /// 禁雨转发（Patch_WeatherDecider_DisableRainFor 消费，Priority.Last）：把禁雨窗口写到域内
        /// 全部成员的 decider（ticksWhenRainAllowedAgain 基于全局 TicksGame，同 tick 转发值一致）。
        /// 重入（被转发的成员调用）直接返回。
        /// </summary>
        internal static void ForwardDisableRainIfShared(WeatherDecider origin, int ticks)
        {
            if (forwardingRain) return;
            if (!deciderDomain.TryGetValue(origin, out var d)) return;

            forwardingRain = true;
            try
            {
                for (int i = 0; i < d.members.Count; i++)
                {
                    var m = d.members[i];
                    if (m.Disposed || m.weatherDecider == origin) continue;
                    m.weatherDecider.DisableRainFor(ticks);
                }
            }
            finally
            {
                forwardingRain = false;
            }
            RimExodusLog.Message(RimExodusLogModule.Weather,
                $"DisableRain: forwarded to domain members (ticks={ticks}, members={d.members.Count})");
        }

        /// <summary>
        /// 强制天气接任（事件③，Patch_GameConditionManager_RegisterCondition 消费）：条件持有图
        /// 接任激活（交接换天进度）。激活图的 ForcedWeather getter 原生读自己图的条件，Anomaly 的
        /// GameCondition_ForceWeather 族无需任何 getter patch；接任后不回切（条件结束 getter 自然
        /// 回 null 恢复正常轮换）。无域（口袋图等）无操作——原生语义本就正确。
        /// </summary>
        internal static void ActivateForcedWeather(Map map)
        {
            if (map == null || map.Disposed || !domainOf.TryGetValue(map, out var d)) return;
            if (d.activeMap == map) return;
            TransferActivation(d, map, "forced weather registered", handover: true);
        }

        /// <summary>
        /// 统一接任入口（事件②/③共用）：更新记录 + 打日志。handover = 从旧激活图交接（同步天气状态
        /// + 拷贝 duration/禁雨窗口——旧图停摆前/条件注册时字段皆可读，接任不跳变）；false = 接任者
        /// 保留自身状态（唤醒接任场景：旧激活图休眠多时、状态反而落后）。
        /// </summary>
        private static void TransferActivation(DomainState d, Map newActive, string reason, bool handover)
        {
            if (newActive == null || newActive.Disposed) return;
            var old = d.activeMap;
            var newTile = SeamlessTileRegistry.GetMapWorldTile(newActive);

            if (handover && old != null && !old.Disposed && old != newActive)
            {
                SyncWeatherState(old, newActive);
                CurWeatherDurationRef(newActive.weatherDecider) = CurWeatherDurationRef(old.weatherDecider);
                RainAllowedAgainRef(newActive.weatherDecider) = RainAllowedAgainRef(old.weatherDecider);
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Activate: wt={newTile} <- wt={d.activeTile} ({reason}, handover " +
                    $"dur={CurWeatherDurationRef(newActive.weatherDecider)} rainAt={RainAllowedAgainRef(newActive.weatherDecider)})");
            }
            else
            {
                RimExodusLog.Message(RimExodusLogModule.Weather,
                    $"Activate: wt={newTile} ({reason}, keeps own state)");
            }

            if (d.activeTile >= 0) activeTiles.Remove(d.activeTile);
            d.activeMap = newActive;
            d.activeTile = newTile;
            activeTiles.Add(newTile);
        }

        /// <summary>天气状态同步：cur/last/age 从源图拷到目标图（直写公开字段 + 清天空 lerp 缓存）。</summary>
        private static void SyncWeatherState(Map from, Map to)
        {
            var src = from.weatherManager;
            var dst = to.weatherManager;
            dst.curWeather = src.curWeather;
            dst.lastWeather = src.lastWeather;
            dst.curWeatherAge = src.curWeatherAge;
            dst.ResetSkyTargetLerpCache();
        }

        /// <summary>
        /// 重算成员结构 + 记录落位。成员归属 = 纯结构（同群系连通分量，含单图区域——单图域自己
        /// 激活 = 原版行为，无特判）；激活 = 只查记录（in 判断），无记录才登记：
        /// reportingMap ∈ 本分量 → 报到登记（它当选，保留自身天气——区域第一张图天然当选）；
        /// 否则无源兜底（最小编号 + 全新随机窗口，防被动期陈旧 duration 立即误判轮换到期）。
        /// 顺带清除指向已删图的失效记录、同分量多条目去重（防御）。
        /// </summary>
        private static void RebuildDomains(Map reportingMap)
        {
            var log = RimExodusLog.Enabled(RimExodusLogModule.Weather);
            var logSb = log ? new StringBuilder() : null;
            if (log) logSb.Append("Rebuild: ");

            domainOf.Clear();
            deciderDomain.Clear();
            if (Find.WorldGrid == null) return;

            // tile → 分量 id（同群系连通）。只为有图的 tile 沿分量展开（一次 BFS 标记整分量）。
            var componentOf = new Dictionary<int, int>();
            var nextId = 0;
            var mapsByTile = new Dictionary<int, Map>();
            var skippedMaps = 0;
            foreach (var m in Find.Maps)
            {
                if (m.Disposed) continue;
                var t = SeamlessTileRegistry.GetMapWorldTile(m);
                if (t >= 0)
                {
                    mapsByTile[t] = m;
                    continue;
                }
                skippedMaps++;
                if (log) logSb.Append($"(skip map {m.uniqueID}: no surface world tile) ");
            }
            if (log) logSb.Append($"maps={mapsByTile.Count} ");

            // 失效记录清除（指向已删图——成员判定再也匹配不上）。
            var pruned = new List<int>();
            foreach (var t in activeTiles)
            {
                if (!mapsByTile.ContainsKey(t)) pruned.Add(t);
            }
            for (int i = 0; i < pruned.Count; i++) activeTiles.Remove(pruned[i]);
            if (log && pruned.Count > 0) logSb.Append($"(pruned records: {string.Join(",", pruned)}) ");

            foreach (var root in mapsByTile.Keys)
            {
                if (componentOf.ContainsKey(root)) continue;
                MarkBiomeComponent(root, nextId++, componentOf);
            }

            // 分量 id → 成员图列表。
            var membersOf = new Dictionary<int, List<Map>>();
            foreach (var kv in mapsByTile)
            {
                var comp = componentOf[kv.Key];
                if (!membersOf.TryGetValue(comp, out var list))
                    membersOf[comp] = list = new List<Map>();
                list.Add(kv.Value);
            }

            foreach (var kv in membersOf)
            {
                var members = kv.Value;
                var d = new DomainState();
                d.members.AddRange(members);

                // 记录落位（in 判断）：本分量内的记录条目（多条防御取最小并去重）。
                var activeTile = int.MaxValue;
                foreach (var t in activeTiles)
                {
                    if (mapsByTile.ContainsKey(t) && componentOf[t] == kv.Key && t < activeTile) activeTile = t;
                }
                string source;
                if (activeTile != int.MaxValue)
                {
                    source = "record";
                    var snapshot = new List<int>(activeTiles);
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        var t = snapshot[i];
                        if (t != activeTile && mapsByTile.ContainsKey(t) && componentOf[t] == kv.Key)
                            activeTiles.Remove(t);
                    }
                }
                else if (reportingMap != null && members.Contains(reportingMap))
                {
                    // 报到登记（事件①）：区域无记录，报到的图当选——区域第一张图天然当选，
                    // 保留自身天气（第三案核心：新图的随机 StartInitialWeather 永远不会覆盖别人）。
                    activeTile = SeamlessTileRegistry.GetMapWorldTile(reportingMap);
                    source = "reporting";
                    activeTiles.Add(activeTile);
                }
                else
                {
                    // 无源兜底（删图后重算 / 读档缺记录的极罕见态）：最小编号 + 全新随机窗口。
                    foreach (var mkv in mapsByTile)
                    {
                        if (componentOf[mkv.Key] != kv.Key || mkv.Key >= activeTile) continue;
                        activeTile = mkv.Key;
                    }
                    source = "fallback";
                    activeTiles.Add(activeTile);
                    var am = mapsByTile[activeTile];
                    CurWeatherDurationRef(am.weatherDecider) =
                        am.weatherManager.curWeatherAge + am.weatherManager.curWeather.durationRange.RandomInRange;
                }

                d.activeTile = activeTile;
                d.activeMap = mapsByTile[activeTile];
                foreach (var m in members)
                {
                    domainOf[m] = d;
                    deciderDomain[m.weatherDecider] = d;
                }

                if (log)
                {
                    logSb.Append("comp[");
                    for (int i = 0; i < members.Count; i++)
                    {
                        if (i > 0) logSb.Append(',');
                        logSb.Append(SeamlessTileRegistry.GetMapWorldTile(members[i]));
                    }
                    logSb.Append($"] active={activeTile} ({source}) ");
                }
            }
            if (log) RimExodusLog.Message(RimExodusLogModule.Weather, logSb.ToString().TrimEnd());
        }

        /// <summary>从 root 出发 BFS 标记同群系连通分量（result[tile]=id；跨群系切断；穿越无图 tile）。</summary>
        private static void MarkBiomeComponent(int root, int id, Dictionary<int, int> result)
        {
            var biome = Find.WorldGrid[new PlanetTile(root)].PrimaryBiome;
            var queue = new Queue<int>();
            queue.Enqueue(root);
            result[root] = id;
            var neighbors = new List<PlanetTile>();
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                Find.WorldGrid.GetTileNeighbors(cur, neighbors);
                foreach (var n in neighbors)
                {
                    var nt = n.tileId;
                    if (result.ContainsKey(nt)) continue;
                    if (Find.WorldGrid[new PlanetTile(nt)].PrimaryBiome != biome) continue; // 跨群系切断传递
                    result[nt] = id;
                    queue.Enqueue(nt);
                }
            }
        }
    }
}
