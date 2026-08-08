using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 首个玩家殖民者跨图进入新地块时，自动聚焦到该地块并无感调整相机。
    /// 仅一次：玩家切回原地图后，第二个 pawn 进入不再触发自动聚焦。
    /// </summary>
    public static class SeamlessCameraFocus
    {
        /// <summary>
        /// 当 pawn 跨图到达 arrivalMap 时，若是首个玩家殖民者且该地块未被聚焦过，
        /// 则切换 CurrentMap 到 arrivalMap 并无感调整相机位置。
        /// 必须在续程之前调用（切图后 pawn.Map==CurrentMap，避免 SelectInternal 二次跳镜头）。
        /// </summary>
        public static void TryAutoFocusOnArrival(Pawn pawn, Map arrivalMap)
        {
            if (pawn == null || arrivalMap == null)
            {
                return;
            }
            // 仅玩家殖民者触发（排除敌人/野生动物）。
            if (!pawn.IsColonist)
            {
                return;
            }
            // 仅口袋地块（MapParent_SeamlessTile）触发，且每个地块仅一次。
            if (arrivalMap.Parent is not MapParent_SeamlessTile parent || parent.autoFocused)
            {
                return;
            }

            var currentMap = Find.CurrentMap;
            if (currentMap == null || currentMap == arrivalMap)
            {
                return;
            }

            // 找到 arrivalMap 相对 currentMap 的偏移（从 currentMap 的邻居表查）。
            var offset = FindNeighborOffset(currentMap, arrivalMap);
            if (!offset.HasValue)
            {
                Log.Message($"[RimExodus] Auto-focus skipped: arrival map {arrivalMap.uniqueID} is not a direct neighbor of current map {currentMap.uniqueID}.");
                return;
            }

            // 记录切换前相机位置（currentMap 坐标系）。
            var camPos = Find.CameraDriver.MapPosition.ToVector3();

            // 目标位置：同一世界位置在 arrivalMap 坐标系中的坐标 = camPos - offset。
            // （offset 是 arrivalMap 本地 → currentMap 坐标，故反向：currentMap → arrivalMap = 减去 offset）
            var targetPos = camPos - offset.Value.ToVector3();

            // 切图（触发原生硬跳到 arrivalMap 的 rememberedCameraPos）。
            Current.Game.CurrentMap = arrivalMap;

            // 立即覆盖相机位置，让画面不动（无感）。JumpToCurrentMapLoc 只设 x/z，不 clamp 不动画。
            Find.CameraDriver.JumpToCurrentMapLoc(targetPos);

            parent.autoFocused = true;
            Log.Message($"[RimExodus] Auto-focused to map {arrivalMap.uniqueID}, camera offset by {-offset.Value}.");
        }

        /// <summary>在 currentMap 的邻居表中查找 neighborMap 的相对偏移（neighborMap 本地 → currentMap）。</summary>
        private static IntVec3? FindNeighborOffset(Map currentMap, Map neighborMap)
        {
            foreach (var info in SeamlessTileGraph.GetAllNeighbors(currentMap))
            {
                if (info.map == neighborMap)
                {
                    return info.offset;
                }
            }
            return null;
        }
    }
}
