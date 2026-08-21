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
        /// 接缝覆写权重噪声幅度（0=关闭噪声，回到纯线性 w）。0.15 = w 上下抖动 ±15%（乘性）。
        /// 在衰减区混合权重上叠加低频空间 Perlin 噪声，dither 掉 GetMode 离散跳变，把规则等距过渡线
        /// 打散成自然弯曲斑块。种子基于 worldTile 稳定，同一地块多次生成噪声一致。
        /// 只作用于衰减区（3 圈接缝带重构后重叠区 w=1 直接拷贝，不受噪声影响）。
        /// 设 0 可关闭对照验证效果。
        /// </summary>
        public float seamOverrideNoiseAmplitude = 0.15f;

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

        /// <summary>
        /// 地图软休眠总开关（2026-08 滚动休眠）。false 时 governor 只做唤醒保活，不休眠/不删除。
        /// 见 <see cref="SeamlessDormancyGovernor"/>。
        /// </summary>
        public bool dormancyEnabled = true;

        /// <summary>
        /// 距所有玩家 pawn 至少跨图 N 次才能到达的图进入休眠（软休眠：不 tick、不显示为邻接地图、
        /// 无地图访问入口，内容完好）。下限 2——相邻图必须活跃（玩家跨图躲追击的 exploit 防线）。
        /// </summary>
        public int dormancySleepHops = 2;

        /// <summary>
        /// 距所有玩家 pawn 至少跨图 N 次才能到达的**地块图**被删除（销毁 Map+WorldObject，下次进入
        /// 走生成链重建；锚点/家园图永不删除）。必须大于 sleepHops。
        /// </summary>
        public int dormancyDeleteHops = 3;

        /// <summary>
        /// 跨图索敌与射击（阶段5）：跨缝 LOS/目标搜索/射击线/弹道交接的总开关。
        /// false 时一切战斗语义回到原版（跨图目标不可射、邻图敌人不可见）。便于 A/B 回归。
        /// </summary>
        public bool crossMapCombatEnabled = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref seamOverrideNoiseAmplitude, "seamOverrideNoiseAmplitude", 0.15f);
            Scribe_Values.Look(ref coastalEdgeDeepWaterDistance, "coastalEdgeDeepWaterDistance", 0.15f);
            Scribe_Values.Look(ref coastalEdgeShallowWaterDistance, "coastalEdgeShallowWaterDistance", 0.25f);
            Scribe_Values.Look(ref coastalEdgeBeachSandDistance, "coastalEdgeBeachSandDistance", 0.35f);
            Scribe_Values.Look(ref dormancyEnabled, "dormancyEnabled", true);
            Scribe_Values.Look(ref dormancySleepHops, "dormancySleepHops", 2);
            Scribe_Values.Look(ref dormancyDeleteHops, "dormancyDeleteHops", 3);
            Scribe_Values.Look(ref crossMapCombatEnabled, "crossMapCombatEnabled", true);
            base.ExposeData();
        }
    }
}
