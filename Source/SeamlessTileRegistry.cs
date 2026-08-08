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

        /// <summary>反查宿主地图上的一个格子落在哪个无缝地块口袋地图的 footprint 内。</summary>
        public static bool TryGetPocketMapAtHostCell(Map host, IntVec3 hostCell, out MapParent_SeamlessTile parent)
        {
            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile candidate || candidate.sourceMap != host)
                {
                    continue;
                }

                if (SeamlessMapUtility.HostCellInFootprint(hostCell, candidate))
                {
                    parent = candidate;
                    return true;
                }
            }

            parent = null;
            return false;
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
