using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    internal static class SeamlessCrossMapOrderDiagnostics
    {
        internal static string DescribeJob(Job job)
        {
            if (job == null) return "<null job>";
            var target = job.targetA.HasThing
                ? $"thing {job.targetA.Thing?.LabelShort ?? "<null>"} at {job.targetA.Cell}"
                : job.targetA.IsValid ? $"cell {job.targetA.Cell}" : "invalid target";
            return $"job={job.def?.defName ?? "<null>"}, driver={job.def?.driverClass?.FullName ?? "<null>"}, "
                + $"targetA={target}, playerForced={job.playerForced}, dutyTag={job.dutyTag ?? "<null>"}";
        }

        internal static bool TargetMatches(Job job, SeamlessCommandTargets.CommandTarget command)
        {
            return job != null && command != null && !job.targetA.HasThing && job.targetA.IsValid
                && job.targetA.Cell == command.cell;
        }
    }

    /// <summary>
    /// 玩家命令进入原版队列前，把 Pawn 级点击目标转交给具体 Goto Job。这里只登记，不提前执行：
    /// Shift 排队、不可打断 job 与原版预约时序保持不变。若命令被拒或只是重复当前 job，清掉绑定；
    /// 真正进入当前/排队列表的绑定由 StartJob 消费，Job.Clear 兜对象池回收。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
    public static class Patch_Pawn_JobTracker_TryTakeOrderedJob_CrossMapGoto
    {
        public static void Prefix(Job job, Pawn ___pawn, out bool __state)
        {
            var hasCommand = SeamlessCommandTargets.TryGet(___pawn, out var command);
            __state = SeamlessJobCommandTargets.TryBindGoto(___pawn, job);
            if (hasCommand && RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Cross-map TryTakeOrderedJob intake: pawn={___pawn?.LabelShort}, "
                    + $"pawnMap={___pawn?.Map?.uniqueID ?? -1}, "
                    + $"command=map {command.map?.uniqueID ?? -1} cell {command.cell}, "
                    + $"targetMatches={SeamlessCrossMapOrderDiagnostics.TargetMatches(job, command)}, "
                    + $"bound={__state}; {SeamlessCrossMapOrderDiagnostics.DescribeJob(job)}.");
        }

        public static void Postfix(Job job, Pawn ___pawn, bool __result, bool __state)
        {
            if (!__state) return;

            var tracked = IsTracked(___pawn, job);
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Cross-map TryTakeOrderedJob result: pawn={___pawn?.LabelShort}, "
                    + $"accepted={__result}, tracked={tracked}, bindingPresent={SeamlessJobCommandTargets.Contains(job)}; "
                    + $"{SeamlessCrossMapOrderDiagnostics.DescribeJob(job)}.");

            // 目标图身份已经交给 Job；Pawn 侧只服务一次菜单/选位，不能污染后续命令。
            SeamlessCommandTargets.Remove(___pawn);
            if (!__result || !tracked)
            {
                SeamlessJobCommandTargets.Remove(job);
            }
            else if (SeamlessJobCommandTargets.Contains(job))
            {
                // 仍有绑定 = 只是排队、尚未进入 StartJob；不要让邻图数字格在等待期间占住源图格。
                SeamlessJobCommandTargets.ReleaseGotoDestinationReservations(___pawn, job);
            }
        }

        private static bool IsTracked(Pawn pawn, Job job)
        {
            if (pawn?.jobs == null || job == null) return false;
            foreach (var tracked in pawn.jobs.AllJobs())
            {
                if (ReferenceEquals(tracked, job)) return true;
            }
            return false;
        }
    }

    /// <summary>Job 使用对象池；Clear 时必须移除未消费的跨图绑定，防复用对象继承旧 Map。</summary>
    [HarmonyPatch(typeof(Job), nameof(Job.Clear))]
    public static class Patch_Job_Clear_CrossMapCommandTarget
    {
        public static void Prefix(Job __instance)
        {
            SeamlessJobCommandTargets.Remove(__instance);
        }
    }

    /// <summary>
    /// 统一的 Job 生命周期钩子。原版 PawnGotoAction 仍直连 TryBridgeJob；绕过该 action、直接
    /// TryTakeOrderedJob 的第三方纯 Goto 在这里消费 Job 级目标并桥接，避免先按源图 SetupToils，
    /// 再依赖 StartPath 找回 Map。搬人任务在预约前也要先桥接，其余玩家 job 由 StartPath 兜底。
    ///
    /// 阶段4a：玩家强制 Goto 指令额外触发边界预加载检测（仅 playerForced，避免动物级联加载）。
    /// 阶段5：传送许可（Grant）生命周期——状态变更即登记/失效，见 <see cref="SeamlessTransferGrants"/>：
    /// ① 普通撤离 job（非玩家强制、非必须入队）→ 登记 Evacuation 许可；
    /// ② 任何非 TransitTag job 启动 → 旧许可失效（驱动 job 被替换/打断/完成）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob
    {
        public static bool Prefix(Job newJob, Pawn ___pawn)
        {
            var hasCommand = SeamlessCommandTargets.TryGet(___pawn, out var command);
            var hasJobBinding = SeamlessJobCommandTargets.Contains(newJob);
            if (hasCommand && RimExodusLog.Enabled(RimExodusLogModule.Transfer))
            {
                var targetMatches = SeamlessCrossMapOrderDiagnostics.TargetMatches(newJob, command);
                Log.Message($"[RimExodus] Cross-map StartJob intake: pawn={___pawn?.LabelShort}, "
                    + $"pawnMap={___pawn?.Map?.uniqueID ?? -1}, "
                    + $"command=map {command.map?.uniqueID ?? -1} cell {command.cell}, "
                    + $"targetMatches={targetMatches}, jobBinding={hasJobBinding}; "
                    + $"{SeamlessCrossMapOrderDiagnostics.DescribeJob(newJob)}.");
                if (targetMatches && !hasJobBinding)
                    Log.Message($"[RimExodus] Cross-map StartJob has no Job binding: {___pawn?.LabelShort} "
                        + $"started {newJob?.def?.defName ?? "<null>"} directly for the registered target; "
                        + "execution now depends on that Job reaching the shared StartPath fallback.");
            }

            // Grant 生命周期：非 TransitTag job 启动 = 旧许可的驱动 job 已被替换/打断 → 失效清除。
            // TransitTag 是我们下发 Goto 的自标识（许可驱动 job 重入本钩子，不清除）。
            if (newJob.dutyTag != SeamlessTransferGrants.TransitTag)
            {
                if ((RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    && SeamlessTransferGrants.TryGet(___pawn, out var cleared))
                {
                    Log.Message($"[RimExodus] Grant cleared: {___pawn?.LabelShort} ({cleared.Kind}) "
                        + $"replaced by job {newJob.def?.defName}.");
                }
                SeamlessTransferGrants.Remove(___pawn);
            }

            if (SeamlessPawnPickupOrders.HasForeignTarget(___pawn, newJob))
            {
                SeamlessPawnPickupOrders.BridgeOrReject(___pawn, newJob);
                return false;
            }

            // Achtung 等命令提供者绕过 PawnGotoAction，直接把邻图局部格写入 playerForced Goto。
            // 此时 Map 身份已在 TryTakeOrderedJob 绑定到这个具体 Job；在任何第三方 Goto toil
            // （如 Alien 的攀爬 initAction）按源图验证前转换为 Bridge。TryTakeOrderedJob 已让
            // JobDriver_Goto 在源图登记目的地预约；只在桥接成功后显式跨图释放，再回池原 Job。
            // 失败则保留预约并完整放行原版，不持有未启动 Job。
            if (SeamlessJobCommandTargets.TryConsume(newJob, out var boundGoto))
            {
                SeamlessCommandTargets.Remove(___pawn);
                var bindingValid = newJob.def == JobDefOf.Goto && newJob.playerForced && ___pawn?.Spawned == true
                    && !newJob.targetA.HasThing && newJob.targetA.IsValid
                    && newJob.targetA.Cell == boundGoto.Cell && boundGoto.Map != ___pawn.Map
                    && SeamlessBoundaryRules.IsCrossMapOrderable(___pawn)
                    && SeamlessViewProjection.TryProject(boundGoto.Map, boundGoto.Cell, ___pawn.Map, out _);
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Cross-map Job binding consumed: pawn={___pawn?.LabelShort}, "
                        + $"bound=map {boundGoto.Map?.uniqueID ?? -1} cell {boundGoto.Cell}, "
                        + $"validForBridge={bindingValid}; {SeamlessCrossMapOrderDiagnostics.DescribeJob(newJob)}.");
                if (bindingValid)
                {
                    var continuation = SeamlessGotoContinuation.Capture(newJob);
                    if (SeamlessCrossMapOrders.TryBridgeJob(___pawn, boundGoto.Map, boundGoto.Cell,
                            gotoContinuation: continuation))
                    {
                        if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                            Log.Message($"[RimExodus] Cross-map bound goto: {___pawn.LabelShort} -> "
                                + $"{boundGoto.Cell} on map {boundGoto.Map.uniqueID}; bridged before toil setup.");
                        // 不调用 ClearReservationsForJob：它还会清该 pawn 的全部 enroute 预约，可能
                        // 影响队列中无关工作。这里只释放这个被替换 Goto 的 destination 声明。
                        SeamlessJobCommandTargets.ReleaseGotoDestinationReservations(___pawn, newJob);
                        JobMaker.ReturnToPool(newJob);
                        return false;
                    }
                    if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                        Log.Message($"[RimExodus] Cross-map bound goto bridge rejected: {___pawn?.LabelShort} -> "
                            + $"{boundGoto.Cell} on map {boundGoto.Map.uniqueID}; original Goto will continue.");
                }
            }

            // 按离图目的登记：玩家直接下令撤离，以及必须加入远行队的自动离图任务，均交还原版。
            // failIfCantJoinOrCreateCaravan 是原版的入队契约（护卫机械族与公共自动入队子树共用）：
            // 队伍不可加入时应取消任务。若登记 Evacuation，跨图续程会丢失这项失败条件，
            // 把随行者当成普通撤离者一路赶出已加载区域。勿按物种/giver 类型或当前有无队伍特判。
            // 普通撤离（撤离 duty/囚犯越狱/野性恐慌/释放访客）仍跨过已加载邻图继续跑。
            // 清理在登记前——天然处理 job 重发/队列复用（先清旧再建新）。
            if (newJob.exitMapOnArrival && !newJob.playerForced
                && !newJob.failIfCantJoinOrCreateCaravan)
            {
                SeamlessTransferGrants.RegisterEvacuation(___pawn, newJob);
            }

            // JobDriver_Goto 的同格 GotoCell 会在 StartJob 方法体内立即跳过移动 toil；没有
            // TryEnterNextPathCell 事件可供踩点触发。许可已经登记、但旧 job 尚未启动时先分派：
            // 成功跨图/离场则跳过原方法体，避免它继续给已换图的 pawn 安装旧地图 job。
            if (newJob.def == JobDefOf.Goto && ___pawn?.Spawned == true
                && newJob.targetA.IsValid && newJob.targetA.Cell == ___pawn.Position
                && SeamlessMapTransferTrigger.TryTriggerStationaryGoto(___pawn))
            {
                JobMaker.ReturnToPool(newJob);
                return false;
            }

            // 沉浸模式（seamExitBandEnabled=false，2026-08）：玩家征召 job 的 exitMapOnArrival 清除
            // ——玩家下令走到接缝带格不再到站原生离场成远行队（隐藏接缝带后误触撤离比看不见边线
            // 严重得多）。只清 playerForced job：NPC 撤离链（上面的 grant 登记）不门控（用户定夺）。
            // job 落到普通移动语义（到站即 Wait），无副作用。
            if (newJob.exitMapOnArrival && newJob.playerForced && !SeamExitBandGating.Enabled)
            {
                newJob.exitMapOnArrival = false;
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Immersive mode: cleared exitMapOnArrival on player-forced job {newJob.def?.defName} for {___pawn?.LabelShort}.");
            }

            // 玩家强制 Goto 指令：检测目标是否接近边界，若是则预加载对应邻居地块。
            // 不拦截 Job（无论是否预加载都让原 Job 正常执行），仅触发副作用。
            if (newJob.def == JobDefOf.Goto && newJob.targetA.IsValid && newJob.playerForced)
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] StartJob Goto playerForced by {___pawn?.LabelShort} -> {newJob.targetA.Cell}");
                SeamlessBorderPreloader.CheckPawnGoto(___pawn, newJob.targetA.Cell);
            }

            return true;
        }
    }

    /// <summary>
    /// RimExodus 跨缝中转沿用原版 Goto，避免 VF 等系统依赖 JobDefOf.Goto 的契约被自定义 JobDef
    /// 打断；但原版 Goto 会在 Notify_JobStarted 中让已飞行 Pawn 强制降落。仅对 TransitTag job
    /// 保持其已经存在的飞行状态，使选点时的 Flying 代价场与段内实际移动模式一致。地面 Pawn
    /// 不会因此起飞，普通 Goto 也完全走原版。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.Notify_JobStarted))]
    public static class Patch_Pawn_FlightTracker_Notify_JobStarted
    {
        public static bool Prefix(Pawn_FlightTracker __instance, Job job)
        {
            if (job?.dutyTag != SeamlessTransferGrants.TransitTag || !__instance.Flying)
            {
                return true;
            }

            job.flying = true;
            return false;
        }
    }
}
