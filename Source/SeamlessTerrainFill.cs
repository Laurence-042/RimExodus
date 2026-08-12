using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 多边形地形铺设工具（阶段3：多边形裁切）。
    /// 每个地块独立按自己的六边形铺 void：六边形内（含边）非 void，六边形外 void。
    /// 不看邻居——两个地图各自独立铺 void，重叠区的 void 在对方地图上恰好是非 void。
    /// 传送点铺在自己六边形的边经过的格子上（含边判定 → 非 void）。
    /// 供 GenStep_SeamlessTile（order=1802）调用，该 genStep 通过 XML patch 注入到所有
    /// 玩家可进入的 MapGeneratorDef（Base_Player / Base_Faction / Encounter）。邻居地块（MapParent_SeamlessTile）
    /// 的 mapGenerator 也是 Base_Player，与锚点家园 A 同链。
    /// </summary>
    public static class SeamlessTerrainFill
    {
        /// <summary>
        /// 备份基础地形 snapshot（void 裁切前的完整矩形 topGrid）并按六边形铺 void。
        ///
        /// **两入口共用同一逻辑体**（归一点）：锚点家园 A 与邻接地块 B 的 void 铺设都走本方法。
        /// snapshot 存储位置随载体类型自动选择（地块存 MapParent_SeamlessTile，
        /// 锚点存 SeamlessTileManager），其余完全一致——避免两份重复的"备份+铺void"逻辑漂移。
        ///
        /// snapshot 用途：供 <see cref="SeamlessSeamOverride"/> 卷积混合读取对端真实地形。
        /// </summary>
        public static void BackupSnapshotAndApplyVoid(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            // 备份基础地形（void 裁切前的完整矩形 topGrid）。
            // 探针：Clone 前记录六边形外格地形（snapshot 即将复制这些格，它们给邻居 SeamOverride 用）。
            // 此时 void 未铺，LogBandTerrain（依赖 void）不可用，用 LogOutsidePolygonTerrain（不依赖 void）。
            SeamTerrainProbe.LogOutsidePolygonTerrain(map, worldTile, "1802-pre-snapshot");

            TerrainDef[] snapshotCopy = null;
            if (map.Parent is MapParent_SeamlessTile pocket)
            {
                pocket.baseTerrainSnapshot = (TerrainDef[])map.terrainGrid.topGrid.Clone();
                snapshotCopy = pocket.baseTerrainSnapshot;
            }
            else
            {
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager != null && manager.anchorBaseTerrainSnapshot == null)
                {
                    manager.anchorBaseTerrainSnapshot = (TerrainDef[])map.terrainGrid.topGrid.Clone();
                    snapshotCopy = manager.anchorBaseTerrainSnapshot;
                }
                else if (manager != null)
                {
                    snapshotCopy = manager.anchorBaseTerrainSnapshot;
                }
            }

            // 探针：Clone 后验证 snapshot 数组内容与 topGrid 一致。
            SeamTerrainProbe.LogSnapshotBand(map, worldTile, snapshotCopy, "1802-post-snapshot");
            // 探针：全图地形分布（区分锚点/口袋），判断拍摄时 topGrid 是否已是海岸填充后的状态。
            SeamTerrainProbe.LogSnapshotFullMap(map, worldTile, snapshotCopy, "1802-snapshot");

            ApplyPolygonTerrain(map, worldTile);
        }

        /// <summary>
        /// 按 worldTile 的六边形铺 void：六边形内（含边）非 void，六边形外 void。
        /// void 格铺 RimExodus_Void 并清除其上的实体与 Pawn。
        /// </summary>
        public static void ApplyPolygonTerrain(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null)
            {
                Log.Error("[RimExodus] RimExodus_Void terrain not found, cannot apply polygon fill.");
                return;
            }

            var size = map.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, size.x);
            if (verts.Count == 0) return;

            // 逐格判定：格中心或 4 角任一在多边形内 → 非 void；否则 void。
            // 格角检测保证：格面积与多边形有交集 → 非 void，消除边界附近的 void 孤岛（切断 region）。
            // 不需要单独的 Bresenham 边格补丁——格角检测覆盖了边附近的格。
            // 不查邻居——void 只取决于自己的六边形。
            var voidCells = new List<IntVec3>();

            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (SeamlessPolygonGeometry.IsCellInPolygon(verts, size.x, cell)) continue; // 多边形内（含边附近）：非 void
                    voidCells.Add(cell);
                }
            }

            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            if (verbose)
                Log.Message($"[RimExodus] ApplyPolygonTerrain worldTile={worldTile} map={map.uniqueID} size={size.x} voidCells={voidCells.Count} nonVoid={size.x*size.z - voidCells.Count}");
            var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
            long tClear = 0, tTerrain = 0, tEvac = 0, tClassify = 0;
            if (sw != null) { tClassify = sw.ElapsedMilliseconds; }

            // 先清除虚空格上的实体（建筑/岩石/植物/物品等），再铺虚空地形。
            ClearThingsOnCells(map, voidCells);
            if (sw != null) { tClear = sw.ElapsedMilliseconds - tClassify; }

            // 铺虚空地形：直接写 topGrid（公开字段 TerrainGrid.cs:13）跳过 SetTerrain 的重计算副作用，
            // 只保留渲染必需的 mesh 脏标记。void 地形 dontRender=true、passability=Impassable、不发光、
            // 不是水、layerable=false。SetTerrain 的 DoTerrainChangedEffects 把 mesh 标记（必需）和
            // pathGrid/waterBodyTracker 重算（非必需且耗时）混在一起，21000 次 × 副作用 = 5.7 秒。
            // 这里只做 mesh 标记，pathGrid 由 FinalizeInit 全量重算覆盖。
            // regenAdjacentCells=false：void 格大面积连续，邻格也是 void 或边格，不需逐格扩散 dirty 标记，
            // FinalizeInit 的 RegenerateEverythingNow 会全量重建 mesh。
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid; // public TerrainDef[]（TerrainGrid.cs:13）
            var mapDrawer = map.mapDrawer;
            foreach (var cell in voidCells)
            {
                var idx = cellIndices.CellToIndex(cell);
                topGrid[idx] = voidDef;
                mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
            }
            if (sw != null) { tTerrain = sw.ElapsedMilliseconds - tClear - tClassify; }

            // 清除生成在虚空格上的 Pawn。
            EvacuatePawnsOnCells(map, voidCells);
            if (sw != null)
            {
                tEvac = sw.ElapsedMilliseconds - tTerrain - tClear - tClassify;
                sw.Stop();
                Log.Message($"[RimExodus] ApplyPolygonTerrain timings: classify={tClassify}ms clear={tClear}ms terrain={tTerrain}ms evac={tEvac}ms");
            }
        }

        /// <summary>
        /// 清除指定格集合上的所有实体（建筑/岩石/植物/物品/草丛等），保留 Pawn（Pawn 单独处理）。
        ///
        /// **当前时序下的实际工作量**：void 在 order=1802（Fog 之后）铺，此时 Plants(900)/Animals(1200) 已 spawn。
        /// - 岩石 Building：Patch_GenStep_RocksFromGrid（Postfix，order=200）已提前清除（用 IsCellInPolygon 算
        ///   将来 void 格），故本方法处理岩石时基本为空操作。
        /// - 植物/物品：Plants(900) spawn 在将来 void 格上的会被本方法实际清理（Destroy Vanish）。
        /// 因此本方法不是空操作——主要清理植物和散落物品。清理开销在生成时一次性发生。
        /// </summary>
        private static void ClearThingsOnCells(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return;

            var cellSet = new HashSet<IntVec3>(cells);

            var toDestroy = new List<Thing>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is Pawn) continue;
                if (!thing.Spawned) continue;
                if (cellSet.Contains(thing.Position))
                {
                    toDestroy.Add(thing);
                }
            }

            foreach (var thing in toDestroy)
            {
                // 用 Destroy（Vanish）彻底移除；对 destroyable=false 的（如 SteamGeyser）改用 DeSpawn。
                if (thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
                else if (thing.Spawned)
                {
                    thing.DeSpawn();
                }
            }
        }

        /// <summary>把生成在虚空格上的 Pawn 移到最近的可通行格（避免它们卡在不可通行地形上）。</summary>
        private static void EvacuatePawnsOnCells(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return;

            var cellSet = new HashSet<IntVec3>(cells);
            var pawnsToMove = new List<Pawn>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (cellSet.Contains(pawn.Position))
                {
                    pawnsToMove.Add(pawn);
                }
            }

            foreach (var pawn in pawnsToMove)
            {
                var dest = FindNearestWalkable(map, pawn.Position, 10);
                if (dest.IsValid)
                {
                    pawn.Position = dest;
                }
                else
                {
                    if (!pawn.RaceProps.Humanlike)
                    {
                        pawn.Destroy(DestroyMode.Vanish);
                    }
                }
            }
        }

        private static IntVec3 FindNearestWalkable(Map map, IntVec3 center, int maxRadius)
        {
            var cellCount = GenRadial.NumCellsInRadius(maxRadius);
            for (var i = 0; i < cellCount; i++)
            {
                var c = center + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && c.Walkable(map))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
