using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 海岸补铺 genStep（order=230）：对临海地块的海洋邻居边铺海水。
    /// 壳，实际逻辑见 <see cref="CoastalEdgeFill"/>。
    ///
    /// 顺序紧随 MutatorPostTerrain(220，原版 Coast mutator 铺海水/沙滩) 之后、Plants(900) 之前。
    /// 补原生 Coast mutator 漏铺的海洋边——原版用"平均海岸角度"，多面环海时只铺一面海；
    /// 本 genStep 按每条海洋邻居边独立铺。铺的水在六边形外部分随后由 void(391) 裁掉。
    ///
    /// **为什么提前到 230（此前在 1801）**：让补铺的水在 Plants(900) 之前就位——植物 spawn 时
    /// 水格 fertility=0 被 CheckSpawnWildPlantAt 跳过，避免"植物先 spawn 在土地上、随后被水覆盖导致浮在水上"。
    /// TileMutatorWorker_Coast 无 GenerateFinal，不会在 MutatorFinal(1600) 覆盖；
    /// RocksFromGrid(200) 在 230 之前，岩石山已 spawn 为 edifice，Stone+edifice 守门正常工作。
    ///
    /// **统一注入**：通过 XML PatchOperation 注入到 Base_Player / Base_Faction / Encounter。
    /// 地块图（MapParent_SeamlessTile）与原生 parent 图（家园/原生家族）走完全相同的 genStep 链。
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
