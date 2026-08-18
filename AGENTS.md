# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

为了验证此文件确实完整读入，你需要在对话开始给出这个文件的目录（这是个mm豆测试）。而且任何你觉得需要长久记忆的内容都应该记在这个里面，或者项目概述里描述的对应文档里，或者作为代码注释，不要依赖copilot或者你所在harness/agent自行提供的memory功能。

## 铁律（务必遵守）

- **未经用户明确指示，不要 git commit / git push**。代码改完只做编译验证（`dotnet build`），等用户测完并明确说"提交"才提交。即使编译通过、即使看起来没问题也不行——未测试的代码提交后若有问题，回退比不提交麻烦得多。用户曾因此差点要求回退。
- 查询优先用自带工具（Grep/Glob/Read），别用命令行，见下文"需要记住的事项"。
- **所有地图一视同仁**：安装 RimExodus 后，任何出现"不连续、不可连续旅行"的地图都是 mod 能力缺陷。RimExodus 地图本质上就是标准任务地图生成 + 我们的 genStep 裁切（void + 接缝带 + 传送点）将其包装成可连续通行的地块。任务地点、遭遇、派系基地、定居点等所有拥有合法 `MapParent` 的地图都走 RimExodus 完整链。代码中 `GetMapWorldTile(map) < 0` 的守卫是**纯防御性 fallback**（防 `map.info.parent == null` 或 `MapParent_SeamlessTile.worldTile` 未初始化等异常态），**不是"RimExodus 地图 vs 普通地图"的类型区分**——对所有合法地图它都不会触发。patch 新功能时不要加"非 RimExodus 地图放行原版"的分支，那与 mod 目的相违背。

## 项目概述

RimWorld Mod：实现"无缝世界地块探索"系统，使相邻世界地块的局部地图在视觉与操作上连续连接，Pawn 可直接从一张地图走入相邻地图，无需组成远行队。

文档索引：
- `doc/无缝世界地块探索.md` — 长期设计与五阶段路线图。
  - 其中描述了核心交互场景，我们的最终目的是保证核心交互场景，因此实现过程中避免临时patch
- `doc/接缝带定义.md` — **接缝带几何的权威定义**（连续边/离散边/3 圈接缝带/传送圈/void/offset 连续边中点对齐/SeamOverride 参考规则/地图生命周期数据链，状态定稿 2026-08）。
- `doc/第一阶段-VMF调研.md` — VMF 源码调研、可复用能力和架构结论（PocketMap 路线已废弃，保留作历史调研）。
- `doc/第二阶段-最小技术原型.md` — 矩形原型的实现进度、渲染验证。
- `doc/第三阶段-六边形裁切.md` — 六边形多边形裁切、void 铺设、接缝带几何归一。
- `doc/第四阶段-连续地形.md` — 连续地形调研（四项可行性分级）、接缝覆写卷积混合（E 节）、分帧增量生成。
- `doc/第四阶段a-预加载与传送机制.md` — 邻居预加载、多跳传送点、异步加载、传送机制重构、边缘 patch 统一。
- `doc/边界行为表.md` — 传送点/接缝带行为规范（主体 × 移动来源全枚举，状态定稿 2026-08；阶段5 边界行为的权威规格，由 `doc/gen_边界行为表.py` 生成）。
- `doc/地图滚动休眠.md` — **地图滚动生命周期的权威文档**（软休眠/唤醒/删除三态、距离策略、tick 分发查证、口径变化，已实现 2026-08）。
- `doc/地图生成步骤.md` — RimWorld 完整 genStep 执行顺序（含 RimExodus 注入点：230 海岸补铺 / 391 铺 void + 备份 snapshot + pathGrid 刷新 / 392 接缝覆写 + pathGrid 刷新 / Harmony patch 在 220 河流端点对齐、390 道路锚点对齐）。
- `doc/用可重叠正方形承载六边形网格的空间映射方案.md` — 六边形网格的空间映射理论。

## 依赖引用目录

> **重要**：本仓库已包含全部所需源码与配置，**无需去游戏安装目录（`SteamLibrary/steamapps/common/RimWorld/`）查找**。游戏的源码、物品/Def 配置、以及 VMF/VF 框架源码都在 `references/` 下，直接在此目录内检索即可。

- `references/RimWorldDecompiled/` — RimWorld 反编译源码（含原生 `MapParent`、`PocketMapParent` 等）。
- `references/VehicleMapFramework/` — VMF（Vehicle Map Framework），跨地图框架（调研参考，非依赖）。
- `references/Vehicle-Framework/` — VF（Vehicle Framework），车辆基础框架（调研参考，非依赖）。

## 需要记住的事项

- **查询优先用自带工具，别用命令行**：正常情况下使用 Grep/Glob/Read 等内置查询工具做检索与定位，不要用 Bash 跑 `Select-String`/`grep`/`findstr` 等命令。命令行转义（尤其 Windows + Git Bash + PowerShell 的引号/路径混用）容易出错，还会消耗用户的检视精力去判断命令是否安全。**例外**：Grep 工具在本仓库偶尔对明确存在的内容返回空结果（不报错，静默失败），此时可改用 PowerShell `Select-String -Path <绝对路径> -Pattern <正则>`（绝对路径用正斜杠）作为后备，这是已知的可靠替代。
- **编译**：仓库根目录运行 `just build`（默认任务也是 `build`），底层命令为 `dotnet build Source/RimExodus.csproj -c Debug`，输出 `1.6/Assemblies/RimExodus.dll`。
- 主设计文档的"当前阶段计划"定义推进顺序：VMF 调研 → 最小技术原型 → 六边形裁切 → 连续地形 → 跨地图寻路与射击（5 个阶段）。阶段 1-4 已完成，阶段 5 进行中（边界行为/传送许可制已落地，跨图寻路与射击未开始）。

## 当前架构核心事实（地块对等论）

以下是长期有效的架构事实，细节见各阶段文档。

