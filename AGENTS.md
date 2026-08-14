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
- `doc/第一阶段-VMF调研.md` — VMF 源码调研、可复用能力和架构结论（PocketMap 路线已废弃，保留作历史调研）。
- `doc/第二阶段-最小技术原型.md` — 矩形原型的实现进度、渲染验证。
- `doc/第三阶段-六边形裁切.md` — 六边形多边形裁切、void 铺设、接缝带几何归一。
- `doc/第四阶段-连续地形.md` — 连续地形调研（四项可行性分级）、接缝覆写卷积混合（E 节）、分帧增量生成。
- `doc/第四阶段a-预加载与传送机制.md` — 邻居预加载、多跳传送点、异步加载、传送机制重构、边缘 patch 统一。
- `doc/地图生成步骤.md` — RimWorld 完整 genStep 执行顺序（含 RimExodus 注入点：230 海岸补铺 / 1400 铺 void + 备份 snapshot / 1410 接缝覆写 / Harmony patch 在 200 预清岩石、390 道路锚点对齐）。
- `doc/用可重叠正方形承载六边形网格的空间映射方案.md` — 六边形网格的空间映射理论。

## 依赖引用目录

> **重要**：本仓库已包含全部所需源码与配置，**无需去游戏安装目录（`SteamLibrary/steamapps/common/RimWorld/`）查找**。游戏的源码、物品/Def 配置、以及 VMF/VF 框架源码都在 `references/` 下，直接在此目录内检索即可。

- `references/RimWorldDecompiled/` — RimWorld 反编译源码（含原生 `MapParent`、`PocketMapParent` 等）。
- `references/VehicleMapFramework/` — VMF（Vehicle Map Framework），跨地图框架（调研参考，非依赖）。
- `references/Vehicle-Framework/` — VF（Vehicle Framework），车辆基础框架（调研参考，非依赖）。

## 需要记住的事项

- **查询优先用自带工具，别用命令行**：正常情况下使用 Grep/Glob/Read 等内置查询工具做检索与定位，不要用 Bash 跑 `Select-String`/`grep`/`findstr` 等命令。命令行转义（尤其 Windows + Git Bash + PowerShell 的引号/路径混用）容易出错，还会消耗用户的检视精力去判断命令是否安全。**例外**：Grep 工具在本仓库偶尔对明确存在的内容返回空结果（不报错，静默失败），此时可改用 PowerShell `Select-String -Path <绝对路径> -Pattern <正则>`（绝对路径用正斜杠）作为后备，这是已知的可靠替代。
- **编译**：仓库根目录运行 `just build`（默认任务也是 `build`），底层命令为 `dotnet build Source/RimExodus.csproj -c Debug`，输出 `1.6/Assemblies/RimExodus.dll`。
- 主设计文档的"当前阶段计划"定义推进顺序：VMF 调研 → 最小技术原型 → 六边形裁切 → 连续地形 → 跨地图寻路与射击（5 个阶段）。阶段 1-3 已完成，阶段 4 进行中（接缝覆写已完成），阶段 5 未开始。

## 当前架构核心事实（地块对等论）

以下是长期有效的架构事实，细节见各阶段文档。

### 基础地图对等架构（非口袋地图）
- **所有地块都是基础地图**：`MapParent_SeamlessTile : MapParent`（原生基类，非 `PocketMapParent`）。`mapParent.Tile = 真实 PlanetTile`，`map.TileInfo` 自动读 `Find.WorldGrid[Tile]`（含真实 biome/hillness/mutators/rivers），原生 Coast/River/Delta 等 TileMutator 自然生效。
- 无 `sourceMap`/`IsPocketMap`/`pocketMaps` 语义。所有地块对等，通过直接邻居表维护关系。
- 历史的 PocketMap 路线（`PocketMapParent`/`sourceMap` 宿主机制）已废弃，VMF 调研结论中相关描述仅作历史参考。

