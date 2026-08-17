using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写 genStep（order=1410，3 圈接缝带架构）。
    ///
    /// 在接缝带 void 裁切（RimExodus_SeamlessTile, 1400）之后、Fog(1500) 之前执行。
    /// 本 genStep 在接缝带 B ∪ 过渡带 T 做 terrainDef 混合：参考所有已生成邻居的
    /// 接缝条带快照（重叠带地形完全一致 + 外条带距离衰减），让接缝处地形视觉/通行连续。
    /// 放在 Fog 之前是为了让 Fog flood-fill 基于覆写后的最终地形算可达性（含 SyncRockBuilding
    /// spawn 的岩石）。MutatorFinal(1600) 多数 mutator 不写 terrainGrid，
    /// 但 AncientUplink/InsectMegahive 的 GeneratePostFog 会 SetTerrain——这些任务地块上
    /// 本 genStep 的覆写可能被覆盖（已知小瑕疵，罕见场景）。
    ///
    /// 混合完成后捕获本图的接缝条带快照（<see cref="SeamStripData.CaptureAndStore"/>）：
    /// B∪T 最终值 + 接缝带外条带原生值，供后续新生成的邻居图参考（跨读档持久——
    /// 读档后 baseTerrainSnapshot 为 null 的问题由序列化的条带快照解决）。
    ///
    /// 单向覆写：只改本端（新生成 tile），不改已生成邻居。锚点地图 A 生成时无已生成邻居
    /// → ApplyOneWay 空操作，但条带快照仍捕获（供未来邻居参考）。
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
            SeamStripData.CaptureAndStore(map, worldTile);
        }
    }
}
