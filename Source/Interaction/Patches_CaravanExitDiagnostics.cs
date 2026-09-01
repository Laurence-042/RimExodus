using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 远行队出口链诊断插桩（2026-08 "你的远行队无法离开此区域"排查，verbose 门控，定位后保留）。
    ///
    /// 排查结论（2026-08 二次定稿）：报错出自组队界面**路线规划**——`Dialog_FormCaravan.Notify_ChoseRoute →
    /// BestExitTileToGoTo → AvailableExitTilesAt`（"每方向有无可达出口"枚举）→ 0 tiles → startingTile
    /// 无效 → Send 时 CheckForErrors 弹 "MessageNoValidExitTile"（与 TryFindClosestEdgeCellTo 无关，
    /// CheckForErrors 失败即 return，走不到 Send 主门）。AvailableExitTilesAt 内部走 5 参（Rot4）
    /// `CellFinder.TryFindRandomEdgeCellWith` 的**原版方形边路径**——原版候选格钉死方形边，仅未倾斜图
    /// （六边形角点恰好轴对齐触到方形边）靠接触格侥幸通过。
    /// 修复 = 5 参重载接缝化（`Patch_CellFinder_TryFindRandomEdgeCellWithRot4`，Patches_CellFinder.cs，
    /// RimExodusMod 手动绑定）。历史教训勿再回退：首版曾据"未倾斜图方向枚举正常（4/6 tiles）"回退为
    /// 纯诊断——该实测只在角点触边的图上成立，倾斜图（顶点朝向来自世界球面，θ≠0 mod 30°，方形边
    /// 整圈 void）六方向必然全灭。
    /// 本文件保留 AvailableExitTilesAt 的结果计数（回归时确认方向枚举非空）；5 参调用的 verbose
    /// 明细由 Prefix 自带（成功无日志、全灭打 seam pool exhausted 统计）。
    ///
    /// 2026-09 扩展（"caravans are getting stuck on the border"用户报告排查，verbose 门控，纯日志不改行为）：
    /// ① <see cref="GatherAnimalsAndSlavesForCaravanUtility.CheckArrived"/> Postfix——组队集结等待
    /// （LordToil_PrepareCaravan_Leave 每 100 ticks 调用，memo="ReadyToExitMap"）要求**全部**成员
    /// 距 exitSpot（我们重定向的传送点格）10 格内且可达；个别成员到不了 → 全队在边界集结点干等 =
    /// "stuck on the border"候选。记录未满足成员明细（距离/可达性/是否倒地）。
    /// ② <see cref="Verse.Pawn.ExitMap"/> Postfix——一切原生离场的单点终点确认：商队/访客最终
    /// 有没有真正 despawn 走人，与上面的"卡在半路"日志配对定位断链位置。
    /// </summary>
    static class Patches_CaravanExitDiagnostics
    {
        /// <summary>世界侧"可撤离方向"枚举（世界图组队入口/方向选择消费）——空列表即"任一方向不可达"。</summary>
        [HarmonyPatch(typeof(CaravanExitMapUtility), nameof(CaravanExitMapUtility.AvailableExitTilesAt))]
        static class Patch_CaravanExitDiag_AvailableExitTilesAt
        {
            static void Postfix(Map map, List<PlanetTile> __result)
            {
                if (!RimExodusMod.Settings?.verboseLogging ?? false) return;
                Log.Message($"[RimExodus] [diag] AvailableExitTilesAt(map {map?.uniqueID}, wt={SeamlessTileRegistry.GetMapWorldTile(map)}) -> {__result?.Count ?? 0} tiles.");
            }
        }

        /// <summary>
        /// 组队集结等待明细（2026-09）。CheckArrived 每 100 ticks 每 lord 一次：任一受检成员不满足
        /// （未生成 / 距集合点 &gt; 10 格 / 不可达）→ memo 不触发 → 全队等待。此 Postfix 在
        /// "仍在等待"时列出拖后腿的成员。同 lord 节流 600 ticks 防 verbose 刷屏。
        /// </summary>
        [HarmonyPatch(typeof(GatherAnimalsAndSlavesForCaravanUtility), nameof(GatherAnimalsAndSlavesForCaravanUtility.CheckArrived))]
        static class Patch_CaravanExitDiag_CheckArrived
        {
            private static readonly Dictionary<int, int> LastLogTick = new Dictionary<int, int>();

            static void Postfix(Lord lord, List<Pawn> pawns, IntVec3 meetingPoint, string memo,
                System.Predicate<Pawn> shouldCheckIfArrived)
            {
                if (!RimExodusMod.Settings?.verboseLogging ?? false) return;
                if (lord == null || pawns == null) return;

                List<string> pending = null;
                foreach (var pawn in pawns)
                {
                    if (!shouldCheckIfArrived(pawn)) continue;
                    string reason = null;
                    if (!pawn.Spawned) reason = "notSpawned";
                    else if (!pawn.Position.InHorDistOf(meetingPoint, 10f))
                        reason = $"dist={pawn.Position.DistanceTo(meetingPoint):f0}";
                    else if (!pawn.CanReach(meetingPoint, Verse.AI.PathEndMode.ClosestTouch, Danger.Deadly))
                        reason = "cannotReach";
                    if (reason == null) continue;

                    pending ??= new List<string>();
                    pending.Add($"{pawn.LabelShort}({reason}{(pawn.Downed ? ",downed" : "")})");
                }
                if (pending == null) return; // 全员到位，memo 已触发，无需诊断。

                var key = lord.loadID;
                if (LastLogTick.TryGetValue(key, out var last) && GenTicks.TicksGame - last < 600) return;
                LastLogTick[key] = GenTicks.TicksGame;
                if (LastLogTick.Count > 64) LastLogTick.Clear(); // lord 数量上界兜底，防字典无界增长。

                Log.Message($"[RimExodus] [caravan-exit] Caravan gathering waiting (memo={memo}, meeting={meetingPoint}, "
                    + $"map={lord.Map?.uniqueID}): {pending.Count} pawn(s) not ready — {string.Join(", ", pending)}.");
            }
        }

        /// <summary>
        /// 原生离场终点确认（2026-09）：Pawn.ExitMap 是一切离场（商队/访客/撤离/组队兜底）的最终
        /// despawn 单点。verbose 下一行记录离场者身份，用于确认"边界上的人最终有没有走出去"。
        /// 用 Prefix：ExitMap 完成时 pawn 已 DeSpawn（Map/lord 均已清空），离场前状态只有 Prefix 拿得到。
        /// </summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
        static class Patch_CaravanExitDiag_PawnExitMap
        {
            static void Prefix(Pawn __instance, bool allowedToJoinOrCreateCaravan)
            {
                if (!RimExodusMod.Settings?.verboseLogging ?? false) return;
                if (__instance == null) return;
                Log.Message($"[RimExodus] [caravan-exit] Pawn.ExitMap: {__instance.LabelShort} "
                    + $"(faction={__instance.Faction?.Name ?? "none"}, lord={__instance.GetLord()?.LordJob?.GetType().Name ?? "none"}, "
                    + $"joinOrCreateCaravan={allowedToJoinOrCreateCaravan}) on map {__instance.Map?.uniqueID} at {__instance.Position}.");
            }
        }
    }
}
