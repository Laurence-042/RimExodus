# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

为了验证此文件确实完整读入，你需要在对话开始给出这个文件的目录，而且任何你觉得需要长久记忆的内容都应该记在这个里面，或者项目概述里描述的对应文档里，或者作为代码注释，不要依赖copilot或者你所在harness/agent自行提供的memory功能。

## 项目概述

RimWorld Mod：实现"无缝世界地块探索"系统，使相邻世界地块的局部地图在视觉与操作上连续连接，Pawn 可直接从一张地图走入相邻地图，无需组成远行队。

文档索引：
- `doc/无缝世界地块探索.md` — 长期设计与五阶段路线图。
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

- 本仓库 grep 需用**绝对路径 + 正斜杠**（如 `d:/SteamLibrary/...`），相对路径会失败。**`grep_search` 工具在本仓库经常对明确存在的内容返回空结果（不报错，静默失败），不可信**；改用 `run_in_terminal` 执行 PowerShell 的 `Select-String -Path <绝对路径> -Pattern <正则>` 代替，稳定可靠。
- `memory` 工具与 `create_file` 对超 ~150 行的内容有截断 bug：先建 stub，再分块（≤150 行）插入。
- 主设计文档的“当前阶段计划”定义了 5 个推进步骤：VMF 调研 → 最小技术原型 → 旅行 Pocket Map → 六边形裁切 → 连续地形；第一、二阶段细节分别维护在独立文档中。
- 最小技术原型验证点：地图绘制/选取/移动命令、跨地图入口往返、存档读档恢复。**生成与渲染、双向 Pawn 转移、相邻地块选中与跨地图移动指令均已实现并通过编译**；游戏内交互表现与保存读档恢复仍待验证。
- **重叠带归属语义**：重叠带内一个格子的逻辑归属**不是**"在地图 A 内就算地图 A"，而是采用六边形方案的**最近中心所有权规则**（`SeamlessTileRegistry.TryGetOwnerPocketMap`）：候选 = 宿主地图中心 + 所有 footprint 覆盖该格的锚定口袋地图中心，取距离最近的格子中心作为逻辑所有者。`TryResolveMapPosition` 调用它决定点击归属。北缘边界：宿主中心 z=125、北侧口袋中心 z=370，中点 z=247.5，因此 `125,0,247` 留在地图 A，`125,0,248`/`125,0,249` 传送到地图 B。该规则与 `doc/用可重叠正方形承载六边形网格的空间映射方案.md` 一致，为六边形裁切/连续地形铺路。

## 最小技术原型基础实现（已完成，可编译）

原型目标：验证"地图能否绘制/选取/接收移动命令、跨地图入口往返、存档读档恢复"。采用**矩形地图**（不做六边形裁切与连续地形），复用 `PocketMapParent`/`sourceMap` 宿主机制 + 自行实现叠加层渲染（不依赖 VMF 渲染管线）。

