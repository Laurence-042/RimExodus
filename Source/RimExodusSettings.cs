using Verse;

namespace RimExodus
{
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
        /// 接缝覆写混合带占 tile 半径的比例（阶段4 连续地形）。
        /// 0.25 = 从接缝线向内 25% 的区域做 terrainDef 过渡混合。0 = 关闭接缝覆写。
        /// </summary>
        public float seamOverrideRatio = 0.25f;

        /// <summary>
        /// 接缝覆写权重噪声幅度（0=关闭噪声，回到纯线性 w）。0.15 = w 上下抖动 ±0.15。
        /// 在混合权重 w 上叠加低频空间 Perlin 噪声，dither 掉 GetMode 离散跳变，把规则等距过渡线
        /// 打散成自然弯曲斑块。种子基于 worldTile 稳定，同一地块多次生成噪声一致。
        /// 设 0 可关闭对照验证效果。
        /// </summary>
        public float seamOverrideNoiseAmplitude = 0.15f;

        /// <summary>
        /// 接缝覆写开发诊断开关（独立于 verboseLogging，只产接缝相关精简日志）。
        /// 开启时在 SeamlessSeamOverride.ApplyOneWay 关键位置取样：每邻居的 offset、混合带 cell 数、
        /// 越界跳过数、权重分布、采样到的 neighborCell 坐标范围、卷积众数。
        /// 用于精确定位"一侧空地/对侧岩石"的采样几何问题，不被 verbose 杂项干扰。
        /// </summary>
        public bool seamOverrideDiag = false;

        /// <summary>
        /// 海岸补铺：深水带宽度（格，到最近海洋边的距离 &lt; 此值铺深水）。0.15×mapSize 的比例。
        /// 见 <see cref="CoastalEdgeFill"/>。0 = 关闭海岸补铺。
        /// </summary>
        public float coastalEdgeDeepWaterDistance = 0.15f;

        /// <summary>
        /// 海岸补铺：浅水带宽度（格，到最近海洋边的距离 &lt; 此值铺浅水，&lt; 深水阈值则铺深水）。
        /// 必须大于深水阈值。
        /// </summary>
        public float coastalEdgeShallowWaterDistance = 0.25f;

        /// <summary>
        /// 海岸补铺：沙滩带宽度（格，到最近海洋边的距离 &lt; 此值且该格非水非冰时铺沙滩）。
        /// 必须大于浅水阈值。对应 Coast mutator 的 MaxForSand(0.6)。
        /// </summary>
        public float coastalEdgeBeachSandDistance = 0.35f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref seamOverrideRatio, "seamOverrideRatio", 0.25f);
            Scribe_Values.Look(ref seamOverrideNoiseAmplitude, "seamOverrideNoiseAmplitude", 0.15f);
            Scribe_Values.Look(ref seamOverrideDiag, "seamOverrideDiag", false);
            Scribe_Values.Look(ref coastalEdgeDeepWaterDistance, "coastalEdgeDeepWaterDistance", 0.15f);
            Scribe_Values.Look(ref coastalEdgeShallowWaterDistance, "coastalEdgeShallowWaterDistance", 0.25f);
            Scribe_Values.Look(ref coastalEdgeBeachSandDistance, "coastalEdgeBeachSandDistance", 0.35f);
            base.ExposeData();
        }
    }
}
