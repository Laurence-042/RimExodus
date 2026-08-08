using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 统一的跨地图 Job 拦截点。任何来源（右键菜单/未来的工作/AI）给出的 Job，
    /// 只要目标 Cell 属于另一张地图，就在这里被替换成两段式桥接方案。
    /// 同地图 Job 完全不受影响（Prefix 直接放行）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob
    {
        public static bool Prefix(Job newJob, Pawn ___pawn)
        {
            return !SeamlessCrossMapOrders.TryInterceptJob(___pawn, newJob);
        }
    }
}
