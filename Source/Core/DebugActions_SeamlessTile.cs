using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块的调试命令（Dev 菜单）。
    /// 用于在游戏内手动生成/卸载无缝地块口袋地图，验证渲染与接缝。
    ///
    /// 阶段3：邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 命令改为"生成当前地块的指定世界邻居地块"。
    /// </summary>
    public static class DebugActions_SeamlessTile
    {
        private const string Category = "RimExodus";

        private const int DefaultMapSize = 250;

        /// <summary>获取当前地图上的 SeamlessTileManager。</summary>
        private static SeamlessTileManager CurrentManager
        {
            get
            {
                var map = Find.CurrentMap;
                if (map == null) return null;
                return map.GetComponent<SeamlessTileManager>();
            }
        }

        /// <summary>
        /// 生成当前地块的全部邻居地块（2026-08-27 入口统一化）：走预加载队列
        /// <see cref="SeamlessTilePreloader.QueuePreload"/>——与玩家命令 pawn 触发的边界预加载
        /// 完全同一条链（队列 → ConsumeQueued → TryPreloadNeighbor → 私有 GenerateTileMap），
        /// 忙时自动重排队下一 tick，六邻图逐个串行生成完。**勿改为同步直调 GenerateTileMap**：
        /// 分帧增量生成全程持有 mapBeingGenerated，同步循环里第一个调用启动后其余全撞忙守卫被拒
        /// （曾致"只生成了一个图"，原 GenerateForNeighbor 即此病灶，已删）。
        /// </summary>
        [DebugAction(Category, "Generate All Seamless Neighbors", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateAllNeighbors()
        {
            var currentMap = Find.CurrentMap;
            var currentWorldTile = SeamlessTileRegistry.GetMapWorldTile(currentMap);
            if (currentWorldTile < 0)
            {
                Log.Warning("[RimExodus] Current map has no valid world tile.");
                return;
            }

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(currentWorldTile, worldNeighbors);
            var queued = 0;
            foreach (var neighborTile in worldNeighbors)
            {
                if (neighborTile.tileId == currentWorldTile) continue;
                SeamlessTilePreloader.QueuePreload(currentMap, neighborTile.tileId);
                queued++;
            }
            Log.Message($"[RimExodus] Queued generation for {queued} seamless neighbor tiles (consumed one per tick via preloader).");
        }

        [DebugAction(Category, "Remove All Tile Maps", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAll()
        {
            var manager = CurrentManager;
            if (manager == null) return;

            // 本工具的显式范围 = 仅地块图（快速清场用）；原生家族图的删除走 governor/
            // Force Delete（RemoveRollingMap 统一入口）——此处类型过滤是工具范围选择，非特权判定。
            var currentMap = Find.CurrentMap;
            var neighbors = SeamlessTileGraph.GetAllNeighbors(currentMap);
            var toRemove = new List<MapParent_SeamlessTile>();
            foreach (var info in neighbors)
            {
                if (info.map.Parent is MapParent_SeamlessTile neighborParent)
                {
                    toRemove.Add(neighborParent);
                }
            }

            foreach (var parent in toRemove)
            {
                manager.RemoveTileMap(parent);
            }

            Log.Message($"[RimExodus] Removed {toRemove.Count} seamless tile maps.");
        }

        /// <summary>手动休眠当前图（软休眠验证：不 tick、不显示为邻接地图、无访问入口，内容完好）。</summary>
        [DebugAction(Category, "Sleep Current Map (Dormancy)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SleepCurrentMap()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            if (!SeamlessDormancyManager.IsDormant(map))
            {
                SeamlessDormancyManager.Sleep(map, "dev command (Sleep Current Map)");
            }
            else
            {
                Log.Message($"[RimExodus] Map {map.uniqueID} is already dormant.");
            }
        }

        /// <summary>手动唤醒当前图（若休眠中）。注意：切图到休眠图会经 CurrentMap setter 自动唤醒。</summary>
        [DebugAction(Category, "Wake Current Map (Dormancy)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WakeCurrentMap()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            if (SeamlessDormancyManager.IsDormant(map))
            {
                SeamlessDormancyManager.Wake(map, "dev command (Wake Current Map)");
            }
            else
            {
                Log.Message($"[RimExodus] Map {map.uniqueID} is not dormant.");
            }
        }

        /// <summary>强制删除当前受管辖图（地块图销毁 Map+WorldObject；原生家族延迟原版偏好；等同 governor 的删除路径；家园/未管辖图拒绝）。</summary>
        [DebugAction(Category, "Force Delete Current Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceDeleteCurrentTileMap()
        {
            var map = Find.CurrentMap;
            if (!SeamlessMapGovernance.CanRollingDelete(map))
            {
                Log.Message("[RimExodus] Current map is not rolling-deletable (player home or unmanaged map).");
                return;
            }
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(map.Parent);
            Log.Message($"[RimExodus] Deleted rolling map wt={tile}.");
        }

        /// <summary>休眠状态报告：活跃/休眠图数、地图总数（127 上限余量）、governor 设置。</summary>
        [DebugAction(Category, "Dormancy Report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DormancyReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RimExodus Dormancy Report]");
            sb.AppendLine($"  maps total: {Find.Maps.Count} (Game.AddMap limit 127)");
            var dormant = 0;
            var governed = 0;
            foreach (var m in Find.Maps)
            {
                if (SeamlessMapGovernance.IsGoverned(m)) governed++;
                if (SeamlessDormancyManager.IsDormant(m)) dormant++;
            }
            sb.AppendLine($"  governed maps (tile ∪ native family): {governed}   dormant: {dormant}");
            var s = RimExodusMod.Settings;
            sb.AppendLine($"  governor: enabled={s?.dormancyEnabled ?? true} sleepHops={s?.dormancySleepHops ?? 2} deleteHops={s?.dormancyDeleteHops ?? 3}");
            Log.Message(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// 调试工具（Dev 地图工具）：点击地图格，输出该格的**结构化接缝诊断报告**
        /// （<see cref="SeamlessSeamOverride.DescribeCellReport"/>）：本侧 + 各对侧对应位置两栏，
        /// 每侧 = 位置（坐标+圈层）/ 生成时 snapshot（389 三层原生备份）/ 当前实际（三层现值）；
        /// 本侧另附混合情况追踪（权重 / 两侧各层 3×3 卷积 / 各层混合结果），对侧附条带快照参考值。
        /// 用于精确定位"某格 snapshot 是水/沙，但被邻居土卷积成了泥"、孤儿岩墙等接缝混合问题。
        /// </summary>
        [DebugAction(Category, "Inspect Snapshot At Position", false, false, false, false, false, 0, false,
            actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectSnapshotAtPosition()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            var cell = UI.MouseCell();
            if (!cell.InBounds(map)) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[RimExodus-SnapshotInspect] map={map.uniqueID} cell=({cell.x},{cell.z}) wt={worldTile}");

            if (worldTile < 0)
            {
                sb.AppendLine("  (no valid worldTile, no polygon/neighbor info)");
            }
            else
            {
                sb.Append(SeamlessSeamOverride.DescribeCellReport(map, worldTile, cell));
            }

            Log.Message(sb.ToString().TrimEnd());
        }
    }
}