### 项目结构
- `About/About.xml` — mod 元数据，packageId `RimExodus.SeamlessWorld`，依赖 `brrainz.harmony`，支持 1.6。
- `Source/RimExodus.csproj` — net48，引用 `Krafs.Rimworld.Ref 1.6.4633` + `Lib.Harmony.Ref 2.4.2`，输出到 `..\1.6\Assemblies\`。
- `1.6/Defs/WorldObjectDefs/WorldObjects.xml` — `RimExodus_SeamlessTileMap` WorldObjectDef，worldObjectClass `RimExodus.MapParent_SeamlessTile`，mapGenerator `RimExodus_SeamlessTileGenerator`。
- `1.6/Defs/MapGeneration/SeamlessTileGenerator.xml` — `RimExodus_SeamlessTileGenerator` MapGeneratorDef（pocketMapProperties biome BorealForest）+ `RimExodus_SeamlessTile` GenStepDef（genStep Class="RimExodus.GenStep_SeamlessTile"）。

### 源码文件（`Source/`）
- `RimExodusMod.cs` — `[StaticConstructorOnStartup]`，`new Harmony("RimExodus.SeamlessWorld").PatchAll()`。
- `MapParent_SeamlessTile.cs` — `PocketMapParent` 子类。字段：`worldTile`、`direction`（0=北,1=东北,2=东南,3=南,4=西南,5=西北）、`hostOffset`（宿主坐标平移）、`neighborTiles`（`List<int>`）。`ExposeData` 存全部字段。
- `SeamlessMapUtility.cs` — 坐标转换：`ToHostCoord`/`ToLocalCoord`/`ToHostDrawPos`/`HostCellInFootprint`。静态地块无旋转，仅平移 `hostOffset`。
- `SeamlessTileManager.cs` — `MapComponent`。`GenerateTileMap(direction, mapSize, overlapBand)`：`WorldObjectMaker.MakeWorldObject` → 设 `sourceMap`/`Tile=0`/`direction`/`hostOffset` → `MapGenerator.GenerateMap(..., isPocketMap: true)` → 加入 `Find.World.pocketMaps` + `Find.World.worldObjects` → 共享宿主 skyManager/weather。`ComputeHostOffset` 把口袋地图放宿主边界外并留重叠带。`GetTileMapInDirection`/`RemoveTileMap`。
- `SeamlessTileRenderer.cs` — `MapComponent`，维护绑定到主相机 `CameraEvent.BeforeForwardOpaque` 的专属 `CommandBuffer`。每帧只提交精确类型为 `SectionLayer_Terrain` 的主地形层，按 `material.renderQueue` 稳定排序；绘制口袋颜色后只清深度，再由原版宿主地图覆盖重叠带。相机切换与 `MapRemoved()` 会正确解绑、释放。dirty section 仍直接 `RegenerateAllLayers()` 并手动清零 `dirtyFlags`。
- `Patch_MapEdgeClipDrawer_DrawClippers.cs` — 收集所有口袋地图 footprint，从四块原版世界裁剪矩形中依次做矩形差集，仅绘制剩余矩形；保留原版高度和世界对齐纹理参数。只在真实 footprint 开洞，避免拖动残影。
- `SeamlessTileRegistry.cs` — `GetFootprintsOnHost(Map)` 返回宿主坐标 `List<CellRect>`（局部矩形 + hostOffset）。
- `GenStep_SeamlessTile.cs` — `GenStep`，`Generate` 铺设矩形地形：边缘 2 格不可通行（WaterOceanDeep），内部可通行（Soil）。
- `CompSeamlessTileEnterSpot.cs` — `ThingComp` 入口点，`direction` 属性，`AdjacentTileParent` 经宿主 `SeamlessTileManager.GetTileMapInDirection` 查相邻地块。
- `SeamlessMapTransfer.cs` — 跨地图 Pawn 转移：`TransferPawnToTile`（宿主→地块，目标局部坐标 = 宿主坐标 - hostOffset，`DeSpawn()` + `GenSpawn.Spawn()`）、`TransferPawnToHost`（地块→宿主）。
- `SeamlessMapTransferTrigger.cs` — `MapComponent`，每 30 tick 检查 `Find.CurrentMap` 上是否有 Pawn 站在 `CompSeamlessTileEnterSpot` 传送点上，触发转移。

### 关键实现要点
- **渲染顺序**：不再依赖 `MapComponentOnDraw` 的 `Graphics.DrawMesh` 调用顺序或高度 epsilon。口袋主 Terrain 在 `BeforeForwardOpaque` 背景通道写颜色，随后清深度，宿主地图照原版路径绘制，因此本端稳定覆盖对端。
- **MapComponent 自动注册**：`Map.FillComponents` 自动实例化所有 `MapComponent` 非抽象子类，无需手动注册。
- **编译**：仓库根目录运行 `just build`（默认任务也是 `build`），底层命令为 `dotnet build Source/RimExodus.csproj -c Debug`，输出 `1.6/Assemblies/RimExodus.dll`。已通过（0 错误 0 警告）。
- **踩坑**：`Scribe_Collections.Look` 只接受 `List<T>`（非数组），故 `neighborTiles` 用 `List<int>`；`Pawn_MindState.Reset` 有两个重载需显式传参；`TerrainDefOf` 需 `using RimWorld;`；`WorldObjectDef` 在 `RimWorld` 命名空间（非 Planet）。
- **待办**：原型尚未实现跨地图寻路/射击（VMF 的 `CrossMapReachabilityUtility`/`AttackTargetFinderOnVehicle` 模型可复用）、六边形裁切、连续地形、传送点 Thing 的生成与放置（当前 `CompSeamlessTileEnterSpot` 需手动放置 Thing）。

## 生成 + 渲染可测试性补全（已完成，可编译）

**背景**：此前原型只是"能编译的空壳"——`GenerateTileMap` 无任何调用者、传送点 Thing 从未放置、转移只有单向、渲染器遍历空列表，验证点一个都测不了。本次补上"生成 + 渲染"的可测试性（用户明确范围：只补生成+渲染，转移后续再说）。

### 方向系统扩展为 6 向
- `direction` 语义从 0-3（四向）扩展为 0-5（六边形方向）：**0=北, 1=东北, 2=东南, 3=南, 4=西南, 5=西北**。
- **原型四向兼容**：1/2 暂时当作东，4/5 暂时当作西（`ComputeHostOffset` 中 case 1/2 共用东偏移，case 4/5 共用西偏移）。这是尚未实现 6 向时的临时兼容。
- 更新处：`MapParent_SeamlessTile.cs`（字段注释）、`SeamlessTileManager.cs`（`ComputeHostOffset`/`GetTileMapInDirection` 注释）。

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
- 传送点 Thing 的自动生成与放置（当前 `CompSeamlessTileEnterSpot` 需手动放置 Thing，转移触发仍无法自动化测试）。
- 双向转移（地块→宿主无触发组件）。
- 跨地图寻路/射击、六边形裁切、连续地形。

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
