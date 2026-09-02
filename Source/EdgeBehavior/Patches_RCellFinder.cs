using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 阶段4前置：让 <see cref="RCellFinder"/> 三个**自构方形边缘格**的出口选择方法改用传送点。
    ///
    /// 背景：本 mod 的边缘归一策略主要在根原语层（<see cref="Patches_CellFinder"/> patch
    /// <c>CellFinder.TryFindRandomEdgeCellWith</c> + <c>RandomEdgeCell</c>，<see cref="Patches_Reachability"/>
    /// patch <c>CanReachMapEdge</c>），一处覆盖袭击/远行队/商队/访客/行人穿越等全部消费者。
    ///
    /// 但 <see cref="RCellFinder.TryFindBestExitSpot"/>/<see cref="RCellFinder.TryFindRandomExitSpot"/> 的
    /// 主体自己直接构造方形边缘格（如 <c>new IntVec3(0,0,result.z)</c>），不走根原语；
    /// <see cref="RCellFinder.TryFindClosestEdgeCellTo"/> 由 <c>Dialog_FormCaravan</c> 直接调用。
    /// 这三个方法必须单独 Prefix 拦截，把出口目标改成最近的可达传送点格。
    ///
    /// 这样 JobGiver_ExitMap 生成带 <c>exitMapOnArrival=true</c> 的 Goto（目标=传送点），pawn 走到传送点
    /// 后原生 JobDriver_Goto.TryExitMap 触发 → 远行队生成。TryTriggerTransfer 检查到
    /// exitMapOnArrival=true 放行（不跨图）。
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
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] TryFindBestExitSpot patched: pawn {pawn.LabelShort} exit target -> enter spot {enterSpot} (was edge, redirected).");
                return false;
            }
            // 诊断插桩（2026-08 远行队"无法离开此区域"排查）：有传送点但找不到可用/可达的——
            // 记录 spot 统计定位是"全部不可站立"还是"全部不可达"。
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] TryFindBestExitSpot: pawn {pawn?.LabelShort} found no usable enter spot on map {pawn?.Map?.uniqueID} ({SeamlessExitSpotFinder.DescribeEnterSpots(pawn.Map)}) — passthrough vanilla.");
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
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] TryFindRandomExitSpot patched: pawn {pawn.LabelShort} exit target -> enter spot {enterSpot}.");
                return false;
            }
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] TryFindRandomExitSpot: pawn {pawn?.LabelShort} found no usable enter spot on map {pawn?.Map?.uniqueID} ({SeamlessExitSpotFinder.DescribeEnterSpots(pawn.Map)}) — passthrough vanilla.");
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
            // 沉浸模式（seamExitBandEnabled=false，2026-08）：不重定向、放行原版——组队界面出口
            // 一并禁用（用户定夺：隐藏接缝带后玩家不应还能经传送点组队离场；裁切图原版方形边
            // 全 void → 出口结构性失败 → 组队报错，属预期，此模式不为正常游玩设计）。
            if (!SeamExitBandGating.Enabled) return true;
            // 原版本体 = 从 root 区域 BFS（PassDoors）到可达的边缘区域——保证出口可达。
            // 我们必须同口径过滤：出口格是 pawn 要实际走到并 ExitMap 的终点，选到隔河/隔山的
            // 传送点会让集结后的 pawn 永久卡路径，且打包点搜索（CanReach(exitSpot→打包点)）
            // 会连带失败（2026-08 "未发现有效的打包点"排查补）。
            var enterSpot = SeamlessExitSpotFinder.FindNearestEnterSpot(root, map, null, requireReachableFromRoot: true);
            if (enterSpot.IsValid)
            {
                result = enterSpot;
                __result = true;
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] TryFindClosestEdgeCellTo patched: exit spot {enterSpot} (root={root}).");
                return false;
            }
            // 诊断插桩（Warning 级——本方法仅 Dialog_FormCaravan 组队时调用、低频）：失败 = 传送点
            // 全部不可站立或从 root 不可达，放行原版（裁切图上原版方形边亦全败 → 走 TryFindExitSpot
            // 兜底，5 参已接缝化、validator 含逐殖民者可达性，通常也失败 → 组队报错）。
            Log.Warning($"[RimExodus] TryFindClosestEdgeCellTo on map {map.uniqueID}(wt={SeamlessTileRegistry.GetMapWorldTile(map)}): " +
                        $"{SeamlessExitSpotFinder.DescribeEnterSpots(map)} but none reachable from {root} — passthrough vanilla (caravan formation likely fails).");
            return true;
        }
    }

    /// <summary>在 RimExodus 地块上找一个 pawn 可到达的传送点格。</summary>
    internal static class SeamlessExitSpotFinder
    {
        /// <summary>map 是否有 RimExodus 传送点（含玩家家园图——原生 MapParent 但也是无缝世界一员）。</summary>
        internal static bool HasRimExodusEnterSpots(Map map)
        {
            if (map == null) return false;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return false;
            return map.listerThings.ThingsOfDef(enterSpotDef).Any();
        }

        /// <summary>诊断插桩（2026-08）：传送点统计（总数/可站立数），失败日志定位"全不可站立"。</summary>
        internal static string DescribeEnterSpots(Map map)
        {
            if (map == null) return "map=null";
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return "spotDef missing";
            var spots = map.listerThings.ThingsOfDef(enterSpotDef);
            var standable = 0;
            for (var i = 0; i < spots.Count; i++)
            {
                if (spots[i].Position.Standable(map)) standable++;
            }
            return $"enterSpots total={spots.Count} standable={standable}";
        }

        internal static IntVec3 FindReachableEnterSpot(Pawn pawn)
        {
            var map = pawn.Map;
            if (map == null) return IntVec3.Invalid;
            return FindNearestEnterSpot(pawn.Position, map, pawn);
        }

        /// <summary>找离 root 最近的（可站立的）传送点。可选传入 pawn 做可达性过滤；
        /// 或 requireReachableFromRoot = 以 root 为起点（PassDoors，对齐原版 TryFindClosestEdgeCellTo
        /// 的区域 BFS 语义）做可达性过滤。</summary>
        internal static IntVec3 FindNearestEnterSpot(IntVec3 root, Map map, Pawn pawn = null, bool requireReachableFromRoot = false)
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
                if (requireReachableFromRoot && !map.reachability.CanReach(root, spot, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors))) continue;
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
