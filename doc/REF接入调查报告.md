# RimExodus 接入 REF 调查报告

> 后续施工已获用户授权，当前进度见 [REF 接入实施与验收](REF接入实施与验收.md)。用户最后确定“保留 legacy、设置切换、验证后再移除”；下文调查阶段关于只保留一个后端、新记录只写 REF 的建议已由这一决定替代。本文保留调查时点事实，运行时现状以专题文档为准。

调查日期：2026-10-30。对象为本机 RimExodus（RE）、RimExodusFramework（REF）和 RimExodus_Delta-V 的当前工作副本；后两者正在同步开发，本报告不代表固定提交或发布验收结果。

本轮仅调查并新增本报告，没有修改代码、依赖、版本号或游戏数据。RE 原有 `About/About.xml`、`doc/TODO.md` 改动保留。REF 和 Delta-V 的 Git 状态查询受到目录访问限制，未取得其提交基线；以下结论来自可读取的文档和源码。

## 结论

**REF 可以作为 RE 的公共底座。后续工作分为公共能力补充、RE 调用适配和验收三类；调用尚未接通或尚未经游戏验证，不等于 REF 缺少能力。** 本报告按 2026-10-30 用户反馈修订职责边界；当前只更新调查与设计，尚未开始代码施工。

Delta-V 已采用 REF 的唯一公共后端，接入模式可以复用。不过它的船壳绘制、整图归档、口袋图和宇宙弹体，与 RE 的完整地表邻图、局部前哨恢复、表面原生地图和原版/CE 弹丸不是同一使用场景。Delta-V 接通不等于 RE 已获功能覆盖。

本报告将“底层替换”理解为：通用引擎机制最终由 REF 唯一实现；世界地块关系、接缝几何、生成内容、生命周期决策和 Mod 玩法适配留在 RE。REF 不应吸收 RE 的六边形世界规则，也不能把 RE 的 MapParent 改为口袋图。

本次确定的工作划分为：

1. **渲染**：1.6 公共后端需要增加可选的颜色目标清理；水深、天色和效果选择主要是 RE 适配及验收，不作为已证实能力缺口。1.7 水面观感更好是用户实测反馈，原因尚未定位。
2. **tick 调度**：快速区几何与判定函数由 RE 提供；REF 提供判定接入、全速例外执行、0% 区外注销/恢复 tick 注册和状态变更通知。百分比、错峰和移动补偿按维护成本与性能选型，不要求保留旧算法。
3. **前哨封存**：RE 在 1100 清理区域、恢复自然地形，再调用 REF 重建 prefab。REF 提供通用捕获、序列化、放置选项和结果报告，不接管 RE 的自然恢复或整区域置换流程。
4. **跨图战斗**：REF 需要完整原版及 CE 战斗桥接，按地图显式启用，默认关闭。关闭地图不承担战斗轮询、坐标更新或 tick 速率变化；Delta-V 不启用该功能。
5. **生成协调**：已有“一个组件独占原版生成静态状态”的登记服务；RE 需要登记任务、提供在同一次调用中完成当前生成的函数、覆盖成功/失败清理，并将保存检查和 MapPreview 互斥接到正确入口。

[REF 当前模块契约][ref-contract]和[原迁移备忘录][ref-plan]用于核对已有能力；其中曾计划的公共区域置换不是本次 RE 接入的前置条件，后续以本报告明确的职责划分施工。

## Delta-V 的实际接入方式

| 层面 | 当前 Delta-V 实现 | RE 应如何采用 |
| --- | --- | --- |
| 运行时依赖 | About 声明 `RimExodus.Framework` 并 `loadAfter` | 声明独立前置，加载顺序 Harmony → REF → RE；保留用户已有 About 修改，不改版本号 |
| 构建引用 | 先构建同级 REF，再复制 DLL 到 `References/REF/<版本>/`；csproj 设置 `Private=false` | 按版本准备编译引用，RE 的 Assemblies 不打包公共 DLL，避免双份加载 |
| 初始化 | `MapModule.Install()`；当前游戏的 `MapRuntime` 持有公共服务 | RE 使用独立 owner；新档/读档配置，删除/换档释放；安装失败应明确报错 |
| 地图调度 | `TickRates.Set/Release`；停止策略由 Delta-V 提供 | RE governor 仍决定休眠/降频，REF 执行调度和注册变更 |
| 渲染 | `MapDrawCapture`、`MapDrawBackend`；船壳规则和层序仍在 Delta-V | RE 提供地表内容选择和偏移，公共后端提交；不能照搬船壳显示规则 |
| 内容保存 | `MapPrefabs.Capture/Rebuild`，全图包装入口用于归档 | RE 清理和恢复自然地形后调用公共 prefab 重建；不能改用整图归档冒充局部恢复 |
| 生成 | 增量服务 `Acquire → Bind → Release`，提供同步完成当前任务的函数 | RE 使用同一生成占用登记；GenStep 执行和业务收尾仍由 RE 负责 |

