# AGENTS.md — RimExodus 无缝世界地块探索

本文件记录本仓库的依赖引用、关键内容与需要长期记住的事项，供后续开发时快速恢复上下文。

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

## 架构评估

- **可复用**：`PocketMapParent`/`sourceMap` 宿主机制、`Find.World.pocketMaps`、跨地图 Pawn 转移、入口机制、坐标反查选择、**跨地图寻路**（地图图 + 入口点对 + AStar 模型）、**跨地图射击**（坐标统一映射到宿主地图模型）。
- **需自行实现**：地图叠加层渲染（VMF 渲染管线面向车辆小地图）、把"锚点"从车辆抽象为静态宿主、六边形裁切与连续地形。
- **倾向方案**：复用 PocketMapParent 机制 + 自行实现叠加层渲染，而非直接依赖 VMF 渲染管线。跨地图寻路与射击的"坐标统一映射到宿主地图"模型可直接复用，是实现"跨地块追击入侵者"（Pawn 跨地块射击）的关键。是否把 VMF 作为正式依赖，由最小技术原型验证后决定。

## 需要记住的事项

- 本仓库 grep 需用**绝对路径 + 正斜杠**（如 `d:/SteamLibrary/...`），相对路径会失败。
- `memory` 工具与 `create_file` 对超 ~150 行的内容有截断 bug：先建 stub，再分块（≤150 行）插入。
- 设计文档 `doc/无缝世界地块探索.md` 的"当前阶段计划"定义了 5 个推进步骤：VMF 调研 → 最小技术原型 → 旅行 Pocket Map → 六边形裁切 → 连续地形。
- 最小技术原型验证点：地图能否绘制/选取/接收移动命令、跨地图入口往返、存档读档恢复。
