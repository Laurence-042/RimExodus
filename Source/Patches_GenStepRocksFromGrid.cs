using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Postfix GenStep_RocksFromGrid.Generate：清除 void 格上的岩石和屋顶。
    ///
    /// RocksFromGrid（order~200）只读 elevation grid，不看 terrain，不知道哪些格是 void。
    /// 它会在 elevation>0.7 的格上 spawn 岩石（含 void 格）+ 设屋顶。
    /// 本 Postfix 在它完成后，把 void 格上的岩石清除 + 屋顶移除，使 void 区域干净无落石。
    ///
    /// 这比 RimExodus_SeamlessTile（order=211）的 ClearThingsOnCells 更早处理岩石，
    /// 确保 Terrain/Plants/RockChunks 等后续 genStep 不在 void 格上遇到岩石。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_RocksFromGrid), nameof(GenStep_RocksFromGrid.Generate))]
    static class Patch_GenStep_RocksFromGrid
    {
        static void Postfix(Map map)
        {
            // 仅对 RimExodus 口袋地图生效。
            if (!(map.Parent is MapParent_SeamlessTile seamlessParent)) return;
            if (seamlessParent.worldTile < 0) return;

            // 计算多边形外的 void 格（与 ApplyPolygonTerrain 同逻辑）。
            var size = map.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(seamlessParent.worldTile, size.x);
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
            // void 地形由 RimExodus_SeamlessTile（order=211，Terrain 之后）铺。
            // 本 Postfix 只清岩石和屋顶，让 RimExodus_SeamlessTile 的 ClearThingsOnCells 变成空操作（此时已无 Thing）。

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Patch_GenStep_RocksFromGrid: cleared {toRemove.Count} things + roofs from {voidCells.Count} void cells.");
        }
    }
}
