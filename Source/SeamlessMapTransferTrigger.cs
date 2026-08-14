using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 当前地图上的无缝入口触发器。
    /// 每张地图只扫描自己的入口；入口通过 CompSeamlessTileEnterSpot.cachedArrivalCell + targetWorldTile
    /// 解析对端（阶段4b 传送机制重构：废弃互绑，改用 offset 算对端坐标并缓存到 spot）。
    /// </summary>
    public class SeamlessMapTransferTrigger : MapComponent
    {
        // 缓存传送点 Def，避免每 tick 调 DefDatabase.GetNamedSilentFail（PurgeInvalidArrivalLocks 用）。
        private ThingDef cachedEnterSpotDef;

        /// <summary>
        /// 处于"跨图到达锁"状态的 Pawn 集合。
        /// Pawn 被传送进本图后进入锁状态：只要它还站在本图任一接缝传送点上，就保持锁，
        /// 防止续程寻路沿接缝前进时踩到相邻接缝传送点被立刻传回（反弹 bug）。
        /// Pawn 离开整条接缝带（不再站在任何本图传送点上）才解除锁。
        /// </summary>
        private HashSet<Pawn> arrivalLocks = new HashSet<Pawn>();

        // PurgeInvalidArrivalLocks 复用的接缝位置集合（实例字段，避免多地图 static 共享污染）。
        private readonly HashSet<IntVec3> seamPositions = new HashSet<IntVec3>();

        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref arrivalLocks, "arrivalLocks", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                arrivalLocks ??= new HashSet<Pawn>();
                PurgeInvalidArrivalLocks();
            }
        }

        public override void MapComponentTick()
        {
            // 每帧刷新锁：Pawn 一离开接缝带（不再站在任何传送点上）就解除锁。
            // arrivalLocks 为空时快速返回（O(1)）。
            PurgeInvalidArrivalLocks();

            // 选中保持集的失效清理（pawn 死亡/销毁/未跨图残留）。仅在锚点地图跑，避免每张地图重复。
            // 集合通常为空（跨图完成即消费），清理开销可忽略。
            // 用 IsAnchorMap 判断家园（基础地图无 IsPocketMap 语义）。
            if (SeamlessTileGraph.IsAnchorMap(map))
            {
                SeamlessSelectionTracker.PurgeInvalid();
            }
        }

        /// <summary>在目标地图上登记 Pawn 的跨图到达锁状态（直到 Pawn 离开接缝带）。</summary>
        internal void RecordArrival(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map != map)
            {
                Log.Error("[RimExodus] Cannot record an invalid seamless-map arrival lock.");
                return;
            }

            arrivalLocks.Add(pawn);
        }

        /// <summary>
        /// 事件驱动的传送检测：由 <see cref="Patch_Pawn_PathFollower_TryEnterNextPathCell"/> 在 pawn 跨格时调用。
        /// 检查 pawn 当前位置是否有已绑定的传送点，若有则触发跨地图转移。
        /// </summary>
        internal static void TryTriggerTransfer(Pawn pawn, IntVec3 cell, Map map)
        {
            if (pawn == null || map == null) return;
            if (pawn.Downed || pawn.Dead) return;

            // 阶段4前置：行为区分——远行队组建流程 vs 征召跨图。
            // 若 pawn 当前 Job 的 exitMapOnArrival==true（远行队组建，原生 JobDriver_Goto 设），
            // 则不做直接跨图传送，放行原生 ExitMap 流程（传送点被 ExitMapGrid patch 标为出口格，
            // pawn 踩传送点 → 原生 ExitMap → 大地图远行队）。
            // 否则（征召 playerForced Goto 或其他）走现有直接跨图传送逻辑。
            var job = pawn.CurJob;
            if (job != null && job.exitMapOnArrival)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] TryTriggerTransfer: pawn {pawn.LabelShort} at {cell} has exitMapOnArrival job, deferring to native ExitMap.");
                return;
            }

            // 用 thingGrid 直接查该格上的传送点（O(1)）。
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;

                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null || !comp.hasArrival) continue;

                // 检查 pawn 是否被到达锁锁住（防回弹）。
                var trigger = map.GetComponent<SeamlessMapTransferTrigger>();
                if (trigger != null && trigger.IsArrivalLocked(pawn)) return;

                // 读缓存的对端坐标（O(1)），按 targetWorldTile 解析对端 Map。
                if (!SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out var arrivalMap))
                {
                    // 对端邻居已卸载：跳过（不应发生，传送点缓存已失效）。
                    return;
                }

                Log.Message($"[RimExodus] Seamless trigger: pawn {pawn.LabelShort} at {cell} "
                    + $"on map {map.uniqueID} targeting {comp.cachedArrivalCell} on map {arrivalMap.uniqueID}");
                if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, arrivalMap, comp.cachedArrivalCell, out _))
                {
                    SeamlessCameraFocus.TryAutoFocusOnArrival(pawn, arrivalMap);
                    // 切图后恢复选中状态：若 pawn 在选中保持集里（玩家下达跨图指令时登记）则 re-Select。
                    // 用保持集而非 wasSelected：多 pawn 跨图是逐个的，首个 pawn 切图会 ClearSelection 清掉
                    // 其余 pawn 的选中，导致它们跨图时 IsSelected 返回 false。保持集跨越该时序记住"这批
                    // pawn 应选中"。必须在切图之后 re-Select：此时 CurrentMap==arrivalMap，SelectInternal
                    // 不会二次硬跳，Patch_Selector_SelectInternal 的无感偏移分支也不触发（targetMap==currentMap）。
                    if (SeamlessSelectionTracker.Consume(pawn) && Find.Selector != null)
                    {
                        Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
                    }
                    ContinueCrossMapMove(pawn, arrivalMap);
                }
                return;
            }
        }

        /// <summary>转移完成后，如果这次移动指令登记了跨图续程目的地，则在到达的地图上续发 Goto。</summary>
        private static void ContinueCrossMapMove(Pawn pawn, Map arrivalMap)
        {
            if (!SeamlessCrossMapPendingDestinations.TryConsume(pawn, arrivalMap, out var finalCell))
            {
                return;
            }

            if (!finalCell.InBounds(arrivalMap) || !finalCell.Walkable(arrivalMap))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Cross-map move continuation skipped: final cell {finalCell} on map {arrivalMap.uniqueID} is not walkable.");
                return;
            }

            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, finalCell), JobCondition.InterruptForced);
        }

        /// <summary>Pawn 是否处于跨图到达锁状态（刚被传送进本图，尚未离开接缝带）。</summary>
        private bool IsArrivalLocked(Pawn pawn)
        {
            return arrivalLocks.Contains(pawn);
        }

        /// <summary>
        /// 解除已失效或已离开接缝带的 Pawn 锁。
        /// 解除条件：Pawn 不再站在本图任何接缝传送点上（即已离开整条接缝带），
        /// 或 Pawn 已失效（destroyed/dead/despawned/不在本图）。
        /// 锁状态与特定传送点无关——Pawn 在锁期间踩任何接缝传送点都不会再触发传送。
        /// </summary>
        private void PurgeInvalidArrivalLocks()
        {
            if (arrivalLocks == null || arrivalLocks.Count == 0)
            {
                return;
            }

            // 收集本图所有接缝传送点的位置，用于判定 Pawn 是否仍在接缝带上。
            // 已锁 Pawn 通常只有 1-3 个（刚跨图的），接缝传送点数 ≈ 接缝长度。
            cachedEnterSpotDef ??= DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            seamPositions.Clear();
            if (cachedEnterSpotDef != null)
            {
                var spots = map.listerThings.ThingsOfDef(cachedEnterSpotDef);
                foreach (var spot in spots)
                {
                    seamPositions.Add(spot.Position);
                }
            }

            List<Pawn> stalePawns = null;
            foreach (var pawn in arrivalLocks)
            {
                // Pawn 失效：立即解除锁。
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned
                    || pawn.Map != map)
                {
                    stalePawns ??= new List<Pawn>();
                    stalePawns.Add(pawn);
                    continue;
                }

                // Pawn 仍在接缝带上（站在某个接缝传送点上）：保持锁。
                if (seamPositions.Contains(pawn.Position))
                {
                    continue;
                }

                // Pawn 已离开接缝带：解除锁。
                stalePawns ??= new List<Pawn>();
                stalePawns.Add(pawn);
            }

            if (stalePawns == null)
            {
                return;
            }

            foreach (var pawn in stalePawns)
            {
                arrivalLocks.Remove(pawn);
            }
        }
    }
}
