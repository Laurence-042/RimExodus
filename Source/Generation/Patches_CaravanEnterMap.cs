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
            // 影子远行队兜底守卫（2026-08-26 实测教训）：影子的成员是图上 spawned pawn，任何把它塞进
            // Enter 的路径（SettlementUtility.AttackNow、mod 交互等）都会对成员二次 Spawn =
            // "already spawned" 红字 + 归属簿记混乱。直接跳过整个 Enter（不做任何事，也勿放行原方法）。
            if (SeamlessShadowCaravan.IsShadow(caravan))
            {
                Log.Warning("[RimExodus] CaravanEnterMap: blocked attempt to enter map with shadow caravan (projection of on-map pawns); skipping.");
                return false;
            }

            // 远行队进图 = 源集合变化（caravan 消耗、pawn 落图）：请求 governor 尽快重算距离。
            // 同时即时恢复落图全速（2026-08 分级休眠——同跨缝传送的 AfterTransfer 口径）。
            SeamlessTickThrottle.Unthrottle(map, "player caravan entering map");
            SeamlessDormancyGovernor.RequestSweepSoonStatic();

            // 对所有有 RimExodus 传送点的地图生效（含玩家家园图——原生 MapParent 也是无缝世界一员）；其他地图放行原方法。
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

    /// <summary>
    /// 远行队组队完成离图（2026-08）：原版在远行队成立时调 parent 的 Notify_CaravanFormed——
    /// 源图可能就此失去全部玩家 pawn，请求 governor 尽快重算距离（下一安全 tick，
    /// 不在回调里直接睡/删图，见 SeamlessDormancyGovernor.RequestSweepSoon 的时序约束注释）。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_CaravanFormed))]
    static class Patch_MapParent_NotifyCaravanFormed
    {
        static void Postfix()
        {
            SeamlessDormancyGovernor.RequestSweepSoonStatic();
        }
    }
}
