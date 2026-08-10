# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

为了验证此文件确实完整读入，你需要在对话开始给出这个文件的目录（这是个mm豆测试）。而且任何你觉得需要长久记忆的内容都应该记在这个里面，或者项目概述里描述的对应文档里，或者作为代码注释，不要依赖copilot或者你所在harness/agent自行提供的memory功能。

## 项目概述

RimWorld Mod：实现"无缝世界地块探索"系统，使相邻世界地块的局部地图在视觉与操作上连续连接，Pawn 可直接从一张地图走入相邻地图，无需组成远行队。

文档索引：
- `doc/无缝世界地块探索.md` — 长期设计与五阶段路线图。
  - 其中描述了核心交互场景，我们的最终目的是保证核心交互场景，因此实现过程中避免临时patch
- `doc/第一阶段-VMF调研.md` — VMF 源码调研、可复用能力和架构结论。
- `doc/第二阶段-最小技术原型.md` — 矩形原型的实现进度、渲染验证和剩余事项。

## 依赖引用目录

> **重要**：本仓库已包含全部所需源码与配置，**无需去游戏安装目录（`SteamLibrary/steamapps/common/RimWorld/`）查找**。游戏的源码、物品/Def 配置、以及 VMF/VF 框架源码都在 `references/` 下，直接在此目录内检索即可。

- `references/RimWorldDecompiled/` — RimWorld 反编译源码（含原生 `PocketMapParent`、`MapParent` 等）。
- `references/VehicleMapFramework/` — VMF（Vehicle Map Framework），口袋地图框架。
- `references/Vehicle-Framework/` — VF（Vehicle Framework），车辆基础框架。

## 关键源码位置

### RimWorld 原生
- `references/RimWorldDecompiled/RimWorld.Planet/PocketMapParent.cs` — 口袋地图基类，字段 `sourceMap`（宿主地图）、`mapGenerator`。
- `references/RimWorldDecompiled/RimWorld.Planet/MapParent.cs` — 地图父类基类。
- `Find.World.pocketMaps`（`World.cs`）— 原生口袋地图列表。

### VMF
- `VehicleMapFramework/Source/VehicleMapFramework/Things/VehiclePawnWithMap.cs` — 核心类。`GenerateVehicleMap`（~548）、`RemoveVehicleMap`（~628）、`SpawnSetup`（~653）、`ExposeData`（~1397）。
- `VehicleMapFramework/Source/VehicleMapFramework/VehicleMap/MapParent_Vehicle.cs` — `PocketMapParent` 子类，持有 `vehicle`。
- `VehicleMapFramework/Source/VehicleMapFramework/Utilities/VehicleMapUtility.cs` — 坐标转换 `ToBaseMapCoord`/`ToVehicleMapCoord`/`OffsetFor`/`TryGetVehicleMap`。
- `VehicleMapFramework/Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Map.cs` — `Patch_Map_MapUpdate`（~228）渲染行星视图到 RenderTexture。
- `VehicleMapFramework/Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Selector.cs` — 口袋地图对象选择。
- `VehicleMapFramework/Source/VehicleMapFramework/Comps/CompVehicleEnterSpot.cs` — 入口点。
- `VehicleMapFramework/Source/VehicleMapFramework/Jobs/ToilsAcrossMaps.cs` — 跨地图 Pawn 转移（`GotoTargetMap`）。
- `VehicleMapFramework/Source/VehicleMapFramework/Jobs/JobDrivers/JobDriverAcrossMaps.cs` — 跨地图 JobDriver。
- `VehicleMapFramework/Source/VehicleMapFramework/Utilities/CrossMapReachabilityUtility.cs` — 跨地图寻路/入口位置（`CanReach`、`EnterVehiclePosition`、`DestMap`/`DepartMap`）。
- `VehicleMapFramework/Source/VehicleMapFramework/Utilities/GenSightOnVehicle.cs` — 跨地图视线（`LineOfSight`/`LineOfSightThingToThing`）。
- `VehicleMapFramework/Source/VehicleMapFramework/Utilities/VerbOnVehicleUtility.cs` — 跨地图射击线（`TryFindShootLineFromToOnVehicle`/`ShouldConsiderCrossMap`）。
- `VehicleMapFramework/Source/VehicleMapFramework/Utilities/GenClosestCrossMap.cs` — 跨地图最近目标搜索。
- `VehicleMapFramework/Source/VehicleMapFramework/Combat/AttackTargetFinderOnVehicle.cs` — 跨地图目标搜索（`BestAttackTarget`）。
- `VehicleMapFramework/Source/VehicleMapFramework/Combat/CastPositionFinderOnVehicle.cs` — 跨地图施法位置。
- `VehicleMapFramework/Source/VehicleMapFramework/Combat/TargetingHelperOnVehicle.cs` — 跨地图炮塔目标搜索。
- `VehicleMapFramework/Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Combat.cs` — 接入跨地图目标搜索/施法位置。
- `VehicleMapFramework/Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Verb.cs` — 接入跨地图射击线/弹道。
- `VehicleMapFramework/Source/VehicleMapFramework/MapComponents/` — `VehicleMapGrid`、`VehiclePawnWithMapCache`、`VehicleSectionLayerManager`。
- `VehicleMapFramework/Source/VehicleMapFramework/WorldComponents/VehicleMapParentsComponent.cs` — 按地图 uniqueID 缓存 MapParent。
- `VehicleMapFramework/Source/VehicleMapFramework/UI/Command_FocusVehicleMap.cs` — 地图聚焦。

### VF
- `Vehicle-Framework/Source/Vehicles/Components/Vehicles/VehiclePawn/VehiclePawn_Handlers.cs` — `DisembarkPawn`（~490）、`DisembarkAll`（~568）。
- `Vehicle-Framework/Source/Vehicles/Utility/Extensions/Ext_Vehicles.cs` — `SpawnPawnNearVehicle`（~63）。

## 核心调研结论（VMF）

1. **PocketMapParent 是 RimWorld 原生类**，非 VMF 独有。`sourceMap` 字段即"宿主"指针，可运行时改写 → 宿主迁移受原生支持。
2. **创建**：`GenerateVehicleMap` 创建 `MapParent_Vehicle`，设 `sourceMap`，`MapGenerator.GenerateMap(..., isPocketMap: true)`，加入 `Find.World.pocketMaps`。尺寸 = `props.size + 2`。
3. **生命周期**：惰性生成；`RemoveVehicleMap` 置空 `sourceMap`、移出 pocketMaps、`DeinitAndRemoveMap`。
4. **坐标转换**：`ToBaseMapCoord` = `(orig - pivot).RotatedBy(angle) + vehiclePos + offset`。依赖车辆位置/朝向/偏移。
5. **渲染（关键差异）**：`Patch_Map_MapUpdate` 把行星视图渲染进 RenderTexture 再画成网格；备选 `DrawVehicleMapMesh` 直接画 SectionLayer。面向"跟随车辆的小地图"，**不适合大尺寸静态地块地图**。
6. **选择**：`Patch_Selector_SelectableObjectsUnderMouse` 用 `TryGetVehicleMap` + `ToVehicleMapCoord` 反查。
7. **入口**：`CompVehicleEnterSpot` + `CrossMapReachabilityUtility.EnterVehiclePosition`。
8. **Pawn 转移**：`ToilsAcrossMaps.GotoTargetMap`：走到出口 → 开门 → `drawOffset` 视觉过渡 → `DeSpawnWithoutJobClear()` + `GenSpawn.Spawn()`。
9. **存档/宿主迁移**：`PocketMapParent.ExposeData` 存 `sourceMap`/`mapGenerator`；`VehiclePawnWithMap.ExposeData` 存 `interiorMap`；读档 `SpawnSetup` 重关联 `sourceMap`。
10. **跨地图寻路**：`CrossMapReachabilityUtility` 用 `DestMap`/`DepartMap`/`DepartPosition` 记录 Pawn 跨地图上下文；`CanReach` 把出发/目的地地图通过**入口点对**（`exitSpot`/`enterSpot`）连接，支持 AStar（`AStar<MapTraverse>` 地图图搜索）与 legacy 遍历两种算法。接入：`Patches_Map.cs` 的 `Patch_Reachability_CanReach` 等。
11. **跨地图射击**：目标搜索 `AttackTargetFinderOnVehicle.BestAttackTarget` 扩展到 `BaseMapAndVehicleMaps`，用 `PositionOnBaseMapSpawned` 统一坐标；LOS 用 `GenSightOnVehicle.LineOfSight`（坐标 `ToBaseMapCoord` 映射到宿主地图）；射击线用 `VerbOnVehicleUtility.TryFindShootLineFromToOnVehicle`（`ShouldConsiderCrossMap` 判定）；弹道用 `Patches_Verb.cs` Transpiler 把 `Thing.Map`/`Position` 替换为 `BaseMap`/`PositionOnBaseMapSpawned`。接入：`Patches_Combat.cs`/`Patches_Verb.cs`。
12. **口袋地图能否显示在宿主边界之外（已验证）**：宿主 `MapDrawer` 的 ViewRect 与 `MapEdgeClipDrawer` 会阻止普通地图网格直接显示在边界外，但额外的主相机绘制通道可以绕开 ViewRect。最终原型使用 `CameraEvent.BeforeForwardOpaque` 的专属 `CommandBuffer` 将口袋地图主 Terrain 作为背景绘制，再只清深度并让原版宿主地图正常覆盖重叠带。宿主四块 `WorldClipper` 不能在 footprint 相交时整块跳过，必须逐块减去所有 footprint 后绘制剩余矩形，否则未被本帧写色的像素会在拖动时形成残影。接缝仍采用物理重叠带，使两端传送点映射到相同宿主坐标。

## 架构评估

- **可复用**：`PocketMapParent`/`sourceMap` 宿主机制、`Find.World.pocketMaps`、跨地图 Pawn 转移、入口机制、坐标反查选择、**跨地图寻路**（地图图 + 入口点对 + AStar 模型）、**跨地图射击**（坐标统一映射到宿主地图模型）。
- **需自行实现**：地图叠加层渲染（VMF 渲染管线面向车辆小地图）、把"锚点"从车辆抽象为静态宿主、六边形裁切与连续地形、**处理宿主 `MapEdgeClipDrawer.DrawClippers` 对边界外口袋地图的遮挡**（patch `MapEdgeClipDrawer` 或 Thing 绘制 + 自绘裁剪）。
- **倾向方案**：复用 PocketMapParent 机制 + 自行实现叠加层渲染，而非直接依赖 VMF 渲染管线。跨地图寻路与射击的"坐标统一映射到宿主地图"模型可直接复用，是实现"跨地块追击入侵者"（Pawn 跨地块射击）的关键。是否把 VMF 作为正式依赖，由最小技术原型验证后决定。

## 需要记住的事项

- **查询优先用自带工具，别用命令行**：正常情况下使用 Grep/Glob/Read 等内置查询工具做检索与定位，不要用 Bash 跑 `Select-String`/`grep`/`findstr` 等命令。命令行转义（尤其 Windows + Git Bash + PowerShell 的引号/路径混用）容易出错，还会消耗用户的检视精力去判断命令是否安全。**例外**：Grep 工具在本仓库偶尔对明确存在的内容返回空结果（不报错，静默失败），此时可改用 PowerShell `Select-String -Path <绝对路径> -Pattern <正则>`（绝对路径用正斜杠）作为后备，这是已知的可靠替代。
- `memory` 工具与 `create_file` 对超 ~150 行的内容有截断 bug：先建 stub，再分块（≤150 行）插入。
- 主设计文档的“当前阶段计划”当前定义推进顺序：VMF 调研 → 最小技术原型 → 六边形裁切 → 连续地形 → 跨地图寻路与射击（5 个阶段，其中“旅行 Pocket Map 宿主迁移”已被扁平化架构作废，跨地图寻路与射击增列为最后独立阶段）。阶段重排理由与扁平化作废详情见主文档“当前阶段计划”和“地块地图管理”节。第一、二阶段细节分别维护在独立文档中。
- 最小技术原型验证点：地图绘制/选取/移动命令、跨地图入口往返、存档读档恢复。**生成与渲染、双向 Pawn 转移、相邻地块选中与跨地图移动指令均已实现并通过编译**；游戏内交互表现与保存读档恢复仍待验证。
- **点击归属语义**（阶段3已升级，注意与 void 铺设区分）：玩家点击屏幕某位置时，`TryResolveMapPosition` → `TryGetOwnerNeighbor` 判定该点击归属哪个地图——cell 在当前地块六边形内 → 归当前地块；否则查是否在某已生成邻居六边形（按 offset 平移到当前坐标）内 → 归该邻居。这决定 pawn 跨图目标。**这与 void 铺设无关**：void 铺设只看自己六边形（六边形外即 void），而点击归属会查邻居（让玩家能点击 A 地图外的 B 渲染区）。旧的"最近中心所有权规则"（`TryGetOwnerPocketMap`，已废弃）已被点在凸多边形内判定（`ContainsPoint`）取代。详见下文"阶段3：多边形裁切"。

## 最小技术原型基础实现（已完成，可编译）

原型目标：验证"地图能否绘制/选取/接收移动命令、跨地图入口往返、存档读档恢复"。采用**矩形地图**（不做六边形裁切与连续地形），复用 `PocketMapParent`/`sourceMap` 宿主机制 + 自行实现叠加层渲染（不依赖 VMF 渲染管线）。

