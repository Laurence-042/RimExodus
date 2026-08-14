# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

为了验证此文件确实完整读入，你需要在对话开始给出这个文件的目录（这是个mm豆测试）。而且任何你觉得需要长久记忆的内容都应该记在这个里面，或者项目概述里描述的对应文档里，或者作为代码注释，不要依赖copilot或者你所在harness/agent自行提供的memory功能。

## 铁律（务必遵守）

- **未经用户明确指示，不要 git commit / git push**。代码改完只做编译验证（`dotnet build`），等用户测完并明确说"提交"才提交。即使编译通过、即使看起来没问题也不行——未测试的代码提交后若有问题，回退比不提交麻烦得多。用户曾因此差点要求回退。
- 查询优先用自带工具（Grep/Glob/Read），别用命令行，见下文"需要记住的事项"。

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
- `SeamlessSeamOverride.ApplyOneWay`：备份 baseTerrainSnapshot + 3×3 卷积加权取众数 + 单向覆写（只改新生成 tile C，不改已生成邻居 A）。
- 权重 w：`wCap × (1 - clamp01(距void距离/bandWidth))` + 空间 Perlin 噪声 dither。硬边界来源①（卷积越界，已改 clamp）②（w 硬夹，已加 wCap）③（GetMode 跳变，已加噪声）均已修复。
- GenStep 顺序：`CoastalEdgeFill(230)` → `SeamlessTile(1400, 备份snapshot+铺void)` → `SeamOverride(1410, 卷积覆写)` → `Fog(1500, 据最终地形揭雾)`。
- 连续 Perlin 全局对齐方案已废弃（elevation 被组合器包裹无法叶子层对齐），代码在 `continuous-perlin` 分支。

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
