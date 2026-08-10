using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 【实验分支】在 GenStep_Terrain 内部铺 void，取代独立的后置 GenStep（RimExodus_SeamlessTile）。
    ///
    /// 原方案的性能问题：独立 genStep（order=1000）事后铺 void 时，void 格上已 spawn 了 RocksFromGrid/
    /// Plants/RockChunks 的 Thing，ClearThingsOnCells 需逐个 Destroy（实测 5000-9000ms）。
    ///
    /// 新方案：Postfix GenStep_Terrain.Generate，在原版逐格 SetTerrain 后立即把多边形外的格覆写为 void。
    /// 这样 void 在 Terrain 完成时就标记好，后续 genStep（Plants/RockChunks/Animals，order 都比 Terrain 高）
    /// 读 terrainGrid 发现 void 格 Impassable，自然不在 void 格 spawn → ClearThingsOnCells 只需清
    /// RocksFromGrid（order 比 Terrain 低）的岩石，Thing 量大幅减少。
    ///
    /// 对锚点地图（非口袋，不走 genStep）：仍由 SeamlessTileManager.RefreshMapVoid 调 ApplyPolygonTerrain。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Terrain), nameof(GenStep_Terrain.Generate))]
    static class Patch_GenStep_Terrain
    {
        static void Postfix(Map map)
        {
            // 仅对 RimExodus 口袋地图生效（map.Parent is MapParent_SeamlessTile）。
            if (!(map.Parent is MapParent_SeamlessTile seamlessParent)) return;
            if (seamlessParent.worldTile < 0) return;

            // 在原版 Terrain 完成后铺 void（复用 ApplyPolygonTerrain 的核心逻辑，但此时 void 格上只有
            // RocksFromGrid 的岩石，Plants/RockChunks 还没 spawn，ClearThingsOnCells 量大幅减少）。
            SeamlessTerrainFill.ApplyPolygonTerrain(map, seamlessParent.worldTile);
        }
    }
}
