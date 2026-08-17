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
    /// 供 GenStep_SeamlessTile（order=1400）调用，该 genStep 通过 XML patch 注入到所有
    /// 玩家可进入的 MapGeneratorDef（Base_Player / Base_Faction / Encounter）。邻居地块（MapParent_SeamlessTile）
    /// 的 mapGenerator 也是 Base_Player，与锚点家园 A 同链。
    /// </summary>
    public static class SeamlessTerrainFill
    {
        /// <summary>
        /// 备份基础快照（void 裁切前，平行三层：terrain / building / roof）并按接缝带几何铺 void。
        ///
        /// **两入口共用同一逻辑体**（归一点）：锚点家园 A 与邻接地块 B 的 void 铺设都走本方法。
        /// 快照存储位置随载体类型自动选择（地块存 MapParent_SeamlessTile，
        /// 锚点存 SeamlessTileManager），其余完全一致——避免两份重复的"备份+铺void"逻辑漂移。
        ///
        /// 三层同点位备份（均非序列化——跨读档的接缝参考由序列化的 SeamStripData 承担）。
        /// void 铺设会清掉接缝带外格的岩体与屋顶，SeamStripData 对外条带格必须引用**原生**
        /// 三层（同源）。**清理全部归 ApplyPolygonTerrain（依次清 roof → rock → terrain，
        /// 用户定夺 2026-08）**：原生数据先落快照再被清理，时序天然安全。历史教训：曾在
        /// order 200 patch 提前清岩，外条带原生岩体在备份前丢失 → 新图照抄区岩壁整齐切断。
        /// </summary>
        public static void BackupSnapshotAndApplyVoid(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            if (map.Parent is MapParent_SeamlessTile tile)
            {
                tile.baseTerrainSnapshot = (TerrainDef[])map.terrainGrid.topGrid.Clone();
                tile.baseBuildingSnapshot = BackupBuildingSnapshot(map);
                tile.baseRoofSnapshot = BackupRoofSnapshot(map);
            }
            else
            {
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager != null)
                {
                    manager.anchorBaseTerrainSnapshot = (TerrainDef[])map.terrainGrid.topGrid.Clone();
                    manager.anchorBaseBuildingSnapshot = BackupBuildingSnapshot(map);
                    manager.anchorBaseRoofSnapshot = BackupRoofSnapshot(map);
                }
            }

            ApplyPolygonTerrain(map, worldTile);
        }

        /// <summary>
        /// 备份全图建筑层快照（格 → 岩石体 BuildingDef，null=无）。遍历 listerThings 而非逐格
        /// GetEdifice（岩石数量级几千，远小于全图 62500 格）。
        /// </summary>
        private static ThingDef[] BackupBuildingSnapshot(Map map)
        {
            var defs = new ThingDef[map.Size.x * map.Size.z];
            var indices = map.cellIndices;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Building b || !thing.Spawned) continue;
                if (b.def.building == null || b.def.building.naturalTerrain == null) continue;
                var idx = indices.CellToIndex(thing.Position);
                if (idx >= 0 && idx < defs.Length) defs[idx] = b.def;
            }
            return defs;
        }

        /// <summary>
        /// 备份全图屋顶层快照（格 → RoofDef，null=无）。RoofGrid 内部数组私有，逐格 RoofAt
        /// （生成期一次性全图遍历，可接受）。
        /// </summary>
        private static RoofDef[] BackupRoofSnapshot(Map map)
        {
            var defs = new RoofDef[map.Size.x * map.Size.z];
            var indices = map.cellIndices;
            var roofGrid = map.roofGrid;
            foreach (var c in map.AllCells)
            {
                defs[indices.CellToIndex(c)] = roofGrid.RoofAt(c);
            }
            return defs;
        }

        /// <summary>
        /// 按 worldTile 的接缝带几何铺 void：接缝带外（格中心在多边形外且 ∉ 接缝带 B）铺 void，
        /// 其余（核心区 + 接缝带三圈，含带外圈）保持原生地形。
        /// void 格铺 RimExodus_Void 并清除其上的实体与 Pawn。
        ///
        /// 【新定义（doc/接缝带定义.md）】带外圈（离散边外侧一圈）从 void 变为实地形——
        /// 传送圈 = 离散边圈 ∪ 带外圈（外侧 2 圈），3 圈带全实地形使传送落点 ±1 格偏差
        /// （连续边中点对齐的取整残差）落在对侧带内/带外圈，不会传送到 void。
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
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, size.x);

            // 逐格判定：void = 接缝带外（格中心在多边形外且 ∉ B）。
            // 防孤岛等价性：凸多边形下角在内的格必属 {中心在内} ∪ 离散边圈（⊆ B），
            // 旧"中心或 4 角任一在内"角检测的非 void 集被 {中心在内} ∪ B 完全覆盖且多出带外圈（有意）。
            var voidCells = new List<IntVec3>();

            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (SeamlessPolygonGeometry.IsVoidCell(band, worldTile, size.x, cell)) voidCells.Add(cell);
                }
            }

            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            if (verbose)
                Log.Message($"[RimExodus] ApplyPolygonTerrain worldTile={worldTile} map={map.uniqueID} size={size.x} voidCells={voidCells.Count} nonVoid={size.x*size.z - voidCells.Count}");
            var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
            long tClear = 0, tTerrain = 0, tEvac = 0, tClassify = 0, tRoof = 0;
            if (sw != null) { tClassify = sw.ElapsedMilliseconds; }

            // 依次清 roof → rock（实体）→ terrain（用户定夺 2026-08；快照已在备份后，时序安全）。
            // 先清屋顶（RocksFromGrid 设的 RoofRockThick/Thin）。
            foreach (var cell in voidCells)
            {
                if (map.roofGrid.RoofAt(cell) != null)
                {
                    map.roofGrid.SetRoof(cell, null);
                }
            }
            if (sw != null) { tRoof = sw.ElapsedMilliseconds - tClassify; }

            // 再清除虚空格上的实体（岩石 Building/植物/物品等），保留 Pawn（Pawn 单独处理）。
            ClearThingsOnCells(map, voidCells);
            if (sw != null) { tClear = sw.ElapsedMilliseconds - tRoof - tClassify; }

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
        /// **当前时序下的实际工作量**：void 在 order=1400（Fog 之前）铺，此时 Plants(900)/Animals(1200)/Snow(1150) 已 spawn。
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
