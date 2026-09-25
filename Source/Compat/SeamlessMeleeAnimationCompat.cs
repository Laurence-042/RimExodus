using System;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Melee Animation 兼容。
    ///
    /// 影子远行队把仍 spawned 的殖民者 holdingOwner 指向影子 Caravan（SeamlessShadowCaravan
    /// 类注释①），因此 pawn.ParentHolder 是 Caravan 而非 null——原版不变量"Spawned ⇒
    /// ParentHolder == null"被有意打破（OAYH 等外交 Mod 依赖该投影识别据点成员，勿改 getter）。
    /// MeleeAnim 的 AnimRenderer.IsPawnValid 要求参与者 ParentHolder 是动画所在 Map，
    /// 影子成员因此被判无效，决斗/近战动画启动即销毁（红字 "HasHolderParent: True (Caravan …)"）。
    ///
    /// 这里只放开"持有者为影子 Caravan 且宿主图 == 动画图"一项：影子成员实际就站在本图上，
    /// 与该检查排除吊舱/运输容器/跨图 pawn 的本意不冲突。死亡/倒地/未 spawned/真容器仍按
    /// 原逻辑拒绝。补丁挂在 IsPawnValid 本身，同时覆盖 Register() 启动校验与动画中途的
    /// 持续校验（GetFirstInvalidPawn 同走此方法）。
    /// </summary>
    internal static class SeamlessMeleeAnimationCompat
    {
        private const string PackageId = "co.uk.epicguru.meleeanimation";

        private static FieldInfo animRendererMapField;

        internal static void Register(Harmony harmony)
        {
            if (!ModsConfig.IsActive(PackageId)) return;

            try
            {
                var animRendererType = AccessTools.TypeByName("AM.AnimRenderer");
                var isPawnValidMethod = AccessTools.Method(animRendererType, "IsPawnValid");
                animRendererMapField = AccessTools.Field(animRendererType, "Map");

                if (animRendererType == null || isPawnValidMethod == null || animRendererMapField == null)
                    throw new MissingMemberException("AM.AnimRenderer.IsPawnValid or .Map not found (Melee Animation version drift?)");

                harmony.Patch(isPawnValidMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(SeamlessMeleeAnimationCompat), nameof(IsPawnValidPostfix))));
                Log.Message("[RimExodus] Melee Animation compat: bound IsPawnValid shadow-caravan allowance.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Melee Animation compat unavailable; RimExodus continues without it: {ex}");
            }
        }

        private static void IsPawnValidPostfix(object __instance, Pawn p, ref bool __result)
        {
            if (__result || p == null || p.Dead || p.Downed || !p.Spawned) return;
            if (p.holdingOwner?.Owner is not Caravan caravan) return;
            if (!SeamlessShadowCaravan.TryGetShadowMap(caravan, out var shadowMap)) return;
            if (animRendererMapField.GetValue(__instance) != shadowMap) return;
            __result = true;
        }
    }
}
