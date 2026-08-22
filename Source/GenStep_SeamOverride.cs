using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写 genStep（order=392，3 圈接缝带架构；SeamlessTile(391) 之后、Settlement(400) 之前）。
    ///
    /// 在接缝带 void 裁切（RimExodus_SeamlessTile, 391）之后立刻执行。本 genStep 在接缝带 B ∪ 过渡带 T
    /// 做 terrainDef 混合：参考所有已生成邻居的接缝条带快照（重叠带地形完全一致 + 外条带距离衰减），
    /// 让接缝处地形视觉/通行连续。放在 Settlement 之前使 Plants(900)/Animals(1200) 在混合后的最终
    /// 地形上生成（植物与地形一致，不再出现"树先生成后混成水"）；Fog(1500) 仍在本步之后，据最终地形揭雾。
    /// MutatorFinal(1600) 多数 mutator 不写 terrainGrid，但 AncientUplink/InsectMegahive 的
    /// GeneratePostFog 会 SetTerrain——这些任务地块上本 genStep 的覆写可能被覆盖（已知小瑕疵，罕见场景）。
    ///
    /// 混合完成后捕获本图的接缝条带快照（<see cref="SeamStripData.CaptureAndStore"/>）：
    /// B∪T 最终值 + 接缝带外条带原生值，供后续新生成的邻居图参考（跨读档持久——
    /// 读档后 baseTerrainSnapshot 为 null 的问题由序列化的条带快照解决）。
    ///
    /// 单向覆写：只改本端（新生成 tile），不改已生成邻居。原生 parent 图（家园等）生成时无已生成邻居
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

            // MapPreview 预览（后台线程，2026-08）：本步之后的三个动作都是真实图专用——
            // ①条带快照存储（预览图 parent null，捕获即白干）；
            // ②pathGrid 刷新在预览图上 NRE（组件被 MapPreview 裁剪，见 SeamlessMapPreviewCompat）；
            // ③预设 PlayerStartSpot 写进程级 static，预览线程写会与主线程竞态。
            // 混合本身（ApplyOneWay，上方）保留——预览要显示接缝混合后的地形。
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return;

            SeamStripData.CaptureAndStore(map, worldTile);

            // 混合同样直写 topGrid（SeamlessSeamOverride 写地形只打 mesh 脏标记），带格地形变更
            // （如混合出水/去水）后必须刷新 pathGrid——后续 Plants(900)/Animals(1200) 的 Standable/Walkable
            // 读 PathGrid 缓存而非 terrainGrid，不刷新会把 pawn/植物放到混合后的水格上。
            map.pathing.RecalculateAllPerceivedPathCosts();

            // 预设 PlayerStartSpot（2026-08，勿删）：FindPlayerStartSpot(850) 的选址 validator 含
            // district.TouchesMapEdge（直读 District 属性，不经 CanReachMapEdge patch），void 裁切图
            // （方形边缘全 void → 无 region touches 方形边）上恒 false → 选址必然 1000 次采样全拒、
            // 走原版 fallback（每图一条红字 "Found no good central spot" + PlayerStartSpot = 随机格）。
            // 在此预设（原版"上游已设则跳过选址"守卫，GenStep_Labyrinth 设 Zero 的同款先例）让揭雾
            // 根确定为六边形中心附近的无顶可站格。判据用实况（方形四角已是 void）而非地图类型——
            // 未裁切的图放行原生选址。必须在上面 pathGrid 刷新之后（StandableCellNear 读 PathGrid 缓存）。
            if (!MapGenerator.PlayerStartSpotValid && MapCornersAreVoid(map))
            {
                var spot = CellFinder.StandableCellNear(map.Center, map, 30f, c => !c.Roofed(map));
                if (spot.IsValid)
                    MapGenerator.PlayerStartSpot = spot;
                // 找不到则不设：FindPlayerStartSpot 自身的 fallback（随机格）兜底，Fog 的
                // UnfogMapFromEdge 分支由 GenStep_EnterSpots(1490) 提前铺点后的接缝语义 patch 兜底。
            }
        }

        /// <summary>
        /// 方形地图四角格是否已是 RimExodus_Void（= 六边形 void 裁切已发生）。
        /// 四角离六边形边界最远（内切模型顶点仅触边中点），裁切后必为 void、未裁切必为普通地形——
        /// 用作"本图已走 RimExodus 裁切链"的实况判定（几何事实，非地图类型区分）。
        /// </summary>
        private static bool MapCornersAreVoid(Map map)
        {
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null) return false;
            int max = map.Size.x - 1;
            return map.terrainGrid.TerrainAt(new IntVec3(0, 0, 0)) == voidDef
                && map.terrainGrid.TerrainAt(new IntVec3(max, 0, 0)) == voidDef
                && map.terrainGrid.TerrainAt(new IntVec3(0, 0, max)) == voidDef
                && map.terrainGrid.TerrainAt(new IntVec3(max, 0, max)) == voidDef;
        }
    }
}
