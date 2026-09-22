using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写 genStep（order=392，3 圈接缝带架构；SeamlessTile(389) 之后、Settlement(400) 之前）。
    ///
    /// 在接缝带 void 裁切（RimExodus_SeamlessTile, 389）之后立刻执行。本 genStep 在接缝带 B ∪ 过渡带 T
    /// 做 terrainDef 混合：参考所有实际加载邻图的当前实况与完整基础快照，
    /// 让接缝处地形视觉/通行连续。放在 Settlement 之前使 Plants(900)/Animals(1200) 在混合后的最终
    /// 地形上生成（植物与地形一致，不再出现"树先生成后混成水"）；Fog(1500) 仍在本步之后，据最终地形揭雾。
    /// MutatorFinal(1600) 多数 mutator 不写 terrainGrid，但 AncientUplink/InsectMegahive 的
    /// GeneratePostFog 会 SetTerrain——这些任务地块上本 genStep 的覆写可能被覆盖（已知小瑕疵，罕见场景）。
    ///
    /// 单向覆写：只改本端（新生成 tile），不改已生成邻居。原生 parent 图（家园等）生成时无已生成邻居
    /// → ApplyOneWay 空操作；未来邻居直接读取这张图的当前实况，void 侧回落基础快照。
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

            // 诊断（2026-08-30 河歪排查，verbose 门控）：混合前/后各报一次缝处水带实测位置，
            // 区分"生成期就歪"vs"混合期弄歪"。
            RiverSeamDiagnostics.Report(map, worldTile, "preMix");

            SeamlessSeamOverride.ApplyOneWay(map, worldTile);

            RiverSeamDiagnostics.Report(map, worldTile, "postMix");

            // MapPreview 预览（后台线程，2026-08）：本步之后的两个动作都是真实图专用——
            // ①pathGrid 刷新在预览图上 NRE（组件被 MapPreview 裁剪，见 SeamlessMapPreviewCompat）；
            // ②预设 PlayerStartSpot 写进程级 static，预览线程写会与主线程竞态。
            // 混合本身（ApplyOneWay，上方）保留——预览要显示接缝混合后的地形。
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return;

            // void 边界岩的隐形 link 延续体（2026-08 视觉优化）：必须挂在 **392 末（混合之后）**——
            // 紧贴 void 的那圈接缝带岩石多数是上方混合 B 照抄 SyncRockBuildingTo 才 spawn 的
            // （原生 Soil 无岩），389 时点"4 邻含岩石"条件全灭一格都铺不出（2026-08 实测）。
            // own snapshot 在生成期仍在内存可读（非序列化只影响读档），双分支判据不受影响。
            // 单向原则（用户定夺）：一切决策发生在本图生成期内，无回铺。
            SeamlessVoidRockLink.PlaceAfterSeamOverride(map, worldTile);

            // 混合同样直写 topGrid（SeamlessSeamOverride 写地形只打 mesh 脏标记），带格地形变更
            // （如混合出水/去水）后必须刷新 pathGrid——后续 Plants(900)/Animals(1200) 的 Standable/Walkable
            // 读 PathGrid 缓存而非 terrainGrid，不刷新会把 pawn/植物放到混合后的水格上。
            map.pathing.RecalculateAllPerceivedPathCosts();

            // 勿在此（或任何 850 之前的 genStep）预设 PlayerStartSpot。FindPlayerStartSpot(850) 的
            // 选址 validator 含 district.TouchesMapEdge，历史上裁切图上恒 false 才需要预设；现由
            // Patches_RegionMaker 的 void 邻接触边判据（850 内 RebuildAllRegionsAndRooms 后生效）解决。
            // 预设反而致命：500 序 MutatorCriticalStructures 可把墙盖在预设点上，而 850 见
            // PlayerStartSpotValid==true 跳过重选 → 生成期 CanReachColony（以出生点为 OnCell 目的地）
            // 的目的地无 region → 全图恒 false → 边缘入场类 ScenPart 选点失败且被静默吞掉
            // （2026-09 VEF 边缘入场开局 pawn 蒸发根因）。850 原版选址跑在全部 ≤850 结构之后、
            // validator 首条即 Standable，天然无此窗口。
        }
    }
}