### 直接邻居表（对称性的根基）
- `MapParent_SeamlessTile.neighbors`：`List<NeighborLink>`，每条含 `{worldTile(int), neighbor(MapParent), offset(IntVec3)}`。`worldTile` 是主键，`offset` 隐式编码方向（无 direction/edgeAngle 字段）。
- **偏移契约**：`offset = 邻居本地坐标 → 本地块坐标`的平移。即 `neighborLocal + offset = myLocal`。所有消费者（渲染/归属/边界带/传送点）严格遵守。
- 锚点地图（家园 A，普通 MapParent）的邻居表存于 `SeamlessTileManager.neighbors`（MapComponent）；地块地图存于 `MapParent_SeamlessTile.neighbors`。`SeamlessTileGraph`（静态）提供统一查询入口（`GetAllNeighbors`/`TryGetNeighborLinkByWorldTile`/`AreNeighbors`/`IsAnchorMap`），屏蔽存储位置差异。
- `SeamlessNeighborRegistry`（静态）：邻居表登记工具（`RegisterNeighborBidirectional`/`SetNeighborOnMap`/`ComputeNeighborOffset`/`CleanupNeighborLinks`）。

### 六边形裁切与 void（阶段3）
- 内切圆顶点模型：多边形顶点 = 地图中心 + 0.5S × 顶点方向单位向量（方向来自世界地块真实顶点投影）。支持 N=5/6。
- 三态几何：六边形内（非 void 无传送点）/ 六边形的边（非 void 有传送点）/ 六边形外（void）。**只看自己六边形**。
- void 铺设：`SeamlessTerrainFill.ApplyPolygonTerrain` 用格角检测（`IsCellInPolygon`：格中心或 4 角任一在多边形内 → 非 void），消除孤岛。归一入口 `BackupSnapshotAndApplyVoid`（备份 snapshot + 调 ApplyPolygonTerrain）。
- void 地形 `RimExodus_Void`：`passability=Impassable`、`dontRender=true`、`fertility=0`、不设 `forcePassableByFlyingPawns`。透明不可通行，消除重叠带视觉冲突。
- 接缝带几何归一：`ComputeVoidBand`（`SeamlessPolygonGeometry`，"距 void 边界 N 格"的唯一实现，多轮膨胀，void 边界同源）。传送点带/接缝覆写混合带/边界带三者共用此实现。

### 传送机制（阶段4b，容纳投影扭曲）
- `CompSeamlessTileEnterSpot`：`targetWorldTile`（标记对端）+ `cachedArrivalCell`/`hasArrival`（缓存对端坐标，不序列化）。废弃了旧的互绑 `CounterpartSpot`。
- `SeamlessEnterSpotPlacer`（静态）：`PlaceEnterSpotsAllNeighbors`（复用 ComputeVoidBand 铺接缝带传送点）+ `RefreshEnterSpotArrivals`（邻居关系建立后按 offset 算缓存）。
- `SeamOverlap=2` 接缝重叠带：`ComputeNeighborOffset` 算 offset 后沿其方向收缩 SeamOverlap 格，使邻居多边形相对当前地图多叠 2 格，吸收投影扭曲（相邻 tile 切平面基旋转，赤道→北极累积约 30°）。
- 防反弹：pawn 级锁（`HashSet<Pawn>`），离开整条接缝带才解锁。事件驱动传送检测（`Patches_PawnPathFollower` Postfix `TryEnterNextPathCell`，O(1) 查 thingGrid）。

### 邻居预加载与异步加载（阶段4a）
- 事件驱动：Hook `Pawn_JobTracker.StartJob`，仅 `playerForced==true` 的 Goto 触发边界检测（避免动物级联加载）。
- `SeamlessBorderLookup`（MapComponent）：边界带速查表 `Dictionary<IntVec3,int>`，O(1) 查询。
- `SeamlessBorderPreloader`/`SeamlessTilePreloader`：异步队列，下一 tick 消费。
- **分帧增量生成**（`IncrementalMapGenerator`）：主线程每帧跑 1+ genStep，不暂停 tick、无进度画面。generating map 被 `Patches_IncrementalMapGen` patch 跳过 MapPreTick/MapPostTick/MapUpdate。纯 Thread/拆分式异步/LongEventHandler 均不可行（Rand/MapGenerator static 非 ThreadStatic + AddMap/genSteps/FinalizeInit 时序依赖）。

