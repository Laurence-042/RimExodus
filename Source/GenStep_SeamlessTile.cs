using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的多边形 void 裁切 + snapshot 备份（order=1400）。
    /// 真实地形（Soil/岩石/植物/水体等）由 MapGeneratorDef 中的原版 GenStep（ElevationFertility/Terrain/RocksFromGrid/Plants）生成。
    /// 本 GenStep 只负责备份 snapshot（void 裁切前的完整矩形 topGrid）+ 按六边形把多边形外部挖成虚空（RimExodus_Void）。
    /// 六边形 = 内切圆顶点模型（顶点 = center + 0.5S × 世界地块顶点方向）。
    /// order=1400，在 Fog(1500) 之前执行——让 GenStep_Fog 的 flood-fill 基于 void 裁切后的最终地形
    /// 算可达性（void 外条带不可通行→不揭雾，与渲染/寻路一致）。此时 Plants(900)/Animals(1200)/Snow(1150)
    /// 已 spawn，void 格上的实体由 ApplyPolygonTerrain 的 ClearThingsOnCells/EvacuatePawnsOnCells 事后清理
    /// （Patch_GenStepRocksFromGrid 已在 order=200 提前清岩石，故此处主要清理植物/物品）。
    ///
    /// **snapshot 备份点选在 1400（Fog 之前）**：仍在 CoastalEdgeFill(230) 之后，能记录海岸水。
    /// 此前锚点家园 A 走 Harmony Postfix（order=220）备份太早，之后 Roads/Plants 等仍会改地形导致 snapshot
    /// 与最终地形漂移。改为 genStep 后 A/B 统一备份，漂移消除（snapshot 已是接近最终的地形）。
    ///
    /// 实际逻辑已抽到 <see cref="SeamlessTerrainFill.BackupSnapshotAndApplyVoid"/>（归一入口）。
    ///
    /// **统一注入**：通过 XML PatchOperation 注入到 Base_Player / Base_Faction / Encounter。
    /// 守卫用 GetMapWorldTile(map) >= 0——任何有合法 worldTile 的地图都走完整链。
    /// </summary>
    public class GenStep_SeamlessTile : GenStep
    {
        public override int SeedPart => 82648291;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0)
            {
                Log.Warning("[RimExodus] GenStep_SeamlessTile: map has no valid worldTile, skipping polygon void fill.");
                return;
            }

            // 备份基础地形（void 裁切前的完整矩形 topGrid）+ 按六边形挖虚空。
            SeamlessTerrainFill.BackupSnapshotAndApplyVoid(map, worldTile);
        }
    }
}
