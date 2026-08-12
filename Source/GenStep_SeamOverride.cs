using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写 genStep（order=1803，阶段4 连续地形）。
    ///
    /// 在 void 裁切（RimExodus_SeamlessTile, 1802）之后执行。此时 Terrain 已铺好地形，void 裁切已挖掉六边形外。
    /// 本 genStep 在六边形内边缘的混合带做 terrainDef 过渡：参考已加载邻居的对应 cell，
    /// 按距离权重混合两端 terrainDef，让接缝处地形视觉/通行连续。
    ///
    /// 单向覆写：只改本端（新生成 tile），不改已加载邻居（已生成 tile 保持原样）。
    /// 锚点地图 A 生成时无已加载邻居 → ApplyOneWay 的卷积循环对每个邻居查 TryGetMapByWorldTile 无命中 → 空操作。
    ///
    /// 实际逻辑委托 <see cref="SeamlessSeamOverride"/>。本类只做 genStep 壳 + worldTile 提取。
    ///
    /// **统一注入**：通过 XML PatchOperation 注入到 Base_Player / Base_Faction / Encounter。
    /// 守卫用 GetMapWorldTile(map) >= 0——任何有合法 worldTile 的地图都走完整链。
    /// </summary>
    public class GenStep_SeamOverride : GenStep
    {
        public override int SeedPart => 82648313;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            SeamlessSeamOverride.ApplyOneWay(map, worldTile);
        }
    }
}