### 基础地图对等架构（非口袋地图）
- **所有地块都是基础地图**：`MapParent_SeamlessTile : MapParent`（原生基类，非 `PocketMapParent`）。`mapParent.Tile = 真实 PlanetTile`，`map.TileInfo` 自动读 `Find.WorldGrid[Tile]`（含真实 biome/hillness/mutators/rivers），原生 Coast/River/Delta 等 TileMutator 自然生效。
- **无锚点特殊论（用户定夺 2026-08）**：家园图不特殊。天气按**群系连通域共享**（`SeamlessWeatherClusterManager`，GameComponent 全局注册机制）——同群系（PrimaryBiome）且世界图邻接连通的图共享一个天气源（域内最小 tileId 图创建的那套 sky/weather 三 manager 实例，字段替换共享；"宿主"纯是工程叫法——实例随谁创建/存档，非地图地位特殊，换宿主不换实例、无跳变，机理勘误见"地图生命周期"小节），跨群系边界切断传递（雨林→温带→另一片不连通雨林 = 三个域）；域结构不序列化，读档 FinalizeInit 重算重绑；图移除后 RebindAll 自动改绑——gravship 起飞销毁家园图无善后需求。家园图仅剩工程性差异：邻居表/条带快照存 SeamlessTileManager（原版 MapParent 挂不了我们字段）、全局清扫原挂其组件 tick（2026-08 已迁 SeamlessDormancyGovernor）。
- 无 `sourceMap`/`IsPocketMap`/`pocketMaps` 语义。所有地块对等，通过直接邻居表维护关系。
- 历史的 PocketMap 路线（`PocketMapParent`/`sourceMap` 宿主机制）已废弃，VMF 调研结论中相关描述仅作历史参考。

### 直接邻居表（对称性的根基）
- `MapParent_SeamlessTile.neighbors`：`List<NeighborLink>`，每条含 `{worldTile(int), neighbor(MapParent), offset(IntVec3)}`。`worldTile` 是主键，`offset` 隐式编码方向（无 direction/edgeAngle 字段）。
- **偏移契约**：`offset = 邻居本地坐标 → 本地块坐标`的平移。即 `neighborLocal + offset = myLocal`。所有消费者（渲染/归属/边界带/传送点）严格遵守。
- 锚点地图（家园 A，普通 MapParent）的邻居表存于 `SeamlessTileManager.neighbors`（MapComponent）；地块地图存于 `MapParent_SeamlessTile.neighbors`。`SeamlessTileGraph`（静态）提供统一查询入口（`GetAllNeighbors`/`TryGetNeighborLinkByWorldTile`/`AreNeighbors`/`IsAnchorMap`），屏蔽存储位置差异。
- `SeamlessNeighborRegistry`（静态）：邻居表登记工具（`RegisterNeighborBidirectional`/`SetNeighborOnMap`/`ComputeNeighborOffset`/`CleanupNeighborLinks`）。

### 地图生命周期（软休眠滚动模型，已实现 2026-08；权威文档 = `doc/地图滚动休眠.md`）
- **软休眠（用户定夺 2026-08，勿回退为"卸 Map"方案）**：休眠图的 **Map 对象保留在 `Find.Maps` 不卸载**，照常随档全量序列化（存档零额外机制，建筑/物品完好）。休眠只意味三件事：不 tick（`Patches_Dormancy` 跳过 MapPreTick/MapPostTick/MapUpdate + `Find.TickManager.RemoveAllFromMap` 摘全局 Thing tick——原版 tick 表按 TickerType 全局分桶不按地图分组，只跳 Map 方法停不掉 Thing）；不作为邻接地图显示（`SeamlessTileGraph` 邻接/已加载口径过滤休眠：渲染零邻居清色帧、传送点 hasArrival 失效、跨图菜单关闭、撤离链 tier0 视对端"未加载"——NPC 不唤醒跟丢/原生离场，用户定夺防级联唤醒）；无地图访问入口（殖民者栏的地图分组框——原版 CheckRecacheEntries 每图一组、无 pawn 也画空分组框可点击切图，**必须 `Patch_ColonistBar_CheckRecacheEntries` Postfix 过滤**（重编 group 须用 Entry 构造器重建：reorderAction 闭包捕获构造时 group）；Sleep/Wake 收尾 `MarkColonistsDirty`；休眠图殖民者尸体 entry 一并隐藏，接受；世界地图"查看地图"gizmo 保留为显式唤醒入口）。**唤醒 = 一切进图路径汇聚的 `Game.CurrentMap` setter Prefix 同步 Wake**（重注册 spawnedThings 的 tick，轻量无生成）；边界预加载带（CheckPawnGoto 休眠分支）与 governor 距离回落兜底。**休眠状态不序列化**：读档后全活跃（tick 注册由 FinalizeLoading 重 spawn 恢复），governor 首轮重新收敛。
- **距离策略（`SeamlessDormancyGovernor`，GameComponent 每 600 ticks）**：源 = 所有玩家阵营 pawn 所在图，世界网格 BFS 拓扑距离。有玩家 pawn → 永不休眠/删除（**仅玩家 pawn 保护**，相邻图因玩家踏入自动距离 0，天然防"跨图躲追击"）；距离 ≥2（sleepHops）休眠；距离 ≥3（deleteHops）且**地块图**→ 删除；锚点/家园图永不删除（WorldObject 是原版殖民地对象）；CurrentMap 无条件保活。设置：`dormancyEnabled`/`dormancySleepHops`(下限2)/`dormancyDeleteHops`。
- **删除 = `RemoveTileMap`**（销毁 Map + WorldObject = "从未出现过"，下次进入走生成链重建；开头 `SeamlessDormancyManager.Forget` 防集合持已 Dispose Map 引用）。Dev Remove All Tile Maps / Force Delete 同路径。图量 ≤ 以玩家为球心的 deleteHops 跳球（默认 ≤37 图），`Game.AddMap` 127 上限（sbyte）的实际压力由此解除。
- **生成守卫（勿删，会断点）**：`GenerateTileMap` 入口用 `worldObjects.MapParentAt` 查休眠 parent——查到则 Wake + 补登记并返回，**绝不 MakeWorldObject 新建**（`TryGetMapByWorldTile` 被休眠口径过滤后查不到休眠图，不拦会造同 tile 双 parent）。
- **`TryGetNeighborSeamStrip` 刻意不过滤休眠**（数据查询 ≠ 邻接交互，快照不因休眠失效）；WorldObject 回落路径保留（历史"硬卸载"预埋，将来若做仍零改动）。
- **天气域与休眠的交互（2026-08 机理勘误，勿回退为"宿主必须活跃"过滤）**：共享实例的推进**不依赖宿主图**——WeatherManager/WeatherDecider/SkyManager 均非 MapComponent，tick/update 由每张图的 MapPostTick/MapUpdate **走自己字段**调用，域内所有图的字段指向同一实例，任一活跃成员都在推进它（软休眠图被跳过 MapPostTick 不参与，实例由成员字段引用保持存活）。因此宿主休眠/换宿主**既不冻结也不跳变**（旧论断"宿主图 tick 是唯一驱动"是错误心智模型）。真 bug 是共享实例被多图重复推进（N 张活跃图 = N 倍速，curWeatherAge 每 tick +N），由 `Patches_WeatherCluster` 的实例级幂等守卫修复（同 tick/同帧第二次调用跳过，ConditionalWeakTable 弱键）。宿主的唯一实义：存档时谁存这套实例的状态副本 + 新成员 BindMap 读谁的字段。**SkyManager 宿主跟随（2026-08，勿删）**：SkyManagerUpdate 的视觉块门 `if (map == Find.CurrentMap)` 用构造时宿主图（readonly 字段）——聚焦域内非宿主图时（宿主 = 域内最小 tileId 图，跑图时玩家大多数时间不在它上面）任何成员调用都过不了门，整屏天空 tint（`MatBases.LightOverlay.color`/FogOfWar 色/相机饱和度/太阳阴影向量/_DayPercent）冻结在宿主上次被聚焦的值；`Patches_WeatherCluster` 在幂等守卫之前把宿主字段反射改写为 CurrentMap（仅当 `CurrentMap.skyManager == 本实例`，非共享/域外实例的宿主天然等于自己，零操作）。SkyManager 无 ExposeData，宿主字段纯运行时、无存档影响；curSky 计算随之改用当前图的 game conditions/AffectsSky 物体（比恒用宿主的更正确）。
- **incident 过滤**：`Storyteller.AllIncidentTargets` Postfix 剔除休眠图（防袭击打到冻结图）；`RandomPlayerHomeMap` 等消费面大的 getter 不 patch（误伤风险，观察项）。
- 历史修正：全局静态清扫（Selection/Grants/CrossMapOrders）已从"仅锚点图 MapComponentTick"迁至 `SeamlessDormancyGovernor.GameComponentTick`（家园无玩家 pawn 时可休眠，锚点 tick 不再可靠）。
- 历史保留：旧"硬休眠（卸 Map 留 WorldObject）"仅作预埋（SeamStripData 挂 WorldObject 即其遗产）；读档缺口（baseTerrainSnapshot 非序列化）已由 `SeamStripData` 解决。

