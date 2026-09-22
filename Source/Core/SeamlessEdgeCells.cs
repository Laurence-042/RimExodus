using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝带格的统一访问入口。本类是"所有边缘行为改用接缝带"策略的几何根基：
    /// 把"传送点格集合"作为接缝带的权威定义，供 <see cref="Patches_CellFinder"/>/
    /// <see cref="Patches_Reachability"/>/ <see cref="Patches_ExitMapGrid"/> 复用，
    /// 避免每处 patch 各自遍历 <c>listerThings</c>。
    ///
    /// 设计：传送点（<c>RimExodus_SeamlessEnterSpot</c>）由 <see cref="SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors"/>
    /// 沿传送圈（离散边圈 ∪ 带外圈，接缝带外侧 2 圈，见 doc/接缝带定义.md）铺设
    /// （**无可通行性过滤**，用户定夺 2026-08——spot 是纯逻辑连接设施，能不能走由地形运行时决定；
    /// 传送圈是 3 圈接缝带内的实地形，非 void 格），传送点格集合与
    /// <see cref="ExitMapGrid"/> 标记的 exit cell 集合同源。复用传送点格而非重新跑带几何，
    /// 保证唯一口径。唯一例外是边缘选点回落（<see cref="GetSeamEdgeCellsWithBandFallback"/>）：
    /// 无传送点（生成期 389-1490 窗口）时按 <see cref="SeamlessPolygonGeometry.BuildSeamBand"/>
    /// 传送圈取格，两池范围恒等（铺点本就无过滤铺满该圈）。
    ///
    /// 缓存：按 (map.uniqueID, spot 数量) 失效。spot 铺设/邻居加载后数量变化即重建。
    /// </summary>
    internal static class SeamlessEdgeCells
    {
        private static ThingDef _cachedEnterSpotDef;
        private static ThingDef EnterSpotDef
        {
            get
            {
                if (_cachedEnterSpotDef == null)
                    _cachedEnterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
                return _cachedEnterSpotDef;
            }
        }

        // 缓存键：map.uniqueID；缓存值：{spots snapshot 版本号, cells}。版本号 = 当前 spot 数量，
        // spot 增减（新邻居加载/卸载）时数量变化触发重建。
        private static readonly Dictionary<int, (int version, List<IntVec3> cells)> _cache = new();

        /// <summary>该地图是否为 RimExodus 无缝图（有传送点）。含原生 parent 图（家园/原生家族——原生 MapParent，也是无缝世界一员）。</summary>
        internal static bool HasSeamEdge(Map map)
        {
            if (map == null || EnterSpotDef == null) return false;
            return map.listerThings.ThingsOfDef(EnterSpotDef).Count > 0;
        }

        /// <summary>
        /// 返回该地图所有传送点格的快照列表（= 六边形接缝带）。调用者可自由打乱/遍历。
        /// 带缓存：spot 数量未变时直接返回缓存列表（只读语义，调用者不应修改）。
        /// </summary>
        internal static List<IntVec3> GetSeamEdgeCells(Map map)
        {
            if (map == null || EnterSpotDef == null) return null;

            var spots = map.listerThings.ThingsOfDef(EnterSpotDef);
            int version = spots.Count;
            if (version == 0) return null;

            if (_cache.TryGetValue(map.uniqueID, out var entry) && entry.version == version)
                return entry.cells;

            // 重建：把所有传送点 Position 拷贝到新列表。
            var cells = new List<IntVec3>(version);
            for (int i = 0; i < version; i++)
                cells.Add(spots[i].Position);

            _cache[map.uniqueID] = (version, cells);
            return cells;
        }

        /// <summary>
        /// 边缘选点专用池（2026-09 VEF 边缘入场开局修复）：<see cref="GetSeamEdgeCells"/> 非空时
        /// 原样返回——spot 池还承载步行敌人邻图入场、袭击朝向（<see cref="PopulateFacingCells"/>）等
        /// 运行时机制，语义不变。无传送点时（生成期 389-1490 窗口，如 genStep 875 的边缘入场
        /// ScenPart；以及恢复模式后的瞬态）对 RimExodus 表面图回落 BuildSeamBand 的传送圈
        /// （离散边圈 ∪ 带外圈，与稳态 spot 铺设范围恒等）；非 RimExodus 图返回 null。
        /// validator 链由调用方（Patches_CellFinder）原样执行。不加独立缓存：BuildSeamBand 自带
        /// (worldTile, mapSize) 进程缓存，无点场景低频，每次拷贝一圈格即可。
        /// </summary>
        internal static List<IntVec3> GetSeamEdgeCellsWithBandFallback(Map map)
        {
            var spotCells = GetSeamEdgeCells(map);
            if (spotCells != null) return spotCells;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return null;

            var transportRing = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x).TransportRing;
            if (transportRing.Count == 0) return null;
            return new List<IntVec3>(transportRing);
        }

        /// <summary>
        /// 把接缝带格填入 <paramref name="result"/>（Clear + Add，复用调用者的列表避免分配）。
        /// 供需要"自己遍历且不复用缓存列表引用"的调用者使用（如 Reachability 逐个 CanReach）。
        /// </summary>
        internal static void PopulateSeamEdgeCells(Map map, List<IntVec3> result)
        {
            result.Clear();
            if (map == null || EnterSpotDef == null) return;
            var spots = map.listerThings.ThingsOfDef(EnterSpotDef);
            int count = spots.Count;
            for (int i = 0; i < count; i++)
                result.Add(spots[i].Position);
        }

        /// <summary>随机一个接缝格。无接缝格返回 <see cref="IntVec3.Invalid"/>。</summary>
        internal static IntVec3 RandomSeamEdgeCell(Map map)
        {
            var cells = GetSeamEdgeCells(map);
            if (cells == null || cells.Count == 0) return IntVec3.Invalid;
            return cells[Rand.Range(0, cells.Count)];
        }

        /// <summary>
        /// 朝向指定世界邻居 tile 的传送点格（袭击外缘生成 2026-09）。spot 由
        /// <see cref="SeamlessEnterSpotPlacer"/> 铺满全部世界邻居边（含未生成邻居——
        /// hasArrival=false 不影响取格），故按 <c>targetWorldTile</c> 过滤即得该侧格集，
        /// 无需几何锚点。无缓存（袭击低频；def 索引 O(1)）。
        /// </summary>
        internal static void PopulateFacingCells(Map map, int worldTile, List<IntVec3> result)
        {
            result.Clear();
            if (map == null || EnterSpotDef == null || worldTile < 0) return;
            var spots = map.listerThings.ThingsOfDef(EnterSpotDef);
            for (int i = 0; i < spots.Count; i++)
            {
                var comp = spots[i].TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp != null && comp.targetWorldTile == worldTile) result.Add(spots[i].Position);
            }
        }

        /// <summary>
        /// cell 是否为传送点格（thingGrid 直查 spot def，O(1)，与 <see cref="GetSeamEdgeCells"/> 同源同口径：
        /// 传送点铺设位置集合 = 接缝带权威定义）。供预加载排除等单格判定使用，避免取整表。
        /// </summary>
        internal static bool IsSeamEdgeCell(Map map, IntVec3 cell)
        {
            if (map == null || EnterSpotDef == null || !cell.InBounds(map)) return false;
            var things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def == EnterSpotDef) return true;
            }
            return false;
        }
    }
}
