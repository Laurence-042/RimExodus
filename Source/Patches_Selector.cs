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
}
