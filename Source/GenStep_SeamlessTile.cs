using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 生成无缝地块口袋地图的地形（阶段3：多边形裁切）。
    /// 先全图铺可通行地形（Soil），再按多边形所有权区域把外部挖成虚空（RimExodus_Void）。
    /// 多边形 = 内切圆顶点模型（顶点 = center + 0.5S × 世界地块顶点方向）。
    /// 锚点家园 A 是原生地图，不走本 GenStep，由 SeamlessTileManager.EnsureAnchorVoidApplied 补铺。
    /// </summary>
    public class GenStep_SeamlessTile : GenStep
    {
        public override int SeedPart => 82648291;

        public override void Generate(Map map, GenStepParams parms)
        {
            var size = map.Size;
            var grass = DefDatabase<TerrainDef>.GetNamedSilentFail("Soil");
            if (grass == null)
            {
                grass = TerrainDefOf.Soil;
            }

            // 1. 全图先铺可通行地形。
            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), grass);
                }
            }

            // 2. 取地块 worldTile，按多边形挖虚空。
            var worldTile = -1;
            if (map.Parent is MapParent_SeamlessTile parent)
            {
                worldTile = parent.worldTile;
            }
            if (worldTile < 0)
            {
                Log.Warning("[RimExodus] GenStep_SeamlessTile: map has no valid worldTile, skipping polygon void fill.");
                return;
            }

            SeamlessTerrainFill.ApplyPolygonTerrain(map, worldTile);
        }
    }
}
