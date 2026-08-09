using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨地图移动指令的桥接逻辑。
    /// 前端（<see cref="Patch_FloatMenuMakerMap_GetOptions"/>）在解析出真正的 Map + 局部坐标后，
    /// 用 <see cref="RecordPendingTarget"/> 登记"这个 Pawn 下一次 Goto Job 真正想去哪"；
    /// 后端（<see cref="Patch_Pawn_JobTracker_StartJob"/>）消费这条记录，如果跨图，
    /// 就把原始 Job 替换成"先走到桥接传送点"的两段式方案。
    /// </summary>
    public static class SeamlessCrossMapOrders
    {
        private readonly struct PendingTarget
        {
            public readonly Map Map;
            public readonly IntVec3 Cell;

            public PendingTarget(Map map, IntVec3 cell)
            {
                Map = map;
                Cell = cell;
            }
        }

        private static readonly Dictionary<Pawn, PendingTarget> pendingMenuTargets = new Dictionary<Pawn, PendingTarget>();

        public static void RecordPendingTarget(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn == null || map == null)
            {
                return;
            }

            pendingMenuTargets[pawn] = new PendingTarget(map, cell);
        }

        /// <summary>
        /// 在 Pawn_JobTracker.StartJob 之前拦截跨地图 Goto Job。
        /// 返回 true 表示已经接管（原始 Job 不应再执行）。
        /// </summary>
        public static bool TryInterceptJob(Pawn pawn, Job newJob)
        {
            if (pawn == null || newJob == null || newJob.def != JobDefOf.Goto)
            {
                return false;
            }

            if (!pendingMenuTargets.TryGetValue(pawn, out var pending))
            {
                return false;
            }

            // 无论是否命中桥接条件，都只消费一次，避免过期记录影响后续无关的 Job。
            pendingMenuTargets.Remove(pawn);

            // 目标地图内部会按可站立格重新选点（不一定等于原始点击格），因此不能按 Cell 精确匹配，
            // 只要该 Pawn 存在跨地图的待处理点击、且这是紧随其后的一个 Goto 单，就当作同一次指令。
            if (pending.Map == pawn.Map)
            {
                return false;
            }

            return TryBridgeJob(pawn, pending.Map, pending.Cell);
        }

        private static bool TryBridgeJob(Pawn pawn, Map targetMap, IntVec3 targetLocalCell)
        {
            if (!TryFindNearestReachableBridgeSpot(pawn, targetMap, out var exitSpot))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Cross-map move rejected: no reachable seamless enter spot bridges map {pawn.Map.uniqueID} to map {targetMap.uniqueID}.");
                return false;
            }

            SeamlessCrossMapPendingDestinations.Record(pawn, targetMap, targetLocalCell);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, exitSpot.Position), JobCondition.InterruptForced);
            return true;
        }

        /// <summary>
        /// 在 fromMap 上找到所有能桥接到 toMap 的传送点，按到 pawn 的距离排序，
        /// 返回第一个 pawn 能到达的。满铺接缝后候选很多，最近的通常可达即返回。
        /// </summary>
        private static bool TryFindNearestReachableBridgeSpot(Pawn pawn, Map toMap, out Thing exitSpot)
        {
            exitSpot = null;
            var fromMap = pawn.Map;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null)
            {
                return false;
            }

            // 用 def 索引查询（O(1)），避免全量遍历 AllThings。
            var candidates = new List<(Thing spot, int distSq)>();
            foreach (var thing in fromMap.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp?.CounterpartSpot == null || comp.CounterpartSpot.Map != toMap)
                {
                    continue;
                }
                candidates.Add((thing, pawn.Position.DistanceToSquared(thing.Position)));
            }

            // 按距离升序排序，依次尝试可达性。
            candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            var traverseParms = TraverseParms.For(pawn);
            foreach (var (spot, _) in candidates)
            {
                if (fromMap.reachability.CanReach(pawn.Position, spot.Position, PathEndMode.OnCell, traverseParms))
                {
                    exitSpot = spot;
                    return true;
                }
            }
            return false;
        }
    }
}
