using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 卸载前恢复原版兼容模式（2026-08）。入口 = Mod 设置"高级诊断"tab 按钮（二次确认）。
    ///
    /// **为什么必须"卸载前"**：mod 移除后存档里的 <see cref="MapParent_SeamlessTile"/> WorldObject
    /// 类解析失败 → 地图 info.parent == null → <c>Map.Tile = PlanetTile.Invalid(-1)</c> →
    /// <c>WorldGrid[-1]</c> 越界（每帧 UI 读 CurrentMap.Biome 触发循环红字）；且 RimExodus_Void
    /// TerrainDef 失落。恢复只能在 mod 在场时执行——地形改写、地块图销毁全依赖本 mod 代码。
    ///
    /// 流程（<see cref="RunRestore"/>）：
    /// ①前置检查（分帧生成进行中 / 地块图上有玩家 pawn → 拒绝并提示撤离；CurrentMap 是地块图 → 切走）；
    /// ②就地恢复全部原生 parent 图（parent 本就是原版类型，卸载后天然无恙——只需清除我方实体足迹）：
    ///   void 格 terrain 按三层快照回填（SetTerrain，经 <see cref="Restoring"/> 旗标放行
    ///   <c>Patch_TerrainGrid_SetTerrain</c> 的 void 不可变守卫）+ roof 回填 + 自然岩石重生
    ///   （快照有岩 def 且当前格无建筑才补——玩家建造格跳过）；无快照（开关关后生成/旧档）降级
    ///   "就近复制边界地形"（全图 BFS 一次，8 邻 = GenAdj.AdjacentCells 口径）；
    ///   删除 RimExodus_SeamlessEnterSpot / RimExodus_VoidRockLink thing；
    ///   清 pawn 的 RimExodus job（TalkWithSettlementTrader）与 TransitTag job、贸易商引用；
    /// ③删除全部地块图（复用 <see cref="SeamlessTileManager.RemoveTileMap" /> "从未出现过"语义）；
    /// ④全局善后（拆影子 / 清手动休眠锁 / 天气域 RebindAll）。
    ///
    /// **只恢复 void 格**（不做全图归一）：接缝带混合只改非 void 地形/岩石（原版可加载，纯观感差异），
    /// 而 void 是唯一卸载后报 def 失落的足迹；玩家不可达 void（不可通行）故无玩家建造可破坏。
    /// </summary>
    internal static class SeamlessUninstallRestore
    {
        /// <summary>
        /// 恢复进行中旗标：<see cref="Patch_TerrainGrid_SetTerrain"/> 的"void 不可变"守卫在此放行
        /// （恢复本身就是把 void 改回原生地形的唯一合法场景）。
        /// </summary>
        internal static bool Restoring;

        /// <summary>
        /// 执行恢复（设置按钮 → 确认对话框后调用）。任一前置失败弹拒绝 Message 并原样返回（可撤离后重试）。
        /// </summary>
        public static void RunRestore()
        {
            // ① 分帧生成进行中：半成品图不可恢复也不可安全删除（与"生成中拒绝存档"同族守卫）。
            if (IncrementalMapGenerator.IsAnyGenerating)
            {
                Messages.Message("RimExodus_UninstallRestoreBlockedGenerating".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            // ② 地块图上有玩家阵营 pawn：删除会丢人，拒绝并列出 tile 让玩家先撤离。
            var tileParents = new List<MapParent_SeamlessTile>();
            foreach (var mp in Find.WorldObjects.MapParents)
            {
                if (mp is MapParent_SeamlessTile tileParent && !tileParent.Destroyed)
                    tileParents.Add(tileParent);
            }

            var occupiedTiles = new List<string>();
            foreach (var tp in tileParents)
            {
                var m = tp.Map;
                if (m == null) continue;
                foreach (var p in m.mapPawns.AllPawnsSpawned)
                {
                    if (p.Faction == Faction.OfPlayer)
                    {
                        // 报地块经纬度而非 tile id（与原版世界地图检视面板同款格式，玩家可直接对照找图）。
                        var latLong = Find.WorldGrid.LongLatOf(tp.Tile);
                        occupiedTiles.Add(latLong.y.ToStringLatitude() + " " + latLong.x.ToStringLongitude());
                        break;
                    }
                }
            }
            if (occupiedTiles.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var t in occupiedTiles) sb.Append(t).Append(", ");
                Messages.Message("RimExodus_UninstallRestoreBlockedPawns".Translate(sb.ToString().TrimEnd(',', ' ')),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            // ③ CurrentMap 是地块图：先切走（家园优先，其次任一存留图），否则删图瞬间视图悬空。
            if (Find.CurrentMap != null && Find.CurrentMap.Parent is MapParent_SeamlessTile)
            {
                var fallbackMap = Find.Maps.FirstOrDefault(m => m.IsPlayerHome) ?? Find.Maps.FirstOrDefault(
                    m => m.Parent is not MapParent_SeamlessTile);
                if (fallbackMap != null) Current.Game.CurrentMap = fallbackMap;
            }

            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var restoredMaps = 0;
            var fallbackMaps = 0;

            // ④ 就地恢复全部原生 parent 图。
            foreach (var map in Find.Maps)
            {
                if (map == null || map.Disposed) continue;
                if (map.Parent is MapParent_SeamlessTile) continue; // 地块图走删除，不恢复。
                if (!HasRimExodusFootprint(map)) continue;

                var usedFallback = RestoreNativeMap(map);
                if (usedFallback) fallbackMaps++; else restoredMaps++;
                if (verbose)
                    Log.Message($"[RimExodus] Uninstall restore: map wt={SeamlessTileRegistry.GetMapWorldTile(map)} " +
                                $"restored ({(usedFallback ? "nearest-copy fallback" : "snapshot")}).");
            }

            // ⑤ 删除全部地块图（快照列表遍历，删除中遍历安全）。
            foreach (var tp in tileParents)
            {
                // RemoveTileMap 是 SeamlessTileManager 实例方法但方法体只用全局态——任一图的组件皆可代调；
                // 地块图已销毁（map null）的孤儿 parent 也走 Destroy 分支。
                var hostManager = Find.Maps.FirstOrDefault()?.GetComponent<SeamlessTileManager>();
                if (tp.Map != null)
                    hostManager = tp.Map.GetComponent<SeamlessTileManager>();
                hostManager?.RemoveTileMap(tp);
            }

            // ⑥ 全局善后：影子拆除、手动休眠锁清空（所锁图多半已删，锁本身也随 mod 消失无意义）、天气域重算。
            SeamlessShadowCaravan.EnsureReleasedForSave();
            Current.Game?.GetComponent<SeamlessDormancyGovernor>()?.ClearAllManualDormantForRestore();
            SeamlessWeatherClusterManager.RebindAll();


            Messages.Message("RimExodus_UninstallRestoreDone".Translate(
                    restoredMaps.ToString(), fallbackMaps.ToString()),
                MessageTypeDefOf.PositiveEvent, false);
            Log.Message($"[RimExodus] Uninstall restore complete: {restoredMaps} map(s) snapshot-restored, " +
                        $"{fallbackMaps} nearest-copy fallback, {tileParents.Count} tile map(s) deleted. " +
                        "Save the game now, then RimExodus can be safely removed.");
        }


        /// <summary>
        /// 图上是否有 RimExodus 足迹（有 void 地形 / 有传送点或岩链 thing / 有快照数据）。
        /// 无足迹的图（如卸载后新开的图、口袋图）零触碰。
        /// </summary>
        private static bool HasRimExodusFootprint(Map map)
        {
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef != null)
            {
                foreach (var t in map.terrainGrid.topGrid)
                {
                    if (t == voidDef) return true;
                }
            }
            if (map.GetComponent<SeamlessTileManager>()?.baseSnapshotData != null) return true;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.def.defName == "RimExodus_SeamlessEnterSpot"
                    || thing.def.defName == "RimExodus_VoidRockLink")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 恢复一张原生 parent 图；返回是否走了"就近复制"降级（无快照）。
        /// </summary>
        private static bool RestoreNativeMap(Map map)
        {
            Restoring = true;
            try
            {
                var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
                var indices = map.cellIndices;
                var terrain = SeamlessMapData.GetBaseTerrainSnapshot(map);
                var building = SeamlessMapData.GetBaseBuildingSnapshot(map);
                var roof = SeamlessMapData.GetBaseRoofSnapshot(map);
                var hasSnapshot = terrain != null && building != null && roof != null
                                  && terrain.Length == indices.NumGridCells;
                var fallbackFill = hasSnapshot ? null : BuildNearestTerrainFill(map, voidDef);

                var changed = false;
                var rocksSpawned = 0;
                foreach (var cell in map.AllCells)
                {
                    if (voidDef == null || map.terrainGrid.TerrainAt(cell) != voidDef) continue;
                    var idx = indices.CellToIndex(cell);

                    // terrain 回填：快照值或就近复制结果；两者皆无（异常态）保底 Soil 防留 void。
                    var newTerrain = hasSnapshot ? terrain[idx]
                        : (fallbackFill != null ? fallbackFill[idx] : TerrainDefOf.Soil);
                    if (newTerrain == null) newTerrain = TerrainDefOf.Soil;
                    map.terrainGrid.SetTerrain(cell, newTerrain);
                    changed = true;

                    if (hasSnapshot)
                    {
                        // roof 回填（仅 void 格——接缝带混合差异属观感，不做全图归一）。
                        var newRoof = roof[idx];
                        if (newRoof != null && map.roofGrid.RoofAt(cell) != newRoof)
                            map.roofGrid.SetRoof(cell, newRoof);

                        // 自然岩石重生：快照该格有岩 def 且当前无建筑（玩家不可达 void，防御性仍查）。
                        var rockDef = building[idx];
                        if (rockDef != null && map.edificeGrid[cell] == null)
                        {
                            var rock = ThingMaker.MakeThing(rockDef);
                            GenSpawn.Spawn(rock, cell, map);
                            rocksSpawned++;
                        }
                    }
                }

                // RimExodus thing 足迹清除（传送点 / 岩链——全图扫，不限于 void 格）。
                // 两 def 均 destroyable=false（防事件误毁，def 注释明示清理走 DeSpawn）——DeSpawn 摘出
                // 注册表后无 holder 的 thing 不进存档，卸 mod 后零残留。
                var toRemove = new List<Thing>();
                foreach (var thing in map.listerThings.AllThings)
                {
                    if (thing.def.defName == "RimExodus_SeamlessEnterSpot"
                        || thing.def.defName == "RimExodus_VoidRockLink")
                        toRemove.Add(thing);
                }
                foreach (var thing in toRemove)
                {
                    if (thing.Spawned) thing.DeSpawn();
                }

                // pawn 面：RimExodus job 结束（TalkWithSettlementTrader / TransitTag Goto）、贸易商引用清空。
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    var job = pawn.jobs?.curJob;
                    if (job != null && (job.def.defName == "RimExodus_TalkWithSettlementTrader"
                                        || job.dutyTag == SeamlessTransferGrants.TransitTag))
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
                    }
                }
                var traderComp = map.GetComponent<SeamlessSettlementTraderComp>();
                if (traderComp != null) traderComp.traderPawn = null;


                if (changed)
                {
                    // SetTerrain 自带逐格簿记（含 mesh/region 标脏），void 大区回填后全量重算一次保险
                    //（与 391 genStep 同款收尾）。
                    map.pathing.RecalculateAllPerceivedPathCosts();
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                }
                if ((RimExodusMod.Settings?.verboseLogging ?? false) && (changed || rocksSpawned > 0))
                    Log.Message($"[RimExodus] Uninstall restore map {map.uniqueID}: rocks respawned={rocksSpawned}, things removed={toRemove.Count}.");
                return !hasSnapshot;
            }
            finally
            {
                Restoring = false;
            }
        }

        /// <summary>
        /// 无快照降级：全图 BFS 一次（多源 8 邻 = <see cref="GenAdj.AdjacentCells"/> 口径），
        /// 每个 void 格取最近非 void 源格的地形。观感近似（边界地形向外延伸），roof/岩体不恢复。
        /// </summary>
        private static TerrainDef[] BuildNearestTerrainFill(Map map, TerrainDef voidDef)
        {
            if (voidDef == null) return null;
            var grid = map.terrainGrid.topGrid;
            var count = grid.Length;
            var result = new TerrainDef[count];
            var queue = new Queue<int>();
            for (var i = 0; i < count; i++)
            {
                if (grid[i] != voidDef) queue.Enqueue(i);
            }
            var mapSize = map.Size;
            var dirs = GenAdj.AdjacentCells;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                var srcTerrain = grid[i];
                var x = i % mapSize.x;
                var z = i / mapSize.x;
                foreach (var dir in dirs)
                {
                    var nx = x + dir.x;
                    var nz = z + dir.z;
                    if (nx < 0 || nx >= mapSize.x || nz < 0 || nz >= mapSize.z) continue;
                    var ni = nz * mapSize.x + nx;
                    if (grid[ni] != voidDef || result[ni] != null) continue;
                    result[ni] = srcTerrain;
                    queue.Enqueue(ni);
                }
            }
            return result;
        }

        /// <summary>
        /// 清除当前存档中全部已序列化快照（设置 UI"关开关"二次确认流程的第二层动作，可选执行）。
        /// 同时清内存数组——否则下次保存时捕获条件（snapshotData==null 且数组非 null）会重新捕入。
        /// 混合参考回落当前实况（既有降级路径），当次会话接缝观感可能轻微变化，接受。
        /// </summary>
        internal static void PurgeAllSnapshots()
        {
            var purged = 0;
            foreach (var map in Find.Maps)
            {
                if (map == null || map.Disposed) continue;
                if (map.Parent is MapParent_SeamlessTile) continue;
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager == null) continue;
                if (manager.baseSnapshotData == null
                    && manager.baseTerrainSnapshot == null
                    && manager.baseBuildingSnapshot == null
                    && manager.baseRoofSnapshot == null) continue;
                manager.baseSnapshotData = null;
                manager.baseTerrainSnapshot = null;
                manager.baseBuildingSnapshot = null;
                manager.baseRoofSnapshot = null;
                purged++;
            }
            Log.Message($"[RimExodus] Purged serialized base snapshots on {purged} map(s). " +
                        "They will be dropped from the save on next save.");
            Messages.Message("RimExodus_SnapshotPurged".Translate(purged.ToString()),
                MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