### 项目结构
- `About/About.xml` — mod 元数据，packageId `RimExodus.SeamlessWorld`，依赖 `brrainz.harmony`，支持 1.6。
- `Source/RimExodus.csproj` — net48，引用 `Krafs.Rimworld.Ref 1.6.4633` + `Lib.Harmony.Ref 2.4.2`，输出到 `..\1.6\Assemblies\`。
- `1.6/Defs/WorldObjectDefs/WorldObjects.xml` — `RimExodus_SeamlessTileMap` WorldObjectDef，worldObjectClass `RimExodus.MapParent_SeamlessTile`，mapGenerator `RimExodus_SeamlessTileGenerator`。
- `1.6/Defs/MapGeneration/SeamlessTileGenerator.xml` — `RimExodus_SeamlessTileGenerator` MapGeneratorDef（pocketMapProperties biome BorealForest）+ `RimExodus_SeamlessTile` GenStepDef（genStep Class="RimExodus.GenStep_SeamlessTile"）。

### 源码文件（`Source/`）
- `RimExodusMod.cs` — `[StaticConstructorOnStartup]`，`new Harmony("RimExodus.SeamlessWorld").PatchAll()`。
- `MapParent_SeamlessTile.cs` — `PocketMapParent` 子类。字段：`worldTile`、`neighbors`（`List<NeighborLink>`，含 `worldTile`/`neighbor`/`offset`；阶段4a 全面重构后无 `direction`/`edgeAngle`/`hostOffset`/`neighborTiles` 字段，offset 隐式编码方向）。`ExposeData` 存全部字段。
- `SeamlessMapUtility.cs` — 坐标转换：基于显式 offset 的 `ToMapCoord`/`FromMapCoord`/`ToMapDrawPos`/`TryResolveMapPosition`（泛化所有权解析）。
- `SeamlessTileManager.cs` — `MapComponent`。`GenerateTileMap(direction, mapSize, overlapBand)`：`WorldObjectMaker.MakeWorldObject` → 设 `sourceMap`/`Tile=0`/`direction`/`hostOffset` → `MapGenerator.GenerateMap(..., isPocketMap: true)` → 加入 `Find.World.pocketMaps` + `Find.World.worldObjects` → 共享宿主 skyManager/weather。`ComputeHostOffset` 把口袋地图放宿主边界外并留重叠带。`GetTileMapInDirection`/`RemoveTileMap`。
- `SeamlessTileRenderer.cs` — `MapComponent`，维护绑定到主相机 `CameraEvent.BeforeForwardOpaque` 的专属 `CommandBuffer`。对称渲染：遍历 `SeamlessTileGraph.PopulateNeighbors(map)`（复用缓存列表），对每个邻居提交 `SectionLayer_Terrain` + `SectionLayer_ThingsGeneral`（精确类型）+ 手动绘制 Pawn。绘制后只清深度，由原版当前地图覆盖重叠带。
- `Patch_MapEdgeClipDrawer_DrawClippers.cs` — 收集所有口袋地图 footprint，从四块原版世界裁剪矩形中依次做矩形差集，仅绘制剩余矩形；保留原版高度和世界对齐纹理参数。只在真实 footprint 开洞，避免拖动残影。
- `SeamlessTileRegistry.cs` — `GetFootprintsOnHost(Map)` 返回宿主坐标 `List<CellRect>`（局部矩形 + hostOffset）。
- `GenStep_SeamlessTile.cs` — `GenStep`，`Generate` 铺设矩形地形：边缘 2 格不可通行（WaterOceanDeep），内部可通行（Soil）。
- `CompSeamlessTileEnterSpot.cs` — `ThingComp` 入口点，仅持有持久化的 `CounterpartSpot` 对端引用。端点双方地位完全对等，不感知方向/宿主/口袋身份。载体是 Ethereal/ThingWithComps（非 Building），见下文"第二阶段细节修复"。
- `SeamlessMapTransfer.cs` — 跨地图 Pawn 转移：`TryTransferPawn`（端点对端点，`DeSpawn()` + `GenSpawn.Spawn()`，不感知宿主/口袋身份）。
- `SeamlessMapTransferTrigger.cs` — `MapComponent`，**每 tick** 用 `ThingsOfDef`（O(1) def 索引）检查本地图传送点上是否有 Pawn，触发转移，转移成功后调用自动聚焦并消费续程登记。每 tick 扫描消除轮询卡顿；复用静态列表避免每 tick GC。防反弹用 **pawn 级锁**（`Dictionary<Pawn,bool>`）：Pawn 跨图到达后进入锁状态，只要还站在本图任一接缝传送点上就保持锁，离开整条接缝带才解锁。

### 关键实现要点
- **渲染顺序**：不再依赖 `MapComponentOnDraw` 的 `Graphics.DrawMesh` 调用顺序或高度 epsilon。口袋主 Terrain 在 `BeforeForwardOpaque` 背景通道写颜色，随后清深度，宿主地图照原版路径绘制，因此本端稳定覆盖对端。
- **MapComponent 自动注册**：`Map.FillComponents` 自动实例化所有 `MapComponent` 非抽象子类，无需手动注册。
- **编译**：仓库根目录运行 `just build`（默认任务也是 `build`），底层命令为 `dotnet build Source/RimExodus.csproj -c Debug`，输出 `1.6/Assemblies/RimExodus.dll`。已通过（0 错误 0 警告）。
- **踩坑**：`Scribe_Collections.Look` 只接受 `List<T>`（非数组），故 `neighborTiles` 用 `List<int>`；`Pawn_MindState.Reset` 有两个重载需显式传参；`TerrainDefOf` 需 `using RimWorld;`；`WorldObjectDef` 在 `RimWorld` 命名空间（非 Planet）。
- **待办**：原型尚未实现跨地图寻路/射击（VMF 的 `CrossMapReachabilityUtility`/`AttackTargetFinderOnVehicle` 模型可复用）、六边形裁切、连续地形。

## 生成 + 渲染可测试性补全（已完成，可编译）

**背景**：此前原型只是"能编译的空壳"——`GenerateTileMap` 无任何调用者、传送点 Thing 从未放置、转移只有单向、渲染器遍历空列表，验证点一个都测不了。本次补上"生成 + 渲染"的可测试性（用户明确范围：只补生成+渲染，转移后续再说）。

### 方向系统扩展为 6 向（已被阶段3取代）
- **历史记录**：第二阶段 `direction` 语义从 0-3（四向）扩展为 0-5（六边形方向），原型四向兼容（1/2 当东，4/5 当西）。
- **阶段3已废弃**：`direction` 字段完全移除，改用基于世界地块真实顶点角度的动态方向（`NeighborLink.edgeAngle` + `worldTile` 主键）。详见下文"阶段3：多边形裁切"。

### Dev 命令（`Source/DebugActions_SeamlessTile.cs`）
- 用 `[DebugAction("RimExodus", ..., allowedGameStates = AllowedGameStates.PlayingOnMap)]` 特性注册静态方法（LudeonTK 命名空间）。
- 命令：Generate North/NorthEast/SouthEast/South/SouthWest/NorthWest、Generate All 6、Remove All。
- 默认地图尺寸 50×50，`DefaultOverlapBand=5`。`CurrentManager` 取 `Find.CurrentMap.GetComponent<SeamlessTileManager>()`。

### 自动生成北侧（`SeamlessTileManager`）
- `MapGenerated()` 钩子：开档自动生成北侧地块（`autoGeneratedNorth` 标志防重复）。
- **关键坑**：`MapComponentUtility.MapGenerated(map)` 在 `MapGenerator.GenerateMap` **内部**调用（`MapGenerator.cs` ~193），此时 `MapGenerator.mapBeingGenerated` 仍非空，直接调 `GenerateTileMap` 会被拒绝返回 null。**必须延迟到下一 tick**：`MapGenerated()` 里设 `pendingAutoGenerateTicks = 1`，`MapComponentTick()` 里递减到 0 再调 `TryAutoGenerateNorth()`。
- `TryAutoGenerateNorth`：若方向 0 已存在则跳过，否则使用 `DefaultOverlapBand` 调用 `GenerateTileMap(0, mapSize, overlapBand)`。
- **致命坑（已修复）**：`SeamlessTileManager` 是 `MapComponent`，会被 `Map.FillComponents` 自动实例化到**每一张地图**上，包括 `GenerateTileMap` 生成的口袋地图本身。若不在口袋地图上跳过，口袋地图的 `MapGenerated()` 也会触发自动生成 → "生成北侧 → 生成口袋地图 → 口袋地图又生成北侧"的**无限递归卡死**（日志刷屏 `Auto-generated north seamless tile map`）。修复：`MapGenerated()` 和 `MapComponentTick()` 开头都加 `if (map.IsPocketMap) return;`。
- **性能坑（已修复）**：`Section.RegenerateAllLayers()` **不会清除 `dirtyFlags`**（只有 `TryUpdate` 会，且 `TryUpdate` 依赖 `bounds.Overlaps(view)` 宿主 ViewRect，口袋地图在边界外恒 false）。若 `RegenerateAllLayers()` 后不手动 `section.dirtyFlags = 0uL`，只要 dirtyFlags != 0，**每帧都会重建整个口袋地图的所有 SectionLayer 网格**（地形/建筑/植物/光照），大量内存分配 + GC 卡顿。修复：`SeamlessTileRenderer.DrawPocketMap` 里 `RegenerateAllLayers()` 后手动清零 `dirtyFlags`。
- **渲染层语义坑（已修复）**：直接重放全部 submesh 会绕过各 `SectionLayer.DrawLayer()` 的专用目标；尤其 `SectionLayer_Watergen` 虽继承 Terrain，却只能进入水深子相机。当前用精确类型判断只提交 `SectionLayer_Terrain`，消除蓝红水深颜色。
- **深度与残影坑（已修复）**：正负高度 epsilon 都不能可靠控制混合 render queue 的 Terrain。最终通过背景 CommandBuffer → 只清深度 → 宿主原版绘制建立确定顺序，并以 clipper 矩形差集保证 footprint 外每帧仍写入颜色。高度 epsilon 已删除。
- **重叠带宽度**：`TryAutoGenerateNorth` 里 `overlapBand` 从 4 改为 5（用户确认边界应为 5 格）。

### 验证
- `dotnet build` 通过（0 错误 0 警告），输出 `1.6/Assemblies/RimExodus.dll`。
- **游戏内结果**：5 格重叠带由本端覆盖；水域/土地无蓝红水深色；拖动和缩放无残影；footprint 外仍由 clipper 覆盖。用户已确认问题解决。
- **测试方式**：开档后北侧地块自动生成（渲染器/裁剪 patch 有东西可画）；Dev 菜单 "RimExodus" 分类下可手动生成其余 5 向、生成全部、卸载全部。

### 仍待办
- 跨地图寻路/射击、六边形裁切、连续地形。
- 口袋地块邻居的自动生成（"pawn 接近边界"事件驱动，当前需 Dev 命令手动触发）。
- 天气/天空管理器共享的存档重载验证（多地图共享同一 manager 实例可能重复 tick）。

## 相邻地块选中 + 跨地图移动指令（已实现，可编译，游戏内待验证）

实现细节完整记录在 `doc/第二阶段-最小技术原型.md` 的"相邻地块选中 + 跨地图移动指令"一节；这里只记长期有效的关键事实。

- 新增文件：`SeamlessGenUI.cs`（Map 感知版 `ThingsUnderMouse`）、`Patches_Selector.cs`（鼠标反查选中口袋地图对象）、`Patches_FloatMenuMakerMap.cs`（Prefix 接管跨地图 FloatMenu 生成）、`SeamlessCrossMapOrders.cs`（pending 目标登记 + 桥接 Job）、`SeamlessCrossMapPendingDestinations.cs`（转移后续程登记）、`Patches_Job.cs`（`Pawn_JobTracker.StartJob` 拦截点）。`SeamlessMapTransferTrigger.cs` 转移成功后会消费续程登记并自动续发 Goto。
- **架构**：仿 VMF 的"环境态地图上下文替换"思路，但因为 `FloatMenuMakerMap.GetOptions`/`FloatMenuContext` 构造器把 `Find.CurrentMap` 写死为字面属性访问（不是参数传递），改用 **Prefix 全量接管**（两端都在宿主地图时 `return true` 完全放行原版；否则复制原方法逻辑处理）取代 VMF 式 Transpiler 精确 IL 替换，更稳妥但需要跟随 RimWorld 版本更新同步核对原方法逻辑是否变化。
- **已知限制**（详见 `doc/第二阶段-最小技术原型.md`）：
  1. `Selector.SelectInternal` 选中跨图 Thing 时会自动切换 `Find.CurrentMap` 并跳镜头（原版行为，未抑制）。
  2. 跨图 Job 拦截靠"pawn 是否有未消费的跨图 pending 记录 + 下一个 Goto"判断，不按 Cell 精确匹配（因为 `FloatMenuOptionProvider_DraftedMove` 会用 `RCellFinder.BestOrderedGotoDestNear` 就近改点）。
  3. `Pawn_JobTracker.TryTakeOrderedJob` 会在 `StartJob` 之前用 `pawn.Map.pawnDestinationReservationManager.Reserve` 预定一个实际属于另一张地图坐标系的 Cell，且这个原始 Job 从不真正 StartJob，正常清理流程不会释放它——已知的无害小缺陷（占用宿主地图上一个不相关格子），未修复。
  4. 只做移动（`JobDefOf.Goto`），单跳桥接，不做跨图射击/近战/搬运/建造，不做框选/Zone高亮/`CameraJumper`细节。

### 补充修复：口袋地图上的 Pawn 之前不可见（已修复）

- **问题**：`SeamlessTileRenderer` 之前只重放口袋地图的 `SectionLayer_Terrain`（地形），完全没有绘制 Pawn/建筑等动态物体。原版 `DynamicDrawManager.DrawDynamicThings()` 只在 `Find.CurrentMap == 该地图` 时才会绘制该地图上的动态物体，所以聚焦宿主地图时，口袋地图上的 Pawn 从未被绘制过，自然也无法被鼠标选中（选中反查逻辑本身没问题，只是画面上根本没有东西可点）。
- **修复**：`SeamlessTileRenderer.DrawPocketMapPawns` 在 `MapComponentDraw()` 里对每个口袋地图，遍历 `pocketMap.mapPawns.AllPawnsSpawned`，直接调用 `pawn.DrawNowAt(pawn.DrawPos + parent.hostOffset.ToVector3())`。
  - `Thing.DrawNowAt(Vector3 drawLoc)` 是引擎自带的公共方法，接受显式坐标、绕开 `DrawPos`/`Position`，`Pawn` 重写的 `DynamicDrawPhaseAt` 会把这个显式坐标一路传给 `PawnRenderer`，因此**不需要 patch `Thing.DrawPos`/`Pawn_DrawTracker.DrawPos` 这种大范围侵入式 patch**（那是 VMF 的做法，但 `DrawPos` 在全局被大量非渲染逻辑读取，全局 patch 风险高）。
  - 只处理了 Pawn；建筑/物品的 GUI 悬浮图标（`ThingOverlays.ThingOverlaysOnGUI`，血条/情绪图标等）仍未做偏移处理，如果后续需要可以参照 VMF 的 `Patch_ThingOverlays_ThingOverlaysOnGUI` 补充。
  - 每个 Pawn 绘制包了 try/catch + `Log.ErrorOnce`，避免单个 Pawn 绘制异常导致整帧渲染中断。

## 对称无缝地块架构（已完成，可编译，游戏内待验证）

**核心架构转变**：从"宿主特殊论"（依赖 `sourceMap`/`IsPocketMap` 区分宿主与口袋）转向"地块对等论"（基于直接邻居表）。目的是支持核心交互场景里的 B→C（口袋到口袋），使 A→B、B→A、B→C 完全相同处理。

### 直接邻居表（对称性的根基）
- `MapParent_SeamlessTile.neighbors`：`List<NeighborLink>`，每条含 `{direction, neighbor(MapParent), offset(IntVec3)}`。`neighbor` 为 `MapParent` 基类，可容纳锚点 MapParent（如 Settlement）和 `MapParent_SeamlessTile`。
- **偏移契约**：`offset = 邻居本地坐标 → 本地块坐标`的平移。即 `neighborLocal + offset = myLocal`。所有组件（Renderer/Registry/CameraFocus/PlaceEnterSpots）严格遵守此契约。
- **无全局世界坐标**：每张地块只存与直接邻居的相对偏移，不累加。原型整数偏移无精度问题；六向扩展时偏移可能带 `H/2`，但因不累加，单次精度无损。
- 锚点地图（家园 A，普通 Map）的邻居表存于其 `SeamlessTileManager` MapComponent；口袋地图的存于 `MapParent_SeamlessTile`。`SeamlessTileGraph`（静态工具）提供 `GetAllNeighbors`/`PopulateNeighbors`/`TryGetNeighborLink`/`AreNeighbors` 统一入口，屏蔽存储位置差异。
- **约束**：单跳可见（A 只看 B，B 只看 A+C），单跳寻路（不跨地图寻路，传送点桥接只做单跳）。

### sourceMap 扁平化
- 所有地块的 `sourceMap` 统一指向**锚点地图**（家园 A），即使从 B 生成 C，C.sourceMap = A（不是 B）。
- 避免原生嵌套副作用（财富/威胁/移除连带的父子归属误判——原生有 8 处依赖 sourceMap 的"宿主-口袋"语义）。
- `hostOffset` 字段保留，语义为"新地块相对生成源地块的偏移"（不是相对锚点）。
- skyManager/weatherManager 共享自锚点 A。**已知风险**：多地图共享同一 manager 实例可能导致重复 tick 和存档重载问题（原代码已有的模式，非本次引入，待测试）。

### 防递归机制
- 旧的 `if (map.IsPocketMap) return;` 禁令部分保留：`MapGenerated` 开档自动生成仅锚点地图触发（口袋不级联自动生成，避免生成风暴）。
- `GenerateTileMap` 内新增邻居表查重防递归：`if (GetNeighborInDirection(direction) != null) return null;`。
- 口袋地块的邻居生成由玩家显式命令（Dev 菜单）或未来的"pawn 接近边界"事件驱动，不通过 MapGenerated 级联。

### 传送点满铺 + 多点寻路
- `PlaceEnterSpots` 沿接缝方向满铺传送点对（跳过边缘 2 格不可通行区）。`EnumerateSeamCells` 按方向枚举接缝候选格。传送点载体是 **Ethereal/ThingWithComps**（非 Building），不可摧毁/不可攻击/不可占领/不占 edifice，可与岩山/深水/墙共存（见下文"第二阶段细节修复"）。
- `Standable/pathCost=0` 的传送点不影响寻路网格成本。
- `TryFindNearestReachableBridgeSpot`：按 def 索引查询候选传送点，按到 pawn 距离排序，依次试 `CanReach`，返回第一个可达。
- 触发扫描 `CheckLocalEnterSpots` 改用 `ThingsOfDef`（O(1) def 索引），避免全量 AllThings 遍历。
- **防反弹（pawn 级锁）**：Pawn 跨图到达后进入锁状态（`Dictionary<Pawn,bool>`），只要还站在本图任一接缝传送点上就保持锁，离开整条接缝带才解锁。锁状态与特定 spot 无关——Pawn 在锁期间踩任何接缝传送点都不会再触发传送，防止续程寻路沿接缝前进时踩到相邻 spot 被立刻传回。

### 自动聚焦 + 无感相机切换（`SeamlessCameraFocus.cs`）
- 首个玩家殖民者（`pawn.IsColonist`）跨图进入新地块（`MapParent_SeamlessTile` 且 `!autoFocused`）时触发。
- 流程：记录相机位置**和缩放**（`camSize = RootSize`）→ `Current.Game.CurrentMap = arrivalMap`（触发原生 `Notify_SwitchedMap` 同时恢复位置+缩放）→ 立即 `SetRootPosAndSize(camPos - offset, camSize)` 覆盖（同时恢复位置和缩放，画面不动=无感）。
- `autoFocused` 标志持久化，每个地块仅触发一次。玩家切回原地图后，第二个 pawn 进入不再自动聚焦。
- **必须在续程前调用**（切图后 `pawn.Map == CurrentMap`，避免 `Selector.SelectInternal` 二次跳镜头）。
- `FindNeighborOffset` 从 `private` 提为 `internal`，供选中切换的无感覆盖（`Patch_Selector_SelectInternal`）复用同一偏移查询。
- **缩放必须一并恢复**：`CurrentMap` setter 触发的 `Notify_SwitchedMap` 会用新地图 `rememberedCameraPos` 同时恢复位置+缩放（`MapInterface.cs:211-212`），`JumpToCurrentMapLoc` 只设位置不设缩放会缩放不一致，必须用 `SetRootPosAndSize`。

### 对称渲染
- `SeamlessTileRenderer` 移除 `map.IsPocketMap` 守门，任意图块聚焦时 Renderer 都工作。
- 遍历 `SeamlessTileGraph.PopulateNeighbors(map)`（复用缓存列表避免每帧分配），对每个邻居用其 offset 平移绘制。
- `CollectNeighborLayers` 收集 `SectionLayer_Terrain`（地形）+ `SectionLayer_ThingsGeneral`（建筑/岩石/静态植物，精确类型，无 Watergen 式独立相机依赖）。排除 SunShadows/FogOfWar/Gas 等有 grid/shadow 依赖的层。
- `DrawNeighborPawns` 遍历邻居 `mapPawns.AllPawnsSpawned`，`pawn.DrawNowAt(pawn.DrawPos + offset)`。
- 保留清深度逻辑（重叠带覆盖本次接受"聚焦者覆盖"；完全对称留待连续地形阶段的归属裁剪——用户确认此策略：之后调整地形生成连续性时，让归属权不在当前地图的 tile 生成为透明虚空，无需渲染层处理重叠）。
- `Patch_MapEdgeClipDrawer_DrawClippers` 改用 `GetNeighborFootprints(map)`，支持任意图块聚焦时为邻居开洞。

### 征召状态跨图保持（已修复）
- **坑 1（drafter 销毁重建）**：`Pawn.DeSpawn()` 调用 `RemoveComponentsOnDespawned`，把 `pawn.drafter = null`（整个 `Pawn_DraftController` 实例被丢弃，连同 `draftedInt`）。`GenSpawn.Spawn` 新建了一个 `Pawn_DraftController`，`draftedInt` 默认 `false`。`drafter` 是"despawn 时移除的组件"，不是持久组件。
- **坑 2（lord 悬空）**：`Pawn.DeSpawn` **不清 `pawn.lord` 字段**。跨图后 pawn 仍指向旧地图的 lord。`Pawn.GetGizmos` 的 `GetLord()?.AllowsDrafting(this)` 会走旧 lord 判定，若旧 lord 禁止征召则征召 gizmo 被禁用（UI 误报"征召丢失"）。旧 lord 残留也干扰 think tree。
- **坑 3（mindState.Reset 时序）**：原代码在 `Drafted=true` 恢复之后调 `mindState.Reset`，清掉 duty/priorityWork——多余且有害，已删除。
- **修复（TryTransferPawn 唯一标准实现）**：`TryTransferPawn` 是所有跨图转移（A→B、B→A、B→C）的唯一入口，征召/lord 状态保存恢复全部内聚于此：
  - 转移前：保存 `wasDrafted`/`wasFireAtWill`；`prevLord.Notify_PawnLost(pawn, Vanished)` detach 旧 lord。
  - 转移：`DeSpawn()` → `Spawn()`（drafter 销毁重建）。
  - 转移后：`pawn.lord = null`（确保字段干净）；`pawn.drafter.Drafted = true` 恢复征召（setter 清队列，须在续程 StartJob 之前）；恢复 fireAtWill。
  - 删除了多余的 `EndCurrentJob`（DeSpawn 已 StopAll）和 `mindState.Reset`（时序错误）。

### 源码文件新增/大幅修改
- 新增：`SeamlessTileGraph.cs`（邻居表统一查询入口）、`SeamlessCameraFocus.cs`（自动聚焦+无感相机）。
- 大幅修改：`MapParent_SeamlessTile.cs`（邻居表+NeighborLink）、`SeamlessTileManager.cs`（扁平化+满铺+防递归+GetTileMapInDirection/RemoveTileMap 改邻居表）、`SeamlessTileRegistry.cs`（GetNeighborFootprints/TryGetOwnerNeighbor/AreSeamlessNeighbors）、`SeamlessMapUtility.cs`（ToMapCoord/FromMapCoord/TryResolveMapPosition 泛化）、`SeamlessTileRenderer.cs`（对称渲染）、`SeamlessCrossMapOrders.cs`（多点寻路）、`SeamlessMapTransferTrigger.cs`（def 索引扫描+自动聚焦）、`DebugActions_SeamlessTile.cs`（RemoveAll 改邻居表）。

## 第二阶段细节修复三则（已完成，可编译，游戏内待验证）

### 修复 1：选中跨图 Pawn 的无感相机覆盖
- **问题**：原生 `Selector.SelectInternal`（`references/.../RimWorld/Selector.cs:379-384`）选中跨图 Thing 时 `Current.Game.CurrentMap = map`（行 381）后 `JumpToCurrentMapLoc(thing.PositionHeld)`（行 384）硬跳聚焦到该 Pawn。期望是切图但相机保持相对偏移。
- **修复**：`Patches_Selector.cs` 新增 `Patch_Selector_SelectInternal`（Prefix + Postfix）。Prefix 在原方法切图前检测即将选中跨图直接邻居 Thing → 记录 `preservedCamPos`/`crossMapOffset`；Postfix 用 `JumpToCurrentMapLoc(camPos - offset)` 覆盖行 384 的聚焦，末尾清空上下文。非直接邻居回退原生聚焦。`SeamlessCameraFocus.FindNeighborOffset` 提为 `internal` 供复用。

### 修复 2：传送点载体从 Building 改为 Ethereal（ThingWithComps）
- **问题**：Building category 可被攻击/拆除/占领，`isEdifice` 默认 true 与岩山墙冲突，`Walkable` 过滤跳过岩山/深水。
- **修复**：`1.6/Defs/ThingDefs/SeamlessEnterSpot.xml` 改 `thingClass=ThingWithComps`、`category=Ethereal`，加 `destroyable=false`/`useHitPoints=false`/`selectable=false`/`drawerType=None`，删 `graphicData`。`PlaceEnterSpots` 源端去掉 `Walkable` 过滤（Ethereal 可与任何地形共存），对端保留 `Walkable`（传送可行性），失败回退 `Destroy()→DeSpawn()`。
- **关键坑**：
  - **`drawerType=None` 必须**：`selectable=false` 不跳过绘制（`DynamicDrawManager` 只看 `drawerType`），默认 `RealtimeOnly` 会画粉色 `BadGraphic`。
  - **Ethereal 进 `listerThings`**：`ListerThings.EverListable` 只排除 `Mote`（和 Region 级 `Projectile`），Ethereal 返回 true，`ThingsOfDef` 照常工作，消费者零改动。
  - **不被攻击/占领**：`AttackTargetFinder` 按 `IAttackTarget` 接口查（与 category 无关）；`Claimable` 要求 `building != null`，Ethereal 无 building 节点。
  - **不进 edificeGrid**：`IsEdifice()` 读 `building.isEdifice`，Ethereal 无 building；`GenSpawn.SpawningWipes` 的 edifice 冲突擦除只在"新生成物 IsEdifice()"时触发，Ethereal 跳过，不擦岩山墙。
  - **`destroyable=false` 后清理用 `DeSpawn`**：`Thing.Destroy`（`Thing.cs:1045`）在 `!def.destroyable` 时直接 return。正常卸载走 `DeinitAndRemoveMap`（整张地图销毁）不受影响，只有 `PlaceEnterSpots` 失败回退需改 `DeSpawn`。

### 修复 3：传送反弹 bug（pawn 级锁 + 离开接缝带才解锁）
- **问题**：传送点满铺接缝，Pawn 被传到对端 B_x 后续程沿接缝前进，离开 B_x 即解锁（旧锁是"特定 spot"），踩上相邻 B_{x+1}（无锁）被传回。
- **修复**：`SeamlessMapTransferTrigger.cs` 的 `arrivalLocks` 从 `Dictionary<Pawn,Thing>` 改为 `Dictionary<Pawn,bool>`（pawn 级锁状态）。`RecordArrival(pawn)` 只记 pawn；`IsArrivalLocked(pawn)` 不比较 spot；`PurgeInvalidArrivalLocks` 清除条件改为"pawn 不再站在本图任何接缝传送点上"（每帧收集接缝 spot 位置到 `HashSet<IntVec3>` 查询）。Pawn 在锁期间踩任何接缝 spot 都不触发传送。`SeamlessMapTransfer.TryTransferPawn` 的 `RecordArrival` 调用同步改为单参数。`ExposeData` value LookMode 从 `Reference` 改 `Value`。

### 修复 4：跨图切换相机缩放不一致
- **根因**：`Current.Game.CurrentMap` setter 触发 `MapInterface.Notify_SwitchedMap`（`MapInterface.cs:211-212`），用 `SetRootPosAndSize` 从新地图的 `rememberedCameraPos`（每张 Map 一份，`Map.cs:217`，默认 rootSize=24f）**同时恢复位置和缩放**。原先只用 `JumpToCurrentMapLoc`（只设 rootPos.x/z）恢复位置，缩放被原生值覆盖。
- **修复**：切图前记 `camSize = Find.CameraDriver.RootSize`，切图后改用 `SetRootPosAndSize(new Vector3(targetPos.x, 0f, targetPos.z), camSize)` 同时恢复。改了 `SeamlessCameraFocus.TryAutoFocusOnArrival` 和 `Patches_Selector.cs` 的 `Patch_Selector_SelectInternal`。无需手写 `rememberedCameraPos`——`CameraUpdater`（`CameraDriver.cs:427-432`）每帧自动镜像回 CurrentMap。

### 修复 5：过传送点卡一下（轮询间隔 + GC）
- **根因**：`ScanIntervalTicks=30`（~0.5s）轮询，Pawn 到达传送点后 Goto 已结束、无事件触发，等下一次扫描窗口。续程 Goto 在转移同 tick 内 StartJob 无延迟。
- **修复**：`ScanIntervalTicks` 改 1（每 tick 扫描）。配合：缓存 `cachedEnterSpotDef` 避免每 tick `DefDatabase` 查；用 `static readonly` 复用列表（`List<Thing>/List<Pawn>/HashSet<IntVec3>`，Clear+Add）替代每 tick `new List` 快照，消除 GC 分配；`PurgeInvalidArrivalLocks` 在 `arrivalLocks.Count==0` 时空操作。

## 阶段3：多边形裁切（已完成，可编译，游戏内待验证）

实现"用可重叠正方形承载六边形网格"的完整多边形几何。支持 N=5（五边形）和 N=6（六边形）地块，统一遍历 N 个顶点，不区分边数。已游戏内验证核心闭环（见末尾）。

### 三态几何模型（核心，务必正确理解）
每个地块地图上的格子分为三态，**完全由自己的六边形决定，不看邻居**：
- **六边形内部**（不含边）：非 void，**无传送点**。
- **六边形的边**（顶点连线经过的格）：非 void，**有传送点**（仅对应已生成邻居的边）。
- **六边形外部**：**void**。

两个地图**各自独立**铺 void。地图 A 六边形外的 void 区域，在地图 B 上恰好是 B 六边形的内部/边（非 void）——这就是"void 完全被 B 覆盖"的含义（从对方地图视角看）。**不要**在 A 地图上把"被 B 覆盖的区域"判为非 void——A 上那个区域就是 void。

两端传送点通过 offset 对齐到同一世界坐标：A 北边格 ↔ B 南边格重合，各自地图上那条边都是非 void（边格），故两端都可站立。

### 几何模型（内切圆顶点模型）
- **不使用** H/S/k 长宽比代数、circumradius 比例常数、apothem/cos 换算。
- **多边形顶点 = 地图中心 + 0.5S × 顶点方向单位向量**（S=地图边长）。顶点位于正方形地图内切圆上。
- **顶点方向**：世界地块顶点（`grid.GetTileVertices`）相对中心投影到切平面（`WorldRendererUtility.GetTangentsToPlanet`）归一化，忠实于地块真实朝向（flat-top/pointy-top/旋转）。
- **邻居 offset** = `round(2 × (边中点 - 中心))`，边中点取自多边形顶点。边由邻居 worldTile 在源地块邻居表中的位置确定。
- **传送点**：沿多边形边（顶点 j→j+1）Bresenham 划线满铺，两端映射同一世界坐标。

### 数据模型重构（direction → 动态方向）
- **`MapParent_SeamlessTile.direction` 字段移除**。旧固定 0-5 编号（北/东北/...）完全废弃。
- **`NeighborLink` 结构变更**：`direction`(int) → `edgeAngle`(float 弧度) + `worldTile`(int)。`worldTile` 作为邻居表主键（稳定无歧义）。（注：阶段4a 全面重构已进一步移除 `edgeAngle`，当前为 `worldTile` + `neighbor` + `offset` 三字段，offset 隐式编码方向。）
- **查询方法**：`GetNeighborInDirection(int)` → `GetNeighborByWorldTile(int)`；`SetNeighbor(int,...)` → `SetNeighbor(int worldTile, float edgeAngle, MapParent, IntVec3 offset)`。
- **`SeamlessTileGraph.OppositeDirection` 移除**（双向登记保证反向关系）。`TryGetNeighborLink` → `TryGetNeighborLinkByWorldTile`。
- **存档不兼容**：旧存档（含 direction）无法加载，原型阶段接受。

### 新增源码文件
- `Source/WorldTileGeometry.cs` — 世界地块真实几何读取。`ComputeVertexDirections`/`ComputeEdgeDirections`（顶点/边方向）、`FindNeighborIndex`（邻居序号反查）。用 Odyssey 版 `WorldGrid` API（`GetTileVertices`/`GetTileNeighbors`/`GetTileCenter`/`GetMaxTileNeighborCountEver`）。
- `Source/SeamlessPolygonGeometry.cs` — 多边形几何工具。`BuildPolygonVertices`（内切圆顶点，阶段3 收尾加了进程级缓存）、`ContainsPoint`/`IsCellInPolygon`（点在凸多边形内 / 格角检测消除 void 孤岛）、`ScanlineFill`（凸多边形扫描线填充）、`EnumerateEdgeCells`（边 Bresenham 划线）。（注：阶段3 设计的 `BuildNeighborCenterOffsets`/`ContainsPointTranslated` 实际内联到 `SeamlessTileManager.ComputeNeighborOffset` / `SeamlessTileRegistry.TryGetOwnerNeighbor`，未作独立方法保留。）
- `Source/SeamlessTerrainFill.cs` — 多边形地形铺设。`ApplyPolygonTerrain`（自己六边形内+边格→非void，六边形外→铺 RimExodus_Void 并清除实体/Pawn）。
- `1.6/Defs/TerrainDefs/VoidTerrain.xml` — `RimExodus_Void` 虚空地形 Def。

### 重写的源码文件
- `MapParent_SeamlessTile.cs` — NeighborLink/MapParent 数据模型（direction→edgeAngle+worldTile）。
- `SeamlessTileGraph.cs` — 邻居查询（worldTile 主键，移除 OppositeDirection）。
- `SeamlessTileManager.cs` — `ComputeNeighborOffset`（内切圆边中点 + `SeamOverlap` 收缩）、`GenerateTileMap(sourceWorldTile, newWorldTile, mapSize)`、`PlaceEnterSpotsAllNeighbors`（阶段4a：沿全部世界邻居边 Bresenham 划线预铺单端 spot）、`RefreshMapVoid`（可重复铺 void，锚点+口袋都适用）、`TrySetupOnStart`（阶段4a 取代 `TryAutoGenerateFirstNeighbor`）、`EnsureNeighborRegistered`/`AutoConnectWorldNeighbors`（多跳间隙补登记）。
- `GenStep_SeamlessTile.cs` — 调 `ApplyPolygonTerrain` 挖虚空（阶段4a 修复2 已移除全铺 Soil，真实地形由原版 Terrain genStep 铺）。
- `SeamlessTileRegistry.cs` — `TryGetOwnerNeighbor` 改为点在凸多边形内判定（`ContainsPoint`）。
- `DebugActions_SeamlessTile.cs` — Dev 命令改为"Generate All Seamless Neighbors"/"Remove All Tile Maps"。

### 虚空地形（RimExodus_Void）
- 参考 VMF `VMF_ImpassableFloor` + 官方 `Space`（Odyssey）。独立顶级 TerrainDef（不继承 NaturalTerrainBase，避免太空边框贴图污染）。
- **不可通行保证**（经源码调研，无需额外 Harmony patch）：
  - `passability=Impassable`：`PathGrid.CalculatedCostAt` 返回 10000，寻路/Region/JumpUtility.ValidJumpTarget 全拒绝。
  - **不设 `forcePassableByFlyingPawns`**（保持默认 false）：Flying PathGrid 也判不可通行，堵飞行漏洞（`PathGrid.cs:125`）。
  - `fertility=0`：不生植物。空 `affordances`：不可建造。`changeable=false`+`layerable=false`：不可覆盖。`dontRender=true`：透明（消除重叠带视觉冲突，无需渲染归属裁剪）。

### void 铺设规则（务必只看自己六边形）
- **判定**：格在自己六边形内（`ContainsPoint`，含边）**或** 是六边形某条边的 Bresenham 边格 → 非 void；否则 → void。
- **边格强制非 void**：传送点铺在边上，边格必须可站立。`ApplyPolygonTerrain` 先收集所有边的 Bresenham 格到 `edgeCells` HashSet，铺 void 时跳过这些格。
- **不看邻居**：void 完全由自己的六边形决定。曾经错误地把"邻居六边形覆盖区"判为非 void，导致 A 地图外部全是非 void、没有 void 出现——已纠正。
- **void 格实体清除**（`ClearThingsOnCells`）：铺 void 前清除该格上的建筑/岩石/植物/物品（`Destroy(Vanish)`，`destroyable=false` 如 SteamGeyser 改 `DeSpawn`）；铺 void 后把生成在 void 格上的 Pawn 径向移到最近可通行格（`EvacuatePawnsOnCells`，找不到则销毁非人类 Pawn）。
- **锚点 A 的 void 铺设**：A 是原生 Map 不走 RimExodus GenStep。`GenerateTileMap` 在邻居登记后调 `RefreshMapVoid(map)` 为源地块（含 A）铺 void。`RefreshMapVoid` 可重复调用（每次生成新邻居后刷新，幂等）。

### 关键实现要点
- **offset 对称性**：A→B 与 B→A 各自从自己的多边形边中点算，理论上 = -(对端)，但凑整可能 ±1 误差（待游戏内验证）。
- **传送点 Bresenham**：沿多边形边（顶点 j→j+1）整数 Bresenham 划线，线经过的每个格放一对传送点。源端+对端均须 Walkable（边格保证两端非 void）。少数边界格仍可能因自然地形不可通行被跳过（游戏内实测 103 候选 placed 94，9 个被自然地形/边角跳过）。

### 已游戏内验证（阶段3核心闭环）
- 自动生成首个世界邻居、多边形虚空裁切（void 出现在六边形外）、传送点满铺（94 对）。
- 点击邻居渲染区域正确解析归属、pawn 跨图转移（Rachel + 野生动物均成功）、自动聚焦切图。
- 无 `placed=0`、无 `destroy non-destroyable`、无 `on unwalkable cell` 报错。

### 待办
- offset 凑整对称性精确验证（B→C 多跳场景）。
- 五边形地块（12 个特殊 tile）游戏内验证。
- 未来：patch 禁止"地势开阔"等影响地块地图长宽的地标，注明与硬改地块地图生成的 mod 不兼容。
- 连续地形（阶段4）依赖本阶段稳定的多边形边界几何。
- **阶段4 必须实现边界带不可建造约束**：patch `GenConstruct.CanPlaceBlueprintAt`（Prefix），禁止玩家在多边形边内侧 `SeamOverlap+1`(=3) 格内建造。**这是阶段3"卡 void 不会发生"论证的前提**——传送点只在对侧可达时才被 goto 激活，但玩家可用建筑改变寻路打破此前提，把 pawn 困在 void 一侧。详细设计见主文档阶段4"边界带不可建造约束"节。

## 阶段4a：邻居预加载 + 多跳传送点（已完成，可编译，游戏内待验证）

实现"pawn 接近边界可配置格数时预加载对应真实邻居地图"，并重构传送点为"预铺+延迟绑定"模式天然支持多跳。核心是**事件驱动**（非轮询）+ **凸多边形内缩算法**（构建边界带速查表）+ **传送点解耦**（铺设与绑定分离）。

### 用户确认的关键设计决策
- **阈值配置**：ModSettings 可调（`borderPreloadDistance` 默认 15 格 + `preloadAllNeighborsOnStart` 默认 false）。
- **事件驱动**：Hook `Pawn_JobTracker.StartJob`，**仅 `playerForced==true` 的 Goto** 触发边界检测。避免动物级联加载（A 的动物触发 B，B 的动物又触发 C）。
- **边界带构建算法**：**算法 C（凸多边形内缩 + 扫描线差集）**——原多边形减去内缩多边形 = 距边 ≤ 阈值的环形带。复用现成 `ScanlineFill`，正确性最强（精确几何定义），性能最优（比朴素逐格法快约 100 倍）。
- **存储结构**：`Dictionary<IntVec3,int>`（边界带格 → 最近边对应的邻居 worldTile）。考虑未来高频查询（如撤退袭击者批量检查），O(1) 查询。
- **传送点模式**：**预铺+延迟绑定**——地图生成时沿全部世界邻居边铺单端 spot（`CounterpartSpot=null` + `targetWorldTile` 标记），邻居加载后按 `targetWorldTile` 匹配 + 坐标平移校验互绑。
- **防重入**：`generatingTiles` HashSet 记录生成中的 worldTile，生成中拒绝重复请求。

### 传送点预铺+延迟绑定（核心架构转变）
- **旧模式（阶段3）**：`PlaceEnterSpots` 在 `GenerateTileMap` 时只铺 `source↔new` 一对，硬编码 hostOffset。不支持多跳（新地块与其他已存在邻居不补铺）。
- **新模式（阶段4a）**：
  - `PlaceEnterSpotsAllNeighbors(map, worldTile)`：沿地图**全部世界邻居边**（5/6 条）铺单端 spot。每个 spot 记 `targetWorldTile = 该边对应的世界邻居 tile`（用 `GetTileNeighbors` 顺序与多边形顶点一致的不变量），`CounterpartSpot = null`。幂等（查重同位置同 def spot）。
  - `BindNewTileWithExistingNeighbors(newMap, newWorldTile)`：新地块加载后，遍历其所有已存在邻居，对每个调 `SeamlessEnterSpotBinder.BindUnboundSpotsBetween`，按 `targetWorldTile` 匹配两端 spot 且坐标平移校验（`expectedCellB = cellA - offsetAtoB`，两端世界坐标重合契约）互绑 `CounterpartSpot`。
  - **多跳天然支持**：新地块 C 加载后，`BindNewTileWithExistingNeighbors` 遍历 C 的所有已存在邻居（含 B），逐一绑定。世界网格上 B↔C 是邻居且 B 已加载时，加载 C 自动绑定 B↔C 传送点。
- **Comp 扩展**：`CompSeamlessTileEnterSpot` 加 `public int targetWorldTile = -1`（持久化），作为延迟绑定的匹配标记。

### 边界带速查表（算法 C：凸多边形内缩）
- `SeamlessPolygonGeometry.InsetPolygon(verts, insetDist)`：凸多边形各边沿内法向（朝中心）平移 insetDist，求相邻内缩边交点得内缩多边形。凸性保证内缩后仍凸。insetDist ≥ apothem 时返回空（整图都是边界带）。
- `SeamlessPolygonGeometry.ComputeEdgeBand(verts, mapSize, bandWidth, neighborWorldTiles, result)`：
  1. `innerVerts = InsetPolygon(verts, bandWidth)`
  2. 两次 `ScanlineFill`：原多边形每行 [a,b]，内缩多边形每行 [c,d]
  3. 每行差集：边界带 = [a, c-1] ∪ [d+1, b]
  4. 边界带内每个格：算到 6 条原边的最近距离（`DistanceToEdge`），取最近边对应的 `neighborWorldTiles[edgeIdx]`
- `SeamlessPolygonGeometry.DistanceToEdge(p, v0, v1)`：点到线段距离（解析，投影 + Clamp01）。
- **性能**：S=250,N=15 约 4000 次浮点运算 + 边界带格×6 距离计算（约 9 万次一次性构建），构建后查询 O(1)。

### 事件驱动预加载流程
1. 玩家右键 pawn 移动 → `FloatMenuOptionProvider_DraftedMove` 产生 Goto job（`playerForced=true`）
2. `Pawn_JobTracker.StartJob` → `Patch_Pawn_JobTracker_StartJob.Prefix`
3. `SeamlessBorderPreloader.CheckPawnGoto(pawn, targetCell)`：
   - `map.GetComponent<SeamlessBorderLookup>().TryGetPreloadTarget(targetCell, out worldTile)`（O(1) 数组查）
   - 若在边界带内且对应 worldTile 邻居未加载 → `SeamlessTilePreloader.TryPreload(map, worldTile)`
4. `SeamlessTilePreloader.TryPreload` → `sourceMap.GetComponent<SeamlessTileManager>().TryPreloadNeighbor(worldTile)`（支持从口袋地块 B 发起，生成 C）
5. `TryPreloadNeighbor`：防重入检查 → 去重检查 → `generatingTiles.Add` → `GenerateTileMap` → `generatingTiles.Remove`（try/finally）

### 防递归与防级联
- **动物级联加载规避**：仅 `playerForced==true` 的 Goto 触发。动物/自动寻路（巡逻/工作/放牧）的 Goto 不设 playerForced，不触发预加载。这是关键——若所有 Goto 都触发，A 地图边界附近的动物会触发 B 加载，B 的动物又触发 C 加载……
- **防重入**：`generatingTiles` HashSet 在 `TryPreloadNeighbor` 入口检查，生成中拒绝同一 worldTile 的再次请求。try/finally 保证异常时也移除。
- **去重**：`SeamlessTileGraph.TryGetNeighborLinkByWorldTile` 检查目标 worldTile 是否已是邻居（已加载则跳过）。
- **MapGenerated 级联**：仅锚点地图 A 触发开档初始化（`if (map.IsPocketMap) return;`）。口袋地块的邻居生成由玩家指令或 Dev 命令驱动。

### 开档行为
- `TrySetupOnStart`（取代旧的 `TryAutoGenerateFirstNeighbor`）：
  1. `PlaceEnterSpotsAllNeighbors(map, map.Tile)`：锚点 A 沿全部世界邻居边预铺单端 spot（对端 null）
  2. 若 `RimExodusMod.Settings.preloadAllNeighborsOnStart`：遍历所有世界邻居调 `TryPreloadNeighbor`（高配玩家开档即加载全部，流畅体验）
  3. 否则：不生成邻居，等 pawn 接近边界时事件驱动加载

### Trigger null 保护
- `SeamlessMapTransferTrigger.CheckLocalEnterSpots` 加 `if (comp.CounterpartSpot == null) continue;`：预铺未绑定的 spot（对端邻居尚未加载）被踩时不 NRE、不触发转移。待邻居加载绑定后自动生效。

### RimExodusMod 改造
- 从 `static class`（`[StaticConstructorOnStartup]`）改为继承 `Mod` 的实例类。
- 持 `public static RimExodusSettings Settings`（`GetSettings<RimExodusSettings>()` 自动持久化）。
- `SettingsCategory()` 返回 "RimExodus" 使其出现在游戏内 Mod 设置菜单。
- Harmony PatchAll 移到 Mod 构造器（时序早于 StaticConstructorOnStartup，此时 Def 已加载）。

### 新增源码文件
- `Source/RimExodusSettings.cs` — ModSettings（`borderPreloadDistance` + `preloadAllNeighborsOnStart`）。
- `Source/SeamlessBorderLookup.cs` — 边界带速查表 MapComponent（`Dictionary<IntVec3,int>`，O(1) 查询，延迟 1 tick 构建）。
- `Source/SeamlessEnterSpotBinder.cs` — 延迟绑定工具（`BindUnboundSpotsBetween`，按 targetWorldTile + 坐标校验互绑）。
- `Source/SeamlessTilePreloader.cs` — 预加载静态入口（屏蔽锚点/口袋差异，取源地图 Manager 调 TryPreloadNeighbor）。
- `Source/SeamlessBorderPreloader.cs` — 边界检测器（`CheckPawnGoto`，被 Patches_Job 调用）。

### 修改的源码文件
- `RimExodusMod.cs` — static class → Mod 实例 + Settings。
- `CompSeamlessTileEnterSpot.cs` — 加 `targetWorldTile` 字段（持久化）。
- `SeamlessPolygonGeometry.cs` — 加 `InsetPolygon`/`ComputeEdgeBand`/`DistanceToEdge`/`LineLineIntersection`/`PolygonArea`/`AddBandRowCells`。
- `SeamlessTileManager.cs` — 移除旧 `PlaceEnterSpots`，加 `PlaceEnterSpotsAllNeighbors`/`BindNewTileWithExistingNeighbors`/`TryPreloadNeighbor`/`generatingTiles`；`TryAutoGenerateFirstNeighbor`→`TrySetupOnStart`；`GenerateTileMap` 改用新铺点+绑定逻辑。
- `Patches_Job.cs` — Prefix 追加 playerForced Goto 边界检测（不拦截 Job，仅触发副作用）。
- `SeamlessMapTransferTrigger.cs` — `CheckLocalEnterSpots` 加 CounterpartSpot null 保护。

### 关键不变量
- **边 ↔ 邻居顺序**：`GetTileNeighbors(worldTile)` 返回顺序与多边形顶点环绕顺序一致（边 j ↔ 邻居 j ↔ 顶点 j→j+1）。这是 `PlaceEnterSpotsAllNeighbors`（边 j 铺 spot 标记 `neighbors[j].tileId`）和 `ComputeEdgeBand`（格最近边 j → `neighborWorldTiles[j]`）正确性的根基。
- **offset 契约**：`BindUnboundSpotsBetween` 参数 `cellAMinusCellB`（= cellA - cellB）满足 `cellB = cellA - cellAMinusCellB`。`BindNewTileWithExistingNeighbors` 从 newMap 查邻居得 `info.offset`（满足 NeighborLink 契约 `cellNeighbor + info.offset = cellNew`），以 newMap 为 A、neighbor 为 B，`cellAMinusCellB = cellNew - cellNeighbor = info.offset` 正确。
- **凸多边形内缩正确性**：算法 C 利用凸性，内缩后仍凸，扫描线差集天然正确。N=15 << apothem=125，无退化风险。

### 待办
- 游戏内验证：开档预铺 spot 数、玩家右键接近边界触发预加载、多跳绑定、动物不触发、存档读档保留。
- offset 凑整对称性在多跳绑定的精确验证（`expectedCellB` 是否精确匹配）。
- 未来：为撤退袭击者等 AI 场景加 patch（目前仅 playerForced 触发）。
- 未来：边界带速查表在 ModSettings 阈值改变后的重建机制（当前仅开档构建一次）。

## 阶段4a 游戏内验证后的五项修复（已完成，可编译）

游戏内测试暴露 5 个问题，全部修复。编译通过（0 错误 0 警告）。

### 修复1：锚点 A 的 void 铺设丢失（最严重，阶段4a 初始 bug）
- **根因**：阶段3 锚点 A 的 void 依赖 `GenerateTileMap` 内的 `RefreshMapVoid(map)` 顺带触发（生成第一个邻居时）。阶段4a 把开档逻辑改成 `TrySetupOnStart`（默认不生成邻居），导致 `RefreshMapVoid(map)` 从未对锚点 A 调用 → A 没 void → 看起来是完整原版地图。
- **修复**：`TrySetupOnStart` 开头加 `RefreshMapVoid(map)`（独立于邻居生成）。

### 修复2：邻接地图用真实地形（而非写死 Soil）
- **根因**：`GenStep_SeamlessTile` 先全铺 Soil 再挖 void；`MapGeneratorDef` 只有这一个 genStep，没有原版地形 genStep（ElevationFertility/Terrain/RocksFromGrid/Plants）。口袋地图无真实地形。
- **修复**：
  - `SeamlessTileGenerator.xml` 的 `genSteps` 加入原版地形序列（ElevationFertility/RocksFromGrid/Terrain/Plants/Animals/Fog 等）；`RimExodus_SeamlessTile` 的 order 调到 1000（在 Terrain 之后挖 void）。
  - `GenStep_SeamlessTile` 移除全铺 Soil 逻辑（原版 Terrain genStep 已铺真实地形），只保留 `ApplyPolygonTerrain`。
  - `GenerateTileMap` 用 `MapGenerator.GenerateMap` 的 `extraInitBeforeContentGen` 回调调 `InjectRealTileInfo(generatedMap, newWorldTile)`，从 `Find.WorldGrid[worldTile]` 读真实 biome/hilliness/elevation/rainfall/temperature/swampiness/pollution 注入到 `map.pocketTileInfo`（pocket 地图的 TileInfo 来源，`Map.cs:386-398` pocket 直接返回 pocketTileInfo，不读 parent.Tile）。
  - **关键**：`pocketMapProperties` 只有 biome/temperature/tileMutators 字段（无 hilliness），不能写死；必须用回调注入 pocketTileInfo 的全部字段。

### 修复3：void 渲染红色（BadGraphic）—— 已回退（见第二轮修复1）
- **根因**：`dontRender=true` 时 `SectionLayer_Terrain` 用 ShadowMask（透明），但某些边/材质路径可能读 `terrainDef.graphic`（默认 BadGraphic=粉色）。
- **原修复（已回退）**：曾给 `VoidTerrain.xml` 加 `texturePath=Misc/ShadowMask` + `edgeType=Hard`。第二轮验证发现红色残影的真正根因是 `SeamlessTileRenderer` CommandBuffer 未清色缓冲（移动相机时上一帧像素残留叠加），与 TerrainDef 无关。texturePath/edgeType 已回退（当前 `VoidTerrain.xml` 无此字段），红色残影改由 `SeamlessTileRenderer` 的 `ClearRenderTarget(true, true)` 解决（见第二轮修复1）。

### 修复4：重复生成同一 worldTile 的地图（多跳间隙）
- **根因**：`TryPreloadNeighbor`/`GenerateTileMap` 的去重只查"直接邻居表"（`TryGetNeighborLinkByWorldTile`）。但 worldTile 63290（A）的地图已存在（map 0），从 C（map 2）发起预加载时，C 的邻居表里没有 A（A 和 C 隔了 B），查不到 → 重复生成 map 3。
- **修复**：
  - `SeamlessTileGraph.TryGetMapByWorldTile(worldTile, out Map)`：全局遍历 `Find.Maps`（含锚点 + 所有口袋）查某 worldTile 是否已有任意地图。用 `SeamlessTileRegistry.GetMapWorldTile` 统一取 worldTile（锚点 map.Tile，口袋 MapParent_SeamlessTile.worldTile）。
  - `GenerateTileMap` 去重检查改用 `TryGetMapByWorldTile`（全局），发现已有地图则调 `EnsureNeighborRegistered` 补登记直接邻居关系（多跳间隙场景：C↔A 隔着 B，A 已存在但与 C 无直接邻居关系）。
  - `EnsureNeighborRegistered`：若已是直接邻居则跳过；否则算 offset + `RegisterNeighborBidirectional` + 补铺两端传送点 + `RefreshMapVoid` + `BindUnboundSpotsBetween`。
  - `RegisterNeighborBidirectional` 泛化为接受 `MapParent` 基类（不限 `MapParent_SeamlessTile`），支持任意组合（锚点-口袋、口袋-口袋、口袋-锚点）。新增 `SetNeighborOnMap` 统一存储位置（锚点存 Manager.neighbors，口袋存 MapParent_SeamlessTile.neighbors）。
  - `ComputeNeighborOffsetStatic`：静态包装，供 `EnsureNeighborRegistered` 复用。

### 修复5：void 寻路表现（以 void 为目标可寻路到最近可达格）
- **根因**：`SeamlessTileRegistry.TryGetOwnerNeighbor` 把"落在已加载邻居多边形内的 void 格"判为该邻居领地（void 在六边形外，几何上可能落在邻居多边形内）。导致右键这类 void 格被 `Patches_FloatMenuMakerMap` 当作跨地图指令，绕过原版"移动到最近可达格"逻辑。
- **修复**：`TryGetOwnerNeighbor` 开头加 void 短路——`currentCell` 上的地形是 `RimExodus_Void` 时直接返回 false（归当前地图）。原版 `FloatMenuOptionProvider_DraftedMove` 的 `StandableCellNear` + `BestOrderedGotoDestNear`（30 格半径找可达格）自然生效，void 表现得像不可通行建筑。

### 异步加载（延迟队列）
- **问题**：`MapGenerator.GenerateMap` 是同步重操作，在 `StartJob` Prefix 内直接调用会阻塞当前 tick 数百毫秒（游戏卡顿）。
- **修复**：`SeamlessBorderPreloader.CheckPawnGoto` 不再同步调 `TryPreload`，改为 `SeamlessTilePreloader.QueuePreload(map, worldTile)` 登记到队列。`SeamlessTileManager.MapComponentTick`（锚点地图）每 tick 调 `SeamlessTilePreloader.ConsumeQueued()` 消费队列。生成在下一 tick 执行，不阻塞玩家输入响应。队列幂等（同 (sourceMap, targetWorldTile) 重复入队只保留一条）。

### 诊断日志（临时，用于验证）
- `Patches_Job`：每次 `playerForced==true` 的 Goto 记录 `[RimExodus] StartJob Goto playerForced by {pawn} -> {cell}`。
- `SeamlessBorderPreloader.CheckPawnGoto`：not in border band / already loaded / queuing preload 三种情况分别记录。
- `InjectRealTileInfo`：记录注入的 biome/hilliness/elevation/rainfall。

### 关键文件变更
- `SeamlessTileGraph.cs` — 加 `TryGetMapByWorldTile`（全局 worldTile 查询）。
- `SeamlessTileManager.cs` — `EnsureNeighborRegistered`/`InjectRealTileInfo`/`ComputeNeighborOffsetStatic`/`SetNeighborOnMap`；`RegisterNeighborBidirectional` 泛化；`GenerateTileMap` 去重改全局 + 调 EnsureNeighborRegistered + extraInitBeforeContentGen；`TrySetupOnStart` 加 RefreshMapVoid；MapComponentTick 消费预加载队列。
- `GenStep_SeamlessTile.cs` — 移除全铺 Soil，只保留 ApplyPolygonTerrain。
- `SeamlessTileGenerator.xml` — genSteps 加原版地形序列，RimExodus_SeamlessTile order=1000。
- `VoidTerrain.xml` — 加 texturePath/edgeType。
- `SeamlessTilePreloader.cs` — 加 QueuePreload/ConsumeQueued 延迟队列。
- `SeamlessBorderPreloader.cs` — CheckPawnGoto 改用 QueuePreload + 诊断日志。
- `Patches_Job.cs` — 诊断日志。
- `SeamlessTileRegistry.cs` — TryGetOwnerNeighbor 加 void 短路。

## 阶段4a 第二轮游戏内验证后的修复（已完成，可编译）

第二轮测试暴露 4 个问题 + 1 个额外问题，全部修复。

### 修复1（回退+重做）：void 残影叠加红色
- **原错误修复**：给 VoidTerrain.xml 加 texturePath（误判为 BadGraphic）→ 已回退。
- **真实根因**：`SeamlessTileRenderer` 的 CommandBuffer 开头不清色缓冲（`clearColor=false`）。void 格用 ShadowMask 材质（半透明乘法，不写不透明色），移动相机时上一帧的正常地形像素残留在色缓冲，多次叠加压暗成红色。
- **修复**：CommandBuffer 开头加 `ClearRenderTarget(true, true, Color.clear, 1f)`（清色+深度），邻居 mesh 画在干净背景上，void 区域变透明黑，当前地图随后覆盖。

### 修复3（异步加载）：分阶段异步线程生成地图
- **原错误修复**：延迟到下一 tick → 仍卡那一 tick。
- **深入分析**：`MapGenerator.GenerateMap` 的耗时部分（genSteps）是纯数据计算，不碰 Unity API。`FinalizeInit` 内的 `mapDrawer.RegenerateEverythingNow`（Unity Mesh）已由原版用 `LongEventHandler.ExecuteWhenFinished` 推迟到主线程。所以异步可行。
- **修复**：`TryPreloadNeighbor` 用 `LongEventHandler.QueueLongEvent(action, "GeneratingMap", doAsynchronously: true, null)` 把整个 `GenerateTileMap` 放到独立线程。`doAsynchronously=true` 时原版 `new Thread` 跑 action（`LongEventHandler.cs:368`），显示"Generating map"进度画面。`FinalizeInit` 注册的 Mesh 重建在线程结束后的主线程执行（`ExecuteToExecuteWhenFinished`）。`generatingTiles` 锁跨 tick 保持，在回调末尾 `ClearGeneratingTile` 清理。

### 修复5（回退+重做）：跨地图寻路失败
- **原错误修复**：`TryGetOwnerNeighbor` 加 void 短路 → 破坏了正常的跨地图点击归属 → 已回退。
- **真实根因**：聚焦 A 时命令 A 的 pawn 前往 A 显示的 B 区域，原版 `FloatMenuOptionProvider_DraftedMove.PawnCanGoto` 用 `pawn.CanReach`（`pawn.Map`=A）对 B 的 cell 做可达性检查，必然失败（目标在另一张地图），菜单选项被禁用（action=null）。
- **修复**：`Patches_FloatMenuMakerMap.InjectCrossMapGotoOption`——在原版 provider 生成选项后，检测跨图 pawn，移除被禁用的 Goto 选项，注入自定义 Goto 选项（action 直接 `TryTakeOrderedJob(Goto)`，由 `Patches_Job.TryInterceptJob` 桥接到传送点，绕过原版 CanReach）。

### 额外问题修复：地图缝隙和 tile 缺失
- **根因**：`ApplyPolygonTerrain` 用 `ContainsPoint`（叉积）逐格判定 void，而区域填充用 `ScanlineFill`（扫描线），两者在边附近/顶点行的边界判定语义不一致（±1 格差异），产生孤立 void 格切碎可通行区域（`numDistrict=0`）。
- **修复**：`ApplyPolygonTerrain` 改用 `ScanlineFill` 的区间结果决定非 void（`innerCells` HashSet），保证铺设与填充用同一套判定。边格（`EnumerateEdgeCells` Bresenham）仍强制非 void。

### 关键文件变更（第二轮）
- `SeamlessTileRenderer.cs` — CommandBuffer 开头加 ClearRenderTarget(clearColor=true)。
- `SeamlessTileManager.cs` — `TryPreloadNeighbor` 改用 `LongEventHandler.QueueLongEvent(doAsynchronously:true)`；加 `ClearGeneratingTile`。
- `Patches_FloatMenuMakerMap.cs` — 加 `InjectCrossMapGotoOption`（跨图 Goto 选项注入）；加 `using Verse.AI`。
- `SeamlessTerrainFill.cs` — `ApplyPolygonTerrain` 改用 `ScanlineFill` 替代 `ContainsPoint`。
- `VoidTerrain.xml` — 回退 texturePath/edgeType（仅注释更新）。

## 阶段4a 第三轮游戏内验证后的修复（已完成，可编译）

第三轮测试暴露 2 个问题，全部修复。

### 修复（问题3重做）：纯 Thread 异步，不中断玩家操作
- **原方案缺陷**：`LongEventHandler.QueueLongEvent(doAsynchronously:true)` 仍会 `ForcePause` + 显示进度画面，中断玩家操作。
- **修复尝试**：改用纯 `System.Threading.Thread` + `Patch_Game_AddMap` 推迟工作线程的 AddMap 到主线程。
- **回退（致命）**：`MapGenerator.GenerateMap` 内部顺序是 `AddMap`（:146）→ genSteps（:186）。推迟 AddMap 后，工作线程的 genSteps（如 `GenStep_RockChunks`）执行时 map 尚未 AddMap（`map.Index == -1`），`Thing.SpawnSetup` 检查 `map.Index < 0` 报 "map does not exist"，所有 Thing 生成失败（`Filth_RubbleRock`/`ChunkSandstone` 等海量错误）。**genSteps 依赖 map 已 AddMap**，不能推迟。
- **最终方案**：回退到 `LongEventHandler.QueueLongEvent(doAsynchronously:true)`，接受进度画面（`ForcePause`）。这是 RimWorld 原生安全机制（VMF 同类做法），生成只需几秒。纯后台异步不可行（AddMap/genSteps 时序依赖）。

### 修复（新额外问题）：C 加载后自动显示间接已加载邻居（如西方的 A）
- **根因**：从 A→B→C 路径生成 C 时，C 只与直接生成源 B 建立邻居关系。C↔A（世界网格上相邻但 A 通过 B 间接连接）的邻居关系只在 pawn 接近 C 的 A 方向边界时（`CheckPawnGoto`→`EnsureNeighborRegistered`）才建立，导致 C 加载后看不到已存在的 A，需 pawn 走过去才触发。
- **修复**：`GenerateTileMap` 末尾新增 `AutoConnectWorldNeighbors(interiorMap, newWorldTile)`——遍历新地块的世界邻居列表（`GetTileNeighbors`），对每个已加载（`TryGetMapByWorldTile` 命中）但未建立直接邻居关系的地图调 `EnsureNeighborRegistered`。使新地块加载时自动与所有相邻已加载地块连接，渲染/寻路即用。
- **幂等**：`EnsureNeighborRegistered` 检查已是直接邻居则跳过。

### 关键文件变更（第三轮）
- `SeamlessTileManager.cs` — `TryPreloadNeighbor` 用 `LongEventHandler.QueueLongEvent(doAsynchronously:true)` 异步生成；加 `AutoConnectWorldNeighbors`（新地块加载时自动连接所有已加载世界邻居）；`ClearGeneratingTile`（回调清理锁）。
- `Patch_Game_AddMap.cs` — **已删除**（纯 Thread 推迟 AddMap 方案回退，genSteps 依赖 map.Index）。

## 阶段4a 第四轮：拆分式异步（不阻塞主线程，已完成，可编译）

第三轮的 LongEventHandler 方案虽安全但会显示进度画面中断操作。第四轮实现真正的拆分式异步。

### 拆分 MapGenerator.GenerateMap 流程
`MapGenerator.GenerateMap` 内部顺序：`:146 AddMap` → `:186 genSteps` → `:190 FinalizeInit`。
- **约束1**：AddMap 必须在主线程（`List<Map>.Add` 与主线程 foreach `Find.Maps` 的 `_version` 校验冲突，Alert/ColonistBar 每帧 foreach）。
- **约束2**：AddMap 必须在 genSteps 之前（genSteps 的 `GenSpawn.Spawn` → `Thing.SpawnSetup` 检查 `map.Index`，由 AddMap 设置；推迟 AddMap 导致 "map does not exist"）。
- **结论（后被推翻）**：尝试拆分为"主线程 AddMap（快）→ 工作线程 genSteps（慢）→ 主线程 FinalizeInit（快）"。

### 三阶段实现（已回退，见第六轮）
- 阶段1（主线程）：创建 Map + AddMap。
- 阶段2（工作线程）：genSteps。map 已在 Find.Maps 但未 FinalizeInit。
- 阶段3（主线程）：FinalizeInit + 配置。

### 反射访问 private 成员（已回退）
- `MapGenerator.ClearWorkingData`（private static）→ 反射调用。
- `Find.Scenario.parts`（private）→ `Find.Scenario.AllParts`（public）。

## 阶段4a 第五轮：拆分式异步验证 + 弹跳诊断（进行中）

### 问题3（进度画面）—— 已在源码移除 LongEventHandler（后被第六轮回退）
第四轮已把 `TryPreloadNeighbor` 改用 `BeginTileMapGeneration`（纯 `new Thread`，无 LongEventHandler）。
用户日志显示旧 DLL（堆栈含 LongEventHandler lambda）。确认新 DLL 不含 LongEventHandler。
**用户需重启游戏加载新编译的 DLL。**

### 偶现问题（三地图交点弹跳）—— 加诊断日志
现象：A↔B↔C 三地图接缝交点附近，pawn 在 map 0 ↔ map 2 之间反复 transfer。
诊断：`CheckLocalEnterSpots` 的 trigger 日志加锁状态标注（`was-locked`/`not-locked`）。
**待用户复现并提供带锁状态的日志。**

### 新额外问题（C 自动显示 A）—— 已解决
第四轮的 `AutoConnectWorldNeighbors` 生效。

## 阶段4a 第六轮：回退拆分式异步（RegionAndRoomUpdater 致命缺陷，已完成，可编译）

### 回退原因：RegionAndRoomUpdater 缺失
拆分式异步（阶段1 AddMap → 阶段2 工作线程 genSteps → 阶段3 主线程 FinalizeInit）在阶段2 期间，map 已在 `Find.Maps` 但 `FinalizeInit`（含 `regionAndRoomUpdater.Enabled = true` + `RebuildAllRegionsAndRooms`）尚未执行。主线程每帧 tick 这个 map（`TempTerrainManager.Tick` → `GetRoom` → `RegionGrid.GetValidRegionAt`），但 region 网格全空（`RegionAndRoomUpdater` disabled），导致：
- 海量 `Trying to get valid region at (...) but RegionAndRoomUpdater is disabled` 警告（每帧每个水格）。
- 寻路失效（pawn 卡接缝，`RandomAnimalSpawnCell_MapGen: numDistrict=0`）。
- 这不是"中间状态可接受"的小问题，而是让 map 的 tick/寻路完全崩溃。

### 根本结论：拆分式异步在当前引擎架构下不可行
`AddMap → genSteps → FinalizeInit` 必须在主线程恢复 tick 前连续完成：
- AddMap 让 map 进入 `Find.Maps`（主线程开始 tick 它）。
- genSteps 依赖 `map.Index`（AddMap 设置）。
- FinalizeInit 初始化 region/pathing/powerNet（主线程 tick 依赖）。
- 三者之间不能有"主线程 tick 半成品 map"的窗口。

### 最终方案：LongEventHandler（doAsynchronously）
`TryPreloadNeighbor` 用 `LongEventHandler.QueueLongEvent(action, "GeneratingMap", doAsynchronously: true, null)`。整个 `GenerateTileMap`（AddMap+genSteps+FinalizeInit+配置）在 LongEventHandler 的独立线程连续完成，主线程期间显示进度画面（ForcePause 几秒）。
- **接受进度画面**：这是 RimWorld 原生安全机制（VMF 同类做法）。地图生成的时序依赖（AddMap→genSteps→FinalizeInit）决定了无法做到完全不中断主线程。
- 纯后台异步（不中断操作）需要分帧执行 genSteps 或重写 MapGenerator，工作量极大且无先例，当前阶段不做。

### 清理
- 删除拆分式异步全部代码：`BeginTileMapGeneration`/`GenerateMapPhase1`/`Phase2WorkerProc`/`FinalizePendingTileMap`/`ClearMapGeneratorWorkingData`/`IsValidBiomeGenStep`/`GetGenStepParms`/跨阶段 static 槽/`MapComponentTick` 的 phase2Completed 检测。
- 移除 `using System.Linq/System.Reflection/HarmonyLib`（不再需要）。
- 保留 `AutoConnectWorldNeighbors`（C 自动显示 A）和 `ClearGeneratingTile`（回调清理锁）。

### 关键文件变更（第六轮）
- `SeamlessTileManager.cs` — `TryPreloadNeighbor` 回退用 `LongEventHandler.QueueLongEvent(doAsynchronously:true)`；删除全部拆分式代码；移除多余 using。

## 阶段4a 第七轮：不连续 void 修复（已完成，可编译）

### 问题：void 孤岛（edge 内部出现 void 格）
- **现象**：`numDistrict=0`（六边形内可通行区域被切碎，无法形成 region），`RandomAnimalSpawnCell_MapGen failed`，`BindUnboundSpotsBetween` 绑定数偏低（28 vs 正常 56-80）。
- **根因**：第六轮（及之前第二轮）的 `ApplyPolygonTerrain` 用 `ScanlineFill` 区间判定非 void，但 `ScanlineFill` 的浮点交点离散化（`CeilToInt`/`FloorToInt`）与 `EnumerateEdgeCells` 的 Bresenham 整数顶点划线**不一致**——两套边界表示在边附近产生缝隙（既不在 ScanlineFill 区间、也不在 Bresenham 边格上的格 → void 孤岛）。
- **修复**：
  - `ApplyPolygonTerrain` 回退用逐格 `ContainsPoint`（精确叉积判定）+ 边格补丁（Bresenham 强制非 void）。
  - 新增 `SeamlessPolygonGeometry.IsCellInPolygon`：`ContainsPoint` 判定在内 **或** 格中心到最近边距离 < 0.71 格（对角线半长，确保格面积有部分在内）。这覆盖了"格中心恰好在边外侧但格面积大部分在内"的边界格，消除 void 孤岛。
  - `ApplyPolygonTerrain` 用 `IsCellInPolygon` 替代 `ContainsPoint`。

### 关键文件变更（第七轮）
- `SeamlessPolygonGeometry.cs` — 新增 `IsCellInPolygon`（ContainsPoint + 点到边距离膨胀判定）。
- `SeamlessTerrainFill.cs` — `ApplyPolygonTerrain` 回退逐格判定，用 `IsCellInPolygon`。

## 阶段4a 全面重构（已完成，可编译）

系统性解决 void 孤岛、加载延迟、代码债务。编译通过（0 错误 0 警告）。

### 第1步：void 边界判定彻底重设计（格角检测）
- **根因**：多套边界判定不一致（ContainsPoint 叉积 + Bresenham RoundToInt 圆整），边附近产生 void 孤岛切断 region（numDistrict=0）。
- **修复**：`IsCellInPolygon` 改为格角检测——格中心在多边形内 **或** 格的 4 个角中任一在多边形内 → 非 void。凸性保证外部格4角都不在内，内部格（含边附近）至少一个角在内，消除所有孤岛。
- 新增 `ContainsPointAt(verts, px, py)` 接受任意点坐标（供角检测复用）。
- `ApplyPolygonTerrain` 移除 edgeCells（Bresenham 边格补丁），一套判定（格角检测）。

### 第2步：加载延迟优化
- `MapComponentTick` 的 `ConsumeQueued` 去掉 `if (!map.IsPocketMap)` 守门——所有地图消费队列（避免口袋地图聚焦时饥饿延迟）。
- `TryPreloadNeighbor` 的 LongEventHandler 加 try/finally + exceptionHandler 清理 `generatingTiles`（防异常残留锁）。

### 第3步：诊断日志系统化
- `RimExodusSettings` 加 `verboseLogging`（默认 false）。
- 全部 `[RimExodus]` Log.Message 包 `if (Settings?.verboseLogging ?? false)`。Warning/Error 不动。

### 第4步：死字段清理
- 删除 `MapParent_SeamlessTile.neighborTiles`（List<int>，零业务引用）+ 序列化行。
- 删除 `NeighborLink.edgeAngle`（无消费者）+ 序列化行 + `GetEdgeAngle` 方法。
- `hostOffset` 从字段改为 `GenerateTileMap` 内局部变量 + 删序列化行。
- `SetNeighbor`/`RegisterNeighborBidirectional`/`SetNeighborOnMap` 移除 edgeAngle 参数。
- `NeighborInfo` 移除 edgeAngle 字段。

### 第5步：代码重复消除
- 合并 `ComputeNeighborOffset`（实例）和 `ComputeNeighborOffsetStatic`（静态）为一个静态方法。
- 删除 `GetCurrentWorldTile` 包装（直接调 `GetMapWorldTile`）。

### 第7步：Trigger 清理
- `arrivalLocks` 从 `Dictionary<Pawn,bool>` 改为 `HashSet<Pawn>`（值从不读取，语义更清晰）。
- `ReusableSpotSnapshot`/`ReusablePawnSnapshot`/`ReusableSeamPositions` 从 static 改为实例字段（`spotSnapshot`/`pawnSnapshot`/`seamPositions`），消除多地图 static 共享污染风险。
- `autoGeneratedFirstNeighbor` 重命名为 `setupOnStartDone`（语义准确：阶段4a 后默认不生成邻居，仅预铺 spot）。
- `MapParent_SeamlessTile.ExposeData` 抽 `PruneInvalidNeighbors` 方法（消除重复清理逻辑）。

### 第6步（待办）：SeamlessTileManager 拆分
- 当前 588 行单类职责过载，计划拆为 Generator/NeighborRegistry/EnterSpotPlacer。
- 纯重构（不改行为），待功能稳定后做。

### 关键文件变更（重构）
- `SeamlessPolygonGeometry.cs` — `ContainsPoint` 重构（复用 `ContainsPointAt`），`IsCellInPolygon` 格角检测。
- `SeamlessTerrainFill.cs` — `ApplyPolygonTerrain` 简化（移除 edgeCells，一套格角判定）。
- `SeamlessTileManager.cs` — 死字段清理、重复消除、命名修正、加载延迟优化。
- `MapParent_SeamlessTile.cs` — 删 neighborTiles/edgeAngle/hostOffset，抽 PruneInvalidNeighbors。
- `SeamlessTileGraph.cs` — 移除 edgeAngle。
- `SeamlessTileRegistry.cs` — 删 GetCurrentWorldTile 包装。
- `SeamlessMapTransferTrigger.cs` — arrivalLocks 改 HashSet，Reusable 改实例。
- `RimExodusSettings.cs` — 加 verboseLogging。
- 全局 Log.Message 包 verboseLogging 开关。

## 阶段4a 后续修复记录（最终状态）

### 异步地图生成方案（最终：LongEventHandler）
- 纯 Thread（无进度画面）和拆分式异步（AddMap→工作线程 genSteps→主线程 FinalizeInit）均不可行：
  - 纯 Thread：`List<Map>.Add` 与主线程 foreach `Find.Maps` 的 `_version` 竞争（Alert/ColonistBar 每帧枚举）。
  - 拆分式异步：genSteps 期间 map 已 AddMap 但未 FinalizeInit（region/pathing 未初始化），主线程 tick 海量 "RegionAndRoomUpdater is disabled" 警告 + 寻路失效。
- **最终方案**：`LongEventHandler.QueueLongEvent(doAsynchronously:true)`，显示进度画面几秒。`AddMap→genSteps→FinalizeInit` 必须在主线程恢复 tick 前连续完成（引擎硬约束）。

### void 孤岛修复（最终：格角检测）
- 多套边界判定不一致（ContainsPoint 叉积 + Bresenham RoundToInt 圆整）导致 void 孤岛切断 region（numDistrict=0）。
- **最终方案**：`IsCellInPolygon` 格角检测——格中心或 4 角任一在凸多边形内 → 非 void。凸性保证外部格 4 角都不在内，内部格至少一个角在内，消除孤岛。`ApplyPolygonTerrain` 移除 edgeCells 补丁，一套判定。

### 传送检测性能优化（事件驱动替代轮询）
- **原方案**：`SeamlessMapTransferTrigger.MapComponentTick` 每 tick 轮询所有传送点（~600 个），O(spots+pawns)，profiler 显示为核心性能瓶颈。
- **最终方案**：Harmony Postfix `Pawn_PathFollower.TryEnterNextPathCell`（pawn 跨格瞬间），O(1) 查 `thingGrid` 该格是否有传送点。pawn 不动时零开销。
- **新文件**：`Patches_PawnPathFollower.cs`。`SeamlessMapTransferTrigger` 的 `CheckLocalEnterSpots` 改为 static `TryTriggerTransfer(Pawn, IntVec3, Map)`，`MapComponentTick` 只保留 `PurgeInvalidArrivalLocks`（arrivalLocks 为空时 O(1) 返回）。
- 覆盖性：征召移动/撤退敌人/续程 Goto 走 pather 跨格→触发；跨图落地（GenSpawn.Spawn）不走 pather→不触发（pawn 在 arrivalLocks 防回弹，后续迈步走 pather 时触发）。

### 已解决的历史问题
- C 自动显示间接邻居 A：`AutoConnectWorldNeighbors`（新地块加载时遍历世界邻居，对已加载的调 EnsureNeighborRegistered）。
- 多跳间隙重复生成：`TryGetMapByWorldTile` 全局查询 + `EnsureNeighborRegistered` 补登记。
- void 渲染残影红色：`SeamlessTileRenderer` CommandBuffer 开头 `ClearRenderTarget(true, true)` 清色缓冲。
- 跨地图寻路失败：`InjectCrossMapGotoOption` 自注入跨图 Goto 选项绕过原版 CanReach。

### 待办（低优先级）
- SeamlesslessTileManager 拆分（588行→Generator/NeighborRegistry/EnterSpotPlacer），纯重构待功能稳定后做。
- 偶现弹跳（A↔C 反复 transfer）待复现后定位根因。
- 地图生成进度画面优化（分帧 genStep 执行，工作量极大且无先例）。

## 阶段4b：传送机制重构——容纳投影扭曲（已完成，可编译，游戏内待验证）

### 核心思想（重要，理解后续所有改动的前提）
RimWorld 星球是球面多面体（大量六边形 + 12 个五边形平面拼成），无缝大地图本质是其在局部地图上的**投影**。**投影必然扭曲**：相邻 tile 各自用自己地块中心的切平面基（`WorldRendererUtility.GetTangentsToPlanet(center)`）投影多边形顶点（`WorldTileGeometry.PopulateVertexDirections`）。相邻 tile 中心不同 → 切平面基方向不同（有旋转）→ 世界网格上的同一条共享边在两端局部坐标系里的 2D 方向旋转了（赤道→北极累积约 30°）。

旧机制（阶段3/4a）试图用"传送点双向精确互绑"对抗这个扭曲：两端各自 `EnumerateEdgeCells` Bresenham 划线铺 spot，`BindUnboundSpotsBetween` 按 `expectedCellB = cellA - offset` 精确坐标校验互绑。但投影旋转导致两端 Bresenham 格不一致，精确校验失败 → 绑定数 0 → 传送点未绑定 → `TryFindNearestReachableBridgeSpot` 找不到对端 → `TryInterceptJob` 不拦截 → pawn 在本图走到目标坐标（完全不跨图）。

**新机制容纳扭曲而非消除它**：加宽接缝重叠带到 2 格吸收偏移；传送改为 offset 算对端坐标并缓存到 spot（废弃互绑）。每个 tile 保留自己的切平面基（几何底层不动），扭曲被重叠带吸收。

### 接缝重叠带（`SeamlessTileManager.SeamOverlap = 2`）
- `ComputeNeighborOffset` 算出 `offsetVec = 2*(边中点 - 中心)` 后，沿其自身方向收缩 `SeamOverlap=2` 格：`offsetVec -= offsetVec/mag * SeamOverlap`，再 round。
- 效果：邻居多边形相对当前地图多叠 2 格。这 2 格在两端都落在各自多边形内（非 void、可站立），吸收投影偏移。
- **所有 offset 消费者自动跟随**（渲染/归属/边界带/void 都读同一个 `NeighborLink.offset`），无需逐个改。
- 从赤道到北极旋转累积 30° 的过程中，2 格重叠带覆盖逐渐累积的偏移（0-124 部分重合在西北方 2 格内，东北方不漏 void）。

### 传送点缓存对端坐标（核心架构，替代互绑）
- `CompSeamlessTileEnterSpot`：**移除** `CounterpartSpot` 字段。**新增** `cachedArrivalCell`(IntVec3) + `hasArrival`(bool)，不序列化。保留 `targetWorldTile`。
- `ComputeAndCacheArrival(Map ownerMap)`：用 `SeamlessTileGraph.TryGetNeighborLinkByWorldTile(ownerMap, targetWorldTile, out info)` 拿 offset，算 `cachedArrivalCell = parent.Position - info.offset`（NeighborLink 契约 `cellNeighbor + offset = cellMy` 的逆），置 `hasArrival=true`。邻居未加载时 `hasArrival=false`。
- `SeamlessTileManager.RefreshEnterSpotArrivals(Map)`：遍历该 map 所有 spot 调 `ComputeAndCacheArrival`。在 `RegisterNeighborBidirectional` 末尾刷新两端（offset 在此确定且不再变，缓存一次即可）；`GenerateTileMap`/`EnsureNeighborRegistered` 在补铺 spot 后再刷一次覆盖新 spot。

### 传送/寻路查询（O(1) 读缓存）
- **传送触发**（`SeamlessMapTransferTrigger.TryTriggerTransfer`）：踩 spot → `if(!comp.hasArrival) continue` → `SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out arrivalMap)`（少量 Map 查询）→ 读 `comp.cachedArrivalCell`（O(1)）→ `TryTransferPawn(pawn, spot, arrivalMap, cachedArrivalCell)`。
- **转移**（`SeamlessMapTransfer.TryTransferPawn`）：签名改为 `(Pawn, Thing departureSpot, Map arrivalMap, IntVec3 arrivalCell)`，移除 `CounterpartSpot` 互引校验。`arrivalCell` 不可通行时记 Warning 不兜底（按设计，重叠带保证可通行；若不可通行说明寻路本就该不可达）。
- **桥接查找**（`SeamlessCrossMapOrders.TryFindNearestReachableBridgeSpot`）：候选过滤改用 `comp.hasArrival && comp.targetWorldTile == toMapWorldTile`（O(1) 读字段/缓存 + worldTile 比对），不再查 `CounterpartSpot.Map`。
- **未来跨图 A* 寻路**：扩展节点时读 `hasArrival`/`cachedArrivalCell` 判断"有对端 + 对端坐标"，O(1)，无需现算 offset/查邻居表/遍历。Map 解析推迟到实际传送时。

### 删除/废弃
- **删除文件 `Source/SeamlessEnterSpotBinder.cs`**（互绑工具，整个废弃）。
- 删除 `SeamlessTileManager.BindNewTileWithExistingNeighbors` 方法 + `GenerateTileMap`/`EnsureNeighborRegistered` 对绑定方法的调用。

### 不改的部分
- 几何底层（`WorldTileGeometry`/`SeamlessPolygonGeometry`/切平面基）：每 tile 保留自己的基。
- void 铺设（`ApplyPolygonTerrain`）、渲染、归属判定（`TryGetOwnerNeighbor`）、边界带（`ComputeEdgeBand`）、预加载：经 `NeighborLink.offset` 自动跟随。
- 传送点铺设（`PlaceEnterSpotsAllNeighbors`）：仍沿边 Bresenham 铺单端 spot 记 `targetWorldTile`，不铺两层（2 格重叠由 offset 提供）。预铺时 `hasArrival` 默认 false，待邻居加载后刷新。

### 待游戏内验证
- 命令 pawn 前往首个邻居 B（旧机制下必然异常的方向）→ 应正常跨图。
- 赤道→北极方向跨图（旋转累积最大），确认 2 格重叠带吸收偏移、无 void 缝隙。
- 防反弹锁：跨图后续程沿接缝前进不被传回。
- offset 偏移幅度评估（verbose 日志观察 `cellA → cellB` 落点）。

## 存档兼容性说明（重要）
**mod 未发布，当前一切测试在新建存档中进行，无需考虑旧存档兼容。** 几何/传送点/字段变更后重开档即可，不做读档迁移。此原则适用于阶段3 direction 字段移除、阶段4b 传送机制重构等所有破坏性变更。

## 阶段4b 补充修复：跨图菜单可达性语义 + 选中状态保持（已完成，可编译，游戏内待验证）

### 修复1：跨图菜单"弹 vs 不弹"被 A 上对应坐标影响（底层设计缺陷）
- **根因**：`Patches_FloatMenuMakerMap` Prefix 把 `context.map = B`，但原版 `FloatMenuOptionProvider_DraftedMove.PawnCanGoto` 调 `pawn.CanReach(gotoLoc)`，而 `ReachabilityUtility.CanReach` 内部用 `pawn.Map`（A）的 reachability，从不看 context.map。gotoLoc 是 B 坐标，同尺寸地图下落在 A bounds 内 → 在 A 上对"A 上对应坐标"做寻路。A 对应位置可通行 → CanReach 成功 → 产出可达 GoHere（autoTakeable）不弹菜单；A 对应位置不可通行（山脉/void）→ CanReach 失败 → 产出禁用选项 → 旧 InjectCrossMapGotoOption 注入新选项但**没设 autoTakeable** → 弹菜单。这是设计缺陷：导航到 B 不该被 A 上对应坐标影响。
- **修复**（`InjectCrossMapGotoOption` 重构）：跨图场景下**完全不让原版 DraftedMove 的可达性检查参与决定**。移除原版全部 GoHere 产出（可达 `isGoto=true` + 禁用 CannotGo），改用桥接可达性（`SeamlessCrossMapOrders.CanBridgeTo` = 本图是否有 pawn 可到达的桥接传送点）：
  - 桥接可达 → 注入 `autoTakeable=true; autoTakeablePriority=10f; isGoto=true` 的跨图 GoHere（对齐原版可达语义）→ `GetAutoTakeOption` 直接执行不弹菜单。
  - 桥接不可达 → 注入 `action=null` 的禁用"无法到达"（`CannotGoNoPath.Translate()`）→ 灰色显示。
- **桥接可达性复用**：新增 `SeamlessCrossMapOrders.CanBridgeTo(pawn, toMap)` 公开方法（调 `TryFindNearestReachableBridgeSpot` 只查可达性，不 Record/不返回 spot），前端菜单和后端桥接共用同一判定。
- **五边形 void 已考虑**：方案基于"本图桥接 spot 可达性"，不感知对端坐标在本图是 void 还是山壁。

### 修复1扩展：跨图右键 Thing/Pawn 选项的一致性收敛（系统性）
- **根因（系统性）**：`FloatMenuContext.cachedClickedThings/cachedClickedPawns` 在构造时从 `Find.CurrentMap`(A) 收集（`GenUI.ThingsUnderMouse` 硬编码 CurrentMap），不从 `context.map`(B)。所以跨图点击 B 上的物品/敌人/Pawn 时，`ClickedThings` 装的是 **A 上同坐标格的对象**，根本不是玩家视觉上点的 B 的对象。所有 52 个 Thing/Pawn provider（攻击/拾取/治疗/修理/haul 等）对这些"A 的错误对象"用 `pawn.Map`(A) 的 reachability 检查，产出错误选项，甚至触发 `Reachability.CanReach`（Reachability.cs:115-117）的跨图 `Log.Error` 红字。修复1只处理了 DraftedMove(GoHere)，其余 51 个 provider 全部错误产出。
- **修复（一致收敛）**：跨图场景下跨图右键**任何东西**（cell/Thing/Pawn）统一收敛为"走到这里"：
  - **Prefix 清空 `ClickedThings`/`ClickedPawns`**：构造 context 后用反射（`AccessTools.FieldRefAccess` 缓存 private 字段 `cachedClickedThings`/`cachedClickedPawns`）清空 → `GetProviderOptions` 的 Thing/Pawn foreach 不执行 → 52 个 Thing/Pawn provider 从源头不产出，避免错误选项和跨图红字。
  - **`InjectCrossMapGotoOption` 清空 result**：cell 级 provider（WorkGivers cell 分支、ExtinguishFires 等）仍会用 `pawn.CanReach(B cell)` 产 NoPath，本方法清空全部原版产出，按桥接可达性注入唯一选项（可达 GoHere 或灰色无法到达）。
- **语义**：当前阶段（跨图移动已实现，跨图射击/交互未实现），跨图右键任何位置 = "走到这里"（桥接过去）。同图场景完全不受影响（Prefix `return true` 放行原版）。未来跨图射击/交互实现时，在清空 ClickedThings 后按需注入专门跨图选项。
- **为什么是一致的**：单一改动点（Prefix 清空 + InjectCrossMapGotoOption 清空注入）覆盖全部 52 个 provider，从源头切断而非逐个过滤，避免 `Reachability.CanReach` 跨图红字。

### 修复2：跨图后选中状态丢失
- **根因**：`SeamlessMapTransfer.TryTransferPawn` 的 `pawn.DeSpawn()` 触发 `Thing.DeSpawn`（`Thing.cs:974-977`）**无条件** `Find.Selector.Deselect(this)`；`GenSpawn.Spawn` 不操作 Selector → 选中列表保持空。
- **修复**（原版范式照搬，参照 `CameraJumper.TrySelectInternal` 和 Pawn 死亡生尸体 `Pawn.cs:2246-2248`）：
  - `TryTransferPawn` 加 `out bool wasSelected`，DeSpawn 前记 `wasSelected = Find.Selector.IsSelected(pawn)`。
  - `TryTriggerTransfer` 在 `TryAutoFocusOnArrival`（切图）之后、`ContinueCrossMapMove` 之前，若 `wasSelected` 调 `Find.Selector.Select(pawn, playSound:false, forceDesignatorDeselect:false)`。
  - **必须切图后 Select**：此时 `CurrentMap==arrivalMap`，`SelectInternal` 第 379 行 `map!=CurrentMap` 为 false 不二次硬跳；`Patch_Selector_SelectInternal` 的无感偏移分支（`targetMap==currentMap`）也不触发。多 pawn 框选各自 wasSelected+Select，`SelectInternal` 段 C 不误删同 map 已选项。

### 修复2补丁：多 pawn 跨图只有第一个保持选中
- **根因（时序问题）**：多 pawn 跨图是逐个的（每个 pawn 各自踩传送点，跨多个 tick）。首个殖民者跨图时 `TryAutoFocusOnArrival` 切图到 arrivalMap，触发 `MapInterface.Notify_SwitchedMap`（`MapInterface.cs:194`）→ `selector.ClearSelection()`，把**尚未跨图的其余 pawn** 从选中列表清掉。它们后续跨图时 `IsSelected` 返回 false，不被 re-Select。
- **修复**（`SeamlessSelectionTracker`）：引入静态选中保持集，跨越切图清空的时序：
  - 前端 `Patch_FloatMenuMakerMap` 在玩家下达跨图指令时，把所有选中且要跨图的 pawn `Register` 到保持集。
  - `TryTriggerTransfer` 转移后用 `SeamlessSelectionTracker.Consume(pawn)`（在集合里就 re-Select 并移除），取代原来依赖 `wasSelected`（会被切图清空污染）。
  - `MapComponentTick`（锚点地图）周期调 `PurgeInvalid` 清理死亡/未跨图残留。
- 这样无论切图清空几次，保持集始终记得"这批 pawn 应选中"，逐个跨图后各自 re-Select。

## 阶段4：连续地形调研 + 边界带不可建造约束 + 共因 bug 修复（已完成，可编译）

阶段4 原定义是"连续地形原型"。本轮聚焦**调研 + 两个可直接落地的实现项**，连续地形四项分轮推进。完整调研结论见 `doc/第四阶段-连续地形.md`。

### 边界带不可建造约束（noBuild，阶段3→4 安全前提）
- **动机**：玩家可在接缝重叠带及其内侧建造建筑，用建筑改变寻路把 pawn 困在 void 一侧——一旦 pawn 站上指向"已被建筑封死对端"的传送点就会卡死。故多边形边内侧必须禁止建造。
- **数据源**：`SeamlessBorderLookup` 新增独立的 `noBuildBandCells`（`HashSet<IntVec3>`，只存格坐标不需 worldTile 值），带宽由新字段 `borderNoBuildDistance`（默认 `SeamOverlap+1=3`）控制，与预加载带 `borderCells`（`borderPreloadDistance=15`）分离。`BuildBorderLookup` 同时构建两张表（各自带宽独立）。
- **查询接口**：`SeamlessBorderLookup.IsInNoBuildBand(cell)`（O(1)）；`IsBuilt` 属性供 patch 判断速查表是否就绪（未就绪放行避免误拒）。
- **patch 点**：`Patch_GenConstruct_CanPlaceBlueprintAt`（Prefix `GenConstruct.CanPlaceBlueprintAt`，所有玩家建造路径的唯一汇聚点）。遍历 `GenAdj.OccupiedRect(center, rot, entDef.Size)`，任一格在禁建带 → `__result = "RimExodus_BorderNoBuild".Translate()` + return false。godMode 放行（开发模式可建）。
- **不处理原版 InNoBuildEdgeArea**：对 pocket map 它本就返回 false；用户明确不处理，避免六边形边角顶边缘时反而能在原版禁建带建造。
- **本地化**：新建 `1.6/Languages/English/Keyed/RimExodus.xml`（仓库此前无 Languages 目录）。

### 共因 bug 修复（map.Tile=0 导致岩石类型读 tile 0）
- **根因**：`GenerateTileMap:269` 设 `mapParent.Tile = 0`（口袋地图惯例，VMF 也如此）。`map.Tile` 读 `MapParent.Tile`（`Map.cs:384` → `MapInfo.Tile` → `MapParent.Tile`），**不读 `pocketTileInfo.tile`**。所以改 `pocketTileInfo.tile` 无效。且不能改 `mapParent.Tile` 为真实 worldTile——`WorldObject.Tile` setter（`WorldObject.cs:99-116`）有副作用（`FastTileFinder.DirtyTile` + `PositionChanged`），会把口袋 MapParent 当真实世界地块处理，破坏口袋语义。
- **后果**：`RockNoises.Init`（`Verse/RockNoises.cs:22`）用 `map.Tile` 调 `NaturalRockTypesIn(map.Tile)`（以 `tile.GetHashCode()` 为 seed 选岩石类型集合）→ 所有口袋地图读到 tile 0 的岩石类型。`GenStep_Roads` 同样读不到真实道路数据。
- **修复**：`Patch_RockNoises_Init`（Prefix `RockNoises.Init`），对 RimExodus 口袋地图（`map.Parent is MapParent_SeamlessTile`）用 `new PlanetTile(seamlessParent.worldTile)` 查 `NaturalRockTypesIn`。复制原方法逻辑，只改 tile 来源；Perlin seed（`Rand.Range`）不动（属连续地形阶段的岩石连续化范围）。非 RimExodus 口袋地图放行原方法。
- **范围限定**：本轮只修"岩石类型读对 tile"。河流/道路数据的完整注入属连续地形阶段。

### 异步加载可行性结论（本轮调研，未实现）
- **子类化方案不可行**：`Map` 是 `sealed`（`Verse/Map.cs:13`），C# 编译期禁止继承。
- **新方向——延迟 AddMap 方案理论上可行**（4 核心 patch）：根因是 map 一旦 AddMap 进 `Find.Maps`，主线程每帧 foreach `Find.Maps` 的系统会访问半成品 map 崩。genSteps + FinalizeInit 内部已确认无其他 `Find.Maps` 依赖（唯一阻断是 `Thing.SpawnSetup:819` IndexOf 检查 + `TickManager.RegisterAllTickAbilityFor` 全局 TickList 登记）。4 patch：①Prefix SpawnSetup 跳过 IndexOf 检查；②Prefix RegisterAllTickAbilityFor 不登记半成品 thing；③Prefix Game.AddMap 延迟；④生成完成后反射修正 mapIndexOrState + 补登记。
- **剩余雷区**：`SignalManager.RegisterReceiver`（全局副作用）、`Pawn.SpawnSetup` override、部分 Comp 的 `this.Map` 读取——实现前必须验证。
- **判断**：比"接受进度画面"工程复杂度高，但唯一能实现"无进度画面"的方向。RimWorld 原生自己都做不到（`SettleInEmptyTileUtility` 同样用进度画面）。建议独立实验分支，不阻塞主线。

### 连续地形四项可行性分级（本轮调研，未实现）
| 项 | 可行性 | 核心 patch 策略 |
|---|---|---|
| 高度/地形 | 完全可做 | `extraInitBeforeContentGen` 预填 Elevation/Fertility grid + Prefix 跳过 `GenStep_ElevationFertility`。Perlin seed 只是哈希系数（`Utils.cs:176`），`GetValue` 是纯函数 |
| 岩石类型 | 完全可做 | Prefix `RockNoises.Init` 确定 seed + 世界坐标采样（本轮已修 tile 来源） |
| 河流 | 部分能做 | Prefix `TileMutatorWorker_River.GenerateRiverGraph` 接缝锚点；接缝处视觉可接上，远离接缝各自弯曲。先决：pocket map 无 River mutator |
| 道路 | 基本不能做 | A* 寻路强依赖整图地形，两端独立寻路不可能接缝重合。降级为穿越点对齐 |

### 源码文件新增
- `Source/Patches_RockNoises.cs` — 共因 bug 修复（Prefix `RockNoises.Init` 用真实 worldTile）。
- `Source/Patches_GenConstruct.cs` — 边界带不可建造约束（Prefix `CanPlaceBlueprintAt`）。
- `1.6/Languages/English/Keyed/RimExodus.xml` — noBuild 提示文案（仓库首个 Languages 文件）。

### 修改的源码文件
- `RimExodusSettings.cs` — 加 `borderNoBuildDistance`（默认 3）。
- `SeamlessBorderLookup.cs` — 加 `noBuildBandCells`（HashSet）+ `IsInNoBuildBand` + `IsBuilt`；`BuildBorderLookup` 重构为同时构建预加载带和禁建带（各自带宽独立）。

### 编译
`dotnet build Source/RimExodus.csproj -c Debug` 通过（0 错误 0 警告）。

### 待办
- 连续地形四项分轮实现（高度/地形优先，岩石次之，河流再次，道路最后或砍）。
- 游戏内验证：noBuild（边内侧 3 格禁建、godMode 可建、外侧可建）、共因 bug（邻居岩石类型随 biome 变化）。

## 阶段4 分帧增量生成最终实现（已完成，可编译，游戏内已验证）

实验分支 `experiment/deferred-addmap-async` 实现并验证了**分帧增量地图生成**：主线程每帧跑 1+ genStep，不暂停 tick、无进度画面、无红字 NRE、性能良好。玩家在生成期间可继续操作其他地图。取代了此前的 LongEventHandler（进度画面）/ forceHideUI / 延迟 AddMap / 纯 Thread 等方案。详细调研与最终状态见 `doc/第四阶段-连续地形.md` 的"更优方案：分帧增量生成"和"最终实现状态"节。

### 文件清单
- `Source/IncrementalMapGenerator.cs` — 分帧生成器 MapComponent（准备阶段同步 + genStep 分帧 + FinishGeneration 单帧）。
- `Source/Patches_IncrementalMapGen.cs` — patch `Map.MapPreTick`/`MapPostTick`/`MapUpdate`，generating map 早退（不 tick、不渲染）。
- `Source/Patches_GenStepRocksFromGrid.cs` — Postfix 清 void 格岩石 + 屋顶（order~200，RocksFromGrid 之后立即）。
- `Source/SeamlessTerrainFill.cs` — `ApplyPolygonTerrain` 改为直接写 `terrainGrid.topGrid` + `MapMeshDirty(regenAdjacentCells:false)`，跳过 SetTerrain 副作用。
- `Source/SeamlessTileManager.cs` — `GenerateTileMap` 改用 `IncrementalMapGenerator.Start`，后续配置放 `onComplete` 回调。
- `1.6/Defs/MapGeneration/SeamlessTileGenerator.xml` — `RimExodus_SeamlessTile` genStep `order=211`（Terrain 之后、Plants 之前）。

### 关键实现要点
- **Plants 分帧**：最重的 genStep（~8000ms 单帧），拆成每批 2000 cells，每帧跑到 `TimeBudgetMs=8ms` 预算耗尽；每批独立 `Rand.Seed`（`HashCombineInt(state.randSeed, batchIndex)`）保证跨帧 Rand 独立、可复现。
- **ApplyPolygonTerrain 直接写 topGrid**：跳过 `SetTerrain` 的 `DoTerrainChangedEffects`（pathGrid/waterBodyTracker 重算，21000 次 × 副作用 = 5.7 秒）。pathGrid 由 FinalizeInit 全量重算覆盖。
- **ProgramState 翻转**：`RunOneGenStep` genStep 执行期间临时设 `MapInitializing`，结束恢复 `Playing`。修复 `WaterBodyTracker.Notify_TerrainChanged` 在生成期间触发 NRE（bodies 未初始化）。
- **Rand.Seed PushState/PopState 保护**：每个 seed 设置都配对，不保持外层 Rand 栈帧（`Root.Update` 每帧 `EnsureStateStackEmpty` 会清空跨帧栈）。消除 "Modifying initial rand seed" 红字。
- **void 内嵌 Terrain genStep**：order=211 在 Terrain(210) 之后覆写多边形外格为 void，后续 Plants/RockChunks/Animals 读 terrainGrid 自然不 spawn。
- **RocksFromGrid Postfix**：清 void 格岩石+屋顶，使 RimExodus_SeamlessTile 的 ClearThingsOnCells 在口袋 genStep 路径变空操作（锚点 RefreshMapVoid 路径仍需它）。
- **移除 FinishGeneration 对锚点 map 的冗余 RefreshMapVoid**：原每次生成邻居都重铺锚点 void（清玩家游戏期间生长物，耗时 12-23 秒）。改为 void 只看自己多边形，TrySetupOnStart 开档铺一次。

### 性能数据
- RimExodus_SeamlessTile（void 裁切）：5762ms → 30ms。
- Plants genStep：8000ms（单帧卡顿）→ 分帧每帧 <50ms。
- 总生成无单帧卡顿，玩家全程可操作。

### 最终结论
纯后台异步（不中断操作 + 工作线程并发）受 Rand/MapGenerator static 非 ThreadStatic 限制不可行（需 fork 级工作量）。分帧方案在主线程顺序执行天然避免 static 竞争，是当前能实现"无进度画面、不暂停 tick、无红字 NRE"的最优方案。
## 阶段5原型：口袋地图 → 基础地图（已完成，游戏内已验证核心闭环）

**架构转变**：无缝地块从 `PocketMapParent`（口袋地图）改为 `MapParent`（基础地图）。所有地块（含锚点 A 的邻居 B/C/D）作为独立基础地图存在，无 sourceMap 父子关系，所有地块对等。锚点 A 本身仍是原生玩家家园 Settlement。

### 核心收益（转基础地图后自然获得）
- **`map.Tile` = 真实 PlanetTile** → 原生 Coast/River/Delta 等 TileMutator 自然生效（mutator.Init 读 map.Tile 拿到真实邻居数据）。这直接解决了阶段4b 调研发现的"海岸 mutator 在口袋地图上无法生效"问题——基础地图的 `map.TileInfo` 自动读 `Find.WorldGrid[parent.Tile]`（真实 SurfaceTile），含完整 biome/hilliness/mutators/rivers 数据，无需 InjectRealTileInfo、无需 patch CoastAngleAt。
- 无 sourceMap 副作用、无口袋特殊语义、所有地块对等。
- `map.TileInfo` 自动读真实 WorldGrid 数据（含 elevation/rivers/roads/mutators 全套）。

### 关键改动
- `MapParent_SeamlessTile` 基类 `PocketMapParent` → `MapParent`。worldTile 字段保留（int 主键，与 map.Tile 的 PlanetTile 值一致）。override `Print(LayerSubMesh)` 为空操作（不在世界视图画图标）。
- `IncrementalMapGenerator.Start` 移除口袋分支（不设 isPocketMap/pocketTileInfo）。`mapParent.Tile = new PlanetTile(newWorldTile)`（真实 PlanetTile，非 0）。
- `GenerateTileMap` 移除 sourceMap/pocketMaps.Add/InjectRealTileInfo 回调。
- `SeamlessTileGraph.IsAnchorMap`/`GetAnchorMap` 改用 `IsPlayerHome` 判断家园（不再依赖 sourceMap/IsPocketMap）。GetAnchorMap 遍历 Find.Maps 找 IsPlayerHome 地图（用于 sky/weather 共享）。
- `SeamlessTileRegistry.GetMapWorldTile` 简化（地块走 worldTile 字段，其他走 map.Tile）。
- `MapGenerated` 守门：`IsPocketMap` → `Parent is MapParent_SeamlessTile`。
- `RemoveTileMap` 移除 sourceMap/pocketMaps 清理。
- `WorldObjects.xml`：`useDynamicDrawer=false` + `<texture>World/WorldObjects/RoutePlannerWaypoint</texture>`（避免静态绘制层 "returned null material" 错误）。

### 远行队机制（重要，本轮新发现）
**家园地图（IsPlayerHome）组建远行队的完整流程**（与临时据点 reform 模式不同）：
1. `Dialog_FormCaravan.TryFormAndSendCaravan` → `RCellFinder.TryFindClosestEdgeCellTo(root, map, out exitSpot)` 找边缘出口格。
2. 失败回退 `Dialog_FormCaravan.TryFindExitSpot`（private）→ `CellFinder.TryFindRandomEdgeCellWith` 找边缘格。
3. exitSpot 传给 `LordJob_FormAndSendCaravan` → `LordToil_PrepareCaravan_Leave`（`DutyDefOf.TravelOrWait`，目标=exitSpot）。
4. pawn 走到 exitSpot → `GatherAnimalsAndSlavesForCaravanUtility.CheckArrived` 触发 `ReadyToExitMap`。
5. `CaravanFormingUtility.FormAndCreateCaravan` → 大地图远行队生成。

**关键**：家园地图组建远行队**需要 pawn 走到边缘**（不像临时据点 reform 模式原地销毁地图）。`ExitMapGrid.MapUsesExitGridNow` 对 IsPlayerHome 返回 false（家园地图不构建 exit grid），但远行队走的是 `TryFindClosestEdgeCellTo`/`TryFindExitSpot`（找边缘格），不依赖 exit grid。

**地块地图（非家园）组建远行队/敌人撤离**走另一条路径：
- `JobGiver_ExitMap`（think tree）→ `TryFindGoodExitDest` → `RCellFinder.TryFindBestExitSpot`/`TryFindRandomExitSpot`。
- 这两个方法也找地图边缘格。

### 远行队衔接无缝地块（本轮实现，已验证）
**问题**：六边形裁切把地块地图的矩形边缘格全切成 void（不可站立）。所有"找边缘出口格"的方法都失败 → 远行队 pawn 硬蹭 void 到地图边缘或卡住。

**修复**：patch 三个找边缘格的方法，对有 RimExodus 传送点的地图返回最近可达传送点：
- `Patch_RCellFinder_TryFindClosestEdgeCellTo`（家园远行队 exitSpot）
- `Patch_RCellFinder_TryFindBestExitSpot`（地块远行队/敌人撤离）
- `Patch_RCellFinder_TryFindRandomExitSpot`（地块远行队随机出口）
- `Patch_ExitMapGrid_Rebuild` Postfix：把传送点格标为出口格（让原生 `JobDriver_Goto.TryExitMap` 在 pawn 踩传送点时触发）
- `Patch_CaravanEnterMap_Enter` Prefix：远行队进入地块地图时从传送点出生（而非随机边缘落 void）
- `SeamlessMapTransferTrigger.TryTriggerTransfer` 入口检查 `job.exitMapOnArrival`：远行队流程放行原生 ExitMap（不做跨图传送），征召跨图走现有传送逻辑

**行为区分**（同一传送点，两种行为）：
- 征召前往已加载邻居接缝 → 直接跨图传送（现有机制）
- 远行队组建离开 → 踩传送点触发原生 ExitMap → 大地图远行队

**判断"有 RimExodus 传送点的地图"**用 `SeamlessExitSpotFinder.HasRimExodusEnterSpots(map)`（不依赖 Parent 类型，锚点 A 是原生 Settlement 也含传送点）。

### 坐标映射（本轮新发现，方向校准）
**`GetTangentsToPlanet(center, out first, out second)` 的语义**（实测校准，非纯推理）：
- `first = quaternion * Vector3.up`、`second = quaternion * Vector3.right`，其中 quaternion 由 `LookRotation(normalized, upwards)` 构造。
- first/second 的绝对朝向难纯推理（依赖球面视角 + LookRotation），**经实测校准**：map 坐标系 x=东、z=南，正确的 3D→2D 投影是：
  - `2D.x = -Dot(d, second)`（东，取负）
  - `2D.y = Dot(d, first)`（南）
- 校准方法：用 `WorldGrid.GetHeadingFromTo(srcTile, tgtTile)` 得世界角度真值（0°=北，顺时针），转成 (东=sin, 南=-cos) 分量，与计算的 edgeWorldDir 对比。
- **之前的错误**：`(Dot(d,second), Dot(d,first))`（x/y 互换 + z 未取负）→ 方向旋转/镜像；`(Dot(d,first), Dot(d,second))` → 仍错；`(-Dot(second), Dot(first))` → 正确。

### sky/weather 共享
基础地图原生自建独立 sky/weather manager。当前代码尝试共享锚点 manager（`interiorMap.skyManager = anchorMap.skyManager` 等），运行时未观察到大问题。若后续异常，改为各地块独立。

### 已删除的冗余代码
- `InjectRealTileInfo` 方法（基础地图 TileInfo 自动正确，不再需要）
- `Patches_RockNoises.cs`（基础地图 map.Tile 已是真实 PlanetTile，原生 RockNoises.Init 正确工作）
- RimExodusMod 启动时的 RCellFinder patch 诊断日志

### 源码文件新增
- `Source/Patches_ExitMapGrid.cs` — 把传送点格标为出口格。
- `Source/Patches_RCellFinder.cs` — 三个找边缘格方法重定向到传送点 + `SeamlessExitSpotFinder` 工具类。
- `Source/Patches_CaravanEnterMap.cs` — 远行队进入地块从传送点出生。

### 仍待办
- 地形连续化（噪声坐标代理层、Perlin seed 确定化）——基础地图转变后，原生 Coast/River mutator 已自然生效，连续化是下一步。
- sky/weather 共享的存档重载验证。
- 远行队进入出生点按来源方向精确推断（当前用任一可站立传送点）。