代码依据：[构建脚本][dv-build]、[项目引用][dv-project]、[公共内容适配器][dv-adapter]、[初始化][dv-runtime]、[生成占用登记][dv-generation]、[渲染适配器][dv-render]。

**构建还有实际差异**：RE 当前固定 net48、输出到 `1.6/Assemblies`；REF/Delta-V 已按 1.6 net48、1.7 net10.0 分版。直接引用 REF 的 1.7 DLL 不适用于 RE 当前项目配置。接入须同步规划双版本构建、内容目录和 release 打包。不能只改 Debug 构建而遗漏现有 release 仅复制 1.6 的脚本。REF 1.6 引用的 Harmony.Ref 为 2.3.3，RE 为 2.4.2，需核对 API 和运行时绑定；差异本身不证明不兼容。[RE 项目][re-project]、[REF 引用配置][ref-build]

## 当前功能覆盖矩阵

“可接入”表示存在可复用机制，仍需实现适配并验证；“待补”表示当前接口或行为不足；“留 RE”表示消费者政策，不属于 REF 缺陷。

| RE 当前能力 | REF 覆盖判断 | 迁移边界 |
| --- | --- | --- |
| 表面 MapParent、真实 PlanetTile、原生 POI 一视同仁 | 留 RE；REF runtime 可登记普通 Map | 禁止套用 `PocketMaps` 或公共 sourceMap 宿主改变 RE 地图模型 |
| 邻接登记、双向 offset、休眠过滤 | 留 RE | `MapTransform` 不提供世界邻接和合法性判定 |
| 显示点/方向转换、保守视区 | 可接入 | 保留 `SeamlessViewProjection` 作为 RE 唯一入口，再委托 REF；方向仍为 `neighborLocal + offset = myLocal` |
| 六/五边形、void、接缝带、道路/河流/海岸/Ocean | 留 RE | REF 没有这些世界生成规则，不应重新实现第二套 |
| 389 前自然三层快照、邻图参考、卸载回填 | 留 RE | 这是自然生成基线，不能用当前区域 prefab 替代；必须区分无数据与明确为空 |
| 增量生成、同步 POI、异常清理 | 公共占用登记可用，RE 待接入 | 预算执行、GL 上下文、快照前置、后处理复放与邻接反向记录清理由 RE 保证；具体顺序见下文 |
| 主图/邻图静态层与动态物、水面、天色 | 可接入；1.6 清色选项待补 | 水面配对、双视区、筛选与重叠是适配/验收项，不据此宣称公共能力缺失 |
| Mote、邻图空间声音、效果投影 | 部分可接入 | 捕获可统一变换；声音选择与投影仍属 RE。不能重复平移 Mote，也不能默认开启整套 Fleck |
| 地图软休眠和唤醒注册 | 可接入 | Map 保留在 Find.Maps；天气、传送点、UI 和手动锁仍由 RE 通知 |
| 0–100% 降频、快速区、移动补偿 | 基础已有，补执行与通知能力 | 采用准确百分比、稳定错峰、0% 部分摘表；快速区规则留 RE，补偿按新调度计算 |
| 距离 BFS、家园/玩家/威胁保活、删除策略 | 留 RE | REF 接受结果，不决定世界距离或原生 WorldObject 去留 |
| 天气域、激活图交接、禁雨广播 | 留 RE | REF 无天气域实现；接入 tick 后仍需停摆/恢复事件 |
| Home 前哨封存/1100 重放 | RE 编排；REF 提供通用 prefab | 区域清理/自然恢复留 RE；公共选择、层过滤、占位配置和旧档转换需接入或补足 |
| 原版整图保真归档 | REF 有；非 RE 当前替代目标 | 不替代软休眠，也不改变“前哨区域恢复、外围自然重生成”的玩法 |
| 单跳跨缝移动、双侧代价场、许可与状态保存 | REF 未覆盖 | RE 保留可用链；若本轮目标包含通用转移底层，需另行从 RE 抽取，不能删除后改调战斗 API |
| 点击重放、公共函数跨图化、StartPath 桥接 | REF 未覆盖 | `TryPick` 仅是显示矩阵到矩形图格，不包含多边形归属、菜单、预约和工作执行 |
| 原版/CE 索敌、射击、纯格目标、弹丸迁移及战斗移动 | REF 待抽取完整链 | 提供按地图启停；源图和目标图均启用才参与；关闭无额外战斗 tick 工作 |
| CE、VF、PS、Giddy-Up、GL、MapPreview、VEF 等 | 按职责拆分 | 通用 CE 战斗桥接归 REF，保持软检测；其余 RE 专属政策与兼容层保留并重新接线 |
| 派系基地贸易、影子远行队、撤离/组队语义 | 留 RE | 不由公共地图 runtime 或 prefab 推断 |
| 袭击外缘、射程曲线、设置/UI/日志/卸载 | 留 RE | 依赖改动需同步说明，但不应顺带改变这些玩法 |

## 需要先解决的差异

### 1. 渲染：自定义管线可复用，默认原版 plan 不是替换按钮

