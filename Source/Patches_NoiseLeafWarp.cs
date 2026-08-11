using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 阶段4 连续地形：噪声叶子统一采样层 patch（可切换生成器）。
    ///
    /// Patch Perlin 和 RidgedMultifractal 的 GetValue(double,double,double) override，
    /// 在 RimExodus 地块地图生成期间调 SeamlessNoiseProvider 变换坐标 + 固定 seed。
    ///
    /// 噪声类型由 RimExodusSettings.noiseGenType 决定（Off/SphereNormal/LatLong/DiagnosticRings）。
    /// 详见 SeamlessNoiseProvider。
    ///
    /// Gate：MapGenerator.mapBeingGenerated?.Parent is MapParent_SeamlessTile
    /// 只在 RimExodus 地块地图生成期间生效，运行时/原版地图生成零误伤。
    /// </summary>
    public static class Patches_NoiseLeafWarp
    {
        private static readonly ConditionalWeakTable<Perlin, object> seededPerlin = new();
        private static readonly ConditionalWeakTable<RidgedMultifractal, object> seededRidged = new();

        private static readonly FieldInfo perlinSeedField =
            typeof(Perlin).GetField("seed", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ridgedSeedField =
            typeof(RidgedMultifractal).GetField("seed", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly object SeedMarker = new();

        private static bool IsRimExodusGen(out int worldTile, out int mapSize)
        {
            worldTile = -1;
            mapSize = 0;
            var map = MapGenerator.mapBeingGenerated;
            if (map == null) return false;
            if (!(map.Parent is MapParent_SeamlessTile parent)) return false;
            if (parent.worldTile < 0) return false;
            worldTile = parent.worldTile;
            mapSize = map.Size.x;
            return true;
        }

        private static void EnsureFixedSeed(Perlin perlin)
        {
            if (seededPerlin.TryGetValue(perlin, out _)) return;
            perlinSeedField?.SetValue(perlin, SeamlessNoiseProvider.FixedSeed);
            seededPerlin.Add(perlin, SeedMarker);
        }

        private static void EnsureFixedSeed(RidgedMultifractal ridged)
        {
            if (seededRidged.TryGetValue(ridged, out _)) return;
            ridgedSeedField?.SetValue(ridged, SeamlessNoiseProvider.FixedSeed);
            seededRidged.Add(ridged, SeedMarker);
        }

        [HarmonyPatch(typeof(Perlin), nameof(Perlin.GetValue), new[] { typeof(double), typeof(double), typeof(double) })]
        static class Patch_Perlin_GetValue
        {
            static bool Prefix(Perlin __instance, ref double x, ref double y, ref double z, ref double __result)
            {
                if (!IsRimExodusGen(out var worldTile, out var mapSize)) return true;

                // 诊断同心圆模式：直接返回值，跳过 Perlin。
                if (SeamlessNoiseProvider.IsDiagnosticRings)
                {
                    __result = SeamlessNoiseProvider.DiagnosticValue(x, z, worldTile, mapSize);
                    return false;
                }

                if (!SeamlessNoiseProvider.TryWarp(x, z, worldTile, mapSize, out var wx, out var wy, out var wz))
                    return true;

                EnsureFixedSeed(__instance);
                x = wx; y = wy; z = wz;
                return true;
            }
        }

        [HarmonyPatch(typeof(RidgedMultifractal), nameof(RidgedMultifractal.GetValue), new[] { typeof(double), typeof(double), typeof(double) })]
        static class Patch_RidgedMultifractal_GetValue
        {
            static bool Prefix(RidgedMultifractal __instance, ref double x, ref double y, ref double z, ref double __result)
            {
                if (!IsRimExodusGen(out var worldTile, out var mapSize)) return true;

                if (SeamlessNoiseProvider.IsDiagnosticRings)
                {
                    __result = SeamlessNoiseProvider.DiagnosticValue(x, z, worldTile, mapSize);
                    return false;
                }

                if (!SeamlessNoiseProvider.TryWarp(x, z, worldTile, mapSize, out var wx, out var wy, out var wz))
                    return true;

                EnsureFixedSeed(__instance);
                x = wx; y = wy; z = wz;
                return true;
            }
        }
    }
}
