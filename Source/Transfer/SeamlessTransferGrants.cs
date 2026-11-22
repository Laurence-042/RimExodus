using System.Collections.Generic;
using RimWorld.Planet;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 跨图传送许可登记表（阶段5 边界行为，doc/边界行为表.md）。
    ///
    /// 架构（判定收拢）：传送资格不在踩点时判定，而在"状态变更事件"处登记 <see cref="Grant"/> 并绑定传送点：
    /// ① StartJob（<see cref="Patches_Job"/>）：NPC 撤离 job 下发 → <see cref="RegisterEvacuation"/>；
    ///    任何非 TransitTag job 启动 → 旧 Grant 失效清除（驱动 job 被替换/打断/完成）。
    /// ② TryBridgeJob（<see cref="SeamlessCrossMapOrders"/>）：玩家跨图 goto → Bridge Grant + TransitTag Goto。
    /// ③ <see cref="NotifyPawnTransferred"/>（传送完成事件）：撤离链续程（出口远离来向接缝）+
    ///    追击者/跟随者扫描（目标/主人刚跨图的瞬间事件标记，意图由事件本身保证）。
    /// 踩点热路径（<see cref="SeamlessMapTransferTrigger.TryTriggerTransfer"/>）只做一次字典查询 + 匹配分派。
    /// 无 Grant 不传——行为表"收紧"（闲逛/工作/无 flag 逃跑一律无事）成为结构性结果。
    /// </summary>
    internal static class SeamlessTransferGrants
    {
        /// <summary>
        /// Grant 驱动 job 的自标识：写在我们下发的 Goto job.dutyTag 上。
        /// dutyTag 是干净的可序列化字符串字段（无引擎消费者副作用）；StartJob 清理逻辑据此
        /// 识别"这是某个 Grant 的驱动 job"，不当作 job 替换清除许可。mod 无旧档兼容负担。
        /// </summary>
        internal const string TransitTag = "RimExodus.Transit";

        internal enum GrantKind
        {
            /// <summary>玩家跨图 goto 桥接（殖民者/殖民地机械族/驯养动物，行为表行 3/10/57）。</summary>
            Bridge,

            /// <summary>NPC 撤离链（撤离 duty/囚犯越狱/野性恐慌/释放访客，行为表行 21/29/37/43/49/55）。</summary>
            Evacuation,

            /// <summary>跨图追击（NPC 战斗体近战追击目标刚跨图，行为表行 17/25/33）。</summary>
            Pursue,

            /// <summary>跟随跨图（驯养动物/机械族 Escort/NPC 随从的 Follow job 目标刚跨图，行为表行 62/13）。</summary>
            Follow
        }

        internal sealed class Grant : IExposable
        {
            public GrantKind Kind;

            /// <summary>
            /// 绑定的传送点格。Invalid = Evacuation 首跳（原版撤离 job 驱动，首个踩到的传送点即匹配，
            /// 由 JobDriver 的原生出口检查兜底）；其余 Kind / 续程链恒为具体格（登记时已选定）。
            /// </summary>
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

            /// <summary>
            /// Follow：跟随目标引用（登记时的 leader）。落地判定"目标已在本图 → 有目的，免游荡
            /// 清扫"用——机械族等非动物跟随者此前落地即进 StrayNpcs，600 ticks 后被误转撤离链
            /// 赶出地图（2026-09 修复）。运行时引用，Grant 不序列化故无存档负担。
            /// </summary>
            public Thing FollowTarget;
            public Thing PursuitTarget;
            public Lord PursuitLord;
            public SeamlessRaidContinuity.Squad PursuitSquad;
            public int PursuitJobDeadline = -1;
            public bool NextJobFromLord;

            /// <summary>
            /// 传送时刻 pawn 所属 lord 的 LordJob 备份（<see cref="SeamlessMapTransfer.TryTransferPawn"/>
            /// 剥离点原子捕获——一切 lord 类型、一切 pawn 统一备份；pawn 至多一个 lord（pawn.lord 单引用），
            /// 多叛奴各自捕获同一引用，落地由续接策略合流）。LordJob 引用在原 lord 销毁后读纯字段仍安全。
            /// 兼容叛乱延续与工兵／破墙行军交界的续程标记。
            /// </summary>
            public LordJob PrevLordJob;

            /// <summary>Evacuation：撤离链已访问的世界 tile（防回弹），跨 hop 传递复用。</summary>
            public HashSet<PlanetTile> VisitedTiles;

            // 首跳记录任务起点；真正过缝时由实际出口确定方向，之后整条链保持方向。
            // 相邻坐标系只平移，方向向量无需转换。均为随许可销毁的运行时状态。
            public IntVec3 EvacuationOrigin = IntVec3.Invalid;
            public IntVec3 EvacuationDirection = IntVec3.Zero;
            public bool CanBashDoors;
            public bool CanBashFences;

            /// <summary>从原撤离 job 复制的移动速度档，续程沿用。</summary>
            public LocomotionUrgency Locomotion = LocomotionUrgency.Jog;

            /// <summary>驱动 job 是否为我们下发的 TransitTag Goto（原版 flag job 驱动 = false）。</summary>
            public bool SelfDriven;

            /// <summary>只剩已访问出口可用：踩点直接原生离场（撤离链终止兜底）。</summary>
            public bool ForceExit;

            /// <summary>撤离推迟 patch（Patches_EvacuationExitDefer）的日志节流：
            /// 上次输出 deferred 日志的 tick，0 = 未输出过（许可按 job 重建，天然按许可重置）。</summary>
            public int LastDeferLogTick;

            public int CreatedTick = GenTicks.TicksGame;

            public void ExposeData()
            {
                Scribe_Values.Look(ref Kind, "kind");
                Scribe_Values.Look(ref BoundSpot, "spot", IntVec3.Invalid);
                Scribe_Values.Look(ref CreatedTick, "createdTick");
                Scribe_References.Look(ref PursuitTarget, "pursuitTarget");
                Scribe_References.Look(ref PursuitLord, "pursuitLord");
                Scribe_Values.Look(ref PursuitJobDeadline, "pursuitDeadline", -1);
                Scribe_Values.Look(ref NextJobFromLord, "nextJobFromLord");
                Scribe_Deep.Look(ref NextJob, "nextJob");
                Scribe_Values.Look(ref Locomotion, "locomotion", LocomotionUrgency.Jog);
                Scribe_Values.Look(ref EvacuationOrigin, "evacuationOrigin", IntVec3.Invalid);
                Scribe_Values.Look(ref EvacuationDirection, "evacuationDirection", IntVec3.Zero);
                if (Kind == GrantKind.Evacuation)
                {
                    List<PlanetTile> visited = null;
                    if (Scribe.mode == LoadSaveMode.Saving)
                        visited = VisitedTiles != null ? new List<PlanetTile>(VisitedTiles) : new List<PlanetTile>();
                    SeamlessTileScribe.Look(ref visited, "visitedTiles");
                    if (Scribe.mode == LoadSaveMode.LoadingVars) VisitedTiles = new HashSet<PlanetTile>(visited);
                }
                Scribe_Values.Look(ref SelfDriven, "selfDriven");
                Scribe_Values.Look(ref ForceExit, "forceExit");
                Scribe_Values.Look(ref CanBashDoors, "canBashDoors");
                Scribe_Values.Look(ref CanBashFences, "canBashFences");
            }
        }

        internal sealed class SavedRaidTransit : IExposable
        {
            internal Pawn Pawn;
            internal int DriverJobId;
            internal Grant Permit;

            public void ExposeData()
            {
                Scribe_References.Look(ref Pawn, "pawn");
                Scribe_Values.Look(ref DriverJobId, "driverJobId");
                Scribe_Deep.Look(ref Permit, "permit");
            }
        }

        private static readonly Dictionary<Pawn, Grant> Grants = new Dictionary<Pawn, Grant>();

        /// <summary>
        /// 游荡 NPC 兜底：失去跟随目标的 NPC（传送时 lord 已被 Notify_PawnLost 剥离、无 duty），
        /// 宽限期内重新接战或加入队伍即解除，超时转入撤离链。
        /// FromWorldTile = 其来向 tile，转入撤离链时排除（防立刻传回原地图）。
        /// </summary>
        private struct StrayInfo
        {
            public int DeadlineTick;
            public PlanetTile FromWorldTile;
        }

        private static readonly Dictionary<Pawn, StrayInfo> StrayNpcs = new Dictionary<Pawn, StrayInfo>();

        private const int StrayGraceTicks = 600;

        /// <summary>Grant 超时兜底（正常生命周期由 StartJob 的 job 替换清理；此值只兜"驱动 job 长期挂起"，
        /// 须容纳 250 图全程步行续程：~3 格/秒 × 6000 ticks ≈ 100 游戏秒）。</summary>
        private const int GrantTimeoutTicks = 6000;

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
            grant.PursuitLord = pawn.GetLord();
            grant.PursuitSquad = SeamlessRaidContinuity.Current?.FindSquad(grant.PursuitLord);
            grant.NextJob = job.Clone();
            grant.NextJob.loadID = Find.UniqueIDsManager.GetNextJobID();
            grant.NextJobFromLord = grant.NextJob.lord != null;
            grant.NextJob.lord = null;
            grant.NextJob.targetA = target;
            if (job.expiryInterval > 0 && job.startTick >= 0)
                grant.PursuitJobDeadline = job.startTick + job.expiryInterval;
        }

        internal static List<SavedRaidTransit> CaptureRaidTransit(SeamlessRaidContinuity state)
        {
            List<SavedRaidTransit> saved = null;
            foreach (var pair in Grants)
            {
                var pawn = pair.Key;
                var grant = pair.Value;
                var driver = pawn.CurJob;
                if (!pawn.Spawned || driver == null || GenTicks.TicksGame - grant.CreatedTick > GrantTimeoutTicks) continue;
                if (grant.PursuitTarget != null && driver.dutyTag == TransitTag)
                {
                    var lord = pawn.GetLord();
                    if (lord != grant.PursuitLord && (grant.PursuitSquad == null || grant.PursuitSquad != state.FindSquad(lord)))
                        continue;
                    // The local Lord may have folded into the authority while we walked to the seam.
                    grant.PursuitLord = lord;
                }
                else if (grant.Kind != GrantKind.Evacuation || !state.TracksWithdrawal(pawn.GetLord())
                    || (!driver.exitMapOnArrival && driver.dutyTag != TransitTag)) continue;

                saved ??= new List<SavedRaidTransit>();
                saved.Add(new SavedRaidTransit { Pawn = pawn, DriverJobId = driver.loadID, Permit = grant });
            }
            return saved;
        }

        internal static void RestoreRaidTransit(SeamlessRaidContinuity state, List<SavedRaidTransit> saved)
        {
            Grants.Clear();
            StrayNpcs.Clear();
            if (saved == null) return;
            foreach (var entry in saved)
            {
                var pawn = entry.Pawn;
                var grant = entry.Permit;
                if (pawn?.Spawned != true || grant == null || pawn.CurJob?.loadID != entry.DriverJobId) continue;
                var lord = pawn.GetLord();
                if (grant.Kind == GrantKind.Evacuation)
                {
                    if (!state.TracksWithdrawal(lord)) continue;
                }
                else
                {
                    if (grant.PursuitTarget?.Spawned != true || grant.NextJob?.def == null
                        || pawn.CurJob.dutyTag != TransitTag || lord != grant.PursuitLord) continue;
                    grant.PursuitSquad = state.FindSquad(lord);
                }
                Grants[pawn] = grant;
            }
        }

        /// <summary>清除 pawn 的许可（传送消费 / 驱动 job 被替换）。</summary>
        internal static void Remove(Pawn pawn)
        {
            if (pawn != null) Grants.Remove(pawn);
        }

        /// <summary>
        /// 普通撤离 job 登记（StartJob 钩子调用）：撤离 duty / 囚犯越狱 / 野性恐慌 / 释放访客。
        /// 调用方已按 job 契约排除玩家强制撤离及必须加入远行队的任务，后者保留原版入队/失败语义。
        /// 首跳不绑具体点：首个踩到的传送点，对端已加载 → 传送续链；对端未生成 → 交还原生撤离。
        /// </summary>
        internal static void RegisterEvacuation(Pawn pawn, Job evacJob)
        {
            if (pawn == null || pawn.Map == null) return;
            var grant = Create(pawn, GrantKind.Evacuation);
            grant.Locomotion = evacJob.locomotionUrgency;
            grant.EvacuationOrigin = pawn.Position;
            grant.CanBashDoors = evacJob.canBashDoors;
            grant.CanBashFences = evacJob.canBashFences;
            grant.VisitedTiles = new HashSet<PlanetTile> { SeamlessTileRegistry.GetMapWorldTile(pawn.Map) };

            SeamlessEvacuationDiagnostics.OnEvacuationJobStarted(pawn, evacJob);
        }

        /// <summary>
        /// 传送完成事件（<see cref="SeamlessMapTransferTrigger"/> 在传送成功、许可已消费后调用）：
        /// 按 Kind 续程 + 在出发地图上扫描追击者/跟随者（目标/主人刚跨图的瞬间事件标记）。
        /// </summary>
        internal static void NotifyPawnTransferred(Pawn pawn, Map departureMap, Thing departureSpot, Map arrivalMap, Grant grant)
        {
            if (pawn == null || departureMap == null || arrivalMap == null || grant == null) return;

            switch (grant.Kind)
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
                case GrantKind.Evacuation:
                    ContinueEvacuationChain(pawn, departureMap, departureSpot, arrivalMap, grant);
                    break;
                case GrantKind.Follow:
                    if (SeamlessBoundaryRules.IsColonyAnimal(pawn)) break;
                    if (grant.FollowTarget != null && !grant.FollowTarget.Destroyed
                        && grant.FollowTarget.Map == arrivalMap) break;
                    StrayNpcs[pawn] = new StrayInfo
                    {
                        DeadlineTick = GenTicks.TicksGame + StrayGraceTicks,
                        FromWorldTile = SeamlessTileRegistry.GetMapWorldTile(departureMap)
                    };
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

        /// <summary>
        /// 撤离链续程：保留首次撤离方向，按前进距离/路径成本选择未访问出口。
        /// 加载状态只决定踩点后的传送/离场，不参与选点。无未访问出口才 ForceExit 兜底。
        /// 续程 job 不带 exitMapOnArrival：传送落点本身常是出口格，flag job 的 pre-tick IsExitCell
        /// 检查会在落地瞬间原生离场，破坏"继续跑"；离场动作改由踩点分派（SelfDriven + !hasArrival → ExitMap）。
        /// </summary>
        private static void ContinueEvacuationChain(Pawn pawn, Map departureMap, Thing departureSpot, Map arrivalMap, Grant grant)
        {
            var visited = grant.VisitedTiles ?? new HashSet<PlanetTile>();
            visited.Add(SeamlessTileRegistry.GetMapWorldTile(departureMap));
            visited.Add(SeamlessTileRegistry.GetMapWorldTile(arrivalMap));

            if (grant.EvacuationDirection == IntVec3.Zero)
            {
                if (grant.EvacuationOrigin.IsValid)
                    grant.EvacuationDirection = departureSpot.Position - grant.EvacuationOrigin;
                // 起点恰在出口时没有行程向量，用这次实际跨图的几何方向初始化。
                if (grant.EvacuationDirection == IntVec3.Zero
                    && SeamlessViewProjection.TryProject(departureMap, departureMap.Center, arrivalMap, out IntVec3 fromCenter))
                    grant.EvacuationDirection = arrivalMap.Center - fromCenter;
            }

            if (!TryFindEvacuationExit(pawn, arrivalMap, visited, grant.EvacuationDirection,
                grant.CanBashDoors, grant.CanBashFences, out var destSpot, out var forceExit))
            {
                // 极端兜底：本图无任何可达出口（孤立地形）。不登记，交还原版 think tree；
                // Grant 已消费，后续 job 启动不会误触发。
                // 2026-09 插桩：think tree 的 JobGiver_ExitMap 在同口径下也找不到出口时返回 null
                // → pawn 永久站桩在落地邻图的接缝带（"stuck on the border"候选之一），此行是唯一痕迹。
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] Evacuation chain stuck: {pawn.LabelShort} landed on map {arrivalMap.uniqueID} "
                        + "but NO reachable exit spot found — pawn left to vanilla think tree (likely idle at seam). "
                        + $"leftLord={grant.PrevLordJob?.GetType().Name ?? "none"}");
                return;
            }

            var next = Create(pawn, GrantKind.Evacuation);
            next.VisitedTiles = visited;
            next.Locomotion = grant.Locomotion;
            next.EvacuationDirection = grant.EvacuationDirection;
            next.CanBashDoors = grant.CanBashDoors;
            next.CanBashFences = grant.CanBashFences;
            next.SelfDriven = true;
            next.BoundSpot = destSpot;
            next.ForceExit = forceExit;

            if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                Log.Message($"[RimExodus] [caravan-exit] Evacuation chain: {pawn.LabelShort} continues on map {arrivalMap.uniqueID} "
                    + $"toward exit {destSpot} (direction={next.EvacuationDirection}, forceExit={forceExit}, visited={visited.Count}).");

            IssueTransitGoto(pawn, destSpot, grant.Locomotion, grant.CanBashDoors, grant.CanBashFences);
        }

        /// <summary>
        /// 单次泛洪估算本图出口成本，再以原生/VF 可达性验证排序后的候选。
        /// 优先未访问且向前推进效率最高的出口；无前向出口则选最便宜的侧向/后退出口，
        /// 最后才用已访问出口 ForceExit。零方向用于尚未选过出口的游荡者，按成本初始化。
        /// </summary>
        private static bool TryFindEvacuationExit(Pawn pawn, Map map, HashSet<PlanetTile> visited,
            IntVec3 direction, bool canBashDoors, bool canBashFences, out IntVec3 destSpot, out bool forceExit)
        {
            destSpot = IntVec3.Invalid;
            forceExit = false;
            var spotDef = EnterSpotDef;
            if (spotDef == null) return false;

            var cells = new List<IntVec3>();
            var tiles = new List<PlanetTile>();
            foreach (var thing in map.listerThings.ThingsOfDef(spotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                // 脚下出口无法再触发 nextCell；候选必须是一次实际移动能进入的格。
                if (comp == null || thing.Position == pawn.Position) continue;
                cells.Add(thing.Position);
                tiles.Add(comp.TargetTile);
            }
            if (cells.Count == 0) return false;

            var traverseParms = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn,
                canBashDoors, canBashFences: canBashFences);
            var costs = SeamlessPathCostField.FloodCosts(map, pawn.Position, cells, pawn, traverseParms);
            var candidates = new List<(IntVec3 cell, SeamlessEvacuationExitScore score)>();
            for (var i = 0; i < cells.Count; i++)
            {
                if (costs[i] <= 0 || costs[i] == int.MaxValue) continue;
                var delta = cells[i] - pawn.Position;
                long progress = (long)delta.x * direction.x + (long)delta.z * direction.z;
                candidates.Add((cells[i], new SeamlessEvacuationExitScore(progress, costs[i], visited.Contains(tiles[i]))));
            }
            candidates.Sort((a, b) =>
            {
                var order = a.score.CompareTo(b.score);
                if (order != 0) return order;
                order = a.cell.x.CompareTo(b.cell.x);
                return order != 0 ? order : a.cell.z.CompareTo(b.cell.z);
            });

            var isVehicle = SeamlessVehiclesCompat.IsVehicle(pawn);
            foreach (var (cell, score) in candidates)
            {
                var reachable = isVehicle
                    ? SeamlessVehiclesCompat.CanReachLocal(pawn, map, pawn.Position, cell)
                    : map.reachability.CanReach(pawn.Position, cell, PathEndMode.OnCell, traverseParms);
                if (!reachable) continue;
                destSpot = cell;
                forceExit = score.Visited;
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] Evacuation exit selected: {pawn.LabelShort} on map {map.uniqueID} "
                        + $"at {pawn.Position} -> {cell}, direction={direction}, progress={score.Progress}, cost={score.PathCost}, forceExit={forceExit}.");
                return true;
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
            {
                Log.Message($"[RimExodus] [caravan-exit] Evacuation exit search failed: no exit for {pawn.LabelShort} on map {map.uniqueID} "
                    + $"(spots={cells.Count}, finiteCostCandidates={candidates.Count}, visitedTiles={visited.Count}, direction={direction}).");
            }
            return false;
        }

        /// <summary>下发 TransitTag Goto（Grant 驱动 job）。dutyTag 自标识使 StartJob 钩子不清除对应 Grant。</summary>
        private static void IssueTransitGoto(Pawn pawn, IntVec3 spotCell, LocomotionUrgency locomotion,
            bool canBashDoors = false, bool canBashFences = false)
        {
            var job = JobMaker.MakeJob(JobDefOf.Goto, spotCell);
            job.dutyTag = TransitTag;
            job.locomotionUrgency = locomotion;
            job.canBashDoors = canBashDoors;
            job.canBashFences = canBashFences;
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
                IssueTransitGoto(other, spot.Position, job.locomotionUrgency, job.canBashDoors, job.canBashFences);
            }
        }

        /// <summary>
        /// 跟随者扫描（行为表行 62/13 + 商队随从）：跟随目标（Follow/FollowClose job 指向跨图者）的 pawn
        /// 借同一传送点跟随跨图。驯养动物、机械族 Escort（2026-09）与 NPC 随从（商队 carrier，
        /// 防商队过缝解体）统一覆盖。
        /// </summary>
        private static void MarkFollowers(Pawn leader, Map departureMap, Thing spot)
        {
            foreach (var other in departureMap.mapPawns.AllPawns)
            {
                if (other == leader || !other.Spawned || other.Downed || other.Dead) continue;
                var job = other.CurJob;
                if (job == null) continue;
                if (job.def != JobDefOf.Follow && job.def != JobDefOf.FollowClose) continue;
                if (job.targetA.Thing != leader) continue;
                if (SeamlessRaidContinuity.HasLocalEscortDuty(other.GetLord())) continue;

                IssueTransitGoto(other, spot.Position, LocomotionUrgency.Jog);
                var grant = Create(other, GrantKind.Follow);
                grant.BoundSpot = spot.Position;
                grant.FollowTarget = leader;

                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Follow grant: {other.LabelShort} follows {leader.LabelShort} across map {departureMap.uniqueID}.");
            }
        }

        /// <summary>
        /// 周期清扫（SeamlessDormancyGovernor.GameComponentTick 调用，2026-08 自"仅家园图组件 tick"迁来）：
        /// ① Grant 失效/超时；② 游荡 NPC 宽限检查（接战/有 lord 解除，超时转入撤离链）。
        /// </summary>
        internal static void TickSweep()
        {
            if (Grants.Count > 0)
            {
                List<Pawn> stale = null;
                foreach (var pair in Grants)
                {
                    var pawn = pair.Key;
                    if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map == null
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

            if (StrayNpcs.Count > 0)
            {
                List<Pawn> released = null;
                List<Pawn> toEvacuate = null;
                foreach (var pair in StrayNpcs)
                {
                    var pawn = pair.Key;
                    if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned)
                    {
                        released ??= new List<Pawn>();
                        released.Add(pawn);
                        continue;
                    }

                    // 已重新接战或被新 lord 收编：不再是游荡者。
                    var job = pawn.CurJob;
                    var inCombat = job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic
                        || job.def == JobDefOf.Wait_Combat);
                    if (inCombat || pawn.GetLord() != null)
                    {
                        released ??= new List<Pawn>();
                        released.Add(pawn);
                        continue;
                    }

                    if (GenTicks.TicksGame < pair.Value.DeadlineTick) continue;

                    toEvacuate ??= new List<Pawn>();
                    toEvacuate.Add(pawn);
                }

                if (released != null)
                {
                    foreach (var pawn in released) StrayNpcs.Remove(pawn);
                }
                if (toEvacuate != null)
                {
                    foreach (var pawn in toEvacuate)
                    {
                        var fromTile = StrayNpcs[pawn].FromWorldTile;
                        StrayNpcs.Remove(pawn);
                        StartEvacuationForStray(pawn, fromTile);
                    }
                }
            }
        }

        /// <summary>游荡 NPC 超时转入撤离链：当撤离者处理，排除来向 tile（防立刻传回原地图）。</summary>
        private static void StartEvacuationForStray(Pawn pawn, PlanetTile fromTile)
        {
            var map = pawn.Map;
            if (map == null) return;

            var visited = new HashSet<PlanetTile>();
            if (fromTile.Valid) visited.Add(fromTile);
            visited.Add(SeamlessTileRegistry.GetMapWorldTile(map));

            // 游荡者没有原生撤离起点。优先延续来图方向；来图已不可用时按成本选首出口。
            var direction = IntVec3.Zero;
            if (SeamlessTileGraph.TryGetMapByWorldTile(fromTile, out var fromMap)
                && SeamlessViewProjection.TryProject(fromMap, fromMap.Center, map, out IntVec3 fromCenter))
                direction = map.Center - fromCenter;
            if (!TryFindEvacuationExit(pawn, map, visited, direction, false, false, out var destSpot, out var forceExit)) return;

            var grant = Create(pawn, GrantKind.Evacuation);
            grant.VisitedTiles = visited;
            grant.EvacuationDirection = direction == IntVec3.Zero ? destSpot - pawn.Position : direction;
            grant.SelfDriven = true;
            grant.BoundSpot = destSpot;
            grant.ForceExit = forceExit;

            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Stray NPC {pawn.LabelShort} converts to evacuation on map {map.uniqueID}.");

            IssueTransitGoto(pawn, destSpot, LocomotionUrgency.Jog);
        }
    }
}
