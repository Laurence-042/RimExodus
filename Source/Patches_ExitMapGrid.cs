using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4前置：把无缝地块的传送点格标记为出口格（exit cell），使原生远行队组建流程生效。
    ///
    /// 背景：六边形裁切把地块地图的边缘格（含原生撤离区）都切成了 void（不可通行），
    /// 原版 <see cref="ExitMapGrid.Rebuild"/> 只标"距地图边缘 ≤2 格且可达"的格，这些格在六边形外全是 void。
    /// 因此原生"走到边缘 → 组建远行队"流程无法触发（pawn 走不到真正的出口格）。
    ///
    /// 修复：<see cref="ExitMapGrid.Rebuild"/> Postfix，对 RimExodus 地块（<see cref="MapParent_SeamlessTile"/>），
    /// 把所有传送点（<c>RimExodus_SeamlessEnterSpot</c>）所在格标记为出口格。
    /// 这样原生 <c>JobDriver_Goto</c> 检查 <c>IsExitCell</c> 时，踩传送点就设 <c>exitMapOnArrival=true</c>，
    /// pawn 到达后触发原生 <c>Pawn.ExitMap</c> → 大地图远行队生成。
    ///
    /// 与直接跨图传送的区分：<see cref="SeamlessMapTransferTrigger.TryTriggerTransfer"/> 入口检查
    /// pawn 当前 Job 的 <c>exitMapOnArrival</c>——远行队流程放行原生 ExitMap，征召跨图走现有传送逻辑。
    /// </summary>
    [HarmonyPatch(typeof(ExitMapGrid), "Rebuild")]
    static class Patch_ExitMapGrid_Rebuild
    {
        static void Postfix(ExitMapGrid __instance, Map ___map)
        {
            // 对所有有 RimExodus 传送点的地图生效（锚点 A 是原生 MapParent，也是无缝地块的一员）。
            // 之前只判断 MapParent_SeamlessTile，导致锚点 A 的传送点未被标为出口格，远行队走不到。
            if (___map == null) return;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;
            if (___map.listerThings.ThingsOfDef(enterSpotDef).Count == 0) return; // 无传送点则跳过

            var grid = __instance.Grid;
            if (grid == null) return;

            // 遍历地图上所有传送点，把它们的格标记为出口格。
            // 传送点通常沿六边形接缝边铺满（~100 个），遍历 ThingsOfDef（O(spots)）比重扫地图轻量。
            var marked = 0;
            var maxIdx = grid.Width * grid.Height;
            foreach (var spot in ___map.listerThings.ThingsOfDef(enterSpotDef))
            {
                var idx = ___map.cellIndices.CellToIndex(spot.Position);
                if (idx >= 0 && idx < maxIdx && !grid[idx])
                {
                    grid.Set(idx, true);
                    marked++;
                }
            }

            if (marked > 0 && (RimExodusMod.Settings?.verboseLogging ?? false))
                Log.Message($"[RimExodus] ExitMapGrid.Rebuild postfix: marked {marked} enter-spot cells as exit cells on map {___map.uniqueID}.");
        }
    }

    /// <summary>
    /// 让铺了 RimExodus 传送点的地图（含玩家家园 A）也算"使用 exit grid"——
    /// 原版 <see cref="ExitMapGrid.MapUsesExitGridNow"/> 对 <c>map.IsPlayerHome</c> 返回 false，
    /// 导致锚点家园 A 的传送点格虽被标为 exit cell（见 <see cref="Patch_ExitMapGrid_Rebuild"/>），
    /// 但 exit grid 的浅绿色 CellBoolDrawer 不绘制（<see cref="ExitMapGrid.ExitMapGridUpdate"/> 读
    /// <see cref="ExitMapGrid.MapUsesExitGrid"/> 决定是否 MarkForDraw）。结果聚焦 B（非家园）能看到
    /// 浅绿色传送点提示、聚焦 A（家园）看不到。
    ///
    /// 修复：Postfix <see cref="ExitMapGrid.MapUsesExitGrid"/>，对有 RimExodus 传送点的地图强制 true。
    /// 覆盖 IsPlayerHome 的 false。副作用是 IsExitCell 在 A 也按 grid 返回（A 的传送点格本就被标为 exit cell，
    /// 这是期望行为——原生撤离/远征队流程在 A 也该工作）。
    /// </summary>
    [HarmonyPatch(typeof(ExitMapGrid), nameof(ExitMapGrid.MapUsesExitGrid), MethodType.Getter)]
    static class Patch_ExitMapGrid_MapUsesExitGrid
    {
        static void Postfix(ExitMapGrid __instance, Map ___map, ref bool __result, ref bool ___mapUsesExitGrid)
        {
            if (__result) return; // 已经 true（如 B），无需改。
            if (___map == null) return;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;
            // 仅对实际铺了 RimExodus 传送点的地图生效（避免影响其他原版家园地图）。
            if (___map.listerThings.ThingsOfDef(enterSpotDef).Count > 0)
            {
                __result = true;
                ___mapUsesExitGrid = true; // 同步改缓存字段，避免同 tick 内其他读取者拿到旧值。
            }
        }
    }
}
