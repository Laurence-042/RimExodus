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
- `MapParent_SeamlessTile.cs` — `PocketMapParent` 子类。字段：`worldTile`、`hostOffset`（宿主坐标平移）、`neighborTiles`（`List<int>`）、`neighbors`（`List<NeighborLink>`，含 `worldTile`/`edgeAngle`/`neighbor`/`offset`）。阶段3已移除旧的 `direction` 字段（固定 0-5 编号），改用基于世界地块真实顶点角度的动态方向。`ExposeData` 存全部字段。
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
- **`NeighborLink` 结构变更**：`direction`(int) → `edgeAngle`(float 弧度) + `worldTile`(int)。`worldTile` 作为邻居表主键（稳定无歧义）。
- **查询方法**：`GetNeighborInDirection(int)` → `GetNeighborByWorldTile(int)`；`SetNeighbor(int,...)` → `SetNeighbor(int worldTile, float edgeAngle, MapParent, IntVec3 offset)`。
- **`SeamlessTileGraph.OppositeDirection` 移除**（双向登记保证反向关系）。`TryGetNeighborLink` → `TryGetNeighborLinkByWorldTile`。
- **存档不兼容**：旧存档（含 direction）无法加载，原型阶段接受。

### 新增源码文件
- `Source/WorldTileGeometry.cs` — 世界地块真实几何读取。`ComputeVertexDirections`/`ComputeEdgeDirections`（顶点/边方向）、`FindNeighborIndex`（邻居序号反查）。用 Odyssey 版 `WorldGrid` API（`GetTileVertices`/`GetTileNeighbors`/`GetTileCenter`/`GetMaxTileNeighborCountEver`）。
- `Source/SeamlessPolygonGeometry.cs` — 多边形几何工具。`BuildPolygonVertices`（内切圆顶点）、`BuildNeighborCenterOffsets`（邻居中心偏移，纯几何）、`ContainsPoint`/`ContainsPointTranslated`（点在凸多边形内）、`ScanlineFill`（凸多边形扫描线填充）、`EnumerateEdgeCells`（边 Bresenham 划线）。
- `Source/SeamlessTerrainFill.cs` — 多边形地形铺设。`ApplyPolygonTerrain`（自己六边形内+边格→非void，六边形外→铺 RimExodus_Void 并清除实体/Pawn）。
- `1.6/Defs/TerrainDefs/VoidTerrain.xml` — `RimExodus_Void` 虚空地形 Def。

### 重写的源码文件
- `MapParent_SeamlessTile.cs` — NeighborLink/MapParent 数据模型（direction→edgeAngle+worldTile）。
- `SeamlessTileGraph.cs` — 邻居查询（worldTile 主键，移除 OppositeDirection）。
- `SeamlessTileManager.cs` — `ComputeNeighborOffset`（内切圆边中点）、`GenerateTileMap(sourceWorldTile, newWorldTile, mapSize)`、`PlaceEnterSpots`（Bresenham 划线）、`RefreshMapVoid`（可重复铺 void，锚点+口袋都适用）、`TryAutoGenerateFirstNeighbor`。
- `GenStep_SeamlessTile.cs` — 全铺 Soil 后调 `ApplyPolygonTerrain` 挖虚空。
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

### 修复3：void 渲染红色（BadGraphic）
- **根因**：`dontRender=true` 时 `SectionLayer_Terrain` 用 ShadowMask（透明），但某些边/材质路径可能读 `terrainDef.graphic`（默认 BadGraphic=粉色）。
- **修复**：`VoidTerrain.xml` 给 `RimExodus_Void` 显式提供 `texturePath=Misc/ShadowMask` + `edgeType=Hard`，使 graphic 被加载为透明贴图（即使某路径绕过 dontRender 读 graphic 也只画透明）。

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

## 阶段4a 第五轮：拆分式异步验证 + 弹跳诊断（进行中）

### 问题3（进度画面）—— 已在源码移除 LongEventHandler
- **验证**：第四轮已把 `TryPreloadNeighbor` 改用 `BeginTileMapGeneration`（纯 `new Thread`，无 LongEventHandler）。
- **用户日志显示旧 DLL**：第五轮测试日志堆栈仍含 `LongEventHandler:RunEventFromAnotherThread` → `TryPreloadNeighbor>b__0`（lambda），但新源码的 `TryPreloadNeighbor` 不含 lambda（调 `BeginTileMapGeneration`）。
- **确认**：重新编译后 `strings RimExodus.dll` 验证不含 `LongEventHandler`/`QueueLongEvent`，含 `BeginTileMapGeneration`/`Phase2WorkerProc`/`FinalizePendingTileMap`。**用户需重启游戏加载新编译的 DLL**（`1.6/Assemblies/RimExodus.dll`，时间戳 15:47+）。
- 预期：重启后生成邻居地图不再显示进度画面，主线程不阻塞。

### 偶现问题（三地图交点弹跳）—— 加诊断日志
- **现象**：A↔B↔C 三地图接缝交点附近，pawn 在 map 0 ↔ map 2 之间反复 transfer（日志显示 map 2(183,235)→map 0(181,16)→map 2(179,235)→map 0(181,16) 循环）。防反弹锁（pawn 级，离开整条接缝带才解锁）应在 map 0 上锁住 pawn 阻止再次 transfer，但实际 transfer 发生了。
- **诊断**：`CheckLocalEnterSpots` 的 trigger 日志加锁状态标注（`was-locked`/`not-locked`）。若弹跳时显示 `was-locked`，说明锁存在但仍 transfer（锁检查逻辑 bug）；若 `not-locked`，说明锁被过早清除（`PurgeInvalidArrivalLocks` 误清）。
- **待用户复现并提供带锁状态的日志**。

### 新额外问题（C 自动显示 A）—— 已解决
- 第四轮的 `AutoConnectWorldNeighbors` 生效，C 加载时自动与间接邻居 A 建立关系。

### 已知偶现问题（待复现）
- pawn 卡在两地图夹缝、所有目标不可达：偶现，日志丢失。可能因异步生成期间传送点/邻居关系时序竞争。待下次复现时通过日志定位。