RE 的构建项目目前使用 1.6 引用、net48 并输出到 1.6 目录，但这不意味着在两版游戏上执行同一条渲染路径。`SeamlessTileRenderer` 检查 `CommandBuffer` 所在程序集是否名为 `UnityStubs`：1.6 使用挂在相机上的 CommandBuffer，1.7 使用 `Graphics.DrawMesh` 交给 Waylith 提交。因此“1.6 编译产物”和“1.6 渲染后端”需要分开表述。

清理操作也有三个不同含义：

| 操作 | 实际作用 | RE 1.6 使用时点 |
| --- | --- | --- |
| `CommandBuffer.Clear()` | 删除缓冲中的旧绘制指令；不修改已经画出的像素 | 每帧重新收集前，主相机与水深相机的缓冲都清理 |
| `ClearRenderTarget(true, true, Color.clear, 1f)` | 清深度，并把当前颜色目标写成透明黑；这是本报告所说的“清色” | 主相机 BeforeForwardOpaque 阶段，画邻图之前；没有邻图也执行，防 void 留上帧残影 |
| `ClearRenderTarget(true, false, Color.clear, 1f)` | 只清深度，保留已绘制颜色；这里 Color.clear 参数不表示清颜色 | 邻图静态层画完后，让随后绘制的当前图覆盖重叠区 |

RE 1.6 主图路径可以写成：**清旧指令 → 录入清色/清深度 → 录入邻图网格 → 录入只清深度 → 相机执行这些指令 → 原版绘制当前图**。Watergen 走另一份挂在原版 WaterDepth 子相机上的缓冲；主相机清色不是清水深纹理。原版子相机还有自己的颜色清理与 RenderTexture 设置。[RE 渲染][re-render]、[1.6 子相机][vanilla-subcamera]

RE 1.7 不执行上述 CommandBuffer 清理命令；它将地形和 Watergen 提交到各自图层，邻图高度另减 0.01。Waylith 参考实现先执行 WaterDepth pass，再执行场景颜色及需要的场景抓取，自己管理渲染目标清理。不能把“RE 没调用清色”等同于“引擎没有清理颜色目标”。[1.7 渲染实现][waylith-render]

**关于用户反馈的水面差异：目前不能确定是清色导致。** 清色会影响透明混合的背景，但本报告未做控制变量实测。已确认同时变化的还有水深提交、引擎水面/场景抓取处理、绘制排序和重叠深度策略；这些都是调查方向，不是已定位根因。不能为了模仿 1.7 而直接删掉 1.6 的清色，也不能推断把清色移到 REF 就会改善水面。

RE 当前已经主动选择四类静态层、Pawn/Projectile/RealtimeOnly/Mote，使用当前图天色，并把 Watergen 独立送到 WaterDepth。REF `MapRenderPlan` 默认会调用原版动态绘制与 Fleck/GameCondition 等效果，且 `TryPick` 仅检查矩形 InBounds。直接用默认选项会改变 RE 当前效果范围和邻图点击边界。[RE 渲染][re-render]、[REF plan][ref-render-plan]

**已确认的 API 缺口**：REF 1.6 `BeginGroup/Finish` 只清深度、不清颜色；RE 在零邻居和正常邻图帧都显式清色，以防 void 留上一帧残影。应在公共后端增加明确的清色策略，并限定于 RE 的合法 Surface 视图，不能影响 Delta-V 星空。不能让 RE 长期保留第二套提交后端来补这个缺口。[REF backend][ref-render-backend]

**适配/验收项，不计作能力缺口**：REF 1.7 会把 `Layer` 改成分组标记，**但保留原生命令的 `PassId`**；源码与离线断言均支持这一点。因此不能据此断言“水深一定进主相机”。RE 需要把 Watergen、扩大的水深视区、关闭邻图 flow 偏移的材质处理，以及颜色/水深成对提交接入已有公共能力，再验证两版本效果。现有 Delta-V 船壳测试不覆盖这些场景，只说明证据不足。[REF 提交][ref-render-backend]、[捕获检查][ref-draw-tests]

建议以 RE 当前自定义收集为起点接入 REF，保留惰性 section 更新、雾中隐藏、天色处理和既定 Fleck 限制。逐步把通用捕获/投影/提交搬入 REF；同一命令只能平移一次。1.7 REF 的分组清深度与 RE 当前高度微偏移方案也需重新验证半透明接缝和前后遮挡，不能把两套补偿同时叠加。

REF 完整默认模式的逐图 shader 隔离尚未完成，这不必阻止 RE 继续使用既有自定义视觉契约；但不能在迁移完成时宣称因此获得“全部原版多图绘制保真”。

### 2. 降频：按维护成本与性能选型

用户已明确不以保留旧算法为目标。以下为源码比较后的施工选型，不是游戏性能实测结果。[RE 降频][re-throttle]、[RE 移动补偿][re-throttle-patches]、[REF tick][ref-ticks]