### 六边形裁切与 void（阶段3；接缝带定义 2026-08 重构，权威定义 = `doc/接缝带定义.md`）
- 圈层（从核心区向外）：**核心区 → 带内圈 → 离散边圈 → 带外圈 → void**，三圈合称接缝带 B。
- **术语铁律**：不用"六边形外/内"指格集合（离散边格横跨连续边，内外归属歧义）；方向词用"接缝带内（核心侧）/接缝带外（void 侧）"；"格中心在多边形内/外"仅作标量判定。
- 连续边 = 浮点多边形顶点线段（内切圆模型，顶点 = center + 0.5S × 顶点方向，N=5/6 通用）。离散边 D = 格方块与连续边线段相交（slab 法，含角点接触，过四格公共角时 2×2 格都入 D）的格。B = Cheb(D,1)（约 3 圈厚，切比雪夫膨胀填对角缝隙无洞）。**传送圈 = D ∪ 带外圈**（外侧 2 圈铺传送点，带内圈无传送点）。**void = 接缝带外**（格中心在多边形外且 ∉B）——带外圈为实地形（不再是 void），这是传送落点 ±1 格偏差不进 void 的关键（pawn 由内向外必先踩离散边圈 spot，落点偏移落对侧带内/带外圈均实地形）。防孤岛等价：凸多边形下角在内的格必属 {中心在内}∪D，取代旧"中心或 4 角任一在内"角检测。
- `BuildSeamBand`（`SeamlessPolygonGeometry`，进程缓存键 (worldTile, mapSize)）是带几何唯一实现：void 铺设 / 传送圈铺设 / SeamOverride 混合范围与邻居参考判定 / 条带快照捕获共用。**清理全部归 391 的 ApplyPolygonTerrain，依次清 roof → rock（实体）→ terrain**（用户定夺 2026-08；三层快照（terrain/building/roof）先备份再清理，时序天然安全）。**order 200 patch 已删除（勿回退）**：曾提前清 void 格岩体/屋顶——void 格含外条带，提前清理使 391 才备份的原生数据丢失（岩壁整齐切断）。接缝带 B 上的岩石保留（自然地形，混合时按对端/地形增删；挡个别传送点是旧版一致的既有行为）。
- void 铺设：`SeamlessTerrainFill.BackupSnapshotAndApplyVoid`（备份 snapshot + 按 `IsVoidCell` 铺 void + **pathGrid 全量刷新**）。void 地形 `RimExodus_Void`：`passability=Impassable`、`dontRender=true`、`fertility=0`。
- **直写 topGrid 后必须即调 `map.pathing.RecalculateAllPerceivedPathCosts()`（2026-08 教训，勿删）**：`Walkable/Standable` 读 PathGrid 缓存数组（`GenGrid.Walkable → pathGrid.WalkableFast`）而非 terrainGrid，直写 topGrid 不触发重算、FinalizeInit 之前的一切消费者拿旧缓存。历史 bug：旧序（void 在 1400）下 Animals(1200) 先在将来 void 格上合法生成，撤离兜底的 `FindNearestWalkable` 又因 pathGrid 过期 + 径向首候选即自身格而原地空转 → 动物站 void。**2026-08 反转（勿回退到 1400/1410）**：void/混合提前到 391/392（Roads 后、Settlement 前），让 Settlement(400)+ 全部后续步骤在最终地形上工作（Plants 肥力门 / Animals+威胁步骤 Standable 门天然跳过 void 格）；快照消费面经查仅剩 void 侧外条带，400+ 地板写入被选址 patch 拦在带外（残余缺口观察项见 `doc/地图生成步骤.md` 反转节）。`Patches_TerrainGrid` 的 void 守卫（当前是 void 拦新 SetTerrain）随提前从生成期空操作变为生效——400+ 写不进 void 格。
- `ComputeVoidBand`（读 terrainGrid void 实况的多轮膨胀）现仅剩 `SeamlessBorderLookup` 预加载带(15)/禁建带(3) 消费，自动适应新 void 形状。