### 接缝覆写卷积混合（阶段4 连续地形）
- 各地块正常用原版噪声独立生成，只在接缝带做 terrainDef 过渡混合。不追求全局连续。
- `SeamlessSeamOverride.ApplyOneWay`：3×3 卷积加权取众数 + 单向覆写（只改新生成 tile C，不改已生成邻居 A）。
- **混合带由 A snapshot 决定（非 C 的 bandWidth）**：遍历 A 被 void 裁掉的条带格（`!IsCellInPolygon(AVerts)`，即 A 六边形外），映射到 C 的 `cCell = aCell + offset` 做卷积。混合带宽度 = A 被裁掉的实际深度，无固定 bandWidth 配置。
- 权重 w：`wCap × (1 - clamp01(aCell距A六边形边距离/maxDepth))` + 空间 Perlin 噪声 dither。**maxDepth 只统计实际参与混合的候选格**（投影到 C 可见区域内）——不能用全图 void 最大深度，方形角落距六边形边可达 40+ 格会污染归一化，把接缝处 w 整体抬高（曾导致 A 全岩石时整条混合带被写成岩石、边界为直线的硬边 bug）。
- **窄结构保护（权威元数据判据）**：3×3 众数卷积天然抹掉 ≤2 格宽线性结构（窗口内少数派）。①道路：`SeamlessRoadPaths`（`GenStep_Roads.Generate` Postfix 快照 static paths 到 MapComponent，防分帧增量生成下被其他地图清空）±2 格缓冲内跳过混合；②水格：C 当前地形 `IsWater` 跳过（水的连续由 CoastalEdgeFill/river mutator 两端独立保证）。判据用生成期权威数据而非局部模式识别（细线检测无法区分 2 格宽土径和常规地形边缘条带——局部模式同构）。不做 A 侧结构继承。
- 卷积采样源：self 用 C 当前 topGrid（裁切后真实状态），neighbor 用 A snapshot（裁切前完整地形，含 A 被裁掉部分的真实地形）。
- GenStep 顺序：`CoastalEdgeFill(230)` → `SeamlessTile(1400, 备份snapshot+铺void)` → `SeamOverride(1410, 卷积覆写)` → `Fog(1500, 据最终地形揭雾)`。
- 连续 Perlin 全局对齐方案已废弃（elevation 被组合器包裹无法叶子层对齐），代码在 `continuous-perlin` 分支。

### 道路与河流接缝对齐（阶段4 连续地形）
- **道路**（`Patches_GenStepRoads.cs`，order 390 内部）：①Prefix `FindRoadExitCell`（private）——道路出口格强制对齐到接缝锚点（边中点向内 SeamOverlap 格），可达性检查用原版同款两级放宽（NoPassClosedDoors → PassAllDestroyableThings，**不能用 NoPassClosedDoorsOrWater**——比原版严，锚点被河挡住时误放行原版导致出口落回方形边）。②Postfix `ApplyDistanceField`——接缝锚点 ≤6 格且 fromRoad≤1.5 的格强制补铺主路面（原版双重随机抽签在 DirtPath/DirtRoad 中线留 ~11-14% 断格：接缝最后 3 格断概率 ~36%；StoneRoad 唯一实心 mult=0）。跳过条件含 `terrain.bridge`（已铺桥格——桥在 foundationGrid，TerrainAt 遮蔽返回 Bridge，tags 只有 Floor 无 Water/Road tag，没有此判据会把桥格 SetTerrain 成路面：topGrid 从水变土、桥塌后露出河里的路）；place 为 FlagstoneSandstone 时映射 rockDef（对齐原版区域岩色）。③Postfix `Generate`——快照 static `paths`（A* 路径节点）到 `SeamlessRoadPaths` MapComponent（防分帧增量生成下被其他地图清空），供 SeamOverride 道路保护用。
- **河流**（`Patches_TileMutatorRiver.cs`，order 220 内部）：Prefix `GetMapEdgeNodes`（protected，元组返回）——河端点从"随机直线的图外交点"替换为接缝边中点锚点（offsetCells=0，河延伸到边使两端水直接相接）。原生河 = 过随机中心(Rand 0.3-0.7×Size)的直线 + Perlin 弯曲（边缘漂移 ±11 格），两端只共享流向角、入口出口落点纯随机——无原生对齐保证。弯曲 bell 端点=0 → 接缝处河是直线段。Confluence 的流入/流出支流各取元组一端连汇合点，与本 patch 返回顺序 (heading反向端, heading正向端) 天然兼容。
- 共享几何：`SeamlessPolygonGeometry.FindClosestEdgeByAngle`（世界图方向角 → 本地图接缝边）+ `ComputeSeamCellForEdge`（边 → 接缝锚点格，offsetCells 参数：道路用 SeamOverlap、河流用 0）+ `DistanceToNearestEdge`（格 → 距最近边浮点距离）。
- `RefineEndcap` 的 ≤5 格端点检查**不会**移动六边形内侧锚点（它只是"是否执行 endcap 重路由"的门槛，锚点距方形边 ~19 格 → 恒跳过）——已排除的嫌疑，勿再查。

