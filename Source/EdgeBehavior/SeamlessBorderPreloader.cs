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

            // 接缝带格 = 撤离意图（2026-08 从传送点格扩到整个 Band）：exit grid 标记集 = Band 三圈 ∪
            // 传送点格（Patches_ExitMapGrid），玩家征召 goto 到带内任意格 → 到站原生离场组队，
            // 永远不会踏进对端地图——排除集必须与 exit 标记集同口径，否则 goto 带内圈非传送点格
            // 会白预加载一张永远不会被进入的对侧图（BuildSeamBand 进程缓存，Contains O(1)）。
            if (SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x)?.Band.Contains(targetCell) == true
                || SeamlessEdgeCells.IsSeamEdgeCell(map, targetCell))
            {
                Log.Message($"[RimExodus] CheckPawnGoto: pawn {pawn.LabelShort} target {targetCell} is in the seam band (exit intent), skip preload.");
                return;
            }

            // 已加载则跳过。
            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, worldTile, out _))
            {
                Log.Message($"[RimExodus] CheckPawnGoto: neighbor worldTile {worldTile} already loaded, skip.");
                return;
            }

            // 邻居图存在但休眠（link 查询被休眠口径过滤）：同步唤醒（轻量——恢复 tick 注册 +
            // 邻接显示，无生成），玩家继续走近的过程即邻接恢复的过程。StartJob 调用栈内安全
            // （RegisterAllTickabilityFor 与原版 tick 中途 spawn 同源安全）。
            if (SeamlessDormancyManager.TryWakeByWorldTile(worldTile, $"border preload band (pawn {pawn.LabelShort} goto {targetCell})"))
            {
                Log.Message($"[RimExodus] CheckPawnGoto: neighbor worldTile {worldTile} was dormant, woke it up.");
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
