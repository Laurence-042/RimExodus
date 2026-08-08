using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 当前地图上的无缝入口触发器。
    /// 每张地图只扫描自己的入口；入口通过 CounterpartSpot 直接指向目标端点。
    /// </summary>
    public class SeamlessMapTransferTrigger : MapComponent
    {
        private const int ScanIntervalTicks = 30;

        private int tickCounter;
        private Dictionary<Pawn, Thing> arrivalLocks = new Dictionary<Pawn, Thing>();

        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref tickCounter, "tickCounter");
            Scribe_Collections.Look(ref arrivalLocks, "arrivalLocks", LookMode.Reference, LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                arrivalLocks ??= new Dictionary<Pawn, Thing>();
                PurgeInvalidArrivalLocks();
            }
        }

        public override void MapComponentTick()
        {
            // 防抖锁不是定时冷却：Pawn 一旦离开到达入口，就在本 tick 解除。
            PurgeInvalidArrivalLocks();

            tickCounter++;
            if (tickCounter < ScanIntervalTicks)
            {
                return;
            }
            tickCounter = 0;

            CheckLocalEnterSpots();
        }

        /// <summary>在目标地图上登记 Pawn 的到达入口，直到 Pawn 离开该格。</summary>
        internal void RecordArrival(Pawn pawn, Thing arrivalSpot)
        {
            if (pawn == null || arrivalSpot == null || !pawn.Spawned || !arrivalSpot.Spawned
                || pawn.Map != map || arrivalSpot.Map != map || pawn.Position != arrivalSpot.Position)
            {
                Log.Error("[RimExodus] Cannot record an invalid seamless-map arrival lock.");
                return;
            }

            arrivalLocks[pawn] = arrivalSpot;
        }

        private void CheckLocalEnterSpots()
        {
            // 转移会修改地图的 Thing/Pawn 注册表，因此两者都使用快照。
            var allThings = new List<Thing>(map.listerThings.AllThings);
            var pawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);

            foreach (var thing in allThings)
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null)
                {
                    continue;
                }

                foreach (var pawn in pawns)
                {
                    if (!pawn.Spawned || pawn.Map != map || pawn.Position != thing.Position
                        || pawn.Downed || pawn.Dead || IsArrivalLocked(pawn, thing))
                    {
                        continue;
                    }

                    Log.Message($"[RimExodus] Seamless trigger: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} targeting {DescribeTarget(comp.CounterpartSpot)}");
                    var arrivalMap = comp.CounterpartSpot.Map;
                    if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, comp.CounterpartSpot))
                    {
                        ContinueCrossMapMove(pawn, arrivalMap);
                    }
                }
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
                Log.Message($"[RimExodus] Cross-map move continuation skipped: final cell {finalCell} on map {arrivalMap.uniqueID} is not walkable.");
                return;
            }

            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, finalCell), JobCondition.InterruptForced);
        }

        private bool IsArrivalLocked(Pawn pawn, Thing currentSpot)
        {
            return arrivalLocks.TryGetValue(pawn, out var arrivalSpot) && arrivalSpot == currentSpot;
        }

        private void PurgeInvalidArrivalLocks()
        {
            if (arrivalLocks == null || arrivalLocks.Count == 0)
            {
                return;
            }

            List<Pawn> stalePawns = null;
            foreach (var pair in arrivalLocks)
            {
                var pawn = pair.Key;
                var arrivalSpot = pair.Value;
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned
                    || arrivalSpot == null || arrivalSpot.Destroyed || !arrivalSpot.Spawned
                    || pawn.Map != map || arrivalSpot.Map != map || pawn.Position != arrivalSpot.Position)
                {
                    stalePawns ??= new List<Pawn>();
                    stalePawns.Add(pawn);
                }
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

        private static string DescribeTarget(Thing targetSpot)
        {
            return targetSpot?.Spawned == true
                ? $"{targetSpot.Position} on map {targetSpot.Map.uniqueID}"
                : "an invalid endpoint";
        }
    }
}
