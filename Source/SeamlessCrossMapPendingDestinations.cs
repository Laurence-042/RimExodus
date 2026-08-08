using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 记录"Pawn 完成一次跨地图桥接转移后，应该继续走到哪个 Map 的哪个格子"。
    /// 由 <see cref="SeamlessMapTransferTrigger"/> 在转移成功后消费，实现移动指令的续程。
    /// </summary>
    public static class SeamlessCrossMapPendingDestinations
    {
        private readonly struct PendingDestination
        {
            public readonly Map Map;
            public readonly IntVec3 Cell;

            public PendingDestination(Map map, IntVec3 cell)
            {
                Map = map;
                Cell = cell;
            }
        }

        private static readonly Dictionary<Pawn, PendingDestination> pending = new Dictionary<Pawn, PendingDestination>();

        public static void Record(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn == null || map == null)
            {
                return;
            }

            pending[pawn] = new PendingDestination(map, cell);
        }

        /// <summary>取出并移除等待续程的目的地；只有 arrivedMap 匹配时才返回 true。</summary>
        public static bool TryConsume(Pawn pawn, Map arrivedMap, out IntVec3 cell)
        {
            cell = default;
            if (pawn == null || !pending.TryGetValue(pawn, out var destination))
            {
                return false;
            }

            pending.Remove(pawn);
            if (destination.Map != arrivedMap)
            {
                return false;
            }

            cell = destination.Cell;
            return true;
        }
    }
}
