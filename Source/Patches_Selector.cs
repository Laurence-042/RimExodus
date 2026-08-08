using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 让鼠标能选中渲染在宿主地图边界外的相邻地块（口袋地图）上的对象。
    /// 仿 VMF 的 Patch_Selector_SelectableObjectsUnderMouse：反查鼠标位置命中口袋地图时
    /// 在该 Map 上收集可选对象；未命中时回退原版，宿主地图自身选中不受影响。
    /// </summary>
    [HarmonyPatch(typeof(Selector), "SelectableObjectsUnderMouse")]
    public static class Patch_Selector_SelectableObjectsUnderMouse
    {
        public static bool Prefix(ref IEnumerable<object> __result)
        {
            var hostMap = Find.CurrentMap;
            var mouseMapPosition = UI.MouseMapPosition();
            if (hostMap == null
                || !SeamlessMapUtility.TryResolveMapPosition(mouseMapPosition, hostMap, out var targetMap, out var targetLocalCell))
            {
                return true;
            }

            // 保留格子内的小数偏移，让口袋地图上的拾取精度与宿主地图一致。
            var hostCell = IntVec3.FromVector3(mouseMapPosition);
            var withinCellOffset = mouseMapPosition - hostCell.ToVector3Shifted();
            var localMousePosition = targetLocalCell.ToVector3Shifted() + withinCellOffset;

            var results = SelectableObjects(targetMap, targetLocalCell, localMousePosition).ToList();
            __result = results;
            return results.Count == 0;
        }

        private static IEnumerable<object> SelectableObjects(Map targetMap, IntVec3 targetLocalCell, Vector3 localMousePosition)
        {
            var targetingParameters = new TargetingParameters
            {
                mustBeSelectable = true,
                canTargetPawns = true,
                canTargetBuildings = true,
                canTargetItems = true,
                mapObjectTargetsMustBeAutoAttackable = false
            };

            var selectableList = SeamlessGenUI.ThingsUnderMouse(localMousePosition, 1f, targetingParameters, targetMap);
            foreach (var thing in selectableList)
            {
                yield return thing;
            }

            var zone = targetMap.zoneManager.ZoneAt(targetLocalCell);
            if (zone != null)
            {
                yield return zone;
            }
        }
    }

    /// <summary>
    /// 选中跨地图 Thing 时，原生 SelectInternal 会切换 CurrentMap 并把相机硬跳到该 Thing 上（聚焦）。
    /// 对于无缝地块，我们希望切换地图后相机保持相对偏移（玩家看上去还在看接缝附近的同一位置），
    /// 而不是聚焦到选中的 Pawn。这里在切图前记录相机位置，切图后用无感偏移覆盖原生聚焦。
    /// </summary>
    [HarmonyPatch(typeof(Selector), "SelectInternal")]
    public static class Patch_Selector_SelectInternal
    {
        // Prefix→Postfix 之间传递的跨图切换上下文（主线程单次使用，Postfix 末尾清空）。
        private static bool hasCrossMapContext;
        private static Vector3 preservedCamPos;
        private static float preservedCamSize;
        private static Vector3 crossMapOffset;

        public static void Prefix(object obj)
        {
            hasCrossMapContext = false;

            // 仅当即将选中跨图 Thing 且目标地图是当前地图的直接邻居时，才记录无感上下文。
            var thing = obj as Thing;
            var currentMap = Find.CurrentMap;
            if (thing == null || currentMap == null)
            {
                return;
            }
            var targetMap = thing.MapHeld;
            if (targetMap == null || targetMap == currentMap)
            {
                return;
            }

            // offset 语义：targetMap 本地 → currentMap（同 SeamlessCameraFocus.FindNeighborOffset）。
            var offset = SeamlessCameraFocus.FindNeighborOffset(currentMap, targetMap);
            if (!offset.HasValue)
            {
                // 不是直接邻居：保持原生聚焦行为，不覆盖。
                return;
            }

            // 在原方法切图之前记录当前相机位置和缩放（currentMap 坐标系）。
            // 缩放必须记：SelectInternal 切图会触发 Notify_SwitchedMap 从新地图记忆值恢复缩放。
            preservedCamPos = Find.CameraDriver.MapPosition.ToVector3();
            preservedCamSize = Find.CameraDriver.RootSize;
            crossMapOffset = offset.Value.ToVector3();
            hasCrossMapContext = true;
        }

        public static void Postfix(object obj)
        {
            if (!hasCrossMapContext)
            {
                return;
            }

            // 原方法已切图并 JumpToCurrentMapLoc 到选中 Thing。这里用无感偏移+缩放覆盖：
            // 同一世界位置在 targetMap 坐标系中的坐标 = camPos - offset。
            var targetPos = preservedCamPos - crossMapOffset;
            // SetRootPosAndSize 同时恢复位置和缩放（JumpToCurrentMapLoc 只恢复位置，缩放会被切图覆盖）。
            Find.CameraDriver.SetRootPosAndSize(new Vector3(targetPos.x, 0f, targetPos.z), preservedCamSize);

            // 清理，避免影响后续同地图选中。
            hasCrossMapContext = false;
        }
    }
}
