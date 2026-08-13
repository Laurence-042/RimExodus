using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 核心 patch：把原版所有"在地图边缘找格"的消费者（袭击入口、远行队出口、撤退 spot、行人穿越等）
    /// 统一改用六边形接缝带（传送点格集合），而不是原版方形地图边缘（六边形裁切后多为 void）。
    ///
    /// 设计：所有边缘消费者最终都走两个根原语——
    /// 1. <see cref="CellFinder.TryFindRandomEdgeCellWith(Predicate{IntVec3}, Map, float, out IntVec3)"/>
    ///    （随机重载，袭击/远行队/撤退/行人穿越都用它）
    /// 2. <see cref="CellFinder.RandomEdgeCell(Map)"/>（无校验随机边缘格，少数直调）
    /// 在这两个根原语上 patch，**一处覆盖全部消费者**（远行队/袭击/商队/访客/行人穿越共用同一改动），
    /// 而非逐个上层方法（<c>TryFindRandomPawnEntryCell</c>/<c>TryFindRandomPawnExitCell</c>/...）patch。
    ///
    /// 上层 <see cref="RCellFinder.TryFindBestExitSpot"/>/<c>TryFindRandomExitSpot</c> 的主体自己直接构造
    /// 方形边缘格（不调本方法），由 <see cref="Patches_RCellFinder"/> 单独 Prefix 处理。
    /// </summary>
    // 重载消歧：CellFinder.TryFindRandomEdgeCellWith 有两个重载（4 参数 / 5 参数带 Rot4 dir）。
    // 由于 out 参数需 MakeByRefType()（非编译期常量），无法用 [HarmonyPatch] 特性声明参数类型，
    // 因此本类的 patch 在 RimExodusMod 构造器里用 AccessTools.Method 显式绑定（见 RimExodusMod.cs）。
    // 这里不带 [HarmonyPatch] 特性，避免 PatchAll 误绑。
    static class Patch_CellFinder_TryFindRandomEdgeCellWith
    {
        /// <summary>
        /// 对有 RimExodus 传送点的地图：把候选池从"方形边缘格"换成"六边形接缝带格（传送点）"。
        /// 打乱后逐个过原版 <paramref name="validator"/>，命中即返回。全部不满足返回 false。
        /// 非 RimExodus 地图放行原版。
        /// </summary>
        internal static bool Prefix(Predicate<IntVec3> validator, Map map, ref IntVec3 result, ref bool __result)
        {
            if (map == null) return true;
            if (!SeamlessEdgeCells.HasSeamEdge(map)) return true; // 非 RimExodus 地块放行原版

            var cells = SeamlessEdgeCells.GetSeamEdgeCells(map);
            if (cells == null || cells.Count == 0)
            {
                result = IntVec3.Invalid;
                __result = false;
                return false;
            }

            // 打乱候选顺序（原版也是随机尝试），然后逐个过 validator。
            // 用 Fisher-Yates 打乱 scratch 副本（不污染缓存列表）。
            _scratch.Clear();
            for (int i = 0; i < cells.Count; i++) _scratch.Add(cells[i]);
            for (int i = _scratch.Count - 1; i > 0; i--)
            {
                int j = Rand.Range(0, i + 1);
                (_scratch[i], _scratch[j]) = (_scratch[j], _scratch[i]);
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                var cell = _scratch[i];
                try
                {
                    if (validator(cell))
                    {
                        result = cell;
                        __result = true;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // 对齐原版 TryFindRandomEdgeCellWith 的异常处理（记 Error 不中断）。
                    Log.Error($"[RimExodus] Patch_CellFinder_TryFindRandomEdgeCellWith exception validating seam cell {cell}: {ex}");
                }
            }

            result = IntVec3.Invalid;
            __result = false;
            return false; // 跳过原版（已用接缝带替换候选池）
        }

        private static readonly List<IntVec3> _scratch = new();
    }

    /// <summary>
    /// 兜底：少数代码路径直接调 <see cref="CellFinder.RandomEdgeCell(Map)"/>（无校验随机边缘格），
    /// 不走 TryFindRandomEdgeCellWith。对 RimExodus 地块改返回随机接缝格。
    /// </summary>
    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomEdgeCell), new[] { typeof(Map) })]
    static class Patch_CellFinder_RandomEdgeCell
    {
        static void Postfix(Map map, ref IntVec3 __result)
        {
            if (map == null) return;
            if (!SeamlessEdgeCells.HasSeamEdge(map)) return;
            var seam = SeamlessEdgeCells.RandomSeamEdgeCell(map);
            if (seam.IsValid) __result = seam;
        }
    }
}
