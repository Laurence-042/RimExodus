using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨图分段视线（阶段5）：统一坐标 Bresenham + 逐格归属图分发阻挡判定。
    ///
    /// 走线骨架逐字段复刻 GenSight.LineOfSight（含半格偏移变体与拐角方向规则——原版拐角
    /// 射击语义），唯一替换是逐格 CanBeSeenOverFast 的查询图：
    /// 本图多边形内 → 本图查；邻图多边形内 → 邻图查（坐标平移）；角部双 void → 本图查
    /// （void 无 edifice，判定天然透明）。两端格原版语义本就不测试（起点格由 skipFirstCell 跳过）。
    /// </summary>
    public static class SeamlessCrossMapSight
    {
        private static readonly List<IntVec3> TempDestCells = new List<IntVec3>(16);
        private static readonly List<IntVec3> TempSources = new List<IntVec3>(16);

        /// <summary>
        /// 跨图射击线（接管 Verb.TryFindShootLineFromTo 的跨图分支）。
        /// 产出的 ShootLine 两端均为宿主（射手图）坐标：Source = 射手格（或 lean/炮塔占格），
        /// Dest = 目标统一格（邻图本地 + offset）。消费方：HitReportFor 的气体扫描（统一点）、
        /// TryCastShot 弹丸落点（Launch Postfix 统一平移）、瞄准/爆炸预览绘制
        /// （本图 void 区 = 邻图投射区，统一坐标视觉正确）。
        /// 门限：仅直射投射物 verb；近战/灵能/flyOverhead（迫击炮类）一律不可跨图；
        /// root（射手格）须在宿主方形内；目标统一格无方形限制（2026-08 放宽，逐格阻挡按
        /// 多边形归属路由，角部越界格透明——"看得见就打得着"）。
        /// </summary>
        public static bool TryFindShootLine(Verb verb, IntVec3 root, LocalTargetInfo targ,
            in SeamlessCombatCoords.CombatLink link, bool ignoreRange, out ShootLine line)
        {
            line = new ShootLine(root, targ.Cell + link.offset);

            if (!SeamlessDirectFireSupport.IsSupportedVerb(verb))
            {
                return false;
            }

            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var hostMap = caster?.Map;
            if (hostMap == null) return false;

            var targetThing = targ.Thing;
            var targetCellUnified = targ.Cell + link.offset;
            // 门限（2026-08 放宽，用户定夺"看得见就打得着"）：root 须在宿主方形内；目标统一格
            // 不再要求方形内——逐格阻挡由归属图路由（CellSeenOver 的多边形归属，越界格查归属图
            // 网格），仅角部双 void 越界格（无归属者）按透明处理。
            if (!root.InBounds(hostMap)) return false;

            var occupiedRectUnified = (targetThing != null
                ? targetThing.OccupiedRect()
                : CellRect.SingleCell(targ.Cell)).MovedBy(link.offset.ToIntVec2);

            if (!ignoreRange)
            {
                // 统一坐标射程（复刻 Verb.OutOfRange；跨图无邻射豁免——缝两侧隔着 void 带）。
                var minRange = verb.verbProps.EffectiveMinRange(allowAdjacentShot: false);
                var distSq = occupiedRectUnified.ClosestDistSquaredTo(root);
                var range = verb.EffectiveRange;
                if (distSq > range * range || distSq < minRange * minRange)
                {
                    return false;
                }
            }

            if (!verb.verbProps.requireLineOfSight)
            {
                line = new ShootLine(root, targetCellUnified);
                return true;
            }

            // 射手侧候选（全部宿主图格）：pawn = root + lean 格（原版同款）；非 pawn（炮塔）= 占格。
            TempSources.Clear();
            if (verb.CasterIsPawn)
            {
                TempSources.Add(root);
                ShootLeanUtility.LeanShootingSourcesFromTo(root, occupiedRectUnified.ClosestCellTo(root), hostMap, TempSources);
            }
            if (!verb.CasterIsPawn)
            {
                TempSources.AddRange(caster.OccupiedRect());
            }

            var includeCorners = targetThing != null && targetThing.def.Fillage == FillCategory.Full;
            for (var s = 0; s < TempSources.Count; s++)
            {
                var sourceCell = TempSources[s];
                if (!sourceCell.InBounds(hostMap)) continue;

                // 目标侧可射格：CalcShootableCellsOf 内部读 target.Map 原生算目标 lean 格（目标本地坐标）；
                // shooterPos 仅参与方向角——传目标坐标系下的射手格（统一格 − offset），方向与统一线一致。
                TempDestCells.Clear();
                if (targetThing != null)
                {
                    ShootLeanUtility.CalcShootableCellsOf(TempDestCells, targetThing, sourceCell - link.offset);
                }
                else
                {
                    TempDestCells.Add(targ.Cell);
                }

                for (var d = 0; d < TempDestCells.Count; d++)
                {
                    var destUnified = TempDestCells[d] + link.offset;
                    // 落点越宿主方形不再跳过——线段终点不参与 LOS 测试，越界终点由弹丸缝交接
                    //（Patches_Projectile）在跨缝时平移回目标图本地坐标系。
                    if (CanHitCellSegmented(verb, sourceCell, destUnified, includeCorners, in link, hostMap, targetThing))
                    {
                        line = new ShootLine(sourceCell, destUnified);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Verb.CanHitCellFromCellIgnoringRange 的跨图等价（LOS 判据逐条对齐原版）。</summary>
        private static bool CanHitCellSegmented(Verb verb, IntVec3 sourceUnified, IntVec3 destUnified,
            bool includeCorners, in SeamlessCombatCoords.CombatLink link, Map hostMap, Thing targetThing)
        {
            if (verb.verbProps.mustCastOnOpenGround)
            {
                var destLocal = destUnified - link.offset;
                if (!destLocal.Standable(link.target) || link.target.thingGrid.CellContains(destLocal, ThingCategory.Pawn))
                {
                    return false;
                }
            }
            if (!verb.verbProps.requireLineOfSight)
            {
                return true;
            }
            if (includeCorners)
            {
                return LineOfSightToEdgesSegmented(sourceUnified, destUnified, in link, hostMap, skipFirstCell: true);
            }
            return LineOfSightSegmented(sourceUnified, destUnified, in link, hostMap, skipFirstCell: true);
        }

        /// <summary>GenSight.LineOfSightToEdges 的跨图等价：基础走线 + 4 个半格偏移变体取或。</summary>
        public static bool LineOfSightToEdgesSegmented(IntVec3 start, IntVec3 end,
            in SeamlessCombatCoords.CombatLink link, Map hostMap, bool skipFirstCell = false)
        {
            if (LineOfSightSegmented(start, end, in link, hostMap, skipFirstCell))
            {
                return true;
            }
            var num = (start * 2).DistanceToSquared(end * 2);
            for (var i = 0; i < 4; i++)
            {
                if ((start * 2).DistanceToSquared(end * 2 + GenAdj.CardinalDirections[i]) <= num
                    && LineOfSightSegmented(start, end, in link, hostMap, skipFirstCell,
                        GenAdj.CardinalDirections[i].x, GenAdj.CardinalDirections[i].z))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// GenSight.LineOfSight（7 参重载，含半格偏移）的跨图移植：Bresenham 骨架与拐角方向
        /// 规则逐字段一致，逐格阻挡改为归属图分发。终点格不测试（原版语义：目标自身格不挡射击线）。
        /// validator 在统一格上按宿主图调用（气体校验的邻图段盲烟漏检为已知观察项）。
        /// </summary>
        public static bool LineOfSightSegmented(IntVec3 start, IntVec3 end,
            in SeamlessCombatCoords.CombatLink link, Map hostMap,
            bool skipFirstCell = false, int halfXOffset = 0, int halfZOffset = 0,
            Func<IntVec3, bool> validator = null)
        {
            var sideOnEqual = start.x != end.x ? start.x < end.x : start.z < end.z;
            var dx = Mathf.Abs(end.x - start.x);
            var dz = Mathf.Abs(end.z - start.z);
            var x = start.x;
            var z = start.z;
            var n = 1 + dx + dz;
            var xInc = end.x > start.x ? 1 : -1;
            var zInc = end.z > start.z ? 1 : -1;
            dx *= 4;
            dz *= 4;
            dx += halfXOffset * 2;
            dz += halfZOffset * 2;
            var err = dx / 2 - dz / 2;

            var cell = default(IntVec3);
            while (n > 1)
            {
                cell.x = x;
                cell.z = z;
                if (!skipFirstCell || cell != start)
                {
                    if (!CellSeenOver(cell, in link, hostMap))
                    {
                        return false;
                    }
                    if (validator != null && !validator(cell))
                    {
                        return false;
                    }
                }
                if (err > 0 || (err == 0 && sideOnEqual))
                {
                    x += xInc;
                    err -= dz;
                }
                else
                {
                    z += zInc;
                    err += dx;
                }
                n--;
            }
            return true;
        }

        /// <summary>
        /// 原版 AttackTargetFinder.CanSee 的跨图等价：目标可射格（目标图原生 lean 格）×
        /// [seer 直射 + seer 侧 lean 格（宿主图）] 组合分段 LOS。语义逐条对齐原版实现。
        /// validator 域包裹（2026-08，随 HitReportFor teleport 正修一并落地）：原版 validator 是
        /// 闭包 `(c) => !c.AnyGas(searcher.Map, …)` 硬编码搜索者图——统一格越宿主方形时数值越界；
        /// 我们分段 LOS 的域定义 = validator 只对宿主图界内格生效（邻图段盲烟维持已记录的简化）。
        /// </summary>
        public static bool CanSeeCrossMap(Thing seer, Thing target,
            in SeamlessCombatCoords.CombatLink link, Func<IntVec3, bool> validator)
        {
            var hostMap = link.host;
            Func<IntVec3, bool> boundedValidator = null;
            if (validator != null)
            {
                boundedValidator = c => c.InBounds(hostMap) && validator(c);
            }
            var seerOnTargetMap = seer.Position - link.offset; // CalcShootableCellsOf 的方向参数需目标坐标系

            TempDestCells.Clear();
            ShootLeanUtility.CalcShootableCellsOf(TempDestCells, target, seerOnTargetMap);
            for (var i = 0; i < TempDestCells.Count; i++)
            {
                var destUnified = TempDestCells[i] + link.offset;
                if (LineOfSightSegmented(seer.Position, destUnified, in link, hostMap, skipFirstCell: true, validator: boundedValidator))
                {
                    return true;
                }
            }

            TempSources.Clear();
            ShootLeanUtility.LeanShootingSourcesFromTo(seer.Position, target.Position + link.offset, hostMap, TempSources);
            for (var s = 0; s < TempSources.Count; s++)
            {
                var source = TempSources[s];
                if (!source.CanBeSeenOver(hostMap)) continue;
                for (var i = 0; i < TempDestCells.Count; i++)
                {
                    var destUnified = TempDestCells[i] + link.offset;
                    if (LineOfSightSegmented(source, destUnified, in link, hostMap, skipFirstCell: true, validator: boundedValidator))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 统一格的阻挡判定：归邻图（多边形归属，纯几何不依赖宿主方形边界——越界格路由到
        /// 归属图网格查）→ 邻图本地格查；归本图或角部双 void 越界格（无归属者且越本图方形）→
        /// 本图查 / 透明（void 无物可挡，2026-08 门限放宽用户定夺）。
        /// </summary>
        private static bool CellSeenOver(IntVec3 unifiedCell, in SeamlessCombatCoords.CombatLink link, Map hostMap)
        {
            if (SeamlessTileRegistry.TryGetOwnerNeighbor(hostMap, unifiedCell, out var ownerMap, out var ownerLocal))
            {
                return ownerLocal.CanBeSeenOverFast(ownerMap);
            }
            return unifiedCell.InBounds(hostMap) && unifiedCell.CanBeSeenOverFast(hostMap);
        }
    }
}
