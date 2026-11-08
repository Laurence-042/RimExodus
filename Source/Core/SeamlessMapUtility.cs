using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的坐标转换工具。离散格归属以 REF MapCellSpace 为事实源：调用方提供
    /// 参考 Map，TryResolve 返回真实 Map + 局部格；runtime/provider 尚未就绪时才回落旧注册表。
    /// 与 VMF 的 VehicleMapUtility 不同，静态地块地图没有旋转项，只保留水平整数平移。
    ///
    /// 偏移语义：邻居本地坐标 + offset = 当前地图坐标系的坐标。
    /// offset 由邻居表 <see cref="NeighborLink.offset"/> 提供，方向无关（对称）。
    /// </summary>
    public static class SeamlessMapUtility
    {
        /// <summary>
        /// 把当前地图坐标解析成实际应操作的 Map + 局部坐标。
        /// MapCellSpace 的 provider 用同一接缝多边形裁决当前图 + 活跃直接邻居：cell 在当前地块
        /// 多边形内则归当前图；宿主 void 而邻图多边形拥有则归邻图。返回 true 仅表示解析到
        /// 另一张图；当前图拥有、未解析或服务尚未就绪均返回 false 并保留当前参考系语义。
        /// </summary>
        public static bool TryResolveMapPosition(Vector3 mouseMapPosition, Map currentMap, out Map targetMap, out IntVec3 targetLocalCell)
        {
            var currentCell = IntVec3.FromVector3(mouseMapPosition);

            if (currentMap != null && SeamlessSpatialQueries.TryCapture(currentMap, out var space)
                && space.TryResolve(currentCell, out var address))
            {
                targetMap = address.Map;
                targetLocalCell = address.Cell;
                return address.Map != currentMap;
            }

            // MapRuntime/provider 在换档或初始化极早期可能尚不可用；旧入口仅作行为等价后备。
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
