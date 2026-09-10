using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// CE CIWS and rotating-turret evaluation/search adapters.
    /// </summary>
    public static partial class SeamlessCombatExtendedCompat
    {
        public sealed class CiwsEvaluationState
        {
            public SeamlessVirtualTeleporter teleporter;
            public object projectile;
            public Vector3 exact;
            public Vector2 origin;
            public IntVec3 originCell;
            public Vector2 destination;
            public Vector3 last;
            public object predictions;
            public bool restored;
        }

        public static void CiwsShootLinePrefix(object __instance, LocalTargetInfo targetInfo,
            out CiwsEvaluationState __state)
        {
            var verb = __instance as Verb;
            var target = targetInfo.Thing;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            __state = BeginCiwsEvaluation(caster, target);
        }

        public static void CiwsProjectileCollisionPrefix(object __instance, Thing thing,
            out CiwsEvaluationState __state)
        {
            __state = BeginCiwsEvaluation(__instance as Thing, thing);
        }

        private static CiwsEvaluationState BeginCiwsEvaluation(Thing observer, Thing target)
        {
            if (observer?.Map == null || target?.Map == null || target.Map == observer.Map
                || !SeamlessCombatCoords.TryGetCombatLink(observer.Map, target.Map, out var link)) return null;

            var state = new CiwsEvaluationState
            {
                teleporter = new SeamlessVirtualTeleporter(target, observer.Map, target.Position + link.offset)
            };
            if (IsProjectile(target))
            {
                var offset3 = new Vector3(link.offset.x, 0f, link.offset.z);
                state.projectile = target;
                state.exact = (Vector3)exactPositionField.GetValue(target);
                state.origin = (Vector2)originField.GetValue(target);
                state.originCell = (IntVec3)originIv3Field.GetValue(target);
                state.destination = (Vector2)destinationField.GetValue(target);
                state.last = (Vector3)lastPosField.GetValue(target);
                state.predictions = predictedField.GetValue(target);
                exactPositionField.SetValue(target, state.exact + offset3);
                originField.SetValue(target, state.origin + new Vector2(link.offset.x, link.offset.z));
                originIv3Field.SetValue(target, state.originCell + link.offset);
                destinationField.SetValue(target, state.destination + new Vector2(link.offset.x, link.offset.z));
                lastPosField.SetValue(target, state.last + offset3);
                predictedField.SetValue(target, null);
            }
            return state;
        }

        public static void CiwsShootLinePostfix(CiwsEvaluationState __state)
        {
            RestoreCiwsEvaluation(__state);
        }

        private static void RestoreCiwsEvaluation(CiwsEvaluationState state)
        {
            if (state == null || state.restored) return;
            if (state.projectile != null)
            {
                exactPositionField.SetValue(state.projectile, state.exact);
                originField.SetValue(state.projectile, state.origin);
                originIv3Field.SetValue(state.projectile, state.originCell);
                destinationField.SetValue(state.projectile, state.destination);
                lastPosField.SetValue(state.projectile, state.last);
                predictedField.SetValue(state.projectile, state.predictions);
            }
            state.teleporter.Dispose();
            state.restored = true;
        }

        public static Exception CiwsEvaluationFinalizer(Exception __exception, CiwsEvaluationState __state)
        {
            RestoreCiwsEvaluation(__state);
            return __exception;
        }

        public static void CiwsFindTargetPostfix(object __instance, ref LocalTargetInfo target, ref bool __result)
        {
            if (__result || !initialized || !ciwsBaseType.IsInstanceOfType(__instance)) return;
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            if (caster?.Map == null) return;
            try
            {
                var active = ciwsActiveProperty?.GetValue(__instance, null);
                if (!(active is bool enabled) || !enabled) return;
                var props = ciwsPropsProperty?.GetValue(__instance, null);
                var ignored = ciwsIgnoredProperty?.GetValue(props, null) as IEnumerable;
                var ignoredDefs = ignored?.Cast<object>().OfType<ThingDef>().ToHashSet() ?? new HashSet<ThingDef>();
                var turret = ciwsTurretProperty?.GetValue(__instance, null);
                var turretIgnored = ciwsTurretIgnoredProperty?.GetValue(turret, null) as IEnumerable;
                if (turretIgnored != null)
                    foreach (var value in turretIgnored) if (value is ThingDef def) ignoredDefs.Add(def);

                var neighbors = Patches_CombatTargetSearch.TempNeighbors;
                neighbors.Clear();
                SeamlessTileGraph.PopulateNeighbors(caster.Map, neighbors);
                for (var n = 0; n < neighbors.Count; n++)
                {
                    var map = neighbors[n].map;
                    if (map == null) continue;
                    foreach (var candidate in CiwsCandidates(__instance.GetType(), map))
                    {
                        if (candidate == null || !candidate.Spawned || candidate.Destroyed
                            || ignoredDefs.Contains(candidate.def)
                            || ciwsInterceptableMethod == null
                            || !(bool)ciwsInterceptableMethod.Invoke(props, new object[] { candidate.def })) continue;
                        if (IsCiwsTargetAlreadyClaimed(caster.Map, map, candidate)) continue;
                        CiwsFriendlyMethods.TryGetValue(__instance.GetType(), out var friendly);
                        if (friendly != null && (bool)friendly.Invoke(__instance, new object[] { candidate })) continue;
                        if (!CiwsShootLineMethods.TryGetValue(__instance.GetType(), out var lineMethod))
                        {
                            lineMethod = AccessTools.Method(__instance.GetType(), "TryFindCEShootLineFromTo",
                                new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(Vector3).MakeByRefType() });
                            CiwsShootLineMethods[__instance.GetType()] = lineMethod;
                        }
                        var args = new object[] { caster.Position, new LocalTargetInfo(candidate), default(ShootLine), default(Vector3) };
                        if (lineMethod == null || !(bool)lineMethod.Invoke(__instance, args)) continue;
                        var line = (ShootLine)args[2];
                        var distanceSq = line.Dest.DistanceToSquared(caster.Position);
                        var min = verb.verbProps.EffectiveMinRange(candidate, caster);
                        if (distanceSq <= min * min || distanceSq >= verb.verbProps.range * verb.verbProps.range) continue;
                        target = candidate;
                        __result = true;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE CIWS neighbor search failed safely: {ex}", 0x4345520b);
            }
        }

        private static IEnumerable<Thing> CiwsCandidates(Type verbRuntimeType, Map map)
        {
            if (ciwsProjectileVerbType.IsAssignableFrom(verbRuntimeType))
                return map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Where(IsProjectile);
            if (ciwsSkyfallerVerbType.IsAssignableFrom(verbRuntimeType))
                return map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).OfType<Skyfaller>().Cast<Thing>();
            if (ciwsCompSkyfallerVerbType.IsAssignableFrom(verbRuntimeType))
            {
                return map.listerThings.AllThings.Where(t => t is ThingWithComps twc
                    && twc.AllComps.Any(c => ciwsCompSkyfallerTargetType.IsInstanceOfType(c)));
            }
            return Enumerable.Empty<Thing>();
        }

        private static bool IsCiwsTargetAlreadyClaimed(Map host, Map targetMap, Thing candidate)
        {
            foreach (var map in host == targetMap ? new[] { host } : new[] { host, targetMap })
            {
                var tracker = map.components.FirstOrDefault(c => ciwsTrackerType.IsInstanceOfType(c));
                if (tracker != null && ciwsTurretsField.GetValue(tracker) is IEnumerable turrets)
                {
                    foreach (var turret in turrets)
                    {
                        if (turretCurrentTargetField.GetValue(turret) is LocalTargetInfo current
                            && current.Thing == candidate) return true;
                    }
                }
                foreach (var projectile in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
                {
                    if (ciwsProjectileType.IsInstanceOfType(projectile)
                        && intendedTargetField.GetValue(projectile) is LocalTargetInfo intended
                        && intended.Thing == candidate) return true;
                }
            }
            return false;
        }

        public sealed class TargetTeleportState
        {
            public SeamlessVirtualTeleporter teleporter;
            public bool restored;
        }

        public static void FireArcPrefix(ThingComp __instance, LocalTargetInfo tgt, out TargetTeleportState __state)
        {
            __state = TryTeleportTarget(__instance?.parent, tgt);
        }

        public static void TurretTargetPrefix(object __instance, out TargetTeleportState __state)
        {
            __state = null;
            var turret = __instance as Thing;
            if (turret == null) return;
            if (turretCurrentTargetProperty == null) return;
            var current = (LocalTargetInfo)turretCurrentTargetProperty.GetValue(__instance, null);
            __state = TryTeleportTarget(turret, current);
        }

        public static void TurretOrderPrefix(object __instance, LocalTargetInfo targ, out TargetTeleportState __state)
        {
            __state = TryTeleportTarget(__instance as Thing, targ);
        }

        public static void TargetTeleportPostfix(TargetTeleportState __state)
        {
            RestoreTargetTeleport(__state);
        }

        private static void RestoreTargetTeleport(TargetTeleportState state)
        {
            if (state == null || state.restored) return;
            state.teleporter.Dispose();
            state.restored = true;
        }

        public static Exception TargetTeleportFinalizer(Exception __exception, TargetTeleportState __state)
        {
            RestoreTargetTeleport(__state);
            return __exception;
        }

        private static TargetTeleportState TryTeleportTarget(Thing caster, LocalTargetInfo target)
        {
            var thing = target.Thing;
            if (caster?.Map == null || thing?.Map == null || caster.Map == thing.Map) return null;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, thing.Map, out var link)) return null;
            var unified = thing.Position + link.offset;
            // CompFireArc.WithinFireArc, Building_TurretGunCE.DeltaAngle and OrderAttack's range
            // gates consume Position only as continuous geometry. A legitimate neighbor-map point is
            // commonly outside the caster map's rectangular bounds even though it belongs to the
            // seamless surface. SeamlessVirtualTeleporter writes backing fields directly and these
            // short read-only evaluations never touch caster.Map grids, so an out-of-bounds unified
            // coordinate is both required and safe here.
            return new TargetTeleportState { teleporter = new SeamlessVirtualTeleporter(thing, caster.Map, unified) };
        }

        /// <summary>
        /// Neighbor-only fallback after CE found no local target. This currently preserves eligibility
        /// but selects the nearest shootable candidate; it is not CE's weighted non-snap scorer. Calling
        /// that private scorer directly would still read cross-map distance/angle/cover in mixed local
        /// coordinates, so semantic parity requires a routed score implementation rather than reflection.
        /// </summary>
        public static void NonSnapBestAttackTargetPostfix(IAttackTargetSearcher searcher, TargetScanFlags flags,
            Predicate<Thing> validator, float minDist, float maxDist, ref IAttackTarget __result)
        {
            if (__result != null || !initialized) return;
            var searcherThing = searcher?.Thing;
            var verb = searcher?.CurrentEffectiveVerb;
            if (searcherThing?.Map == null || !IsSupportedVerb(verb)) return;

            var neighbors = Patches_CombatTargetSearch.TempNeighbors;
            neighbors.Clear();
            SeamlessTileGraph.PopulateNeighbors(searcherThing.Map, neighbors);
            var minSq = minDist * minDist;
            var maxSq = maxDist * maxDist;
            IAttackTarget best = null;
            var bestSq = float.MaxValue;
            for (var n = 0; n < neighbors.Count; n++)
            {
                var neighbor = neighbors[n];
                if (!SeamlessCombatCoords.TryGetCombatLink(searcherThing.Map, neighbor.map, out var link)) continue;
                var pool = neighbor.map.attackTargetsCache.GetPotentialTargetsFor(searcher);
                for (var i = 0; i < pool.Count; i++)
                {
                    var candidate = pool[i];
                    var thing = candidate?.Thing;
                    if (thing == null || !thing.Spawned || thing.Destroyed || thing == searcherThing) continue;
                    var distSq = searcherThing.Position.DistanceToSquared(thing.Position + link.offset);
                    if (distSq < minSq || distSq > maxSq || distSq >= bestSq) continue;
                    if (!searcherThing.HostileTo(thing) || (validator != null && !validator(thing))) continue;
                    if ((flags & TargetScanFlags.NeedThreat) != 0 && candidate.ThreatDisabled(searcher)) continue;
                    if ((flags & TargetScanFlags.NeedActiveThreat) != 0 && !GenHostility.IsActiveThreatTo(candidate, searcherThing.Faction)) continue;
                    if (!verb.CanHitTarget(thing)) continue;
                    best = candidate;
                    bestSq = distSq;
                }
            }
            if (best != null) __result = best;
        }
    }
}
