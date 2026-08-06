# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

为了验证此文件确实完整读入，你需要在对话开始给出这个文件的目录

## 项目概述

RimWorld Mod：实现"无缝世界地块探索"系统，使相邻世界地块的局部地图在视觉与操作上连续连接，Pawn 可直接从一张地图走入相邻地图，无需组成远行队。

设计文档：`doc/无缝世界地块探索.md`（长期设计记录，含 VMF 调研结果）。

## 依赖引用目录

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
12. **口袋地图能否显示在宿主边界之外（核心限制）**：宿主地图 `Map.MapUpdate`（`Verse/Map.cs` ~1176）只在 `Find.CurrentMap == this` 时绘制，`MapDrawer.DrawMapMesh` 只画 ViewRect（裁剪到地图边界）内；`MapEdgeClipDrawer.DrawClippers`（`Verse/MapEdgeClipDrawer.cs`）在地图四边画 500 单位宽黑色裁剪平面（`ClipMat`，`AltitudeLayer.WorldClipper`），在 `Map.MapUpdate` 中**最后**绘制（`dynamicDrawManager.DrawDynamicThings()` 之后），会遮挡宿主边界外的口袋地图。VMF 车辆地图通过 `DrawAt`/`DynamicDrawPhaseAt`（Thing 绘制方法，`VehiclePawnWithMap.cs` ~981/~1032）用 `Graphics.DrawMesh(subMesh.mesh, drawPos, rot, ...)` 绘制到宿主地图任意位置（`drawPos` 由 `ToBaseMapCoord` 计算），**绕开 `Find.CurrentMap == this` 限制**；`SectionLayer_TerrainOnVehicle` 继承 `SectionLayer_Terrain`，网格在车辆地图局部坐标生成再偏移。VMF 自己的 `DrawClippers`（~1307）只在聚焦时画车辆地图边界裁剪，不调用宿主 `MapEdgeClipDrawer.DrawClippers`（被注释）。**结论**：技术上可显示在宿主边界外，但必须处理宿主 `MapEdgeClipDrawer.DrawClippers` 遮挡——方案 A 宿主 `generatorDef.disableMapClippers=true`；方案 B（推荐）patch `MapEdgeClipDrawer.DrawClippers` 跳过与对端地图 footprint 相交的裁剪平面；方案 C 复用 VMF Thing 绘制 + 自绘裁剪（VMF 已证明可行，但**怀疑 VMF 的地图也会因默认的 `MapEdgeClipDrawer.DrawClippers` 被覆盖**，故优先方案 B）。选取/移动命令用 `ToVehicleMapCoord` 反查，与边界无关。**两个关键设计点**：(1) 方案 B 的裁剪跳过范围必须覆盖对端地图**整个可见 footprint**（Pawn 到边界时对端地图大部分在宿主边界外，只跳接缝线仍会被涂黑），判定以"裁剪平面是否与已加载/可见对端地图 footprint 相交"为准；(2) 对端地图与本地地图在接缝处**物理重叠**（overlap band），本端传送点与对端传送点在宿主坐标（drawPos）上重合于同一 tile，Pawn 走到本端传送点后经 `ToilsAcrossMaps.GotoTargetMap` 无缝传送到对端相同 drawPos 的 tile，视觉位置不跳变。

## 架构评估

- **可复用**：`PocketMapParent`/`sourceMap` 宿主机制、`Find.World.pocketMaps`、跨地图 Pawn 转移、入口机制、坐标反查选择、**跨地图寻路**（地图图 + 入口点对 + AStar 模型）、**跨地图射击**（坐标统一映射到宿主地图模型）。
- **需自行实现**：地图叠加层渲染（VMF 渲染管线面向车辆小地图）、把"锚点"从车辆抽象为静态宿主、六边形裁切与连续地形、**处理宿主 `MapEdgeClipDrawer.DrawClippers` 对边界外口袋地图的遮挡**（patch `MapEdgeClipDrawer` 或 Thing 绘制 + 自绘裁剪）。
- **倾向方案**：复用 PocketMapParent 机制 + 自行实现叠加层渲染，而非直接依赖 VMF 渲染管线。跨地图寻路与射击的"坐标统一映射到宿主地图"模型可直接复用，是实现"跨地块追击入侵者"（Pawn 跨地块射击）的关键。是否把 VMF 作为正式依赖，由最小技术原型验证后决定。

## 需要记住的事项

- 本仓库 grep 需用**绝对路径 + 正斜杠**（如 `d:/SteamLibrary/...`），相对路径会失败。
- `memory` 工具与 `create_file` 对超 ~150 行的内容有截断 bug：先建 stub，再分块（≤150 行）插入。
- 设计文档 `doc/无缝世界地块探索.md` 的"当前阶段计划"定义了 5 个推进步骤：VMF 调研 → 最小技术原型 → 旅行 Pocket Map → 六边形裁切 → 连续地形。
- 最小技术原型验证点：地图能否绘制/选取/接收移动命令、跨地图入口往返、存档读档恢复。**渲染上已验证可行**（VMF 把口袋地图作为 Thing 绘制可显示在宿主边界外），但原型必须同时处理宿主 `MapEdgeClipDrawer.DrawClippers` 遮挡（见核心结论 12）。

