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

        /// <summary>
        /// RimExodus 内置 Ocean 地图补全：为原版空白 Ocean biome 提供深海地形与基础天气，并跳过
        /// 只适用于陆地的出生点/散布器/动物步骤。设置在启动时锁存，修改后须重启；关闭供专门
        /// 海洋 mod 完整接管 Ocean 地图表现，无缝裁切与跨图框架本身仍然生效。
        /// </summary>
        public bool oceanMapSupportEnabled = true;

        /// <summary>
        /// 详细诊断日志总开关。**已退役（2026-09 分模块日志开关取代）**：字段与 Scribe 仅为旧档
        /// 兼容保留，读入后忽略——无 UI、无消费点。新代码勿再读此字段，按模块用
        /// <see cref="RimExodusLog.Enabled(RimExodusLogModule)"/>。
        /// </summary>
        public bool verboseLogging = false;

        // ===== 分模块诊断日志开关（2026-09，取代 verboseLogging；默认全关，设置"高级"tab 逐项开启） =====
        // 归属划分与纪律见 RimExodusLogModule 类注释。

        /// <summary>生成与地形（分帧 genStep/河路/接缝混合/快照/中心走廊/海岸补铺）。</summary>
        public bool logGeneration = false;

        /// <summary>传送与边界（跨缝传送/桥接/许可/边界预加载/点击重放/相机聚焦/跨图下令/出口格）。</summary>
        public bool logTransfer = false;

        /// <summary>离场链诊断（[caravan-exit]/[diag]——远行队撤离/组队出口问题排查）。</summary>
        public bool logCaravanExit = false;

        /// <summary>天气域（域重算/图接入/换天广播/决策门控/强制激活/唤醒校准）。</summary>
        public bool logWeather = false;

        /// <summary>休眠与性能（governor 决策/降频细节；SLEEP/WAKE/DELETE 心跳常开不受控）。</summary>
        public bool logDormancy = false;

        /// <summary>跨图战斗（索敌/射击/弹道交接）。</summary>
        public bool logCombat = false;

        /// <summary>Combat Extended 独立诊断（仅弹丸发射/跨缝/命中离散事件）。</summary>
        public bool logCombatExtended = false;

        /// <summary>兼容层（VF/PS/GL 详细日志；绑定确认/自检行常开不受控）。</summary>
        public bool logCompat = false;

        /// <summary>据点与贸易（贸易商指定/影子远行队/贸易对话）。</summary>
        public bool logSettlement = false;

        /// <summary>核心与图管理（TileManager/BorderLookup/卸载恢复/Dev 调试动作）。</summary>
        public bool logCore = false;

        /// <summary>
        /// tick 花费剖析器（2026-08-27 诊断分级休眠收益）：每 600 ticks 输出一次每图分桶耗时
        /// （thing/MapPreTick/MapPostTick/MapUpdate）+ 全局 DoSingleTick 总耗时日志。
        /// 见 <see cref="SeamlessTickProfiler"/>。开启时有微小插桩开销，诊断用、平时关。
        /// </summary>
        public bool tickProfilingEnabled = false;

        /// <summary>剖析器汇报间隔（ticks，2026-08-27 设置化，原 const 600）。运行时 clamp [60, 3000]。
        /// 注意游戏内三档速度每现实秒最多 ~360 ticks，间隔太小汇报太频繁、样本也小。</summary>
        public int tickProfileIntervalTicks = 600;

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
        /// 无地图访问入口，内容完好）。下限 2（2026-08-29 收回 1——邻图预加载生成时玩家就在旁边，
        /// 距离 1 恒达，1 会导致刚生成的图立刻休眠甚至删除；CurrentMap / 玩家 pawn 在场保活仍在）。
        /// </summary>
        public int dormancySleepHops = 2;

        /// <summary>
        /// 距所有玩家 pawn 至少跨图 N 次才能到达的受管辖图被删除（地块图销毁 Map+WorldObject、
        /// 下次进入走生成链重建；原生家族延迟执行原版删除偏好——Settlement 删图留对象等；
        /// 玩家家园（IsPlayerHome）不休眠不删除）。允许与 sleepHops 相等（= 休眠当轮即删）；
        /// 运行时 clamp 为 ≥ sleepHops（"先满足休眠条件才可能被删除"），到达删除距离当轮直接删除。
        /// </summary>
        public int dormancyDeleteHops = 3;

        /// <summary>
        /// governor 扫描间隔（ticks，2026-08 设置化，原 const 600）。休眠判定非紧急，默认
        /// 600 ticks（10 游戏秒）粒度足够；调小 = 休眠/删除响应更快但扫描更频繁，调大反之。
        /// pawn 变动事件（跨缝/远行队进出图）不受此间隔约束、仍即时触发。运行时 clamp ≥60。
        /// </summary>
        public int dormancySweepIntervalTicks = 600;

        /// <summary>
        /// 威胁保活轮询间隔（ticks，2026-09 威胁保活）：governor 对"因活跃敌人而保活"的追踪名单
        /// 复查敌人是否清零的间隔，独立于扫描间隔（敌人清零后多久回落降频）。默认 120 ticks
        ///（2 游戏秒）；名单空时零成本。敌人的**发现**仍在扫描间隔粒度（≤ 一轮 Sweep）。
        /// **与休眠扫描间隔刻意不合并（2026-09 用户定夺，勿复犯）**：两者节奏本质不同——本复查
        /// 平时不执行零开销、有敌人时较高频执行以保证敌人行为正常回落；休眠扫描平时就要一直
        /// 低频跑。设置 UI 里两个滑条相邻摆放（同族"间隔"设置），机制独立。
        /// 运行时 clamp ≥60。
        /// </summary>
        public int threatKeepalivePollIntervalTicks = 120;

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

        // ===== 前哨保留（2026-09 封存/重放：居住区达阈的图到删除距离时不删，改为捕获记录拆图，
        // 再访时重生成 + genStep 395 重放。判定语义唯一出处 = SeamlessMapModificationTracker） =====

        /// <summary>
        /// 居住区忽略阈值（格）。删除时居住区格数 ≥ 此值 → 自动封存；非空但 &lt; 此值 → 弹窗询问
        /// （可用下方开关永久静默）。0 = 关闭阈值：任何非空居住区都自动封存、弹窗永不触发。
        /// 运行时 clamp [0,500]（消费处）。默认 20（自动居住区下随手一格不会误封存）。
        /// </summary>
        public int dormancyPreserveHomeAreaThreshold = 20;

        /// <summary>
        /// 保留数量上限（"保留最近的多少个"，0 = 不限，默认）。超限时按权重公式淘汰权重最低的
        /// 封存前哨（销毁 WO 连记录）。封存记录是 KB 级稀疏数据，运行时内存成本为零——上限纯粹
        /// 是玩家的管理偏好，不是性能保护（用户定夺 2026-09：不设硬约束）。运行时 clamp [0,999]。
        /// </summary>
        public int dormancyPreserveCount = 0;

        /// <summary>
        /// 淘汰权重公式 X：保留权重 = X×居住区格数 + Y×封存新近度序号（1 = 最近封存，越大越旧），
        /// 超限时淘汰**权重最低**者。X 调大 = 小居住区先出（保护大基地）；默认 X=0。范围 [-100,100]。
        /// </summary>
        public int dormancyPreserveWeightHome = 0;

        /// <summary>
        /// 淘汰权重公式 Y：默认 -1 → 权重 = -序号 → 淘汰最旧 = "保留最近 N 个"的直觉语义。
        /// 调成正数 = 反向（新封存先出）。范围 [-100,100]。
        /// </summary>
        public int dormancyPreserveWeightAge = -1;

        /// <summary>
        /// "以后不再弹出"（弹窗第三按钮经二次确认写入；设置里给开关便于反悔）。true 时小于阈值的
        /// 居住区图到删除距离直接删除、不封存不弹窗。
        /// </summary>
        public bool dormancyPreservePromptDisabled = false;

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
        /// 袭击外缘生成（2026-09）：步行类袭击（部落/海盗步行袭击、发狂动物、食尸鬼等）从
        /// "对侧未被看见（无图或休眠）的外侧接缝"进场，而非被袭击图朝向已加载邻图的边上凭空
        /// 出现。敌对袭击可生成在一跳邻图的外侧边并自行行军跨缝进攻（复用追击链）。空投/虫灾
        /// 等非步行进场不受影响。false = 完全原版落点。见 <see cref="SeamlessRaidOuterSpawn"/>。
        /// </summary>
        public bool raidOuterSpawnEnabled = true;

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

        /// <summary>
        /// 基础三层快照入存档（2026-08，默认开）：原生 parent 图（家园/原生家族）生成时的
        /// terrain/building/roof 三层快照以紧凑格式随图序列化，供"卸载前恢复原版兼容模式"
        /// 在**任意会话**回填真实地形（无快照只有"就近复制边界地形"降级，观感可能奇怪）。
        /// 只门控捕获，已捕获数据照存。关闭只影响"新数据不入档"——内存快照总保留、不再弹
        /// "删除已有快照"提示（2026-08-31 语义收拢），读档后缺失由
        /// <see cref="regenerateMissingSnapshots"/> 自动精简重生成补齐；清除 = 卸载恢复流程
        /// 的显式动作（PurgeAllSnapshots）。长玩在意存档体积可关。见 <see cref="SeamlessBaseSnapshotData"/>。
        /// </summary>
        public bool serializeBaseSnapshots = true;

        /// <summary>
        /// 生成邻接地图时源图缺内存快照的精简重生成（2026-08-31，默认开）：读档后（旧档或
        /// <see cref="serializeBaseSnapshots"/> 关闭）源图无基础三层快照 → 在临时 Map 上同步重跑
        /// order &lt; 389 的地形/岩体/屋顶 genStep 子集得到精简基础快照，回填所有实际参考邻图。
        /// 关闭后，缺失快照的 void 侧采样跳过，但邻图非 void 当前实况仍可参与混合。
        /// 见 <see cref="SeamlessSnapshotRegenerator"/>。
        /// </summary>
        public bool regenerateMissingSnapshots = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref serializeBaseSnapshots, "serializeBaseSnapshots", true);
            Scribe_Values.Look(ref regenerateMissingSnapshots, "regenerateMissingSnapshots", true);
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref oceanMapSupportEnabled, "oceanMapSupportEnabled", true);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false); // 退役字段：仅旧档兼容
            Scribe_Values.Look(ref logGeneration, "logGeneration", false);
            Scribe_Values.Look(ref logTransfer, "logTransfer", false);
            Scribe_Values.Look(ref logCaravanExit, "logCaravanExit", false);
            Scribe_Values.Look(ref logWeather, "logWeather", false);
            Scribe_Values.Look(ref logDormancy, "logDormancy", false);
            Scribe_Values.Look(ref logCombat, "logCombat", false);
            Scribe_Values.Look(ref logCombatExtended, "logCombatExtended", false);
            Scribe_Values.Look(ref logCompat, "logCompat", false);
            Scribe_Values.Look(ref logSettlement, "logSettlement", false);
            Scribe_Values.Look(ref logCore, "logCore", false);
            Scribe_Values.Look(ref tickProfilingEnabled, "tickProfilingEnabled", false);
            Scribe_Values.Look(ref tickProfileIntervalTicks, "tickProfileIntervalTicks", 600);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            Scribe_Values.Look(ref seamOverrideNoiseAmplitude, "seamOverrideNoiseAmplitude", 0.15f);
            Scribe_Values.Look(ref coastalEdgeDeepWaterDistance, "coastalEdgeDeepWaterDistance", 0.15f);
            Scribe_Values.Look(ref coastalEdgeShallowWaterDistance, "coastalEdgeShallowWaterDistance", 0.25f);
            Scribe_Values.Look(ref coastalEdgeBeachSandDistance, "coastalEdgeBeachSandDistance", 0.35f);
            Scribe_Values.Look(ref dormancyEnabled, "dormancyEnabled", true);
            Scribe_Values.Look(ref dormancySleepHops, "dormancySleepHops", 2);
            Scribe_Values.Look(ref dormancyDeleteHops, "dormancyDeleteHops", 3);
            Scribe_Values.Look(ref dormancySweepIntervalTicks, "dormancySweepIntervalTicks", 600);
            Scribe_Values.Look(ref threatKeepalivePollIntervalTicks, "threatKeepalivePollIntervalTicks", 120);
            Scribe_Values.Look(ref dormancyThrottlePercent, "dormancyThrottlePercent", 50);
            Scribe_Values.Look(ref throttleSeamFastRadius, "throttleSeamFastRadius", 15);
            Scribe_Values.Look(ref crossMapCombatEnabled, "crossMapCombatEnabled", true);
            Scribe_Values.Look(ref raidOuterSpawnEnabled, "raidOuterSpawnEnabled", true);
            Scribe_Values.Look(ref seamExitBandEnabled, "seamExitBandEnabled", true);
            Scribe_Values.Look(ref hideEmptyColonistBarGroups, "hideEmptyColonistBarGroups", false);
            Scribe_Values.Look(ref incrementalGenerationEnabled, "incrementalGenerationEnabled", true);
            Scribe_Values.Look(ref generationBatchSize, "generationBatchSize", 64);
            Scribe_Values.Look(ref dormancyPreserveHomeAreaThreshold, "dormancyPreserveHomeAreaThreshold", 20);
            Scribe_Values.Look(ref dormancyPreserveCount, "dormancyPreserveCount", 0);
            Scribe_Values.Look(ref dormancyPreserveWeightHome, "dormancyPreserveWeightHome", 0);
            Scribe_Values.Look(ref dormancyPreserveWeightAge, "dormancyPreserveWeightAge", -1);
            Scribe_Values.Look(ref dormancyPreservePromptDisabled, "dormancyPreservePromptDisabled", false);
            base.ExposeData();
        }
    }
}
