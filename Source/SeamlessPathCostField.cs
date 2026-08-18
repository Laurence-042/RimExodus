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
    /// 成本口径对齐原版 1.6 A* 的基础层（references/RimWorldDecompiled/Verse/PathFinderJob.cs:140-155）：
    /// - 进入格成本 = PathGrid 缓存感知成本（map.pathing.Normal.pathGrid.Cost；≥ ImpassableCost(10000) 不可入）；
    /// - 步进 = pawn.TicksPerMoveCardinal/Diagonal（pawn 为 null → 13/18，同 PathFinder 默认常量）；
    /// - 对角切角规则 = 两侧 cardinal 均须可入（原版另查 building/fence 位图，此处近似为成本判定）。
    /// 刻意不复刻逐请求层：关门砸门成本、挡路 pawn +175（Cost_Blocked）、water tuning、
    /// fence 独立 PathGrid、avoid/allowed 网格——只影响"选哪个过缝传送点"的近似精度；
    /// 段内执行仍由原版 A* 完成，合法性不受影响。
    /// </summary>
    public static class SeamlessPathCostField
    {
        // 前 4 个 cardinal、后 4 个对角（Offsets[i,0]=dx、Offsets[i,1]=dz）。
        private static readonly int[,] Offsets =
        {
            { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 },
            { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 },
        };

        /// <summary>
        /// 从 start 泛洪，返回与 targets 对齐的通行成本数组（不可达 = int.MaxValue）。
        /// 目标格全部 settle 或前沿耗尽即提前结束；越界的 target 保持不可达。
        /// </summary>
        public static int[] FloodCosts(Map map, IntVec3 start, List<IntVec3> targets, Pawn pawn)
        {
            var results = new int[targets.Count];
            var indices = map.cellIndices;
            var pathGrid = map.pathing.Normal.pathGrid;
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
                for (int dir = 0; dir < 8; dir++)
                {
                    int nx = cell.x + Offsets[dir, 0];
                    int nz = cell.z + Offsets[dir, 1];
                    if (nx < 0 || nx >= sizeX || nz < 0 || nz >= sizeZ)
                    {
                        continue;
                    }
                    int nIdx = indices.CellToIndex(nx, nz);
                    int enterCost = pathGrid.Cost(new IntVec3(nx, 0, nz));
                    if (enterCost >= PathGrid.ImpassableCost)
                    {
                        continue;
                    }
                    bool diagonal = dir >= 4;
                    if (diagonal
                        && (pathGrid.Cost(new IntVec3(nx, 0, cell.z)) >= PathGrid.ImpassableCost
                            || pathGrid.Cost(new IntVec3(cell.x, 0, nz)) >= PathGrid.ImpassableCost))
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