### 传送机制（阶段4b；接缝带定义 2026-08 重构）
- `CompSeamlessTileEnterSpot`：`targetWorldTile`（标记对端）+ `cachedArrivalCell`/`hasArrival`（缓存对端坐标，不序列化）。废弃了旧的互绑 `CounterpartSpot`。
- `SeamlessEnterSpotPlacer`（静态）：`PlaceEnterSpotsAllNeighbors`（铺**传送圈** = 离散边圈 ∪ 带外圈，BuildSeamBand 纯几何。**无任何可通行性过滤（用户定夺 2026-08，勿回退为 cell.Standable 或地形 passability 判定）**——spot 是纯逻辑连接设施：能不能走由地形运行时决定，不能走 pawn 自然绕路（与地图中央的深水/岩石挡路同构），地形变化（挖岩石/铺桥/水位）后 spot 已在、即时可用；带内圈无传送点）+ `RefreshEnterSpotArrivals`（邻居关系建立后按 offset 算缓存）。
- **offset = 连续边中点对齐**：`offset = round(midSource − midNew)`，两端各用自己多边形上共享边的连续边中点，渲染平移后两端连续边中点精确重合（浮点级），无收缩。**旧镜像假设公式（round(2·(midA−centerA) − SeamOverlap·unit)，只用源端几何）有系统性 ±1 格渲染错位，勿回退**——同一条世界共享边在两图投影的内切距不等（顶点方向角间隔差 1° ≈ 内切距差 ~1 格；2026-08 实测聚焦 C 时接缝带内偏一格，赤道正北侧同样复现）。取整残差 ≤1 格/分量由 3 圈接缝带吸收。公式天然对称（round(−x)=−round(x)），任一端算等值。旧常量 `SeamOverlap` 改名 `RoadAnchorInset`（值 2，仅剩道路锚点内偏语义）。
- ~~防反弹：pawn 级锁~~ **已删除（阶段5 改传送许可登记制，无许可不传 + 撤离链 VisitedTiles 防回弹；锁的"站在任一传送点保持"语义会卡死沿相邻边带内行走的撤离者）**。事件驱动传送检测保留但**必须用 Prefix + `pather.nextCell`（即将进入的格），不能用 Postfix**——历史教训（勿回退）：进入终点格时原方法体内部同步跑 `PatherArrived → job 完成 → think tree 发新 job（Wait_Combat 等）→ StartJob 钩子清掉传送许可`，Postfix 永远晚于这条链，终点格传送永不触发（实测 Bridge issued 后紧跟 "Grant cleared by Wait_Combat"）；Prefix 还抢先于撤离 job 到达 toil 的原生 TryExitMap（对端已加载时撤离者应传送而非原生离场）。传送后 Prefix 以 **Map 前后变化**判定跳过原方法体（教训勿回退：不能用 `pawn.Spawned` 判定——传送后 pawn 在新图上仍 Spawned，旧图 path/nextCell 状态跑方法体必然错乱，实测每次传送当 tick NRE 于 `TryEnterNextPathCell`）。`TryTransferPawn` **不校验 pawn 与 spot 的格距**（教训勿回退：原版 pather 的 `SetupMoveIntoNextCell` 节点去重双消费 + 路径重建可合法产生"nextCell 距 pawn ≥2 格"的调用，曼哈顿 ≤1 校验会误拒；不变量由调用方保证——触发器在 nextCell 上找到该 spot 即"正在进入"），坐标映射只依赖 spot。

### 邻居预加载与异步加载（阶段4a）
- 事件驱动：Hook `Pawn_JobTracker.StartJob`，仅 `playerForced==true` 的 Goto 触发边界检测（避免动物级联加载）。
- `SeamlessBorderLookup`（MapComponent）：边界带速查表 `Dictionary<IntVec3,int>`，O(1) 查询。
- `SeamlessBorderPreloader`/`SeamlessTilePreloader`：异步队列，下一 tick 消费。
- **分帧增量生成**（`IncrementalMapGenerator`）：主线程每帧跑 1+ genStep，不暂停 tick、无进度画面。generating map 被 `Patches_IncrementalMapGen` patch 跳过 MapPreTick/MapPostTick/MapUpdate。纯 Thread/拆分式异步/LongEventHandler 均不可行（Rand/MapGenerator static 非 ThreadStatic + AddMap/genSteps/FinalizeInit 时序依赖）。