| 项目 | RE 当前实现 | REF 当前实现 | 迁移要求 |
| --- | --- | --- | --- |
| 百分比 | `ceil(100/percent)` 间隔，例如设置 75 实际每两次跑一次 | 执行机会累计，75 对应 75% | 选 REF 准确比例，减少设置与实际语义差异；按图缓存本次机会判定，避免每 Thing 重复整数除法 |
| 多图错峰 | 加 map.uniqueID 相位 | 相同比例按同一全局机会放行 | 选稳定地图相位，分散尖峰；相位不序列化、不逐 tick 重算，不能按图相位破坏三桶计数公平性 |
| Rare/Long | 按桶机会计数避免混叠 | 同样按机会计数 | 可复用，但需要验证真实三桶及切换 |
| 0% + 快速区 | 区外 Thing 摘表，区内保留注册 | 有 FullSpeed 委托时 `IsStopped=false`，不会区外摘表 | 公共层补部分摘表及幂等恢复；否则回退到大量空转 DoTick |
| 0% 可见图的 Update | 继续 Update | `suppressUpdateWhenStopped` 默认 true | 明确传 false；软休眠另用 true，保留两种状态的区别 |
| Pawn 移动 | 放行 tick 按 N 补移动成本 | 无移动补偿接口/patch | 选可配置的平均速度补偿：0<p<100 时按 100/p，快速区/全速为 1；不建每 Pawn 历史字典、不补睡眠时间、不保留旧 N |
| 声音/天气 | 0% 结束 sustainer 并通知天气交接 | 完全停止状态有声音清理，部分全速状态不走该分支；无天气域 | 补 RE 状态变更回调，快速区存在时也保留既定停摆事件 |

另一个需要单列验证的差异：REF 在 MapPreTick 用“下一 tick”计算机会，MapPostTick/Thing 用当前 tick；已核对 1.7 参考源码，原版在 Pre 之后递增计数。这是对调用时序的显式处理。RE 当前 Pre/Post 使用同一个当前计数谓词，二者并非简单可互换。迁移时应测试完整 Pre → 递增 → Thing → Post 顺序，而不是只测 `Allows()` 比例。

快速区职责明确分为两部分：

- RE 计算接缝附近哪些格属于快速区，向 REF 提供 `Func<Thing, bool>` 判定。REF 不引用接缝几何、worldTile 或 RE 设置。
- REF 负责执行全速例外、0% 时只保留例外 Thing 的 tick 注册，以及区域/位置/地图变化后的注册更新。现有 FullSpeed 参数已满足判定函数接入，缺的是完整执行和通知能力，不能写成“REF 没有快速区接口”。

选用 0% 区外摘表，因为它直接消除长期不会执行的 Thing 在全局 TickList 中的派发成本。1–99% 继续在执行入口筛选，不随每个 tick 摘表/回挂。0% 的注册更新由出生、离图、换图、位置变化及消费者显式通知的快速区规则变化触发；规则变化时允许扫描一次该图，不能新增周期性全图扫描。必须覆盖物体由快速区进入冻结区、外部代码将冻结区物体移入快速区、半径修改和反复睡醒；仅在首次进入 0% 摘一次表不足以构成通用能力。

移动补偿默认关闭，由 RE 开启。选择 `100/p` 是为了维护简单、无逐 Pawn 计时状态，长期期望速度保持原值；它不承诺逐 tick 位移轨迹与全速完全相同。补偿仅影响原版移动成本，不给所有 Thing 统一补算 tickDelta；0% 区外不执行，快速区不放大。设置变更直接使用新比率，不追补休眠时间。需验证低比例、跨快速区和寻路跨格上限；若实测失败，记录具体反例再调整，不能宣称数学平均值即完整游戏验收。

公共调度最终只留 REF 一套。RE 和 REF 两个 DoTick 筛选不能同时生效；profiler 等观察逻辑应与筛选分离。准确百分比作为本次已获方向授权的行为改进，更新设置说明与玩家变更记录，无需再次询问是否保留旧的近似百分比。性能验收比较相同地图/Thing 数下的派发次数、帧内峰值与总耗时；本轮不提供未经测量的性能提升数字。

### 3. 区域封存：RE 恢复场地，REF 重建 prefab

RE 在 1100 执行以下固定顺序：读取已保存的区域与内容 → RE 清理可拆建筑及应为空的屋顶、标记不可拆占用格 → RE 使用本轮自然快照恢复地形 → REF 按选项重建 prefab → RE 恢复 Home、揭雾关联状态并刷新路径成本。自然生成、采空/拆顶含义、场地清理都属于 RE，不迁入 REF。[现有恢复步骤][re-zone-restore]

REF 当前 `Capture/Rebuild` 的差异如下。[内容入口][ref-prefabs]

