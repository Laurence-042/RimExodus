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
        /// 无地图访问入口，内容完好）。下限 1（2026-08 放宽，原下限 2——性能不佳的玩家可
        /// "离开即休眠"；CurrentMap / 玩家 pawn 在场保活仍在，不会睡脚下的图）。
        /// </summary>
        public int dormancySleepHops = 2;

        /// <summary>
        /// 距所有玩家 pawn 至少跨图 N 次才能到达的受管辖图被删除（地块图销毁 Map+WorldObject、
        /// 下次进入走生成链重建；原生家族延迟执行原版删除偏好——Settlement 删图留对象等；
        /// 玩家家园（IsPlayerHome）不休眠不删除）。允许与 sleepHops 相等（2026-08 放宽，
        /// 1/1 = 离开即休眠并销毁；运行时 clamp 为 ≥ sleepHops）。
        /// </summary>
        public int dormancyDeleteHops = 3;

        /// <summary>
        /// governor 扫描间隔（ticks，2026-08 设置化，原 const 600）。休眠判定非紧急，默认
        /// 600 ticks（10 游戏秒）粒度足够；调小 = 休眠/删除响应更快但扫描更频繁，调大反之。
        /// pawn 变动事件（跨缝/远行队进出图）不受此间隔约束、仍即时触发。运行时 clamp ≥60。
        /// </summary>
        public int dormancySweepIntervalTicks = 600;

        /// <summary>
        /// 分级休眠中间档（2026-08）：活跃圈内（BFS dist &lt; sleepHops）**无玩家 pawn** 的图按
        /// 百分比降频 tick（100 = 原生全速；50 = 每两 tick 一拍；0 = 永不放行 = 可见的凝固，
        /// 等价休眠但保留显示与 tick 注册）。Pawn 一并降频（远处世界变慢属预期语义）。
        /// 见 <see cref="SeamlessTickThrottle"/>。运行时 clamp [0,100]。
        /// </summary>
        public int dormancyThrottlePercent = 50;

        /// <summary>
        /// 降频图的接缝快速区半径（格，2026-08 空间分区）：距接缝边 ≤ 此值的格内 thing 全速 tick
        /// （跨缝战斗的 turret/投射物/追兵不吃降频），区外按相位跳。0 = 关闭空间分区（全图降频）。
        /// 图级系统（powerNet/天气/野生生成）不分区，整图同相位。运行时 clamp [0,50]。
        /// </summary>
        public int throttleSeamFastRadius = 15;

        /// <summary>
        /// 分帧增量生成开关（2026-08，默认 true）。false 时普通 tile 地图改走**原版
        /// <see cref="Verse.MapGenerator.GenerateMap"/> 方法本体**同步单帧生成（与 POI 分支同族）
        /// ——任何 patch 原版生成管线的第三方 mod（Geological Landforms 等）原生生效，
        /// 这是给特殊 mod 环境玩家的逃生通道（勿回退为"复刻同步行为"，必须是调原方法）。
        /// 仅影响普通 tile 地图；POI 原生分支（Settlement/Site 等 GetOrGenerateMap）本就走原版。
        /// </summary>
        public bool incrementalGenerationEnabled = true;

        /// <summary>
        /// 分帧增量生成的重步骤每帧格数批次（2026-08，默认 64 = 实测 ~5.4ms/批贴合 8ms 帧预算）。
        /// 调大 = 生成更快但每帧更卡。仅 <see cref="incrementalGenerationEnabled"/> 开启时有实义。
        /// 运行时 clamp [16, 512]。
        /// </summary>
        public int generationBatchSize = 64;

        /// <summary>
        /// 跨图索敌与射击（阶段5）：跨缝 LOS/目标搜索/射击线/弹道交接的总开关。
        /// false 时一切战斗语义回到原版（跨图目标不可射、邻图敌人不可见）。便于 A/B 回归。
        /// </summary>
        public bool crossMapCombatEnabled = true;

        /// <summary>
        /// 接缝带显示与撤离开关（2026-08，默认 true）。false = 沉浸模式（拍视频等）：
        /// 隐藏浅绿撤离带与接缝中心划线，并禁用一切经接缝带的原生离场成远行队（征召踩带、
        /// JobGiver_ExitMap、组队界面出口重定向一并关闭——隐藏后误触撤离比看不见边线严重）。
        /// 跨缝步行/跨图下令/传送**不受影响**；NPC 撤离链（逃窜动物/游荡兜底/追击）不门控
        /// （门控会造成追兵消失、动物滞留）。关闭时组队组不出队属预期（裁切图原生出口全 void），
        /// 不为正常游玩设计。
        /// </summary>
        public bool seamExitBandEnabled = true;

        /// <summary>
        /// 隐藏无人的殖民者栏分组（2026-08，默认 false = 原生行为）。true 时殖民者栏不显示
        /// "无玩家 pawn 的非玩家家地图"的空分组框（原版每图一组、空组也画框可点击切图）。
        /// 玩家家（含临时无人的家）保留 = 原版"空家仍显示"语义；休眠图过滤与此开关无关
        /// （既定设计，常开）。PlaySettings 全局控制条 toggle（与接缝带开关同族）。
        /// </summary>
        public bool hideEmptyColonistBarGroups = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref seamOverrideNoiseAmplitude, "seamOverrideNoiseAmplitude", 0.15f);
            Scribe_Values.Look(ref coastalEdgeDeepWaterDistance, "coastalEdgeDeepWaterDistance", 0.15f);
            Scribe_Values.Look(ref coastalEdgeShallowWaterDistance, "coastalEdgeShallowWaterDistance", 0.25f);
            Scribe_Values.Look(ref coastalEdgeBeachSandDistance, "coastalEdgeBeachSandDistance", 0.35f);
            Scribe_Values.Look(ref dormancyEnabled, "dormancyEnabled", true);
            Scribe_Values.Look(ref dormancySleepHops, "dormancySleepHops", 2);
            Scribe_Values.Look(ref dormancyDeleteHops, "dormancyDeleteHops", 3);
            Scribe_Values.Look(ref dormancySweepIntervalTicks, "dormancySweepIntervalTicks", 600);
            Scribe_Values.Look(ref dormancyThrottlePercent, "dormancyThrottlePercent", 50);
            Scribe_Values.Look(ref throttleSeamFastRadius, "throttleSeamFastRadius", 15);
            Scribe_Values.Look(ref crossMapCombatEnabled, "crossMapCombatEnabled", true);
            Scribe_Values.Look(ref seamExitBandEnabled, "seamExitBandEnabled", true);
            Scribe_Values.Look(ref hideEmptyColonistBarGroups, "hideEmptyColonistBarGroups", false);
            Scribe_Values.Look(ref incrementalGenerationEnabled, "incrementalGenerationEnabled", true);
            Scribe_Values.Look(ref generationBatchSize, "generationBatchSize", 64);
            base.ExposeData();
        }
    }
}
