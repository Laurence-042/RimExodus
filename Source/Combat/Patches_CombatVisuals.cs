using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图战斗视觉层 patch（阶段5，2026-08）：攻击连线 / 行走路线 / 枪口瞄准角。
    /// 三者共同根因：视觉消费者直接读跨图目标的本地坐标（或被同图守卫跳过）。Thing 目标
    /// 从自身恢复 Map；手雷/迫击炮等纯格目标统一从 SeamlessCrossMapCellTarget 恢复 Map。
    /// ① <see cref="Patch_Pawn_JobTracker_DrawLinesBetweenTargets"/>——原版 1020 行
    ///    "targetA.Thing.Map == pawn.Map" 守卫使跨图攻击目标**完全不画线**（对侧攻击标记）；
    ///    邻图 pawn 被选中时按其本图坐标画线（当前视图错位）。
    /// ② <see cref="Patch_PawnPath_DrawPath"/>——邻图 pawn 的行走路线按其本图坐标画（错位）。
    /// ③ <see cref="Patch_PawnRenderUtility_CrossMapAim"/>——瞄准角用 focusTarg.Thing.DrawPos
    ///    （目标图本地坐标）求差，跨图时枪口方向/角度错误；ref-Prefix 在 DrawEquipmentAiming
    ///    消费端改参修正（**勿再走 transpiler**——Mono DMD 产出非法 IL，见该类注释）。
    /// 全部换算为统一坐标（DrawPos + offset，与弹道/渲染同系）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.DrawLinesBetweenTargets))]
    public static class Patch_Pawn_JobTracker_DrawLinesBetweenTargets
    {
        public sealed class State
        {
            public Job job;
            public LocalTargetInfo originalTarget;
            public bool replacedCellTarget;
            public bool restored;
        }

        private static void Restore(State state)
        {
            if (state == null || state.restored) return;
            if (state.replacedCellTarget && state.job != null)
                state.job.targetA = state.originalTarget;
            state.restored = true;
        }

        /// <summary>
        /// 场景 A（邻图 pawn 被选中）：原版按其本图坐标画线，在当前视图是错位线——Prefix 接管，
        /// 起点与目标全部 +offset 重画（简化克隆：curJob.targetA）。
        /// 场景 B（本图 pawn、目标在邻图）由 Postfix 补画（见下）。
        /// </summary>
        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn, out State __state)
        {
            __state = null;
            var pawn = ___pawn;
            if (pawn?.Map == null) return true;

            // Cell-only targets carry no Map in LocalTargetInfo. For a pawn on the focused map vanilla
            // therefore draws targetA as though it belonged to that map. Temporarily substitute the
            // unified cell while the vanilla routine draws its current target, then
            // restore the real job target in Postfix. Thing targets keep the vanilla Map guard and are
            // supplemented below, because replacing a Thing with a cell would lose its exact DrawPos.
            if (pawn.Map == Find.CurrentMap)
            {
                var currentJob = __instance.curJob;
                if (currentJob?.targetA.IsValid == true && !currentJob.targetA.HasThing
                    && SeamlessCrossMapCellTarget.TryGetUnifiedTarget(pawn, pawn.CurrentEffectiveVerb,
                        currentJob.targetA, out var unified))
                {
                    __state = new State
                    {
                        job = currentJob,
                        originalTarget = currentJob.targetA,
                        replacedCellTarget = true
                    };
                    currentJob.targetA = new LocalTargetInfo(unified.ToIntVec3());
                }
                return true;
            }

            if (!SeamlessViewProjection.TryProject(pawn.Map, Vector3.zero, Find.CurrentMap, out var offsetV)) return true;
            var a = (pawn.pather.curPath != null
                ? pawn.pather.Destination.CenterVector3
                : pawn.Position.ToVector3Shifted()) + offsetV;
            var job = __instance.curJob;
            if (job?.targetA.IsValid == true)
            {
                var target = job.targetA.CenterVector3;
                if (SeamlessCrossMapCellTarget.TryGetUnifiedTarget(pawn, pawn.CurrentEffectiveVerb,
                        job.targetA, out var unified)) target = unified;
                GenDraw.DrawLineBetween(a, target + offsetV, AltitudeLayer.Item.AltitudeFor());
            }
            return false; // 原版画线在其本图坐标系，当前视图下错位，不采用
        }

        /// <summary>
        /// 场景 B 补画：本图 pawn 的 job 目标（或桥接最终目的地）在邻图——原版同图守卫跳过。
        /// 起点 = 行进中的路径终点 / 站立时的自身位置（与原版链一致）。
        /// 样式（2026-08 二次修正，对齐原版）：**攻击与移动都是纯白线，无末端标记**——原版执行期
        /// 从不画持续目标标记（CurTargetingColor 红色脉冲材质仅瞄准 UI 期使用；曾对跨图攻击持续画
        /// DrawTargetHighlightWithLayer 造成"指示线末端红色闪烁圆圈"的非原版表现，已删，勿回退）。
        /// </summary>
        public static void Postfix(Pawn ___pawn, State __state)
        {
            Restore(__state);

            var pawn = ___pawn;
            if (pawn?.Map == null || pawn.Map != Find.CurrentMap) return;

            var job = pawn.CurJob;
            if ((__state == null || !__state.replacedCellTarget) && job != null && job.targetA.IsValid
                && SeamlessCrossMapCellTarget.TryGetUnifiedTarget(pawn, pawn.CurrentEffectiveVerb,
                    job.targetA, out var unified))
            {
                var a = pawn.pather.curPath != null
                    ? pawn.pather.Destination.CenterVector3
                    : pawn.Position.ToVector3Shifted();
                GenDraw.DrawLineBetween(a, unified, AltitudeLayer.Item.AltitudeFor());
            }

            // 桥接行走的最终目的地延伸段：TransitGoto 只画到本图 spot，玩家看不到缝对面的终点。
            // 移动语义不画目标框（与攻击样式区分；对齐原版移动 = 路径线 + fleck 无框）。
            if (SeamlessTransferGrants.TryGet(pawn, out var grant) && grant.Kind == SeamlessTransferGrants.GrantKind.Bridge
                && grant.FinalDestMap != null && grant.FinalDestMap != pawn.Map
                && SeamlessViewProjection.TryProject(grant.FinalDestMap,
                    grant.FinalDestCell.ToVector3Shifted(), Find.CurrentMap, out var unifiedDest))
            {
                var start = pawn.pather.curPath != null
                    ? pawn.pather.Destination.CenterVector3
                    : pawn.Position.ToVector3Shifted();
                GenDraw.DrawLineBetween(start, unifiedDest, AltitudeLayer.Item.AltitudeFor());
            }
        }

        /// <summary>Job is persistent state; restore it even when vanilla or another patch throws.</summary>
        public static Exception Finalizer(Exception __exception, State __state)
        {
            Restore(__state);
            return __exception;
        }
    }

    /// <summary>
    /// 跨图瞄准进度指示器（2026-08 修复"瞄准 pie 角度不对"）：原版 GenDraw.DrawAimPie 用
    /// target.Thing.DrawPos 或 target.Cell（目标图本地坐标）求 facing——身体朝向/枪口角两 patch 已修，pie 是
    /// 同一坐标源的第三个消费者。Prefix 把跨图目标 **ref 替换为统一坐标的 cell 型
    /// target** 后放行原方法（原版 `target.Thing == null` 分支用 Cell 差值算角，原生完成绘制，
    /// 零克隆零跨方法调用）；顺带覆盖 Building_TurretGun 选中时的跨图 pie。原版门槛（仅选中
    /// 显示、drawAimPie 旗标）在上游不动。
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawAimPie))]
    public static class Patch_GenDraw_DrawAimPie_CrossMap
    {
        public static void Prefix(Thing shooter, ref LocalTargetInfo target)
        {
            var verb = (shooter as IAttackTargetSearcher)?.CurrentEffectiveVerb;
            if (!SeamlessCrossMapCellTarget.TryGetUnifiedTarget(shooter, verb, target, out var unified)) return;
            // DrawAimPie only consumes the cell as a direction vector. A valid point on the neighboring
            // map may be outside the shooter's rectangular Map bounds while still being visible in the
            // composite view; rejecting it here would diverge from target/path/aim-line rendering.
            target = new LocalTargetInfo(unified.ToIntVec3()); // 原生 cell 分支用 (Cell - Position).AngleFlat 算 facing
        }
    }

    /// <summary>
    /// 跨图瞄准 mote 指示器（2026-08 修复"瞄准进度指示器方向不对"的第二类消费者）：
    /// <c>Stance_Warmup</c> 的瞄准三 mote——aimChargeMote（射手脚下随瞄准方向旋转的进度环，
    /// **玩家所见"瞄准进度指示器"的主体**）、aimTargetMote（目标标记，位置+朝向）、
    /// aimLineMote（瞄准线终点）——位置/方向全部经 <c>focusTarg.CenterVector3</c>（Thing 或纯格的目标图本地
    /// 坐标）计算。**纯 Prefix/Postfix 方案（勿再对此链走 transpiler——2026-08 实测对
    /// InitEffects 的调用点替换在 Mono DMD 下产出非法 IL（InvalidProgramException，与历史瞄准角
    /// case 同款），PatchAll 整体抛异常 = mod 加载失败；CLR 验证器对此返回 OK，拦不住）：**
    /// mote 保留在射手图，StanceTick Postfix 只按射手图统一坐标修正三者；复合视图绘制邻图
    /// mote 时在最终 DrawMote 出口统一叠加视图 offset。DrawAimPie（选中才显示）由上方 patch 单独覆盖。
    /// </summary>
    [HarmonyPatch(typeof(Stance_Warmup), nameof(Stance_Warmup.StanceTick))]
    public static class Patch_StanceWarmup_StanceTick_AimMotes
    {
        private static readonly AccessTools.FieldRef<Stance_Busy, LocalTargetInfo> FocusTargRef =
            AccessTools.FieldRefAccess<Stance_Busy, LocalTargetInfo>("focusTarg");

        private static readonly AccessTools.FieldRef<Stance_Busy, Verb> VerbRef =
            AccessTools.FieldRefAccess<Stance_Busy, Verb>("verb");

        private static readonly AccessTools.FieldRef<Stance_Warmup, Mote> AimLineMoteRef =
            AccessTools.FieldRefAccess<Stance_Warmup, Mote>("aimLineMote");

        private static readonly AccessTools.FieldRef<Stance_Warmup, Mote> AimTargetMoteRef =
            AccessTools.FieldRefAccess<Stance_Warmup, Mote>("aimTargetMote");

        private static readonly AccessTools.FieldRef<Stance_Warmup, Mote> AimChargeMoteRef =
            AccessTools.FieldRefAccess<Stance_Warmup, Mote>("aimChargeMote");

        public static void Postfix(Stance_Warmup __instance)
        {
            var verb = VerbRef(__instance);
            var caster = verb?.Caster;
            var focusTarg = FocusTargRef(__instance);
            if (!SeamlessCrossMapCellTarget.TryGetUnifiedTarget(caster, verb, focusTarg,
                    out var unifiedCenter)) return;
            var casterCenter = caster.DrawPos;
            var dir = unifiedCenter - casterCenter;
            dir.y = 0f;
            var angle = dir.AngleFlat();

            var targetMote = AimTargetMoteRef(__instance);
            if (targetMote != null)
            {
                targetMote.exactPosition = unifiedCenter;
                targetMote.exactRotation = angle;
            }

            var chargeMote = AimChargeMoteRef(__instance);
            if (chargeMote != null && dir.sqrMagnitude > 0.001f)
            {
                chargeMote.exactRotation = angle;
                chargeMote.exactPosition = casterCenter + dir.normalized * verb.verbProps.aimingChargeMoteOffset;
            }

            if (AimLineMoteRef(__instance) is MoteDualAttached lineMote)
            {
                var targetCell = unifiedCenter.ToIntVec3();
                lineMote.UpdateTargets(caster, new TargetInfo(targetCell, caster.Map),
                    Vector3.zero, unifiedCenter - targetCell.ToVector3Shifted());
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
            if (!SeamlessViewProjection.TryProject(pathingPawn.Map, Vector3.zero, Find.CurrentMap, out var offsetV)) return true;
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
    /// focusTarg.Thing.DrawPos / focusTarg.Cell（目标图本地坐标）求 AngleFlat 并按它旋转武器贴身偏移。
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
            if (busy == null || !busy.focusTarg.IsValid
                || !SeamlessCrossMapCellTarget.TryGetUnifiedTarget(pawn, busy.verb,
                    busy.focusTarg, out var unified)) return true;
            var correctAngle = (unified - pawn.DrawPos).AngleFlat();
            if (!SeamlessViewProjection.TryProject(pawn.Map, pawn.DrawPos, Find.CurrentMap, out var projectedPawn))
            {
                return true;
            }
            // 原始 drawLoc 的贴身偏移按错误角度旋转过——绕 pawn 位置回旋角度差修正。
            drawLoc = projectedPawn + (drawLoc - projectedPawn).RotatedBy(correctAngle - aimAngle);
            aimAngle = correctAngle;
            return true;
        }
    }
}
