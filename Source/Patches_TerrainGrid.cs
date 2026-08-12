using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 保护 RimExodus void 格不被第三方/运行时地形改写覆盖（防御性守卫）。
    ///
    /// **时序事实**：void 由 RimExodus_SeamlessTile（order=1802）铺设，远在任何原生地形 genStep 之后——
    /// 原生 Coast mutator（在 MutatorPostTerrain order=220 内）在 void 铺设**之前**跑，
    /// 两者时序上不冲突。因此本 patch 对原生 Coast mutator 场景**当前是空操作**。
    ///
    /// **本 patch 的实际作用**：防御 order&gt;1802 的第三方 genStep、或运行时地形改写（如 ModSettings
    /// 触发的刷新、玩家建造、mod 地形工具）把已铺的 void 改成其他地形。确立"void 一旦铺设不可变"语义。
    ///
    /// **历史背景**：早期 void 铺设在 order=211（Terrain 210 之后、Coast 220 之前），那时 Coast mutator
    /// 确实会在 void 之后跑并改写 void 格（沿海地块传送点散开 bug）。后 void 铺设挪到 1802，时序冲突消失，
    /// 但本守卫保留——成本低，防御价值仍在。
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
