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
    /// CE instant and moving projectile launch, guidance, impact and seam handoff adapters.
    /// </summary>
    public static partial class SeamlessCombatExtendedCompat
    {
        /// <summary>
        /// CE's instant projectiles never enter MoveForward, so the ordinary seam handoff cannot help
        /// them.  Re-run the original one-cell ray walk in the shooter's unified coordinate frame and
        /// route only the cell/Thing lookup to the owning map.  Damage and Impact remain CE's own.
        /// </summary>
        public static bool RayCastPrefix(object __instance, Thing launcher, VerbProperties verbProps,
            Vector2 origin, float shotAngle, float shotRotation, float shotHeight, float shotSpeed,
            float spreadDegrees, float aperatureSize, Thing equipment, bool useSameHeight)
        {
            var projectile = __instance as Thing;
            if (projectile?.Map == null || launcher?.Map == null || laserBeamType == null
                || !laserBeamType.IsInstanceOfType(__instance)) return true;
            var intended = intendedTargetField.GetValue(__instance) is LocalTargetInfo info
                ? info : LocalTargetInfo.Invalid;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unifiedTarget;
            if (intended.HasThing)
            {
                if (intended.Thing?.Map == null || intended.Thing.Map == launcher.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(launcher.Map, intended.Thing.Map, out link)) return true;
                unifiedTarget = intended.Thing.Position + link.offset;
            }
            else if (!SeamlessCrossMapCellTarget.TryResolveCurrent(intended, out link, out unifiedTarget)
                     && !SeamlessCrossMapCellTarget.TryResolve(launcher, intended, out link, out unifiedTarget))
            {
                return true;
            }

            var rayStateInitialized = false;
            try
            {
                var props = projectile.def.projectile;
                var radians = shotRotation * Mathf.Deg2Rad + Mathf.PI / 2f;
                var direction = new Vector3(Mathf.Cos(radians) * Mathf.Cos(shotAngle),
                    Mathf.Sin(shotAngle), Mathf.Sin(radians) * Mathf.Cos(shotAngle));
                var origin3 = new Vector3(origin.x, shotHeight, origin.y);
                var ray = new Ray(origin3, direction);
                var destination = ray.GetPoint(verbProps.range);
                var spreadRadius = Mathf.Sin(spreadDegrees / 2f * Mathf.Deg2Rad);
                var magicSpread = Mathf.Sin(0.03f * Mathf.Deg2Rad) + aperatureSize;
                var magicDamage = 1f / (magicSpread * magicSpread * Mathf.PI);
                var barrelLength = equipment?.def != null && laserGunDefType != null
                    && laserGunDefType.IsInstanceOfType(equipment.def) && laserBarrelLengthField != null
                    ? (float)laserBarrelLengthField.GetValue(equipment.def)
                    : 0.9f;
                var muzzle = ray.GetPoint(barrelLength);

                shotAngleField.SetValue(__instance, shotAngle);
                shotHeightField.SetValue(__instance, shotHeight);
                shotRotationField.SetValue(__instance, radians);
                launcherField.SetValue(__instance, launcher);
                originField.SetValue(__instance, origin);
                equipmentField.SetValue(__instance, equipment);
                equipmentDefField.SetValue(__instance, equipment?.def);
                rayStateInitialized = true;

                Thing hit = null;
                Map finalMap = launcher.Map;
                Vector3 finalUnified = destination;
                Vector3 lastValidUnified = origin3;
                var leftLoadedMaps = false;
                for (var i = 1; i < verbProps.range; i++)
                {
                    if ((bool)(damageFalloffField?.GetValue(props) ?? false))
                    {
                        var area = Mathf.Pow(i * spreadRadius + aperatureSize, 2f) * Mathf.PI;
                        laserDamageModifierField.SetValue(__instance, 1f / (magicDamage * area));
                    }
                    var point = ray.GetPoint(i);
                    if (point.y < 0f)
                    {
                        finalUnified = point;
                        break;
                    }
                    var unifiedCell = point.ToIntVec3();
                    if (!TryResolveCell(launcher.Map, unifiedCell, out var ownerMap, out var ownerLocal))
                    {
                        finalUnified = ray.GetPoint(i - 1);
                        leftLoadedMaps = true;
                        break;
                    }
                    finalMap = ownerMap;
                    lastValidUnified = point;
                    foreach (var candidate in ownerMap.thingGrid.ThingsListAtFast(ownerLocal))
                    {
                        if (candidate == projectile) continue;
                        var bounds = (Bounds)getBoundsMethod.Invoke(null, new object[] { candidate });
                        if (ownerMap != launcher.Map
                            && SeamlessCombatCoords.TryGetCombatLink(launcher.Map, ownerMap, out var ownerLink))
                            bounds.center += new Vector3(ownerLink.offset.x, 0f, ownerLink.offset.z);
                        if (!bounds.IntersectRay(ray, out _) || (i < 2 && candidate != intended.Thing)) continue;
                        if (candidate is Plant plant && !Rand.Chance(candidate.def.fillPercent * plant.Growth)) continue;
                        if (candidate is Building && !Rand.Chance(candidate.def.fillPercent)) continue;
                        hit = candidate;
                        finalUnified = point;
                        goto RayFinished;
                    }
                }

            RayFinished:
                if (hit == null && !leftLoadedMaps
                    && !TryResolveCell(launcher.Map, finalUnified.ToIntVec3(), out finalMap, out _))
                {
                    finalUnified = lastValidUnified;
                    TryResolveCell(launcher.Map, finalUnified.ToIntVec3(), out finalMap, out _);
                }
                RelocateInstantProjectile(projectile, launcher.Map, finalMap, origin3, finalUnified);
                var mapOffset = MapOffsetToHost(launcher.Map, finalMap);
                var localMuzzle = muzzle - mapOffset;
                var localDestination = finalUnified - mapOffset;
                if (useSameHeight) localMuzzle.y = localDestination.y;
                laserSpawnBeamMethod.Invoke(__instance, new object[] { localMuzzle, localDestination });
                ApplyRaySuppressionByMap(__instance, launcher.Map, muzzle, finalUnified);
                if (hit != null || leftLoadedMaps)
                    laserImpactMethod.Invoke(__instance, new object[] { hit, localMuzzle });
                else
                    projectile.Destroy(DestroyMode.Vanish);
                DebugLog($"ProjectileCE.RayCast id={projectile.thingIDNumber} map={launcher.Map.uniqueID}->{finalMap.uniqueID} "
                    + $"target={unifiedTarget} end={finalUnified} hit={hit?.LabelShort ?? "null"}");
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE instant-ray cross-map routing failed open: {ex}", 0x43455209);
                // Once projectile state has been initialized, running CE's original ray as well would
                // double-hit or operate in a half-relocated coordinate frame. Treat that one beam as a
                // failed shot; signature/binding failures before mutation still safely fall through.
                return !rayStateInitialized;
            }
        }

        private static Vector3 MapOffsetToHost(Map host, Map map)
        {
            if (host == map) return Vector3.zero;
            return SeamlessCombatCoords.TryGetCombatLink(host, map, out var link)
                ? new Vector3(link.offset.x, 0f, link.offset.z)
                : Vector3.zero;
        }

        private static void RelocateInstantProjectile(Thing projectile, Map host, Map finalMap,
            Vector3 originUnified, Vector3 destinationUnified)
        {
            var offset = MapOffsetToHost(host, finalMap);
            var localOrigin = originUnified - offset;
            var localDestination = destinationUnified - offset;
            if (projectile.Map != finalMap)
            {
                projectile.DeSpawn();
                GenSpawn.Spawn(projectile, localDestination.ToIntVec3(), finalMap);
            }
            originField.SetValue(projectile, new Vector2(localOrigin.x, localOrigin.z));
            originIv3Field?.SetValue(projectile, localOrigin.ToIntVec3());
            destinationField.SetValue(projectile, new Vector2(localDestination.x, localDestination.z));
            lastPosField.SetValue(projectile, localDestination);
            exactPositionProperty.SetValue(projectile, localDestination, null);
            landedField?.SetValue(projectile, true);
        }

        private static void ApplyRaySuppressionByMap(object projectile, Map host,
            Vector3 startUnified, Vector3 endUnified)
        {
            if (rayCastSuppressionMethod == null) return;
            Map segmentMap = null;
            IntVec3 first = default;
            IntVec3 last = default;
            foreach (var unified in GenSight.PointsOnLineOfSight(startUnified.ToIntVec3(), endUnified.ToIntVec3()))
            {
                if (!TryResolveCell(host, unified, out var map, out var local)) continue;
                if (segmentMap != map)
                {
                    if (segmentMap != null && first != last)
                        rayCastSuppressionMethod.Invoke(projectile, new object[] { first, last, segmentMap });
                    segmentMap = map;
                    first = local;
                }
                last = local;
            }
            if (segmentMap != null && first != last)
                rayCastSuppressionMethod.Invoke(projectile, new object[] { first, last, segmentMap });
        }

        public static void LaunchCorePostfix(object __instance, Thing launcher, Vector2 origin)
        {
            if (!DebugEnabled) return;
            var projectile = __instance as Thing;
            if (!IsProjectile(projectile)) return;

            var target = intendedTargetField?.GetValue(__instance) is LocalTargetInfo info ? info : LocalTargetInfo.Invalid;
            // This switch diagnoses RimExodus' cross-map path, not CE combat globally. In particular,
            // rocket explosions may create hundreds of same-map fragments and would otherwise suppress
            // the launch/handoff/impact events that the log is intended to preserve.
            var crossMap = target.HasThing
                ? target.Thing?.Map != null && projectile.Map != null && target.Thing.Map != projectile.Map
                    && SeamlessCombatCoords.TryGetCombatLink(projectile.Map, target.Thing.Map, out _)
                : SeamlessCrossMapCellTarget.TryResolveCurrent(target, out _, out _);
            if (!crossMap) return;
            var destination = destinationField?.GetValue(__instance);
            var aimDiagnostic = string.Empty;
            if (destination is Vector2 destinationVec && projectile.Map != null && target.Thing?.Map != null
                && target.Thing.Map != projectile.Map
                && SeamlessCombatCoords.TryGetCombatLink(projectile.Map, target.Thing.Map, out var link))
            {
                var targetCenter = target.Thing.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z);
                var intendedVector = new Vector2(targetCenter.x - origin.x, targetCenter.z - origin.y);
                var launchedVector = destinationVec - origin;
                var angularError = intendedVector.sqrMagnitude > 0.0001f && launchedVector.sqrMagnitude > 0.0001f
                    ? Vector2.Angle(intendedVector, launchedVector)
                    : 0f;
                aimDiagnostic = $" unifiedTarget=({targetCenter.x:F2}, {targetCenter.z:F2})"
                    + $" aimDist={intendedVector.magnitude:F2} angularErrorDeg={angularError:F3}";
            }
            DebugLog($"ProjectileCE.LaunchCore id={projectile.thingIDNumber} type={projectile.GetType().FullName} "
                + $"def={projectile.def?.defName ?? "null"} map={projectile.Map?.uniqueID.ToString() ?? "null"} "
                + $"launcher={launcher?.LabelShort ?? "null"} origin={origin} destination={destination?.ToString() ?? "null"} "
                + $"targetThing={target.Thing?.LabelShort ?? "null"} targetMap={target.Thing?.Map?.uniqueID.ToString() ?? "null"} "
                + $"targetCell={target.Cell}{aimDiagnostic}");
        }

        public static void LaunchPostfix(object __instance)
        {
            var thing = __instance as Thing;
            if (!IsProjectile(thing) || thing.Map == null || !IsSupportedProjectileInstance(__instance)) return;
            var target = (LocalTargetInfo)intendedTargetField.GetValue(__instance);
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            if (target.HasThing)
            {
                if (target.Thing.Map == null || target.Thing.Map == thing.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(thing.Map, target.Thing.Map, out link)) return;
                unified = target.Cell + link.offset;
            }
            else if (!SeamlessCrossMapCellTarget.TryResolveCurrent(target, out link, out unified))
            {
                return;
            }
            MarkProjectileTarget(thing, link.target, target.Cell);
            var distance = (unified - thing.Position).LengthHorizontal;
            minCollisionDistanceField.SetValue(__instance, distance <= 7.5f ? Mathf.Min(1.5f, distance * 0.75f) : distance * 0.2f);
        }

        public static void ThrowPostfix(object __instance)
        {
            var thing = __instance as Thing;
            if (!IsProjectile(thing) || thing.Map == null || !IsSupportedProjectileInstance(__instance)) return;
            var target = (LocalTargetInfo)intendedTargetField.GetValue(__instance);
            if (target.HasThing && target.Thing.Map != null && target.Thing.Map != thing.Map
                && SeamlessCombatCoords.TryGetCombatLink(thing.Map, target.Thing.Map, out var thingLink))
            {
                MarkProjectileTarget(thing, thingLink.target, target.Cell);
            }
            else if (!target.HasThing
                && SeamlessCrossMapCellTarget.TryResolveCurrent(target, out var cellLink, out _))
            {
                MarkProjectileTarget(thing, cellLink.target, target.Cell);
            }
        }

        private static void MarkProjectileTarget(Thing projectile, Map map, IntVec3 local)
        {
            ProjectileTargets.Remove(projectile);
            ProjectileTargets.Add(projectile, new ProjectileTargetState { targetMap = map, targetLocal = local });
        }

        public sealed class GuidedTargetState
        {
            public SeamlessVirtualTeleporter teleporter;
            public LocalTargetInfo originalTarget;
            public object projectile;
            public bool replacedCell;
            public bool restored;
        }

        public static void GuidedTargetPrefix(object projectile, out GuidedTargetState __state)
        {
            __state = null;
            var thing = projectile as Thing;
            if (!IsProjectile(thing) || thing.Map == null) return;
            var target = (LocalTargetInfo)intendedTargetField.GetValue(projectile);
            if (target.HasThing)
            {
                var targetThing = target.Thing;
                if (targetThing?.Map == null || targetThing.Map == thing.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(thing.Map, targetThing.Map,
                        out var link)) return;
                __state = new GuidedTargetState
                {
                    teleporter = new SeamlessVirtualTeleporter(targetThing, thing.Map,
                        targetThing.Position + link.offset)
                };
                return;
            }
            if (!ProjectileTargets.TryGetValue(thing, out var saved) || saved.targetMap == null
                || saved.targetMap == thing.Map
                || !SeamlessCombatCoords.TryGetCombatLink(thing.Map, saved.targetMap,
                    out var savedLink)) return;
            var targetOnCurrent = saved.targetLocal + savedLink.offset;
            __state = new GuidedTargetState
            {
                projectile = projectile,
                originalTarget = target,
                replacedCell = true
            };
            intendedTargetField.SetValue(projectile, new LocalTargetInfo(targetOnCurrent));
        }

        public static void GuidedTargetPostfix(GuidedTargetState __state)
        {
            RestoreGuidedTarget(__state);
        }

        private static void RestoreGuidedTarget(GuidedTargetState state)
        {
            if (state == null || state.restored) return;
            state.teleporter.Dispose();
            if (state.replacedCell && state.projectile != null)
                intendedTargetField.SetValue(state.projectile, state.originalTarget);
            state.restored = true;
        }

        public static Exception GuidedTargetFinalizer(Exception __exception, GuidedTargetState __state)
        {
            RestoreGuidedTarget(__state);
            return __exception;
        }

        public static void ImpactPrefix(object __instance, Thing hitThing)
        {
            if (!DebugEnabled) return;
            var projectile = __instance as Thing;
            if (!IsProjectile(projectile)) return;
            var intendedInfo = intendedTargetField?.GetValue(__instance) is LocalTargetInfo info
                ? info : LocalTargetInfo.Invalid;
            var intended = intendedInfo.Thing;
            var tracked = ProjectileTargets.TryGetValue(projectile, out _);
            var instantCrossMap = intended?.Map != null && projectile.Map != null && intended.Map != projectile.Map
                && SeamlessCombatCoords.TryGetCombatLink(projectile.Map, intended.Map, out _);
            if (!tracked && !instantCrossMap) return;
            var exact = exactPositionProperty?.GetValue(__instance, null);
            DebugLog($"ProjectileCE.Impact id={projectile.thingIDNumber} type={projectile.GetType().FullName} "
                + $"projectile={projectile.LabelShort} map={projectile.Map?.uniqueID.ToString() ?? "null"} "
                + $"exact={exact?.ToString() ?? "null"} hit={hitThing?.LabelShort ?? "ground/null"} "
                + $"hitCell={hitThing?.Position.ToString() ?? "null"} intended={intended?.LabelShort ?? "null"} "
                + $"intendedMap={intended?.Map?.uniqueID.ToString() ?? "null"} intendedCell={intended?.Position.ToString() ?? "null"}");
        }

        public static void MoveForwardPostfix(object __instance, ref Vector3 __result)
        {
            var thing = __instance as Thing;
            if (!IsProjectile(thing) || thing.Destroyed || !thing.Spawned || !IsSupportedProjectileInstance(__instance)) return;
            var map = thing.Map;
            if (map == null || !SeamlessCombatCoords.HasActiveSeamNeighbors(map)) return;
            if (!SeamlessTileRegistry.TryGetOwnerNeighbor(map, __result.ToIntVec3(), out var ownerMap, out var ownerLocal)) return;
            if (ownerMap == null || !ownerLocal.InBounds(ownerMap)) return;
            var tile = SeamlessTileRegistry.GetMapWorldTile(ownerMap);
            if (!SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, tile, out var info)) return;

            try
            {
                var dx = info.offset.x;
                var dz = info.offset.z;
                var offset3 = new Vector3(dx, 0f, dz);
                var origin = (Vector2)originField.GetValue(__instance);
                originField.SetValue(__instance, new Vector2(origin.x - dx, origin.y - dz));
                originIv3Field.SetValue(__instance, (IntVec3)originIv3Field.GetValue(__instance) - info.offset);
                var destination = (Vector2)destinationField.GetValue(__instance);
                destinationField.SetValue(__instance, new Vector2(destination.x - dx, destination.y - dz));
                lastPosField.SetValue(__instance, (Vector3)lastPosField.GetValue(__instance) - offset3);
                var exact = (Vector3)exactPositionProperty.GetValue(__instance, null) - offset3;
                __result -= offset3;

                if (predictedField.GetValue(__instance) is IList predicted)
                {
                    for (var i = 0; i < predicted.Count; i++) predicted[i] = (Vector3)predicted[i] - offset3;
                }
                dangerTrackerField?.SetValue(__instance, null);
                lastShotLineField?.SetValue(__instance, -1);

                thing.DeSpawn();
                exactPositionProperty.SetValue(__instance, exact, null);
                GenSpawn.Spawn(thing, __result.ToIntVec3(), ownerMap);
                if (DebugEnabled) DebugLog($"ProjectileCE.Handoff id={thing.thingIDNumber} type={thing.GetType().FullName} "
                    + $"map={map.uniqueID}->{ownerMap.uniqueID} next={__result} local={ownerLocal}");
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE projectile seam handoff failed: {ex}", 0x43455202);
            }
        }

        private static bool IsSupportedProjectileInstance(object projectile)
        {
            try
            {
                var thing = (Thing)projectile;
                var props = thing.def?.projectile;
                if (props == null || !projectilePropsType.IsInstanceOfType(props)) return false;
                if ((bool)isInstantField.GetValue(props)) return false;
                if (globalTargetField != null && ((GlobalTargetInfo)globalTargetField.GetValue(projectile)).IsValid) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