- **多格建筑选择不同**：RE 从 Home 格的 thingGrid 收集，与区域相交即记录；REF 先要求建筑锚点在 mask 内，之后才调用 include。仅传一个 include 回调无法恢复 RE 的相交选择语义。需要公共层提供选择政策，保持 mask 字面范围，不用扩大 Home 偷换含义。
- **自然地形不同**：RE 只记录地板/基架，自然环境相信重新生成；REF 记录选区 TerrainAt。默认捕获会将旧自然地形一并回放。需要可选内容层/地形过滤，并保留 RE 的自然基线来源。
- **空屋顶/采空由 RE 解释**：REF 记录 mask 和非空内容，RE 在调用前清理区域，从而保证“没有屋顶/建筑记录”的格不保留本次生成的岩顶或岩石。无需为此要求 REF 先实现整区域置换。
- **冲突配置是通用能力**：RE 提供保留占用格及处理政策；REF 对建筑完整足迹、地形/基架/屋顶都遵守该配置，支持跳过、报告失败或调用自定义放置函数，并区分预期跳过与真实失败。现有 PlaceThing 可定制建筑放置，但发生在地形/基架写入之后，不能单独保护全部层；应扩展现有选项，不再建第二套重建器。
- **Home 和探索态**：REF mask 不是 Home 自动恢复。仍需恢复 Home、现有揭雾流程和路径成本刷新，否则下一次离开可能失去封存条件。
- **身份与派系**：默认 Clone=true 会重分配 Thing/Verb 身份，并把可有派系对象改成一个传入派系；RE 现记录每建筑派系。恢复不能直接照抄 Delta-V 模板参数。Clone=false 又要求旧身份已离开有效对象集合，必须核对嵌套持有物、外部引用和旧图销毁过程。
- **完整 Comp 状态是能力升级**：旧 RE 明确不保真 Comp，REF 逐实例 Scribe 更丰富；档体大小、床归属等外部引用、容器内对象的处理均需声明，不能把旧记录没有的数据假装恢复出来。

REF 的补充范围限于通用选择/层过滤、受保护占位的放置选项、持久化和结果报告。RE 的区域清理、自然地形恢复及失败后的整次生成处理继续留在 RE。前哨恢复失败时保留原记录，丢弃本次失败生成图；该流程不要求 REF 对现有成熟地图提供整区域事务回滚。RE 不重复实现逐实例 Scribe 或 prefab 重建。

### 4. 旧档兼容：Delta-V 的新档纪律不能照搬

RE 已发布，以下身份与键不能因抽库随意删除或改名：`MapParent_SeamlessTile`、邻接 `NeighborLink`、Map/Game components、`preserveRecord`、`baseSnapshotData`、`manualDormantTiles`，以及 void/传送点 Def。REF 的 `MapRuntime` 可重建状态不应变成第二份持久事实。

当前 REF 文档对象只有文件式 Save/Load，没有 RE `ZoneMapRecord` 的 IExposable 存档契约；且 `MapPrefabThingState.Instantiate` 拒绝缺少 stateXml 的条目。旧 RE 记录只有简要属性，不能直接塞进 REF thingEntries 就当完成迁移。[旧记录][re-zone-record]、[REF 文档][ref-document]、[REF 实例读取][ref-thing-state]

**建议确认的方案**：保留旧类型和读取键，用一次转换适配器把旧前哨记录提升到公共内容模型；新增记录只走 REF，转换后也走同一恢复执行器。旧档缺失的 Comp 状态维持旧语义，不伪造历史值。转换/保存公共文档不得在游戏 Scribe 正忙时嵌套开新的 Scribe；可提供公共数据的外层持久化格式，把实体解析留到安全重建阶段。失败不清空旧记录。

这项兼容方案必须在施工前确认。不能使用“旧档请重开”作为默认实施办法，也不建议永久保留两套捕获/重放后端。

### 5. 跨图战斗/移动：接口存在，完整管线尚未迁入

REF `CrossMapCombat` 提供域许可、MapTarget、距离、分段 LOS、目标图掩体和伤害入口，并用 `CombatLease` 对象记录参与地图、增加战斗使用计数，在 Dispose 时减少计数。它目前没有原版 Verb/Projectile 自动跨图补丁，也没有 StartPath、许可触发、Follow/追击或 CE 实现。[战斗基础][ref-combat]

RE 的完整能力还包括纯格目标 Map 侧表、目标框架虚拟传送、索敌与命中报告、射程/LOS、弹丸越缝前 Map 迁移、origin/destination/剩余飞行状态、CE 射线与制导、NPC 跨缝追击、机械师命令、载具与骑乘状态。通用原版/CE 战斗桥接纳入 REF；RE 提供地表邻接、权威坐标与边界归属。载具/骑乘、撤离/组队等消费方规则仍留 RE。普通跨缝下令和 Follow 的许可不能被战斗开关一并关掉。

**按地图启停的目标契约（待实现）**：

