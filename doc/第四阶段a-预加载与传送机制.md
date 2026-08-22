# 第四阶段 a：邻居预加载与传送机制

状态：已完成核心实现，部分游戏内待验证。

> **2026-08 取代注**：本文若干节描述的方案已被后续重构取代，正文保留作历史——旧接缝带/offset 口径、"防反弹 pawn 级锁"、"跨图菜单接管"、传送检测 Postfix（详见各节内注）。

上级设计：[无缝世界地块探索](无缝世界地块探索.md)
上一阶段：[第三阶段：六边形裁切](第三阶段-六边形裁切.md)（六边形几何与 void 铺设）
下一阶段：[第四阶段：连续地形](第四阶段-连续地形.md)

## 阶段目标

第三阶段确立了"用可重叠正方形承载六边形网格"的多边形几何：每张地块地图在自己六边形外铺 void、边上铺传送点，相邻地块通过邻居偏移彼此叠合。但此时邻居地图只能由 Dev 命令手动生成，玩家走不到边就看不到下一张图；传送点也只会在两端地块同时存在时才生效。

本阶段解决两个问题，把"静态几何"变成"可游玩的连续体验"：

- **邻居预加载**：pawn 接近边界时按需生成对应邻居地图，让玩家走到哪里图就延伸到哪里，不再需要手动生成。
- **多跳传送点**：把传送点从"一对互绑的端点对象"重构为"预铺 + 缓存对端坐标"的模式，天然支持任意拓扑的多跳连通，并容纳 RimWorld 球面投影带来的几何扭曲。

这两件事是同一件事的两面：预加载决定"何时生成下一张图"，传送机制决定"生成后两端如何连接"。架构上所有地块都是对等的基础地图（继承 `MapParent`），没有"宿主/口袋"之分；锚点家园 A 只是用 `IsPlayerHome` 区分出的一张普通地图，与地块 B/C/D 在邻居表里地位完全相同。

## 邻居预加载

### 事件驱动：只响应玩家指令

最朴素的预加载思路是轮询——每 tick 检查每张图上每个 pawn 到边界的距离。这会引入动物级联：A 图边界附近的野生动物接近边界触发 B 生成，B 的动物又触发 C，最终把整片大陆全部加载。

最终方案是事件驱动，且严格只响应一种事件：玩家右键下达的强制移动指令（`JobDefOf.Goto` 且 `playerForced==true`）。Hook 点是 `Pawn_JobTracker.StartJob` 的 Prefix（`Patches_Job.cs`），在其中调用 `SeamlessBorderPreloader.CheckPawnGoto(pawn, targetCell)`。动物寻路、放牧、巡逻等自动行为产生的 Goto 不设 `playerForced`，完全不触发预加载。

### 边界带速查表

`CheckPawnGoto` 需要快速判断"目标格是否接近边界、对应哪个世界邻居"。逐格几何计算太慢，故每张无缝地图持有一份 `SeamlessBorderLookup`（MapComponent），开档/读档后延迟 1 tick 构建一次边界带速查表：

- 数据结构：`Dictionary<IntVec3, int>`，键是边界带格，值是该格最近多边形边对应的世界邻居 tile id。查询 O(1)，适合未来高频场景（如撤退袭击者批量检查）。
- 构建算法：`SeamlessPolygonGeometry.ComputeVoidBand`（多轮膨胀 BFS）——从 void 格出发，逐轮把 8-邻域非 void 格标记为到 void 的切比雪夫距离 k，跑 `bandWidth` 轮。带宽由 `RimExodusSettings.borderPreloadDistance` 控制（默认 15 格）。
- 同一份速查表还附带构建 `noBuildBandCells`（`HashSet<IntVec3>`，`SeamOverlap+1=3` 格宽的禁建带——**2026-08 起改由 `borderNoBuildDistance` 直接控制，默认 3**），供 `Patches_GenConstruct.CanPlaceBlueprintAt` 拦截玩家在接缝内侧建造——这是防止玩家用建筑改变寻路、把 pawn 困在 void 一侧的安全前提。

