using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图选点用的单源通行代价场（Dijkstra，即 A* 的 h≡0 形态）。
    /// 单跳总代价在过缝点处可分解：total(spot) = C1(pawn→spot) + C2(落点→dest)，
    /// 两侧各跑一次本泛洪、对候选 spot 逐个求和取最小，即两图在 spot↔落点缝零代价边后的
    /// 全局最优过缝点。
    ///
    /// 成本口径对齐移动主体自己的基础路径网格，而不是固定按普通人类计算：
    /// - 普通 Pawn = map.pathing.For(pawn)（Normal/FenceBlocked/Flying）+ WaterCellCost 覆盖；
    /// - VF 载具 = 该 VehicleDef 的 VehiclePathGrid（经软兼容层读取，核心不引用 VF 类型）；
    /// - 进入格成本 ≥ ImpassableCost(10000) 时不可入；
    /// - 步进 = pawn.TicksPerMoveCardinal/Diagonal（pawn 为 null → 13/18，同 PathFinder 默认常量）；
    /// - 对角切角规则 = 两侧 cardinal 均须可入（原版另查 building/fence 位图，此处近似为成本判定）。
    /// 刻意不复刻逐请求层：关门砸门成本、挡路 pawn +175（Cost_Blocked）、locomotion urgency、
    /// avoid/allowed 网格——只影响"选哪个过缝传送点"的近似精度；
    /// 段内执行仍由原版 A* 完成，合法性不受影响。
    /// </summary>
    public static class SeamlessPathCostField
    {
        /// <summary>
        /// 从 start 泛洪，返回与 targets 对齐的通行成本数组（不可达 = int.MaxValue）。
        /// 目标格全部 settle 或前沿耗尽即提前结束；越界的 target 保持不可达。
        /// </summary>
        public static int[] FloodCosts(Map map, IntVec3 start, List<IntVec3> targets, Pawn pawn)
        {
            var results = new int[targets.Count];
            var indices = map.cellIndices;
            int sizeX = map.Size.x;
            int sizeZ = map.Size.z;
            int moveTicksCardinal = pawn != null ? Mathf.RoundToInt(pawn.TicksPerMoveCardinal) : PathFinder.DefaultMoveTicksCardinal;
            int moveTicksDiagonal = pawn != null ? Mathf.RoundToInt(pawn.TicksPerMoveDiagonal) : PathFinder.DefaultMoveTicksDiagonal;

            // dist 是"已见最优成本"；堆中惰性删除：弹出时与 dist 不符的过期项跳过。
            var dist = new int[indices.NumGridCells];
            for (int i = 0; i < dist.Length; i++)
            {
                dist[i] = int.MaxValue;
            }

            var remainingTargets = new HashSet<int>();
            for (int i = 0; i < targets.Count; i++)
            {
                results[i] = int.MaxValue;
                if (targets[i].InBounds(map))
                {
                    remainingTargets.Add(indices.CellToIndex(targets[i]));
                }
            }

            if (remainingTargets.Count == 0 || !start.InBounds(map))
            {
                return results;
            }

            // 主体差异只在成本源构造处派发；Dijkstra 热循环不认识飞行、游泳或 VF 类型。
            // VF 网格若尚未生成，这里以 Urgent 同步就绪化后再读取，避免默认零值假阳性。
            var costSource = CreateCostSource(map, pawn);
            if (costSource == null) return results;

            var heap = new List<(int cost, int index)>(1024);
            dist[indices.CellToIndex(start)] = 0;
            Push(heap, 0, indices.CellToIndex(start));

            int remaining = remainingTargets.Count;
            while (heap.Count > 0 && remaining > 0)
            {
                var entry = Pop(heap);
                if (entry.cost != dist[entry.index])
                {
                    continue;
                }
                if (remainingTargets.Remove(entry.index))
                {
                    remaining--;
                }

                IntVec3 cell = indices.IndexToCell(entry.index);
                foreach (var offset in GenAdj.AdjacentCells)
                {
                    int nx = cell.x + offset.x;
                    int nz = cell.z + offset.z;
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ)
                    {
                        continue;
                    }
                    int nIdx = indices.CellToIndex(nx, nz);
                    var next = new IntVec3(nx, 0, nz);
                    int enterCost = costSource.CostToEnter(cell, next);
                    if (enterCost >= PathGrid.ImpassableCost)
                    {
                        continue;
                    }
                    bool diagonal = offset.x != 0 && offset.z != 0;
                    if (diagonal
                        && (costSource.CostToEnter(cell, new IntVec3(nx, 0, cell.z)) >= PathGrid.ImpassableCost
                            || costSource.CostToEnter(cell, new IntVec3(cell.x, 0, nz)) >= PathGrid.ImpassableCost))
                    {
                        continue;
                    }
                    int newDist = entry.cost + enterCost + (diagonal ? moveTicksDiagonal : moveTicksCardinal);
                    if (newDist < dist[nIdx])
                    {
                        dist[nIdx] = newDist;
                        Push(heap, newDist, nIdx);
                    }
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].InBounds(map))
                {
                    results[i] = dist[indices.CellToIndex(targets[i])];
                }
            }
            return results;
        }

        /// <summary>
        /// 单格准入与代价场共用同一成本源。传送落点守卫必须走这里，不能退回 Terrain/Thing 的
        /// 普通 Pawn Walkable：后者不认识 Flying，也会在车辆专用整车落点解析前误杀水上载具。
        /// VF 载具的最终准入仍由 TryResolveVehicleArrivalCell 做整车矩形判定，本方法供非载具使用。
        /// </summary>
        internal static bool CanEnterCell(Map map, IntVec3 cell, Pawn pawn)
        {
            if (map == null || !cell.InBounds(map)) return false;
            var costSource = CreateCostSource(map, pawn);
            return costSource != null && costSource.CostToEnter(cell, cell) < PathGrid.ImpassableCost;
        }

        /// <summary>
        /// 格成本提供器是第三方移动系统的维护边界：新增特殊移动主体只需在这里增加一种成本源，
        /// 泛洪、候选排序与桥接执行均无需知道其类型。
        /// </summary>
        private static ICellCostSource CreateCostSource(Map map, Pawn pawn)
        {
            if (pawn != null && SeamlessVehiclesCompat.IsVehicle(pawn))
            {
                if (!SeamlessVehiclesCompat.EnsureGridsReady(pawn, map)) return null;
                return new VehicleCellCostSource(map, pawn);
            }
            return new PawnCellCostSource(map, pawn);
        }

        private interface ICellCostSource
        {
            int CostToEnter(IntVec3 from, IntVec3 to);
        }

        /// <summary>
        /// 原版 Pawn 成本源。Pathing.For(pawn) 会按实时主体状态选 Normal/FenceBlocked/Flying；
        /// 水面另应用 Pawn.WaterCellCost（飞行、游泳、种族与基因覆盖的共同原版入口）。
        /// </summary>
        private sealed class PawnCellCostSource : ICellCostSource
        {
            private readonly Map map;
            private readonly Pawn pawn;
            private readonly PathGrid pathGrid;
            private readonly int? waterCellCost;

            internal PawnCellCostSource(Map map, Pawn pawn)
            {
                this.map = map;
                this.pawn = pawn;
                pathGrid = pawn == null ? map.pathing.Normal.pathGrid : map.pathing.For(pawn).pathGrid;
                waterCellCost = pawn?.WaterCellCost;
            }

            public int CostToEnter(IntVec3 from, IntVec3 to)
            {
                if (pawn == null) return pathGrid.Cost(to);
                var terrain = map.terrainGrid.TerrainAt(to);
                var waterCost = terrain != null && terrain.IsWater ? waterCellCost : null;
                return waterCost.HasValue
                    ? pathGrid.CalculatedCostAt(to, perceivedStatic: true, from, waterCost)
                    : pathGrid.Cost(to);
            }
        }

        /// <summary>
        /// VF 成本源。每格最多反射读取一次并缓存在本次泛洪快照中；签名漂移时兼容层退化为
        /// VehiclePathGrid.Walkable 的 0/Impassable 二值成本，仍保证可达性不回退到普通 Pawn 口径。
        /// </summary>
        private sealed class VehicleCellCostSource : ICellCostSource
        {
            private readonly Map map;
            private readonly Pawn vehicle;
            private readonly int[] cachedCosts;

            internal VehicleCellCostSource(Map map, Pawn vehicle)
            {
                this.map = map;
                this.vehicle = vehicle;
                cachedCosts = new int[map.cellIndices.NumGridCells];
                for (var i = 0; i < cachedCosts.Length; i++) cachedCosts[i] = -1;
            }

            public int CostToEnter(IntVec3 from, IntVec3 to)
            {
                var index = map.cellIndices.CellToIndex(to);
                var cached = cachedCosts[index];
                if (cached >= 0) return cached;
                if (!SeamlessVehiclesCompat.TryGetVehiclePathCost(vehicle, map, to, out cached))
                {
                    cached = PathGrid.ImpassableCost;
                }
                cachedCosts[index] = cached;
                return cached;
            }
        }

        // net48 无 PriorityQueue，自实现二叉最小堆（按 cost 排序，容量按需增长）。

        private static void Push(List<(int cost, int index)> heap, int cost, int index)
        {
            heap.Add((cost, index));
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heap[parent].cost <= heap[i].cost)
                {
                    break;
                }
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        private static (int cost, int index) Pop(List<(int cost, int index)> heap)
        {
            var top = heap[0];
            var last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0)
            {
                heap[0] = last;
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1;
                    int right = left + 1;
                    int smallest = i;
                    if (left < heap.Count && heap[left].cost < heap[smallest].cost)
                    {
                        smallest = left;
                    }
                    if (right < heap.Count && heap[right].cost < heap[smallest].cost)
                    {
                        smallest = right;
                    }
                    if (smallest == i)
                    {
                        break;
                    }
                    (heap[smallest], heap[i]) = (heap[i], heap[smallest]);
                    i = smallest;
                }
            }
            return top;
        }
    }
}