- 每图显式登记是否启用通用跨图战斗，缺省关闭；不同图可独立开关。源图与目标图都启用、都可参与战斗且消费方允许这对地图连接，才执行桥接。域总开关可以保留，不能替代逐图状态。
- Delta-V 地图不登记启用，继续自己的 SpaceProjectile/SpaceCollision；不为这些图更新邻图索引、投影、LOS 缓存或扫描目标。RE 只登记合法 Surface Map，休眠图退出可参与集合。
- 未启用地图的索敌、Verb 和 Projectile 补丁先做状态快速判断，立即交还原版；源图判断必须早于邻图枚举和坐标转换。不增加每 tick 扫描所有地图或所有 pawn 的调度器，不设置 tick 全速覆盖，不改变原来的停止状态。
- 共享 Harmony 补丁仍可能有一次调用分派及常数时间状态判断，不能承诺绝对零 CPU 指令；要求是关闭地图没有新增战斗 tick 工作、缓存维护和 tick 速率副作用。只有 Delta-V 使用 REF、没有任何地图启用桥接时，同样成立。
- 关闭时立即禁止新跨图选敌/发射/迁移，清理该图的跨图目标记录、路径请求和战斗使用计数。已登记在飞弹丸不继续保留后台跨图任务：需要解除跨图记录及异图目标引用，并在其当前真实地图按原版飞行/越界规则完成。此转换须对原版/CE 分别验证；不能只关 Enabled 却遗留跨图工作。
- 存读档、地图删除和换档重建逐图登记；不能将停止状态或过期 Map 引用遗留到下一局。CE 未安装零行为，安装时仍软检测并验证真实签名。

当前 `Begin` 会调用 `Pin` 强制两图全速，且 Pin 优先于停止策略。这不满足上述目标：**记录正在交战**与**请求全图全速**必须拆开，查询不能自动改 tick。RE 可以继续由快速区/威胁保护决定模拟强度；正在跨图飞行的对象若需要防止删除，应通过明确的删除前检查协调，不能隐式唤醒手动休眠图。现有 CombatLease API 是待调整实现，不是应照抄的设计标准。

验收必须同时覆盖 RE 开启图、RE 关闭图、Delta-V 图和两消费者同装；记录关闭前后的新增调度次数、坐标回调次数、跨图追踪对象数与 tick 注册变化。完整战斗迁移包括 CE，不再把 CE 通用桥接整体留给 RE。

### 6. 生成协调：独占状态、调用顺序和失败处理

要解决的具体问题是：RE、Delta-V 和原版地图生成都会使用进程共享的 `MapGenerator.mapBeingGenerated`、工作数据及随机生成上下文。一张图分多帧生成时，这些状态跨帧存在；第二个生成流程不能同时写入。REF 当前是一个**生成占用登记与同步完成回调服务**，不是完整地图生成器，也不是后台线程锁。[生成协调][ref-generation]

现有 API 的准确含义如下：

| API/字段 | 现有行为 | 不包含的行为 |
| --- | --- | --- |
| `Acquire(owner, drainSynchronously)` | 检查没有已登记的生成者且 mapBeingGenerated 为空，保存调用方标识与同步完成函数 | 不生成地图，不排队，不等待预览线程，也不自动冻结图 |
| `Bind(owner, map)` | 校验调用方，记录本次生成的 Map | 不设置原版 mapBeingGenerated，不登记 WorldObject |
| `IsBusy/IsOwnedBy/GeneratingMap` | 查询是否有占用、是否属于指定调用方、正在生成哪张图 | 不是所有第三方生成活动的总检测 |
| `Drain()` | 调用已登记的同步完成函数，返回后要求占用已解除，否则抛错 | 不是取消，也不是等若干帧；不能从后台线程随意调用 |
| `Release(owner)` | 校验调用方并清除 REF 的 owner、回调和 Map 引用 | 不清 MapGenerator 数据、GL 上下文、半成品地图或邻接关系 |
| 原版 `GenerateMap` Prefix | 在原版方法体前调用 Drain | 不覆盖绕过 GenerateMap 的所有第三方入口，也不阻止保存 |

RE 接入时的规定顺序：

