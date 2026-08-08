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
    /// 供 GenStep（口袋地块）和 SeamlessTileManager.RefreshMapVoid 复用。
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

            // 收集六边形所有边经过的格（Bresenham 沿每条边）。
            // 这些"边格"是非 void（传送点铺在这里，且两端边格通过 offset 对齐重合）。
            var edgeCells = new HashSet<IntVec3>();
            for (var j = 0; j < verts.Count; j++)
            {
                foreach (var cell in SeamlessPolygonGeometry.EnumerateEdgeCells(verts, j, size.x))
                {
                    if (cell.InBounds(map)) edgeCells.Add(cell);
                }
            }

            var voidCells = new List<IntVec3>();

            // 逐格判定：六边形内（含边）或边格上 → 非 void；否则 void。
            // 不查邻居——void 只取决于自己的六边形。两个地图各自独立铺 void，
            // A 的 void 区域在 B 地图上恰好是 B 的非 void 区域（被 B 六边形覆盖）。
            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (edgeCells.Contains(cell)) continue; // 边格：非 void
                    if (!SeamlessPolygonGeometry.ContainsPoint(verts, size.x, cell))
                    {
                        voidCells.Add(cell);
                    }
                }
            }

            Log.Message($"[RimExodus] ApplyPolygonTerrain worldTile={worldTile} map={map.uniqueID} size={size.x} voidCells={voidCells.Count}");

            // 先清除虚空格上的实体（建筑/岩石/植物/物品等），再铺虚空地形。
            ClearThingsOnCells(map, voidCells);

            // 铺虚空地形。
            var terrainGrid = map.terrainGrid;
            foreach (var cell in voidCells)
            {
                terrainGrid.SetTerrain(cell, voidDef);
            }

            // 清除生成在虚空格上的 Pawn。
            EvacuatePawnsOnCells(map, voidCells);
        }

        /// <summary>清除指定格集合上的所有实体（建筑/岩石/植物/物品/草丛等），保留 Pawn（Pawn 单独处理）。</summary>
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
