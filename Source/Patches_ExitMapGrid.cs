using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4前置：把无缝地块的接缝带标记为出口格（exit cell），使原生远行队组建/撤离/撤退流程生效；
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
    /// 自己初始化一个干净的 <see cref="BoolGrid"/>，把接缝带格标 true。
    ///
    /// 标记集 = **接缝带三圈并集 Band ∪ 传送点格兜底**（2026-08 用户定夺：撤离带拓宽到整个 3 圈接缝带
    /// 而非外侧两圈传送圈——接缝两侧的撤离带镜像连续，切图时接缝和撤离带不跳变；语义影响接受：
    /// 带撤离 flag 的 pawn 踩带内圈即原生离场。首版曾试三色分圈渲染，因颜色过浅缺乏区分度被用户回退，
    /// 恢复原版单色浅绿；接缝位置改由 <see cref="Patch_MapInterface_SeamOutline"/> 的中心线段勾勒）。
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
            // 对所有有 RimExodus 传送点的地图生效（玩家家园图是原生 MapParent，也是无缝世界一员）。
            // 之前只判断 MapParent_SeamlessTile，导致家园图的传送点未被标为出口格，远行队走不到。
            if (___map == null) return true; // 放行原版
            if (!SeamlessEdgeCells.HasSeamEdge(___map)) return true; // 非 RimExodus 地块放行原版

            // —— RimExodus 地块：跳过原版方形带铺设，自己只标接缝带格。——
            // 复刻原版 Rebuild 的骨架但把候选集换成接缝带三圈 + 传送点格。
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
            // 标记集 = 接缝带三圈并集（Band = 离散边 ∪ 带内圈 ∪ 带外圈，2026-08 用户定夺拓宽：
            // 接缝两侧的撤离带在 Band 全宽上镜像连续，切换地图时不再跳变）∪ 传送点格兜底
            // （防几何/铺点异常态漏标）。
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(___map);
            var band = worldTile >= 0
                ? SeamlessPolygonGeometry.BuildSeamBand(worldTile, ___map.Size.x)
                : null;
            if (band != null)
            {
                foreach (var cell in band.Band)
                {
                    int idx = ___map.cellIndices.CellToIndex(cell);
                    if (idx >= 0 && idx < maxIdx && !grid[idx])
                    {
                        grid.Set(idx, true);
                        marked++;
                    }
                }
            }
            // 遍历地图上所有传送点（沿六边形接缝带铺设，~100 个），把它们的格标为 exit cell（兜底）。
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
                Log.Message($"[RimExodus] ExitMapGrid.Rebuild prefix: marked {marked} band+spot cells as exit cells on map {___map.uniqueID} (square band suppressed).");

            return false; // 跳过原版 Rebuild
        }

        // 复用的 scratch 列表（避免每次 Rebuild 分配）。
        private static readonly System.Collections.Generic.List<IntVec3> _spotCellsScratch = new();
    }

    /// <summary>
    /// 让铺了 RimExodus 传送点的地图（含玩家家园图）也算"使用 exit grid"——
    /// 原版 <see cref="ExitMapGrid.MapUsesExitGridNow"/> 对 <c>map.IsPlayerHome</c> 返回 false，
    /// 导致家园图的传送点格虽被标为 exit cell（见 <see cref="Patch_ExitMapGrid_Rebuild"/>），
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

    /// <summary>
    /// 接缝中心线段渲染（2026-08 用户定夺，替代已回退的三色分圈方案）：
    /// 每帧对 CurrentMap 沿**多边形边**（连续边 = 接缝带的几何中心线）画线段勾勒接缝位置——
    /// 两侧线段即中点对齐的视觉基准，对不齐 = 地块投影角度的细微偏差（正常）。
    ///
    /// 实现零自绘 mesh：<see cref="GenDraw.DrawLineBetween(Vector3, Vector3, SimpleColor, float)"/>
    /// 内部 Graphics.DrawMesh 延迟提交（与原版 <c>GenDraw.DrawMapBoundaryLines</c> 同款基础设施、
    /// 同款 AltitudeLayer.MetaOverlays 高度）。挂 <see cref="MapInterface.MapInterfaceUpdate"/> Postfix
    /// （原方法开头有 CurrentMap/WorldRendererUtility 门控，返回后仍需自查）。
    /// 顶点来自 <see cref="SeamlessPolygonGeometry.BuildPolygonVertices"/>（进程缓存，Vector2 纯几何）。
    /// </summary>
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
    static class Patch_MapInterface_SeamOutline
    {
        // 线段颜色：保持与撤离带同族的浅绿但提亮（区分度优先于三色方案——首版三色因过浅被回退）。
        private static readonly SimpleColor SeamLineColor = SimpleColor.Green;

        public static void Postfix()
        {
            var map = Find.CurrentMap;
            if (map == null || !WorldRendererUtility.DrawingMap) return;
            if (Find.ScreenshotModeHandler.Active) return;
            if (!SeamlessEdgeCells.HasSeamEdge(map)) return;
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3) return;

            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            for (int i = 0; i < verts.Count; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % verts.Count];
                GenDraw.DrawLineBetween(
                    new Vector3(a.x, y, a.y),
                    new Vector3(b.x, y, b.y),
                    SeamLineColor,
                    0.2f);
            }
        }
    }
}
