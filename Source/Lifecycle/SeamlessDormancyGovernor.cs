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
    /// - 距离 &lt; sleepHops（默认 2）→ 活跃（距离 0/1）；
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

        public SeamlessDormancyGovernor(Game game) { }

        public override void GameComponentTick()
        {
            // 全局静态清扫（自 SeamlessMapTransferTrigger.MapComponentTick 迁移，2026-08 软休眠）：
            // 原挂"仅家园图 tick"——家园无玩家 pawn 时可随软休眠冻结，图 tick 不再可靠；
            // GameComponent 恒 tick，与一切地图的活跃状态解耦。
            SeamlessSelectionTracker.PurgeInvalid();
            SeamlessTransferGrants.TickSweep();

            if (Find.TickManager.TicksGame < nextSweepTick) return;
            // 间隔设置化（2026-08，原 const 600）：每轮现读，改设置即时生效（含 UI 滑条拖动）。
            var interval = System.Math.Max(RimExodusMod.Settings?.dormancySweepIntervalTicks ?? 600, MinSweepIntervalTicks);
            nextSweepTick = Find.TickManager.TicksGame + interval;
            Sweep();
        }

        /// <summary>
        /// 请求尽快扫描（事件触发入口，2026-08）：pawn 跨缝传送完成 / 远行队组队离图 / 远行队进图
        /// 三类 pawn 变动把休眠决策提前到即时（600 ticks 周期保留兜底——远行队逐 tile 移动靠周期采样）。
        /// 刻意只置零计数器、不在事件回调里直接 Sweep：Sleep 要摘全局 tick 表，落在 thing tick 遍历
        /// 中途不安全（doc/地图滚动休眠.md 2.1 时序约束），GameComponentTick 是安全执行点。
        /// </summary>
        public void RequestSweepSoon()
        {
            nextSweepTick = 0;
        }

        /// <summary>静态便捷入口（事件处调用；无实例时静默跳过）。</summary>
        public static void RequestSweepSoonStatic()
        {
            Current.Game?.GetComponent<SeamlessDormancyGovernor>()?.RequestSweepSoon();
        }

        private void Sweep()
        {
            if (Find.WorldGrid == null || Faction.OfPlayerSilentFail == null) return;

            var settings = RimExodusMod.Settings;
            var enabled = settings?.dormancyEnabled ?? true;
            // sleepHops 下限 1（2026-08 放宽，原下限 2 + deleteHops ≥ sleep+1）：性能不佳的玩家可
            // "离开即休眠并销毁"（1/1）；deleteHops 允许与 sleepHops 相等（同一轮先睡后删）。
            // 脚下安全仍由三条保活保证：CurrentMap / 有玩家 pawn / IsProtectedHome。
            var sleepHops = System.Math.Max(settings?.dormancySleepHops ?? 2, 1);
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

                if (!enabled) continue; // 关闭时只唤醒（上面的保活分支），不休眠/不删除。

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
                    SeamlessDormancyManager.Sleep(m, $"governor: BFS dist={d} ≥ sleepHops={sleepHops}");
                }
                else if (SeamlessDormancyManager.IsDormant(m))
                {
                    // 距离回落到活跃圈（玩家走近）：唤醒（正常由边界预加载带先行触发，此处兜底）。
                    SeamlessDormancyManager.Wake(m, $"governor: BFS dist={d} < sleepHops={sleepHops}");
                }
            }
        }
    }
}