`ComputeVoidBand` 直接读 terrainGrid 判 void，与 void 铺设（`ApplyPolygonTerrain` 的格角检测）同源。这是经过多轮游戏内验证后确立的唯一"距边界 N 格"实现；早期用过的浮点多边形边距离（`ComputeEdgeBand`）会在凸多边形顶点附近振荡，与格角检测口径不一致，已全部删除。

### 防重入与异步队列

`CheckPawnGoto` 不在 `StartJob` 调用栈内同步生成地图——`MapGenerator.GenerateMap` 是重操作，同步执行会阻塞当前 tick 数百毫秒。改为登记到延迟队列 `SeamlessTilePreloader.QueuePreload(map, worldTile)`，由 `SeamlessTileManager.MapComponentTick` 每 tick 调 `ConsumeQueued` 消费。队列幂等（同 `(originMap, targetWorldTile)` 重复入队只保留一条），消费时快照后清空，期间产生的新请求下一 tick 再处理。

实际生成入口 `SeamlessTileManager.TryPreloadNeighbor(worldTile)` 有两层防护：

- **去重**：`SeamlessTileGraph.TryGetMapByWorldTile` 全局遍历 `Find.Maps`（含锚点 + 所有地块），发现该 worldTile 已有任意地图则跳过，并调 `EnsureNeighborRegistered` 补登记直接邻居关系（多跳间隙场景：C 与 A 隔着 B，A 已存在但与 C 无直接邻居关系）。
- **防重入**：`generatingTiles` HashSet 记录生成中的 worldTile，生成中拒绝同一 worldTile 的再次请求，try/finally 保证异常时也移除。

### 开档行为

`SeamlessTileManager.TrySetupOnStart` 在锚点地图开档时执行：

1. `PlaceEnterSpotsAllNeighbors(map, map.Tile)`：锚点 A 沿全部世界邻居边预铺单端传送点（对端坐标暂未缓存，`hasArrival=false`）。
2. 若 `RimExodusSettings.preloadAllNeighborsOnStart` 为 true（默认 false）：遍历所有世界邻居调 `TryPreloadNeighbor`，高配玩家开档即加载全部邻居。否则不生成，等 pawn 接近边界时事件驱动加载。

## 多跳传送点

### 从互绑到缓存对端坐标

早期实现（阶段 3/4a 初版）用"传送点双向精确互绑"：两端各自沿多边形边 Bresenham 划线铺 spot，`BindUnboundSpotsBetween` 按 `expectedCellB = cellA - offset` 精确坐标校验互绑 `CounterpartSpot`。但 RimWorld 球面投影必然扭曲——相邻 tile 各自用自己地块中心的切平面基投影多边形顶点，中心不同导致切平面基旋转，世界网格上的同一条共享边在两端局部坐标系里的 2D 方向旋转了（赤道到北极累积约 30°）。两端 Bresenham 格因此不一致，精确校验失败，绑定数为 0，传送点形同虚设。

重构后的机制（阶段 4b）容纳扭曲而非消除它：

- `CompSeamlessTileEnterSpot` 移除 `CounterpartSpot` 字段，改为 `targetWorldTile`（持久化，预铺时由世界邻居序号确定）+ `cachedArrivalCell`/`hasArrival`（不序列化，邻居关系建立后刷新）。
- `ComputeAndCacheArrival(Map ownerMap)`：用 `SeamlessTileGraph.TryGetNeighborLinkByWorldTile` 查得 offset，按 NeighborLink 契约 `cellNeighbor + offset = cellMy` 的逆算出 `cachedArrivalCell = parent.Position - info.offset`，置 `hasArrival=true`。邻居未加载时 `hasArrival=false`。
- `SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(Map)`：遍历该 map 所有 spot 调 `ComputeAndCacheArrival`。offset 在 `RegisterNeighborBidirectional` 时确定且不再变，故只需缓存一次。

