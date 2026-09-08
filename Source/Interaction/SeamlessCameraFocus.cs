using RimWorld;
using UnityEngine;
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
            // 仅地块地图（MapParent_SeamlessTile）触发，且每个地块仅一次。
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
            if (!SeamlessViewProjection.TryProject(arrivalMap, IntVec3.Zero, currentMap, out var offset))
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Auto-focus skipped: arrival map {arrivalMap.uniqueID} is not a direct neighbor of current map {currentMap.uniqueID}.");
                return;
            }

            // 记录切换前相机位置和缩放（currentMap 坐标系）。
            // 缩放必须记：CurrentMap setter 会触发 Notify_SwitchedMap 用 SetRootPosAndSize
            // 从新地图 rememberedCameraPos 同时恢复位置+缩放，只靠 JumpToCurrentMapLoc 恢复位置不够。
            var camPos = Find.CameraDriver.MapPosition.ToVector3();
            var camSize = Find.CameraDriver.RootSize;

            // 目标位置：同一世界位置在 arrivalMap 坐标系中的坐标 = camPos - offset。
            // （offset 是 arrivalMap 本地 → currentMap 坐标，故反向：currentMap → arrivalMap = 减去 offset）
            var targetPos = camPos - offset.ToVector3();

            // 切图（触发原生硬跳到 arrivalMap 的 rememberedCameraPos，覆盖位置+缩放）。
            Current.Game.CurrentMap = arrivalMap;

            // 立即同时恢复位置和缩放，让画面不动（无感）。
            // SetRootPosAndSize 与原生 Notify_SwitchedMap 用同一方法，y 由 ApplyPositionToGameObject 重算。
            Find.CameraDriver.SetRootPosAndSize(new Vector3(targetPos.x, 0f, targetPos.z), camSize);

            parent.autoFocused = true;
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Auto-focused to map {arrivalMap.uniqueID}, camera offset by {-offset}.");
        }
    }
}