## 最小技术原型实现（已完成，可编译）

原型目标：验证"地图能否绘制/选取/接收移动命令、跨地图入口往返、存档读档恢复"。采用**矩形地图**（不做六边形裁切与连续地形），复用 `PocketMapParent`/`sourceMap` 宿主机制 + 自行实现叠加层渲染（不依赖 VMF 渲染管线）。

### 项目结构
- `About/About.xml` — mod 元数据，packageId `RimExodus.SeamlessWorld`，依赖 `brrainz.harmony`，支持 1.6。
- `Source/RimExodus.csproj` — net48，引用 `Krafs.Rimworld.Ref 1.6.4633` + `Lib.Harmony.Ref 2.4.2`，输出到 `..\1.6\Assemblies\`。
- `1.6/Defs/WorldObjectDefs/WorldObjects.xml` — `RimExodus_SeamlessTileMap` WorldObjectDef，worldObjectClass `RimExodus.MapParent_SeamlessTile`，mapGenerator `RimExodus_SeamlessTileGenerator`。
- `1.6/Defs/MapGeneration/SeamlessTileGenerator.xml` — `RimExodus_SeamlessTileGenerator` MapGeneratorDef（pocketMapProperties biome BorealForest）+ `RimExodus_SeamlessTile` GenStepDef（genStep Class="RimExodus.GenStep_SeamlessTile"）。

### 源码文件（`Source/`）
- `RimExodusMod.cs` — `[StaticConstructorOnStartup]`，`new Harmony("RimExodus.SeamlessWorld").PatchAll()`。
- `MapParent_SeamlessTile.cs` — `PocketMapParent` 子类。字段：`worldTile`、`direction`（0=北,1=东,2=南,3=西）、`hostOffset`（宿主坐标平移）、`neighborTiles`（`List<int>`）。`ExposeData` 存全部字段。
- `SeamlessMapUtility.cs` — 坐标转换：`ToHostCoord`/`ToLocalCoord`/`ToHostDrawPos`/`HostCellInFootprint`。静态地块无旋转，仅平移 `hostOffset`。
- `SeamlessTileManager.cs` — `MapComponent`。`GenerateTileMap(direction, mapSize, overlapBand)`：`WorldObjectMaker.MakeWorldObject` → 设 `sourceMap`/`Tile=0`/`direction`/`hostOffset` → `MapGenerator.GenerateMap(..., isPocketMap: true)` → 加入 `Find.World.pocketMaps` + `Find.World.worldObjects` → 共享宿主 skyManager/weather。`ComputeHostOffset` 把口袋地图放宿主边界外并留重叠带。`GetTileMapInDirection`/`RemoveTileMap`。
- `SeamlessTileRenderer.cs` — `MapComponent`，`MapComponentDraw()` 遍历 `Find.World.pocketMaps` 中 `sourceMap == map` 的口袋地图，用 `AccessTools.FieldRefAccess` 取 `MapDrawer.sections`/`Section.layers`，对 dirty section 调 `RegenerateAllLayers()`，再用 `Graphics.DrawMesh(subMesh.mesh, drawPos, rot, ...)` 绘制（drawPos = `hostOffset.ToVector3()`，rot = identity）。**注意**：不能依赖 `MapMeshDrawerUpdate_First` 的 ViewRect 逻辑（口袋地图 section 不在宿主 ViewRect 内），须直接 `RegenerateAllLayers()`。
- `Patch_MapEdgeClipDrawer_DrawClippers.cs` — 方案 B。`Prefix` 收集 `SeamlessTileRegistry.GetFootprintsOnHost(map)`，若非空则手动绘制四条裁剪平面、跳过与 footprint 相交的边（`footprint.Overlaps(edgeRect)`），返回 false。带 `MaterialPropertyBlock` 纹理缩放/偏移（对齐原版）。
- `SeamlessTileRegistry.cs` — `GetFootprintsOnHost(Map)` 返回宿主坐标 `List<CellRect>`（局部矩形 + hostOffset）。
- `GenStep_SeamlessTile.cs` — `GenStep`，`Generate` 铺设矩形地形：边缘 2 格不可通行（WaterOceanDeep），内部可通行（Soil）。
- `CompSeamlessTileEnterSpot.cs` — `ThingComp` 入口点，`direction` 属性，`AdjacentTileParent` 经宿主 `SeamlessTileManager.GetTileMapInDirection` 查相邻地块。
- `SeamlessMapTransfer.cs` — 跨地图 Pawn 转移：`TransferPawnToTile`（宿主→地块，目标局部坐标 = 宿主坐标 - hostOffset，`DeSpawn()` + `GenSpawn.Spawn()`）、`TransferPawnToHost`（地块→宿主）。
- `SeamlessMapTransferTrigger.cs` — `MapComponent`，每 30 tick 检查 `Find.CurrentMap` 上是否有 Pawn 站在 `CompSeamlessTileEnterSpot` 传送点上，触发转移。

### 关键实现要点
- **渲染顺序**：`Root_Play.Update()` 先 `base.Update()`（→ `UIRootUpdate` → `MapComponentOnDraw` 绘制口袋地图），后 `Game.UpdatePlay()`（→ `Map.MapUpdate` → `DrawClippers`）。故口袋地图先画、裁剪平面后画，方案 B patch 必须跳过与 footprint 相交的边，否则覆盖口袋地图。
- **MapComponent 自动注册**：`Map.FillComponents` 自动实例化所有 `MapComponent` 非抽象子类，无需手动注册。
- **编译**：`cd Source; dotnet build RimExodus.csproj -c Debug`，输出 `1.6/Assemblies/RimExodus.dll`。已通过（0 错误 0 警告）。
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
- 默认地图尺寸 50×50，overlapBand=4。`CurrentManager` 取 `Find.CurrentMap.GetComponent<SeamlessTileManager>()`。

### 自动生成北侧（`SeamlessTileManager`）
- `MapGenerated()` 钩子：开档自动生成北侧地块（`autoGeneratedNorth` 标志防重复）。
- **关键坑**：`MapComponentUtility.MapGenerated(map)` 在 `MapGenerator.GenerateMap` **内部**调用（`MapGenerator.cs` ~193），此时 `MapGenerator.mapBeingGenerated` 仍非空，直接调 `GenerateTileMap` 会被拒绝返回 null。**必须延迟到下一 tick**：`MapGenerated()` 里设 `pendingAutoGenerateTicks = 1`，`MapComponentTick()` 里递减到 0 再调 `TryAutoGenerateNorth()`。
- `TryAutoGenerateNorth`：若方向 0 已存在则跳过，否则 `GenerateTileMap(0, mapSize, 4)`。
- **致命坑（已修复）**：`SeamlessTileManager` 是 `MapComponent`，会被 `Map.FillComponents` 自动实例化到**每一张地图**上，包括 `GenerateTileMap` 生成的口袋地图本身。若不在口袋地图上跳过，口袋地图的 `MapGenerated()` 也会触发自动生成 → "生成北侧 → 生成口袋地图 → 口袋地图又生成北侧"的**无限递归卡死**（日志刷屏 `Auto-generated north seamless tile map`）。修复：`MapGenerated()` 和 `MapComponentTick()` 开头都加 `if (map.IsPocketMap) return;`。
- **性能坑（已修复）**：`Section.RegenerateAllLayers()` **不会清除 `dirtyFlags`**（只有 `TryUpdate` 会，且 `TryUpdate` 依赖 `bounds.Overlaps(view)` 宿主 ViewRect，口袋地图在边界外恒 false）。若 `RegenerateAllLayers()` 后不手动 `section.dirtyFlags = 0uL`，只要 dirtyFlags != 0，**每帧都会重建整个口袋地图的所有 SectionLayer 网格**（地形/建筑/植物/光照），大量内存分配 + GC 卡顿。修复：`SeamlessTileRenderer.DrawPocketMap` 里 `RegenerateAllLayers()` 后手动清零 `dirtyFlags`。
- **深度冲突坑（已修复）**：口袋地图地形与宿主地图地形都在 `AltitudeLayer.Terrain`（`Alts[2]=0.7317`）**同一高度**。绘制顺序：`MapComponentOnDraw`（口袋地图，`UIRoot_Play.UIRootUpdate`→`MapInterfaceUpdate`）**先画**，`Map.MapUpdate`（宿主 `DrawMapMesh` + `DrawClippers`）**后画**。地形 shader 用严格 `ZTest Less`，同深度时**先画的口袋地图赢**（后画的宿主 fragment 深度不严格小于被剔除）→ 对侧覆盖本侧 + 拖动时宿主/裁剪平面无法覆盖口袋旧位置留下残影。修复：`SeamlessTileRenderer` 里 `drawPos.y -= PocketMapAltitudeOffset`（0.01f），压低口袋地图整体高度，让宿主地形在重叠区通过深度测试覆盖口袋地图。
- **重叠带宽度**：`TryAutoGenerateNorth` 里 `overlapBand` 从 4 改为 5（用户确认边界应为 5 格）。

### 验证
- `dotnet build` 通过（0 错误 0 警告），输出 `1.6/Assemblies/RimExodus.dll`。
- **测试方式**：开档后北侧地块自动生成（渲染器/裁剪 patch 有东西可画）；Dev 菜单 "RimExodus" 分类下可手动生成其余 5 向、生成全部、卸载全部。

### 仍待办
- 传送点 Thing 的自动生成与放置（当前 `CompSeamlessTileEnterSpot` 需手动放置 Thing，转移触发仍无法自动化测试）。
- 双向转移（地块→宿主无触发组件）。
- 跨地图寻路/射击、六边形裁切、连续地形。
