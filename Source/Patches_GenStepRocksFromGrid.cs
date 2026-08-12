using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Postfix GenStep_RocksFromGrid.Generate：提前清除"将来会是 void 的格"上的岩石和屋顶。
    ///
    /// RocksFromGrid（order=200）只读 elevation grid，不看 terrain，不知道哪些格是 void。
    /// 它会在 elevation>0.7 的格上 spawn 岩石（含将来 void 的格）+ 设屋顶。
    /// 本 Postfix 在它完成后，用 IsCellInPolygon 算出多边形外的格（即将来 void 格），
    /// 提前清除上面的岩石 Building + 移除屋顶，使 void 区域干净。
    ///
    /// **时序**：void 地形由 RimExodus_SeamlessTile（order=1802）铺，远在 RocksFromGrid(200) 之后。
    /// 本 Postfix（order=200，Terrain 之前）提前清岩石，避免 1802 时 ClearThingsOnCells
    /// 处理海量岩石 Building 的开销。Plants/Animals 在 900/1200 spawn，仍会在将来 void 格上生成，
    /// 由 ApplyPolygonTerrain(1802) 的 ClearThingsOnCells/EvacuatePawnsOnCells 事后清理。
    ///
    /// **统一守卫**：用 GetMapWorldTile(map) >= 0，与三个 RimExodus genStep 一致——
    /// 任何有合法 worldTile 的地图（邻接地块/锚点家园/派系基地/遭遇）都走本清理。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_RocksFromGrid), nameof(GenStep_RocksFromGrid.Generate))]
    static class Patch_GenStep_RocksFromGrid
    {
        static void Postfix(Map map)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            // 计算多边形外的 void 格（与 ApplyPolygonTerrain 同逻辑）。
            var size = map.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, size.x);
            if (verts.Count == 0) return;

            var voidCells = new List<IntVec3>();
            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!SeamlessPolygonGeometry.IsCellInPolygon(verts, size.x, cell))
                    {
                        voidCells.Add(cell);
                    }
                }
            }

            if (voidCells.Count == 0) return;

            // 清除 void 格上的岩石（Building，destroyable）。
            var cellSet = new HashSet<IntVec3>(voidCells);
            var toRemove = new List<Thing>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is Pawn) continue;
                if (!thing.Spawned) continue;
                if (cellSet.Contains(thing.Position))
                {
                    toRemove.Add(thing);
                }
            }
            foreach (var thing in toRemove)
            {
                if (thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
                else if (thing.Spawned)
                {
                    thing.DeSpawn();
                }
            }

            // 移除 void 格的屋顶（RocksFromGrid 设的 RoofRockThick/Thin）。
            foreach (var cell in voidCells)
            {
                if (map.roofGrid.RoofAt(cell) != null)
                {
                    map.roofGrid.SetRoof(cell, null);
                }
            }

            // 注意：不铺 void 地形（Terrain genStep 还没跑，此时铺会覆盖 elevation 判定）。
            // void 地形由 RimExodus_SeamlessTile（order=1802，Fog 之后）铺。
            // 本 Postfix 只清岩石和屋顶，减少 1802 时 ClearThingsOnCells 的工作量（岩石已无，主要剩植物/物品）。

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Patch_GenStep_RocksFromGrid: cleared {toRemove.Count} things + roofs from {voidCells.Count} void cells.");
        }
    }
}
