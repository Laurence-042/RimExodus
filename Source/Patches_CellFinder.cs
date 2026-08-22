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
    /// 设计：所有边缘消费者最终都走三个根原语——
    /// 1. <see cref="CellFinder.TryFindRandomEdgeCellWith(Predicate{IntVec3}, Map, float, out IntVec3)"/>
    ///    （随机重载，袭击/撤退/行人穿越等）
    /// 2. <see cref="CellFinder.TryFindRandomEdgeCellWith(Predicate{IntVec3}, Map, Rot4, float, out IntVec3)"/>
    ///    （Rot4 方向重载，远行队组队出口：AvailableExitTilesAt 路线规划 + Dialog_FormCaravan.TryFindExitSpot）
    /// 3. <see cref="CellFinder.RandomEdgeCell(Map)"/>（无校验随机边缘格，少数直调）
    /// 在这两个根原语上 patch，**一处覆盖全部消费者**（远行队/袭击/商队/访客/行人穿越共用同一改动），
    /// 而非逐个上层方法（<c>TryFindRandomPawnEntryCell</c>/<c>TryFindRandomPawnExitCell</c>/...）patch。
    ///
    /// 上层 <see cref="RCellFinder.TryFindBestExitSpot"/>/<c>TryFindRandomExitSpot</c> 的主体自己直接构造
    /// 方形边缘格（不调本方法），由 <see cref="Patches_RCellFinder"/> 单独 Prefix 处理。
    /// </summary>
    // 重载消歧：CellFinder.TryFindRandomEdgeCellWith 有两个重载（4 参数 / 5 参数带 Rot4 dir）。
    // 由于 out 参数需 MakeByRefType()（非编译期常量），无法用 [HarmonyPatch] 特性声明参数类型，
    // 因此两个重载的 patch 都在 RimExodusMod 构造器里用 AccessTools.Method 显式绑定（见 RimExodusMod.cs）。
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
            // 诊断插桩（2026-08 远行队"无法离开此区域"排查，verbose）：接缝候选池全灭时记录
            // fogged/unwalkable 分布——validator 是调用方任意谓词，这两项只是提示不是精确归因。
            if (RimExodusMod.Settings?.verboseLogging ?? false)
            {
                var fogged = 0;
                var unwalkable = 0;
                for (int i = 0; i < _scratch.Count; i++)
                {
                    if (_scratch[i].Fogged(map)) fogged++;
                    if (!_scratch[i].Walkable(map)) unwalkable++;
                }
                Log.Message($"[RimExodus] TryFindRandomEdgeCellWith(4-arg) seam pool exhausted on map {map.uniqueID}: " +
                            $"{_scratch.Count} cells ({fogged} fogged, {unwalkable} unwalkable) -> false.");
            }
            return false; // 跳过原版（已用接缝带替换候选池）
        }

        private static readonly List<IntVec3> _scratch = new();
    }

    /// <summary>
    /// 5 参（Rot4 方向）重载的接缝化。消费者：组队路线规划
    /// <c>CaravanExitMapUtility.AvailableExitTilesAt</c>（每个相邻世界 tile 按 45° 扇区算出
    /// primary/secondary 方向各调一次）与 <c>Dialog_FormCaravan.TryFindExitSpot</c>（严格档）。
    ///
    /// 原版候选格硬编码在方形边（RandomEdgeCell(dir) + 穷举 CellRect.GetEdgeCells(dir)），六边形
    /// 裁切后方形边整圈 void；仅顶点恰好轴对齐（顶点朝向来自世界球面，θ≡0 mod 30°）的未倾斜图
    /// 靠角点接触格侥幸通过原版。2026-08 倾斜图实测"你的远行队无法离开此区域"即此链
    /// （0 tiles → startingTile 无效 → CheckForErrors 弹 MessageNoValidExitTile）——
    /// 勿再以未倾斜图实测回退本 patch（首轮落地即被此误判回退过）。
    ///
    /// 候选池 = 该方向半平面的接缝格（中心线含等号：对角边格被两方向共享，近似原版
    /// "方形整条边"宽覆盖语义）；validator 原样调用（含调用方的 !Fogged——未侦察方向出口
    /// 不可用，用户定夺 2026-08）。
    /// </summary>
    static class Patch_CellFinder_TryFindRandomEdgeCellWithRot4
    {
        internal static bool Prefix(Predicate<IntVec3> validator, Map map, Rot4 dir, ref IntVec3 result, ref bool __result)
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

            // 方向半平面过滤（RimWorld 朝向：North=+z 高位、East=+x 高位，对齐 RandomEdgeCell(dir)）。
            // dir 无效（理论上调用方都传有效方向）→ 不过滤，退化为 4 参语义。
            _scratchRot4.Clear();
            var halfX = map.Size.x / 2;
            var halfZ = map.Size.z / 2;
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (dir.IsValid)
                {
                    if (dir == Rot4.North && c.z < halfZ) continue;
                    if (dir == Rot4.South && c.z > halfZ) continue;
                    if (dir == Rot4.East && c.x < halfX) continue;
                    if (dir == Rot4.West && c.x > halfX) continue;
                }
                _scratchRot4.Add(c);
            }

            // 打乱候选顺序（原版也是随机尝试）。scratch 与 4 参重载分开，防 validator 触发
            // 嵌套调用（4 参链）时互相清空对方的候选副本。
            for (int i = _scratchRot4.Count - 1; i > 0; i--)
            {
                int j = Rand.Range(0, i + 1);
                (_scratchRot4[i], _scratchRot4[j]) = (_scratchRot4[j], _scratchRot4[i]);
            }

            for (int i = 0; i < _scratchRot4.Count; i++)
            {
                var cell = _scratchRot4[i];
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
                    Log.Error($"[RimExodus] Patch_CellFinder_TryFindRandomEdgeCellWithRot4 exception validating seam cell {cell}: {ex}");
                }
            }

            result = IntVec3.Invalid;
            __result = false;
            // 诊断插桩（verbose）：该方向半平面候选全灭时记录 fogged/unwalkable 分布——
            // validator 是调用方任意谓词，这两项只是提示不是精确归因。
            if (RimExodusMod.Settings?.verboseLogging ?? false)
            {
                var fogged = 0;
                var unwalkable = 0;
                for (int i = 0; i < _scratchRot4.Count; i++)
                {
                    if (_scratchRot4[i].Fogged(map)) fogged++;
                    if (!_scratchRot4[i].Walkable(map)) unwalkable++;
                }
                Log.Message($"[RimExodus] TryFindRandomEdgeCellWith(5-arg dir={dir.AsInt}) seam pool exhausted on map {map.uniqueID}: " +
                            $"{_scratchRot4.Count}/{cells.Count} cells ({fogged} fogged, {unwalkable} unwalkable) -> false.");
            }
            return false; // 跳过原版（已用接缝带替换候选池；方形边在裁切图上结构性无望）
        }

        private static readonly List<IntVec3> _scratchRot4 = new();
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
