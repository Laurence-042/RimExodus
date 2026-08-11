using Verse;

namespace RimExodus
{
    /// <summary>
    /// 噪声生成器类型（连续地形用）。通过 Mod 设置切换，便于隔离测试不同方案。
    /// </summary>
    public enum NoiseGenType
    {
        /// <summary>不干预噪声生成（原版独立 map-local Perlin，各地块不连续）。对照用。</summary>
        Off = 0,
        /// <summary>全局平面坐标：cell 偏移 + tileOrigin，与 void 体系用同一套平面坐标（主方案）。</summary>
        PlaneGlobal = 1,
        /// <summary>诊断条纹：全局平面坐标的 X 分量做周期条纹，用于目测接缝连续性。</summary>
        DiagnosticRings = 2,
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
        /// 连续地形噪声生成器类型。默认 PlaneGlobal（全局平面坐标，与 void 体系同构）。
        /// 在 Mod 设置里切换以测试不同方案。
        /// </summary>
        public NoiseGenType noiseGenType = NoiseGenType.PlaneGlobal;

        /// <summary>
        /// 调试诊断模式：开启时 MapParent_SeamlessTile.MapGeneratorDef 切换到
        /// RimExodus_DebugDirtWaterGenerator（只跑 GenStep_DebugDirtWater + void 裁切 + Fog）。
        ///
        /// GenStep_DebugDirtWater 是纯"噪声显示器"：传 cell-local 坐标给 Perlin，
        /// 由 Patches_NoiseLeafWarp 统一做坐标变换（与正常 ElevationFertility 完全相同的路径），
        /// noise > 阈值 → Soil，否则 → WaterOceanDeep。直接写 terrainGrid + elevation grid。
        ///
        /// noiseGenType 仍生效：SphereNormal 显示球面法线噪声、DiagnosticRings 显示同心圆、
        /// Off 显示原版独立 map-local 噪声（对照）。不再需要"关 NoiseProvider 再开 debug"的矛盾操作。
        /// </summary>
        public bool debugDirtWaterMode = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref noiseGenType, "noiseGenType", NoiseGenType.PlaneGlobal);
            Scribe_Values.Look(ref debugDirtWaterMode, "debugDirtWaterMode", false);
            base.ExposeData();
        }
    }
}

