using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图可达性公共函数（2026-08 架构重构：点击重放 + 公共函数跨图化，VMF CrossMapReachability
    /// 同构思想）：原版把"邻图本地坐标当本图坐标"查出任意结果，旧版统一安静 false（选项诚实禁用）。
    /// 现在**玩家下令语境**（菜单重放帧 / pawn 带 CommandTarget 登记且指向目标图）给出真实答案
    /// ——CanBridgeTo（pawn 能到指向目标图的传送点）——使原生 GoHere/近战/开采等选项真正启用，
    /// 执行由 StartPath 跨图包装（Patches_CrossMapCommon）承接。**非下令语境（NPC/AI 自动评估）
    /// 保持安静 false**：跨图执行能力仅由玩家下令链享有，NPC 不会拿到"可达"却走不过去。
    /// 防递归：CanBridgeTo 内部经 pawn 所在图的实例 CanReach 查 spot（同图，原生放行）。
    /// </summary>
    public static class Patches_ReachabilityCrossMap
    {
        /// <summary>
        /// 该 pawn 当前是否处于"指向 targetMap 的玩家下令语境"（真实答案）；
        /// false = 保持安静不可达（NPC/自动评估）。
        /// </summary>
        internal static bool IsCommandedToward(Pawn pawn, Map targetMap)
        {
            if (SeamlessReplayContext.Active
                && pawn.Map == SeamlessReplayContext.Host
                && targetMap == SeamlessReplayContext.Target)
            {
                return true; // 菜单重放帧（provider 评估，同步窗口）
            }
            return SeamlessCommandTargets.TryGet(pawn, out var ct) && ct.map == targetMap;
        }

        internal static bool AnswerCrossMap(Pawn pawn, Map targetMap, ref bool __result)
        {
            __result = SeamlessCrossMapOrders.CanBridgeTo(pawn, targetMap);
            return false;
        }
    }

    [HarmonyPatch(typeof(ReachabilityUtility), nameof(ReachabilityUtility.CanReach),
        new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(Danger),
            typeof(bool), typeof(bool), typeof(TraverseMode) })]
    public static class Patch_ReachabilityUtility_CanReach
    {
        public static bool Prefix(Pawn pawn, LocalTargetInfo dest, ref bool __result)
        {
            if (pawn?.Map == null) return true;

            // Thing 目标：坐标框架无歧义（目标物在邻图上）。
            if (dest.HasThing && dest.Thing.Map != null && dest.Thing.Map != pawn.Map)
            {
                var targetMap = dest.Thing.Map;
                if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, targetMap, out _)) return true; // 非邻居：原版语义
                if (Patches_ReachabilityCrossMap.IsCommandedToward(pawn, targetMap)) return Patches_ReachabilityCrossMap.AnswerCrossMap(pawn, targetMap, ref __result);
                __result = false; // NPC/自动评估：安静不可达
                return false;
            }

            // Cell 目标：仅菜单重放帧内按重放目标图判定（此时 cell 派生自 context.map = 邻图框架；
            // 帧歧义窗口被压缩到同步重放窗口内，非菜单帧的 cell 调用全部原生）。
            if (!dest.HasThing && SeamlessReplayContext.Active
                && pawn.Map == SeamlessReplayContext.Host
                && dest.Cell.InBounds(SeamlessReplayContext.Target)
                && SeamlessCommandTargets.TryGet(pawn, out var ct)
                && ct.map == SeamlessReplayContext.Target)
            {
                return Patches_ReachabilityCrossMap.AnswerCrossMap(pawn, SeamlessReplayContext.Target, ref __result);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    public static class Patch_Reachability_CanReach
    {
        private static readonly AccessTools.FieldRef<Reachability, Map> MapRef =
            AccessTools.FieldRefAccess<Reachability, Map>("map");

        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref bool __result)
        {
            var pawn = traverseParams.pawn;
            if (pawn?.Map == null) return true;
            var instMap = MapRef(__instance);
            if (instMap == null || instMap == pawn.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, instMap, out _)) return true; // 非邻居：原版（含红字）

            // 检查图是 pawn 的活跃邻图：原版此处 Log.Error（Reachability.cs:117"pawn spawned not on
            // this map"）——下令语境给真实答案，其余安静 false 吞红字。
            if (Patches_ReachabilityCrossMap.IsCommandedToward(pawn, instMap)) return Patches_ReachabilityCrossMap.AnswerCrossMap(pawn, instMap, ref __result);
            __result = false;
            return false;
        }
    }
}
