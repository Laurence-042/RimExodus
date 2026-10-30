using System;
using RimWorld;
using RimExodus.Maps;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using HarmonyLib;

namespace RimExodus
{
    [HarmonyPatch(typeof(JobGiver_AIGotoNearestHostile), "TryGiveJob")]
    internal static class Patch_FrameworkRaidMarch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (SeamlessFramework.Enabled && __result == null
                && pawn?.GetLord()?.LordJob is LordJob_SeamlessRaidMarch march && march.Marching)
                __result = march.TryMakeMarchJob(pawn);
        }
    }
    public static class SeamlessCrossMapSight
    {
        internal static MapCombatCoordinates.CombatLink Shared(in SeamlessCombatCoords.CombatLink link)
            => new MapCombatCoordinates.CombatLink { host = link.host, target = link.target, offset = link.offset };
        public static bool TryFindShootLine(Verb verb, IntVec3 root, LocalTargetInfo target,
            in SeamlessCombatCoords.CombatLink link, bool ignoreRange, out ShootLine line)
            => SeamlessFramework.Enabled
                ? MapCombatSight.TryFindShootLine(verb, root, target, Shared(link), ignoreRange, out line)
                : LegacySeamlessCrossMapSight.TryFindShootLine(verb, root, target, link, ignoreRange, out line);
        public static bool LineOfSightToEdgesSegmented(IntVec3 start, IntVec3 end,
            in SeamlessCombatCoords.CombatLink link, Map hostMap, bool skipFirstCell = false)
            => SeamlessFramework.Enabled ? MapCombatSight.LineOfSightToEdgesSegmented(start, end, Shared(link), hostMap, skipFirstCell)
                : LegacySeamlessCrossMapSight.LineOfSightToEdgesSegmented(start, end, link, hostMap, skipFirstCell);
        public static bool LineOfSightSegmented(IntVec3 start, IntVec3 end,
            in SeamlessCombatCoords.CombatLink link, Map hostMap, bool skipFirstCell = false,
            int halfXOffset = 0, int halfZOffset = 0, Func<IntVec3, bool> validator = null)
            => SeamlessFramework.Enabled ? MapCombatSight.LineOfSightSegmented(start, end, Shared(link), hostMap, skipFirstCell, halfXOffset, halfZOffset, validator)
                : LegacySeamlessCrossMapSight.LineOfSightSegmented(start, end, link, hostMap, skipFirstCell, halfXOffset, halfZOffset, validator);
        public static bool CanSeeCrossMap(Thing seer, Thing target,
            in SeamlessCombatCoords.CombatLink link, Func<IntVec3, bool> validator)
            => SeamlessFramework.Enabled ? MapCombatSight.CanSeeCrossMap(seer, target, Shared(link), validator)
                : LegacySeamlessCrossMapSight.CanSeeCrossMap(seer, target, link, validator);
    }

    public static class SeamlessCrossMapCellTarget
    {
        public static void Register(Verb verb, Thing caster, Map host, Map target, IntVec3 cell)
        {
            if (SeamlessFramework.Enabled) MapCombatCellTargets.Register(verb, caster, host, target, cell);
            else LegacySeamlessCrossMapCellTarget.Register(verb, caster, host, target, cell);
        }
        public static void Clear(Verb verb, Thing caster)
        {
            if (SeamlessFramework.Enabled) MapCombatCellTargets.Clear(verb, caster);
            else LegacySeamlessCrossMapCellTarget.Clear(verb, caster);
        }
        private static bool Convert(bool valid, MapCombatCoordinates.CombatLink shared, out SeamlessCombatCoords.CombatLink link)
        {
            link = new SeamlessCombatCoords.CombatLink { host = shared.host, target = shared.target,
                offset = shared.offset, targetWorldTile = SeamlessTileRegistry.GetMapWorldTile(shared.target) };
            return valid;
        }
        public static bool TryResolve(Verb verb, LocalTargetInfo target, out SeamlessCombatCoords.CombatLink link, out IntVec3 unified)
        {
            if (!SeamlessFramework.Enabled) return LegacySeamlessCrossMapCellTarget.TryResolve(verb, target, out link, out unified);
            return Convert(MapCombatCellTargets.TryResolve(verb, target, out var shared, out unified), shared, out link);
        }
        public static bool TryResolve(Thing caster, LocalTargetInfo target, out SeamlessCombatCoords.CombatLink link, out IntVec3 unified)
        {
            if (!SeamlessFramework.Enabled) return LegacySeamlessCrossMapCellTarget.TryResolve(caster, target, out link, out unified);
            return Convert(MapCombatCellTargets.TryResolve(caster, target, out var shared, out unified), shared, out link);
        }
        public static bool TryResolve(Thing caster, Verb verb, LocalTargetInfo target, out SeamlessCombatCoords.CombatLink link, out IntVec3 unified)
        {
            if (!SeamlessFramework.Enabled) return LegacySeamlessCrossMapCellTarget.TryResolve(caster, verb, target, out link, out unified);
            return Convert(MapCombatCellTargets.TryResolve(caster, verb, target, out var shared, out unified), shared, out link);
        }
        internal static Verb BeginFiring(Verb verb) => SeamlessFramework.Enabled ? MapCombatCellTargets.BeginFiring(verb) : LegacySeamlessCrossMapCellTarget.BeginFiring(verb);
        internal static void EndFiring(Verb previous)
        {
            if (SeamlessFramework.Enabled) MapCombatCellTargets.EndFiring(previous);
            else LegacySeamlessCrossMapCellTarget.EndFiring(previous);
        }
        internal static bool TryResolveCurrent(LocalTargetInfo target, out SeamlessCombatCoords.CombatLink link, out IntVec3 unified)
        {
            if (!SeamlessFramework.Enabled) return LegacySeamlessCrossMapCellTarget.TryResolveCurrent(target, out link, out unified);
            return Convert(MapCombatCellTargets.TryResolveCurrent(target, out var shared, out unified), shared, out link);
        }
        public static bool TryGetUnifiedTarget(Thing caster, Verb verb, LocalTargetInfo target, out Vector3 unified)
            => SeamlessFramework.Enabled ? MapCombatCellTargets.TryGetUnifiedTarget(caster, verb, target, out unified)
                : LegacySeamlessCrossMapCellTarget.TryGetUnifiedTarget(caster, verb, target, out unified);
    }
}
