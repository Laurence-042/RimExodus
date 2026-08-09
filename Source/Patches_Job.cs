using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 统一的跨地图 Job 拦截点。任何来源（右键菜单/未来的工作/AI）给出的 Job，
    /// 只要目标 Cell 属于另一张地图，就在这里被替换成两段式桥接方案。
    /// 同地图 Job 完全不受影响（Prefix 直接放行）。
    ///
    /// 阶段4a：玩家强制 Goto 指令额外触发边界预加载检测（仅 playerForced，避免动物级联加载）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob
    {
        public static bool Prefix(Job newJob, Pawn ___pawn)
        {
            var intercepted = SeamlessCrossMapOrders.TryInterceptJob(___pawn, newJob);

            // 玩家强制 Goto 指令：检测目标是否接近边界，若是则预加载对应邻居地块。
            // 不拦截 Job（无论是否预加载都让原 Job 正常执行），仅触发副作用。
            if (!intercepted && newJob.def == JobDefOf.Goto && newJob.targetA.IsValid)
            {
                // 诊断：记录所有 Goto 的 playerForced 状态，确认事件驱动链路。
                if (newJob.playerForced)
                {
                    if (RimExodusMod.Settings?.verboseLogging ?? false)
                        Log.Message($"[RimExodus] StartJob Goto playerForced by {___pawn?.LabelShort} -> {newJob.targetA.Cell}");
                    SeamlessBorderPreloader.CheckPawnGoto(___pawn, newJob.targetA.Cell);
                }
            }

            return !intercepted;
        }
    }
}
