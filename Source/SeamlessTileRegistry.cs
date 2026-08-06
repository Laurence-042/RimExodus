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
    }
}
