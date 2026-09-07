using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Makes the vanilla weapon-gizmo Targeter resolve Things drawn from an active neighbor map.
    /// LocalTargetInfo has no Map for cell-only targets, so this deliberately handles Thing targets only;
    /// the Thing itself carries its real Map through ValidateTarget/CanHitTarget/OrderForceTarget.
    /// </summary>
    [HarmonyPatch(typeof(GenUI), nameof(GenUI.TargetsAtMouse))]
    public static class Patch_GenUI_TargetsAtMouse_CrossMapCombat
    {
        public static bool Prefix(TargetingParameters clickParams, bool thingsOnly, ITargetingSource source,
            ref IEnumerable<LocalTargetInfo> __result)
        {
            var verb = source?.GetVerb;
            if (!(verb is Verb_LaunchProjectile launchVerb) || verb.verbProps.IsMeleeAttack
                || launchVerb.Projectile?.projectile?.flyOverhead != false)
            {
                return true;
            }

            var hostMap = Find.CurrentMap;
            var mouseMapPosition = UI.MouseMapPosition();
            if (hostMap == null
                || !SeamlessMapUtility.TryResolveMapPosition(mouseMapPosition, hostMap,
                    out var targetMap, out var targetLocalCell)
                || targetMap == null || targetMap == hostMap)
            {
                return true;
            }

            // Preserve the sub-cell mouse offset so pawn wide-click and item ordering match vanilla.
            var hostCell = IntVec3.FromVector3(mouseMapPosition);
            var withinCellOffset = mouseMapPosition - hostCell.ToVector3Shifted();
            var localMousePosition = targetLocalCell.ToVector3Shifted() + withinCellOffset;
            var things = SeamlessGenUI.ThingsUnderMouse(
                localMousePosition, 0.8f, clickParams, targetMap, localMousePosition, source);

            var targets = new List<LocalTargetInfo>(things.Count);
            var caster = source.Caster;
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (!(thing is Pawn pawn) || !pawn.IsPsychologicallyInvisible()
                    || caster == null || caster.Faction == pawn.Faction)
                {
                    targets.Add(thing);
                }
            }

            // Do not synthesize a cell target: LocalTargetInfo(cell) cannot retain the neighbor Map.
            __result = targets;
            return false;
        }
    }

    /// <summary>
    /// Weapon-gizmo targeting has no vanilla command fleck (the right-click attack provider creates its
    /// own). Add it once at Targeter's common confirmation point, after validation and before the per-pawn
    /// orders fan out, so grouped verbs do not create duplicate markers.
    /// </summary>
    [HarmonyPatch(typeof(Targeter), "OrderVerbForceTarget")]
    public static class Patch_Targeter_OrderVerbForceTarget_CrossMapFeedback
    {
        public struct State
        {
            public LocalTargetInfo target;
            public Verb verb;
        }

        private static readonly MethodInfo CurrentTargetUnderMouseMethod =
            AccessTools.Method(typeof(Targeter), "CurrentTargetUnderMouse");

        public static void Prefix(Targeter __instance, ITargetingSource ___targetingSource,
            out State __state)
        {
            __state = default;
            if (!(___targetingSource?.GetVerb is Verb_LaunchProjectile launchVerb)
                || launchVerb.verbProps.IsMeleeAttack
                || launchVerb.Projectile?.projectile?.flyOverhead != false)
            {
                return;
            }

            __state.target = (LocalTargetInfo)CurrentTargetUnderMouseMethod.Invoke(__instance, new object[] { true });
            __state.verb = launchVerb;
        }

        public static void Postfix(State __state)
        {
            var thing = __state.target.Thing;
            if (__state.verb == null) return;
            if (thing?.Map == null || thing.Map == Find.CurrentMap) return;
            if (!SeamlessViewProjection.TryProjectToCurrent(thing.Map, thing.DrawPos, out var projected)) return;

            FleckMaker.Static(projected, Find.CurrentMap, FleckDefOf.FeedbackShoot);
        }
    }

    /// <summary>
    /// Vanilla weapon hover highlighting consumes target.Thing in that Thing's local coordinates. Replace
    /// only the DrawHighlight argument with a cell in the caster map's unified coordinate system; the real
    /// Thing target retained by Targeter and the eventual order are untouched.
    /// </summary>
    [HarmonyPatch(typeof(Verb), nameof(Verb.DrawHighlight))]
    public static class Patch_Verb_DrawHighlight_CrossMapTarget
    {
        public static void Prefix(Verb __instance, ref LocalTargetInfo target)
        {
            if (!(__instance is Verb_LaunchProjectile launchVerb) || !target.HasThing
                || launchVerb.verbProps.IsMeleeAttack
                || launchVerb.Projectile?.projectile?.flyOverhead != false)
            {
                return;
            }

            var casterMap = __instance.Caster?.Map;
            var thing = target.Thing;
            if (casterMap == null || thing?.Map == null || thing.Map == casterMap
                || !SeamlessCombatCoords.TryGetCombatLink(casterMap, thing.Map, out var link))
            {
                return;
            }

            target = new LocalTargetInfo(thing.Position + link.offset);
        }
    }

}
