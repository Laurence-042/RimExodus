using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 传送点铺设 genStep（order=1490，Fog(1500) 之前、威胁步骤(1600) 之前）。
    ///
    /// 把 <see cref="SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors"/> 从生成完成的
    /// onComplete 回调提前到 genStep 链内、Fog 之前：Fog 的 fallback 分支
    /// <c>GenStep_Fog.UnfogMapFromEdge</c> 的三级候选 validator 都要过
    /// <c>Reachability.CanReachMapEdge</c>，而本 mod 的该 patch（Patches_Reachability）
    /// 以"传送点已铺"（<see cref="SeamlessEdgeCells.HasSeamEdge"/> = listerThings 实况查询）为门——
    /// 传送点若在 onComplete 才铺，Fog 运行时 patch 放行原版，原版语义 = District.TouchesMapEdge
    /// 在六边形裁切图（方形边缘全 void）上恒 false → 三级候选结构性全败 → 零揭雾、整图全雾
    /// （2026-08 偶发全雾 bug 的成因之一，与 PlayerStartSpot static 残留叠加）。
    /// 铺在 1490 后 Fog(1500)/MutatorFinal(1600) 期间接缝语义 patch（Patches_Reachability/
    /// Patches_CellFinder）即已生效；400-1490 期间行为与旧序一致（点尚未铺），零回归。
    ///
    /// 铺设幂等（同格查重），onComplete 对 originMap 的补铺调用与锚点图 TrySetupOnStart 的
    /// 调用保留不冲突。RefreshEnterSpotArrivals 仍留 onComplete——它依赖邻居表 offset
    /// （RegisterNeighborBidirectional 在 genStep 链之后登记）。
    ///
    /// **统一注入**：通过 XML PatchOperation 注入到 Base_Player / Base_Faction / Encounter。
    /// 守卫用 GetMapWorldTile(map) >= 0——任何有合法 worldTile 的地图都走完整链。
    /// </summary>
    public class GenStep_EnterSpots : GenStep
    {
        public override int SeedPart => 82648337;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            // MapPreview 预览（后台线程）不铺点：预览不消费传送点（def 未加 includeInPreviews，
            // 此处为双保险——真跑也只是浪费，GenSpawn 在预览图上行为未验证）。
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return;

            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, worldTile);
        }
    }
}
