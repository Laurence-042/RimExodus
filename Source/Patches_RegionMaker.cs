using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 区域系统"触边"判定的接缝化——"地图边缘"对区域系统整体变为六边形边（2026-08）。
    ///
    /// <see cref="Region"/>.touchesMapEdge 的唯一计算点 = <c>RegionMaker.AddCell</c>（原版仅方形边格
    /// c.x==0/Size-1、z==0/Size-1 置位），经 <c>District.AddRegion</c> 的 numRegionsTouchingMapEdge
    /// 计数流到全部消费者（District/Room.TouchesMapEdge）。六边形裁切后方形边整圈 void，
    /// 倾斜图（角点不触方形边）上无任何区域触边，消费者结构性全灭：
    /// - 远行队打包点 <c>RCellFinder.TryFindRandomSpotJustOutsideColony</c> 的 FinalValidator
    ///   （district.TouchesMapEdge &amp;&amp; CellCount&gt;=60，2026-08 "未发现有效的打包点"根因）；
    /// - 袭击入口 <c>TryFindRandomPawnEntryCell</c> 的 validator（同款条件，EdgeWalkIn 选点失败）；
    /// - <c>CellFinderLoose.TryFindCentralCell</c>（FindPlayerStartSpot 选址——392 预设已使主链
    ///   不依赖此判定，本 patch 让它自然通过，预设守卫不变）。
    /// - GenStep_Animals(1200) 的 <c>RCellFinder.RandomAnimalSpawnCell_MapGen</c>（validator 含
    ///   district.TouchesMapEdge；倾斜图上结构性全败 → fallback RandomCell 零校验 +
    ///   RandomClosewalkCellNear 找不到返回 center 本身 → 初始野生动物偶现生成在 void 上）。
    ///
    /// 置位判据两路（任一即置）：区域含传送圈格（IsSeamEdgeCell）或 4 邻格含 void 地形
    /// （最外圈实地形）。void 邻接是关键：void=391 已铺而传送点 1490 才铺，只有 void 邻接
    /// 能让生成期（Animals=1200）的消费者也拿到正确触边。
    /// 在此单点置位天然复用原版 District 计数与缓存；Region/District 不序列化
    /// （读档 FinalizeLoading 全量重建区域），无旧档陈旧旗标问题。
    ///
    /// 语义辐射（接受，与"所有地图一视同仁"铁律一致）：动物栏舍区域触及接缝圈 = 视为开放
    /// （原版"触边不可圈养"对六边形边同样成立）；外圈房间不自动建顶（AutoBuildRoofAreaSetter）；
    /// 动物尸体在外圈房间按时消失（Corpse vanish）。无传送点地图（spot 未铺/非本 mod 图）
    /// IsSeamEdgeCell 恒 false = 原版行为零改动。
    /// </summary>
    [HarmonyPatch(typeof(RegionMaker), "AddCell")]
    static class Patch_RegionMaker_AddCell
    {
        static void Postfix(RegionMaker __instance, IntVec3 c)
        {
            var region = _newRegRef(__instance);
            if (region == null || region.touchesMapEdge) return; // 方形边格已置位（未倾斜图角点）早退
            var map = _mapRef(__instance);
            if (map == null) return;
            if (SeamlessEdgeCells.IsSeamEdgeCell(map, c) || IsAdjacentToVoid(map, c))
                region.touchesMapEdge = true; // 区域触及传送圈/void 边界 = 触达六边形可玩区边界
        }

        /// <summary>
        /// void 邻接：4 邻格是 void 地形 = 本格是最外圈实地形，触达六边形边界。
        /// 不依赖传送点（1490 才铺），生成期（void=391 已铺、Animals=1200）即生效——
        /// RandomAnimalSpawnCell_MapGen 的 validator（district.TouchesMapEdge）在倾斜图上
        /// 结构性全败 → fallback RandomCell 零校验 + RandomClosewalkCellNear 找不到时返回
        /// center 本身 → 初始野生动物**偶现生成在 void 上**（方形角部距六边形 ≈69 格 >
        /// 搜索半径 45，随机格落进角部 void 区即复现；落进六边形内则无症状——偶现来源）。
        /// </summary>
        private static bool IsAdjacentToVoid(Map map, IntVec3 c)
        {
            var voidDef = VoidTerrainDef;
            if (voidDef == null) return false;
            var terrainGrid = map.terrainGrid;
            if (c.x > 0 && terrainGrid.TerrainAt(new IntVec3(c.x - 1, 0, c.z)) == voidDef) return true;
            if (c.x < map.Size.x - 1 && terrainGrid.TerrainAt(new IntVec3(c.x + 1, 0, c.z)) == voidDef) return true;
            if (c.z > 0 && terrainGrid.TerrainAt(new IntVec3(c.x, 0, c.z - 1)) == voidDef) return true;
            if (c.z < map.Size.z - 1 && terrainGrid.TerrainAt(new IntVec3(c.x, 0, c.z + 1)) == voidDef) return true;
            return false;
        }

        private static TerrainDef _cachedVoidDef;
        private static TerrainDef VoidTerrainDef
        {
            get
            {
                if (_cachedVoidDef == null)
                    _cachedVoidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
                return _cachedVoidDef;
            }
        }

        private static readonly AccessTools.FieldRef<RegionMaker, Map> _mapRef =
            AccessTools.FieldRefAccess<RegionMaker, Map>("map");

        private static readonly AccessTools.FieldRef<RegionMaker, Region> _newRegRef =
            AccessTools.FieldRefAccess<RegionMaker, Region>("newReg");
    }
}
