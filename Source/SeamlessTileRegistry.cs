using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的静态注册表。
    /// 提供宿主地图上所有无缝地块口袋地图的 footprint（宿主坐标矩形）查询，
    /// 供 MapEdgeClipDrawer 裁剪跳过判定（方案 B）使用。
    /// </summary>
    public static class SeamlessTileRegistry
    {
        /// <summary>
        /// 计算宿主地图上所有无缝地块口袋地图的 footprint（宿主坐标矩形）。
        /// footprint = 口袋地图局部矩形 + hostOffset。
        /// </summary>
        public static List<CellRect> GetFootprintsOnHost(Map host)
        {
            var result = new List<CellRect>();
            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile parent)
                {
                    continue;
                }
                if (parent.sourceMap != host)
                {
                    continue;
                }

                var pocketMapInstance = parent.Map;
                if (pocketMapInstance == null || pocketMapInstance.Disposed)
                {
                    continue;
                }

                var localRect = new CellRect(0, 0, pocketMapInstance.Size.x, pocketMapInstance.Size.z);
                var hostRect = localRect.MovedBy(parent.hostOffset.ToIntVec2);
                result.Add(hostRect);
            }
            return result;
        }

        /// <summary>
        /// 用最近中心所有权规则（六边形方案）反查宿主地图上的一个格子属于哪个口袋地图。
        /// 候选 = 宿主地图 + 所有 footprint 覆盖该格的锚定口袋地图；取距离最近的格子中心作为逻辑所有者。
        /// 返回 true 表示该格属于某个口袋地图（owner 为其 MapParent）；false 表示属于宿主地图自身。
        /// </summary>
        public static bool TryGetOwnerPocketMap(Map host, IntVec3 hostCell, out MapParent_SeamlessTile owner)
        {
            owner = null;
            if (host == null)
            {
                return false;
            }

            var hostCenter = host.Center;
            var bestDistSq = DistanceSq(hostCell, hostCenter);
            var bestIsPocket = false;
            MapParent_SeamlessTile bestPocket = null;

            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile parent || parent.sourceMap != host)
                {
                    continue;
                }
                var pocketInstance = parent.Map;
                if (pocketInstance == null || pocketInstance.Disposed)
                {
                    continue;
                }
                // 只把 footprint 覆盖该格的邻居作为候选，避免远处口袋地图误抢所有权。
                if (!SeamlessMapUtility.HostCellInFootprint(hostCell, parent))
                {
                    continue;
                }

                var pocketCenter = SeamlessMapUtility.ToHostCoord(pocketInstance.Center, parent);
                var d = DistanceSq(hostCell, pocketCenter);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    bestIsPocket = true;
                    bestPocket = parent;
                }
            }

            if (bestIsPocket)
            {
                owner = bestPocket;
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

        /// <summary>判断 map 是否为锚定在 hostMap 上的无缝地块口袋地图。</summary>
        public static bool IsPocketMapAnchoredTo(Map map, Map hostMap)
        {
            if (map == null || hostMap == null)
            {
                return false;
            }

            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is MapParent_SeamlessTile candidate && candidate.sourceMap == hostMap && candidate.Map == map)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
