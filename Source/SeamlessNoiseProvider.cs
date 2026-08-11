using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4 连续地形：全局平面坐标噪声采样器。
    ///
    /// 【核心思想】噪声采样和 void 裁切用完全相同的坐标系——cell 的平面坐标。
    /// void 已用平面坐标 + ComputeNeighborOffset 实现跨 tile 连续（接缝中点重合，误差 ≤ 重叠带）。
    /// 噪声复用这套坐标：采样坐标 = cellLocal - center + tileOrigin。
    /// 相邻 tile 共享边 cell 的采样坐标接近（同构于 void 的连续性）→ Perlin 连续。
    ///
    /// 不用球面坐标。Perlin 在任何连续坐标系里都连续，不限于球面。
    /// 球面坐标方案（CellToSphere/SphereToPerlinSpace/插值）已废弃——相邻 tile 切平面不对齐，
    /// 任何"cell→球面"反向映射都产生 ~1.0 球面单位的系统性错位。
    ///
    /// tileOrigin 传播：锚点=(0,0)，口袋=源 tileOrigin + ComputeNeighborOffset（生成时确定）。
    /// 固定 seed（FixedSeed）让所有 tile 共享同一 Perlin 场。
    /// </summary>
    public static class SeamlessNoiseProvider
    {
        public const int FixedSeed = 13579;

        private static int curTile = -1;
        private static int curMapSize;
        private static Vector2 curTileOrigin;

        /// <summary>
        /// 通知噪声生成器：开始为 worldTile 生成地图，缓存其 tileOrigin。
        /// 在 GenerateTileMap 里调用（tileOrigin 已算出）。
        /// </summary>
        public static void NotifyGenerationStarted(int worldTile, int mapSize, Vector2 tileOrigin)
        {
            curTile = worldTile;
            curMapSize = mapSize;
            curTileOrigin = tileOrigin;
            if (RimExodusMod.Settings?.verboseLogging ?? false)
            {
                Log.Message($"[RimExodus] NotifyGen: tile={worldTile} tileOrigin=({tileOrigin.x:F1},{tileOrigin.y:F1}) type={RimExodusMod.Settings?.noiseGenType}");
            }
        }

        public static void NotifyGenerationEnded() { curTile = -1; }

        /// <summary>
        /// 把 map-local 坐标 (x,z) 变换为全局平面采样坐标 (wx, wz)。
        /// 返回 true = 已变换，调用方用新坐标继续原 Perlin。
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

            // 取当前 tile 的 tileOrigin（NotifyGenerationStarted 缓存）。
            // 理论上 TryWarp 只在生成期间被 patch 调用，curTile 必匹配；防御性回退 (0,0)。
            Vector2 origin;
            if (worldTile == curTile && mapSize == curMapSize)
                origin = curTileOrigin;
            else
                origin = Vector2.zero;

            var center = mapSize * 0.5f;
            // 全局平面坐标：cell 相对 tile 中心的偏移 + tile 在全局平面的原点。
            wx = (float)x - center + origin.x;
            wy = 0;
            wz = (float)z - center + origin.y;
            return true;
        }

        /// <summary>
        /// 诊断条纹：直接计算输出值（不经过 Perlin）。
        /// 用全局平面坐标的 X 分量做周期条纹——与 PlaneGlobal 走相同的坐标路径。
        /// 如果 tileOrigin 传播正确，相邻 tile 共享边的条纹会连续对齐（误差 ≤ 重叠带）。
        /// </summary>
        public static double DiagnosticValue(double x, double z, int worldTile, int mapSize)
        {
            Vector2 origin;
            if (worldTile == curTile && mapSize == curMapSize)
                origin = curTileOrigin;
            else
                origin = Vector2.zero;

            var center = mapSize * 0.5f;
            // 全局平面坐标（与 TryWarp 相同）。
            var gx = (float)x - center + origin.x;

            // 取 X 分量做条纹。period = mapSize（一个 tile 宽度一个周期）。
            var period = (float)mapSize;
            var phase = (gx % period) / period;
            if (phase < 0) phase += 1f;
            // 三角波 [0,1]：条纹边缘。
            var tri = phase < 0.5f ? phase * 2f : (1f - phase) * 2f;
            var v = Mathf.SmoothStep(0.35f, 0.65f, tri);
            return v * 2f - 1f; // [-1,1]
        }

        /// <summary>当前是否为诊断条纹模式（patch 据此决定是否直接返回值）。</summary>
        public static bool IsDiagnosticRings =>
            (RimExodusMod.Settings?.noiseGenType ?? NoiseGenType.Off) == NoiseGenType.DiagnosticRings;
    }
}
