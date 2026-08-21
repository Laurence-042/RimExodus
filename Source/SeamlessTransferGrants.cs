using System.Collections.Generic;
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
            /// <summary>玩家跨图 goto 桥接（殖民者/殖民地机械族/驯养动物，行为表行 3/10/55）。</summary>
            Bridge,

            /// <summary>NPC 撤离链（撤离 duty/囚犯越狱/野性恐慌/释放访客，行为表行 19/27/35/41/47/53）。</summary>
            Evacuation,

            /// <summary>跨图追击（NPC 战斗体近战追击目标刚跨图，行为表行 15/23/31）。</summary>
            Pursue,

            /// <summary>跟随跨图（驯养动物/NPC 随从的 Follow job 目标刚跨图，行为表行 60）。</summary>
            Follow
        }

        internal sealed class Grant
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

            /// <summary>Evacuation：撤离链已访问的世界 tile（方向性 + 防回弹），跨 hop 传递复用。</summary>
            public HashSet<int> VisitedTiles;

            /// <summary>从原撤离 job 复制的移动速度档，续程沿用。</summary>
            public LocomotionUrgency Locomotion = LocomotionUrgency.Jog;

            /// <summary>驱动 job 是否为我们下发的 TransitTag Goto（原版 flag job 驱动 = false）。</summary>
            public bool SelfDriven;

            /// <summary>只剩已访问出口可用：踩点直接原生离场（撤离链终止兜底）。</summary>
            public bool ForceExit;

            public int CreatedTick = GenTicks.TicksGame;
        }

        private static readonly Dictionary<Pawn, Grant> Grants = new Dictionary<Pawn, Grant>();

        /// <summary>
        /// 游荡 NPC 兜底：Pursue/Follow 落地的 NPC（传送时 lord 已被 Notify_PawnLost 剥离、无 duty），
        /// 若对端无战斗目标会永久滞留。宽限期内重新接战/有 lord → 解除；超时仍无 → 转入撤离链跑出世界。
        /// FromWorldTile = 其来向 tile，转入撤离链时排除（防立刻传回原地图）。
        /// </summary>
        private struct StrayInfo
        {
            public int DeadlineTick;
            public int FromWorldTile;
        }

        private static readonly Dictionary<Pawn, StrayInfo> StrayNpcs = new Dictionary<Pawn, StrayInfo>();

        private const int StrayGraceTicks = 600;

        /// <summary>Grant 超时兜底（正常生命周期由 StartJob 的 job 替换清理；此值只兜"驱动 job 长期挂起"，
        /// 须容纳 250 图全程步行续程：~3 格/秒 × 6000 ticks ≈ 100 游戏秒）。</summary>
        private const int GrantTimeoutTicks = 6000;

        private static ThingDef _enterSpotDef;

        private static ThingDef EnterSpotDef
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

        /// <summary>清除 pawn 的许可（传送消费 / 驱动 job 被替换）。</summary>
        internal static void Remove(Pawn pawn)
        {
            if (pawn != null) Grants.Remove(pawn);
        }

        /// <summary>
        /// NPC 撤离 job 登记（StartJob 钩子调用）：撤离 duty / 囚犯越狱 / 野性恐慌 / 释放访客——
        /// exitMapOnArrival 且非 playerForced（playerForced 精确区分玩家征召 goto：
        /// 原版 5 个 flag 赋值点中只有 DraftedMove.PawnGotoAction 走 TryTakeOrderedJob 设 playerForced）。
        /// 首跳不绑具体点：首个踩到的传送点，对端已加载 → 传送续链；对端未生成 → 交还原生撤离。
        /// </summary>
        internal static void RegisterEvacuation(Pawn pawn, Job evacJob)
        {
            if (pawn == null || pawn.Map == null) return;
            var grant = Create(pawn, GrantKind.Evacuation);
            grant.Locomotion = evacJob.locomotionUrgency;
            grant.VisitedTiles = new HashSet<int> { SeamlessTileRegistry.GetMapWorldTile(pawn.Map) };
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
                    ContinueBridgeMove(pawn, arrivalMap, grant);
                    break;
                case GrantKind.Evacuation:
                    ContinueEvacuationChain(pawn, departureMap, arrivalMap, grant);
                    break;
                case GrantKind.Pursue:
                case GrantKind.Follow:
                    // 落地 NPC 无 lord/duty：驯养动物留在主人身边无需兜底；敌对追击者立即并入/新建
                    // 袭击 lord（否则原版 think tree 对无 lord 敌对 pawn 的 JobGiver_ExitMap 分支会在
                    // 下一 think tick 直接离场——被 Patches_RCellFinder 重定向到脚下传送点格后原生
                    // 离图，即 2026-08 实测"追击者传送后消失"的根因）；其余登记游荡宽限。
                    if (SeamlessBoundaryRules.IsColonyAnimal(pawn)) break;
                    if (grant.Kind == GrantKind.Pursue && TryAttachAssaultLord(pawn, arrivalMap)) break;
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

        /// <summary>
        /// 敌对追击者落地的袭击 lord 接管（2026-08 修复"追击者传送后消失"）：
        /// <see cref="SeamlessMapTransfer.TryTransferPawn"/> 剥离原 lord（跨图本不能留在旧图 lord），
        /// 落地成为无 lord 敌对 pawn——原版 think tree 的 JobGiver_ExitMap 分支会给它离场 job
        /// （堆栈实测：JobGiver_ExitMap → TryFindBestExitSpot → 我们的重定向 → 脚下传送点格 → 原生离图）。
        /// 修复：敌对追击者落地即并入/新建 AssaultColony lord，原版袭击 AI 完整接管追杀
        /// （canKidnap/canSteal 关闭——追击语义不是抄家；canTimeoutOrFlee 保留超时放弃）。
        /// 目标再次跨图时 MarkPursuers 的 job 判据仍成立（AttackMelee/AttackStatic 来自 lord duty），
        /// 追击链可跨多图延续；同批落地（600 ticks 内）同图同阵营的追击者编入同一 lord。
        /// 盟友追击兵不建袭击 lord（对玩家阵营敌对性不成立），维持游荡宽限兜底。
        /// </summary>
        private static readonly Dictionary<int, (Faction faction, Lord lord, int createdTick)> RecentAssaultLords
            = new Dictionary<int, (Faction, Lord, int)>();

        private static bool TryAttachAssaultLord(Pawn pawn, Map arrivalMap)
        {
            var faction = pawn.Faction;
            if (faction == null || !pawn.HostileTo(Faction.OfPlayer)) return false;

            if (RecentAssaultLords.TryGetValue(arrivalMap.uniqueID, out var recent)
                && recent.faction == faction && GenTicks.TicksGame - recent.createdTick < 600
                && recent.lord != null && recent.lord.lordManager != null
                && recent.lord.ownedPawns.Count > 0 && recent.lord.faction == faction)
            {
                recent.lord.AddPawn(pawn);
                return true;
            }

            var lord = LordMaker.MakeNewLord(faction,
                new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: true,
                    sappers: false, useAvoidGridSmart: true, canSteal: false),
                arrivalMap, new[] { pawn });
            RecentAssaultLords[arrivalMap.uniqueID] = (faction, lord, GenTicks.TicksGame);

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Pursuit lord: {pawn.LabelShort} lands on map {arrivalMap.uniqueID} "
                    + "and joins a new AssaultColony lord (cross-map chase continues).");
            return true;
        }

        /// <summary>
        /// Bridge 续程：跨图落地后继续执行。NextJob 非空（StartPath 跨图包装路径）→ 直接续跑原 job
        ///（传送后 pawn 已在目标图，job 目标变同图，原生 driver 接管）；否则走 FinalDest 的 Goto
        /// 续程（PawnGotoAction 桥接路径，移动语义）。
        /// </summary>
        private static void ContinueBridgeMove(Pawn pawn, Map arrivalMap, Grant grant)
        {
            if (grant.NextJob != null)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Bridge continuation: resuming wrapped job {grant.NextJob.def.defName} "
                        + $"for {pawn.LabelShort} on map {arrivalMap.uniqueID}.");
                pawn.jobs.StartJob(grant.NextJob, JobCondition.InterruptForced);
                return;
            }
            if (grant.FinalDestMap != arrivalMap) return;
            var finalCell = grant.FinalDestCell;
            if (!finalCell.InBounds(arrivalMap) || !finalCell.Walkable(arrivalMap))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Bridge continuation skipped: final cell {finalCell} on map {arrivalMap.uniqueID} is not walkable.");
                return;
            }
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, finalCell), JobCondition.InterruptForced);
        }

        /// <summary>
        /// 撤离链续程（行为表待办②方向性）：在对端地图选一个远离来向接缝的出口——
        /// 排除 VisitedTiles（来向 + 已访问边，防回弹/横跳），优先对端未加载的边（到达即原生离场，链终止最快），
        /// 组内取最近可达 spot。无未访问出口 → 回退最近可达 spot + ForceExit（踩点直接原生离场）。
        /// 续程 job 不带 exitMapOnArrival：传送落点本身常是出口格，flag job 的 pre-tick IsExitCell
        /// 检查会在落地瞬间原生离场，破坏"继续跑"；离场动作改由踩点分派（SelfDriven + !hasArrival → ExitMap）。
        /// </summary>
        private static void ContinueEvacuationChain(Pawn pawn, Map departureMap, Map arrivalMap, Grant grant)
        {
            var visited = grant.VisitedTiles ?? new HashSet<int>();
            visited.Add(SeamlessTileRegistry.GetMapWorldTile(departureMap));
            visited.Add(SeamlessTileRegistry.GetMapWorldTile(arrivalMap));

            if (!TryFindEvacuationExit(pawn, arrivalMap, visited, out var destSpot, out var forceExit))
            {
                // 极端兜底：本图无任何可达出口（孤立地形）。不登记，交还原版 think tree；
                // Grant 已消费，后续 job 启动不会误触发。
                return;
            }

            var next = Create(pawn, GrantKind.Evacuation);
            next.VisitedTiles = visited;
            next.Locomotion = grant.Locomotion;
            next.SelfDriven = true;
            next.BoundSpot = destSpot;
            next.ForceExit = forceExit;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Evacuation chain: {pawn.LabelShort} continues on map {arrivalMap.uniqueID} "
                    + $"toward exit {destSpot} (forceExit={forceExit}, visited={visited.Count}).");

            IssueTransitGoto(pawn, destSpot, grant.Locomotion);
        }

        /// <summary>
        /// 在 map 上为撤离链选出口 spot。
        /// 候选过滤（按优先级）：① 对端未加载的边（未访问）；② 对端已加载的边（未访问，链继续）；
        /// ③ 无过滤（只剩已访问边，forceExit=true）。各级内取距 pawn 最近且 pawn 可达的 spot。
        /// </summary>
        private static bool TryFindEvacuationExit(Pawn pawn, Map map, HashSet<int> visited, out IntVec3 destSpot, out bool forceExit)
        {
            destSpot = IntVec3.Invalid;
            forceExit = false;
            var spotDef = EnterSpotDef;
            if (spotDef == null) return false;

            var candidates = new List<(IntVec3 cell, int tile, int distSq)>();
            foreach (var thing in map.listerThings.ThingsOfDef(spotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;
                candidates.Add((thing.Position, comp.targetWorldTile, pawn.Position.DistanceToSquared(thing.Position)));
            }
            if (candidates.Count == 0) return false;
            candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            var traverseParms = TraverseParms.For(pawn);
            for (var tier = 0; tier < 3; tier++)
            {
                foreach (var (cell, tile, _) in candidates)
                {
                    if (tier < 2 && visited.Contains(tile)) continue;
                    if (tier == 0 && SeamlessTileGraph.TryGetMapByWorldTile(tile, out _)) continue;
                    if (!map.reachability.CanReach(pawn.Position, cell, PathEndMode.OnCell, traverseParms)) continue;

                    destSpot = cell;
                    forceExit = tier == 2;
                    return true;
                }
            }
            return false;
        }

        /// <summary>下发 TransitTag Goto（Grant 驱动 job）。dutyTag 自标识使 StartJob 钩子不清除对应 Grant。</summary>
        private static void IssueTransitGoto(Pawn pawn, IntVec3 spotCell, LocomotionUrgency locomotion)
        {
            var job = JobMaker.MakeJob(JobDefOf.Goto, spotCell);
            job.dutyTag = TransitTag;
            job.locomotionUrgency = locomotion;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
        }

        /// <summary>
        /// 追击者扫描（行为表行 15/23/31，待办④）：目标刚跨图，出发地图上正追击它的 NPC 战斗体
        /// 借同一传送点跨图追击（玩家不能靠跨图甩掉追兵）。
        /// 意图判据（2026-08 扩展）：①AttackMelee/AttackStatic job 目标=跨图者（交战瞬间）；
        /// ②mindState.enemyTarget=跨图者（**走位 Goto 期间持续有效**——追击的大多数时刻 job 不是
        /// 两个 Attack 之一，只认 job 会漏掉正在赶路的追击者）。远程统一门槛：还能从原地跨缝
        /// 射击（TryGetAttackVerb + CanHitTarget 走我们的跨图射击线）→ 留在原地继续打，打不着才传送。
        /// 闲逛/工作/无 flag 逃跑仍不扫（行为表"无事"）；狂猎动物不属战斗体（表无此行）。
        /// </summary>
        private static void MarkPursuers(Pawn target, Map departureMap, Thing spot)
        {
            foreach (var other in departureMap.mapPawns.AllPawns)
            {
                if (other == target || !other.Spawned || other.Downed || other.Dead) continue;
                if (!SeamlessBoundaryRules.IsNpcCombatant(other)) continue;
                var job = other.CurJob;
                var jobPursuing = job != null
                    && ((job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic) && job.targetA.Thing == target);
                var enemyTargetPursuing = other.mindState?.enemyTarget == target;
                if (!jobPursuing && !enemyTargetPursuing) continue;

                // 还能从原地跨缝射击：留在原地继续打（跨图索敌自然维持目标），不传送。
                var verb = other.TryGetAttackVerb(target, true);
                if (verb != null && verb.CanHitTarget(new LocalTargetInfo(target))) continue;

                IssueTransitGoto(other, spot.Position, LocomotionUrgency.Jog);
                var grant = Create(other, GrantKind.Pursue);
                grant.BoundSpot = spot.Position;

                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Pursuit grant: {other.LabelShort} chases {target.LabelShort} across map {departureMap.uniqueID}.");
            }
        }

        /// <summary>
        /// 跟随者扫描（行为表行 60 + 商队随从）：跟随目标（Follow/FollowClose job 指向跨图者）的 pawn
        /// 借同一传送点跟随跨图。驯养动物与 NPC 随从（商队 carrier，防商队过缝解体）统一覆盖。
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

                IssueTransitGoto(other, spot.Position, LocomotionUrgency.Jog);
                var grant = Create(other, GrantKind.Follow);
                grant.BoundSpot = spot.Position;

                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Follow grant: {other.LabelShort} follows {leader.LabelShort} across map {departureMap.uniqueID}.");
            }
        }

        /// <summary>
        /// 周期清扫（锚点地图 MapComponentTick 调用）：
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
        private static void StartEvacuationForStray(Pawn pawn, int fromTile)
        {
            var map = pawn.Map;
            if (map == null) return;

            var visited = new HashSet<int>();
            if (fromTile >= 0) visited.Add(fromTile);

            if (!TryFindEvacuationExit(pawn, map, visited, out var destSpot, out var forceExit)) return;

            var grant = Create(pawn, GrantKind.Evacuation);
            grant.VisitedTiles = visited;
            grant.SelfDriven = true;
            grant.BoundSpot = destSpot;
            grant.ForceExit = forceExit;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Stray NPC {pawn.LabelShort} converts to evacuation on map {map.uniqueID}.");

            IssueTransitGoto(pawn, destSpot, LocomotionUrgency.Jog);
        }
    }
}