1. **接受请求**：普通地块仍进入 QueuePreload。检查 REF 是否已有生成者、原版 mapBeingGenerated 是否非空、MapPreview 是否在飞；繁忙则保留/重新排队，不触碰生成静态状态。
2. **准备参考快照**：主线程在上述生成活动均空闲时，先完成 EnsureNeighborSnapshots。它可能使用临时图生成自然内容，必须在正式目标图登记占用之前完成；结束后重新检查是否能开始正式生成。
3. **登记正式任务**：调用 Acquire，传 RE 的固定调用方标识和“立即完成当前任务”函数；随后创建 Map、设置原版生成状态并 Bind。Acquire 成功后发生的任何异常都进入统一失败清理，即使 Map 尚未创建。
4. **分段执行**：RE 继续组装和执行自己的 GenStep，保留现有预算、GL Prepare/Cleanup、预览线程判定与生成中地图停止更新规则。REF 仅负责共享占用和公共停止执行，不复制 RE 的地表生成内容。
5. **同步生成插入**：若原版调用方要求 GenerateMap 当场返回 Map，REF 先调用已登记函数，在同一次调用中完成正在分段生成的图，然后原版生成才开始。RE 需新增该函数，直接推进已有任务至成功或失败收尾；不得等待未来 tick/帧，不得取出额外排队任务，不得递归调用 GenerateMap 来完成自己。可能产生一次同步卡顿，这是保留原版同步返回契约的代价。
6. **成功收尾**：沿现有 FinishGeneration 执行 FinalizeInit、组件/parent 通知及 RE 邻接、天气、传送点、封存记录处理。之后清理该任务的原版静态数据和生成标记，再解除 REF 占用；只在整段成功且状态清理后复放第三方 GenerateMap Postfix。不能复放 Prefix/transpiler/finalizer。
7. **失败收尾**：沿 CleanupFailedGeneration 撤销 RE 防重入记录，清理半成品 Map、已添加 parent、邻接反向记录、GL 上下文及该任务的原版静态数据；finally 保证解除 REF 占用。清理前核对调用方标识，不得清掉另一组件的生成任务。关键清理失败须明确报错并禁止盲目开始下一次生成，避免在污染状态上继续写入。
8. **保存与换档**：保存对话框和 SaveGame 都查询共享生成占用，生成未完成时拒绝保存且不显示假成功；RE 仍释放影子远行队的临时持有关系。换档清理旧任务引用，不序列化半成品任务；新游戏使用自己的 MapRuntime。

其中第 5 步必须有重入保护：当前任务正在执行 GenStep 或最终回调时，又从同一调用栈请求同步生成，不能递归进入自己的完成函数。需要依据真实调用点选择明确拒绝或允许在安全步骤边界处理，不能伪造成功返回或暗中重跑步骤。这是待补的行为约束，不是当前 REF 已具备的功能。

MapPreview 的预览走 `GenerateContentsIntoMap`，不会经过 REF 当前的 GenerateMap Prefix。RE 既有预览适配必须保留；后续检查应覆盖“预览已开始时 RE 请求生成”和“RE/Delta-V 已生成时预览请求启动”两个方向，不能只检查一个全局布尔值就宣称互斥完成。第三方线程协调保留在兼容层；REF 服务只允许在合法主线程调用边界进入。[RE 生成器][re-generation]、[保存检查][re-save-gates]、[MapPreview 参考][mp-source]

## 建议施工顺序与验收门槛

每步完成后只保留一个通用执行入口；过渡兼容外壳可以留，但不能继续维护两套等价底层。下面是施工建议，本轮未执行。

| 阶段 | 工作 | 进入下一阶段的条件 |
| --- | --- | --- |
| A：职责与版本基线 | 固定三仓库版本；落实本报告职责划分、调度选型及旧档转换方案 | 总体方案确认后开始施工；已确定的职责不重复征求确认 |
| B：依赖/登记/生成 | 双版本构建、独立 DLL、地图归属登记、生成独占登记、同步完成函数、保存检查、失败清理 | RE 单装消费者及 RE+Delta-V 同装无共享状态覆盖；POI 同步返回；失败正确处理 |
| C：渲染 | REF 补清色/提交政策；RE 自定义计划接入，统一捕获与投影 | 1.6/1.7 的水面、雾、动态物、接缝、删图/换图及零邻居无回归 |
| D：tick | REF 补部分摘表/相位/调度信息；RE governor、快速区、移动与天气回调接线 | 三桶、0% 快速区、休眠/恢复无双注册；移动速度与性能符合约定 |
| E：区域内容/存档 | REF 补选择/层过滤、占位处理与持久化；RE 1100 清理及自然恢复后调 prefab 重建；兼容旧记录 | 旧档/新档均能封存→保存→读档→重生成→再次封存；失败保留记录 |
| F：转移与战斗 | REF 提供完整原版/CE 桥接与逐图启停；RE 保留世界几何、许可来源及专属适配 | 既有功能保持覆盖；关闭图和 Delta-V 无额外战斗调度、坐标更新或 tick 速率变化 |
| G：收尾 | 移除重复底层、检查缓存/patch 所有权、同步权威文档 | 构建、离线绑定、游戏内日志及回归齐备后才认定迁移完成 |

关键回归应至少包含：

- 地表家园、普通地块、Site、友方/敌方派系基地、营地；口袋/轨道图不被 RE 登记或清屏。
- 两版本河流/海岸/GL 地貌，水深快速平移，夜间灯光、雾中目标、Mote、邻图删除、零邻居与反复切图。
- 0/25/50/75/100% tick、Rare/Long、多图错峰、快速区内外、出生/死亡/跨图后注册、手动休眠读档、天气域接任、威胁保护开关。
- 不规则 Home、孔洞、空屋顶、采空矿道、锚点在区域外的多格建筑、不可拆任务物、混合派系、物资和嵌套持有物；新旧格式往返与故障注入。
- GL/MapPreview 与两消费者生成竞争、同步 POI 插入、保存被拒无假成功消息、任一阶段失败后下一张图仍可生成。
- 原版普通/高抛弹、CE 移动/instant/制导/纯格目标，近战与 Follow，机械师征召，VF 整车与 PS 驾驶，Giddy-Up 随迁，VGE 世界炮击仍交还原生。
- 战斗关闭图、运行中关图、关闭时已有在飞弹丸、只启用 Delta-V 的存档；关闭后的跨图缓存/任务/回调归零，地图原有 tick 速率不变。
- 卸载恢复仍由 389 前自然快照回填，不从 prefab 或封存记录猜测原始自然地形。

