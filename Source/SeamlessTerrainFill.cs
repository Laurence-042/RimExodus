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
    /// 供两类 genStep 介入路径复用：
    /// - 邻接地块：<see cref="GenStep_SeamlessTile"/>（order=211，Terrain 之后、Plants 之前）。
    /// - 锚点家园：<c>Patch_GenStep_MutatorPostTerrain</c> Postfix（order=220，同窗口）。
    /// </summary>
    public static class SeamlessTerrainFill
    {
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
        /// **当前对所有 genStep 路径都是空操作/近空操作**：
        /// - 邻接地块：Patch_GenStep_RocksFromGrid（Postfix，order~200）已清掉 void 格岩石/屋顶，
        ///   等 RimExodus_SeamlessTile（order=211）调本方法时已无 Thing 可清。
        /// - 锚点家园：Patch_GenStep_RocksFromGrid 同样提前清岩石（已扩展到锚点），
        ///   Patch_GenStep_MutatorPostTerrain（order=220）调本方法时 Plants/Animals 还没 spawn，也无 Thing 可清。
        /// 本方法保留作为防御性清理（如读档重建或未来运行时刷新场景）。
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
