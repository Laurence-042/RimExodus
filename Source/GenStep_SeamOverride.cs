using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写 genStep（阶段4 连续地形）。
    ///
    /// 在 void 裁切（order=211）之后、Plants（order=900）之前执行（order=212）。
    /// 此时 Terrain(210) 已铺好地形，void 裁切(211) 已挖掉六边形外。
    /// 本 genStep 在六边形内边缘的混合带做 terrainDef 过渡：参考已加载邻居的对应 cell，
    /// 按距离权重混合两端 terrainDef，让接缝处地形视觉/通行连续。
    ///
    /// 双向覆写：本端生成时，对面已加载邻居的侧带也一并覆写（回补邻居生成时本端不存在的情况）。
    ///
    /// 实际逻辑委托 <see cref="SeamlessSeamOverride"/>。本类只做 genStep 壳 + worldTile 提取。
    /// </summary>
    public class GenStep_SeamOverride : GenStep
    {
        public override int SeedPart => 82648313;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!(map.Parent is MapParent_SeamlessTile parent) || parent.worldTile < 0)
                return; // 非 RimExodus 地块地图：跳过。

            SeamlessSeamOverride.ApplyBidirectional(map, parent.worldTile);
        }
    }
}