## 本轮证据与验证边界

已完成：完整阅读三仓库 AGENTS/README；检查 RE 工作树；核对 REF 当前公共 API、Delta-V 接入源码，以及 RE 渲染、tick/休眠、封存、生成/保存、坐标与持久化入口；对功能全集按 README/专题入口进行覆盖分类。核心行为差异逐项以源码确认，并检查 REF 离线回归程序实际覆盖的范围。

外部机制只查统一参考目录。已核对 1.6 反编译 AssemblyVersion `1.6.9438.38202`，1.7 为 `1.7.9781.31297` / net10.0。它们是参考材料版本，不代表用户本次实际运行版本。MapPreview 参考确认其预览生成入口；本轮未重新审计所有外部 Mod 版本及全部兼容分支。

**没有运行编译、PatchAll 或游戏实测**：本轮没有实现变更，也没有为验证假接入而修改三仓库。REF README 的“175 项离线检查”是其既有记录，本轮没有重跑，不能当作 RE 迁移验收；其检查主要为数学/调度、绑定、生成门禁组合和命令捕获，不覆盖区域 Scribe 往返、GPU 水面和 RE 完整战斗行为。

本次修订另作纯数学检查：准确比例公式加入机会序号相位后，0–100 的所有整数百分比 × 100 种相位共 10,100 组，在连续 100 次机会中都恰好执行指定次数；六张连续相位图在 50% 下每次三张执行，避免六图同拍。该检查不运行游戏、不测 CPU 性能，也不验证 Pawn 寻路或注册表更新。

后续构建通过仍只是第一层证据。应核对游戏实际加载的 DLL、Mod 实例化行及 GetPatchedMethods 报告；REF 的 Waylith、tick 与生成 patch 也要纳入报告，旧 RE 的离线数字不能原封不动沿用为新基线。

**后续依据**：REF 提供公共执行能力；RE 保留快速区几何、场地清理与自然恢复、世界政策及专属兼容。调度以本报告的维护/性能选型为起点；完整原版/CE 战斗按地图显式启用，Delta-V 关闭。旧档转换仍须落实，水面差异原因尚未定位。总体方案确认后再施工，本次没有改动运行时代码。

[ref-contract]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/doc/地图模块.md
[ref-plan]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/doc/迁移备忘录.md
[dv-build]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/build/Build.ps1
[dv-project]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/Source/DeltaV.csproj:26
[dv-adapter]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/Source/Core/SharedMapBackend.cs
[dv-runtime]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/Source/Core/UniverseManager.cs:49
[dv-generation]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/Source/Generation/IncrementalMapGenerationService.cs:304
[dv-render]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus_Delta-V/Source/Rendering/SharedShellDrawBackend.cs
[re-project]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/RimExodus.csproj
[ref-build]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/build/RimWorldReferences.props
[re-render]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Rendering/SeamlessTileRenderer.cs:100
[ref-render-plan]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapRenderPlan.cs:43
[ref-render-backend]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapDrawing.cs:119
[ref-draw-tests]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/scripts/MapCoreRegression/DrawCaptureChecks.cs
[re-throttle]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Lifecycle/SeamlessTickThrottle.cs
[re-throttle-patches]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Lifecycle/Patches_TickThrottle.cs
[ref-ticks]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapTickRates.cs:30
[re-zone-restore]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Generation/GenStep_ZoneRestore.cs
[ref-prefabs]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapPrefabs.cs:33
[re-zone-record]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Lifecycle/ZoneMapRecord.cs:103
[ref-document]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapPrefabDocument.cs
[ref-thing-state]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapPrefabThingState.cs:64
[ref-combat]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/CrossMapCombat.cs:53
[ref-generation]: C:/Users/Laure/Documents/workspace/rimworld/RimExodusFramework/Source/MapGenerationCoordinator.cs:17
[re-generation]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Generation/IncrementalMapGenerator.cs:124
[re-save-gates]: C:/Users/Laure/Documents/workspace/rimworld/RimExodus/Source/Generation/Patches_IncrementalMapGen.cs
[mp-source]: C:/Users/Laure/Documents/workspace/rimworld/_references/MapPreview/Sources/MapPreview/MapPreviewGenerator.cs:355
[vanilla-subcamera]: C:/Users/Laure/Documents/workspace/rimworld/_references/RimWorldDecompiled/Verse/SubcameraDriver.cs
[waylith-render]: C:/Users/Laure/Documents/workspace/rimworld/_references/RimWorldDecompiled_1.7/RimWorldWaylith.Rendering.MapRenderFeature.decompiled.cs:49
