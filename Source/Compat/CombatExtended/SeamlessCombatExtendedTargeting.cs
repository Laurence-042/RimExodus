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
    /// CE cross-map verb targeting, trajectory reports and routed environment sampling.
    /// </summary>
    public static partial class SeamlessCombatExtendedCompat
    {
        public static bool TryFindShootLinePrefix(object __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref Vector3 targetPos, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null) return true;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            if (target != null)
            {
                if (target.Map == null || target.Map == caster.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out link)) return true;
                unified = target.Position + link.offset;
                // CE may replace a lost Thing with lastTargetPos during a suppressive burst. Preserve
                // the real map identity now so that later cell-only continuation uses the same frame.
                SeamlessCrossMapCellTarget.Register(verb, caster, caster.Map, target.Map, target.Position);
            }
            else if (!SeamlessCrossMapCellTarget.TryResolve(verb, targ, out link, out unified))
            {
                return true;
            }

            __result = SeamlessCrossMapSight.TryFindShootLine(verb, root, targ, in link, false, out resultingLine);
            if (__result && target != null)
            {
                if (!CanPassCeVerticalCover(__instance, caster, target, resultingLine, in link)) __result = false;
            }
            targetPos = target != null
                ? target.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z)
                : unified.ToVector3Shifted();
            if (__result) SeamlessCombatCoords.MarkCrossMapCast(verb, link.target);
            return false;
        }

        public static void CeTryCastPrefix(object __instance, out Verb __state)
        {
            __state = SeamlessCrossMapCellTarget.BeginFiring(__instance as Verb);
        }

        public static Exception CeTryCastFinalizer(Exception __exception, Verb __state)
        {
            SeamlessCrossMapCellTarget.EndFiring(__state);
            return __exception;
        }

        public static void AbilityLaunchPrefix(ThingDef projectileDef, Vector2 origin,
            LocalTargetInfo target, Thing shooter, ref float shotAngle, ref float shotRotation,
            float shotHeight, float shotSpeed)
        {
            CorrectUtilityLaunchAngles(projectileDef, origin, target, shooter,
                ref shotAngle, ref shotRotation, shotHeight, shotSpeed);
        }

        public static void VehicleAbilityLaunchPrefix(ThingDef projectileDef, Vector2 origin,
            LocalTargetInfo target, Pawn launcher, ref float shotAngle, ref float shotRotation,
            float shotHeight, float shotSpeed)
        {
            CorrectUtilityLaunchAngles(projectileDef, origin, target, launcher,
                ref shotAngle, ref shotRotation, shotHeight, shotSpeed);
        }

        private static void CorrectUtilityLaunchAngles(ThingDef projectileDef, Vector2 origin,
            LocalTargetInfo target, Thing shooter, ref float shotAngle, ref float shotRotation,
            float shotHeight, float shotSpeed)
        {
            if (shooter?.Map == null || projectileDef?.projectile == null
                || !projectilePropsType.IsInstanceOfType(projectileDef.projectile)) return;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            if (target.HasThing)
            {
                if (target.Thing?.Map == null || target.Thing.Map == shooter.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(shooter.Map, target.Thing.Map, out link)) return;
                unified = target.Thing.Position + link.offset;
            }
            else if (!SeamlessCrossMapCellTarget.TryResolve(shooter, target, out link, out unified))
            {
                return;
            }

            try
            {
                var props = projectileDef.projectile;
                var worker = trajectoryWorkerProperty.GetValue(props, null);
                var source = new Vector3(origin.x, shotHeight, origin.y);
                var targetPos = target.HasThing
                    ? target.Thing.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z)
                    : unified.ToVector3Shifted();
                if (target.HasThing && getBoundsMethod != null)
                    targetPos.y = ((Bounds)getBoundsMethod.Invoke(null, new object[] { target.Thing })).max.y;
                shotAngle = (float)trajectoryShotAngleMethod.Invoke(worker,
                    new object[] { props, source, targetPos, (float?)shotSpeed });
                shotRotation = (float)trajectoryShotRotationMethod.Invoke(worker,
                    new object[] { props, source, targetPos });
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE ability cross-map trajectory correction failed open: {ex}", 0x43455208);
            }
        }

        public static bool GrenadeFindAnglePrefix(object __instance, LocalTargetInfo target,
            ref float smokeDensity, ref bool roofed, ref float launchAngle, ref float speed,
            ref int ticks, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            if (!TryGetCrossMapTarget(verb, target, out var link, out var targetUnified)) return true;
            try
            {
                smokeDensity = 0f;
                roofed = false;
                ticks = 0;
                var targetThing = target.Thing;
                var sourceCenter = caster.TrueCenter();
                var targetCenter = targetThing != null
                    ? targetThing.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z)
                    : targetUnified.ToVector3Shifted();
                var horizontal = targetCenter - sourceCenter;
                horizontal.y = 0f;
                var distance = horizontal.magnitude;
                if (distance < 0.001f) { __result = false; return false; }

                var projectileDef = projectileProperty.GetValue(verb, null) as ThingDef;
                var props = projectileDef?.projectile;
                if (props == null) { __result = false; return false; }
                var gravityPerHeight = (float)gravityPerHeightProperty.GetValue(props, null);
                var gravity = gravityPerHeight / GenTicks.TicksPerRealSecond / GenTicks.TicksPerRealSecond;
                var shooter = shooterPawnProperty?.GetValue(__instance, null) as Pawn;
                var manipulation = shooter?.health?.capacities?.GetLevel(PawnCapacityDefOf.Manipulation) ?? 1f;
                var maxSpeed = (float)shotSpeedProperty.GetValue(__instance, null) * manipulation
                    / GenTicks.TicksPerRealSecond;
                var targetHeight = 0f;
                if (targetThing != null)
                {
                    var targetBounds = (Bounds)getBoundsMethod.Invoke(null, new object[] { targetThing });
                    targetHeight = (targetBounds.max.y + targetBounds.min.y) * 0.5f;
                }
                var shotHeight = (float)shotHeightProperty.GetValue(__instance, null);
                var heightOffset = targetHeight - shotHeight;
                var discriminant = maxSpeed * maxSpeed * maxSpeed * maxSpeed
                    - gravity * (gravity * distance * distance + 2f * heightOffset * maxSpeed * maxSpeed);
                if (discriminant < 0f)
                {
                    launchAngle = 0f;
                    speed = 0f;
                    __result = false;
                    return false;
                }

                launchAngle = Mathf.Atan((maxSpeed * maxSpeed - Mathf.Sqrt(discriminant))
                    / (gravity * distance));
                speed = maxSpeed;
                var cells = new List<IntVec3>(GenSight.PointsOnLineOfSight(caster.Position, targetUnified));
                var midway = false;
                for (var i = 1; i < cells.Count; i++)
                {
                    var unified = cells[i];
                    if (!TryResolveCell(link.host, unified, out var map, out var local)) continue;
                    if (local.AnyGas(map, GasType.BlindSmoke)) smokeDensity += GasUtility.BlindingGasAccuracyPenalty;
                    roofed |= map.roofGrid.RoofAt(local) != null;
                    var cover = local.GetFirstPawn(map) ?? local.GetCover(map);
                    if (cover == null || cover == targetThing) continue;

                    midway |= i >= cells.Count / 2;
                    var px = midway ? i + 0.5f : i - 0.5f;
                    var py = ((Bounds)getBoundsMethod.Invoke(null, new object[] { cover })).max.y * 1.1f;
                    var a = py / (px * (px - distance));
                    var b = -a * distance;
                    var vertexX = -b / (2f * a);
                    var vertexY = a * vertexX * vertexX + b * vertexX;
                    var timeToVertex = Mathf.Sqrt(2f * vertexY / gravity);
                    var requiredAngle = Mathf.Atan((4f * vertexY) / distance);
                    var requiredSpeed = distance / (2f * timeToVertex * Mathf.Cos(requiredAngle));
                    if (requiredAngle <= launchAngle) continue;
                    launchAngle = requiredAngle;
                    speed = requiredSpeed;
                    if (speed > maxSpeed) { __result = false; return false; }
                }

                ticks = (int)(distance / (Mathf.Cos(launchAngle) * speed)) + 1;
                ticks = Rand.RangeInclusive(ticks, props.explosionDelay);
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE cross-map grenade trajectory failed open: {ex}", 0x43455207);
                return true;
            }
        }

        public static void GrenadeShootLinePostfix(object __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref Vector3 targetPos, ref bool __result)
        {
            if (!__result || !TryGetCrossMapTarget(__instance as Verb, targ, out _, out var unified)) return;
            resultingLine = new ShootLine(root, unified);
            targetPos = targ.Thing != null
                ? targetPos
                : unified.ToVector3Shifted();
            grenadeDirectField?.SetValue(__instance, resultingLine);
        }

        public static void CellHighlightPrefix(object __instance, ref LocalTargetInfo target)
        {
            if (!target.HasThing && SeamlessCrossMapCellTarget.TryResolve(__instance as Verb, target,
                    out _, out var unified)) target = new LocalTargetInfo(unified);
        }

        public static void MortarReportPostfix(object __instance, LocalTargetInfo target, object __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            if (__result == null || caster?.Map == null
                || !TryGetCrossMapTarget(verb, target, out var link, out var unified)) return;
            try
            {
                var markerDef = DefDatabase<ThingDef>.GetNamedSilentFail("ArtilleryMarker");
                Thing marker = null;
                if (markerDef != null)
                {
                    marker = target.HasThing
                        ? target.Thing.GetAttachment(markerDef)
                        : target.Cell.GetFirstThing(link.target, markerDef);
                }
                if (marker != null)
                {
                    SetField(__result, "indirectFireShift", 0f);
                    CopyField(marker, __result, "aimingAccuracy");
                    CopyField(marker, __result, "sightsEfficiency");
                    CopyField(marker, __result, "weatherShift");
                    CopyField(marker, __result, "lightingShift");
                    return;
                }

                var distance = (unified - caster.Position).LengthHorizontal;
                var direct = distance <= 75f && SeamlessCrossMapSight.LineOfSightSegmented(
                    caster.Position, unified, in link, caster.Map, skipFirstCell: true);
                if (!direct)
                {
                    var ceProps = verbPropsCeProperty.GetValue(__instance, null);
                    var penalty = (float)indirectFirePenaltyField.GetValue(ceProps);
                    SetField(__result, "indirectFireShift", penalty * distance);
                    SetField(__result, "weatherShift", 0f);
                    SetField(__result, "lightingShift", 0f);
                    return;
                }

                SetField(__result, "indirectFireShift", 0f);
                SampleEnvironment(__result, caster, target.Thing, target.Cell, unified, in link);
                var ignoreMaluses = verb.EquipmentSource != null
                    && verb.EquipmentSource.TryGetComp(out CompUniqueWeapon unique)
                    && unique.IgnoreAccuracyMaluses;
                var eitherExposed = !caster.Position.Roofed(caster.Map)
                    || !target.Cell.Roofed(link.target);
                SetField(__result, "weatherShift", !ignoreMaluses && eitherExposed
                    ? 1f - caster.Map.weatherManager.CurWeatherAccuracyMultiplier
                    : 0f);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE mortar cross-map report correction failed: {ex}", 0x4345520a);
            }
        }

        private static void CopyField(object source, object destination, string name)
        {
            var sourceField = CachedField(source.GetType(), name);
            var destinationFieldInfo = CachedField(destination.GetType(), name);
            if (sourceField != null && destinationFieldInfo != null)
                destinationFieldInfo.SetValue(destination, sourceField.GetValue(source));
        }

        private static bool TryGetCrossMapTarget(Verb verb, LocalTargetInfo target,
            out SeamlessCombatCoords.CombatLink link, out IntVec3 unified)
        {
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            return SeamlessCrossMapCellTarget.TryResolve(caster, verb, target, out link, out unified);
        }

        private static bool CanPassCeVerticalCover(object ceVerb, Thing caster, Thing target, ShootLine line,
            in SeamlessCombatCoords.CombatLink link)
        {
            if (cePointsMethod == null || highestCoverMethod == null || getLightingTrackerMethod == null
                || getBoundsMethod == null || shotHeightProperty == null) return true;
            try
            {
                var source = line.Source.ToVector3Shifted();
                source.y = (float)shotHeightProperty.GetValue(ceVerb, null);
                var targetBounds = (Bounds)getBoundsMethod.Invoke(null, new object[] { target });
                var destination = targetBounds.center + new Vector3(link.offset.x, 0f, link.offset.z);
                destination.y = targetBounds.max.y;
                var ray = new Ray(source, destination - source);
                var shooterPawn = shooterPawnProperty?.GetValue(ceVerb, null) as Pawn;
                // GenSightCE uses integer casts internally and can stall forever when either endpoint
                // is negative.  Unified neighbor coordinates legitimately can be negative. Translation
                // preserves the exact ray geometry while keeping CE's iterator in its valid domain.
                var shiftX = Mathf.Max(0, 2 - Mathf.FloorToInt(Mathf.Min(source.x, destination.x)));
                var shiftZ = Mathf.Max(0, 2 - Mathf.FloorToInt(Mathf.Min(source.z, destination.z)));
                var coordinateShift = new Vector3(shiftX, 0f, shiftZ);
                var cellShift = new IntVec3(shiftX, 0, shiftZ);
                var points = (IEnumerable)cePointsMethod.Invoke(null,
                    new object[] { source + coordinateShift, destination + coordinateShift });
                var visited = 0;
                var maxExpected = (Mathf.CeilToInt(Mathf.Abs(destination.x - source.x))
                    + Mathf.CeilToInt(Mathf.Abs(destination.z - source.z)) + 8) * 3;
                foreach (var value in points)
                {
                    visited++;
                    if (visited > maxExpected)
                    {
                        Log.WarningOnce($"[RimExodus] CE vertical-cover iterator exceeded its finite bound "
                            + $"({maxExpected}); falling back to routed LOS for this shot.", 0x43455204);
                        return true;
                    }
                    var unified = (IntVec3)value - cellShift;
                    if (unified == line.Source || unified == line.Dest) continue;
                    if (!TryResolveCell(link.host, unified, out var map, out var local)) continue;
                    var tracker = getLightingTrackerMethod.Invoke(null, new object[] { map });
                    if (tracker == null || (float)highestCoverMethod.Invoke(tracker, new object[] { local }) < source.y) continue;
                    var cover = local.GetFirstPawn(map) ?? local.GetCover(map);
                    if (cover == null || cover == caster || cover == target || cover == shooterPawn || cover is Plant) continue;
                    if (cover is Pawn pawn && pawn.HostileTo(caster)) continue;
                    if (cover is Pawn ally && !ally.Downed && ally.Faction != null && shooterPawn?.Faction != null
                        && (ally.Faction == shooterPawn.Faction || shooterPawn.Faction.RelationKindWith(ally.Faction) == FactionRelationKind.Ally)
                        && !ally.AdjacentTo8WayOrInside(caster))
                    {
                        return false;
                    }
                    var bounds = (Bounds)getBoundsMethod.Invoke(null, new object[] { cover });
                    if (map != link.host) bounds.center += new Vector3(link.offset.x, 0f, link.offset.z);
                    if (cover.def.Fillage == FillCategory.Full || bounds.IntersectRay(ray))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE routed vertical-cover check failed open: {ex}", 0x43455203);
                return true;
            }
        }

        public sealed class ShiftReportState
        {
            public SeamlessVirtualTeleporter teleporter;
            public SeamlessCombatCoords.CombatLink link;
            public Thing caster;
            public Thing target;
            public IntVec3 targetLocal;
            public IntVec3 targetUnified;
            public bool active;
            public bool restored;
        }

        private static void RestoreShiftReport(ShiftReportState state)
        {
            if (state == null || state.restored) return;
            state.teleporter.Dispose();
            state.restored = true;
        }

        public static void ShiftReportPrefix(object __instance, LocalTargetInfo target, ref IntVec3 targetCell,
            out ShiftReportState __state)
        {
            __state = null;
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var targetThing = target.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null) return;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            if (targetThing != null)
            {
                if (targetThing.Map == null || targetThing.Map == caster.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(caster.Map, targetThing.Map, out link)) return;

                // TryCastShot passes the unified targetPos returned by our CE shoot-line hook, while UI
                // report callers pass target.Cell in the target map's local coordinates.
                var anchorUnified = targetThing.Position + link.offset;
                var alreadyUnified = targetCell.DistanceToSquared(anchorUnified) < targetCell.DistanceToSquared(targetThing.Position);
                unified = alreadyUnified ? targetCell : targetCell + link.offset;
            }
            else if (!SeamlessCrossMapCellTarget.TryResolve(verb, target, out link, out unified))
            {
                return;
            }

            // CE's report builder dereferences target.Cell against caster.Map.  Give it a side-effect-free
            // evaluation view, then replace the environmental fields with routed samples in the postfix.
            var evaluationCell = unified;
            evaluationCell.x = Mathf.Clamp(evaluationCell.x, 0, caster.Map.Size.x - 1);
            evaluationCell.z = Mathf.Clamp(evaluationCell.z, 0, caster.Map.Size.z - 1);
            __state = new ShiftReportState
            {
                link = link,
                caster = caster,
                target = targetThing,
                targetLocal = target.Cell,
                targetUnified = unified,
                teleporter = targetThing == null
                    ? default
                    : new SeamlessVirtualTeleporter(targetThing, caster.Map, evaluationCell),
                active = true
            };
            targetCell = evaluationCell;
        }

        public static void ShiftReportPostfix(object __result, ShiftReportState __state)
        {
            if (__state == null || !__state.active) return;
            RestoreShiftReport(__state);
            if (__result == null) return;
            try
            {
                var unifiedTarget = __state.targetUnified;
                SetField(__result, "shotDist", (unifiedTarget - __state.caster.Position).LengthHorizontal);
                SampleEnvironment(__result, __state.caster, __state.target, __state.targetLocal,
                    unifiedTarget, in __state.link);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE cross-map ShiftVecReport correction failed: {ex}", 0x43455201);
            }
        }

        public static Exception ShiftReportFinalizer(Exception __exception, ShiftReportState __state)
        {
            RestoreShiftReport(__state);
            return __exception;
        }

        public static void CanHitPostfix(object __instance, IntVec3 root, LocalTargetInfo targ,
            ref string report, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null) return;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            CellRect rect;
            if (target != null)
            {
                if (target.Map == null || target.Map == caster.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out link)) return;
                unified = target.Position + link.offset;
                rect = target.OccupiedRect().MovedBy(link.offset.ToIntVec2);
            }
            else
            {
                if (!SeamlessCrossMapCellTarget.TryResolve(verb, targ, out link, out unified)) return;
                rect = CellRect.SingleCell(unified);
            }

            if (!PassCeNonGeometricGates(__instance, verb, caster, targ, ref report))
            {
                __result = false;
                return;
            }

            // CE normally exits at CE_OutofBounds before reaching its non-geometric gates because a
            // neighbor-local cell is meaningless on caster.Map. The helper above replays those gates;
            // only then may we replace the expected local-coordinate rejection.
            if (!__result && report != "CE_BlockedMaxRange".Translate()
                && report != "CE_BlockedMinRange".Translate()
                && report != "CE_NoLoS".Translate()
                && report != "CE_BlockedRoof".Translate()
                && report != "CE_OutofBounds".Translate()) return;

            var distSq = rect.ClosestDistSquaredTo(root);
            var min = verb.verbProps.EffectiveMinRange(false);
            if (distSq > verb.EffectiveRange * verb.EffectiveRange)
            {
                __result = false;
                report = "CE_BlockedMaxRange".Translate();
            }
            else if (distSq < min * min)
            {
                __result = false;
                report = "CE_BlockedMinRange".Translate();
            }
            else if ((projectileProperty.GetValue(verb, null) as ThingDef)?.projectile?.flyOverhead == true
                && targ.Cell.InBounds(link.target) && link.target.roofGrid.RoofAt(targ.Cell)?.isThickRoof == true)
            {
                __result = false;
                report = "CE_BlockedRoof".Translate();
            }
            else
            {
                __result = SeamlessCrossMapSight.TryFindShootLine(verb, root, targ, in link, false, out var line)
                    && (target == null || CanPassCeVerticalCover(__instance, caster, target, line, in link));
                report = __result ? "" : "CE_NoLoS".Translate();
            }
        }

        private static bool PassCeNonGeometricGates(object ceVerb, Verb verb, Thing caster,
            LocalTargetInfo target, ref string report)
        {
            if (target.Thing == caster && !verb.verbProps.targetParams.canTargetSelf)
            {
                report = "CE_NoSelfTarget".Translate();
                return false;
            }
            var shooter = shooterPawnProperty?.GetValue(ceVerb, null) as Pawn;
            if (shooter?.story != null && shooter.WorkTagIsDisabled(WorkTags.Violent))
            {
                report = "IsIncapableOfViolenceLower".Translate(shooter.Name.ToStringShort);
                return false;
            }
            if (shooter?.apparel == null) return true;
            var turretOperator = caster.def.building?.IsTurret ?? false;
            foreach (var apparel in shooter.apparel.WornApparel)
            {
                if (apparel.AllowVerbCast(verb)) continue;
                var ceShield = turretOperator && apparel.AllComps.Any(c =>
                    c.GetType().FullName == "CombatExtended.CompShield");
                if (ceShield) continue;
                report = "CE_BlockedShield".Translate() + apparel.LabelShort;
                return false;
            }
            return true;
        }

        public static bool ShootCeCanHitPrefix(object __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null) return true;
            SeamlessCombatCoords.CombatLink link;
            IntVec3 unified;
            if (target != null)
            {
                if (target.Map == null || target.Map == caster.Map
                    || !SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out link)) return true;
                unified = target.Position + link.offset;
            }
            else if (!SeamlessCrossMapCellTarget.TryResolve(verb, targ, out link, out unified))
            {
                return true;
            }
            try
            {
                var shooter = shooterPawnProperty?.GetValue(__instance, null) as Pawn;
                // Sighted shooters already reach the patched three-argument CE base method through CE's
                // original override.  Do not reflectively re-enter that path from the ordinary float-menu
                // hot loop; this prefix exists solely to replace Verb_ShootCE's blind-shooter local-coordinate
                // precheck.
                if (shooter == null || shooter.health.capacities.CapableOf(PawnCapacityDefOf.Sight))
                {
                    return true;
                }
                if (!shooter.health.capacities.CapableOf(PawnCapacityDefOf.Hearing))
                {
                    __result = false;
                    return false;
                }
                var distance = (unified - root).LengthHorizontal;
                if (distance >= 5f && getLightingTrackerMethod != null && getGlowForCellMethod != null)
                {
                    var tracker = getLightingTrackerMethod.Invoke(null, new object[] { link.target });
                    var glowCell = target != null ? target.Position : targ.Cell;
                    var glow = tracker == null ? 0f : (float)getGlowForCellMethod.Invoke(tracker, new object[] { glowCell });
                    if (glow / distance < 0.1f)
                    {
                        __result = false;
                        return false;
                    }
                }
                var args = new object[] { root, targ, null };
                __result = (bool)canHitReportMethod.Invoke(__instance, args);
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE blind-shooter cross-map check failed open: {ex}", 0x43455204);
                return true;
            }
        }

        private static void SampleEnvironment(object report, Thing caster, Thing target, IntVec3 targetLocal,
            IntVec3 targetUnified,
            in SeamlessCombatCoords.CombatLink link)
        {
            float smoke = 0f;
            bool roofed = false;
            Thing highestCover = null;
            float highestCoverHeight = -1f;
            var cells = new List<IntVec3>(GenSight.PointsOnLineOfSight(targetUnified, caster.Position));
            var end = cells.Count / 2;
            for (var i = 0; i < end; i++)
            {
                var unified = cells[i];
                if (!TryResolveCell(link.host, unified, out var map, out var local)) continue;
                if (unified.AdjacentTo8Way(caster.Position)) continue;
                if (local.AnyGas(map, GasType.BlindSmoke)) smoke += GasUtility.BlindingGasAccuracyPenalty;
                roofed |= map.roofGrid.RoofAt(local) != null;
                var cover = local.GetFirstPawn(map) ?? local.GetCover(map);
                // Match CE's GetHighestCoverAndSmokeForTarget exactly here.  Full-fill blockers and
                // plants can still intercept the projectile, but CE deliberately does not use them to
                // raise the selected aim height.  Treating a tree as report.cover can lift the aim point
                // above a pawn and creates a large, cross-map-only accuracy penalty.
                if (cover == null || cover == caster || cover == target
                    || cover.def.Fillage != FillCategory.Partial || cover is Plant
                    || cover is Building_TrapExplosive)
                {
                    continue;
                }
                var coverHeight = getBoundsMethod != null
                    ? ((Bounds)getBoundsMethod.Invoke(null, new object[] { cover })).max.y
                    : cover.def.fillPercent;
                if (coverHeight > highestCoverHeight)
                {
                    highestCover = cover;
                    highestCoverHeight = coverHeight;
                }
            }
            SetField(report, "cover", highestCover);
            SetField(report, "smokeDensity", smoke);
            SetField(report, "roofed", roofed);

            if (getLightingTrackerMethod == null || getGlowForCellMethod == null || getLightingShiftMethod == null) return;
            var sourceTracker = getLightingTrackerMethod.Invoke(null, new object[] { link.host });
            var targetTracker = getLightingTrackerMethod.Invoke(null, new object[] { link.target });
            if (sourceTracker == null || targetTracker == null) return;
            var sourceGlow = (float)getGlowForCellMethod.Invoke(sourceTracker, new object[] { caster.Position });
            var targetGlow = (float)getGlowForCellMethod.Invoke(targetTracker,
                new object[] { target != null ? target.Position : targetLocal });
            var combined = sourceGlow > 0.5f ? Mathf.Max(targetGlow, sourceGlow / 2f) : targetGlow;
            SetField(report, "lightingShift", (float)getLightingShiftMethod.Invoke(null, new object[] { caster, combined }));
        }

        private static bool TryResolveCell(Map host, IntVec3 unified, out Map map, out IntVec3 local)
        {
            if (SeamlessTileRegistry.TryGetOwnerNeighbor(host, unified, out map, out local)) return true;
            map = host;
            local = unified;
            return unified.InBounds(host);
        }
    }
}
