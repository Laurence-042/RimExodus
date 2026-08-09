using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 传送点延迟绑定工具（阶段4a：预铺 + 延迟绑定）。
    ///
    /// 预铺时每张地图沿全部世界邻居边铺单端 spot（<see cref="CompSeamlessTileEnterSpot.targetWorldTile"/> 记录对端，
    /// <see cref="CompSeamlessTileEnterSpot.CounterpartSpot"/> 为 null）。当对端邻居地图加载后，
    /// 调用本工具扫描两端的未绑定 spot，按 targetWorldTile 匹配 + 坐标平移校验（两端世界坐标重合契约）互绑。
    ///
    /// 多跳天然支持：新地块加载后遍历其所有已存在邻居，逐一调用本工具绑定。
    /// </summary>
    public static class SeamlessEnterSpotBinder
    {
        /// <summary>
        /// 绑定 mapA 与 mapB 之间所有可配对的未绑定传送点。
        /// 两端 worldTile 互为对方，坐标校验：配对的两个 spot 在各自地图本地坐标满足
        /// <c>cellB = cellA - cellAMinusCellB</c>（两端世界坐标重合契约）。
        /// </summary>
        /// <param name="mapA">一端地图。</param>
        /// <param name="worldTileA">mapA 对应的世界地块（用于匹配 mapB 上 targetWorldTile==worldTileA 的 spot）。</param>
        /// <param name="mapB">另一端地图。</param>
        /// <param name="worldTileB">mapB 对应的世界地块。</param>
        /// <param name="cellAMinusCellB">cellA - cellB 的平移量（即从 mapA 查 mapB 邻居表得到的 offset，满足 neighborLocal + offset = mapACoord，故 cellB = cellA - offset）。</param>
        /// <returns>成功绑定的 spot 对数。</returns>
        public static int BindUnboundSpotsBetween(Map mapA, int worldTileA, Map mapB, int worldTileB, IntVec3 cellAMinusCellB)
        {
            if (mapA == null || mapB == null) return 0;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return 0;

            // 收集 mapB 上 targetWorldTile==worldTileA 且未绑定的 spot，按格位置建索引。
            var bSpotsByCell = new Dictionary<IntVec3, Thing>();
            foreach (var thing in mapB.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;
                if (comp.targetWorldTile != worldTileA) continue;
                if (comp.CounterpartSpot != null) continue;
                bSpotsByCell[thing.Position] = thing;
            }

            if (bSpotsByCell.Count == 0) return 0;

            // 遍历 mapA 上 targetWorldTile==worldTileB 且未绑定的 spot，找对端配对。
            var bound = 0;
            var aSpots = mapA.listerThings.ThingsOfDef(enterSpotDef);
            // 快照避免绑定过程中修改集合。
            var snapshot = new List<Thing>(aSpots);
            foreach (var thing in snapshot)
            {
                var compA = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (compA == null) continue;
                if (compA.targetWorldTile != worldTileB) continue;
                if (compA.CounterpartSpot != null) continue;

                var cellA = thing.Position;
                var expectedCellB = cellA - cellAMinusCellB;
                if (!bSpotsByCell.TryGetValue(expectedCellB, out var spotB)) continue;

                var compB = spotB.TryGetComp<CompSeamlessTileEnterSpot>();
                if (compB == null || compB.CounterpartSpot != null) continue;

                // 互绑。
                compA.CounterpartSpot = spotB;
                compB.CounterpartSpot = thing;
                bound++;
            }

            if (bound > 0)
            {
                Log.Message($"[RimExodus] BindUnboundSpotsBetween mapA={mapA.uniqueID}(wt={worldTileA}) <-> " +
                    $"mapB={mapB.uniqueID}(wt={worldTileB}), bound {bound} pairs (cellA-cellB={cellAMinusCellB}).");
            }

            return bound;
        }
    }
}
