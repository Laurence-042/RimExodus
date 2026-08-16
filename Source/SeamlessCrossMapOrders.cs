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

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] TryInterceptJob: consumed pending goto for {pawn.LabelShort} "
                    + $"(pending map {pending.Map?.uniqueID ?? -1} cell {pending.Cell}, pawn map {pawn.Map?.uniqueID ?? -1}).");

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

            // 阶段5：桥接登记为 Bridge 传送许可（绑定 exitSpot + 携带最终目的地，吸收原
            // SeamlessCrossMapPendingDestinations），下发的 Goto 用 TransitTag 自标识为许可驱动 job。
            // 踩点时凭许可传送（无许可不传），传送消费后由许可携带的最终目的地续程。
            var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Bridge);
            grant.BoundSpot = exitSpot.Position;
            grant.FinalDestMap = targetMap;
            grant.FinalDestCell = targetLocalCell;

            var job = JobMaker.MakeJob(JobDefOf.Goto, exitSpot.Position);
            job.dutyTag = SeamlessTransferGrants.TransitTag;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Bridge issued: {pawn.LabelShort} on map {pawn.Map.uniqueID} "
                    + $"-> spot {exitSpot.Position}, final dest map {targetMap.uniqueID} cell {targetLocalCell}.");
            return true;
        }

        /// <summary>清除已失效 pawn 的待处理点击登记（死亡/销毁/离场后残留，防过期记录影响后续 Goto）。</summary>
        public static void PurgeInvalid()
        {
            if (pendingMenuTargets.Count == 0) return;

            List<Pawn> stale = null;
            foreach (var pair in pendingMenuTargets)
            {
                var pawn = pair.Key;
                // !Spawned 覆盖"原生离场转世界 pawn"（非 Destroyed 但不再跑 job，登记会永久残留）。
                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned)
                {
                    stale ??= new List<Pawn>();
                    stale.Add(pawn);
                }
            }
            if (stale != null)
            {
                foreach (var pawn in stale) pendingMenuTargets.Remove(pawn);
            }
        }

        /// <summary>
        /// 检查 pawn 是否能从本图桥接到 toMap（是否本图存在 pawn 可到达的、对端指向 toMap 的传送点）。
        /// 仅供前端菜单判断"跨图移动是否可行"用——不 Record pending、不返回 spot、不触发桥接。
        /// 与 <see cref="TryFindNearestReachableBridgeSpot"/> 共用同一可达性判定逻辑。
        /// </summary>
        public static bool CanBridgeTo(Pawn pawn, Map toMap)
        {
            return TryFindNearestReachableBridgeSpot(pawn, toMap, out _);
        }

        /// <summary>
        /// 在 fromMap 上找到所有能桥接到 toMap 的传送点，按到 pawn 的距离排序，
        /// 返回第一个 pawn 能到达的。满铺接缝后候选很多，最近的通常可达即返回。
        /// 候选判定：传送点的 <see cref="CompSeamlessTileEnterSpot.hasArrival"/> 且
        /// <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/> 等于 toMap 的 worldTile（O(1) 读缓存/字段）。
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

            var toMapWorldTile = SeamlessTileRegistry.GetMapWorldTile(toMap);

            // 用 def 索引查询（O(1)），避免全量遍历 AllThings。
            var candidates = new List<(Thing spot, int distSq)>();
            foreach (var thing in fromMap.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                // 对端已缓存可用坐标 + targetWorldTile 指向 toMap。
                if (comp == null || !comp.hasArrival || comp.targetWorldTile != toMapWorldTile)
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