这样传送触发与寻路查询都 O(1) 读缓存，不再现算 offset、不依赖对端 spot 是否存在。Map 解析推迟到实际传送时才做。未来扩展到跨图 A* 寻路时，节点扩展只需读 `hasArrival`/`cachedArrivalCell` 判断"有对端 + 对端坐标"。

### 预铺 + 延迟绑定

`SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, worldTile)` 在地图生成时沿**全部世界邻居边**（5 或 6 条）预铺单端 spot：

- 接缝带定义：到最近 void 格的切比雪夫距离 ∈ {1, 2} 的非 void 格，即紧贴 void 的 `SeamOverlap`=2 格宽环形带（最外圈 + 次外圈）。复用 `ComputeVoidBand`，与 void 铺设/接缝覆写/边界带同一实现、同一 void 边界口径。（**2026-08 已废**：现行铺点范围 = 传送圈 = 离散边圈 ∪ 带外圈，`BuildSeamBand` 纯几何、无任何可通行性过滤，权威定义见 [接缝带定义](接缝带定义.md)。）
- 每格按 `FindClosestEdgeIndex`（到 6 条多边形边的浮点距离取最小）确定 `targetWorldTile`（= 该边对应的世界邻居 tile）。
- spot 载体是 Ethereal/ThingWithComps（非 Building）：`destroyable=false`/`useHitPoints=false`/`selectable=false`/`drawerType=None`，不可攻击/占领/拆除，不进 edificeGrid，可与岩山墙/深水/任意地形共存。守门只跳过 void 格（岩石 Building 格照铺）。
- 预铺时 `hasArrival` 默认 false，待邻居加载、`RegisterNeighborBidirectional` 末尾刷新两端 spot 时算出。

多跳天然支持：新地块 C 加载后，`GenerateTileMap` 末尾的 `AutoConnectWorldNeighbors` 遍历 C 的世界邻居列表，对每个已加载（`TryGetMapByWorldTile` 命中）但未建立直接邻居关系的地图调 `EnsureNeighborRegistered`。世界网格上 B↔C 是邻居且 B 已加载时，加载 C 自动建立 B↔C 的邻居关系并刷新两端 spot 缓存，传送点即用。

## 异步加载

### 为何不能简单异步

`MapGenerator.GenerateMap` 内部顺序是 `AddMap`（让 map 进入 `Find.Maps`）→ `genSteps`（生成地形/岩石/植物/动物）→ `FinalizeInit`（初始化 region/pathing/powerNet）。这三步之间的时序依赖决定了异步方案的边界：

- **纯后台 Thread 不可行**：`List<Map>.Add` 与主线程每帧 foreach `Find.Maps`（Alert/ColonistBar 等）的 `_version` 校验冲突。
- **拆分式异步不可行**：把 AddMap 留在主线程、genSteps 丢到工作线程，则工作线程跑 genSteps 期间 map 已在 `Find.Maps` 但 `FinalizeInit` 未执行（region/pathing 未初始化），主线程每帧 tick 这个半成品 map 会刷出海量 "RegionAndRoomUpdater is disabled" 警告，寻路完全失效。
- **LongEventHandler 可行但中断操作**：`QueueLongEvent(doAsynchronously:true)` 是 RimWorld 原生安全机制（VMF 同类做法），整个生成在独立线程连续完成，但会 `ForcePause` + 显示进度画面几秒。

### 分帧增量生成（最终方案）

实验分支 `experiment/deferred-addmap-async` 实现并验证了真正的分帧方案：主线程每帧跑 1+ genStep，不暂停 tick、无进度画面、无红字 NRE。

`IncrementalMapGenerator`（MapComponent）把 genStep 链拆成 N 帧：

