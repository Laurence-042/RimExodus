using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

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
    /// 本文件只保留 AvailableExitTilesAt 的结果计数（回归时确认方向枚举非空）；5 参调用的 verbose
    /// 明细由 Prefix 自带（成功无日志、全灭打 seam pool exhausted 统计）。
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
    }
}
