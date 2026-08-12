using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 保护 RimExodus void 格不被原生/第三方地形改写覆盖。
    ///
    /// **根因**：海岸 mutator（<see cref="TileMutatorWorker_Coast.GeneratePostTerrain"/>，genStep
    /// <c>MutatorPostTerrain</c> order=220）在 void 铺设（<c>RimExodus_SeamlessTile</c> order=211）
    /// **之后**跑，遍历 map.AllCells 调 <see cref="TerrainGrid.SetTerrain"/> 把海岸 noise 驱动的水/沙
    /// 铺到格上——**包括六边形外的 void 格**（void 的 categoryType 默认 Misc ≠ Stone，Coast mutator
    /// 的"非 Stone 才覆盖"判定放行）。结果六边形外的 void 被改成水，void 边界被破坏，传送点铺设
    /// （读 terrainGrid 判 void 邻接）的"距 void 切比雪夫距离"基于被改坏的地形 → 传送点沿海岸
    /// 不规则散开，不组成六边形（用户报告：沿海地块海洋侧传送点散开）。
    ///
    /// **修复**：Prefix <see cref="TerrainGrid.SetTerrain"/>，若目标格当前是 RimExodus_Void 且新地形
    /// 不是 void，拒绝改写（return false）。确立"void 一旦铺设不可变"语义，从源头阻止 Coast mutator
    /// 及任何未来/第三方 mutator 改 void 格。
    ///
    /// **不影响 RimExodus 自身**：<see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/> 铺 void 用
    /// 直接写 <c>topGrid[idx] = voidDef</c>（跳过 SetTerrain），不触发本 patch。
    ///
    /// **不影响合法地形转换**：void 是 <c>changeable=false</c> + <c>layerable=false</c>，设计上不可
    /// 被烧/移除/覆盖，拦截 void→其他 不会误伤任何合法场景。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    static class Patch_TerrainGrid_SetTerrain
    {
        static bool Prefix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr)
        {
            // 只拦"当前是 void 且新地形不是 void"的改写。
            if (newTerr == null) return true;
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null || newTerr == voidDef) return true;
            var current = __instance.TerrainAt(c);
            if (current == voidDef) return false; // 拒绝改写 void 格。
            return true;
        }
    }
}
