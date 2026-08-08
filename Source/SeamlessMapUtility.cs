using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的坐标转换工具（基于显式偏移，对称架构）。
    /// 与 VMF 的 VehicleMapUtility 不同，静态地块地图没有旋转项，
    /// 只保留"地图局部坐标 + 偏移 → 另一地图坐标"的平移映射。
    ///
    /// 偏移语义：邻居本地坐标 + offset = 当前地图坐标系的坐标。
    /// offset 由邻居表 <see cref="NeighborLink.offset"/> 提供，方向无关（对称）。
    /// </summary>
    public static class SeamlessMapUtility
    {
        /// <summary>把局部坐标按偏移映射到目标地图坐标（通用平移）。</summary>
        public static IntVec3 ToMapCoord(IntVec3 localCell, IntVec3 offset)
        {
            return localCell + offset;
        }

        /// <summary>把目标地图坐标按偏移反查为局部坐标（通用平移取反）。</summary>
        public static IntVec3 FromMapCoord(IntVec3 mapCell, IntVec3 offset)
        {
            return mapCell - offset;
        }

        /// <summary>把局部坐标按偏移映射到目标地图的世界绘制位置（Vector3）。</summary>
        public static Vector3 ToMapDrawPos(IntVec3 localCell, IntVec3 offset)
        {
            return (localCell + offset).ToVector3Shifted();
        }

        /// <summary>
        /// 把当前地图坐标解析成实际应操作的 Map + 局部坐标。
        /// 用最近中心所有权规则（六边形方案）在当前地图 + 直接邻居间裁决归属。
        /// 命中邻居则返回该邻居 Map + 其局部坐标；否则归属当前地图自身。
        /// </summary>
        public static bool TryResolveMapPosition(Vector3 mouseMapPosition, Map currentMap, out Map targetMap, out IntVec3 targetLocalCell)
        {
            var currentCell = IntVec3.FromVector3(mouseMapPosition);

            // 用最近中心所有权规则决定归属：候选 = 当前地图 + 覆盖该格的直接邻居，
            // 取距离最近的格子中心作为逻辑所有者。重叠带内靠近当前地图中心的格归当前地图，
            // 靠近邻居中心的格归邻居。
            if (currentMap != null
                && SeamlessTileRegistry.TryGetOwnerNeighbor(currentMap, currentCell, out var ownerMap, out var ownerLocalCell))
            {
                targetMap = ownerMap;
                targetLocalCell = ownerLocalCell;
                return true;
            }

            targetMap = currentMap;
            targetLocalCell = currentCell;
            return false;
        }
    }
}
