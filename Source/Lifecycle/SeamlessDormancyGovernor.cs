using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
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

        /// <summary>
        /// 手动休眠锁的持久化面（2026-08，用户要求存档保留）：存世界 tile id 而非 Map 引用——
        /// 手动休眠对任意受管辖图（地块图 ∪ 原生家族 Settlement 等）均可用，挂 MapParent_SeamlessTile
        /// 会漏原生家族。运行时真值仍是 <see cref="SeamlessDormancyManager"/> 的静态集合，本集合由
        /// Manager 的 Sleep(manual)/Wake/Forget 桥接增删；读档后图全活跃（休眠不序列化），首轮 Sweep
        /// 对 tile ∈ 本集合的图 Sleep 时传 manual:true，锁跨档续期。
        /// </summary>
        private HashSet<int> manualDormantTiles = new HashSet<int>();

        public SeamlessDormancyGovernor(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref manualDormantTiles, "manualDormantTiles", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && manualDormantTiles == null)
                manualDormantTiles = new HashSet<int>();
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

                // 保活（无条件活跃）：玩家正看着的图 / 玩家 pawn 所在图 / 玩家家园。
                // 家园（IsProtectedHome，2026-08 用户定夺"不休眠不删除"）：开局家园/定居/gravship
                // 降落/引力引擎营地——特权判定全项目唯一行为消费点在此，删除分支不再有独立豁免。
                if (m == current || sources.Contains(tile) || SeamlessMapGovernance.IsProtectedHome(m))
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
                    // 地块图销毁 Map+WorldObject；原生家族延迟执行原版删除偏好（Settlement 删图
                    // 留对象等，见 RemoveNativeFamilyMap；原版判定 false（建筑/pawn 阻挡）则本轮跳过）。
                    if (SeamlessMapGovernance.CanRollingDelete(m))
                    {
                        Log.Message($"[RimExodus] Dormancy DELETE: map {m.uniqueID} wt={tile} (BFS dist={d} ≥ deleteHops={deleteHops}) — governor rolling delete");
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
        }
    }
}
