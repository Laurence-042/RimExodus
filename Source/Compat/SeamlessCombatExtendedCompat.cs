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
    /// Reflection-only Combat Extended projectile adapter.  No CE type occurs in a signature in this
    /// assembly, so removing CE never creates a loader dependency.  Same-map calls always run CE unchanged.
    /// </summary>
    public static partial class SeamlessCombatExtendedCompat
    {
        private const string PackageId = "CETeam.CombatExtended";

        private static Type verbType;
        private static Type projectileType;
        private static Type projectilePropsType;
        private static Type guidedWorkerType;
        private static PropertyInfo projectileProperty;
        private static PropertyInfo trajectoryWorkerProperty;
        private static PropertyInfo exactPositionProperty;
        private static FieldInfo exactPositionField;
        private static FieldInfo isInstantField;
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
        private static FieldInfo landedField;
        private static FieldInfo launcherField;
        private static FieldInfo equipmentField;
        private static FieldInfo equipmentDefField;
        private static FieldInfo shotAngleField;
        private static FieldInfo shotHeightField;
        private static FieldInfo shotRotationField;
        private static MethodInfo getLightingTrackerMethod;
        private static MethodInfo getGlowForCellMethod;
        private static MethodInfo getLightingShiftMethod;
        private static MethodInfo cePointsMethod;
        private static MethodInfo highestCoverMethod;
        private static MethodInfo getBoundsMethod;
        private static PropertyInfo shotHeightProperty;
        private static PropertyInfo shotSpeedProperty;
        private static PropertyInfo gravityPerHeightProperty;
        private static PropertyInfo shooterPawnProperty;
        private static PropertyInfo verbPropsCeProperty;
        private static FieldInfo indirectFirePenaltyField;
        private static MethodInfo canHitReportMethod;
        private static MethodInfo trajectoryShotAngleMethod;
        private static MethodInfo trajectoryShotRotationMethod;
        private static MethodInfo rayCastSuppressionMethod;
        private static MethodInfo laserSpawnBeamMethod;
        private static MethodInfo laserImpactMethod;
        private static FieldInfo laserDamageModifierField;
        private static FieldInfo damageFalloffField;
        private static Type laserGunDefType;
        private static FieldInfo laserBarrelLengthField;
        private static Type laserBeamType;
        private static Type ciwsBaseType;
        private static Type ciwsProjectileVerbType;
        private static Type ciwsSkyfallerVerbType;
        private static Type ciwsCompSkyfallerVerbType;
        private static Type ciwsCompSkyfallerTargetType;
        private static Type ciwsProjectileType;
        private static Type ciwsTrackerType;
        private static FieldInfo ciwsTurretsField;
        private static FieldInfo turretCurrentTargetField;
        private static PropertyInfo ciwsActiveProperty;
        private static PropertyInfo ciwsPropsProperty;
        private static PropertyInfo ciwsTurretProperty;
        private static PropertyInfo turretCurrentTargetProperty;
        private static FieldInfo grenadeDirectField;
        private static PropertyInfo ciwsIgnoredProperty;
        private static PropertyInfo ciwsTurretIgnoredProperty;
        private static MethodInfo ciwsInterceptableMethod;
        private static readonly Dictionary<Type, MethodInfo> CiwsShootLineMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> CiwsFriendlyMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> ReflectedFields =
            new Dictionary<Type, Dictionary<string, FieldInfo>>();
        private static bool initialized;
        private static long debugSequence;

        private sealed class ProjectileTargetState
        {
            public Map targetMap;
            public IntVec3 targetLocal;
        }

        private static readonly ConditionalWeakTable<Thing, ProjectileTargetState> ProjectileTargets =
            new ConditionalWeakTable<Thing, ProjectileTargetState>();

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
                    throw new MissingMemberException("CE core projectile types were not found");

                projectileProperty = AccessTools.Property(verbType, "Projectile");
                trajectoryWorkerProperty = AccessTools.Property(projectilePropsType, "TrajectoryWorker");
                isInstantField = AccessTools.Field(projectilePropsType, "isInstant");

                exactPositionProperty = AccessTools.Property(projectileType, "ExactPosition");
                exactPositionField = AccessTools.Field(projectileType, "exactPosition");
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
                landedField = AccessTools.Field(projectileType, "landed");
                launcherField = AccessTools.Field(projectileType, "launcher");
                equipmentField = AccessTools.Field(projectileType, "equipment");
                equipmentDefField = AccessTools.Field(projectileType, "equipmentDef");
                shotAngleField = AccessTools.Field(projectileType, "shotAngle");
                shotHeightField = AccessTools.Field(projectileType, "shotHeight");
                shotRotationField = AccessTools.Field(projectileType, "shotRotation");

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
                shotSpeedProperty = AccessTools.Property(verbType, "ShotSpeed");
                gravityPerHeightProperty = AccessTools.Property(projectilePropsType, "GravityPerHeight");
                shooterPawnProperty = AccessTools.Property(verbType, "ShooterPawn");
                verbPropsCeProperty = AccessTools.Property(verbType, "VerbPropsCE");
                indirectFirePenaltyField = AccessTools.Field(
                    AccessTools.TypeByName("CombatExtended.VerbPropertiesCE"), "indirectFirePenalty");
                trajectoryShotAngleMethod = AccessTools.Method(guidedWorkerType, "ShotAngle",
                    new[] { projectilePropsType, typeof(Vector3), typeof(Vector3), typeof(float?) });
                trajectoryShotRotationMethod = AccessTools.Method(guidedWorkerType, "ShotRotation",
                    new[] { projectilePropsType, typeof(Vector3), typeof(Vector3) });
                rayCastSuppressionMethod = AccessTools.Method(projectileType, "RayCastSuppression",
                    new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) });
                laserBeamType = AccessTools.TypeByName("CombatExtended.Lasers.LaserBeamCE");
                laserSpawnBeamMethod = AccessTools.Method(laserBeamType, "SpawnBeam",
                    new[] { typeof(Vector3), typeof(Vector3) });
                laserImpactMethod = AccessTools.Method(laserBeamType, "Impact",
                    new[] { typeof(Thing), typeof(Vector3) });
                laserDamageModifierField = AccessTools.Field(laserBeamType, "DamageModifier");
                damageFalloffField = AccessTools.Field(projectilePropsType, "damageFalloff");
                laserGunDefType = AccessTools.TypeByName("CombatExtended.Lasers.LaserGunDef");
                laserBarrelLengthField = AccessTools.Field(laserGunDefType, "barrelLength");

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
                var moveForward = AccessTools.Method(projectileType, "MoveForward", Type.EmptyTypes);
                var launch = AccessTools.Method(projectileType, "Launch",
                    new[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(float) });
                var launchCore = AccessTools.Method(projectileType, "Launch",
                    new[] { typeof(Thing), typeof(Vector2), typeof(Thing) });
                var impact = AccessTools.Method(projectileType, "Impact", new[] { typeof(Thing) });
                var throwMethod = AccessTools.Method(projectileType, "Throw",
                    new[] { typeof(Thing), typeof(Vector3), typeof(Vector3), typeof(Thing) });
                var rayCastMethod = AccessTools.Method(projectileType, "RayCast",
                    new[] { typeof(Thing), typeof(VerbProperties), typeof(Vector2), typeof(float), typeof(float),
                        typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(bool) });
                ciwsProjectileType = AccessTools.TypeByName("CombatExtended.ProjectileCE_CIWS");
                var ciwsCollision = AccessTools.Method(ciwsProjectileType, "CanCollideWith",
                    new[] { typeof(Thing), typeof(float).MakeByRefType() });
                var ballisticWorkerType = AccessTools.TypeByName("CombatExtended.BallisticsTrajectoryWorker");
                var fireArcType = AccessTools.TypeByName("CombatExtended.CompFireArc");
                var turretType = AccessTools.TypeByName("CombatExtended.Building_TurretGunCE");
                var nonSnapFinder = AccessTools.TypeByName("CombatExtended.NonSnapAttackTargetFinder");
                var grenadeType = AccessTools.TypeByName("CombatExtended.Verb_ThrowGrenade");
                var mortarType = AccessTools.TypeByName("CombatExtended.Verb_ShootMortarCE");
                var tacticalManagerType = AccessTools.TypeByName("CombatExtended.CompTacticalManager");
                ciwsBaseType = AccessTools.TypeByName("CombatExtended.VerbCIWS");
                ciwsProjectileVerbType = AccessTools.TypeByName("CombatExtended.VerbCIWSProjectile");
                ciwsSkyfallerVerbType = AccessTools.TypeByName("CombatExtended.VerbCIWSSkyfaller");
                ciwsCompSkyfallerVerbType = AccessTools.TypeByName("CombatExtended.VerbCIWS_CompSkyfaller");
                ciwsCompSkyfallerTargetType = AccessTools.TypeByName("CombatExtended.CompCIWSTarget_Skyfaller");
                ciwsTrackerType = AccessTools.TypeByName("CombatExtended.TurretTracker");
                ciwsTurretsField = AccessTools.Field(ciwsTrackerType, "CIWS");
                turretCurrentTargetField = AccessTools.Field(turretType, "currentTargetInt");
                turretCurrentTargetProperty = AccessTools.Property(turretType, "CurrentTarget");
                grenadeDirectField = AccessTools.Field(grenadeType, "_direct");
                ciwsActiveProperty = AccessTools.Property(ciwsBaseType, "Active");
                ciwsPropsProperty = AccessTools.Property(ciwsBaseType, "Props");
                ciwsTurretProperty = AccessTools.Property(ciwsBaseType, "Turret");
                var ciwsPropsType = AccessTools.TypeByName("CombatExtended.VerbProperties_CIWS");
                ciwsIgnoredProperty = AccessTools.Property(ciwsPropsType, "Ignored");
                ciwsInterceptableMethod = AccessTools.Method(ciwsPropsType, "Interceptable", new[] { typeof(ThingDef) });
                ciwsTurretIgnoredProperty = AccessTools.Property(
                    AccessTools.TypeByName("CombatExtended.Building_CIWS_CE"), "IgnoredDefsSettings");

                initialized = true;
                TryPatch(harmony, shootLine, prefix: nameof(TryFindShootLinePrefix));
                TryPatch(harmony, reportMethod, prefix: nameof(ShiftReportPrefix), postfix: nameof(ShiftReportPostfix),
                    finalizer: nameof(ShiftReportFinalizer));
                TryPatch(harmony, canHit, postfix: nameof(CanHitPostfix));
                var shootVerbType = AccessTools.TypeByName("CombatExtended.Verb_ShootCE");
                TryPatch(harmony, AccessTools.Method(shootVerbType, "CanHitTargetFrom",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo) }), prefix: nameof(ShootCeCanHitPrefix));
                TryPatch(harmony, moveForward, postfix: nameof(MoveForwardPostfix));
                TryPatch(harmony, launch, postfix: nameof(LaunchPostfix));
                TryPatch(harmony, launchCore, postfix: nameof(LaunchCorePostfix));
                TryPatch(harmony, impact, prefix: nameof(ImpactPrefix));
                TryPatch(harmony, throwMethod, postfix: nameof(ThrowPostfix));
                TryPatch(harmony, rayCastMethod, prefix: nameof(RayCastPrefix));
                TryPatch(harmony, ciwsCollision, prefix: nameof(CiwsProjectileCollisionPrefix),
                    postfix: nameof(CiwsShootLinePostfix), finalizer: nameof(CiwsEvaluationFinalizer));
                TryPatch(harmony, AccessTools.Method(ballisticWorkerType, "MoveForward",
                    new[] { projectileType }), prefix: nameof(GuidedTargetPrefix), postfix: nameof(GuidedTargetPostfix),
                    finalizer: nameof(GuidedTargetFinalizer));
                TryPatch(harmony, AccessTools.Method(verbType, "TryCastShot", Type.EmptyTypes),
                    prefix: nameof(CeTryCastPrefix), finalizer: nameof(CeTryCastFinalizer));
                TryPatch(harmony, AccessTools.Method(grenadeType, "TryCastShot", Type.EmptyTypes),
                    prefix: nameof(CeTryCastPrefix), finalizer: nameof(CeTryCastFinalizer));
                TryPatch(harmony, AccessTools.Method(grenadeType, "FindAngle",
                    new[] { typeof(LocalTargetInfo), typeof(float).MakeByRefType(), typeof(bool).MakeByRefType(),
                        typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(int).MakeByRefType() }),
                    prefix: nameof(GrenadeFindAnglePrefix));
                TryPatch(harmony, AccessTools.Method(grenadeType, "TryFindCEShootLineFromTo",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(),
                        typeof(Vector3).MakeByRefType() }),
                    postfix: nameof(GrenadeShootLinePostfix));
                TryPatch(harmony, AccessTools.Method(grenadeType, "DrawHighlight",
                    new[] { typeof(LocalTargetInfo) }), prefix: nameof(CellHighlightPrefix));
                TryPatch(harmony, AccessTools.Method(mortarType, "ShiftVecReportFor",
                    new[] { typeof(LocalTargetInfo), typeof(IntVec3) }), postfix: nameof(MortarReportPostfix));
                TryPatch(harmony, AccessTools.Method(tacticalManagerType, nameof(ThingComp.CompTickRare), Type.EmptyTypes),
                    prefix: nameof(TacticalManagerTickRarePrefix));
                var abilityLaunch = AccessTools.Method(ceUtility, "LaunchProjectileCE",
                    new[] { typeof(ThingDef), typeof(Vector2), typeof(LocalTargetInfo), typeof(Thing),
                        typeof(float), typeof(float), typeof(float), typeof(float) });
                TryPatch(harmony, abilityLaunch, prefix: nameof(AbilityLaunchPrefix));
                var vehicleAbilityLaunch = AccessTools.Method(ceUtility, "LaunchProjectileCE",
                    new[] { typeof(ThingDef), typeof(ThingDef), typeof(Def), typeof(Vector2),
                        typeof(LocalTargetInfo), typeof(Pawn), typeof(float), typeof(float), typeof(float), typeof(float) });
                TryPatch(harmony, vehicleAbilityLaunch, prefix: nameof(VehicleAbilityLaunchPrefix));
                // Building_TurretGunCE's static constructor creates Unity materials.  Mod constructors run
                // inside the asynchronous play-data long event; asking Harmony/Mono to compile a turret
                // detour here initializes that type on the worker thread and Unity rejects the resource load.
                // Resolve metadata now, but compile all turret-related detours at the long-event main-thread
                // completion point.
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    RegisterTurretHooks(harmony, fireArcType, turretType, nonSnapFinder);
                    RegisterCiwsHooks(harmony);
                });

                var mod = LoadedModManager.RunningModsListForReading.Find(m => m.PackageIdPlayerFacing == PackageId);
                Log.Message($"[RimExodus] Combat Extended compat: bound projectile hooks (CE {mod?.ModMetaData?.ModVersion ?? "unknown"}).");
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
                prefix: nameof(FireArcPrefix), postfix: nameof(TargetTeleportPostfix),
                finalizer: nameof(TargetTeleportFinalizer));
            TryPatch(harmony, AccessTools.PropertyGetter(turretType, "DeltaAngle"),
                prefix: nameof(TurretTargetPrefix), postfix: nameof(TargetTeleportPostfix),
                finalizer: nameof(TargetTeleportFinalizer));
            TryPatch(harmony, AccessTools.Method(turretType, "OrderAttack", new[] { typeof(LocalTargetInfo) }),
                prefix: nameof(TurretOrderPrefix), postfix: nameof(TargetTeleportPostfix),
                finalizer: nameof(TargetTeleportFinalizer));
            TryPatch(harmony, AccessTools.Method(nonSnapFinder, "BestAttackTarget"),
                postfix: nameof(NonSnapBestAttackTargetPostfix));
            Log.Message("[RimExodus] Combat Extended compat: main-thread turret hook registration finished.");
        }

        private static void RegisterCiwsHooks(Harmony harmony)
        {
            var patchedLines = new HashSet<MethodBase>();
            var patchedFinders = new HashSet<MethodBase>();
            foreach (var type in new[] { ciwsProjectileVerbType, ciwsSkyfallerVerbType, ciwsCompSkyfallerVerbType })
            {
                if (type == null) continue;
                CiwsFriendlyMethods[type] = type.GetMethods(AccessTools.all)
                    .FirstOrDefault(m => m.Name == "IsFriendlyTo" && m.GetParameters().Length == 1);
                var line = AccessTools.Method(type, "TryFindCEShootLineFromTo",
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(Vector3).MakeByRefType() });
                if (line != null)
                {
                    CiwsShootLineMethods[type] = line;
                    if (patchedLines.Add(line))
                        TryPatch(harmony, line, prefix: nameof(CiwsShootLinePrefix), postfix: nameof(CiwsShootLinePostfix),
                            finalizer: nameof(CiwsEvaluationFinalizer));
                }
                var finder = AccessTools.Method(type, "TryFindNewTarget",
                    new[] { typeof(LocalTargetInfo).MakeByRefType() });
                if (finder != null && patchedFinders.Add(finder))
                    TryPatch(harmony, finder, postfix: nameof(CiwsFindTargetPostfix));
            }
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
                if (props == null || !projectilePropsType.IsInstanceOfType(props))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsProjectile(Thing thing) => initialized && thing != null && projectileType.IsInstanceOfType(thing);

        /// <summary>
        /// Corpse.TickRare deliberately ticks its inner Pawn. CE's tactical manager checks
        /// CompSuppressable.IsHunkering before it checks SelPawn.Spawned, so a pawn that died while
        /// suppressed/hunkering can enter live-pawn job repair with corpse state and throw. A corpse
        /// can never receive a tactical job; stop at the common comp boundary instead of special-casing
        /// individual weapons, projectiles, or corpse callers.
        /// </summary>
        public static bool TacticalManagerTickRarePrefix(ThingComp __instance)
        {
            return !(__instance?.parent is Pawn pawn) || (pawn.Spawned && !pawn.Dead);
        }

        private static void Require(MemberInfo member, string name)
        {
            if (member == null) throw new MissingMemberException(name);
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null,
            string finalizer = null)
        {
            if (target == null) throw new MissingMethodException("CE target method not found");
            var pre = prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SeamlessCombatExtendedCompat), prefix)) { priority = Priority.First };
            var post = postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SeamlessCombatExtendedCompat), postfix)) { priority = Priority.Last };
            var final = finalizer == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SeamlessCombatExtendedCompat), finalizer)) { priority = Priority.Last };
            harmony.Patch(target, pre, post, null, final);
            var info = Harmony.GetPatchInfo(target);
            var bound = info != null
                && (pre == null || info.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == pre.method))
                && (post == null || info.Postfixes.Any(p => p.owner == harmony.Id && p.PatchMethod == post.method))
                && (final == null || info.Finalizers.Any(p => p.owner == harmony.Id && p.PatchMethod == final.method));
            if (!bound) throw new InvalidOperationException($"Harmony did not register CE hook {target.DeclaringType?.FullName}::{target.Name}");
            Log.Message($"[RimExodus] Combat Extended compat: bound {target.DeclaringType?.FullName}::{target.Name}.");
        }

        private static bool TryPatch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null,
            string finalizer = null)
        {
            try
            {
                Patch(harmony, target, prefix, postfix, finalizer);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Combat Extended compat: one hook was disabled after signature drift: {ex.Message}");
                return false;
            }
        }

        private static void SetField(object instance, string name, object value)
        {
            CachedField(instance.GetType(), name)?.SetValue(instance, value);
        }

        /// <summary>
        /// Compatibility report types are known only after CE is loaded. Cache their fields on first
        /// encounter so burst fire and mortar reports never repeat Harmony reflection discovery.
        /// </summary>
        private static FieldInfo CachedField(Type type, string name)
        {
            lock (ReflectedFields)
            {
                if (!ReflectedFields.TryGetValue(type, out var fields))
                {
                    fields = new Dictionary<string, FieldInfo>();
                    ReflectedFields[type] = fields;
                }
                if (!fields.TryGetValue(name, out var field))
                {
                    field = AccessTools.Field(type, name);
                    fields[name] = field;
                }
                return field;
            }
        }
    }
}
