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

        /// <summary>
        /// 跨图 fleck 重定向共用判定：fleck 目标图是 CurrentMap 的活跃邻居 → 改写 map 为本图并
        /// 输出统一坐标 offset；否则不动。返回是否发生重定向。
        /// </summary>
        internal static bool RedirectCrossMapFleck(ref Map map, out IntVec3 offset)
        {
            offset = IntVec3.Zero;
            var hostMap = Find.CurrentMap;
            if (hostMap == null || map == null || map == hostMap || map.Disposed) return false;
            if (!SeamlessCombatCoords.TryGetCombatLink(hostMap, map, out var link)) return false;
            offset = link.offset;
            map = hostMap;
            return true;
        }

        /// <summary>瞄准 mote 的目标点兼容层已移除（transpiler 方案在 Mono 下产出非法 IL，改用
        /// Patch_MoteMaker_StaticMote_CrossMap + Patch_StanceWarmup_StanceTick_AimMotes 的
        /// Prefix/Postfix 方案，见对应类注释）。</summary>
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
        /// 起点 = 行进中的路径终点 / 站立时的自身位置（与原版链一致）。
        /// 样式（2026-08 二次修正，对齐原版）：**攻击与移动都是纯白线，无末端标记**——原版执行期
        /// 从不画持续目标标记（CurTargetingColor 红色脉冲材质仅瞄准 UI 期使用；曾对跨图攻击持续画
        /// DrawTargetHighlightWithLayer 造成"指示线末端红色闪烁圆圈"的非原版表现，已删，勿回退）。
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
                }
            }

            // 桥接行走的最终目的地延伸段：TransitGoto 只画到本图 spot，玩家看不到缝对面的终点。
            // 移动语义不画目标框（与攻击样式区分；对齐原版移动 = 路径线 + fleck 无框）。
            if (SeamlessTransferGrants.TryGet(pawn, out var grant) && grant.Kind == SeamlessTransferGrants.GrantKind.Bridge
                && grant.FinalDestMap != null && grant.FinalDestMap != pawn.Map
                && SeamlessCombatCoords.TryGetCombatLink(pawn.Map, grant.FinalDestMap, out var bridgeLink))
            {
                var start = pawn.pather.curPath != null
                    ? pawn.pather.Destination.CenterVector3
                    : pawn.Position.ToVector3Shifted();
                var unifiedDest = grant.FinalDestCell.ToVector3Shifted() + Patches_CombatVisuals.OffsetVector(in bridgeLink);
                GenDraw.DrawLineBetween(start, unifiedDest, AltitudeLayer.Item.AltitudeFor());
            }
        }
    }

    /// <summary>
    /// 跨图 fleck 反馈公共重定向（2026-08 修复"跨图命令标记与原版不一致"）：原版下令反馈
    /// （FeedbackShoot/FeedbackMelee 等）生成在目标自己的图上，而邻图 FleckSystem 不被本视图
    /// 渲染 → 玩家看不到任何反馈。Prefix 在公共入口把"生成在活跃邻图上的 fleck"以统一坐标
    /// （loc + offset）重生成本图——一个 patch 覆盖所有跨图 fleck（下令反馈、弹着、治疗等纯
    /// 视觉效果全部跟随当前视图可见）。Static 有 Vector3/IntVec3 两个重载，分别声明。
    /// </summary>
    [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.Static),
        new[] { typeof(Vector3), typeof(Map), typeof(FleckDef), typeof(float) })]
    public static class Patch_FleckMaker_StaticVec_CrossMap
    {
        public static void Prefix(ref Vector3 loc, ref Map map)
        {
            if (Patches_CombatVisuals.RedirectCrossMapFleck(ref map, out var offset))
            {
                loc += new Vector3(offset.x, 0f, offset.z);
            }
        }
    }

    [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.Static),
        new[] { typeof(IntVec3), typeof(Map), typeof(FleckDef), typeof(float) })]
    public static class Patch_FleckMaker_StaticCell_CrossMap
    {
        public static void Prefix(ref IntVec3 cell, ref Map map)
        {
            if (Patches_CombatVisuals.RedirectCrossMapFleck(ref map, out var offset))
            {
                cell += offset;
            }
        }
    }

    /// <summary>
    /// 跨图瞄准进度指示器（2026-08 修复"瞄准 pie 角度不对"）：原版 GenDraw.DrawAimPie 用
    /// target.Thing.DrawPos（目标图本地坐标）求 facing——身体朝向/枪口角两 patch 已修，pie 是
    /// 同一坐标源的第三个消费者。Prefix 把跨图 thing 目标 **ref 替换为统一坐标的 cell 型
    /// target** 后放行原方法（原版 `target.Thing == null` 分支用 Cell 差值算角，原生完成绘制，
    /// 零克隆零跨方法调用）；顺带覆盖 Building_TurretGun 选中时的跨图 pie。原版门槛（仅选中
    /// 显示、drawAimPie 旗标）在上游不动。
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawAimPie))]
    public static class Patch_GenDraw_DrawAimPie_CrossMap
    {
        public static void Prefix(Thing shooter, ref LocalTargetInfo target)
        {
            if (shooter?.Map == null || !target.HasThing) return;
            var thing = target.Thing;
            if (thing?.Map == null || thing.Map == shooter.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(shooter.Map, thing.Map, out var link)) return;

            var unifiedCell = thing.Position + link.offset;
            if (!unifiedCell.InBounds(shooter.Map)) return;
            target = new LocalTargetInfo(unifiedCell); // 原生 cell 分支用 (Cell - Position).AngleFlat 算 facing
        }
    }

    /// <summary>
    /// 跨图瞄准 mote 指示器（2026-08 修复"瞄准进度指示器方向不对"的第二类消费者）：
    /// <c>Stance_Warmup</c> 的瞄准三 mote——aimChargeMote（射手脚下随瞄准方向旋转的进度环，
    /// **玩家所见"瞄准进度指示器"的主体**）、aimTargetMote（目标标记，位置+朝向）、
    /// aimLineMote（瞄准线终点）——位置/方向全部经 <c>focusTarg.CenterVector3</c>（目标图本地
    /// 坐标）计算。**纯 Prefix/Postfix 方案（勿再对此链走 transpiler——2026-08 实测对
    /// InitEffects 的调用点替换在 Mono DMD 下产出非法 IL（InvalidProgramException，与历史瞄准角
    /// case 同款），PatchAll 整体抛异常 = mod 加载失败；CLR 验证器对此返回 OK，拦不住）：**
    /// ① <see cref="Patch_MoteMaker_StaticMote_CrossMap"/>——mote 生成重定向（跨图活跃邻图 →
    /// 统一坐标 + 本图，兼防目标本地格越界时 GenSpawn 崩溃）；② <see cref="Patch_StanceWarmup_StanceTick_AimMotes"/>
    /// ——StanceTick Postfix 后修正三 mote 的 exactPosition/exactRotation/UpdateTargets（原方法
    /// 先按本地坐标写一遍，Postfix 覆盖为统一坐标；DrawAimPie（选中才显示）由上方 patch 单独覆盖）。
    /// </summary>
    [HarmonyPatch(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote),
        new[] { typeof(Vector3), typeof(Map), typeof(ThingDef), typeof(float), typeof(bool), typeof(float) })]
    public static class Patch_MoteMaker_StaticMote_CrossMap
    {
        public static void Prefix(ref Vector3 loc, ref Map map)
        {
            if (Patches_CombatVisuals.RedirectCrossMapFleck(ref map, out var offset))
            {
                loc += new Vector3(offset.x, 0f, offset.z);
            }
        }
    }

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
            if (caster?.Map == null || !focusTarg.HasThing) return;
            var thing = focusTarg.Thing;
            if (thing?.MapHeld == null || thing.MapHeld == caster.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, thing.MapHeld, out var link)) return;

            var unifiedCenter = thing.DrawPos + Patches_CombatVisuals.OffsetVector(in link);
            var dir = unifiedCenter - caster.DrawPos;
            dir.y = 0f;
            var angle = (unifiedCenter - caster.DrawPos).AngleFlat();

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
                chargeMote.exactPosition = caster.Position.ToVector3Shifted() + dir.normalized * verb.verbProps.aimingChargeMoteOffset;
            }

            if (AimLineMoteRef(__instance) is MoteDualAttached lineMote)
            {
                // 原版 StanceTick L174-176 同式：目标端点换统一格。
                var cell = unifiedCenter.ToIntVec3();
                if (cell.InBounds(caster.Map))
                {
                    lineMote.UpdateTargets(caster, new TargetInfo(cell, caster.Map),
                        Vector3.zero, unifiedCenter - cell.ToVector3Shifted());
                }
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
