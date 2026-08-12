using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块口袋地图的多边形 void 裁切（阶段4a：真实地形 + void 裁切）。
    /// 真实地形（Soil/岩石/植物/水体等）由 MapGeneratorDef 中的原版 GenStep（ElevationFertility/Terrain/RocksFromGrid/Plants）生成。
    /// 本 GenStep 只负责按六边形把多边形外部挖成虚空（RimExodus_Void）。
    /// 六边形 = 内切圆顶点模型（顶点 = center + 0.5S × 世界地块顶点方向）。
    /// order=211，在 Terrain(210) 之后执行，确保先有真实地形再挖 void。
    ///
    /// 备份点：Generate 开头备份 topGrid 到 baseTerrainSnapshot（void 裁切前的完整矩形地形），
    /// 供接缝覆写卷积混合读取。
    /// </summary>
    public class GenStep_SeamlessTile : GenStep
    {
        public override int SeedPart => 82648291;

        public override void Generate(Map map, GenStepParams parms)
        {
            MapParent_SeamlessTile parent = null;
            var worldTile = -1;
            if (map.Parent is MapParent_SeamlessTile p)
            {
                parent = p;
                worldTile = parent.worldTile;
            }
            if (worldTile < 0)
            {
                Log.Warning("[RimExodus] GenStep_SeamlessTile: map has no valid worldTile, skipping polygon void fill.");
                return;
            }

            // 备份基础地形（void 裁切前的完整矩形 topGrid），供接缝覆写卷积混合读取。
            if (parent != null)
                parent.baseTerrainSnapshot = (TerrainDef[])map.terrainGrid.topGrid.Clone();

            // 按六边形挖虚空（六边形外 = void）。真实地形已由前置原版 GenStep 铺好。
            SeamlessTerrainFill.ApplyPolygonTerrain(map, worldTile);
        }
    }
}