- **准备阶段（同步）**：`ConstructComponents` → `AddMap` → 组装 genStep 列表。用 `Rand.PushState/Seed/PopState` 包裹，seed = `World.info.Seed ⊕ mapParent.Tile.GetHashCode()`（与原版 MapGenerator 和 MapPreview 三者公式对齐）。
- **分帧阶段**：每帧 `TickGeneration` 跑 1+ genStep。每个 genStep 开始时 `Rand.Seed = baseSeed + GetSeedPart(index)` 独立重置，主帧 tick 改变 Rand 不影响下一个 genStep。最重的 Plants genStep（~8000ms 单帧）拆成每批 2000 cells、每帧跑到 `TimeBudgetMs=8ms` 预算耗尽，每批独立 `Rand.Seed` 保证跨帧可复现（**2026-08 修正**：批次已降为 64 格/批——2000 格/批 ≈170ms/帧远超 8ms 预算，见 AGENTS.md"邻居预加载与异步加载"节）。
- **FinishGeneration（单帧）**：`FinalizeInit`（region rebuild 不能拆）+ `onComplete` 回调。

配合 `Patches_IncrementalMapGen`：Prefix `Map.MapPreTick`/`MapPostTick`/`MapUpdate`，对 generating map 早退（不 tick、不渲染）。玩家在生成期间可继续操作其他地图。性能数据：void 裁切 genStep 从 5762ms 降到 30ms（`ApplyPolygonTerrain` 改为直接写 `terrainGrid.topGrid` 跳过 `SetTerrain` 副作用），Plants 从 8000ms 单帧卡顿降到每帧 <50ms。

关键约束：`Map` 是 `sealed`，不能子类化；`MapGenerator` 的 static 字段（`tmpGenSteps`/`data`/`mapBeingGenerated`/`RockNoises`）非 ThreadStatic，分帧期间不 `ClearWorkingData`/`RockNoises.Reset`，保持跨帧共享数据。

## 传送机制重构

### 接缝重叠带容纳投影扭曲

`SeamlessTileManager.SeamOverlap = 2`。`SeamlessNeighborRegistry.ComputeNeighborOffset` 算出 `offsetVec = 2 × (边中点 - 中心)` 后，沿其自身方向收缩 `SeamOverlap=2` 格再 round。效果是邻居多边形相对当前地图多叠 2 格——这 2 格在两端都落在各自多边形内（非 void、可站立），吸收投影偏移。

> **2026-08 已废**：镜像假设公式（只用源端几何）有系统性 ±1 格渲染错位，已被**连续边中点对齐**（`offset = round(midSource − midNew)`，两端各用自己多边形上共享边的连续边中点，浮点级精确重合、无收缩）取代；`SeamOverlap` 常量已废弃（后继 `RoadAnchorInset` 现值 0）。权威定义见 [接缝带定义](接缝带定义.md)。

所有 offset 消费者（渲染/归属/边界带/void/传送点）都读同一个 `NeighborLink.offset`，收缩一处即全局跟随。从赤道到北极旋转累积 30° 的过程中，2 格重叠带覆盖逐渐累积的偏移，不会漏出 void 缝隙。

### 事件驱动传送检测

早期用 `MapComponentTick` 每 tick 轮询所有传送点（~600 个），profiler 显示为核心性能瓶颈。最终改为事件驱动：

- `Patches_PawnPathFollower` `Pawn_PathFollower.TryEnterNextPathCell`（pawn 跨格瞬间），O(1) 查 `thingGrid` 该格是否有传送点。pawn 不动时零开销。（**2026-08 修正**：现用 **Prefix + `pather.nextCell`**（即将进入的格）而非本文的 Postfix——进入终点格时原方法体内部同步跑 `PatherArrived`→think tree 发新 job→`StartJob` 清掉传送许可，Postfix 永远晚于这条链，终点格传送永不触发；详见 AGENTS.md"传送机制"节。）
- `SeamlessMapTransferTrigger.TryTriggerTransfer(pawn, cell, map)`：踩 spot → 检查 `comp.hasArrival` → `TryGetMapByWorldTile` 解析对端 Map → 读 `comp.cachedArrivalCell`（O(1)）→ `SeamlessMapTransfer.TryTransferPawn`。
- `MapComponentTick` 只保留 `PurgeInvalidArrivalLocks`（arrivalLocks 为空时 O(1) 返回）。（**2026-08 已废**：arrivalLocks 随 pawn 级锁一并删除，见下节注。）