### 接缝覆写卷积混合（阶段4 连续地形；3 圈接缝带重构 2026-08，权威定义 = `doc/接缝带定义.md`）
- 各地块正常用原版噪声独立生成，只在接缝带 B ∪ 过渡带 T 做 terrainDef 混合（**T 数据驱动**：邻居外条带 snapshot 投影覆盖处即过渡带，深至 `SeamTransitionWidth=7`，与外条带数据深度一致——固定窄过渡带会在覆盖区中部截断：A 侧数据还在、C 侧却不混合，2026-08 用户纠正）。**混合范围 = C 自己的带格显式枚举**（旧"从 A 全图 snapshot 枚举候选（cCell=aCell+offset 落 C 六边形内）"已废弃——A 方形角落格绕六边形顶点投影进 C 侧向楔形区的污染（实测数据点 A(249,249)→C(59,146)）结构性消失：只枚举 C 带格，每格反向找邻居参考）。
- **参考源 = 所有已生成邻居的接缝条带快照**（`SeamStripData`，genStep 392 末捕获，平行三层 terrain/building/roof）：B∪T 格存**最终实况**；接缝带外条带（B 向带外膨胀 `SeamTransitionWidth=7` 格）存**原生快照**（三层同源，391 备份）。地块图挂 `MapParent_SeamlessTile.seamStrip`（WorldObject、序列化），锚点挂 `SeamlessTileManager.anchorSeamStrip`——地图滚动加载卸载预埋 + 读档缺口修复（见"地图生命周期"小节）。
- **混合规则（用户定夺 2026-08，规则轴 = 本端圈层，对端只提供数据不参与规则判定，勿回退为"按对端圈层分规则"）**：**B_C（三圈）→ 三层字面照抄对端对应格**（a = c − offset，strip 有数据即抄地形/岩体/屋顶——岩体用对端 def 跨缝岩色连续，屋顶照抄对端原生岩顶 Thick/Thin 防裸顶岩壁，地形未变时岩体/屋顶同步仍执行，无任何地形例外；错位时 a 落在对端哪个圈层无所谓，照抄天然免疫错位——历史版本按 B_A/T_A/外条带/核心区四条判定需错位补偿补丁，已废弃）；**void_C → 不在枚举范围**（直接用自己的 void）；**T_C（过渡带，全深数据驱动）→ 卷积权重覆盖**（**源地图轴权重 w = dSq/(dSq+dOut)**：dOut = a 距源接缝带切比雪夫深度（贴缝≈0），dSq = a 到源方形边切比雪夫距离——接近源六边形权重高、到源方形边渐近 0，混合范围截止边界（源方形边）恰是权重归零处自然闭合；×乘性 dither 打散等值线。历史教训勿回退：曾按"本端距接缝带固定深度衰减 + 外条带限深 7 格"，数据边界处权重残值戛然而止，源方形边在 C 上投影成一条直线）。self+参考 3×3 分布 → 众数（地形走卷积混合）；**岩体/屋顶跟随主导参考（w 最大 cellRef）的 def**——与照抄区语义统一（离散层跟随参考；历史教训勿回退：地形驱动 spawn 会在参考无岩体处生成本端岩体，2026-08 用户实测不一致）。顶点楔形区（B_C 多邻居命中）卷积合成（各 w=1）。offset 现算与邻居表登记同公式恒等（genStep 期邻居表未登记——登记在 onComplete 晚于 genStep 链；运行时消费走邻居表）。
- **平行双 snapshot（terrain/building，2026-08 归一）**：接缝参考数据是两层平行快照——terrain 层（TerrainDef）+ building 层（岩石体 BuildingDef，null=无），两层同点位、同来源、同查询（SeamStripData 的 terrainLookup/buildingLookup）。来源分两段：B∪T 格 = 混合后最终实况（392 末捕获）；外条带格 = 原生快照（`baseTerrainSnapshot`/`baseBuildingSnapshot`，391 同点位备份——void 铺设已清掉外条带岩体，用实况会把世界连续岩壁误判无岩体，2026-08 实测 (98,233) 岩壁断裂）。完全一致区（w=1）两层照抄对端：岩体 spawn **用对端 def**（跨缝岩色连续；原版岩石地形阈值 elevation≥0.61 与 Building 阈值 >0.7 不同的中间带状态也正确继承），地形未变时岩体同步仍执行；过渡带（w<1）岩体跟本端混合后地形走（RockDefAt 本图 def）。
- **多邻居仲裁**（顶点楔形区）：各参考按 w 加权合成，Σw ≥ 1 时 self 权重 0——替代旧"串行后写者胜"。卷积：self 用 C 当前 topGrid 3×3（越界 clamp），neighbor 用快照稀疏字典 3×3（缺格跳过，水跳过）；N 路加权 → 众数（平局 defName 稳定决胜）。
- **保护判据：只有道路，无地形例外（用户定夺 2026-08，勿回退）**：SeamOverride 只做三件事——按卷积权重覆盖、随机化边缘（dither）、道路修复。①本格 `IsRoad`/`bridge` → 跳过；②`SeamlessRoadPaths`（`GenStep_Roads.Generate` Postfix 快照 static paths 到 MapComponent，防分帧增量生成下被其他地图清空）±3 格切比雪夫缓冲兜底 Gravel 等无 Road tag 路面。**水体/沼泽等一切地形照常参与混合与直接拷贝**——对端是水体本端就是水体（参考位置由中点对齐保证精确；不能走 pawn 自然绕路；河/海走廊位置由 river patch 权威对齐，SeamOverride 管逐格地形一致，互补）。历史的水体例外（本格 IsWater 跳过、卷积跳水）是旧 offset ±2 格系统误差的补丁，中点对齐后不成立（还曾因 `HasTag("Water")` 前缀匹配误伤 Marsh 造成接缝断裂）。判据用生成期权威数据而非局部模式识别。不做 A 侧结构继承。
- Perlin dither 只作用衰减区（乘性 `w×(1+n·amp)`，端点 0/1 不动）——完全一致区保持字面一致。旧 `seamOverrideWeightCap` 设置已删（w=1 直接拷贝 + 衰减公式自带上限，无消费者）。
- GenStep 顺序：`CoastalEdgeFill(230)` → `SeamlessTile(391, 备份snapshot+铺void+pathGrid刷新)` → `SeamOverride(392, 混合+捕获条带快照+pathGrid刷新)` → `Fog(1500, 据最终地形揭雾)`。2026-08 从 1400/1410 提前（理由与残余风险见"六边形裁切与 void"节及 `doc/地图生成步骤.md` 反转节）。
- 历史（勿回退参考）：旧 dSq/(dSq+dHex) 逐格局部归一权重（wCap=0.9）连同候选枚举方案一起废弃；更早的"全图 void 最大深度"/"候选集 maxDepth"全局归一被角落格污染的教训仍有效——新架构不再有全局统计量。连续 Perlin 全局对齐方案已废弃（elevation 被组合器包裹无法叶子层对齐），代码在 `continuous-perlin` 分支。

### 道路与河流接缝对齐（阶段4 连续地形）
- **道路**（`Patches_GenStepRoads.cs`，order 390 内部）：①Prefix `FindRoadExitCell`（private）——道路出口格强制对齐到接缝锚点（**统一工具 `ComputeSeamCellForEdge`**：边中点沿外法向的最外非 void 格 = 新 void 边界内侧第一格（带外圈），与带宽无关；道路 `RoadAnchorInset=0` 贴边——跨缝两侧路相接。历史：旧"边中点固定内偏 2 格"是旧抽象（void 边界=连续边）写死范围，接缝带定义变更后路出口距地图边缘 3-4 格跨缝断路，2026-08 修正）。边匹配用 `FindRoadLinkEdgeByHeading`：**只遍历有 road link 的邻居**（GetRoadDef != null）做世界 heading 匹配——原版 `CalculateNeededRoads` 对多条路的 angle 加向量平均偏置+随机抖动（让方形边缘出口散开；两条路夹角 60° 时偏置可达 60°，推过邻居间隔错到隔壁边），被污染的 angle 不能信任，过滤到 road 邻居后偏置再大也命中正确 link。可达性检查用原版同款两级放宽（NoPassClosedDoors → PassAllDestroyableThings，**不能用 NoPassClosedDoorsOrWater**——比原版严，锚点被河挡住时误放行原版导致出口落回方形边）。②Postfix `ApplyDistanceField`——接缝锚点 ≤6 格且 fromRoad≤1.5 的格强制补铺主路面（原版双重随机抽签在 DirtPath/DirtRoad 中线留 ~11-14% 断格：接缝最后 3 格断概率 ~36%；StoneRoad 唯一实心 mult=0）。跳过条件含 `terrain.bridge`（已铺桥格——桥在 foundationGrid，TerrainAt 遮蔽返回 Bridge，tags 只有 Floor 无 Water/Road tag，没有此判据会把桥格 SetTerrain 成路面：topGrid 从水变土、桥塌后露出河里的路）；place 为 FlagstoneSandstone 时映射 rockDef（对齐原版区域岩色）。③Postfix `Generate`——快照 static `paths`（A* 路径节点）到 `SeamlessRoadPaths` MapComponent（防分帧增量生成下被其他地图清空），供 SeamOverride 道路保护用。
- **河流**（`Patches_TileMutatorRiver.cs`，order 220 内部，两个 patch 配合）：
  - Prefix `GetMapEdgeNodes`（protected，元组返回）：河端点 = 接缝边中点（`FindEdgeByWorldHeading` 精确邻居映射）**沿外法向延伸到方形矩形边**（解析 slab 求交）。**必须延伸到方形边**：停在边中点会在河源头后方留下无水走廊——道路 A*(390) 先于 void(391) 跑，无岩走廊（旧序 order 200 提前清岩时期）第一级 `NoPassClosedDoorsOrWater`（一切水硬不可走）直接穿过、有岩走廊（现行时序，岩石 391 才清）第二级 `PassAllDestroyableThings` 同样挖岩绕行 → 路结构性绕经河源头（实测三叉河地图路绕北河口大转弯）。延伸后臂+方形边分割地图（原版语义），A* 只能涉水+铺桥。**宽度/水深由原版深度场自然延伸**（端点外延 → GenerateDepthMaps 的 GetTValue∈[0,1] 覆盖走廊，宽度噪声/水深分级全原版）——不自铺水带，无突变。
  - Postfix `GetDisplacedPoint`（protected virtual，返回 Vector2）：**压弯窗口**——原版弯曲偏移 = Perlin×幅度×bell(t)，端点外延后接缝处 tSeam≠0 → bell≠0 → 两侧独立 seed 接缝漂移 ±19-32 格。窗口把偏移乘 `clamp01((t-tSeamStart)/rise)×clamp01((tSeamEnd-t)/rise)`（tSeam = 各边中点在本线段的投影，边中点缓存 per worldTile）：走廊段偏移=0、接缝处有效 bell=0（对齐保持）、图内深处=原版行为。上下文（worldTile/mapSize）由 GetMapEdgeNodes Prefix 记 static（生成期单线程安全）。
  - 原生河 = 过随机中心(Rand 0.3-0.7×Size)的直线 + Perlin 弯曲（边缘漂移 ±11 格），两端只共享流向角——无原生对齐保证。Confluence 流入/流出支流各取元组一端连汇合点，与返回顺序 (heading反向端, heading正向端) 天然兼容。
  - **走廊水的下游影响**：391 被 void 覆盖（视觉河止于接缝带外）；snapshot 含走廊水 → SeamOverride 无地形例外，走廊水照常参与混合（对端走廊水 ↔ 本端走廊水，接缝两侧一致）。
