using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>Runtime bridge reservations for jobs with an actual cross-map target.</summary>
    internal static class SeamlessTransferGrants
    {
        internal const string TransitTag = "RimExodus.Transit";
        internal const string AiTransitTag = "RimExodus.AiTransit";

        internal static bool IsTransitJob(Job job) => job?.dutyTag == TransitTag || job?.dutyTag == AiTransitTag;

        internal enum GrantKind
        {
            /// <summary>玩家跨图 goto 桥接（殖民者/殖民地机械族/驯养动物，行为表行 3/10/57）。</summary>
            Bridge,

            /// <summary>跨图追击（NPC 战斗体近战追击目标刚跨图，行为表行 17/25/33）。</summary>
            Pursue,

            /// <summary>跟随跨图（驯养动物/机械族 Escort/NPC 随从的 Follow job 目标刚跨图，行为表行 62/13）。</summary>
            Follow
        }

        internal sealed class Grant
        {
            public GrantKind Kind;

            public IntVec3 BoundSpot = IntVec3.Invalid;

            /// <summary>Bridge：跨图后的续程目的地（吸收原 SeamlessCrossMapPendingDestinations）。</summary>
            public Map FinalDestMap;
            public IntVec3 FinalDestCell;

            /// <summary>
            /// Bridge：跨图包装的原 job（2026-08 公共函数层重构，StartPath 包装路径）——传送落地后
            /// 直接续跑此 job（一切玩家点击下令的 job 通用：开采/砍伐/搬运/近战等），null = 走
            /// FinalDest 的 Goto 续程（PawnGotoAction 桥接路径，移动语义）。job 对象不克隆：
            /// 点击下令的 job 无累积状态（MakeNewToils 从头跑）。
            /// </summary>
            public Job NextJob;

            /// <summary>
            /// 玩家纯 Goto 在 StartJob 前被桥接时的值语义快照。与 NextJob 互斥：不持有尚未启动的
            /// Job 对象，桥接被取消时没有回池所有权；落地后据此重建目标图本地 Goto。
            /// </summary>
            public SeamlessGotoContinuation GotoContinuation;

            public Thing FollowTarget;
            public Thing PursuitTarget;
            public Lord SourceLord;
            public LordToil SourcePhase;
            public SeamlessRaidContinuity.Squad SourceSquad;
            public Job Driver;
            public int PursuitJobDeadline = -1;
            public bool NextJobFromLord;

            /// <summary>
            /// 传送时刻 pawn 所属 lord 的 LordJob 备份（<see cref="SeamlessMapTransfer.TryTransferPawn"/>
            /// 剥离点原子捕获——一切 lord 类型、一切 pawn 统一备份；pawn 至多一个 lord（pawn.lord 单引用），
            /// 多叛奴各自捕获同一引用，落地由续接策略合流）。LordJob 引用在原 lord 销毁后读纯字段仍安全。
            /// 兼容叛乱延续与工兵／破墙行军交界的续程标记。
            /// </summary>
            public LordJob PrevLordJob;

            public int CreatedTick = GenTicks.TicksGame;
        }

        // Old saves record an intermediate driver ID. Read that identity only so the
        // obsolete driver can be stopped and the original think tree can run again.
        internal sealed class SavedRaidTransit : IExposable
        {
            internal Pawn Pawn;
            internal int DriverJobId;

            public void ExposeData()
            {
                Scribe_References.Look(ref Pawn, "pawn");
                Scribe_Values.Look(ref DriverJobId, "driverJobId");
            }
        }

        private static readonly Dictionary<Pawn, Grant> Grants = new Dictionary<Pawn, Grant>();

        internal const int GrantTimeoutTicks = 6000;

        private static ThingDef _enterSpotDef;

        /// <summary>传送点 def（缓存；撤离推迟 patch 与踩点触发器共用的判据源）。</summary>
        internal static ThingDef EnterSpotDef
        {
            get
            {
                if (_enterSpotDef == null)
                    _enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
                return _enterSpotDef;
            }
        }

        internal static bool TryGet(Pawn pawn, out Grant grant)
        {
            grant = null;
            return pawn != null && Grants.TryGetValue(pawn, out grant);
        }

        /// <summary>登记/覆盖 pawn 的传送许可（覆盖语义：同 pawn 新许可取代旧许可）。</summary>
        internal static Grant Create(Pawn pawn, GrantKind kind)
        {
            var grant = new Grant { Kind = kind };
            Grants[pawn] = grant;
            return grant;
        }

        internal static void BindPursuit(Grant grant, Pawn pawn, Thing target, Job job)
        {
            grant.PursuitTarget = target;
            BindAiPhase(grant, pawn);
            grant.NextJob = job.Clone();
            grant.NextJob.loadID = Find.UniqueIDsManager.GetNextJobID();
            grant.NextJobFromLord = grant.NextJob.lord != null;
            grant.NextJob.lord = null;
            grant.NextJob.targetA = target;
            if (job.expiryInterval > 0 && job.startTick >= 0)
                grant.PursuitJobDeadline = job.startTick + job.expiryInterval;
        }

        internal static void BindFollow(Grant grant, Pawn pawn, Thing target)
        {
            grant.FollowTarget = target;
            BindAiPhase(grant, pawn);
        }

        private static void BindAiPhase(Grant grant, Pawn pawn)
        {
            grant.SourceLord = pawn.GetLord();
            grant.SourcePhase = grant.SourceLord?.CurLordToil;
            grant.SourceSquad = SeamlessRaidContinuity.Current?.FindSquad(grant.SourceLord);
        }

        internal static bool MatchesAiPhase(Pawn pawn, Grant grant)
        {
            var lord = pawn.GetLord();
            if (lord == grant.SourceLord) return lord?.CurLordToil == grant.SourcePhase;
            return grant.SourceSquad != null && grant.SourceSquad == SeamlessRaidContinuity.Current?.FindSquad(lord)
                && lord?.CurLordToil?.GetType() == grant.SourcePhase?.GetType();
        }

        internal static void Reset()
        {
            Grants.Clear();
            SeamlessEvacuationRouting.Reset();
        }

        internal static void ReplanLoadedJobs(List<SavedRaidTransit> oldTransit)
        {
            Reset();
            var obsolete = new Dictionary<Pawn, int>();
            if (oldTransit != null)
                foreach (var entry in oldTransit)
                    if (entry.Pawn != null) obsolete[entry.Pawn] = entry.DriverJobId;

            foreach (var map in Find.Maps)
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    if (NeedsReplan(pawn, obsolete))
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        internal static bool NeedsReplan(Pawn pawn, Dictionary<Pawn, int> obsolete)
        {
            var job = pawn?.CurJob;
            if (job?.dutyTag == AiTransitTag) return true;
            return job?.dutyTag == TransitTag
                && ((obsolete.TryGetValue(pawn, out var oldId) && oldId == job.loadID)
                    || pawn.Faction != Faction.OfPlayer || pawn.GetLord() != null);
        }

        /// <summary>Forget a transfer binding when its driver is replaced.</summary>
        internal static void Remove(Pawn pawn)
        {
            if (pawn != null) Grants.Remove(pawn);
        }

        /// <summary>
        /// 传送完成事件（<see cref="SeamlessMapTransferTrigger"/> 在传送成功、许可已消费后调用）：
        /// 按 Kind 续程 + 在出发地图上扫描追击者/跟随者（目标/主人刚跨图的瞬间事件标记）。
        /// </summary>
        internal static void NotifyPawnTransferred(Pawn pawn, Map departureMap, Thing departureSpot, Map arrivalMap, Grant grant)
        {
            if (pawn == null || departureMap == null || arrivalMap == null) return;

            switch (grant?.Kind)
            {
                case GrantKind.Bridge:
                case GrantKind.Pursue:
                    if (grant.PrevLordJob is LordJob_SlaveRebellion rebellion)
                        ContinueSlaveRebellion(pawn, arrivalMap, rebellion);
                    // An explicit raid march ends at the seam. The target map's sapper or
                    // breaching duty chooses its own target as soon as the pawn is attached.
                    if (grant.PrevLordJob is LordJob_SeamlessRaidMarch march && march.Marching
                        && SeamlessTileRegistry.GetMapWorldTile(arrivalMap) == march.TargetTile)
                        break;
                    ContinueBridgeMove(pawn, arrivalMap, grant);
                    break;
            }

            // 扫描与本次传送同源：目标（pawn）刚从 departureSpot 跨图，此刻的 job 意图即跨图意图。
            MarkPursuers(pawn, departureMap, departureSpot);
            MarkFollowers(pawn, departureMap, departureSpot);
        }

        private static bool ContinueSlaveRebellion(Pawn pawn, Map arrivalMap, LordJob_SlaveRebellion rebellion)
        {
            var manager = arrivalMap.lordManager;
            if (manager == null) return false;

            // 复用落地图上存活的叛乱 lord：多叛奴先后跨图合流为一场；追回原图时并回原叛乱（原生语义）。
            // AddPawn 内部自带 attackTargetsCache.UpdateTarget（Lord.AddPawnInternal），无需手动刷新。
            foreach (var lord in manager.lords)
            {
                if (lord.LordJob is LordJob_SlaveRebellion && lord.ownedPawns.Count > 0 && lord.CanAddPawn(pawn))
                {
                    lord.AddPawn(pawn);
                    if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                        Log.Message($"[RimExodus] Lord continuation (SlaveRebellion): {pawn.LabelShort} joins the ongoing "
                            + $"rebellion lord on map {arrivalMap.uniqueID}.");
                    return true;
                }
            }

            // 无存活叛乱 lord → 重建：groupUpLoc/exitPoint = 落地点（Travel toil 即刻到达 → TravelArrived
            // memo → 立即进攻击/逃亡 toil），sapperThingID=-1，passive 保留原展别（IsAggressiveRebellion
            // 为 public 属性，无反射）。激进展计时器在新图重起（26k-38k ticks 后 ExitMapFighting 撤出
            // = 原版超时放弃语义）。
            LordMaker.MakeNewLord(pawn.Faction,
                new LordJob_SlaveRebellion(pawn.Position, pawn.Position, -1, !rebellion.IsAggressiveRebellion),
                arrivalMap, new[] { pawn });
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Lord continuation (SlaveRebellion): {pawn.LabelShort} starts a rebellion lord "
                    + $"on map {arrivalMap.uniqueID} (passive={!rebellion.IsAggressiveRebellion}).");
            return true;
        }

        /// <summary>
        /// Bridge 续程：跨图落地后继续执行。NextJob 非空（StartPath 跨图包装路径）→ 直接续跑原 job
        ///（传送后 pawn 已在目标图，job 目标变同图，原生 driver 接管）；否则走 FinalDest 的 Goto
        /// 续程（PawnGotoAction 桥接路径，移动语义）。
        /// </summary>
        private static void ContinueBridgeMove(Pawn pawn, Map arrivalMap, Grant grant)
        {
            if (grant.PursuitTarget != null && grant.NextJob != null)
            {
                if (grant.PursuitTarget.Map != arrivalMap) return;
                if (grant.NextJobFromLord)
                    grant.NextJob.lord = pawn.GetLord();
                if (grant.PursuitJobDeadline >= 0)
                    grant.NextJob.expiryInterval = System.Math.Max(1, grant.PursuitJobDeadline - GenTicks.TicksGame);
            }

            // def 判空防御（2026-09 NRE 根因收口）：包装 job 按引用存于 grant，一旦被回池
            //（Job.Clear 置 def=null）即成悬垂引用——StartJob→MakeDriver 访问 def.driverClass
            // 直接 NRE，并把 jobTracker 打进每 tick 报错的坏状态。命中时按无包装 job 走
            // FinalDest 续链（行为等价：Goto 到目标格，同图 think 接管）。
            if (grant.NextJob != null && grant.NextJob.def != null)
            {
                var pickupOrder = SeamlessPawnPickupOrders.IsPickupOrder(grant.NextJob);
                if (pickupOrder && !SeamlessPawnPickupOrders.TargetsOnMap(grant.NextJob, arrivalMap))
                {
                    JobMaker.ReturnToPool(grant.NextJob);
                    return;
                }

                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Bridge continuation: resuming wrapped job {grant.NextJob.def.defName} "
                        + $"for {pawn.LabelShort} on map {arrivalMap.uniqueID}.");
                pawn.jobs.StartJob(grant.NextJob, JobCondition.InterruptForced,
                    preToilReservationsCanFail: pickupOrder);
                return;
            }
            if (grant.NextJob != null)
                Log.Warning($"[RimExodus] Bridge continuation: wrapped job of {pawn.LabelShort} was pooled before "
                    + $"arrival on map {arrivalMap.uniqueID}; falling back to final-dest goto.");
            if (grant.FinalDestMap != arrivalMap) return;
            var finalCell = grant.FinalDestCell;
            // 载具终点判据（2026-08 第三轮对齐）：pawn 用 Walkable；载具终点必须是整车矩形可立格
            //（region/Walkable 单格语义的终点会被 VF A* 终点门槛截断 → "ran out of path nodes"
            // PatherFailed），经 VF 原生"就近合法终点"解析，放不下时自动取最近合法格。
            if (SeamlessVehiclesCompat.IsVehicle(pawn))
            {
                if (!finalCell.InBounds(arrivalMap)
                    || !SeamlessVehiclesCompat.TryFindVehicleStandableNear(pawn, finalCell, out finalCell))
                {
                    if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                        Log.Message($"[RimExodus] Bridge continuation skipped: no vehicle-standable cell near final dest "
                            + $"{grant.FinalDestCell} on map {arrivalMap.uniqueID} for {pawn.LabelShort}.");
                    return;
                }
            }
            else if (!finalCell.InBounds(arrivalMap) || !finalCell.Walkable(arrivalMap))
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Bridge continuation skipped: final cell {finalCell} on map {arrivalMap.uniqueID} is not walkable.");
                return;
            }
            // 载具与 pawn 同路：传送前已同步就绪化 VF 网格（TryTransferPawn 车辆分支①），
            // StartJob 立即可走，无需任何等待/重试机制（v1 的推迟队列/看护踢发已删，教训见
            // SeamlessVehiclesCompat 类注释）。
            var finalJob = JobMaker.MakeJob(JobDefOf.Goto, finalCell);
            grant.GotoContinuation?.ApplyToFinal(finalJob);
            pawn.jobs.StartJob(finalJob, JobCondition.InterruptForced);
        }

        /// <summary>下发 TransitTag Goto（Grant 驱动 job）。dutyTag 自标识使 StartJob 钩子不清除对应 Grant。</summary>
        private static void IssueTransitGoto(Pawn pawn, Grant grant, IntVec3 spotCell, LocomotionUrgency locomotion,
            bool canBashDoors = false, bool canBashFences = false)
        {
            if (pawn.Position == spotCell)
            {
                grant.Driver = pawn.CurJob;
                SeamlessMapTransferTrigger.TryTriggerTransfer(pawn, spotCell, pawn.Map);
                return;
            }
            var job = JobMaker.MakeJob(JobDefOf.Goto, spotCell);
            job.dutyTag = AiTransitTag;
            job.locomotionUrgency = locomotion;
            job.canBashDoors = canBashDoors;
            job.canBashFences = canBashFences;
            job.expiryInterval = GrantTimeoutTicks;
            grant.Driver = job;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        private static void MarkPursuers(Pawn target, Map departureMap, Thing spot)
        {
            if (!SeamlessCombatCoords.Enabled || !target.Spawned) return;
            List<Pawn> pursuers = null;
            foreach (var other in departureMap.mapPawns.AllPawns)
            {
                if (other == target || other.Map != departureMap
                    || SeamlessPursuit.Target(other, other.CurJob) != target
                    || !SeamlessPursuit.CanCross(other, target, target.Map)) continue;

                // Keep shooting across the seam when the current firing position still works.
                var verb = other.TryGetAttackVerb(target, true);
                if (verb != null && verb.CanHitTarget(new LocalTargetInfo(target))) continue;

                pursuers ??= new List<Pawn>();
                pursuers.Add(other);
            }
            if (pursuers == null) return;
            foreach (var other in pursuers)
            {
                // A pawn already standing on this spot may transfer synchronously in StartJob.
                var job = other.CurJob;
                if (other.Map != departureMap || SeamlessPursuit.Target(other, job) != target
                    || !SeamlessPursuit.CanCross(other, target, target.Map)) continue;
                var grant = Create(other, GrantKind.Pursue);
                grant.BoundSpot = spot.Position;
                BindPursuit(grant, other, target, job);
                IssueTransitGoto(other, grant, spot.Position, job.locomotionUrgency, job.canBashDoors, job.canBashFences);
            }
        }

        /// <summary>
        /// 跟随者扫描（行为表行 62/13 + 商队随从）：跟随目标（Follow/FollowClose job 指向跨图者）的 pawn
        /// 借同一传送点跟随跨图。驯养动物、机械族 Escort（2026-09）与 NPC 随从（商队 carrier，
        /// 防商队过缝解体）统一覆盖。
        /// </summary>
        private static void MarkFollowers(Pawn leader, Map departureMap, Thing spot)
        {
            // An adjacent follower can cross synchronously and change this map's pawn list.
            List<Pawn> followers = null;
            foreach (var other in departureMap.mapPawns.AllPawns)
            {
                if (other == leader || !other.Spawned || other.Downed || other.Dead) continue;
                var job = other.CurJob;
                if (job == null) continue;
                if (job.def != JobDefOf.Follow && job.def != JobDefOf.FollowClose) continue;
                if (job.targetA.Thing != leader) continue;
                if (!SeamlessRaidContinuity.CanFollowAcross(other.GetLord())) continue;
                (followers ??= new List<Pawn>()).Add(other);
            }
            if (followers == null) return;
            foreach (var other in followers)
            {
                var job = other.CurJob;
                if (other.Map != departureMap || job?.targetA.Thing != leader
                    || (job.def != JobDefOf.Follow && job.def != JobDefOf.FollowClose)
                    || !SeamlessRaidContinuity.CanFollowAcross(other.GetLord())) continue;
                var grant = Create(other, GrantKind.Follow);
                grant.BoundSpot = spot.Position;
                BindFollow(grant, other, leader);
                IssueTransitGoto(other, grant, spot.Position, job.locomotionUrgency);

                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Follow grant: {other.LabelShort} follows {leader.LabelShort} across map {departureMap.uniqueID}.");
            }
        }

        /// <summary>Drop stale runtime bridge reservations.</summary>
        internal static void TickSweep()
        {
            if (Grants.Count > 0)
            {
                List<Pawn> stale = null;
                foreach (var pair in Grants)
                {
                    var pawn = pair.Key;
                    if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map == null
                        || (pair.Value.Driver != null && pair.Value.Driver != pawn.CurJob)
                        || GenTicks.TicksGame - pair.Value.CreatedTick > GrantTimeoutTicks)
                    {
                        stale ??= new List<Pawn>();
                        stale.Add(pawn);
                    }
                }
                if (stale != null)
                {
                    foreach (var pawn in stale) Grants.Remove(pawn);
                }
            }
        }
    }
}
