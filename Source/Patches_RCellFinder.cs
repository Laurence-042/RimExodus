using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 阶段5原型：让远行队组建流程的出口目标指向传送点（而非地图边缘，边缘被六边形 void 封死）。
    ///
    /// 背景：六边形裁切把地块地图的矩形边缘格都切成了 void（不可站立）。
    /// 原版 <see cref="RCellFinder.TryFindBestExitSpot"/>/<see cref="RCellFinder.TryFindRandomExitSpot"/>
    /// 在地图矩形边缘找可站立且可达的格作为出口目标——六边形 void 边缘全失败 → 返回 false →
    /// <see cref="JobGiver_ExitMap.TryGiveJob"/> 返回 null（不生成 ExitMap Job）→ 远行队组建流程卡住，
    /// pawn 没有 ExitMap Job，可能踩到传送点被 <see cref="SeamlessMapTransferTrigger.TryTriggerTransfer"/> 直接跨图。
    ///
    /// 修复：Prefix 拦截这两个方法，对 RimExodus 地块（<see cref="MapParent_SeamlessTile"/>），
    /// 找一个 pawn 可到达的传送点格作为出口目标返回 true。这样 JobGiver_ExitMap 生成带
    /// <c>exitMapOnArrival=true</c> 的 Goto（目标=传送点），pawn 走到传送点后原生 JobDriver_Goto.TryExitMap
    /// 触发 → 远行队生成。TryTriggerTransfer 检查到 exitMapOnArrival=true 放行（不跨图）。
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindBestExitSpot))]
    static class Patch_RCellFinder_TryFindBestExitSpot
    {
        static bool Prefix(Pawn pawn, ref IntVec3 spot, ref bool __result, TraverseMode mode, bool canBash)
        {
            if (!SeamlessExitSpotFinder.HasRimExodusEnterSpots(pawn?.Map)) return true;
            var enterSpot = SeamlessExitSpotFinder.FindReachableEnterSpot(pawn);
            if (enterSpot.IsValid)
            {
                spot = enterSpot;
                __result = true;
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] TryFindBestExitSpot patched: pawn {pawn.LabelShort} exit target -> enter spot {enterSpot} (was edge, redirected).");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomExitSpot))]
    static class Patch_RCellFinder_TryFindRandomExitSpot
    {
        static bool Prefix(Pawn pawn, ref IntVec3 spot, ref bool __result, TraverseMode mode)
        {
            if (!SeamlessExitSpotFinder.HasRimExodusEnterSpots(pawn?.Map)) return true;
            var enterSpot = SeamlessExitSpotFinder.FindReachableEnterSpot(pawn);
            if (enterSpot.IsValid)
            {
                spot = enterSpot;
                __result = true;
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] TryFindRandomExitSpot patched: pawn {pawn.LabelShort} exit target -> enter spot {enterSpot}.");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 家园地图（IsPlayerHome）组建远行队时，Dialog_FormCaravan 用 <see cref="RCellFinder.TryFindClosestEdgeCellTo"/>
    /// 找边缘出口格（exitSpot）。六边形裁切后地图边缘是 void，找不到 → 远行队 pawn 卡住或硬蹭 void。
    /// Patch 让它对有 RimExodus 传送点的地图返回最近可达传送点作为 exitSpot。
    /// pawn 走到传送点 → 远行队 lord 触发 ReadyToExitMap → FormAndCreateCaravan → 大地图远行队。
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindClosestEdgeCellTo))]
    static class Patch_RCellFinder_TryFindClosestEdgeCellTo
    {
        static bool Prefix(IntVec3 root, Map map, ref IntVec3 result, ref bool __result)
        {
            if (!SeamlessExitSpotFinder.HasRimExodusEnterSpots(map)) return true;
            var enterSpot = SeamlessExitSpotFinder.FindNearestEnterSpot(root, map);
            if (enterSpot.IsValid)
            {
                result = enterSpot;
                __result = true;
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] TryFindClosestEdgeCellTo patched: exit spot {enterSpot} (root={root}).");
                return false;
            }
            return true;
        }
    }

    /// <summary>在 RimExodus 地块上找一个 pawn 可到达的传送点格。</summary>
    internal static class SeamlessExitSpotFinder
    {
        /// <summary>map 是否有 RimExodus 传送点（含锚点 A，A 是原生 MapParent 但也是无缝地块的一员）。</summary>
        internal static bool HasRimExodusEnterSpots(Map map)
        {
            if (map == null) return false;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return false;
            return map.listerThings.ThingsOfDef(enterSpotDef).Any();
        }

        internal static IntVec3 FindReachableEnterSpot(Pawn pawn)
        {
            var map = pawn.Map;
            if (map == null) return IntVec3.Invalid;
            return FindNearestEnterSpot(pawn.Position, map, pawn);
        }

        /// <summary>找离 root 最近的（可站立的）传送点。可选传入 pawn 做可达性过滤。</summary>
        internal static IntVec3 FindNearestEnterSpot(IntVec3 root, Map map, Pawn pawn = null)
        {
            if (map == null) return IntVec3.Invalid;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return IntVec3.Invalid;

            Thing bestSpot = null;
            float bestDist = float.MaxValue;
            foreach (var spot in map.listerThings.ThingsOfDef(enterSpotDef))
            {
                if (!spot.Position.Standable(map)) continue;
                if (pawn != null && !map.reachability.CanReach(pawn.Position, spot, PathEndMode.OnCell, TraverseParms.For(pawn, Danger.Deadly))) continue;
                float d = (spot.Position - root).LengthHorizontalSquared;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestSpot = spot;
                }
            }
            return bestSpot?.Position ?? IntVec3.Invalid;
        }
    }
}
