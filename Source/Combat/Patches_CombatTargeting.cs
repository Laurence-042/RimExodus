using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图射击链 patch（阶段5）：
    /// ① <see cref="Patch_Verb_TryFindShootLineFromTo"/> Prefix——跨图目标（活跃邻居）时接管为
    ///    统一坐标射击线（分段 LOS + 统一射程），并登记跨图施法上下文；
    /// ② <see cref="Patch_Verb_LaunchProjectile_TryCastShot"/> transpiler——原版开头
    ///    "目标图 ≠ 施法者图 → return false" 的硬拒绝在跨图有效时放行（**全 mod 唯一 IL 替换点**，
    ///    集中此一处，勿再散加；锚点指令对匹配失败启动即红字，版本升级可即时发现）；
    /// ③ <see cref="Patch_ShotReport_HitReportFor"/> Postfix——跨图时修正距离敏感因子与掩体：
    ///    原版跑的是"目标图本地格 × 本图查询"的错位数据；重算用公开 API
    ///    （ShotReport.HitFactorFromShooter / verbProps.GetHitChanceFactor / CoverUtility 两查询），
    ///    公式本体永远跟随原版，无克隆漂移。掩体的射手格传"目标坐标系下的平移格（可越界）"——
    ///    CoverUtility 对 shooterLoc 只做向量运算（角度/距离/相等），无网格访问，越界安全。
    /// 同图调用（绝大多数）：三个 patch 各 1-2 次引用比较即早退，零额外成本。
    /// </summary>
    public static class Patches_CombatTargeting
    {
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    public static class Patch_Verb_TryFindShootLineFromTo
    {
        /// <summary>
        /// 跨图分支接管：目标 Thing 在另一张互为活跃邻居的图上时，射击线在统一坐标
        /// （宿主=射手图）上计算；否则完全放行原版。近战/flyOverhead 等不支持的 verb
        /// 在 <see cref="SeamlessCrossMapSight.TryFindShootLine"/> 内拒绝（与原版 false 等价）。
        /// </summary>
        public static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, bool ignoreRange, ref bool __result)
        {
            if (!targ.HasThing || targ.Thing.Map == null) return true;
            var caster = SeamlessCombatCoords.VerbCaster(__instance);
            if (caster?.Map == null) return true;
            if (targ.Thing.Map == caster.Map) return true; // 同图：原版
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, targ.Thing.Map, out var link)) return true; // 非活跃邻居：原版语义（false）

            // 登记跨图施法上下文（TryCastShot 的门在其后才跑到）：仅直射投射物 verb，
            // 近战/灵能等不放行（它们跨图本就 false，登记了也无消费者）。
            if (__instance is Verb_LaunchProjectile && !__instance.verbProps.IsMeleeAttack)
            {
                SeamlessCombatCoords.MarkCrossMapCast(__instance, targ.Thing.Map);
            }

            __result = SeamlessCrossMapSight.TryFindShootLine(__instance, root, targ, in link, ignoreRange, out resultingLine);
            return false;
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    public static class Patch_Verb_LaunchProjectile_TryCastShot
    {
        /// <summary>
        /// 原版开头：if (currentTarget.HasThing &amp;&amp; currentTarget.Thing.Map != caster.Map) return false;
        /// 真实 IL（1.6.4871 实测解码）：[ldarg.0][ldflda currentTarget][call LocalTargetInfo::get_Thing]
        /// [callvirt Thing::get_Map]…——Roslyn 对 struct 字段调方法用 ldflda（免拷贝）而非 ldfld。
        /// 锚定 [call get_Thing][callvirt get_Map] 指令对（方法体内唯一，"currentTarget.Thing"的
        /// 其余消费都不接 get_Map），替换为 **栈中立**的 [pop（弃地址）][ldarg.0][call 门]：
        /// 跨图有效时返回施法者图使 != 比较通过，否则返回真实目标图（原语义）。
        /// 原地改写 opcode/operand + 插入无标签新指令（**勿用 new CodeInstruction 替换原对象**——
        /// 原指令可能是分支落点，丢标签 = InvalidProgramException，见 CombatVisuals 同款教训）。
        /// 不依赖前置指令形态（ldflda/ldfld/局部变量提升等编译器差异全部免疫）。
        /// 锚点失配启动即红字（版本升级可即时发现）。
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var gateMethod = AccessTools.Method(typeof(SeamlessCombatCoords), nameof(SeamlessCombatCoords.TargetMapForCastGate));
            var getThing = AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Thing));
            var getMap = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));

            var codes = new List<CodeInstruction>(instructions);
            var found = false;
            for (var i = 0; i < codes.Count - 1; i++)
            {
                if (codes[i].Calls(getThing) && codes[i + 1].Calls(getMap))
                {
                    codes[i].opcode = OpCodes.Pop;
                    codes[i].operand = null;
                    codes[i + 1].opcode = OpCodes.Ldarg_0;
                    codes[i + 1].operand = null;
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, gateMethod)); // 新插入指令不可能是分支落点
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                Log.Error("[RimExodus] TryCastShot transpiler anchor (get_Thing→get_Map pair) not found; "
                    + "cross-map shots will be silently rejected. Game version changed?");
            }
            return codes;
        }
    }

    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class Patch_ShotReport_HitReportFor
    {
        private static readonly FieldInfo DistanceField = AccessTools.Field(typeof(ShotReport), "distance");
        private static readonly FieldInfo TargetField = AccessTools.Field(typeof(ShotReport), "target");
        private static readonly FieldInfo CoversField = AccessTools.Field(typeof(ShotReport), "covers");
        private static readonly FieldInfo CoversOverallField = AccessTools.Field(typeof(ShotReport), "coversOverallBlockChance");
        private static readonly FieldInfo FactorShooterField = AccessTools.Field(typeof(ShotReport), "factorFromShooterAndDist");
        private static readonly FieldInfo FactorEquipmentField = AccessTools.Field(typeof(ShotReport), "factorFromEquipment");

        /// <summary>
        /// 坐标系正修（2026-08，替代已删除的 GasUtility 越界守卫——守卫是兜底，违反"不搞兜底"铁律）：
        /// 原版方法体的气体扫描用目标图本地格查 caster.Map——两图尺寸不同时数值越界崩溃。正修 =
        /// 把 caster 虚拟传送到**目标图坐标系**（Position − offset，clamp 界内），原版方法体
        /// （气体/掩体/距离）全程目标图系自洽；Postfix 6 字段精确写回保留（覆盖 clamp 近似的
        /// 深目标场景）。clamp 后段内气体为目标图侧近似，精度项全部被 Postfix 覆盖。
        /// </summary>
        public static void Prefix(Thing caster, Verb verb, LocalTargetInfo target,
            out SeamlessVirtualTeleporter __state)
        {
            __state = default;
            if (!SeamlessCombatCoords.Enabled) return;
            if (!target.HasThing || caster == null || caster.Map == null) return;
            var targetMap = target.Thing.Map;
            if (targetMap == null || targetMap == caster.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, targetMap, out var link)) return;

            var local = caster.Position - link.offset;
            local.x = Mathf.Clamp(local.x, 0, targetMap.Size.x - 1);
            local.z = Mathf.Clamp(local.z, 0, targetMap.Size.z - 1);
            __state = new SeamlessVirtualTeleporter(caster, targetMap, local);
        }

        /// <summary>
        /// 跨图修正 6 个坐标敏感字段（其余字段——目标体型/黑暗/天气/forcedMiss——原版计算本就
        /// 与图无关或只读射手图，不动）。距离改统一坐标后，两个因子用原版公开 API 重算，
        /// 掩体集在目标图上以平移射手格计算（方向/距离与统一线严格一致）。
        /// 先 Dispose 恢复 caster 真实图/坐标，判定守卫才读到真实归属。
        /// </summary>
        public static void Postfix(Thing caster, Verb verb, LocalTargetInfo target,
            SeamlessVirtualTeleporter __state, ref ShotReport __result)
        {
            __state.Dispose();
            if (!target.HasThing || caster == null || caster.Map == null) return;
            var targetMap = target.Thing.Map;
            if (targetMap == null || targetMap == caster.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, targetMap, out var link)) return;

            var targetUnified = target.Cell + link.offset;
            var distance = (targetUnified - caster.Position).LengthHorizontal;

            var shooterOnTargetMap = caster.Position - link.offset;
            var covers = CoverUtility.CalculateCoverGiverSet(target, shooterOnTargetMap, targetMap);
            var blockChance = CoverUtility.CalculateOverallBlockChance(target, shooterOnTargetMap, targetMap);

            var factorShooter = verb.verbProps.canGoWild
                ? ShotReport.HitFactorFromShooter(caster, distance)
                : 1f;
            var factorEquipment = verb.verbProps.GetHitChanceFactor(verb.EquipmentSource, distance);

            DistanceField.SetValueDirect(__makeref(__result), distance);
            TargetField.SetValueDirect(__makeref(__result), target.ToTargetInfo(targetMap));
            CoversField.SetValueDirect(__makeref(__result), covers);
            CoversOverallField.SetValueDirect(__makeref(__result), blockChance);
            FactorShooterField.SetValueDirect(__makeref(__result), factorShooter);
            FactorEquipmentField.SetValueDirect(__makeref(__result), factorEquipment);
        }
    }

    /// <summary>
    /// 多选攻击选项的 null 守卫（2026-08 修复"跨图右键攻击报 NRE"）：原版
    /// <c>GetMultiselectAttackOption</c> 在没有任何选中 pawn 能攻击（tmpPawns 空）时返回
    /// null，而 <c>GetOptionsFor</c> 是迭代器——<c>yield return null</c> 把 null 选项塞进
    /// <c>GetProviderOptions</c> 的消费循环，循环体 <c>item.iconThing</c> 直接解引用 → NRE
    /// （**原版潜伏 bug**，单帧栈 [0x000b0] 即消费循环体；平时"至少一人能攻击"掩盖了它）。
    /// 跨图场景下"统一格越本图方形的目标（远程 CannotHitTarget，见 SeamlessCrossMapSight
    /// bounds 门）+ 跨图不可近战（CanReach 安静 false → NoPath）"使全员失败成为常态。
    /// Postfix：null → 禁用"攻击"选项（诚实 UX，对齐跨图近战禁用"No path"先例），不再红字。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedAttack), "GetMultiselectAttackOption")]
    public static class Patch_DraftedAttack_MultiselectNullGuard
    {
        public static void Postfix(Thing clickedThing, ref FloatMenuOption __result)
        {
            if (__result != null) return;
            __result = new FloatMenuOption("Attack".Translate(clickedThing.Label, clickedThing), null);
        }
    }

    /// <summary>
    /// 跨图朝向修正（2026-08 修复"射击时面朝方向不对"）：原版 Pawn_RotationTracker.UpdateRotation
    /// 对瞄准姿态（Stance_Busy.focusTarg）与 Job 面向（curDriver.rotateToFace 目标）直接用
    /// focusTarg.Thing.DrawPos——那是目标在自己图上的本地坐标，pawn 会朝本图同数字坐标方向看。
    /// 焦点目标在对端活跃邻居图时改朝统一坐标（DrawPos + offset，与弹道/渲染同一坐标系）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_RotationTracker), "UpdateRotation")]
    public static class Patch_PawnRotationTracker_CrossMapFacing
    {
        private static readonly AccessTools.FieldRef<JobDriver, TargetIndex> RotateToFaceRef =
            AccessTools.FieldRefAccess<JobDriver, TargetIndex>("rotateToFace");

        public static void Postfix(Pawn_RotationTracker __instance, Pawn ___pawn)
        {
            var pawn = ___pawn;
            if (pawn?.Map == null) return;

            Thing focusThing = null;
            var busy = pawn.stances?.curStance as Stance_Busy;
            if (busy != null)
            {
                if (busy.focusTarg.IsValid && busy.focusTarg.HasThing)
                {
                    focusThing = busy.focusTarg.Thing;
                }
            }
            else if (pawn.jobs?.curJob != null && pawn.jobs.curDriver != null)
            {
                var focus = pawn.CurJob.GetTarget(RotateToFaceRef(pawn.jobs.curDriver));
                if (focus.HasThing)
                {
                    focusThing = focus.Thing;
                }
            }

            if (focusThing?.Map == null || focusThing.Map == pawn.Map) return;
            if (!SeamlessViewProjection.TryProject(focusThing.Map, focusThing.DrawPos, pawn.Map, out var projected)) return;
            __instance.Face(projected);
        }
    }
}
