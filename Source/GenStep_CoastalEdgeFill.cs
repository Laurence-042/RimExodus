using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 海岸补铺 genStep（order=1801）：对临海地块的海洋邻居边铺海水。
    /// 壳，实际逻辑见 <see cref="CoastalEdgeFill"/>。
    ///
    /// 顺序在 Fog(1500) 之后、RimExodus_SeamlessTile(void, 1802) 之前。补原生 Coast mutator
    /// （在 MutatorPostTerrain order=220 内铺海水/沙滩）漏铺的海洋边——原版用"平均海岸角度"，
    /// 多面环海时只铺一面海；本 genStep 按每条海洋邻居边独立铺。铺的水在六边形外部分随后由 void 裁掉。
    ///
    /// **统一注入**：通过 XML PatchOperation 注入到 Base_Player / Base_Faction / Encounter，
    /// 加上原有的 RimExodus_SeamlessTileGenerator（邻接地块），所有玩家可进入的地图都走本 genStep。
    /// 守卫用 GetMapWorldTile(map) >= 0——任何有合法 worldTile 的地图（MapParent_SeamlessTile 读 worldTile 字段，
    /// 普通地图读原生 map.Tile）都走完整链。RimExodus 是全局无缝，任何地图都应当 void 也切、传送点也放。
    /// </summary>
    public class GenStep_CoastalEdgeFill : GenStep
    {
        public override int SeedPart => 73129458;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            CoastalEdgeFill.Apply(map, worldTile);
        }
    }
}
