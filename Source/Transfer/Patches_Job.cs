using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 统一的 Job 生命周期钩子（跨图移动不再在此拦截——2026-08 移除 pending 机制：
    /// 跨图 Goto 由浮菜单选项 action 直连 SeamlessCrossMapOrders.TryBridgeJob 下发，
    /// 本钩子不消费任何登记）。
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
