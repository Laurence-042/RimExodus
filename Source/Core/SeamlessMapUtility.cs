using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的鼠标拾取入口。优先使用当前图的实地形，邻图归属由 REF MapCellSpace 解析。
    /// 与 VMF 的 VehicleMapUtility 不同，静态地块地图没有旋转项，只保留水平整数平移。
    ///
    /// 偏移语义：邻居本地坐标 + offset = 当前地图坐标系的坐标。
    /// offset 由邻居表 <see cref="NeighborLink.offset"/> 提供，方向无关（对称）。
    /// </summary>
    public static class SeamlessMapUtility
    {
        /// <summary>
        /// 点击当前图界内的实地形时保留本地操作，包括多边形外的接缝格。
        /// 只有点击 void 或界外时才查邻图，成功解析到邻图则返回 true。
        /// </summary>
        public static bool TryResolveMapPosition(Vector3 mouseMapPosition, Map currentMap, out Map targetMap, out IntVec3 targetLocalCell)
            => TryResolveNeighborCell(IntVec3.FromVector3(mouseMapPosition), currentMap, out targetMap, out targetLocalCell);

        internal static bool TryResolveNeighborCell(IntVec3 currentCell, Map currentMap, out Map targetMap, out IntVec3 targetLocalCell)
        {
            targetMap = currentMap;
            targetLocalCell = currentCell;
            if (currentMap == null || currentMap.Disposed) return false;

            if (currentCell.InBounds(currentMap)
                && currentMap.terrainGrid?.TerrainAt(currentCell) is TerrainDef terrain
                && terrain.defName != "RimExodus_Void")
                return false;

            if (SeamlessSpatialQueries.TryCapture(currentMap, out var space)
                && space.TryResolve(currentCell, out var address))
            {
                targetMap = address.Map;
                targetLocalCell = address.Cell;
                return address.Map != currentMap;
            }

            // MapRuntime/provider 在换档或初始化极早期可能尚不可用；旧入口仅作行为等价后备。
            if (SeamlessTileRegistry.TryGetOwnerNeighbor(currentMap, currentCell, out var ownerMap, out var ownerLocalCell))
            {
                targetMap = ownerMap;
                targetLocalCell = ownerLocalCell;
                return true;
            }

            return false;
        }
    }
}
