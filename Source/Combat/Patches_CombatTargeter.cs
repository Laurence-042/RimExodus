using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Right-click attack actions create their feedback fleck on the target's real map. Neighbor fleck
    /// managers are intentionally not replayed by the composite renderer, so redirect only attack feedback
    /// to the focused map at its projected position. Other flecks retain their source-map ownership.
    /// </summary>
    [HarmonyPatch(typeof(FleckMaker), nameof(FleckMaker.Static),
        new[] { typeof(Vector3), typeof(Map), typeof(FleckDef), typeof(float) })]
    public static class Patch_FleckMaker_Static_CrossMapAttackFeedback
    {
        public static void Prefix(ref Vector3 loc, ref Map map, FleckDef fleckDef)
        {
            var viewMap = Find.CurrentMap;
            if (map == null || viewMap == null || map == viewMap
                || (fleckDef != FleckDefOf.FeedbackShoot && fleckDef != FleckDefOf.FeedbackMelee)) return;
            if (!SeamlessViewProjection.TryProject(map, loc, viewMap, out var projected)) return;

            loc = projected;
            map = viewMap;
        }
    }

    /// <summary>
    /// Makes the shared weapon/ability Targeter resolve Things and cell-only targets drawn from an active
    /// neighbor map. Cell targets retain their otherwise missing Map identity in
    /// <see cref="SeamlessCrossMapCellTarget"/>.
    /// </summary>
    [HarmonyPatch(typeof(GenUI), nameof(GenUI.TargetsAtMouse))]
    public static class Patch_GenUI_TargetsAtMouse_CrossMapCombat
    {
        public static bool Prefix(TargetingParameters clickParams, bool thingsOnly, ITargetingSource source,
            ref IEnumerable<LocalTargetInfo> __result)
        {
            var verb = source?.GetVerb;
            if (!SeamlessDirectFireSupport.IsSupportedVerb(verb))
            {
                return true;
            }

            var hostMap = Find.CurrentMap;
            var mouseMapPosition = UI.MouseMapPosition();
            if (hostMap == null) return true;
            if (!SeamlessMapUtility.TryResolveMapPosition(mouseMapPosition, hostMap,
                    out var targetMap, out var targetLocalCell)
                || targetMap == null)
            {
                SeamlessCrossMapCellTarget.Clear(verb, source.Caster);
                return true;
            }

            var caster = source.Caster;
            var casterMap = caster?.Map ?? hostMap;
            if (targetMap == casterMap)
                SeamlessCrossMapCellTarget.Clear(verb, caster);
            else
                SeamlessCrossMapCellTarget.Register(verb, caster, casterMap, targetMap, targetLocalCell);

            // The mouse is on the focused map, so vanilla can enumerate it correctly. We only needed
            // to retain Map identity above when the selected caster itself belongs to a neighbor map.
            if (targetMap == hostMap) return true;

            // Preserve the sub-cell mouse offset so pawn wide-click and item ordering match vanilla.
            var hostCell = IntVec3.FromVector3(mouseMapPosition);
            var withinCellOffset = mouseMapPosition - hostCell.ToVector3Shifted();
            var localMousePosition = targetLocalCell.ToVector3Shifted() + withinCellOffset;
            var things = SeamlessGenUI.ThingsUnderMouse(
                localMousePosition, 0.8f, clickParams, targetMap, localMousePosition, source);

            var targets = new List<LocalTargetInfo>(things.Count);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (!(thing is Pawn pawn) || !pawn.IsPsychologicallyInvisible()
                    || caster == null || caster.Faction == pawn.Faction)
                {
                    targets.Add(thing);
                }
            }

            if (!thingsOnly)
            {
                var cellTarget = new LocalTargetInfo(targetLocalCell);
                if (clickParams.CanTarget(new TargetInfo(targetLocalCell, targetMap))) targets.Add(cellTarget);
            }
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
            var launchVerb = ___targetingSource?.GetVerb;
            if (!SeamlessDirectFireSupport.IsSupportedVerb(launchVerb))
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
            if (thing == null && SeamlessCrossMapCellTarget.TryResolve(__state.verb, __state.target,
                    out var cellLink, out _))
            {
                FleckMaker.Static(__state.target.Cell.ToVector3Shifted(), cellLink.target, FleckDefOf.FeedbackShoot);
                return;
            }
            if (thing?.Map == null || thing.Map == Find.CurrentMap) return;
            // FleckMaker's shared cross-map feedback patch projects this onto the focused map.
            FleckMaker.Static(thing.DrawPos, thing.Map, FleckDefOf.FeedbackShoot);
        }
    }

    /// <summary>
    /// Vanilla weapon hover highlighting consumes target.Thing in that Thing's local coordinates although
    /// GenDraw renders in the focused map's coordinate system. Reproduce that common drawing sink at the
    /// projected TrueCenter; the real Thing target retained by Targeter and the eventual order are untouched.
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawTargetHighlight))]
    public static class Patch_GenDraw_DrawTargetHighlight_SeamlessView
    {
        private static readonly FieldInfo TargetingSourceField =
            AccessTools.Field(typeof(Targeter), "targetingSource");

        public static bool Prefix(LocalTargetInfo targ)
        {
            var thing = targ.Thing;
            var viewMap = Find.CurrentMap;
            if (thing == null)
            {
                var source = TargetingSourceField?.GetValue(Find.Targeter) as ITargetingSource;
                if (!SeamlessCrossMapCellTarget.TryResolve(source?.GetVerb, targ, out _, out var unified)) return true;
                Graphics.DrawMesh(MeshPool.plane10, unified.ToVector3ShiftedWithAltitude(AltitudeLayer.Building),
                    Quaternion.identity, GenDraw.CurTargetingMat, 0);
                return false;
            }
            if (thing.Map == null || viewMap == null || thing.Map == viewMap) return true;
            if (!SeamlessViewProjection.TryProject(thing.Map, thing.TrueCenter(), viewMap, out var projected)) return true;

            projected.y = AltitudeLayer.MapDataOverlay.AltitudeFor();
            Graphics.DrawMesh(MeshPool.plane10, projected, thing.Rotation.AsQuat, GenDraw.CurTargetingMat, 0);
            if (thing is Pawn || thing is Corpse) TargetHighlighter.Highlight(thing, arrow: false);
            return false;
        }
    }

    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawTargetHighlightWithLayer),
        new[] { typeof(Vector3), typeof(AltitudeLayer) })]
    public static class Patch_GenDraw_DrawTargetHighlightWithLayer_CrossMapCell
    {
        private static readonly FieldInfo TargetingSourceField =
            AccessTools.Field(typeof(Targeter), "targetingSource");

        public static void Prefix(ref Vector3 c)
        {
            var source = TargetingSourceField?.GetValue(Find.Targeter) as ITargetingSource;
            var local = IntVec3.FromVector3(c);
            if (SeamlessCrossMapCellTarget.TryResolve(source?.GetVerb, new LocalTargetInfo(local),
                    out _, out var unified))
            {
                var offset = c - local.ToVector3Shifted();
                c = unified.ToVector3Shifted() + offset;
            }
        }
    }

    /// <summary>
    /// Ability highlights render both a target marker and (optionally) an effect-radius ring. Passing the
    /// unified cell at this visual boundary fixes both without changing the stored cast target.
    /// </summary>
    [HarmonyPatch(typeof(Verb_CastAbility), nameof(Verb_CastAbility.DrawHighlight))]
    public static class Patch_Verb_CastAbility_DrawHighlight_CrossMapCell
    {
        public static void Prefix(Verb_CastAbility __instance, ref LocalTargetInfo target)
        {
            if (!target.HasThing && SeamlessCrossMapCellTarget.TryResolve(__instance, target,
                    out _, out var unified)) target = new LocalTargetInfo(unified);
        }
    }

}
