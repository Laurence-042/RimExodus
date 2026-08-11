using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4 连续地形：可切换的噪声生成器框架。
    ///
    /// 根据 RimExodusSettings.noiseGenType 选择不同的噪声坐标变换方案。
    /// 每个方案把 map-local 采样坐标 (x,z) 映射到世界坐标 (wx,wy,wz)，
    /// 用相同 seed 的 Perlin 在世界坐标采样，使相邻地块接缝连续。
    ///
    /// 设计目的：
    /// - 隔离测试不同方案（球面法线 / 经纬网格 / 诊断同心圆）。
    /// - 后续调整互不干扰（改一个生成器不影响其他）。
    /// </summary>
    public static class SeamlessNoiseProvider
    {
        public const int FixedSeed = 13579;

        private static readonly Dictionary<int, TileBasis> basisCache = new();

        private class TileBasis
        {
            public Vector3 anchor;
            public Vector3 first;
            public Vector3 second;
            public Vector2 latLong; // 经纬度（弧度）
        }

        private static int curTile = -1;
        private static TileBasis curBasis;
        private static int curMapSize;

        public static void NotifyGenerationStarted(int worldTile, int mapSize)
        {
            curTile = worldTile;
            curMapSize = mapSize;
            curBasis = GetBasis(worldTile);
            if (RimExodusMod.Settings?.verboseLogging ?? false)
            {
                var b = curBasis;
                Log.Message($"[RimExodus] NotifyGen: tile={worldTile} anchor=({b.anchor.x:F3},{b.anchor.y:F3},{b.anchor.z:F3}) latLong=({b.latLong.x:F4},{b.latLong.y:F4}) type={RimExodusMod.Settings?.noiseGenType}");
            }
        }

        public static void NotifyGenerationEnded() { curTile = -1; }

        /// <summary>
        /// 根据 noiseGenType 把 map-local (x,z) 变换为世界坐标 (wx,wy,wz)。
        /// 返回 true = 已变换坐标，调用方应用变换后继续原 Perlin。
        /// 返回 false = 不干预，调用方放行原版。
        /// </summary>
        public static bool TryWarp(double x, double z, int worldTile, int mapSize,
            out double wx, out double wy, out double wz)
        {
            var type = RimExodusMod.Settings?.noiseGenType ?? NoiseGenType.Off;
            if (type == NoiseGenType.Off)
            {
                wx = x; wy = 0; wz = z;
                return false;
            }

            TileBasis basis;
            if (worldTile == curTile && mapSize == curMapSize && curBasis != null)
                basis = curBasis;
            else
                basis = GetBasis(worldTile);

            var center = mapSize * 0.5f;
            var offX = (float)x - center;
            var offZ = (float)z - center;

            switch (type)
            {
                case NoiseGenType.SphereNormal:
                    // 球面法线投影：sample3D = anchor + offZ·first - offX·second
                    wx = basis.anchor.x + basis.first.x * offZ - basis.second.x * offX;
                    wy = basis.anchor.y + basis.first.y * offZ - basis.second.y * offX;
                    wz = basis.anchor.z + basis.first.z * offZ - basis.second.z * offX;
                    return true;

                case NoiseGenType.LatLong:
                    // 经纬网格：经纬度放大到 cell 尺度，加 cell 偏移。
                    // 关键标定：相邻 tile 经纬差 ≈ avgTileSize/Radius ≈ 0.5/100 = 0.005 弧度。
                    // 一个 tile 的 cell 跨度 = mapSize = 250 格。
                    // 要让经纬差对应 cell 距离：scale = mapSize / 经纬差 = 250 / 0.005 = 50000。
                    // 这样 A 东边 cell（offX=+125）和 B 西边 cell（offX=-125）的世界坐标相同。
                    // 运行时用 AverageTileSize 精确标定。
                    var llScale = mapSize / (Find.WorldGrid.AverageTileSize / 100f);
                    wx = basis.latLong.y * llScale + offX; // 经度方向
                    wy = 0;
                    wz = basis.latLong.x * llScale + offZ; // 纬度方向
                    return true;

                case NoiseGenType.DiagnosticRings:
                    // 同心圆模式由 patch 的 IsDiagnosticRings 分支直接调 DiagnosticValue 返回。
                    // TryWarp 不会在此模式下被调用（patch 先检查 IsDiagnosticRings）。
                    // 但以防万一，回退到 SphereNormal 坐标。
                    wx = basis.anchor.x + basis.first.x * offZ - basis.second.x * offX;
                    wy = basis.anchor.y + basis.first.y * offZ - basis.second.y * offX;
                    wz = basis.anchor.z + basis.first.z * offZ - basis.second.z * offX;
                    return true;

                default:
                    wx = x; wy = 0; wz = z;
                    return false;
            }
        }

        /// <summary>
        /// 诊断同心圆：直接计算输出值（不经过 Perlin）。
        /// 用球面法线投影坐标（anchor + first·offZ - second·offX）做距离。
        /// 这是之前验证过连续的坐标变换——圆弧跨接缝连续。
        /// </summary>
        public static double DiagnosticValue(double x, double z, int worldTile, int mapSize)
        {
            TileBasis basis;
            if (worldTile == curTile && mapSize == curMapSize && curBasis != null)
                basis = curBasis;
            else
                basis = GetBasis(worldTile);

            var center = mapSize * 0.5f;
            var offX = (float)x - center;
            var offZ = (float)z - center;

            // 球面法线投影坐标（与 SphereNormal 模式相同的坐标变换）
            var sx = basis.anchor.x + basis.first.x * offZ - basis.second.x * offX;
            var sy = basis.anchor.y + basis.first.y * offZ - basis.second.y * offX;
            var sz = basis.anchor.z + basis.first.z * offZ - basis.second.z * offX;

            // 以球心为圆心的距离（球面 3D 距离）
            var dist = Mathf.Sqrt(sx * sx + sy * sy + sz * sz);
            var period = 0.5f; // 球面单位周期（一个 tile ~0.5 单位）
            var phase = (dist % period) / period;
            var d = phase < 0.5f ? phase : 1f - phase;
            return 1f - Mathf.SmoothStep(0f, 0.1f, d) * 2f; // [-1,1]
        }

        /// <summary>当前是否为诊断同心圆模式（patch 据此决定是否直接返回值）。</summary>
        public static bool IsDiagnosticRings =>
            (RimExodusMod.Settings?.noiseGenType ?? NoiseGenType.Off) == NoiseGenType.DiagnosticRings;

        private static TileBasis GetBasis(int worldTile)
        {
            if (!basisCache.TryGetValue(worldTile, out var basis))
            {
                basis = new TileBasis();
                basis.anchor = Find.WorldGrid.GetTileCenter(worldTile);
                WorldRendererUtility.GetTangentsToPlanet(basis.anchor, out basis.first, out basis.second);
                var n = basis.anchor.normalized;
                basis.latLong = new Vector2(Mathf.Asin(n.y), Mathf.Atan2(n.z, n.x));
                basisCache[worldTile] = basis;
            }
            return basis;
        }
    }
}