- 共享几何：`SeamlessPolygonGeometry.FindEdgeByWorldHeading`（世界图方向角 → 邻居 → 边索引的**精确身份映射**：枚举世界邻居算 `GetHeadingFromTo(me, neighbor)` 匹配 angle，再用邻居索引直接映射边——"边 j ↔ GetTileNeighbors[j]"是传送点系统依赖的架构事实。**勿改回本地边中点角度近似**：六边形边方向离散 60° + 投影扭曲，会把"正南流向"锚到 SE/SW 边——用户实测南北河被画成东北-西南、下方地图连不上）+ `ComputeSeamCellForEdge`（**统一接缝锚点工具**：边 → 锚点格 = 边中点沿外法向最外非 void 格（新 void 边界内侧第一格）再内偏 offsetCells；基于 BuildSeamBand/IsVoidCell 唯一口径，与带宽无关）+ `DistanceToNearestEdge`（格 → 距最近边浮点距离）。
- `RefineEndcap` 的 ≤5 格端点检查**不会**移动六边形内侧锚点（它只是"是否执行 endcap 重路由"的门槛，锚点距方形边 ~19 格 → 恒跳过）——已排除的嫌疑，勿再查。

### 建筑选址避开六边形边（阶段4b）
- **问题**：原版建筑选址全按方形边界收缩（如 `GenStep_Settlement.CanScatterAt` 的 `BoundsRect(12)`），不感知六边形（边中点距方形边 ~17 格）→ 建筑锚点跨六边形边 → order 391 铺 void 时被切半。
- **三个 patch**（`Patches_BuildingPlacement.cs`，覆盖全部选址根原语）：①Prefix `GenStep_Scatterer.CanScatterAt`（protected virtual，字符串声明特性；子类 base 调用命中）——锚点六边形外或距边 <20 格（=最大建筑半宽 19+1，Settlement 38×38）拒绝，上层 1000 次重试消化（安全区约占方形 55-60%）。②Postfix `MapGenUtility.GetClearRects`——过滤四角+边中点距边 <10 格或六边形外的矩形（清晰矩形路：Outpost/AncientComplex/Gravcore/SurveySite/Harbor 等；Burst 内核不可 patch，此托管入口是唯一可行点）。③Postfix `CellFinder.RandomNotEdgeCell`——采样六边形外 → Invalid（FindPlayerStartSpot tightness 降级兜底 + 运行时 CompDeepScanner/incident）。
- 几何判定用 `BuildPolygonVertices` 纯几何（不读地形实况、与 void 铺设时机无关——2026-08 起 void(391) 虽先于选址铺好，几何判定与其等价），勿用 `HasSeamEdge`（运行时传送点入口，传送点在整条 genStep 链之后才铺、生成期恒 false）。
- 残余缺口（接受）：`GetOutpostRect` 贴附矩形、`GenerateLandingPadNearby`——锚点已安全后溢出概率低，观察（2026-08 void 提前后这些缺口处地板可盖住混合带结果，旧序相反，见 `doc/地图生成步骤.md` 反转节观察项）。

