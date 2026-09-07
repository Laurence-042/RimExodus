using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的多边形 void 裁切 + snapshot 备份（order=389，Roads(390) 之前、Settlement(400) 之前）。
    /// 真实地形（Soil/岩石/水体等）由更早的原版 GenStep（ElevationFertility/Terrain/RocksFromGrid/Coast）生成，
    /// 本 GenStep 只负责备份 snapshot（void 裁切前的完整矩形 topGrid）+ 按六边形把多边形外部挖成虚空（RimExodus_Void）。
    /// 六边形 = 内切圆顶点模型（顶点 = center + 0.5S × 世界地块顶点方向）。
    ///
    /// **为什么在 389（而非旧 1400/391）**（2026-08 反转到 391、2026-08-30 再提前到 Roads 前，勿回退）：
    /// ①让 Roads(390) 及之后的全部步骤在最终地形上工作——道路 A* 视界内六边形外全不可走 void，
    /// 无绕行通道只能在河上架桥（河端点因此无需延伸到方形边，见 Patches_TileMutatorRiver）；
    /// ②void fertility=0、Standable=false（配合 ApplyPolygonTerrain 内的 pathGrid 即时刷新），
    /// 植物/动物/威胁 pawn 天然不落 void，无需事后清理。旧序 1400 下 Animals(1200) 先在将来
    /// void 格上合法生成、再靠撤离兜底，而撤离曾因 pathGrid 过期失效（动物站 void 的历史 bug）。
    ///
    /// **snapshot 备份点选在 389**：在所有基础地形写入步骤（Terrain 210 / Coast 220 / CoastalEdgeFill 230）
    /// 之后；Roads(390) 虽在其后但被 TerrainGrid void 守卫拦住写不进 void 格，因此 389 基础快照
    /// 仍是 void 下自然地形的权威来源。order 400+ 的 BaseGen 地板写入已被选址 patch
    /// （ScatterEdgeMargin=20 / ClearRectMargin=10）拦在接缝带之外，残余缺口（贴附矩形/降落平台/Harbor 桥）记观察项。
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
