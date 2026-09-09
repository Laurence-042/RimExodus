using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// Reflection-only Combat Extended direct-fire adapter.  No CE type occurs in a signature in this
    /// assembly, so removing CE never creates a loader dependency.  Same-map calls always run CE unchanged.
    /// </summary>
    public static class SeamlessCombatExtendedCompat
    {
        private const string PackageId = "CETeam.CombatExtended";

        private static Type verbType;
        private static Type projectileType;
        private static Type projectilePropsType;
        private static Type guidedWorkerType;
        private static PropertyInfo projectileProperty;
        private static PropertyInfo trajectoryWorkerProperty;
        private static PropertyInfo guidedProjectileProperty;
        private static PropertyInfo exactPositionProperty;
        private static FieldInfo isInstantField;
        private static FieldInfo trajectoryWorkerField;
        private static FieldInfo originField;
        private static FieldInfo originIv3Field;
        private static FieldInfo destinationField;
        private static FieldInfo lastPosField;
        private static FieldInfo predictedField;
        private static FieldInfo dangerTrackerField;
        private static FieldInfo lastShotLineField;
        private static FieldInfo minCollisionDistanceField;
        private static FieldInfo intendedTargetField;
        private static FieldInfo globalTargetField;
        private static FieldInfo homingAccelerationField;
        private static MethodInfo getLightingTrackerMethod;
        private static MethodInfo getGlowForCellMethod;
        private static MethodInfo getLightingShiftMethod;
        private static MethodInfo cePointsMethod;
        private static MethodInfo highestCoverMethod;
        private static MethodInfo getBoundsMethod;
        private static PropertyInfo shotHeightProperty;
        private static PropertyInfo shooterPawnProperty;
        private static MethodInfo canHitReportMethod;
        private static bool initialized;
        private static long debugSequence;

        internal static bool DebugEnabled => RimExodusMod.Settings?.logCombatExtended ?? false;

        internal static bool IsCeVerb(Verb verb) => initialized && verb != null && verbType.IsInstanceOfType(verb);

        internal static void DebugLog(string text)
        {
            if (!DebugEnabled) return;
            var sequence = Interlocked.Increment(ref debugSequence);
            Log.Message($"[RimExodus:CombatExtended] #{sequence} T{Thread.CurrentThread.ManagedThreadId} {text}");
        }

        private static readonly HashSet<string> ExcludedVerbNames = new HashSet<string>
        {
            "CombatExtended.Verb_ThrowGrenade",
            "CombatExtended.Verb_ShootMortarCE",
            "CombatExtended.VerbCIWS",
            "CombatExtended.Verb_MarkForArtillery"
        };

        public static void Register(Harmony harmony)
        {
            if (!ModsConfig.IsActive(PackageId)) return;
            try
            {
                verbType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
                projectileType = AccessTools.TypeByName("CombatExtended.ProjectileCE");
                projectilePropsType = AccessTools.TypeByName("CombatExtended.ProjectilePropertiesCE");
                guidedWorkerType = AccessTools.TypeByName("CombatExtended.BaseTrajectoryWorker");
                if (verbType == null || projectileType == null || projectilePropsType == null)
                    throw new MissingMemberException("CE core direct-fire types were not found");

                projectileProperty = AccessTools.Property(verbType, "Projectile");
                trajectoryWorkerProperty = AccessTools.Property(projectilePropsType, "TrajectoryWorker");
                isInstantField = AccessTools.Field(projectilePropsType, "isInstant");
                trajectoryWorkerField = AccessTools.Field(projectilePropsType, "trajectoryWorker");
                guidedProjectileProperty = AccessTools.Property(
                    guidedWorkerType, "GuidedProjectile");

                exactPositionProperty = AccessTools.Property(projectileType, "ExactPosition");
                originField = AccessTools.Field(projectileType, "origin");
                originIv3Field = AccessTools.Field(projectileType, "OriginIV3");
                destinationField = AccessTools.Field(projectileType, "Destination");
                lastPosField = AccessTools.Field(projectileType, "LastPos");
                predictedField = AccessTools.Field(projectileType, "cachedPredictedPositions");
                dangerTrackerField = AccessTools.Field(projectileType, "_dangerTracker");
                lastShotLineField = AccessTools.Field(projectileType, "lastShotLine");
                minCollisionDistanceField = AccessTools.Field(projectileType, "minCollisionDistance");
                intendedTargetField = AccessTools.Field(projectileType, "intendedTarget");
                globalTargetField = AccessTools.Field(projectileType, "globalTargetInfo");
                homingAccelerationField = AccessTools.Field(projectileType, "homingAcceleration");

                var ceUtility = AccessTools.TypeByName("CombatExtended.CE_Utility");
                var lightingTracker = AccessTools.TypeByName("CombatExtended.LightingTracker");
                getLightingTrackerMethod = AccessTools.Method(ceUtility, "GetLightingTracker", new[] { typeof(Map) });
                getGlowForCellMethod = AccessTools.Method(lightingTracker, "GetGlowForCell", new[] { typeof(IntVec3) });
                getLightingShiftMethod = AccessTools.Method(ceUtility, "GetLightingShift", new[] { typeof(Thing), typeof(float) });
                getBoundsMethod = AccessTools.Method(ceUtility, "GetBoundsFor", new[] { typeof(Thing) });
                cePointsMethod = AccessTools.Method(AccessTools.TypeByName("CombatExtended.GenSightCE"),
                    "PointsOnLineOfSight", new[] { typeof(Vector3), typeof(Vector3) });
                highestCoverMethod = AccessTools.Method(lightingTracker, "HighestCoverAt", new[] { typeof(IntVec3) });
                shotHeightProperty = AccessTools.Property(verbType, "ShotHeight");
                shooterPawnProperty = AccessTools.Property(verbType, "ShooterPawn");

                Require(projectileProperty, "Verb_LaunchProjectileCE.Projectile");
                Require(exactPositionProperty, "ProjectileCE.ExactPosition");
                Require(originField, "ProjectileCE.origin");
                Require(destinationField, "ProjectileCE.Destination");

                var shootLine = AccessTools.Method(verbType, "TryFindCEShootLineFromTo",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(Vector3).MakeByRefType() });
                var reportMethod = AccessTools.Method(verbType, "ShiftVecReportFor",
                    new[] { typeof(LocalTargetInfo), typeof(IntVec3) });
                var canHit = AccessTools.Method(verbType, "CanHitTargetFrom",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(string).MakeByRefType() });
                canHitReportMethod = canHit;
                var moveForward = AccessTools.Method(projectileType, "MoveForward");
                var launch = AccessTools.Method(projectileType, "Launch",
                    new[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(float) });
                var launchCore = AccessTools.Method(projectileType, "Launch",
                    new[] { typeof(Thing), typeof(Vector2), typeof(Thing) });
                var impact = AccessTools.Method(projectileType, "Impact", new[] { typeof(Thing) });
                var fireArcType = AccessTools.TypeByName("CombatExtended.CompFireArc");
                var turretType = AccessTools.TypeByName("CombatExtended.Building_TurretGunCE");
                var nonSnapFinder = AccessTools.TypeByName("CombatExtended.NonSnapAttackTargetFinder");

                initialized = true;
                TryPatch(harmony, shootLine, prefix: nameof(TryFindShootLinePrefix));
                TryPatch(harmony, reportMethod, prefix: nameof(ShiftReportPrefix), postfix: nameof(ShiftReportPostfix));
                TryPatch(harmony, canHit, postfix: nameof(CanHitPostfix));
                var shootVerbType = AccessTools.TypeByName("CombatExtended.Verb_ShootCE");
                TryPatch(harmony, AccessTools.Method(shootVerbType, "CanHitTargetFrom",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo) }), prefix: nameof(ShootCeCanHitPrefix));
                TryPatch(harmony, moveForward, postfix: nameof(MoveForwardPostfix));
                TryPatch(harmony, launch, postfix: nameof(LaunchPostfix));
                TryPatch(harmony, launchCore, postfix: nameof(LaunchCorePostfix));
                TryPatch(harmony, impact, prefix: nameof(ImpactPrefix));
                // Building_TurretGunCE's static constructor creates Unity materials.  Mod constructors run
                // inside the asynchronous play-data long event; asking Harmony/Mono to compile a turret
                // detour here initializes that type on the worker thread and Unity rejects the resource load.
                // Resolve metadata now, but compile all turret-related detours at the long-event main-thread
                // completion point.
                LongEventHandler.ExecuteWhenFinished(() => RegisterTurretHooks(
                    harmony, fireArcType, turretType, nonSnapFinder));

                var mod = LoadedModManager.RunningModsListForReading.Find(m => m.PackageIdPlayerFacing == PackageId);
                Log.Message($"[RimExodus] Combat Extended compat: bound direct-fire hooks (CE {mod?.ModMetaData?.ModVersion ?? "unknown"}).");
            }
            catch (Exception ex)
            {
                initialized = false;
                Log.Warning($"[RimExodus] Combat Extended compat disabled; CE signature/type mismatch: {ex}");
            }
        }

        private static void RegisterTurretHooks(Harmony harmony, Type fireArcType, Type turretType, Type nonSnapFinder)
        {
            TryPatch(harmony, AccessTools.Method(fireArcType, "WithinFireArc", new[] { typeof(LocalTargetInfo) }),
                prefix: nameof(FireArcPrefix), postfix: nameof(TargetTeleportPostfix));
            TryPatch(harmony, AccessTools.PropertyGetter(turretType, "DeltaAngle"),
                prefix: nameof(TurretTargetPrefix), postfix: nameof(TargetTeleportPostfix));
            TryPatch(harmony, AccessTools.Method(turretType, "OrderAttack", new[] { typeof(LocalTargetInfo) }),
                prefix: nameof(TurretOrderPrefix), postfix: nameof(TargetTeleportPostfix));
            TryPatch(harmony, AccessTools.Method(nonSnapFinder, "BestAttackTarget"),
                postfix: nameof(NonSnapBestAttackTargetPostfix));
            Log.Message("[RimExodus] Combat Extended compat: main-thread turret hook registration finished.");
        }

        public static bool IsSupportedVerb(Verb verb)
        {
            if (!initialized || verb == null || !verbType.IsInstanceOfType(verb) || ExcludedVerbNames.Contains(verb.GetType().FullName))
            {
                return false;
            }
            try
            {
                var projectileDef = projectileProperty.GetValue(verb, null) as ThingDef;
                var props = projectileDef?.projectile;
                if (props == null || props.flyOverhead || !projectilePropsType.IsInstanceOfType(props))
                {
                    return false;
                }
                if ((bool)isInstantField.GetValue(props))
                {
                    return false;
                }
                var worker = trajectoryWorkerProperty?.GetValue(props, null);
                return worker == null || guidedProjectileProperty == null || !(bool)guidedProjectileProperty.GetValue(worker, null);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsProjectile(Thing thing) => initialized && thing != null && projectileType.IsInstanceOfType(thing);

        private static void Require(MemberInfo member, string name)
        {
            if (member == null) throw new MissingMemberException(name);
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null)
        {
            if (target == null) throw new MissingMethodException("CE target method not found");
            var pre = prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SeamlessCombatExtendedCompat), prefix)) { priority = Priority.First };
            var post = postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SeamlessCombatExtendedCompat), postfix)) { priority = Priority.Last };
            harmony.Patch(target, pre, post);
            var info = Harmony.GetPatchInfo(target);
            var bound = info != null && ((pre != null && info.Prefixes.Any(p => p.owner == harmony.Id))
                || (post != null && info.Postfixes.Any(p => p.owner == harmony.Id)));
            if (!bound) throw new InvalidOperationException($"Harmony did not register CE hook {target.DeclaringType?.FullName}::{target.Name}");
            Log.Message($"[RimExodus] Combat Extended compat: bound {target.DeclaringType?.FullName}::{target.Name}.");
        }

        private static bool TryPatch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null)
        {
            try
            {
                Patch(harmony, target, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Combat Extended compat: one hook was disabled after signature drift: {ex.Message}");
                return false;
            }
        }

        public static bool TryFindShootLinePrefix(object __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref Vector3 targetPos, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null || target?.Map == null || target.Map == caster.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out var link)) return true;

            __result = SeamlessCrossMapSight.TryFindShootLine(verb, root, targ, in link, false, out resultingLine);
            if (__result)
            {
                if (!CanPassCeVerticalCover(__instance, caster, target, resultingLine, in link)) __result = false;
            }
            targetPos = target.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z);
            if (__result) SeamlessCombatCoords.MarkCrossMapCast(verb, target.Map);
            return false;
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
            public IntVec3 targetUnified;
            public bool active;
        }

        public static void ShiftReportPrefix(object __instance, LocalTargetInfo target, ref IntVec3 targetCell,
            out ShiftReportState __state)
        {
            __state = null;
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var targetThing = target.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null || targetThing?.Map == null || targetThing.Map == caster.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, targetThing.Map, out var link)) return;

            // TryCastShot passes the unified targetPos returned by our CE shoot-line hook, while UI report
            // callers pass target.Cell in the target map's local coordinates.  Accept both without ever
            // adding the seam offset twice.
            var anchorUnified = targetThing.Position + link.offset;
            var alreadyUnified = targetCell.DistanceToSquared(anchorUnified) < targetCell.DistanceToSquared(targetThing.Position);
            var unified = alreadyUnified ? targetCell : targetCell + link.offset;
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
                targetUnified = unified,
                teleporter = new SeamlessVirtualTeleporter(targetThing, caster.Map, evaluationCell),
                active = true
            };
            targetCell = evaluationCell;
        }

        public static void ShiftReportPostfix(object __result, ShiftReportState __state)
        {
            if (__state == null || !__state.active) return;
            __state.teleporter.Dispose();
            if (__result == null) return;
            try
            {
                var unifiedTarget = __state.targetUnified;
                SetField(__result, "shotDist", (unifiedTarget - __state.caster.Position).LengthHorizontal);
                SampleEnvironment(__result, __state.caster, __state.target, unifiedTarget, in __state.link);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[RimExodus] CE cross-map ShiftVecReport correction failed: {ex}", 0x43455201);
            }
        }

        public static void CanHitPostfix(object __instance, IntVec3 root, LocalTargetInfo targ,
            ref string report, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null || target?.Map == null || target.Map == caster.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out var link)) return;

            var rect = target.OccupiedRect().MovedBy(link.offset.ToIntVec2);
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
            else if (!__result)
            {
                report = "CE_NoLoS".Translate();
            }
        }

        public static bool ShootCeCanHitPrefix(object __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            var verb = __instance as Verb;
            var caster = SeamlessCombatCoords.VerbCaster(verb);
            var target = targ.Thing;
            if (!IsSupportedVerb(verb) || caster?.Map == null || target?.Map == null || target.Map == caster.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, target.Map, out var link)) return true;
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
                var distance = (target.Position + link.offset - root).LengthHorizontal;
                if (distance >= 5f && getLightingTrackerMethod != null && getGlowForCellMethod != null)
                {
                    var tracker = getLightingTrackerMethod.Invoke(null, new object[] { target.Map });
                    var glow = tracker == null ? 0f : (float)getGlowForCellMethod.Invoke(tracker, new object[] { target.Position });
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

        private static void SampleEnvironment(object report, Thing caster, Thing target, IntVec3 targetUnified,
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
            var targetGlow = (float)getGlowForCellMethod.Invoke(targetTracker, new object[] { target.Position });
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

        public sealed class TargetTeleportState
        {
            public SeamlessVirtualTeleporter teleporter;
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
            var current = (LocalTargetInfo)AccessTools.Property(__instance.GetType(), "CurrentTarget").GetValue(__instance, null);
            __state = TryTeleportTarget(turret, current);
        }

        public static void TurretOrderPrefix(object __instance, LocalTargetInfo targ, out TargetTeleportState __state)
        {
            __state = TryTeleportTarget(__instance as Thing, targ);
        }

        public static void TargetTeleportPostfix(TargetTeleportState __state)
        {
            __state?.teleporter.Dispose();
        }

        private static TargetTeleportState TryTeleportTarget(Thing caster, LocalTargetInfo target)
        {
            var thing = target.Thing;
            if (caster?.Map == null || thing?.Map == null || caster.Map == thing.Map) return null;
            if (!SeamlessCombatCoords.TryGetCombatLink(caster.Map, thing.Map, out var link)) return null;
            var unified = thing.Position + link.offset;
            if (!unified.InBounds(caster.Map)) return null;
            return new TargetTeleportState { teleporter = new SeamlessVirtualTeleporter(thing, caster.Map, unified) };
        }

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

        private static void SetField(object instance, string name, object value)
        {
            AccessTools.Field(instance.GetType(), name)?.SetValue(instance, value);
        }

        public static void LaunchCorePostfix(object __instance, Thing launcher, Vector2 origin)
        {
            if (!DebugEnabled) return;
            var projectile = __instance as Thing;
            if (!IsProjectile(projectile)) return;

            var target = intendedTargetField?.GetValue(__instance) is LocalTargetInfo info ? info : LocalTargetInfo.Invalid;
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
            if (!target.HasThing || target.Thing.Map == null || target.Thing.Map == thing.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(thing.Map, target.Thing.Map, out var link)) return;
            var distance = (target.Cell + link.offset - thing.Position).LengthHorizontal;
            minCollisionDistanceField.SetValue(__instance, distance <= 7.5f ? Mathf.Min(1.5f, distance * 0.75f) : distance * 0.2f);
        }

        public static void ImpactPrefix(object __instance, Thing hitThing)
        {
            if (!DebugEnabled) return;
            var projectile = __instance as Thing;
            if (!IsProjectile(projectile)) return;
            var intended = intendedTargetField?.GetValue(__instance) is LocalTargetInfo info ? info.Thing : null;
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
                if (props == null || props.flyOverhead || !projectilePropsType.IsInstanceOfType(props)) return false;
                if ((bool)isInstantField.GetValue(props)) return false;
                if (homingAccelerationField != null && (float)homingAccelerationField.GetValue(projectile) > 0f) return false;
                if (globalTargetField != null && ((GlobalTargetInfo)globalTargetField.GetValue(projectile)).IsValid) return false;
                var worker = trajectoryWorkerProperty?.GetValue(props, null);
                return worker == null || guidedProjectileProperty == null || !(bool)guidedProjectileProperty.GetValue(worker, null);
            }
            catch
            {
                return false;
            }
        }
    }
}