覆盖性：征召移动/撤退敌人/续程 Goto 走 pather 跨格触发；跨图落地（`GenSpawn.Spawn`）不走 pather 不触发，但此时 pawn 在 arrivalLocks 里防回弹，后续迈步走 pather 时锁已解除。

### 防反弹：pawn 级锁（2026-08 已废，见节末注）

传送点沿接缝满铺，pawn 被传到对端后续程沿接缝前进，踩上相邻接缝传送点会被立刻传回。旧锁是"特定 spot 对象"，pawn 离开到达的那个 spot 就解锁，防不住沿接缝走到相邻 spot。

最终方案是 pawn 级锁（`HashSet<Pawn>`）：pawn 跨图到达后进入锁状态，只要还站在本图任一接缝传送点上就保持锁，踩任何接缝 spot 都不触发传送；离开整条接缝带（不再站在任何本图传送点上）才解锁。`PurgeInvalidArrivalLocks` 每帧收集本图所有接缝 spot 位置到 `HashSet<IntVec3>`，遍历已锁 pawn 检查是否仍在接缝带上。

> **2026-08 已废**：pawn 级锁已被阶段5 **传送许可登记制**删除——无许可不传（闲逛/工作踩点无事是结构性结果；锁的"站在任一传送点保持"语义会卡死沿相邻边带内行走的撤离者），防回弹改由撤离链 VisitedTiles 承担。权威规格见 [边界行为表](边界行为表.md)。

### 行为区分：跨图传送 vs 远行队

同一个传送点，两种行为：

- **征召前往已加载邻居接缝** → 直接跨图传送（上述机制）。
- **远行队组建离开** → 踩传送点触发原生 `ExitMap` → 大地图远行队。

区分点在 `TryTriggerTransfer` 入口检查 pawn 当前 Job 的 `exitMapOnArrival`：远行队流程（`JobDriver_Goto` 设 `exitMapOnArrival=true`）放行原生 ExitMap，征召跨图走现有传送逻辑。配合 `Patches_ExitMapGrid` 把传送点格标为 exit cell，原生撤离/远征队流程在地块地图上也能工作。

## 边缘行为统一

六边形裁切后矩形地图边缘多为 void，所有"在地图边缘找格"的原版消费者（袭击入口、远行队出口、撤退 spot、行人穿越）都会失败。统一策略是**把传送点格集合作为六边形边缘带的权威定义**，所有边缘消费者改用它。

`SeamlessEdgeCells`（静态工具）是这一策略的几何根基，提供 `HasSeamEdge`/`GetSeamEdgeCells`/`PopulateSeamEdgeCells`/`RandomSeamEdgeCell`，带缓存（按 spot 数量失效）。复用传送点格而非重新跑 `ComputeVoidBand`，保证唯一口径。

覆盖矩阵：

- **`Patches_ExitMapGrid`**：Prefix `ExitMapGrid.Rebuild`，对有 RimExodus 传送点的地图跳过原版方形带铺设，自己初始化干净 BoolGrid 只标传送点格。Postfix `MapUsesExitGrid` getter，对有传送点的地图（含家园 A）强制 true，让浅绿色提示在锚点家园也显示。
- **`Patches_CellFinder`**：Prefix `CellFinder.TryFindRandomEdgeCellWith`（随机重载，袭击/远行队/撤退/行人穿越共用）+ Postfix `CellFinder.RandomEdgeCell`（无校验随机边缘格兜底）。把候选池从方形边缘格换成接缝带格，打乱后逐个过原版 validator。
- **`Patches_Reachability`**：Prefix `Reachability.CanReachMapEdge`，把"能否到达矩形地图边缘"改为"从起点能否到达任一接缝格"（逐个 `CanReach`，region 缓存）。
- **`Patches_RCellFinder`**：`TryFindBestExitSpot`/`TryFindRandomExitSpot`/`TryFindClosestEdgeCellTo` 的主体自己直接构造方形边缘格（不调 CellFinder 根原语），单独 Prefix 处理。

