using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 撤离首跳「带内圈提前离场」修复（2026-09-06，用户报告"敌人撤退时消失在两边都有地图的接缝上"）。
    ///
    /// 根因：exit grid 拓宽到接缝带全 3 圈（<see cref="Patches_ExitMapGrid"/> 所在文件，2026-08 定夺）
    /// 后，原版 <see cref="JobDriver_Goto"/> 的 pre-tick 检查（MakeNewToils 里的逐 tick 动作：
    /// exitMapOnArrival 且 IsExitCell(当前位置) 即 TryExitMap）使从图内出发的撤离者在踏入带内圈的
    /// 第一格就 despawn——传送点只铺离散边圈∪带外圈（带内圈无点），踩点分派
    /// （<see cref="SeamlessMapTransferTrigger.HandleEvacuation"/>：对端已加载→传送续链 /
    /// 未加载→原生离场）对首跳结构性不可达，撤离者与对侧是否有图无关地在缝前一圈凭空消失
    /// （AGENTS.md"NPC 离场链全景"2026-09-01 旧定案"实测主路径 = 带内圈原生离场"即此机制，本修复取代之）。
    ///
    /// 修复 = TryExitMap（pre-tick 与到站检查的唯一汇聚点，private void）Prefix 对【持有 Evacuation
    /// 许可、正走向传送点、尚未站在传送点上】的 pawn 推迟原生离场：让其走到 spot 交给踩点分派——
    /// 对端已加载 → 传送续链（双图缝上可见地跨缝继续跑，主计划理想场景）；对端未加载/休眠 →
    /// 在缝线上原生离场（Q4"不唤醒"决议不变，仅离场点从带内圈挪到缝线）。
    ///
    /// 安全网（全部放行原版，勿删）：无许可 / 非 Evacuation 许可（玩家 pawn 不登记、机械族例外
    /// 无 flag——原版语义零变化）；已站在传送点上（tier0 原生交接与"传送被拒后 flag job 的到站
    /// 兜底"都依赖此处离场）；job 目标非传送点（出口重定向失败放行原版方形边格的兜底路径）。
    /// 许可 6000t 超时兜底（<see cref="SeamlessTransferGrants.TickSweep"/>）保证推迟态不可能永久卡死。
    /// 已知小边界（接受）：撤离 job 下发时人恰好已站在 spot 上 → 原地缝上离场，不做即席分派；
    /// JobDriver_ExitMapFlying（飞行离场）不经 JobDriver_Goto，维持原版（观察项）。
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_Goto), "TryExitMap")]
    static class Patch_JobDriver_Goto_TryExitMap_EvacuationDefer
    {
        static bool Prefix(JobDriver_Goto __instance)
        {
            var pawn = __instance.pawn;
            var map = pawn?.Map;
            if (map == null) return true;

            if (!SeamlessTransferGrants.TryGet(pawn, out var grant)
                || grant.Kind != SeamlessTransferGrants.GrantKind.Evacuation)
            {
                return true; // 玩家 pawn / 无撤离许可：原版语义零变化。
            }

            // 已站在传送点上：放行——tier0 原生交接（HandleEvacuation 的"JobDriver pre-tick 在此
            // 出口格原生撤离"注释）与传送被拒后 flag job 的到站兜底都从这里离场。
            if (SeamlessMapTransferTrigger.HasEnterSpotAt(map, pawn.Position)) return true;

            // 目标是传送点格（出口重定向成功）且人未到：推迟原生离场，让 pawn 走到 spot 交给
            // 踩点分派（进入首格传送点时：传送续链 / 缝上原生离场，见 HandleEvacuation）。
            if (__instance.job.exitMapOnArrival
                && SeamlessMapTransferTrigger.HasEnterSpotAt(map, __instance.job.targetA.Cell))
            {
                if (GenTicks.TicksGame - grant.LastDeferLogTick >= 600)
                {
                    grant.LastDeferLogTick = GenTicks.TicksGame;
                    if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                        Log.Message($"[RimExodus] [caravan-exit] Evacuation exit deferred: {pawn.LabelShort} at {pawn.Position} "
                            + $"on map {map.uniqueID} — en route to enter spot {__instance.job.targetA.Cell}, vanilla exit suppressed until spot.");
                }
                return false;
            }

            // 重定向失败（目标为原版边格）等其余情形：放行，保住"总能离场"安全网。
            return true;
        }
    }
}
