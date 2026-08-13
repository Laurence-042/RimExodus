using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 让"能否到达地图边缘"的判定改用六边形接缝带（而非原版方形地图边缘）。
    ///
    /// 背景：原版 <see cref="Reachability.CanReachMapEdge"/> 通过 <c>Region.District.TouchesMapEdge</c>
    /// 判定，<c>TouchesMapEdge</c> 检查的是矩形地图边缘（<c>WholeMap</c> 的外圈）。六边形裁切后
    /// 矩形边缘多为 void，<c>TouchesMapEdge</c> 几乎恒 false → 袭击/远行队/撤退的"能否撤离"可行性
    /// 判定失效（即使 pawn 旁边就有传送点接缝格）。
    ///
    /// 修复：Prefix 对有 RimExodus 传送点的地图，把判定改为"从起点能否到达任一接缝格"（用
    /// <see cref="Reachability.CanReach"/> 逐个检查）。命中即 true，全部不可达即 false。
    /// 影响：<see cref="RCellFinder.TryFindBestExitSpot"/> 开头 CanReachMapEdge、<c>JobGiver_ExitMap</c>
    /// 可行性、远行队组建——全部改用六边形边缘语义。
    ///
    /// 性能：接缝格 ~100 个，逐个 CanReach 用 region 缓存，单次调用通常只遍历少量 region。
    /// 可达性结果会被 <see cref="Reachability"/> 自身缓存（同 tick 同参数），高频调用代价可控。
    /// </summary>
    [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReachMapEdge))]
    static class Patch_Reachability_CanReachMapEdge
    {
        static bool Prefix(Reachability __instance, IntVec3 c, TraverseParms traverseParms, Map ___map, ref bool __result)
        {
            if (___map == null) return true;
            if (!SeamlessEdgeCells.HasSeamEdge(___map)) return true; // 非 RimExodus 地块放行原版

            // 复刻原版的前置守门（pawn 必须生成在本地图）。
            if (traverseParms.pawn != null)
            {
                if (!traverseParms.pawn.Spawned)
                {
                    __result = false;
                    return false;
                }
                if (traverseParms.pawn.Map != ___map)
                {
                    Log.Error($"Called CanReachMapEdge() with a pawn spawned not on this map. pawn={traverseParms.pawn} pawn.Map={traverseParms.pawn.Map} map={___map}");
                    __result = false;
                    return false;
                }
            }

            // 用接缝格（传送点）替换"地图边缘"语义：从 c 能否到达任一接缝格。
            SeamlessEdgeCells.PopulateSeamEdgeCells(___map, _seamCellsScratch);
            for (int i = 0; i < _seamCellsScratch.Count; i++)
            {
                if (__instance.CanReach(c, _seamCellsScratch[i], PathEndMode.OnCell, traverseParms))
                {
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false; // 跳过原版
        }

        private static readonly List<IntVec3> _seamCellsScratch = new();
    }
}