## 跨图菜单与选中

### 跨图菜单接管

`Patches_FloatMenuMakerMap` Prefix 全量接管 `FloatMenuMakerMap.GetOptions`。两端都在同一张地图时 `return true` 完全放行原版；否则：

- **清空 `ClickedThings`/`ClickedPawns`**：`FloatMenuContext` 的这两个 private 字段在构造时从 `Find.CurrentMap`(A) 收集（`GenUI.ThingsUnderMouse` 硬编码 CurrentMap），不从 `context.map`(B)。跨图点击 B 上的物品/敌人/pawn 时，`ClickedThings` 装的是 A 上同坐标格的错误对象，52 个 Thing/Pawn provider 对这些错误对象用 A 的 reachability 检查会产出错误选项甚至触发 `Reachability.CanReach` 跨图红字。用反射清空后 provider 从源头不产出。
- **`InjectCrossMapGotoOption`**：跨图场景下完全不让原版 DraftedMove 的可达性检查参与决定（它用 `pawn.Map`(A) 的 reachability 对 B 坐标做检查必然失败）。改用桥接可达性 `SeamlessCrossMapOrders.CanBridgeTo`（= 本图是否有 pawn 可到达的桥接传送点）：桥接可达 → 注入 `autoTakeable=true` 的跨图 GoHere（不弹菜单直接执行）；不可达 → 注入灰色"无法到达"。同时登记所有选中且要跨图的 pawn 到 `SeamlessSelectionTracker`。

语义收敛：当前阶段跨图右键任何位置 = "走到这里"（桥接过去）。同图场景完全不受影响。未来跨图射击/交互实现时，在清空 ClickedThings 后按需注入专门跨图选项。

> **2026-08 已废**：整方法接管 + 手写注入已被**点击重放 + 公共函数层**二次重构取代——点击拦截后在真邻图上重放拿原生选项与命令（52 个 provider 原生产出与禁用态），CanReach/选位/下令等公共函数跨图化。历史首版教训（手写注入连续漏原版语义、多层拦截混框架乱坐标）见 [第五阶段](第五阶段-跨图寻路与射击.md)与 AGENTS.md"跨图交互架构"节。

### 选中状态保持

跨图转移 `pawn.DeSpawn()` 触发 `Thing.DeSpawn` 无条件 `Find.Selector.Deselect`，`GenSpawn.Spawn` 不操作 Selector，选中列表变空。多 pawn 跨图是逐个的（每个 pawn 各自踩传送点，跨多个 tick），首个殖民者跨图时 `TryAutoFocusOnArrival` 切图触发 `MapInterface.Notify_SwitchedMap` → `selector.ClearSelection()`，把尚未跨图的其余 pawn 从选中列表清掉。

`SeamlessSelectionTracker`（静态选中保持集）跨越该时序：前端菜单在玩家下达跨图指令时 `Register` 所有选中且要跨图的 pawn；`TryTriggerTransfer` 转移后用 `Consume(pawn)`（在集合里就 re-Select 并移除）。无论切图清空几次，保持集始终记得"这批 pawn 应选中"，逐个跨图后各自 re-Select。`MapComponentTick`（锚点地图）周期调 `PurgeInvalid` 清理死亡/未跨图残留。

re-Select 必须在切图之后：此时 `CurrentMap==arrivalMap`，`SelectInternal` 不会二次硬跳镜头，`Patch_Selector_SelectInternal` 的无感偏移分支也不触发。

### 自动聚焦与无感相机