### void 渲染与邻居背景（`SeamlessTileRenderer`）
- void 地形 `dontRender=true` 实际画在 `MatBases.ShadowMask`（半透明不写不透明色），主相机只清深度——void 带的背景完全依赖 `SeamlessTileRenderer` 的 CommandBuffer 每帧 `ClearRenderTarget(true,true)` 全屏清色 + 邻居地形 mesh（`BeforeForwardOpaque`）。
- **邻居收集层 = Terrain / ThingsGeneral / LightingOverlay / FogOfWar 四层（2026-08 补光照遮罩，勿回退为三层）**：原版 1.6 地图亮度 = 纯 albedo 地形/物体 mesh × `SectionLayer_LightingOverlay` 顶点色（glow 颜色 + 岩顶黑暗 alpha，per-section 菱形渐变）× 共享材质 `MatBases.LightOverlay` 的昼夜色（每帧被当前图 SkyManager 染色）——不收集 LightingOverlay 则邻图背景无昼夜变暗、无灯光 glow、无岩顶黑暗（永远满亮度）。该层 mesh 顶点同为地图本地绝对坐标（y = AltitudeLayer.LightingOverlay ≈13.17，低于 FogOfWar），offset 矩阵直接平移；glow/roof 脏标记（Roofs|GroundGlow）由 `EnsureSectionsGenerated` 的 RegenerateAllLayers 连带重建消化（邻图灯光/屋顶变化即时反映）；可见性经 `layer.Visible` 自动尊重 `DebugViewSettings.drawLightingOverlay`。SunShadows/Gas 等其余层仍不收集（shadow/grid 依赖）。
- **光照天色分层（2026-08，勿回退为"邻图 overlay 用原材质"）**：LightOverlay shader 的**天色染色走材质 color alpha、glow/roof 走顶点色，两通道独立**（游戏内实验验证：材质 color=(1,1,1,0) 时灯晕在/岩顶黑在/户外昼夜变暗消失；原版先例 `SkyManager.cs:49` disableSkyLighting 群设置同款开关；注意顶点 (0,0,0,0) 的语义 = 按材质色染天色**而非透传**——`MapDrawLayer_ExteriorLightingOverlay` 专门用它给图外区域染昼夜色）。直接给邻图 overlay 用原材质会在 void 透明圈双重染天色（当前图 overlay 的 mesh 覆盖全图方形含 void 格 + 邻图 overlay 各染一次 = sky² 暗带）。分层：邻图 overlay 材质换 `(1,1,1,0)` 克隆（`NeighborGlowOnlyMaterial`，纯 glow/roof 数据层，全图照画）；天色由当前图 overlay（自己方形内，含 void 透明圈）+ 全零顶点 quad（`CollectSkyTintRects`，邻图方形 − 当前图方形的 L 形 ≤4 矩形，配共享材质）各管一块——任意像素天色恰好一层。重叠区两图 glow 叠加 = 跨缝照明（物理合理，接受）；void 格无 roof，岩顶不会双份。fog 层重叠双叠未处理（症状弱，观察项）。跨天气域邻居的天色跟当前图走（共享材质限制，近似，观察项）。
- **零邻居分支也必须清色**（曾经的历史 bug）：`cachedNeighbors.Count == 0` 时不能提前 return——否则清色被短路，void 带保留上一帧像素成红色拖影（新档锚点首生成/孤岛地块/Remove All Tile Maps/读档邻居未再生成四种场景）。零邻居时执行"清色-only"帧（不画邻居 mesh），同时 `commandBuffer.Clear()` 防邻居全部卸载后旧 buffer 逐帧重放已 Dispose 地图的 DrawMesh。

### 边缘行为统一（阶段4b）
- 浅绿色 exit grid：`Patches_ExitMapGrid` Prefix 重建只标传送圈传送点格（不标原版方形带）。
- 根原语 patch：`Patches_CellFinder`（TryFindRandomEdgeCellWith/RandomEdgeCell）、`Patches_Reachability`（CanReachMapEdge）、`Patches_RCellFinder`（自构方形边的方法）。覆盖袭击入口/远行队出口/撤退/行人穿越。
- `SeamlessEdgeCells`：接缝带格统一入口（复用传送点格集合 = 传送圈的 Standable 格）。

### 跨图菜单与选中（阶段4b）
- `Patches_FloatMenuMakerMap`：Prefix 接管跨图 FloatMenu 生成，`InjectCrossMapGotoOption` 按桥接可达性注入跨图 Goto 选项。
- **跨图选点 = 双侧代价场联合最优（2026-08，跨图 A* 仅单跳——用户定夺，`doc/第二阶段-最小技术原型.md` 的"不做多跳地图图"约束继续有效）**：`SeamlessCrossMapOrders.TryFindBestBridgeSpot` 对候选 spot 取 C1（本图 pawn→spot 代价场）+ C2（对图 dest→落点代价场）总和最小者（平手取 C1 小；无有限 total 回退最小 C1，保持旧"走到最近可达点后停下"降级）——单跳总代价在过缝点处可分解，等价两图缝零代价边后的全局最优过缝点。代价场 = `SeamlessPathCostField`（Dijkstra；口径对齐原版 PathFinderJob 基础层：PathGrid 缓存成本 + pawn 移动 tick 步进 + 对角切角，**刻意不复刻逐请求层**——关门/挡路 pawn/水/fence 只影响选点近似精度，段内执行仍原版 A*）；落点不可走的 spot 自然得 ∞ 被避开（旧版要到传送瞬间才发现）。`CanBridgeTo` 菜单热路径无目标格，仍用 `TryFindNearestReachableBridgeSpot` 轻量最近可达探测，不跑场。
- `SeamlessSelectionTracker`：跨切图的选中保持集（解决切图 ClearSelection 丢选中）。
- `SeamlessCameraFocus`：首个殖民者跨图自动聚焦 + 无感相机（切图后用 SetRootPosAndSize 同时恢复位置+缩放）。

