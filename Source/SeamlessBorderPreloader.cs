using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 边界预加载检测器（阶段4a：事件驱动）。
    /// 由 <see cref="Patch_Pawn_JobTracker_StartJob"/> 在玩家强制 Goto 指令时调用，
    /// 检查目标格是否在边界带内，若是且对应邻居未加载则触发预加载。
    ///
    /// 严格只响应 playerForced 的 Goto（玩家右键移动指令），避免动物/自动寻路接近边界时
    /// 触发级联加载（A 地图动物触发 B 加载，B 的动物又触发 C 加载……）。
    /// </summary>
    public static class SeamlessBorderPreloader
    {
        /// <summary>
        /// 检查玩家强制 Goto 指令的目标格是否接近边界，若是则预加载对应邻居。
        /// 在 <see cref="Pawn_JobTracker.StartJob"/> Prefix 中调用。
        /// </summary>
        public static void CheckPawnGoto(Pawn pawn, IntVec3 targetCell)
        {
            if (pawn == null) return;
            var map = pawn.Map;
            if (map == null) return;

            var lookup = map.GetComponent<SeamlessBorderLookup>();
            if (lookup == null)
            {
                Log.Warning($"[RimExodus] CheckPawnGoto: no SeamlessBorderLookup on map {map.uniqueID}.");
                return;
            }

            if (!lookup.TryGetPreloadTarget(targetCell, out int worldTile))
            {
                // 诊断：目标格不在边界带内。
                Log.Message($"[RimExodus] CheckPawnGoto: pawn {pawn.LabelShort} target {targetCell} not in border band, skip.");
                return;
            }
            if (worldTile < 0) return;

            // 传送点格 = 撤离意图（玩家征召 goto 踩传送点 → 原生撤离/组队，行为表行 1/8）：
            // 不需要对端地图，不触发预加载。边界带带宽（15）包含传送点带（2），必须显式排除。
            if (SeamlessEdgeCells.IsSeamEdgeCell(map, targetCell))
            {
                Log.Message($"[RimExodus] CheckPawnGoto: pawn {pawn.LabelShort} target {targetCell} is a seamless enter spot (exit intent), skip preload.");
                return;
            }

            // 已加载则跳过。
            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, worldTile, out _))
            {
                Log.Message($"[RimExodus] CheckPawnGoto: neighbor worldTile {worldTile} already loaded, skip.");
                return;
            }

            Log.Message($"[RimExodus] CheckPawnGoto: pawn {pawn.LabelShort} target {targetCell} in border band, " +
                $"queuing preload for neighbor worldTile {worldTile}.");
            // 不在 StartJob 调用栈内同步生成（会阻塞当前 tick 数百毫秒），登记到延迟队列，
            // 由 SeamlessTileManager.MapComponentTick 在下一 tick 消费。
            SeamlessTilePreloader.QueuePreload(map, worldTile);
        }
    }
}
