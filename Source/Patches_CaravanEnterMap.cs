using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4前置：远行队进入 RimExodus 地块地图时，从传送点出生（而非随机边缘）。
    ///
    /// 背景：原版 <see cref="CaravanEnterMapUtility.Enter(Caravan, Map, CaravanEnterMode, ...)"/>
    /// 用 <c>FindNearEdgeCell</c> 随机选地图边缘格出生。对六边形裁切的地块地图，
    /// 边缘多是 void（不可站立），随机边缘格可能落空或落 void 内侧。
    ///
    /// 修复：Prefix 拦截该重载，对 RimExodus 地块（<see cref="MapParent_SeamlessTile"/>），
    /// 选一个可站立的传送点作为出生点，调用 <c>spawnCellGetter</c> 重载完成进入。
    ///
    /// 来源方向精确推断（从远行队路径历史取来源 worldTile，查对应方向传送点）留作后续优化。
    /// 原型阶段用"任一可站立传送点"，已比"随机边缘落 void"可靠得多。
    /// </summary>
    [HarmonyPatch(typeof(CaravanEnterMapUtility), nameof(CaravanEnterMapUtility.Enter),
        new[] { typeof(Caravan), typeof(Map), typeof(CaravanEnterMode), typeof(CaravanDropInventoryMode), typeof(bool), typeof(System.Predicate<IntVec3>) })]
    static class Patch_CaravanEnterMap_Enter
    {
        static bool Prefix(Caravan caravan, Map map, CaravanEnterMode enterMode,
            CaravanDropInventoryMode dropInventoryMode, bool draftColonists,
            System.Predicate<IntVec3> extraCellValidator)
        {
            // 对所有有 RimExodus 传送点的地图生效（含锚点 A）；其他地图放行原方法。
            if (!SeamlessExitSpotFinder.HasRimExodusEnterSpots(map)) return true;

            var enterCell = FindEnterSpot(map);
            if (!enterCell.IsValid)
            {
                // 找不到传送点：放行原方法（回退随机边缘）。
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Warning($"[RimExodus] CaravanEnterMap: no enter spot found on map {map.uniqueID}, fallback to vanilla.");
                return true;
            }

            // 用 spawnCellGetter 重载，出生点 = 传送点附近的随机可站立格。
            System.Func<Pawn, IntVec3> spawnCellGetter = (Pawn p) =>
                CellFinder.RandomSpawnCellForPawnNear(enterCell, map);

            CaravanEnterMapUtility.Enter(caravan, map, spawnCellGetter, dropInventoryMode, draftColonists);
            return false; // 跳过原方法
        }

        /// <summary>找一个可站立的传送点作为远行队出生点。</summary>
        private static IntVec3 FindEnterSpot(Map targetMap)
        {
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return IntVec3.Invalid;

            foreach (var spot in targetMap.listerThings.ThingsOfDef(enterSpotDef))
            {
                if (spot.Position.Standable(targetMap)) return spot.Position;
            }
            return IntVec3.Invalid;
        }
    }
}
