using Verse;

namespace RimExodus
{
    /// <summary>
    /// 噪声生成器类型（连续地形用）。通过 Mod 设置切换，便于隔离测试不同方案。
    /// </summary>
    public enum NoiseGenType
    {
        /// <summary>不干预噪声生成（原版独立 map-local Perlin，各地块不连续）。</summary>
        Off = 0,
        /// <summary>球面法线投影：cell 偏移通过切平面基映射到球面 3D。</summary>
        SphereNormal = 1,
        /// <summary>经纬网格：cell 偏移加到 tile 经纬度，2D Perlin 在经纬空间采样。</summary>
        LatLong = 2,
        /// <summary>诊断同心圆：以锚点为圆心的同心圆（用于目测接缝连续性）。</summary>
        DiagnosticRings = 3,
    }

    /// <summary>
    /// RimExodus 的 Mod 设置（阶段4a：邻居预加载）。
    /// 持久化在 mod 自有存档文件中（GetSettings&lt;T&gt; 机制）。
    /// </summary>
    public class RimExodusSettings : ModSettings
    {
        /// <summary>
        /// pawn 距多边形边界多少格内时触发对应邻居地块的预加载。
        /// </summary>
        public int borderPreloadDistance = 15;

        /// <summary>开档时是否预加载锚点地块的全部世界邻居。</summary>
        public bool preloadAllNeighborsOnStart = false;

        /// <summary>详细诊断日志开关。</summary>
        public bool verboseLogging = false;

        /// <summary>多边形边内侧 N 格禁建（阶段4 安全约束）。</summary>
        public int borderNoBuildDistance = 3;

        /// <summary>
        /// 连续地形噪声生成器类型。默认 SphereNormal。
        /// 在 Mod 设置里切换以测试不同方案。
        /// </summary>
        public NoiseGenType noiseGenType = NoiseGenType.SphereNormal;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref noiseGenType, "noiseGenType", NoiseGenType.SphereNormal);
            base.ExposeData();
        }
    }
}

