using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跟随类 giver 的跨图拦截（2026-09 修复"Escort 机械族 FollowClose 一 tick 10 job 循环"）：
    /// Follow/FollowClose 的 JobDriver 无法跨图预约/寻路——与 AttackMelee 同教训（见
    /// Patches_CombatTargetSearch 的 AIFightEnemy Prefix 注），跨图目标必须在 giver 层拦截，
    /// 不得原生下发。原版基类 JobGiver_AIFollowPawn.TryGiveJob 的门是 CanReach(followee)——
    /// 战斗链接缺失时（跨图战斗开关关闭 / overseer 跨 2 图非邻接）放行原版，邻图坐标被当本图
    /// 坐标可查出可达 → FollowClose 下发 → JobDriver 瞬时失败 → 同 tick 重发循环。
    ///
    /// patch 基类一次覆盖全部派生（JobGiver_AIFollowOverseer 覆写 TryGiveJob 但调 base）：
    /// 【Prefix】followee 在邻图且可桥接 → 产出结构化推进 Goto(followee)（追击样板）——
    /// StartPath 包装按 IsNpcApproachGoto 结构判据桥接过缝，落地续跑 Goto，抵达后 think
    /// 原生恢复 FollowClose。非邻接 / 不可桥接 / duty 语境 → null（站桩自愈，杀循环）。
    /// 【Postfix 安全网】任何漏网的原生跨图 Follow/FollowClose（反射失败/未知派生/第三方
    /// giver）同口径转换——失败循环的确定性终结层。
    ///
    /// duty 语境（NPC 随从）不走本链：ThinkNode_Duty 会把 think tree 产出的 job.dutyTag 无条件
    /// 覆写为 duty.tag（2026-08 实证，见 StartPath 包装 Prefix 注），桥接自标识不安全；NPC 随从的
    /// 跨图跟随由 NotifyPawnTransferred 的 MarkFollowers 瞬间扫描覆盖（直接 StartJob，无抹除窗口）。
    /// 机械族/宠物 think 树无 duty，结构化 Goto 经 IsNpcApproachGoto 识别不依赖 dutyTag，安全。
    /// 邻接判定用几何投影（SeamlessViewProjection）而非 TryGetCombatLink：跟随不是战斗语义，
    /// 不应被跨图战斗开关一并关死（StartPath 包装的对应放行见其 Prefix 内注释）。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIFollowPawn), "TryGiveJob")]
    public static class Patch_JobGiver_AIFollowPawn_TryGiveJob_CrossMap
    {
        private static readonly Dictionary<Type, MethodInfo> FolloweeGetters = new Dictionary<Type, MethodInfo>();

        /// <summary>
        /// 解析 giver 的 followee（protected abstract GetFollowee，按实例类型缓存 MethodInfo；
        /// 调用带 try/catch——第三方派生实现异常时安全降级）。解析失败回落 GetOverseer()：
        /// 机械族跟随 overseer 是本修复的主案，反射通道失灵也保住它。
        /// </summary>
        private static Pawn TryGetFollowee(JobGiver_AIFollowPawn giver, Pawn pawn)
        {
            try
            {
                if (giver != null)
                {
                    var type = giver.GetType();
                    if (!FolloweeGetters.TryGetValue(type, out var method))
                    {
                        method = AccessTools.Method(type, "GetFollowee");
                        FolloweeGetters[type] = method;
                    }
                    if (method != null) return method.Invoke(giver, new object[] { pawn }) as Pawn;
                }
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[RimExodus] Follow giver reflection failed, vanilla follow kept: " + ex, 79103001);
            }
            return pawn?.GetOverseer();
        }

        public static bool Prefix(JobGiver_AIFollowPawn __instance, Pawn pawn, ref Job __result)
        {
            if (pawn?.Map == null || pawn.Map.Disposed || !pawn.Spawned) return true;
            var followee = TryGetFollowee(__instance, pawn);
            if (followee == null || followee.Destroyed || !followee.Spawned || followee.Map == null) return true;
            if (followee.Map == pawn.Map) return true; // 同图：原生 Follow/FollowClose

            // 跨图 followee：三种"不跟随"都返回 null（诚实不可达，杀循环）。
            // ① 非邻接（多跳外）——原版 CanReach 对此放行混框架计算，正是循环入口之一；
            if (!SeamlessViewProjection.TryProject(followee.Map, IntVec3.Zero, pawn.Map, out _))
            {
                __result = null;
                return false;
            }
            // ② duty 语境的 NPC 随从——ThinkNode_Duty 抹 dutyTag，桥接 job 不安全；瞬间扫描链负责；
            if (pawn.mindState?.duty?.def != null)
            {
                __result = null;
                return false;
            }
            // ③ 不可桥接（本图无可达传送点）——站桩，缝开/目标折返后自愈。
            if (!SeamlessCrossMapOrders.CanBridgeTo(pawn, followee.Map))
            {
                __result = null;
                return false;
            }

            __result = MakeApproachJob(followee);
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Cross-map follow approach: {pawn.LabelShort} -> {followee.LabelShort} on map {followee.Map.uniqueID}.");
            return false;
        }

        /// <summary>
        /// 安全网：原生（或 Prefix 放行的）结果若是跨图目标的 Follow/FollowClose，按同口径转换。
        /// Prefix 各分支的产出（推进 Goto / null）都不满足本条件，不会二次转换。
        /// </summary>
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            var job = __result;
            if (job == null || (job.def != JobDefOf.Follow && job.def != JobDefOf.FollowClose)) return;
            if (pawn?.Map == null) return;
            var followee = job.targetA.Thing;
            if (followee == null || followee.Map == null || followee.Map == pawn.Map) return;

            var bridgeable = SeamlessViewProjection.TryProject(followee.Map, IntVec3.Zero, pawn.Map, out _)
                && pawn.mindState?.duty?.def == null
                && SeamlessCrossMapOrders.CanBridgeTo(pawn, followee.Map);
            __result = bridgeable ? MakeApproachJob(followee) : null;

            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Follow safety net: converted cross-map {job.def.defName} for {pawn.LabelShort} "
                    + $"(bridgeable={bridgeable}).");
        }

        /// <summary>
        /// 结构化推进 job（对齐 AIFightEnemy 跨图近战推进的参数）：Goto(followee Thing) 非
        /// playerForced，StartPath 包装按 IsNpcApproachGoto 桥接；expiry 让长路中途重评
        /// （目标折返/到位后 think 原生接管），checkOverrideOnExpiry 保证过期即重评。
        /// </summary>
        private static Job MakeApproachJob(Thing followee)
        {
            var job = JobMaker.MakeJob(JobDefOf.Goto, followee);
            job.expiryInterval = Rand.Range(360, 480);
            job.checkOverrideOnExpire = true;
            job.collideWithPawns = true;
            return job;
        }
    }
}
