using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4前置：把无缝地块的传送点格标记为出口格（exit cell），使原生远行队组建/撤离/撤退流程生效；
    /// 同时**清空原版方形 2 格宽撤离带**，只保留六边形接缝带的浅绿色提示。
    ///
    /// 背景：
    /// 1. 原版 <see cref="ExitMapGrid.Rebuild"/> 标记"距矩形地图边缘 ≤2 格且 <see cref="ExitMapGrid.IsGoodExitCell"/>"
    ///    的格为 exit cell，<see cref="CellBoolDrawer"/> 把这些格画成浅绿色带。
    /// 2. 六边形裁切后矩形边缘全是 void，方形带无效（既看不清也走不到），但 <c>CanBeSeenOver</c> 只看 edifice
    ///    不看 walkability，部分 void/边缘格仍会被标 → 画出一圈无效的浅绿色方形带。
    /// 3. 同时传送点沿六边形接缝带铺设（<see cref="SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors"/>），
    ///    需要把这些格标为 exit cell，原生 <c>JobDriver_Goto</c> 检查 <c>IsExitCell</c> 时踩传送点设
    ///    <c>exitMapOnArrival=true</c>，触发原生 <c>Pawn.ExitMap</c> → 大地图远行队生成。
    ///
    /// 修复（Prefix 跳过原版 Rebuild）：对有 RimExodus 传送点的地图，**完全跳过原版方形带铺设**，
    /// 自己初始化一个干净的 <see cref="BoolGrid"/>，只把传送点格标 true。这样浅绿色只出现在六边形接缝处。
    ///
    /// 与直接跨图传送的区分：<see cref="SeamlessMapTransferTrigger.TryTriggerTransfer"/> 入口检查
    /// pawn 当前 Job 的 <c>exitMapOnArrival</c>——远行队流程放行原生 ExitMap，征召跨图走现有传送逻辑。
    /// </summary>
    [HarmonyPatch(typeof(ExitMapGrid), "Rebuild")]
    static class Patch_ExitMapGrid_Rebuild
    {
        // 反射访问 ExitMapGrid 的 private 字段（缓存委托，避免每次反射查找）。
        private static readonly AccessTools.FieldRef<ExitMapGrid, BoolGrid> _exitMapGridRef =
            AccessTools.FieldRefAccess<ExitMapGrid, BoolGrid>("exitMapGrid");
        private static readonly AccessTools.FieldRef<ExitMapGrid, bool> _dirtyRef =
            AccessTools.FieldRefAccess<ExitMapGrid, bool>("dirty");
        private static readonly AccessTools.FieldRef<ExitMapGrid, CellBoolDrawer> _drawerIntRef =
            AccessTools.FieldRefAccess<ExitMapGrid, CellBoolDrawer>("drawerInt");

        static bool Prefix(ExitMapGrid __instance, Map ___map)
        {
            // 对所有有 RimExodus 传送点的地图生效（锚点 A 是原生 MapParent，也是无缝地块的一员）。
            // 之前只判断 MapParent_SeamlessTile，导致锚点 A 的传送点未被标为出口格，远行队走不到。
            if (___map == null) return true; // 放行原版
            if (!SeamlessEdgeCells.HasSeamEdge(___map)) return true; // 非 RimExodus 地块放行原版

            // —— RimExodus 地块：跳过原版方形带铺设，自己只标传送点格。——
            // 复刻原版 Rebuild 的骨架但把候选集换成传送点格。
            _dirtyRef(__instance) = false;

            var existing = _exitMapGridRef(__instance);
            if (existing == null)
            {
                _exitMapGridRef(__instance) = new BoolGrid(___map);
            }
            else
            {
                existing.Clear();
            }

            var grid = _exitMapGridRef(__instance);
            if (grid == null) return false; // 保险：反射失败则不画（原版也画不出）
            int maxIdx = grid.Width * grid.Height;

            int marked = 0;
            // 遍历地图上所有传送点（沿六边形接缝带铺设，~100 个），把它们的格标为 exit cell。
            SeamlessEdgeCells.PopulateSeamEdgeCells(___map, _spotCellsScratch);
            for (int i = 0; i < _spotCellsScratch.Count; i++)
            {
                var cell = _spotCellsScratch[i];
                int idx = ___map.cellIndices.CellToIndex(cell);
                if (idx >= 0 && idx < maxIdx && !grid[idx])
                {
                    grid.Set(idx, true);
                    marked++;
                }
            }

            var drawer = _drawerIntRef(__instance);
            if (drawer != null) drawer.SetDirty();

            if (marked > 0 && (RimExodusMod.Settings?.verboseLogging ?? false))
                Log.Message($"[RimExodus] ExitMapGrid.Rebuild prefix: marked {marked} enter-spot cells as exit cells on map {___map.uniqueID} (square band suppressed).");

            return false; // 跳过原版 Rebuild
        }

        // 复用的 scratch 列表（避免每次 Rebuild 分配）。
        private static readonly System.Collections.Generic.List<IntVec3> _spotCellsScratch = new();
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
            if (SeamlessEdgeCells.HasSeamEdge(___map))
            {
                __result = true;
                ___mapUsesExitGrid = true; // 同步改缓存字段，避免同 tick 内其他读取者拿到旧值。
            }
        }
    }
}
