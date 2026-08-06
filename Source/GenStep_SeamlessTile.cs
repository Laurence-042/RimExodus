using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 生成无缝地块口袋地图的地形。
    /// 原型阶段使用简单的矩形可活动区域（不实现六边形裁切，见计划书第 2 节）。
    /// 在矩形内部铺设可通行草地，边缘铺设不可通行地形。
    /// </summary>
    public class GenStep_SeamlessTile : GenStep
    {
        public override int SeedPart => 82648291;

        public override void Generate(Map map, GenStepParams parms)
        {
            var size = map.Size;
            var grass = DefDatabase<TerrainDef>.GetNamedSilentFail("Soil");
            var impassable = DefDatabase<TerrainDef>.GetNamedSilentFail("WaterOceanDeep");

            if (grass == null)
            {
                grass = TerrainDefOf.Soil;
            }
            if (impassable == null)
            {
                impassable = TerrainDefOf.WaterOceanDeep;
            }

            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    // 原型：边缘 2 格不可通行，内部可通行
                    var isEdge = x < 2 || z < 2 || x >= size.x - 2 || z >= size.z - 2;
                    map.terrainGrid.SetTerrain(cell, isEdge ? impassable : grass);
                }
            }
        }
    }
}
