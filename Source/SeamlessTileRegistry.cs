using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的静态注册表（基于直接邻居表，对称架构）。
    /// 提供"当前地图所有直接邻居的 footprint"查询，供 MapEdgeClipDrawer 裁剪与渲染使用。
    /// 不再依赖 sourceMap/IsPocketMap，支持 A→B、B→A、B→C 对等处理。
    /// </summary>
    public static class SeamlessTileRegistry
    {
        /// <summary>
        /// 计算当前地图所有直接邻居的 footprint（当前地图坐标系矩形）。
        /// footprint = 邻居本地矩形 + 邻居相对偏移。
        /// </summary>
        public static List<CellRect> GetNeighborFootprints(Map currentMap)
        {
            var result = new List<CellRect>();
            foreach (var info in SeamlessTileGraph.GetAllNeighbors(currentMap))
            {
                var localRect = new CellRect(0, 0, info.map.Size.x, info.map.Size.z);
                var currentRect = localRect.MovedBy(info.offset.ToIntVec2);
                result.Add(currentRect);
            }
            return result;
        }

        /// <summary>
        /// 用最近中心所有权规则（六边形方案）反查当前地图上的一个格子属于哪个邻居。
        /// 候选 = 当前地图自身 + 所有 footprint 覆盖该格的直接邻居；取距离最近的格子中心。
        /// 返回 true 表示该格属于某个邻居；out owner 为邻居的 Map。
        /// </summary>
        public static bool TryGetOwnerNeighbor(Map currentMap, IntVec3 currentCell, out Map ownerMap, out IntVec3 ownerLocalCell)
        {
            ownerMap = null;
            ownerLocalCell = default;
            if (currentMap == null)
            {
                return false;
            }

            var currentCenter = currentMap.Center;
            var bestDistSq = DistanceSq(currentCell, currentCenter);
            Map bestNeighbor = null;
            IntVec3 bestLocalCell = default;

            foreach (var info in SeamlessTileGraph.GetAllNeighbors(currentMap))
            {
                // 邻居覆盖该格的前提：该格在邻居本地坐标系内（即邻居 footprint 覆盖）。
                var neighborLocal = currentCell - info.offset;
                if (!neighborLocal.InBounds(info.map))
                {
                    continue;
                }

                // 邻居中心转到当前地图坐标系。
                var neighborCenterInCurrent = info.map.Center + info.offset;
                var d = DistanceSq(currentCell, neighborCenterInCurrent);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    bestNeighbor = info.map;
                    bestLocalCell = neighborLocal;
                }
            }

            if (bestNeighbor != null)
            {
                ownerMap = bestNeighbor;
                ownerLocalCell = bestLocalCell;
                return true;
            }
            return false;
        }

        private static int DistanceSq(IntVec3 a, IntVec3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>判断 a 与 b 是否为直接邻居（对称关系）。</summary>
        public static bool AreSeamlessNeighbors(Map a, Map b)
        {
            return SeamlessTileGraph.AreNeighbors(a, b);
        }
    }
}
