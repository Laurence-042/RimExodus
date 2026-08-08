using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的坐标转换工具。
    /// 与 VMF 的 VehicleMapUtility 不同，静态地块地图没有旋转项，
    /// 只保留"地图局部坐标 → 宿主地图坐标"的平移映射。
    ///
    /// 关键设计（计划书 1.10 结论 4）：
    /// 对端地图与本地地图在接缝处物理重叠（overlap band），
    /// 本端传送点与对端传送点在宿主坐标（drawPos）上重合于同一 tile。
    /// 因此 ToHostCoord 必须保证：对端地图的 enter spot 与本地地图的
    /// exit spot 映射到宿主地图后坐标一致。
    /// </summary>
    public static class SeamlessMapUtility
    {
        /// <summary>
        /// 把口袋地图局部坐标映射到宿主地图坐标。
        /// 静态地块地图无旋转，仅平移 hostOffset。
        /// </summary>
        public static IntVec3 ToHostCoord(IntVec3 localCell, MapParent_SeamlessTile parent)
        {
            return localCell + parent.hostOffset;
        }

        /// <summary>
        /// 把宿主地图坐标反查为口袋地图局部坐标。
        /// </summary>
        public static IntVec3 ToLocalCoord(IntVec3 hostCell, MapParent_SeamlessTile parent)
        {
            return hostCell - parent.hostOffset;
        }

        /// <summary>
        /// 口袋地图局部坐标 → 宿主地图世界绘制位置（Vector3）。
        /// 用于把口袋地图作为 Thing 绘制到宿主地图上。
        /// </summary>
        public static Vector3 ToHostDrawPos(IntVec3 localCell, MapParent_SeamlessTile parent)
        {
            return (localCell + parent.hostOffset).ToVector3Shifted();
        }

        /// <summary>
        /// 判断宿主地图上的一个格子是否落在某个口袋地图的 footprint 内。
        /// 用于 MapEdgeClipDrawer 裁剪跳过判定（方案 B）。
        /// </summary>
        public static bool HostCellInFootprint(IntVec3 hostCell, MapParent_SeamlessTile parent)
        {
            var local = ToLocalCoord(hostCell, parent);
            var map = parent.Map;
            return map != null && local.InBounds(map);
        }

        /// <summary>把宿主坐标解析成实际应操作的 Map + 局部坐标；命中相邻地块 footprint 则返回该口袋地图。</summary>
        public static bool TryResolveMapPosition(Vector3 mouseMapPosition, Map hostMap, out Map targetMap, out IntVec3 targetLocalCell)
        {
            var hostCell = IntVec3.FromVector3(mouseMapPosition);

            // 用最近中心所有权规则（六边形方案）决定归属：候选 = 宿主 + 覆盖该格的口袋地图，
            // 取距离最近的格子中心作为逻辑所有者。这样重叠带内靠近宿主中心的格属于宿主，
            // 靠近口袋地图中心的格属于口袋地图（如北缘 125,0,248/249 属于地图 B，125,0,247 属于宿主）。
            if (hostMap != null && SeamlessTileRegistry.TryGetOwnerPocketMap(hostMap, hostCell, out var parent))
            {
                targetMap = parent.Map;
                targetLocalCell = ToLocalCoord(hostCell, parent);
                return true;
            }

            targetMap = hostMap;
            targetLocalCell = hostCell;
            return false;
        }
    }
}
