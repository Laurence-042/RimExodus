using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图滚动休眠调度器（用户定夺 2026-08）。
    ///
    /// 【距离策略】源 = 所有玩家阵营 spawned pawn 所在图的 tile（含锚点图——玩家在家时家园即源）。
    /// 世界网格 BFS 跳数（GetTileNeighbors，纯拓扑、不依赖图加载状态）：
    /// - 图上有玩家 pawn → 永不休眠、永不删除（保护判据，"仅玩家 pawn 保护"——玩家踏入即距离 0，
    ///   天然满足"相邻图不休眠防跨图躲追击"）；
    /// - 距离 &lt; sleepHops（默认 2）→ 活跃（距离 0/1）；
    /// - 距离 ≥ sleepHops → 休眠（软休眠：内容完好，见 <see cref="SeamlessDormancyManager"/>）；
    /// - 距离 ≥ deleteHops（默认 3）且为地块图 → 删除（<see cref="SeamlessTileManager.RemoveTileMap"/>，
    ///   销毁 Map+WorldObject，下次进入走生成链重建——解除 Game.AddMap 127 图上限的实际压力）。
    ///   **锚点图（家园）永不删除**：其 WorldObject 是原版殖民地对象，Destroy 会引爆殖民地逻辑。
    /// - Find.CurrentMap 无条件保活（玩家正看着的图冻结会卡 UI）。
    ///
    /// 【管辖范围】仅地块图（MapParent_SeamlessTile）与锚点图（IsAnchorMap）。原版任务图/site
    /// 等其他 MapParent 子类的图不碰。
    ///
    /// 【注册】Game.FillComponents 反射自动实例化所有 GameComponent 子类，无需 XML def。
    ///
    /// 【读档语义】休眠状态不序列化：读档后全活跃，本 governor 首轮扫描（TicksGame 从 0 计，
    /// nextSweepTick=0 立即触发）重新收敛远图。
    /// </summary>
    public class SeamlessDormancyGovernor : GameComponent
    {
        /// <summary>扫描间隔（ticks）。休眠判定非紧急，600 ticks（10 游戏秒）粒度足够。</summary>
        private const int SweepIntervalTicks = 600;

        /// <summary>下次扫描的 game tick（不序列化：读档后立即首轮扫描，见类注释）。</summary>
        private int nextSweepTick;

        public SeamlessDormancyGovernor(Game game) { }

        public override void GameComponentTick()
        {
            // 全局静态清扫（自 SeamlessMapTransferTrigger.MapComponentTick 迁移，2026-08 软休眠）：
            // 原挂"仅锚点图 tick"——家园无玩家 pawn 时可随软休眠冻结，锚点 tick 不再可靠；
            // GameComponent 恒 tick，与一切地图的活跃状态解耦。
            SeamlessSelectionTracker.PurgeInvalid();
            SeamlessTransferGrants.TickSweep();
            SeamlessCrossMapOrders.PurgeInvalid();

            if (Find.TickManager.TicksGame < nextSweepTick) return;
            nextSweepTick = Find.TickManager.TicksGame + SweepIntervalTicks;
            Sweep();
        }

        private void Sweep()
        {
            if (Find.WorldGrid == null || Faction.OfPlayerSilentFail == null) return;

            var settings = RimExodusMod.Settings;
            var enabled = settings?.dormancyEnabled ?? true;
            // sleepHops 下限 2：相邻图（距离 1）必须活跃——玩家可跨图躲追击的 exploit 防线（用户定夺）。
            var sleepHops = System.Math.Max(settings?.dormancySleepHops ?? 2, 2);
            var deleteHops = System.Math.Max(settings?.dormancyDeleteHops ?? 3, sleepHops + 1);

            // 快照 Find.Maps：删除分支会移除地图（RemoveTileMap → DeinitAndRemoveMap），遍历中修改原列表不安全。
            var snapshot = new List<Map>(Find.Maps);

            // 源 = 有玩家阵营 spawned pawn 的图的 tile 集合（同时充当 hasPlayerPawn 判定）。
            var sources = new HashSet<int>();
            foreach (var m in snapshot)
            {
                if (m == null || m.Disposed) continue;
                var pawns = m.mapPawns.AllPawnsSpawned;
                for (var i = 0; i < pawns.Count; i++)
                {
                    if (pawns[i].Faction == Faction.OfPlayer)
                    {
                        sources.Add(SeamlessTileRegistry.GetMapWorldTile(m));
                        break;
                    }
                }
            }

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

                var tileParent = m.Parent as MapParent_SeamlessTile;
                if (tileParent == null && !SeamlessTileGraph.IsAnchorMap(m)) continue; // 非 RimExodus 管辖图不碰。
                if (IncrementalMapGenerator.IsGenerating(m)) continue; // 分帧生成中的图还在构建。

                var tile = SeamlessTileRegistry.GetMapWorldTile(m);
                if (!dist.TryGetValue(tile, out var d)) d = int.MaxValue; // BFS 未收录 = 超过 deleteHops 或不连通。

                // 玩家正看着的图 / 玩家 pawn 所在图：无条件活跃。
                if (m == current || sources.Contains(tile))
                {
                    if (SeamlessDormancyManager.IsDormant(m))
                    {
                        var keepReason = m == current
                            ? "governor keep-alive (CurrentMap)"
                            : "governor keep-alive (player pawn on map)";
                        SeamlessDormancyManager.Wake(m, keepReason);
                    }
                    continue;
                }

                if (!enabled) continue; // 关闭时只唤醒（上面的保活分支），不休眠/不删除。

                if (d >= deleteHops)
                {
                    // 删除：仅地块图（锚点/家园的 WorldObject 是原版殖民地对象，Destroy 会引爆殖民地逻辑）。
                    if (tileParent != null)
                    {
                        Log.Message($"[RimExodus] Dormancy DELETE: map {m.uniqueID} wt={tile} (BFS dist={d} ≥ deleteHops={deleteHops}) — governor rolling delete");
                        m.GetComponent<SeamlessTileManager>()?.RemoveTileMap(tileParent);
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