### 跨图边界行为（阶段5，传送许可登记制，权威规格 = `doc/边界行为表.md`）
- **判定收拢原则**：传送资格不在踩点时判定——状态变更事件处登记许可（Grant）并绑定传送点，踩点热路径（`SeamlessMapTransferTrigger.TryTriggerTransfer`）只做一次字典查询 + 匹配分派。**无许可不传**（闲逛/工作/无 flag 逃跑一律无事是结构性结果）。
- `SeamlessTransferGrants`（静态登记表 `Dictionary<Pawn,Grant>`）：Kind = Bridge（玩家跨图 goto）/ Evacuation（NPC 撤离链）/ Pursue（跨图追击）/ Follow（跟随跨图）。`TransitTag`（"RimExodus.Transit"）写在我们下发 Goto 的 `job.dutyTag` 上（干净可序列化字段，无引擎消费者）——StartJob 钩子据此识别"许可驱动 job"，不当 job 替换清除许可。
- 三个登记点：① `Patches_Job` StartJob Prefix——`exitMapOnArrival && !playerForced`（撤离 duty/囚犯越狱/野性恐慌/释放访客）→ Evacuation 许可；任何非 TransitTag job 启动 → 旧许可清除（清理先于登记，天然处理 job 重发/队列复用）。playerForced 精确区分玩家征召 goto（原版 5 个 flag 赋值点中只有 DraftedMove 走 TryTakeOrderedJob 设 true）→ 玩家撤离不登记，踩传送点走原生撤离/组队。② `SeamlessCrossMapOrders.TryBridgeJob`——Bridge 许可（绑定 exitSpot + 携带最终目的地，吸收已删除的 SeamlessCrossMapPendingDestinations）+ TransitTag Goto。③ `NotifyPawnTransferred`（传送完成事件）——撤离链续程 + 追击者/跟随者扫描（目标/主人刚跨图的瞬间事件标记，跨图意图由事件本身保证，无需踩点时判意图）。
- **撤离链方向性（防回弹/横跳）**：`TryFindEvacuationExit` 三级候选——未访问边且对端未加载（到达即原生离场，链终止最快）> 未访问边（链继续）> 无过滤（只剩已访问边，ForceExit 踩点直接 `pawn.ExitMap`）。VisitedTiles 跨 hop 传递（含来向 tile）。**续程 job 不带 exitMapOnArrival**（传送落点本身常是出口格，flag job 的 pre-tick IsExitCell 检查会在落地瞬间原生离场，破坏"继续跑"）；Evacuation 首跳许可不绑具体点（首个踩到的传送点：对端已加载→传送续链，未生成→交还原版 JobDriver 原生撤离）。
- 追击扫描：出发地图上 `AttackMelee` 目标=刚跨图者 且 NPC 战斗体（`SeamlessBoundaryRules.IsNpcCombatant`：人形/机械族非玩家阵营，覆盖敌人+盟友）→ 借同一传送点传送追击。走位 Goto 无跨图意图判据不激活、AttackStatic 恒站立不踩格——均不扫。狂猎动物不属战斗体（行为表无此行）。
- 跟随扫描：出发地图上 `Follow/FollowClose` 目标=刚跨图者 → 跟随传送（驯养动物 + NPC 随从统一覆盖，防商队过缝解体）。
- **游荡兜底**：Pursue/Follow 落地 NPC（传送时 lord 已被 Notify_PawnLost 剥离、无 duty）若无目标会永久滞留——600 ticks 宽限内重新接战/有 lord 则解除，超时转入撤离链跑出世界（排除来向 tile）。
- 预加载排除传送点格（`SeamlessBorderPreloader.CheckPawnGoto` + `SeamlessEdgeCells.IsSeamEdgeCell` thingGrid O(1)）：征召 goto 传送点格 = 撤离意图，不触发对端生成。
- 跨图指令主体（`SeamlessBoundaryRules.IsCrossMapOrderable`）：殖民者 / 殖民地机械族（`IsColonyMech`，保留原版 `!IsColonyMech` 不自行离图例外）/ 玩家阵营驯养动物。
- 静态清扫（Grants 失效/超时（6000 ticks 兜底，正常生命周期由 StartJob 替换清理）、游荡宽限检查、`SeamlessCrossMapOrders.PurgeInvalid`（pendingMenuTargets 死亡/转世界 pawn 泄漏））**已迁至 `SeamlessDormancyGovernor.GameComponentTick`**（2026-08 软休眠：原挂"仅锚点图 tick"，家园无玩家 pawn 时可休眠不再可靠）。

## 当前待办（2026-08 接缝带重构后盘点）

**临时（本轮重构的观察项/回归项，游戏内验证进行中）**：
- **genStep 提前回归（2026-08 新增，void/混合 1400/1410→391/392）**：动物不落 void（本轮修复主目标）、动物密度正常、接缝混合/岩色/屋顶跨缝一致、Settlement/site 布局正常、Fog(1500) 揭雾、manhunter/mech 任务图 pawn 不落 void、生成耗时无感（两次全图 pathGrid 重算）。
- **genStep 提前新增观察项（2026-08）**：Harbor 桥跨缝行为（外条带快照不再含 400-1400 地形写入）、污染地块接缝两侧污染变体一致性、`completelyIgnoreFertility` 植物是否漏进 void（肥力门对它们无效，新序无事后清理）、贴附矩形/降落平台残余缺口处"地板盖混合"（旧序相反）、MapPreview 预览链 order 变化。
- 重开档全面回归：渲染对齐（聚焦 C 看 A 背景无系统内偏）、跨缝三层一致（地形/岩体/屋顶，含岩色与岩顶）、传送往返落点、读档后生成新图接缝混合仍工作、道路（锚点已改贴 void 边界）、河流接缝、撤离链/追击/跟随。
- 植物层未纳入三层快照——接缝带树木/植被的跨缝一致性未处理（观察：跨缝树缺失/多余是否显眼，必要时作第四层加入 seamLayers）。
- 顶点楔形区（B_C 多邻居命中）卷积合成（各 w=1）的视觉效果观察。
- 条带快照存档增量观察：外条带全深后每图约 2-3 万格 ×4 列（terrain/building/roof/depth），存档增加约几百 KB/图——若超标做 def 索引压缩。
- MutatorFinal(1600) 的 GeneratePostFog SetTerrain 覆写接缝（AncientUplink/InsectMegahive 任务地块）——已知瑕疵未处理。
- Fog(1500) 对带外圈（新实地形）的揭雾验证；远行队进入出生点（Patches_CaravanEnterMap）与带外圈传送圈兼容回归。
- 软休眠回归（2026-08 新增，清单见 `doc/地图滚动休眠.md` 第五节）：跑图 3 跳后休眠/走近唤醒、休眠图邻接显示/访问入口关闭（含顶部殖民者栏分组框）、休眠期间存档读档收敛、追击者不跨休眠缝、家园全员远行冻结/回家恢复、Dev Force Delete 后重走生成链、天气域幂等守卫生效（同域多活跃图天气演化不再 N 倍速）、远行队进入已删除 tile 的 parent 类型缺口（原版 GetOrGenerateMap 用原版 def 建 parent——删除策略放大出现率，必要时 patch）。

**路线图（主文档"当前阶段计划"）**：
- 阶段5 剩余：跨地图寻路与射击（跨图 LOS/目标搜索/射击线/弹道）——未开始（边界行为/传送许可制已落地）。
- ~~地图滚动加载卸载（休眠）~~ **已实现（2026-08 软休眠，`doc/地图滚动休眠.md`）**：休眠/唤醒/删除三态 + 距离策略自动调度 + Dev 工具落地；游戏内回归进行中（测试清单见该文档第五节）。
- `Game.AddMap` 127 图上限（sbyte）——实际压力已由删除策略解除（图量 ≤ deleteHops 跳球）；上限本身仍在（软休眠不卸 Map）。
- ~~锚点图销毁善后~~ **已消解（2026-08 天气域机制）**：天气改为群系连通域共享（无锚点依赖，宿主迁移由 RebindAll 自动处理）；gravship 销毁家园图的场景由 Manager.MapRemoved → RebindAll 覆盖，待游戏内验证。

## 存档兼容性说明

**mod 未发布，当前一切测试在新建存档中进行，无需考虑旧存档兼容。** 几何/传送点/字段变更后重开档即可，不做读档迁移。
