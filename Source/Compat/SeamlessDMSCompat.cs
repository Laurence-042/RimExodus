using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// DMS / Fortified Features Framework 的建筑选点只认识方形地图。DMS 散布建筑把
    /// 非水、无屋顶的 void 当作空地；FFF 卫星只检查方形 UsableRect，主结构遇到
    /// UsedRect 时也可能被挪到方形角落。候选阶段按同一套接缝几何排除 void
    /// 与接缝带，不在生成后删除建筑（那时清理和任务状态已经发生）。
    /// </summary>
    internal static class SeamlessDMSCompat
    {
        private const string DmsPackageId = "aoba.deadmanswitch.core";
        private const string FffPackageId = "aoba.framework";
        private static readonly ConditionalWeakTable<Map, BlockedGrid> BlockedGrids = new();

        [ThreadStatic] private static Map mainStructureMap;
        [ThreadStatic] private static Map satelliteMap;

        private static MethodInfo footprintAt;
        private static MethodInfo currentUsedRects;
        private static FieldInfo useTagField;
        private static bool warnedGeometry;

        public static void Register(Harmony harmony)
        {
            if (!IsLoaded(DmsPackageId) || !IsLoaded(FffPackageId)) return;

            try
            {
                var dmsType = AccessTools.TypeByName("DMS.DMS_GenStep_ScatterStructures");
                var openGround = dmsType == null ? null : AccessTools.Method(dmsType, "IsOpenGround",
                    new[] { typeof(Map), typeof(CellRect) });
                if (openGround == null)
                    Log.Warning("[RimExodus] DMS compat: IsOpenGround(Map, CellRect) not found; scatter filter disabled.");
                else
                {
                    harmony.Patch(openGround, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(SeamlessDMSCompat), nameof(OpenGroundPostfix))));
                    Log.Message("[RimExodus] DMS compat: bound scattered structure cell filter.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] DMS compat: scatter filter binding failed: {ex}");
            }

            try
            {
                var baseType = AccessTools.TypeByName("Fortified.Structures.GenStep_FFFStructureGen");
                var garrisonType = AccessTools.TypeByName("Fortified.Structures.GenStep_FFFGarrisonedStructure");
                var utilityType = AccessTools.TypeByName("Fortified.Structures.FFF_StructureUtility");
                var structureType = AccessTools.TypeByName("Fortified.Structures.IFFF_Structure");
                if (baseType == null || garrisonType == null || utilityType == null || structureType == null)
                {
                    Log.Warning("[RimExodus] DMS compat: FFF structure types not found; placement filters disabled.");
                    return;
                }

                useTagField = AccessTools.Field(baseType, "useTag");
                footprintAt = AccessTools.Method(utilityType, "FootprintAt",
                    new[] { structureType, typeof(IntVec3), typeof(Rot4) });
                currentUsedRects = AccessTools.Method(utilityType, "CurrentUsedRects", new[] { typeof(Map) });
                var satelliteScatter = AccessTools.Method(garrisonType, "ScatterSatellites",
                    new[] { typeof(Map), typeof(Faction) });
                if (useTagField != null && footprintAt != null && satelliteScatter != null)
                {
                    // 被调用方先绑定；若上下文钩子绑定失败，Postfix 不会产生行为。
                    harmony.Patch(footprintAt, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(SeamlessDMSCompat), nameof(FootprintAtPostfix))));
                    harmony.Patch(satelliteScatter,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(SeamlessDMSCompat), nameof(SatellitePrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(SeamlessDMSCompat), nameof(SatelliteFinalizer))));
                    Log.Message("[RimExodus] DMS compat: bound FFF satellite footprint filter.");
                }
                else
                    Log.Warning("[RimExodus] DMS compat: FFF satellite signature drift; satellite filter disabled.");

                var baseGenerate = AccessTools.Method(baseType, nameof(GenStep.Generate),
                    new[] { typeof(Map), typeof(GenStepParams) });
                var garrisonGenerate = AccessTools.Method(garrisonType, nameof(GenStep.Generate),
                    new[] { typeof(Map), typeof(GenStepParams) });
                var avoidUsedRects = AccessTools.Method(baseType, "AvoidUsedRects",
                    new[] { typeof(Map), structureType, typeof(IntVec3), typeof(Rot4) });
                if (useTagField == null || footprintAt == null || currentUsedRects == null || baseGenerate == null
                    || garrisonGenerate == null || avoidUsedRects == null)
                {
                    Log.Warning("[RimExodus] DMS compat: FFF main structure signature drift; main filter disabled.");
                    return;
                }

                // AvoidUsedRects 在 ReserveUsedRect / Generate / post-scatter 之前。
                // 无处可放时抛本层哨兵，由最外层 Garrisoned.Generate finalizer 吞掉；
                // 这样卫星、中央建筑和守军都不会围着不存在的主建筑生成。
                harmony.Patch(garrisonGenerate, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(SeamlessDMSCompat), nameof(GarrisonFinalizer))));
                harmony.Patch(baseGenerate,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(SeamlessDMSCompat), nameof(MainPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(SeamlessDMSCompat), nameof(MainFinalizer))));
                harmony.Patch(avoidUsedRects, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SeamlessDMSCompat), nameof(AvoidUsedRectsPostfix))));
                Log.Message("[RimExodus] DMS compat: bound FFF main structure footprint filter.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] DMS compat: FFF placement binding failed: {ex}");
            }
        }

        private static bool IsLoaded(string packageId)
        {
            foreach (var mod in LoadedModManager.RunningMods)
                if (string.Equals(mod.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsDmsStep(object step)
        {
            return useTagField?.GetValue(step) is string tag
                && tag.StartsWith("DMS_", StringComparison.Ordinal);
        }

        private static bool IsSurface(Map map) => map != null && SeamlessTileRegistry.GetMapWorldTile(map) >= 0;

        private static void OpenGroundPostfix(Map map, CellRect rect, ref bool __result)
        {
            if (!__result) return;
            try { if (IsSurface(map) && IsBlocked(map, rect)) __result = false; }
            catch (Exception ex) { WarnGeometry(ex); }
        }

        private static void SatellitePrefix(object __instance, Map map, out Map __state)
        {
            __state = satelliteMap;
            satelliteMap = null;
            try { if (IsSurface(map) && IsDmsStep(__instance)) satelliteMap = map; }
            catch (Exception ex) { WarnGeometry(ex); }
        }

        private static Exception SatelliteFinalizer(Exception __exception, Map __state)
        {
            satelliteMap = __state;
            return __exception;
        }

        private static void FootprintAtPostfix(ref CellRect __result)
        {
            if (satelliteMap == null) return;
            try
            {
                // ScatterSatellites 接着调用 Contains(usable, rect)。default rect
                // 位于 (0,0)，方形 UsableRect 至少内缩 8 格，原循环会继续重试/内收。
                if (IsBlocked(satelliteMap, __result)) __result = default;
            }
            catch (Exception ex) { WarnGeometry(ex); }
        }

        private static void MainPrefix(object __instance, Map map, out Map __state)
        {
            __state = mainStructureMap;
            mainStructureMap = null;
            try { if (IsSurface(map) && IsDmsStep(__instance)) mainStructureMap = map; }
            catch (Exception ex) { WarnGeometry(ex); }
        }

        private static Exception MainFinalizer(Exception __exception, Map __state)
        {
            mainStructureMap = __state;
            return __exception;
        }

        private static Exception GarrisonFinalizer(Exception __exception)
        {
            if (__exception is NoStructureRoomException noRoom)
            {
                Log.Warning($"[RimExodus] DMS compat: {noRoom.Message}; skipped this FFF site structure step.");
                return null;
            }
            return __exception;
        }

        private static void AvoidUsedRectsPostfix(Map map, object def, Rot4 rot, ref IntVec3 __result)
        {
            if (map == null || map != mainStructureMap || def == null) return;
            try
            {
                var foot = (CellRect)footprintAt.Invoke(null, new object[] { def, __result, rot });
                if (!IsBlocked(map, foot)) return;

                var used = currentUsedRects.Invoke(null, new object[] { map }) as IEnumerable<CellRect>;
                if (used == null)
                    throw new InvalidOperationException("FFF CurrentUsedRects did not return a CellRect list");
                var usedRects = new List<CellRect>(used);
                var size = map.Size;
                var found = false;
                var best = __result;
                long bestDistance = long.MaxValue;
                // 全图搜索仅在 FFF 原位置错误时发生，不进入逐格生成热路径。
                for (var z = 0; z < size.z; z++)
                for (var x = 0; x < size.x; x++)
                {
                    var dx = x - __result.x;
                    var dz = z - __result.z;
                    var distance = (long)dx * dx + (long)dz * dz;
                    if (distance >= bestDistance) continue;
                    var candidate = new IntVec3(x, 0, z);
                    var rect = foot.MovedBy(candidate - __result);
                    if (IsBlocked(map, rect)) continue;
                    var overlaps = false;
                    foreach (var reserved in usedRects)
                        if (reserved.Overlaps(rect)) { overlaps = true; break; }
                    if (overlaps) continue;
                    found = true;
                    bestDistance = distance;
                    best = candidate;
                }

                if (!found)
                    throw new NoStructureRoomException($"no complete playable footprint for {(def as Def)?.defName ?? "structure"} on map {map.uniqueID}");
                __result = best;
            }
            catch (NoStructureRoomException) { throw; }
            catch (Exception ex) { WarnGeometry(ex); }
        }

        private static bool IsBlocked(Map map, CellRect rect)
        {
            return BlockedGrids.GetValue(map, BuildBlockedGrid).Intersects(rect);
        }

        private static BlockedGrid BuildBlockedGrid(Map map)
        {
            var size = map.Size;
            if (size.x != size.z)
                throw new InvalidOperationException($"expected a square RimExodus map, got {size.x}x{size.z}");
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, size.x);
            var stride = size.x + 1;
            var prefix = new int[stride * (size.z + 1)];
            for (var z = 0; z < size.z; z++)
            for (var x = 0; x < size.x; x++)
            {
                var cell = new IntVec3(x, 0, z);
                var blocked = band.Band.Contains(cell)
                    || SeamlessPolygonGeometry.IsVoidCell(band, worldTile, size.x, cell);
                var i = (z + 1) * stride + x + 1;
                prefix[i] = (blocked ? 1 : 0) + prefix[i - 1] + prefix[i - stride] - prefix[i - stride - 1];
            }
            return new BlockedGrid(size.x, size.z, prefix);
        }

        private static void WarnGeometry(Exception ex)
        {
            if (warnedGeometry) return;
            warnedGeometry = true;
            Log.Warning($"[RimExodus] DMS compat: footprint geometry failed; placement filter degraded: {ex}");
        }

        private sealed class NoStructureRoomException : Exception
        {
            public NoStructureRoomException(string message) : base(message) { }
        }

        private sealed class BlockedGrid
        {
            private readonly int width;
            private readonly int height;
            private readonly int[] prefix;
            private readonly int stride;

            public BlockedGrid(int width, int height, int[] prefix)
            {
                this.width = width;
                this.height = height;
                this.prefix = prefix;
                stride = width + 1;
            }

            public bool Intersects(CellRect rect)
            {
                if (rect.minX < 0 || rect.minZ < 0 || rect.maxX >= width || rect.maxZ >= height)
                    return true;
                var left = rect.minX;
                var right = rect.maxX + 1;
                var top = rect.minZ;
                var bottom = rect.maxZ + 1;
                return prefix[bottom * stride + right] - prefix[top * stride + right]
                    - prefix[bottom * stride + left] + prefix[top * stride + left] > 0;
            }
        }
    }
}
