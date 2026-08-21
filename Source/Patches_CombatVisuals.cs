using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图战斗视觉层 patch（阶段5，2026-08）：攻击连线 / 行走路线 / 枪口瞄准角。
    /// 三者共同根因：视觉消费者直接读跨图目标的本地坐标（或被同图守卫跳过）。
    /// ① <see cref="Patch_Pawn_JobTracker_DrawLinesBetweenTargets"/>——原版 1020 行
    ///    "targetA.Thing.Map == pawn.Map" 守卫使跨图攻击目标**完全不画线**（对侧攻击标记）；
    ///    邻图 pawn 被选中时按其本图坐标画线（当前视图错位）。
    /// ② <see cref="Patch_PawnPath_DrawPath"/>——邻图 pawn 的行走路线按其本图坐标画（错位）。
    /// ③ <see cref="Patch_PawnRenderUtility_CrossMapAim"/>——瞄准角用 focusTarg.Thing.DrawPos
    ///    （目标图本地坐标）求差，跨图时枪口方向/角度错误；ref-Prefix 在 DrawEquipmentAiming
    ///    消费端改参修正（**勿再走 transpiler**——Mono DMD 产出非法 IL，见该类注释）。
    /// 全部换算为统一坐标（DrawPos + offset，与弹道/渲染同系）。
    /// </summary>
    public static class Patches_CombatVisuals
    {
        internal static Vector3 OffsetVector(in SeamlessCombatCoords.CombatLink link) =>
            new Vector3(link.offset.x, 0f, link.offset.z);
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.DrawLinesBetweenTargets))]
    public static class Patch_Pawn_JobTracker_DrawLinesBetweenTargets
    {
        /// <summary>
        /// 场景 A（邻图 pawn 被选中）：原版按其本图坐标画线，在当前视图是错位线——Prefix 接管，
        /// 起点与目标全部 +offset 重画（简化克隆：curJob.targetA）。
        /// 场景 B（本图 pawn、目标在邻图）由 Postfix 补画（见下）。
        /// </summary>
        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            var pawn = ___pawn;
            if (pawn?.Map == null || pawn.Map == Find.CurrentMap) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(Find.CurrentMap, pawn.Map, out var link)) return true;

            var offsetV = Patches_CombatVisuals.OffsetVector(in link);
            var a = (pawn.pather.curPath != null
                ? pawn.pather.Destination.CenterVector3
                : pawn.Position.ToVector3Shifted()) + offsetV;
            var job = __instance.curJob;
            if (job?.targetA.IsValid == true)
            {
                GenDraw.DrawLineBetween(a, job.targetA.CenterVector3 + offsetV, AltitudeLayer.Item.AltitudeFor());
            }
            return false; // 原版画线在其本图坐标系，当前视图下错位，不采用
        }

        /// <summary>
        /// 场景 B 补画：本图 pawn 的 job 目标（或桥接最终目的地）在邻图——原版同图守卫跳过。
        /// 起点 = 行进中的路径终点 / 站立时的自身位置（与原版链一致）；目标格加持续高亮方框
        /// （原版 targeting 高亮同款材质）= 对侧攻击标记。
        /// </summary>
        public static void Postfix(Pawn ___pawn)
        {
            var pawn = ___pawn;
            if (pawn?.Map == null || pawn.Map != Find.CurrentMap) return;

            var job = pawn.CurJob;
            if (job != null && job.targetA.IsValid && job.targetA.HasThing)
            {
                var thing = job.targetA.Thing;
                if (thing?.Map != null && thing.Map != pawn.Map
                    && SeamlessCombatCoords.TryGetCombatLink(pawn.Map, thing.Map, out var link))
                {
                    var a = pawn.pather.curPath != null
                        ? pawn.pather.Destination.CenterVector3
                        : pawn.Position.ToVector3Shifted();
                    var unified = thing.DrawPos + Patches_CombatVisuals.OffsetVector(in link);
                    GenDraw.DrawLineBetween(a, unified, AltitudeLayer.Item.AltitudeFor());
                    GenDraw.DrawTargetHighlightWithLayer(thing.Position + link.offset, AltitudeLayer.MapDataOverlay);
                }
            }

            // 桥接行走的最终目的地延伸段：TransitGoto 只画到本图 spot，玩家看不到缝对面的终点。
            if (SeamlessTransferGrants.TryGet(pawn, out var grant) && grant.Kind == SeamlessTransferGrants.GrantKind.Bridge
                && grant.FinalDestMap != null && grant.FinalDestMap != pawn.Map
                && SeamlessCombatCoords.TryGetCombatLink(pawn.Map, grant.FinalDestMap, out var bridgeLink))
            {
                var start = pawn.pather.curPath != null
                    ? pawn.pather.Destination.CenterVector3
                    : pawn.Position.ToVector3Shifted();
                var unifiedDest = grant.FinalDestCell.ToVector3Shifted() + Patches_CombatVisuals.OffsetVector(in bridgeLink);
                GenDraw.DrawLineBetween(start, unifiedDest, AltitudeLayer.Item.AltitudeFor());
                GenDraw.DrawTargetHighlightWithLayer(grant.FinalDestCell + bridgeLink.offset, AltitudeLayer.MapDataOverlay);
            }
        }
    }

    [HarmonyPatch(typeof(PawnPath), nameof(PawnPath.DrawPath))]
    public static class Patch_PawnPath_DrawPath
    {
        /// <summary>邻图 pawn 的行走路线按其本图坐标画（当前视图错位）——整体 +offset 重画。</summary>
        public static bool Prefix(PawnPath __instance, Pawn pathingPawn)
        {
            if (!__instance.Found || __instance.NodesLeftCount <= 0) return false;
            if (pathingPawn?.Map == null || pathingPawn.Map == Find.CurrentMap) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(Find.CurrentMap, pathingPawn.Map, out var link)) return true;

            var offsetV = Patches_CombatVisuals.OffsetVector(in link);
            float y = AltitudeLayer.Item.AltitudeFor();
            for (var i = 0; i < __instance.NodesLeftCount - 1; i++)
            {
                var a = __instance.Peek(i).ToVector3Shifted();
                a.y = y;
                var b = __instance.Peek(i + 1).ToVector3Shifted();
                b.y = y;
                GenDraw.DrawLineBetween(a + offsetV, b + offsetV);
            }
            var from = pathingPawn.DrawPos;
            from.y = y;
            var first = __instance.Peek(0).ToVector3Shifted();
            first.y = y;
            if ((from - first).sqrMagnitude > 0.01f)
            {
                GenDraw.DrawLineBetween(from + offsetV, first + offsetV);
            }
            return false;
        }
    }

    /// <summary>
    /// 跨图瞄准角（2026-08 修复"枪的朝向和角度不对"）：原版 DrawEquipmentAndApparelExtras 用
    /// focusTarg.Thing.DrawPos（目标图本地坐标）求 AngleFlat 并按它旋转武器贴身偏移。
    /// 在消费端 <see cref="PawnRenderUtility.DrawEquipmentAiming"/> 用 **ref 改参**修正后放行原方法
    /// （mesh/后坐力/材质零克隆）。**勿再为此走 transpiler**：曾对其取值链做指令对替换，Mono DMD
    /// 下持续产出 InvalidProgramException（call 0x00000037——分支标签保留的原地改写也复现），
    /// 根因未定位，ref-Prefix 是确定性等价方案。
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    public static class Patch_PawnRenderUtility_CrossMapAim
    {
        public static bool Prefix(Thing eq, ref Vector3 drawLoc, ref float aimAngle)
        {
            if (!(eq.holdingOwner?.Owner is Pawn_EquipmentTracker tracker)) return true;
            var pawn = tracker.pawn;
            if (pawn?.Map == null) return true;

            var busy = pawn.stances?.curStance as Stance_Busy;
            if (busy == null || !busy.focusTarg.IsValid || !busy.focusTarg.HasThing) return true;
            var thing = busy.focusTarg.Thing;
            if (thing?.Map == null || thing.Map == pawn.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, thing.Map, out var link)) return true;

            var unified = thing.DrawPos + Patches_CombatVisuals.OffsetVector(in link);
            var correctAngle = (unified - pawn.DrawPos).AngleFlat();
            // 原始 drawLoc 的贴身偏移按错误角度旋转过——绕 pawn 位置回旋角度差修正。
            drawLoc = pawn.DrawPos + (drawLoc - pawn.DrawPos).RotatedBy(correctAngle - aimAngle);
            aimAngle = correctAngle;
            return true;
        }
    }
}
