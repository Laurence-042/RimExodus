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
        // 每 tick 扫描传送点：消除"pawn 走到传送点上后停顿等待轮询"的卡顿。
        // 开销可控：ThingsOfDef 是 O(1) 取 list，pawn × spot 位置比较在接缝满铺（~96 spot）× 少量 pawn 下很低。
        private const int ScanIntervalTicks = 1;

        private int tickCounter;

        // 缓存传送点 Def，避免每 tick 调 DefDatabase.GetNamedSilentFail。
        private ThingDef cachedEnterSpotDef;

        /// <summary>
        /// 处于"跨图到达锁"状态的 Pawn 集合。
        /// Pawn 被传送进本图后进入锁状态：只要它还站在本图任一接缝传送点上，就保持锁，
        /// 防止续程寻路沿接缝前进时踩到相邻接缝传送点被立刻传回（反弹 bug）。
        /// Pawn 离开整条接缝带（不再站在任何本图传送点上）才解除锁。
        /// </summary>
        private Dictionary<Pawn, bool> arrivalLocks = new Dictionary<Pawn, bool>();

        // 复用的临时列表，避免每 tick new List 产生 GC（每 tick 调用 Clear + Add）。
        // 转移会修改 Thing/pawn 注册表，遍历前必须快照；用复用列表替代 new List。
        private static readonly List<Thing> ReusableSpotSnapshot = new List<Thing>();
        private static readonly List<Pawn> ReusablePawnSnapshot = new List<Pawn>();
        // PurgeInvalidArrivalLocks 复用的接缝位置集合。
        private static readonly HashSet<IntVec3> ReusableSeamPositions = new HashSet<IntVec3>();

        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref tickCounter, "tickCounter");
            Scribe_Collections.Look(ref arrivalLocks, "arrivalLocks", LookMode.Reference, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                arrivalLocks ??= new Dictionary<Pawn, bool>();
                PurgeInvalidArrivalLocks();
            }
        }

        public override void MapComponentTick()
        {
            // 每帧刷新锁：Pawn 一离开接缝带（不再站在任何传送点上）就解除锁。
            PurgeInvalidArrivalLocks();

            tickCounter++;
            if (tickCounter < ScanIntervalTicks)
            {
                return;
            }
            tickCounter = 0;

            CheckLocalEnterSpots();
        }

        /// <summary>在目标地图上登记 Pawn 的跨图到达锁状态（直到 Pawn 离开接缝带）。</summary>
        internal void RecordArrival(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map != map)
            {
                Log.Error("[RimExodus] Cannot record an invalid seamless-map arrival lock.");
                return;
            }

            arrivalLocks[pawn] = true;
        }

        private void CheckLocalEnterSpots()
        {
            cachedEnterSpotDef ??= DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (cachedEnterSpotDef == null)
            {
                return;
            }

            // 用 def 索引查询传送点（O(1)），避免全量遍历 AllThings。
            var enterSpots = map.listerThings.ThingsOfDef(cachedEnterSpotDef);
            if (enterSpots.Count == 0)
            {
                return;
            }

            // 转移会修改 Thing/pawn 注册表，遍历前必须快照。用复用列表避免每 tick GC。
            ReusableSpotSnapshot.Clear();
            ReusableSpotSnapshot.AddRange(enterSpots);
            ReusablePawnSnapshot.Clear();
            ReusablePawnSnapshot.AddRange(map.mapPawns.AllPawnsSpawned);

            foreach (var thing in ReusableSpotSnapshot)
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null)
                {
                    continue;
                }

                foreach (var pawn in ReusablePawnSnapshot)
                {
                    if (!pawn.Spawned || pawn.Map != map || pawn.Position != thing.Position
                        || pawn.Downed || pawn.Dead || IsArrivalLocked(pawn))
                    {
                        continue;
                    }

                    // 阶段4a：预铺未绑定的 spot（CounterpartSpot==null）不触发转移，
                    // 等对应邻居加载后由 SeamlessEnterSpotBinder 绑定。
                    if (comp.CounterpartSpot == null)
                    {
                        continue;
                    }

                    Log.Message($"[RimExodus] Seamless trigger: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} targeting {DescribeTarget(comp.CounterpartSpot)}");
                    var arrivalMap = comp.CounterpartSpot.Map;
                    if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, comp.CounterpartSpot))
                    {
                        // 自动聚焦必须在续程之前完成（切图后 pawn.Map==CurrentMap，避免 SelectInternal 二次跳镜头）。
                        SeamlessCameraFocus.TryAutoFocusOnArrival(pawn, arrivalMap);
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

        /// <summary>Pawn 是否处于跨图到达锁状态（刚被传送进本图，尚未离开接缝带）。</summary>
        private bool IsArrivalLocked(Pawn pawn)
        {
            return arrivalLocks.ContainsKey(pawn);
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
            ReusableSeamPositions.Clear();
            if (cachedEnterSpotDef != null)
            {
                var spots = map.listerThings.ThingsOfDef(cachedEnterSpotDef);
                foreach (var spot in spots)
                {
                    ReusableSeamPositions.Add(spot.Position);
                }
            }

            List<Pawn> stalePawns = null;
            foreach (var pair in arrivalLocks)
            {
                var pawn = pair.Key;
                // Pawn 失效：立即解除锁。
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned
                    || pawn.Map != map)
                {
                    stalePawns ??= new List<Pawn>();
                    stalePawns.Add(pawn);
                    continue;
                }

                // Pawn 仍在接缝带上（站在某个接缝传送点上）：保持锁。
                if (ReusableSeamPositions.Contains(pawn.Position))
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

        private static string DescribeTarget(Thing targetSpot)
        {
            return targetSpot?.Spawned == true
                ? $"{targetSpot.Position} on map {targetSpot.Map.uniqueID}"
                : "an invalid endpoint";
        }
    }
}
