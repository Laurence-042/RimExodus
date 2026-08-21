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
    /// ① NPC 撤离 job（exitMapOnArrival 且非 playerForced）→ 登记 Evacuation 许可；
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
                if ((RimExodusMod.Settings?.verboseLogging ?? false)
                    && SeamlessTransferGrants.TryGet(___pawn, out var cleared))
                {
                    Log.Message($"[RimExodus] Grant cleared: {___pawn?.LabelShort} ({cleared.Kind}) "
                        + $"replaced by job {newJob.def?.defName}.");
                }
                SeamlessTransferGrants.Remove(___pawn);
            }

            // NPC 撤离 job 登记（撤离 duty/囚犯越狱/野性恐慌/释放访客）。
            // playerForced 精确区分玩家征召 goto（TryTakeOrderedJob 统一设 true）：
            // 玩家撤离不登记 → 踩传送点走原生撤离/组队（行为表行 1）。
            // 清理在登记前——天然处理 job 重发/队列复用（先清旧再建新）。
            if (newJob.exitMapOnArrival && !newJob.playerForced)
            {
                SeamlessTransferGrants.RegisterEvacuation(___pawn, newJob);
            }

            // 玩家强制 Goto 指令：检测目标是否接近边界，若是则预加载对应邻居地块。
            // 不拦截 Job（无论是否预加载都让原 Job 正常执行），仅触发副作用。
            if (newJob.def == JobDefOf.Goto && newJob.targetA.IsValid && newJob.playerForced)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] StartJob Goto playerForced by {___pawn?.LabelShort} -> {newJob.targetA.Cell}");
                SeamlessBorderPreloader.CheckPawnGoto(___pawn, newJob.targetA.Cell);
            }

            return true;
        }
    }
}
