using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图滚动休眠调度器（用户定夺 2026-08）。
    ///
        /// 【距离策略】源 = 所有玩家阵营 spawned pawn 所在图的 tile（含玩家家园——玩家在家时家园即源）
        /// ∪ 玩家阵营远行队所在 tile（2026-08：用户原则"距离对全部 pawn 成立"——远行队里的 pawn
        /// 也在世界网格上占一个 tile）。世界网格 BFS 跳数（GetTileNeighbors，纯拓扑、不依赖图加载
        /// 状态）；每轮现算、无维护状态——源每刻在动（pawn 跨缝 / caravan 逐 tile 移动），任何预算好
        /// 的距离表写完即过期，实时查询不可能脏：
    /// - Find.CurrentMap 无条件保活（玩家正看着的图冻结会卡 UI）；
    /// - 图上有玩家 pawn → 距离 0（"仅玩家 pawn 保护"——玩家踏入即源，天然防跨图躲追击）；
    /// - **玩家家园（<see cref="SeamlessMapGovernance.IsProtectedHome"/> = 原版 IsPlayerHome，
    ///   2026-08 用户定夺"不休眠不删除"）→ 永不休眠、永不删除**。覆盖开局家园/定居/逆重飞船
    ///   降落产生的原生 Settlement 与建了引力引擎的图（含地块图营地）。家园特权判定全项目
    ///   唯一行为消费点 = 本类保活分支；
    /// - 距离 &lt; sleepHops（默认 2）→ 活跃（距离 0/1）；其中**无玩家 pawn 的空图**按百分比降频
    ///   tick（2026-08 分级休眠中间档，<see cref="SeamlessTickThrottle"/>——接缝快速区内全速）；
    /// - 距离 ≥ sleepHops → 休眠（软休眠：内容完好，见 <see cref="SeamlessDormancyManager"/>）；
    /// - 距离 ≥ deleteHops（默认 3）且可删（<see cref="SeamlessMapGovernance.CanRollingDelete"/>）
    ///   → 删除（<see cref="SeamlessTileManager.RemoveRollingMap"/>：地块图销毁 Map+WorldObject，
    ///   "从未出现过"重建；原生家族延迟执行原版删除偏好——Settlement 删图留对象等。
    ///   解除 Game.AddMap 127 图上限的实际压力）。
    ///
    /// 【管辖范围】<see cref="SeamlessMapGovernance.IsGoverned"/> = 地块图（MapParent_SeamlessTile）
    /// ∪ 原生家族（原版"全员离开即删图"的 Settlement/Site/Camp/CaravansBattlefield/
    /// DestroyedSettlement——被动删除由 <see cref="Patches_NativeMapFamily"/> 拦截，删除时机
    /// 移交本 governor）。判定归一层单点，上层勿自写 parent 类型特判（历史教训：IsAnchorMap
    /// 时代特权判定散落、口径漂移，造成家园"睡而不删"的不对称）。
    ///
    /// 【注册】Game.FillComponents 反射自动实例化所有 GameComponent 子类，无需 XML def。
    ///
    /// 【读档语义】休眠状态不序列化：读档后全活跃，本 governor 首轮扫描（TicksGame 从 0 计，
    /// nextSweepTick=0 立即触发）重新收敛远图。
    /// </summary>
    public class SeamlessDormancyGovernor : GameComponent
    {
        /// <summary>扫描间隔下限（ticks）：UI 滑条以秒为单位，最小 1 秒。</summary>
        private const int MinSweepIntervalTicks = 60;

        /// <summary>下次扫描的 game tick（不序列化：读档后立即首轮扫描，见类注释）。</summary>
        private int nextSweepTick;

        /// <summary>威胁追踪轮询间隔下限（ticks，2026-09 威胁保活）：UI 滑条以秒为单位，最小 1 秒。</summary>
        private const int MinThreatPollIntervalTicks = 60;

        /// <summary>
        /// 活跃威胁保活追踪名单（2026-09，实例字段不序列化——随 Game 重建天然换档干净，读档后
        /// 首轮 Sweep 重新发现注册）。Sweep 对非休眠图跑 <see cref="SeamlessMapGovernance.HasActiveThreat"/>
        /// 发现敌人 → 注册 + Unthrottle（豁免降频/休眠/删除 = 保活第四条）；独立轮询段按
        /// <c>threatKeepalivePollIntervalTicks</c> 复查，敌人清零/图休眠/销毁 → 注销，回落交给
        /// 下轮 Sweep 按距离收敛（0% 图重新摘表走既有幂等路径）。**休眠图刻意不注册**（休眠冻结
        /// 防级联语义保留——袭击打不进休眠图，唯一残留 = 玩家撤离留追兵后图休眠，冻着属预期）。
        /// 消费方 = 保活分支 + 告警（Alert_ThreatKeepalive 读快照）。
        /// </summary>
        private readonly HashSet<Map> threatKeepaliveMaps = new HashSet<Map>();

        /// <summary>下次威胁轮询的 game tick（名单空时不动，零常驻成本）。</summary>
        private int nextThreatPollTick;

        /// <summary>追踪名单只读快照（告警 <see cref="Alert_ThreatKeepalive"/> 消费；拷贝防遍历中变异）。</summary>
        public List<Map> ThreatKeepaliveSnapshot()
        {
            var list = new List<Map>(threatKeepaliveMaps.Count);
            foreach (var m in threatKeepaliveMaps)
            {
                if (m != null && !m.Disposed) list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// 手动休眠锁的持久化面（2026-08，用户要求存档保留）：存世界 tile id 而非 Map 引用——
        /// 手动休眠对任意受管辖图（地块图 ∪ 原生家族 Settlement 等）均可用，挂 MapParent_SeamlessTile
        /// 会漏原生家族。运行时真值仍是 <see cref="SeamlessDormancyManager"/> 的静态集合，本集合由
        /// Manager 的 Sleep(manual)/Wake/Forget 桥接增删；读档后图全活跃（休眠不序列化），首轮 Sweep
        /// 对 tile ∈ 本集合的图 Sleep 时传 manual:true，锁跨档续期。
        /// </summary>
        private HashSet<int> manualDormantTiles = new HashSet<int>();

        /// <summary>
        /// 前哨封存序号分配器（2026-09 前哨保留，持久化）：每次封存递增，写入记录的 archiveOrdinal
        /// ——淘汰权重的"新近度序号"由它派生（1 = 最近封存，越大越旧）。
        /// </summary>
        private int nextArchiveOrdinal;

        /// <summary>封存询问弹窗队列（会话态，不序列化——弹窗上下文是活 Map 引用）。一次弹一张。</summary>
        private sealed class PreservePrompt
        {
            public Map map;
            public MapParent_SeamlessTile parent;
            public int tile;
            public int homeCells;
        }

        private readonly List<PreservePrompt> preservePrompts = new List<PreservePrompt>();

        public SeamlessDormancyGovernor(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref manualDormantTiles, "manualDormantTiles", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && manualDormantTiles == null)
                manualDormantTiles = new HashSet<int>();
            Scribe_Values.Look(ref nextArchiveOrdinal, "nextArchiveOrdinal", 0);
        }

        /// <summary>手动休眠登记（Manager 的 Sleep(manual:true) 桥接调用）。</summary>
        internal void RecordManualDormant(int worldTile)
        {
            if (worldTile >= 0) manualDormantTiles.Add(worldTile);
        }

        /// <summary>手动休眠解除（Manager 的 Wake/Forget 桥接调用——任意钥匙唤醒都解除锁）。</summary>
        internal void ClearManualDormant(int worldTile)
        {
            manualDormantTiles.Remove(worldTile);
        }

        /// <summary>卸载前恢复原版兼容模式（2026-08）：清空手动休眠锁（所锁图多已删，锁随 mod 卸载无意义，纯善后）。</summary>
        internal void ClearAllManualDormantForRestore()
        {
            manualDormantTiles.Clear();
        }

        /// <summary>
        /// 读档即睡（2026-08，方案 3）：休眠状态本身不序列化（读档后图全活跃是软休眠模型的读档语义），
        /// 手动锁图若等首轮 Sweep 再睡回会有一个"活跃进场 → 睡回"的横跳窗口（几十 tick 白跑）——
        /// 玩家多次存读档时可能把窗口期的 tick 误读为休眠漏洞。本回调在 maps.FinalizeLoading()
        /// （tick 重注册完成）之后、首个游戏 tick 之前调用（Game.cs LoadGame 时序），锁内图从第一
        /// tick 起即休眠。保活三条件同 Sweep（CurrentMap / 玩家 pawn / 家园）——CurrentMap 情形
        /// 本就是唤醒钥匙，保持活跃正确。Sweep 的重睡分支保留为幂等安全网（mod 交互等异常路径）。
        /// </summary>
        public override void LoadedGame()
        {
            // 几何进程缓存随档重置（2026-09 泄漏收口）：旧档图不经 DeinitAndRemoveMap 整体丢弃、
            // MapRemoved 不触发——同进程换档必须显式清空，否则旧档条目跨档累积（纯几何、随用随建，
            // 清空后首批消费各重建一次，无感）。开新档同理（StartedNewGame）。
            SeamlessPolygonGeometry.ClearAllCaches();
            if (manualDormantTiles.Count == 0) return;
            foreach (var m in new List<Map>(Find.Maps))
            {
                if (m == null || m.Disposed) continue;
                if (!SeamlessMapGovernance.IsGoverned(m)) continue;
                if (!manualDormantTiles.Contains(SeamlessTileRegistry.GetMapWorldTile(m))) continue;
                if (m == Find.CurrentMap || SeamlessMapGovernance.HasPlayerPawn(m)
                    || SeamlessMapGovernance.IsProtectedHome(m)) continue;
                SeamlessDormancyManager.Sleep(m, "governor: manual dormancy restored from save", true);
            }
        }

        public override void StartedNewGame()
        {
            // 同 LoadedGame 的缓存重置（新档开局同样可能带着主菜单前旧档的进程级几何残留）。
            SeamlessPolygonGeometry.ClearAllCaches();
        }

        public override void GameComponentTick()
        {
            // 降频设置缓存刷新（2026-09 热路径收口）：每 tick 一次读设置进 static 缓存，
            // ShouldThingTick/MoveCostMultiplier（每 thing 每 tick）不再各自读设置+Clamp；
            // 滑条即时性损失 ≤1 tick。必须先于本方法后续一切 Throttle/Unthrottle 消费。
            SeamlessTickThrottle.RefreshSettingsCache();

            // tick 剖析器心跳（2026-08-27）：每游戏 tick 恒调，测相邻调用墙钟差 = 每 tick 墙钟
            // 耗时 + 触发周期汇报。关闭时零开销（内部首行早退）。
            SeamlessTickProfiler.OnGameComponentTick();

            // 全局静态清扫（自 SeamlessMapTransferTrigger.MapComponentTick 迁移，2026-08 软休眠）：
            // 原挂"仅家园图 tick"——家园无玩家 pawn 时可随软休眠冻结，图 tick 不再可靠；
            // GameComponent 恒 tick，与一切地图的活跃状态解耦。（预约/UI 域，刻意不并入下方位置刷新段。）
            SeamlessSelectionTracker.PurgeInvalid();
            SeamlessTransferGrants.TickSweep();

            // === pawn 所在地追踪·统一更新段（2026-08-29 收拢架构，底座 = SeamlessPawnLocationTracker）===
            // 本方法 = 底座的唯一更新机制（安全执行点：刷新可能 Sleep 摘全局 tick 表 / 拆影子改写
            // holdingOwner，不能落在 thing tick 遍历中途——所以事件回调只置旗标，见底座注释）。
            // 两个消费者 = 同一事实（"pawn 现在在哪"）的两套派生计算，固定顺序、只在派生逻辑处差异：
            var locationsDirty = SeamlessPawnLocationTracker.ConsumeDirty();

            // ① 影子远行队（事件即时 或 自持 60t 轮询兜底）：据点图名单同步 + 轮询清扫注入副作用。
            SeamlessShadowCaravan.Maintain(locationsDirty);

            // ② 休眠距离扫描（事件即时 或 自持间隔轮询兜底，原 RequestSweepSoon 语义并入本段）。
            // **固定排在影子之后**：Sweep 的睡/删决策必须看到影子同步后的名单与清扫后的 spawnedThings
            //（删除路径另经 Forget → 底座 NotifyMapRemoving 的删前清扫双保险）。
            if (locationsDirty || Find.TickManager.TicksGame >= nextSweepTick)
            {
                // 间隔设置化（2026-08，原 const 600）：每轮现读，改设置即时生效（含 UI 滑条拖动）。
                var interval = System.Math.Max(RimExodusMod.Settings?.dormancySweepIntervalTicks ?? 600, MinSweepIntervalTicks);
                nextSweepTick = Find.TickManager.TicksGame + interval;
                Sweep();
            }

            // 前哨保留（2026-09）：弹窗队列 drain（Sweep 之后同 tick 末；弹窗 forcePause，开着时
            // 游戏暂停、队列静止）。（首版 +120t 延迟冲突清理已随 ZoneRestore order 后置整体拆除，
            // 此处不再有 Cleaner.Tick。）
            DrainPreservePrompts();

            // 威胁保活轮询（2026-09，独立于 Sweep 间隔可配——名单空时零成本）：复查追踪名单，
            // 敌人清零（死亡/离图皆覆盖——轮询查实况而非事件，自愈）/ 图休眠（玩家手动睡让位）/
            // 图销毁 → 注销。**回落不立即 Throttle**：交给下轮 Sweep 按距离重新收敛（0% 图重新
            // 摘表走既有幂等路径）——"袭击者也都离开地图时再次做一次判断"的落点。
            if (threatKeepaliveMaps.Count > 0 && Find.TickManager.TicksGame >= nextThreatPollTick)
            {
                var interval = System.Math.Max(RimExodusMod.Settings?.threatKeepalivePollIntervalTicks ?? 120,
                    MinThreatPollIntervalTicks);
                nextThreatPollTick = Find.TickManager.TicksGame + interval;
                PollThreatKeepalive();
            }
        }

        /// <summary>威胁追踪轮询体：注销失效条目（心跳日志无条件——生命周期事件非 spam）。</summary>
        private void PollThreatKeepalive()
        {
            var removed = null as List<Map>;
            foreach (var m in threatKeepaliveMaps)
            {
                if (m == null || m.Disposed
                    || SeamlessDormancyManager.IsDormant(m)
                    || !SeamlessMapGovernance.HasActiveThreat(m))
                {
                    (removed ??= new List<Map>()).Add(m);
                }
            }

            if (removed == null) return;
            foreach (var m in removed)
            {
                threatKeepaliveMaps.Remove(m);
                if (m != null && !m.Disposed)
                {
                    Log.Message($"[RimExodus] Threat keepalive ended: map {m.uniqueID} " +
                        $"(wt={SeamlessTileRegistry.GetMapWorldTile(m)}) — no active threats, next sweep re-evaluates");
                }
            }
        }

        private void Sweep()
        {
            if (Find.WorldGrid == null || Faction.OfPlayerSilentFail == null) return;

            var settings = RimExodusMod.Settings;
            var enabled = settings?.dormancyEnabled ?? true;
            // sleepHops 下限 2（2026-08-29 收回：1 会导致刚生成的邻接地图立刻被休眠甚至删除——
            // 邻图预加载生成时玩家就在旁边，距离 1 恒达；放宽到 1 系 2026-08 误判，UI 同步 2-8）。
            // deleteHops 允许与 sleepHops 相等（同一轮即删，无需先休眠一段时间）；语义 =
            // "地图先满足休眠条件才可能被删除"（delete < sleep 时取较大值），到达删除距离当轮直接删除。
            // 脚下安全仍由三条保活保证：CurrentMap / 有玩家 pawn / IsProtectedHome。
            var sleepHops = System.Math.Max(settings?.dormancySleepHops ?? 2, 2);
            var deleteHops = System.Math.Max(settings?.dormancyDeleteHops ?? 3, sleepHops);

            // 快照 Find.Maps：删除分支会移除地图（RemoveTileMap → DeinitAndRemoveMap），遍历中修改原列表不安全。
            var snapshot = new List<Map>(Find.Maps);

            // 源 = 有玩家阵营 spawned pawn 的图的 tile 集合（判定唯一出处 = SeamlessMapGovernance.HasPlayerPawn）。
            var sources = new HashSet<int>();
            foreach (var m in snapshot)
            {
                if (m == null || m.Disposed) continue;
                if (SeamlessMapGovernance.HasPlayerPawn(m))
                {
                    sources.Add(SeamlessTileRegistry.GetMapWorldTile(m));
                }
            }

            // 远行队也是源（2026-08）：玩家阵营 caravan 的 tile 并入（实时查询——caravan 每刻移动，
            // 事件登记必滞后）。修复"全员远行 → 源空"的结构性炸弹：源空时下方 BFS 距离表恒空，
            // 所有管辖图落进 d=MaxValue≥deleteHops 的删除分支，只有 CurrentMap/家园靠保活幸存（纯运气）。
            var caravans = Find.WorldObjects.Caravans;
            for (var i = 0; i < caravans.Count; i++)
            {
                if (caravans[i].Faction == Faction.OfPlayer)
                {
                    sources.Add(caravans[i].Tile.tileId);
                }
            }

            // 空源守卫（勿删）：无任何玩家 pawn（全员死亡等异常态）本轮不睡不删——
            // 与其按"无穷远"处理不如保守跳过，状态由源恢复后的下轮扫描收敛。
            if (sources.Count == 0) return;

            // 世界网格 BFS：源出发的跳数表（纯拓扑——路径可以穿过任何图，与加载状态无关）。
            var dist = new Dictionary<int, int>();
            var queue = new Queue<int>();
            foreach (var t in sources)
            {
                if (t < 0 || dist.ContainsKey(t)) continue;
                dist[t] = 0;
                queue.Enqueue(t);
            }
            var neighbors = new List<PlanetTile>();
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (dist[cur] >= deleteHops) continue;
                Find.WorldGrid.GetTileNeighbors(cur, neighbors);
                foreach (var n in neighbors)
                {
                    if (dist.ContainsKey(n.tileId)) continue;
                    dist[n.tileId] = dist[cur] + 1;
                    queue.Enqueue(n.tileId);
                }
            }

            var current = Find.CurrentMap;
            foreach (var m in snapshot)
            {
                if (m == null || m.Disposed) continue;

                // 管辖判定归一层（地块图 ∪ 原生家族；玩家家园 Settlement 亦入辖以获得保活唤醒）。
                if (!SeamlessMapGovernance.IsGoverned(m)) continue;
                if (IncrementalMapGenerator.IsGenerating(m)) continue; // 分帧生成中的图还在构建。

                var tile = SeamlessTileRegistry.GetMapWorldTile(m);
                if (!dist.TryGetValue(tile, out var d)) d = int.MaxValue; // BFS 未收录 = 超过 deleteHops 或不连通。

                // 威胁保活发现（2026-09，第四条保活的注册点，用户定夺"图上有活跃敌人即忽略降速"）：
                // 非休眠图上发现活跃敌人 → 注册追踪（下方保活分支豁免降频/休眠/删除；清零回落走
                // GameComponentTick 的独立轮询段）。已追踪图跳过谓词（交给轮询复查，省 Sweep 成本）；
                // 休眠图刻意不注册（休眠冻结防级联语义保留，见 threatKeepaliveMaps 注释）。
                // 发现延迟 ≤ 一个 Sweep 周期——顺带自愈"袭击 incident 选中降频图、敌人冻在 0%
                // 生成点"的既有半 bug。
                if (!SeamlessDormancyManager.IsDormant(m) && !threatKeepaliveMaps.Contains(m)
                    && SeamlessMapGovernance.HasActiveThreat(m))
                {
                    threatKeepaliveMaps.Add(m);
                    SeamlessTickThrottle.Unthrottle(m, "active threat keepalive");
                    Log.Message($"[RimExodus] Threat keepalive ON: map {m.uniqueID} " +
                        $"(wt={SeamlessTileRegistry.GetMapWorldTile(m)}) — active threats present");
                }

                // 保活（无条件活跃）：玩家正看着的图 / 玩家 pawn 所在图 / 玩家家园 / 活跃威胁追踪中。
                // 家园（IsProtectedHome，2026-08 用户定夺"不休眠不删除"）：开局家园/定居/gravship
                // 降落/引力引擎营地——特权判定全项目唯一行为消费点在此，删除分支不再有独立豁免。
                if (m == current || sources.Contains(tile) || SeamlessMapGovernance.IsProtectedHome(m)
                    || threatKeepaliveMaps.Contains(m))
                {
                    // 保活 = 恢复全速（降频一并解除；玩家落图/进图的即时解除另有事件入口，此处兜底）。
                    SeamlessTickThrottle.Unthrottle(m, "governor keep-alive");
                    if (SeamlessDormancyManager.IsDormant(m))
                    {
                        // 手动休眠锁（2026-08 玩家 gizmo）：玩家手动睡的图不被保活/距离回落自动唤醒
                        // ——只有玩家主动进图（CurrentMap setter，上方 m == current 即其一）与 pawn
                        // 被命令接近其接缝（BorderPreloader 的 TryWakeByWorldTile）能唤醒（用户定夺）。
                        // 手动锁图上不应有玩家 pawn（gizmo 对有玩家 pawn 的图距离 0 恒活跃），
                        // 此守卫主要挡"家园/回落兜底"类误唤醒。删除分支不受锁影响。
                        if (m != current && SeamlessDormancyManager.IsManuallyDormant(m)) continue;
                        var keepReason = m == current
                            ? "governor keep-alive (CurrentMap)"
                            : sources.Contains(tile)
                                ? "governor keep-alive (player pawn on map)"
                                : threatKeepaliveMaps.Contains(m)
                                    ? "governor keep-alive (active threat)"
                                    : "governor keep-alive (player home)";
                        SeamlessDormancyManager.Wake(m, keepReason);
                    }
                    continue;
                }

                if (!enabled)
                {
                    SeamlessTickThrottle.Unthrottle(m, "dormancy disabled"); // 降频随总开关关闭。
                    continue; // 关闭时只唤醒（上面的保活分支），不休眠/不删除。
                }

                // 手动锁的安全网（主路径 = 上方 LoadedGame 的读档即睡，2026-08 方案 3）：
                // 覆盖"锁内图读档后处于活跃圈（d < sleepHops）"等异常路径——Sleep 分支够不着、
                // 唤醒守卫又只挡"已休眠"的图，没有本分支它就永远醒着。幂等（已休眠零操作）。
                if (manualDormantTiles.Contains(tile) && !SeamlessDormancyManager.IsDormant(m))
                {
                    SeamlessDormancyManager.Sleep(m, "governor: manual dormancy restored from save", true);
                    continue;
                }

                if (d >= deleteHops)
                {
                    // 滚动删除（可删判定归一层：受管辖且非家园——家园已在上方保活分支 return）。
                    // **删除流程单一入口（2026-09 二轮归一）**：Auto 封存分流、BelowThreshold 弹窗
                    // 询问、None 直删——全部在 RemoveRollingMap 入口内部，这里不再有任何前置判定
                    //（首轮实现把弹窗策略留在本分支 = 显式删除路径不弹的霰弹残留，已收拢）。
                    if (SeamlessMapGovernance.CanRollingDelete(m))
                    {
                        m.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(m.Parent);
                    }
                    continue;
                }

                if (d >= sleepHops)
                {
                    // manual 续期：读档后休眠状态不序列化（图全活跃），本分支对持久化手动锁
                    // （manualDormantTiles）内的图以 manual:true 重新入睡——锁跨档保留（2026-08）。
                    SeamlessDormancyManager.Sleep(m, $"governor: BFS dist={d} >= sleepHops={sleepHops}",
                        manualDormantTiles.Contains(tile));
                }
                else if (SeamlessDormancyManager.IsDormant(m))
                {
                    // 距离回落到活跃圈（玩家走近）：唤醒（正常由边界预加载带先行触发，此处兜底）。
                    // 手动休眠锁同样挡此兜底（2026-08 实测遗漏：玩家走到手动睡的图旁 1 跳即被
                    // 本分支唤醒——保活分支有守卫、回落分支漏加，日志 "BFS dist=1 < sleepHops" 即此）。
                    if (SeamlessDormancyManager.IsManuallyDormant(m)) continue;
                    SeamlessDormancyManager.Wake(m, $"governor: BFS dist={d} < sleepHops={sleepHops}");
                    // 降频判定（2026-08 分级休眠中间档）：唤醒后立即评估——空图（无玩家 pawn，
                    // 到达此处即非源非 CurrentMap 非家园）按百分比降频，不等下轮 Sweep。
                    if (SeamlessTickThrottle.ThrottleEnabled)
                        SeamlessTickThrottle.Throttle(m, $"governor: BFS dist={d} < sleepHops, no player pawn");
                }
                else if (SeamlessTickThrottle.ThrottleEnabled)
                {
                    // 活跃圈内空图：维持/进入降频（幂等重入，半径变更时自动重建快速区）。
                    // Sweep 是唯一常态入口——玩家落图/切图/贴近接缝的即时解除走各事件的 Unthrottle，
                    // 这里按最新距离与设置重新收敛。
                    SeamlessTickThrottle.Throttle(m, $"governor: BFS dist={d} < sleepHops, no player pawn");
                }
                else
                {
                    // 降频关闭（100% = 原生）：活跃圈内空图全速。
                    SeamlessTickThrottle.Unthrottle(m, "throttle disabled (100%)");
                }
            }

            // 前哨保留收尾（2026-09）：权重淘汰（cap>0 时，覆盖本轮新封存与设置调小两类触发）
            // + 全量发布 PreservedTiles（世界图点标消费）。
            RunArchiveEviction();
            RefreshPreservedTiles();
        }

        // ====================================================================
        // 前哨保留（2026-09 封存/重放）。设计定案（四轮对话收敛，详见 doc/地图滚动休眠.md）：
        // - 判定唯一出处 = SeamlessMapModificationTracker（居住区语义，不与删除策略混杂）；
        // - 封存 = 捕获 ZoneMapRecord 挂 WO + DeinitAndRemoveMap 拆图，WO 刻意保留
        //   （邻居链不断/世界图第四态——与 RemoveTileMap 的差异；无 Map 时不参与接缝参考）；
        // - 恢复 = 生成守卫识别"无图有记录"复用 WO 走生成链，GenStep_ZoneRestore(395) 置换重放；
        // - 淘汰不设硬上限：保留数量 N（0=不限）+ 权重公式 X×居住区格数 + Y×新近度序号，
        //   超限淘汰权重最低者（默认 X=0/Y=-1 = 淘汰最旧 = "保留最近 N 个"）。
        // ====================================================================

        /// <summary>
        /// 封存一张地块图。调用面 = 唯一删除入口 <see cref="SeamlessTileManager.RemoveRollingMap"/> 的内部分流
        ///（2026-09 单一入口收拢后 governor sweep 与 gizmo/Dev 显式删除同路）与本类弹窗"保留"。
        /// 捕获失败（居住区突然变空等竞态）回落普通删除。
        /// </summary>
        internal void ArchiveTileMap(MapParent_SeamlessTile parent, Map map, int tile, int homeCells, string reason)
        {
            var record = ZoneMapRecord.Capture(map, tile, nextArchiveOrdinal++, homeCells);
            if (record == null)
            {
                Log.Warning($"[RimExodus] Preserve archive: capture failed for map {map.uniqueID} wt={tile}, falling back to deletion.");
                map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(parent);
                return;
            }
            parent.preserveRecord = record;

            // 拆图（与 RemoveTileMap 的三点差异，勿"顺手对齐"：①WO 保留——邻居链/
            // 世界图第四态的载体；②不 CleanupNeighborLinks——链接指向活 WO，跨档有效；封存态
            // 无 Map，不参与接缝参考，恢复后重新生成自然外围并与当前邻图混合；③不
            // ReleaseTileMesh——WO 仍在绘制（封存态）。Forget 先行 = 删前清扫 + 休眠集合摘除）。
            SeamlessDormancyManager.Forget(map);
            Current.Game.DeinitAndRemoveMap(map, false);
            SeamlessWeatherClusterManager.RebindAll();

            // 状态迁移心跳（常开，对照 DELETE；不逐轮刷屏——封存是一次性事件非周期状态）。
            Log.Message($"[RimExodus] Dormancy ARCHIVE: map {map.uniqueID} wt={tile} (home={homeCells}, " +
                        $"zone={record.zoneCells.Count} cells, buildings={record.buildings.Count}, items={record.items.Count}) — {reason}");

            RunArchiveEviction();
            RefreshPreservedTiles();
        }

        /// <summary>
        /// 入队封存询问（2026-09 二轮归一：唯一入队点 = 单一删除入口 RemoveRollingMap 的内部分流，
        /// 所有删除路径——距离删除/gizmo/Dev——统一触发）。同 tile 去重；返回 true = 已入队或已在队
        ///（= 调用方应视为"本次不删"）。
        /// </summary>
        internal bool QueuePreservePrompt(MapParent_SeamlessTile parent, Map map, int tile, int homeCells)
        {
            for (int i = 0; i < preservePrompts.Count; i++)
            {
                if (preservePrompts[i].tile == tile) return true; // 已在队（弹窗未决期间每轮 Sweep 重入）
            }
            preservePrompts.Add(new PreservePrompt { map = map, parent = parent, tile = tile, homeCells = homeCells });
            return true;
        }

        /// <summary>
        /// 弹窗队列 drain（GameComponentTick 末尾）：一次一张（任意 Dialog_MessageBox 在场时避让，
        /// 防堆叠）；队首失效（玩家走回/图没了/不再可删）静默丢弃。三按钮：
        /// 「保留」（=封存，回车/accept 同效）「不保留」（=删除）「不保留，以后不再询问」
        /// （→二次确认讲清收益与风险，确认后写设置 + 删除）。Esc = "稍后"——出队不动作，下轮
        /// Sweep 重新入队重弹（Dialog forcePause，弹窗期间无 Sweep，队列静止）。
        /// </summary>
        private void DrainPreservePrompts()
        {
            if (preservePrompts.Count == 0) return;
            if (Find.WindowStack.IsOpen<Dialog_MessageBox>()) return;

            while (preservePrompts.Count > 0 && !PromptStillEligible(preservePrompts[0]))
            {
                preservePrompts.RemoveAt(0);
            }
            if (preservePrompts.Count == 0) return;

            var p = preservePrompts[0];
            var threshold = Mathf.Clamp(RimExodusMod.Settings?.dormancyPreserveHomeAreaThreshold ?? 20, 0, 500);

            Action keepAction = delegate
            {
                preservePrompts.RemoveAt(0);
                if (PromptStillEligible(p))
                {
                    ArchiveTileMap(p.parent, p.map, p.tile, p.homeCells, "player kept (sub-threshold prompt)");
                }
            };
            Action discardAction = delegate
            {
                preservePrompts.RemoveAt(0);
                if (PromptStillEligible(p))
                {
                    DeleteFromPrompt(p, "player discarded (prompt)");
                }
            };
            Action escAction = delegate
            {
                // Esc/取消 = 稍后：出队不动作，下轮 Sweep 重弹（用户未做决定，诚实 nagging）。
                preservePrompts.RemoveAt(0);
            };

            var dialog = new Dialog_MessageBox(
                "RimExodus_PreservePromptText".Translate(p.parent.Label, p.homeCells, threshold),
                "RimExodus_PreserveKeepBtn".Translate(), keepAction,
                "RimExodus_PreserveDiscardBtn".Translate(), discardAction,
                null, false, keepAction, escAction);
            dialog.buttonCText = "RimExodus_PreserveNeverAskBtn".Translate();
            dialog.buttonCAction = delegate
            {
                // buttonC 点击后原窗自关（buttonCClose 默认 true），转二次确认；取消二次确认 =
                // 队列未动，下个 tick 重弹原窗（玩家说了"不再询问"又反悔 → 重新问是正确行为）。
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RimExodus_PreserveNeverAskConfirmText".Translate(), delegate
                    {
                        preservePrompts.RemoveAt(0);
                        if (RimExodusMod.Settings != null)
                        {
                            RimExodusMod.Settings.dormancyPreservePromptDisabled = true;
                        }
                        if (PromptStillEligible(p))
                        {
                            DeleteFromPrompt(p, "player discarded + never ask again (prompt)");
                        }
                    }, destructive: true));
            };
            Find.WindowStack.Add(dialog);
        }

        /// <summary>答复时重验资格（弹窗开着玩家可能正走回去）：图活着、仍挂同一 parent、仍受管辖可删、无玩家 pawn、非 CurrentMap。</summary>
        private static bool PromptStillEligible(PreservePrompt p)
        {
            var m = p.map;
            return m != null && !m.Disposed && !p.parent.Destroyed && m.Parent == p.parent
                && SeamlessMapGovernance.IsGoverned(m) && SeamlessMapGovernance.CanRollingDelete(m)
                && !SeamlessMapGovernance.HasPlayerPawn(m) && m != Find.CurrentMap;
        }

        private static void DeleteFromPrompt(PreservePrompt p, string reason)
        {
            // preservePromptAnswered: true——玩家已在弹窗里选了"不保留"，跳过入口的 BelowThreshold
            // 再次询问（防死循环：询问→删→又询问）。实际删除日志由入口统一输出。
            p.map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(p.parent, preservePromptAnswered: true);
        }

        /// <summary>
        /// 权重淘汰（每轮 Sweep 末 + 每次封存后）：cap（保留数量）>0 且封存数超限时，淘汰
        /// 保留权重 = X×homeCellsAtCapture + Y×新近度序号（1 = 最近封存，越大越旧）**最低**者
        /// （销毁 WO 连记录；默认 X=0/Y=-1 → 淘汰最旧）。WO 无图 → Destroy 无连删副作用。
        /// </summary>
        private void RunArchiveEviction()
        {
            var cap = Mathf.Clamp(RimExodusMod.Settings?.dormancyPreserveCount ?? 0, 0, 999);
            if (cap <= 0) return;

            var archived = new List<MapParent_SeamlessTile>();
            foreach (var wo in Find.World.worldObjects.AllWorldObjects)
            {
                if (wo is MapParent_SeamlessTile st && !st.Destroyed && st.preserveRecord != null)
                {
                    archived.Add(st);
                }
            }
            if (archived.Count <= cap) return;

            var wHome = RimExodusMod.Settings?.dormancyPreserveWeightHome ?? 0;
            var wAge = RimExodusMod.Settings?.dormancyPreserveWeightAge ?? -1;

            while (archived.Count > cap)
            {
                int maxOrdinal = int.MinValue;
                foreach (var a in archived)
                {
                    if (a.preserveRecord.archiveOrdinal > maxOrdinal) maxOrdinal = a.preserveRecord.archiveOrdinal;
                }
                MapParent_SeamlessTile victim = null;
                long bestWeight = long.MaxValue;
                foreach (var a in archived)
                {
                    long rank = maxOrdinal - a.preserveRecord.archiveOrdinal + 1;
                    long weight = (long)wHome * a.preserveRecord.homeCellsAtCapture + wAge * rank;
                    if (weight < bestWeight)
                    {
                        bestWeight = weight;
                        victim = a;
                    }
                }
                if (victim == null) break;

                archived.Remove(victim);
                var label = victim.Label;
                Log.Message($"[RimExodus] Dormancy ARCHIVE EVICT: wt={victim.Tile.tileId} (weight={bestWeight}, " +
                            $"archived={archived.Count + 1} > cap={cap}) — weight formula");
                Messages.Message("RimExodus_PreserveEvicted".Translate(label), MessageTypeDefOf.NeutralEvent, false);
                TileWorldIcons.ReleaseTileMesh(victim.Tile);
                victim.Destroy();
            }
        }

        /// <summary>
        /// 全量重写 tracker.PreservedTiles（世界图点标数据源）：达阈活图 ∪ 已封存 WO。
        /// 状态派生无持久化（居住区是实时状态、封存 = 记录在档），每轮重算即自愈。
        /// </summary>
        private static void RefreshPreservedTiles()
        {
            var set = SeamlessMapModificationTracker.PreservedTiles;
            set.Clear();
            foreach (var m in Find.Maps)
            {
                if (m == null || m.Disposed || !(m.Parent is MapParent_SeamlessTile)) continue;
                if (((MapParent_SeamlessTile)m.Parent).preserveRecord != null
                    || SeamlessMapModificationTracker.Evaluate(m, out _) == PreserveDecision.Auto)
                {
                    set.Add(SeamlessTileRegistry.GetMapWorldTile(m));
                }
            }
            foreach (var wo in Find.World.worldObjects.AllWorldObjects)
            {
                if (wo is MapParent_SeamlessTile st && !st.Destroyed && st.preserveRecord != null)
                {
                    set.Add(st.Tile.tileId);
                }
            }
        }
    }
}