`SeamlessCameraFocus.TryAutoFocusOnArrival`：首个殖民者（`pawn.IsColonist`）跨图进入新地块（`MapParent_SeamlessTile` 且 `!autoFocused`）时触发。记录相机位置**和缩放**（`camSize = RootSize`）→ 切 `CurrentMap`（触发原生 `Notify_SwitchedMap` 同时恢复位置+缩放）→ 立即 `SetRootPosAndSize(camPos - offset, camSize)` 覆盖，画面不动 = 无感。`autoFocused` 标志持久化，每个地块仅触发一次。缩放必须一并恢复：`Notify_SwitchedMap` 会用新地图 `rememberedCameraPos` 同时恢复位置+缩放，只设位置会缩放不一致。

## 已解决的历史问题

- **三地图交点弹跳**：A↔B↔C 接缝交点附近 pawn 反复 transfer，根因是 void 铺设（`ContainsPoint` 叉积 + Bresenham 边格补丁）与传送点铺设（RoundToInt Bresenham 线）两套边界口径不一致，产生 void 孤岛切碎 region。统一为 `IsCellInPolygon` 格角检测（格中心或 4 角任一在凸多边形内 → 非 void）+ `ComputeVoidBand` 平移法接缝带（直接由 terrainGrid void 边界决定 spot），消除所有孤岛与口径错配。
- **C 加载后看不到间接邻居 A**：`AutoConnectWorldNeighbors` 在新地块加载时遍历其世界邻居列表，对每个已加载但未建立直接邻居关系的地图调 `EnsureNeighborRegistered`，使渲染/寻路即用。
- **多跳间隙重复生成**：`TryGetMapByWorldTile` 全局查询 + `EnsureNeighborRegistered` 补登记，避免 C 与已存在的 A（隔着 B）重复生成。
- **跨图寻路失败**：原版 `FloatMenuOptionProvider_DraftedMove.PawnCanGoto` 用 `pawn.Map`(A) 的 reachability 对 B 坐标做检查必然失败。`InjectCrossMapGotoOption` 改用桥接可达性注入跨图 Goto 选项，绕过原版 CanReach。
- **void 渲染残影红色**：`SeamlessTileRenderer` 的 CommandBuffer 开头不清色缓冲，移动相机时上一帧正常地形像素残留叠加压暗成红色。加 `ClearRenderTarget(true, true)` 清色缓冲解决。
- **传送点过传送点卡一下**：早期 30 tick 轮询间隔导致停顿，改为事件驱动（`Pawn_PathFollower.TryEnterNextPathCell` Postfix）零延迟。
- **锚点家园 A 的 void 铺设丢失**：阶段 4a 初版把开档逻辑改成默认不生成邻居，导致 `RefreshMapVoid` 从未对 A 调用。修复为统一 genStep 介入（XML PatchOperation 注入到 Base_Player/Base_Faction/Encounter），A/B 走完全相同的 genStep 链。

## 待办

- **offset 凑整对称性多跳精确验证**：`ComputeNeighborOffset` 各自从自己的多边形边中点算 offset，理论上 A→B 与 B→A 互为相反数，但 round 可能 ±1 误差。多跳（B→C→D）场景下误差是否累积、是否影响传送落点，需游戏内 verbose 日志观察 `cellA → cellB` 落点。
- **赤道→北极旋转累积验证**：投影旋转在赤道到北极累积约 30°，2 格重叠带是否在所有纬度都足够吸收偏移、无 void 缝隙，留待玩法验证阶段在真实长程旅行中确认。
- **SeamlessTileManager 进一步拆分**：已拆出 `SeamlessNeighborRegistry`（邻居表登记）和 `SeamlessEnterSpotPlacer`（传送点铺设），Manager 仍持生成流程。纯重构待功能稳定后继续。
- **偶现弹跳复现定位**：A↔C 反复 transfer 的偶现问题（若仍存在）待复现后定位根因。
