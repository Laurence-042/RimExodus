using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 保护 RimExodus void 格不被第三方/运行时地形改写覆盖（防御性守卫）。
    ///
    /// **时序事实（2026-08-30 起）**：void 由 RimExodus_SeamlessTile（order=389，Roads 390 之前、
    /// Settlement 400 之前）铺设。原生 Coast mutator（MutatorPostTerrain 220 内）在 void 之前跑，
    /// 时序不冲突；而 order 400+ 的全部地形写入（Settlement/site BaseGen 的 SymbolResolver_FloorFill
    /// 铺地板、Harbor 桥、Pollution 890 污染变体、MutatorFinal 1600、Fog 1500 之后的第三方步骤）
    /// 都在 void 之后——本守卫在生成期从空操作变为**实际生效**，把上述写入挡在 void 格之外
    /// （预期收益：旧序 1400 时这些写入先发生、随后被 void 覆盖；新序下若不拦会在 void 上打洞）。
    ///
    /// **本 patch 的实际作用**：防御 order&gt;389 的任意 genStep 与运行时地形改写（ModSettings 触发的
    /// 刷新、玩家建造、mod 地形工具）把已铺的 void 改成其他地形。确立"void 一旦铺设不可变"语义。
    ///
    /// **历史背景**：早期 void 铺设在 order=211（Terrain 210 之后、Coast 220 之前），那时 Coast mutator
    /// 确实会在 void 之后跑并改写 void 格（沿海地块传送点散开 bug）。先后挪到 1400（Fog 之前）、
    /// 再挪到 389（2026-08，让后续步骤在最终地形上工作），时序冲突早已消失，守卫始终保留——
    /// 成本低，且新序下防御面反而扩大到全部生成期地形写入。
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
            // 卸载前恢复（2026-08）：恢复流程本身就是"把 void 改回原生地形"的唯一合法例外。
            if (SeamlessUninstallRestore.Restoring) return true;
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