### 建筑选址避开六边形边（阶段4b）
- **问题**：原版建筑选址全按方形边界收缩（如 `GenStep_Settlement.CanScatterAt` 的 `BoundsRect(12)`），不感知六边形（边中点距方形边 ~17 格）→ 建筑锚点跨六边形边 → order 1400 铺 void 时被切半。
- **三个 patch**（`Patches_BuildingPlacement.cs`，覆盖全部选址根原语）：①Prefix `GenStep_Scatterer.CanScatterAt`（protected virtual，字符串声明特性；子类 base 调用命中）——锚点六边形外或距边 <20 格（=最大建筑半宽 19+1，Settlement 38×38）拒绝，上层 1000 次重试消化（安全区约占方形 55-60%）。②Postfix `MapGenUtility.GetClearRects`——过滤四角+边中点距边 <10 格或六边形外的矩形（清晰矩形路：Outpost/AncientComplex/Gravcore/SurveySite/Harbor 等；Burst 内核不可 patch，此托管入口是唯一可行点）。③Postfix `CellFinder.RandomNotEdgeCell`——采样六边形外 → Invalid（FindPlayerStartSpot tightness 降级兜底 + 运行时 CompDeepScanner/incident）。
- 几何判定用 `BuildPolygonVertices` 纯几何（order 400-970 期间可用），勿用 `HasSeamEdge`（运行时传送点入口，order 1400 前为 false）。
- 残余缺口（接受）：`GetOutpostRect` 贴附矩形、`GenerateLandingPadNearby`——锚点已安全后溢出概率低，观察。

### void 渲染与邻居背景（`SeamlessTileRenderer`）
- void 地形 `dontRender=true` 实际画在 `MatBases.ShadowMask`（半透明不写不透明色），主相机只清深度——void 带的背景完全依赖 `SeamlessTileRenderer` 的 CommandBuffer 每帧 `ClearRenderTarget(true,true)` 全屏清色 + 邻居地形 mesh（`BeforeForwardOpaque`）。
- **零邻居分支也必须清色**（曾经的历史 bug）：`cachedNeighbors.Count == 0` 时不能提前 return——否则清色被短路，void 带保留上一帧像素成红色拖影（新档锚点首生成/孤岛地块/Remove All Tile Maps/读档邻居未再生成四种场景）。零邻居时执行"清色-only"帧（不画邻居 mesh），同时 `commandBuffer.Clear()` 防邻居全部卸载后旧 buffer 逐帧重放已 Dispose 地图的 DrawMesh。

### 边缘行为统一（阶段4b）
- 浅绿色 exit grid：`Patches_ExitMapGrid` Prefix 重建只标六边形接缝带（不标原版方形带）。
- 根原语 patch：`Patches_CellFinder`（TryFindRandomEdgeCellWith/RandomEdgeCell）、`Patches_Reachability`（CanReachMapEdge）、`Patches_RCellFinder`（自构方形边的方法）。覆盖袭击入口/远行队出口/撤退/行人穿越。
- `SeamlessEdgeCells`：接缝带格统一入口（复用传送点格集合）。

### 跨图菜单与选中（阶段4b）
- `Patches_FloatMenuMakerMap`：Prefix 接管跨图 FloatMenu 生成，`InjectCrossMapGotoOption` 按桥接可达性注入跨图 Goto 选项。
- `SeamlessSelectionTracker`：跨切图的选中保持集（解决切图 ClearSelection 丢选中）。
- `SeamlessCameraFocus`：首个殖民者跨图自动聚焦 + 无感相机（切图后用 SetRootPosAndSize 同时恢复位置+缩放）。

## 存档兼容性说明

**mod 未发布，当前一切测试在新建存档中进行，无需考虑旧存档兼容。** 几何/传送点/字段变更后重开档即可，不做读档迁移。
