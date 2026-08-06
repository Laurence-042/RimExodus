# 第一阶段：VMF 调研

状态：已完成。

上级设计：[无缝世界地块探索](无缝世界地块探索.md)
下一阶段：[第二阶段：最小技术原型](第二阶段-最小技术原型.md)

本阶段的目标是确认 Vehicle Map Framework（VMF）中哪些机制能够复用，并识别“把相邻地块显示在宿主地图边界外”所需的额外实现。

> 后续原型验证修正：边界裁剪不能在 footprint 相交时直接取消整块裁剪平面，而应从四块原版裁剪矩形中精确减去所有 footprint；口袋地图也不能泛化重放全部 SectionLayer，详见第二阶段记录。

记录 VMF 中与本系统直接相关的实现：

- Pocket Map 的创建和生命周期。
- 地图坐标转换。
- 地图绘制与选择。
- 车辆楼梯或其他跨地图入口。
- Pawn 转移流程。
- 地图锚点与车辆逻辑之间的耦合。
- 地图位于宿主边界之外时可能受到影响的代码。
- 存档和地图宿主迁移机制。

调研内容应包含对应源码位置和实际行为。文档描述与实现不一致时，以当前实现为准。

## 1.1 关键结论（先读）

VMF 的 Pocket Map 机制建立在 **RimWorld 原生 `PocketMapParent` 类**之上，并非 VMF 独有。RimWorld 通过 `Find.World.pocketMaps` 列表原生支持"宿主地图 + 口袋地图"结构。这对本系统是重大利好：宿主迁移（把地块地图从一个宿主换到另一个宿主）在底层是受支持的。

但 VMF 的**渲染与车辆强耦合**：口袋地图要么把行星视图渲染进 RenderTexture 再画成网格（`Patch_Map_MapUpdate`），要么在车辆位置直接绘制 SectionLayer。这套设计面向"跟随移动车辆的小尺寸地图"，**不直接适用于静态、大尺寸、位于宿主边界之外的世界地块地图**。因此本系统大概率需要：复用 `PocketMapParent`/`sourceMap` 机制 + 自行实现地图叠加层渲染，而不是直接依赖 VMF 的渲染管线。

## 1.2 Pocket Map 的创建和生命周期

- 基类：`references/RimWorldDecompiled/RimWorld.Planet/PocketMapParent.cs`。字段 `Map sourceMap`（宿主地图）与 `MapGeneratorDef mapGenerator`，`ExposeData` 会保存两者。
- 创建：`references/VehicleMapFramework/Source/VehicleMapFramework/Things/VehiclePawnWithMap.cs` 的 `GenerateVehicleMap(Map sourceMap)`（约 548 行）：
  1. 用 `WorldObjectMaker.MakeWorldObject(VMF_DefOf.VMF_VehicleMap)` 创建 `MapParent_Vehicle`（继承 `PocketMapParent`）。
  2. 设置 `mapParent.sourceMap = sourceMap`。
  3. 调用 `MapGenerator.GenerateMap(mapSize, mapParent, mapParent.MapGeneratorDef, mapParent.ExtraGenStepDefs, isPocketMap: true)`。
  4. 加入 `Find.World.pocketMaps`。
  5. 地图尺寸为 `props.size + 2`（每轴各 +2）。
- 生命周期：地图在首次访问 `VehicleMap` 时**惰性生成**；`RemoveVehicleMap()`（约 628 行）将 `pocketMapParent.sourceMap = null`、从 `Find.World.pocketMaps` 移除、调用 `Current.Game.DeinitAndRemoveMap(interiorMap, false)` 卸载。
- 对无缝地块的意义：`sourceMap` 字段就是"宿主"指针。把地块地图的 `sourceMap` 从基地地图改为旅行 Pocket Map，即可实现宿主迁移（见 1.8）。

## 1.3 地图坐标转换

- `references/VehicleMapFramework/Source/VehicleMapFramework/Utilities/VehicleMapUtility.cs`：
  - `ToBaseMapCoord(vehicle)`：`(original - pivot).RotatedBy(vehicle.FullAngle) + vehiclePos + OffsetFor(vehicle)`，`pivot` 为地图中心。
  - `ToVehicleMapCoord(vehicle)`：逆运算。
  - `OffsetFor(vehicle, rot)`：从 `VehicleMapProps`（offsetNorth/South/East/West 等）取偏移。
  - `TryGetVehicleMap(IntVec3 c, Map map, out vehicle)`：用 `VehicleMapGrid.VehicleAt(c)` 反查。
- 坐标转换依赖车辆的位置、朝向和偏移。对静态地块地图，若采用"地图相对宿主固定偏移"的模型，可简化掉旋转项，只保留平移。

## 1.4 地图绘制与选择

- 绘制（关键差异点）：`references/VehicleMapFramework/Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Map.cs` 的 `Patch_Map_MapUpdate`（约 228 行）：
  - 当 `drawPlanet` 开启且当前聚焦车辆地图时，把**行星视图渲染进 RenderTexture**（`TextureSize=2048`），再作为网格画在宿主地图上（`MeshSize=200`）。
  - 备选：`VehiclePawnWithMap.DrawVehicleMapMesh()` 直接在车辆位置绘制 SectionLayer。
  - 该渲染管线面向"跟随车辆的小地图"，且依赖 `Find.WorldCamera`/行星视图，**不适合大尺寸静态地块地图**。
- 选择：`Patches_Selector.cs` 的 `Patch_Selector_SelectableObjectsUnderMouse` 用 `TryGetVehicleMap` + `ToVehicleMapCoord` 把鼠标位置映射到口袋地图并选中对象。`UI/Command_FocusVehicleMap.cs` 的 `FocusedVehicle`/`FocusLockedVehicle` 跟踪聚焦状态（`FocusVehicle` 是 `IDisposable`，用于作用域聚焦）。
- 对无缝地块的意义：选择逻辑（坐标反查）可复用；渲染逻辑需自行实现（把相邻地块地图作为叠加层画在宿主地图上）。

## 1.5 跨地图入口（车辆楼梯）

- `references/VehicleMapFramework/Source/VehicleMapFramework/Comps/CompVehicleEnterSpot.cs`：`ThingComp`，标记入口点。`Available` 检查对侧格子是否越界/可扩展；`EnterVehiclePosition` 委托给 `CrossMapReachabilityUtility`。
- `references/VehicleMapFramework/Source/VehicleMapFramework/Utilities/CrossMapReachabilityUtility.cs`：`EnterVehiclePosition` 计算 Pawn 进入前站在宿主地图上的位置；`CanReach` 处理跨地图寻路。
- 对无缝地块的意义：入口机制（宿主地图上的一个"进入点" + 对侧口袋地图位置）可复用为地块接缝入口。

## 1.6 Pawn 转移流程

- `references/VehicleMapFramework/Source/VehicleMapFramework/Jobs/ToilsAcrossMaps.cs` 的 `GotoTargetMap`：走到出口点 → 开门 → 地图过渡动画（用 `driver.drawOffset` 让 Pawn 视觉上连续移动）→ `DeSpawnWithoutJobClear()` + `GenSpawn.Spawn(pawn, pos, map, rot)` 把 Pawn 从一张地图转移到另一张。
- `references/VehicleMapFramework/Source/VehicleMapFramework/Jobs/JobDrivers/JobDriverAcrossMaps.cs`：抽象 JobDriver，含 `exitSpotA/B`、`enterSpotA/B`、`spotsQueue`，处理跨地图点的存档/读档。
- 对无缝地块的意义：`DeSpawn + GenSpawn` 的转移方式 + `drawOffset` 视觉过渡，正是"Pawn 直接走进相邻地图"所需的核心机制，可整体复用。

## 1.6.1 跨地图寻路（核心能力之一）

VMF 的跨地图寻路是"跨地块追击入侵者"功能的基础，也是本系统必须复用的核心。

- 核心：`references/VehicleMapFramework/Source/VehicleMapFramework/Utilities/CrossMapReachabilityUtility.cs`。
  - 用 `ConditionalWeakTable<Pawn, Map>` 记录每个 Pawn 的 `DestMap`（目的地地图）与 `DepartMap`（出发地地图），以及 `DepartPosition`（出发位置）。这些是跨地图寻路的"上下文"。
  - `CanReach(departMap, root, dest, peMode, traverseParms, destMap, out exitSpot, out enterSpot, out spotsQueue)`：核心入口。若 `departMap == destMap` 走原版 `reachability.CanReach`；否则进入跨地图逻辑。
  - 跨地图逻辑：把"出发地图上的根位置"与"目的地地图上的目标"通过**入口点对**（`exitSpot`/`enterSpot`）连接。支持三种情况：车辆地图→基地地图、基地地图→车辆地图、两个不同车辆地图之间。
  - 两种算法：`aStarTraverse` 设置开启时用 `AStar<MapTraverse>` 在"地图图"上搜索（`traverser.Neighbors` 跨地图扩展邻居）；否则用 legacy 遍历（枚举 `GetSortedEnterComps` 入口组件 + `CachedWalkableMapEdgeCells` 地图边缘格子）。
  - 结果缓存：`CrossMapReachabilityCache.TryGetCache` 按 `(region, region2, parms)` 缓存。
  - `EnterVehiclePosition(enterSpot)`：计算 Pawn 进入前站在宿主地图上的位置（沿入口朝向向外推，直到离开车辆矩形）。
- 接入原版：`Patches_Map.cs` 的 `Patch_Reachability_CanReach` / `Patch_Reachability_CanReachNonLocal` / `Patch_Reachability_CanReachMapEdge` 用 Prefix 拦截原版 `Reachability.CanReach`，当出发/目的地地图不同时改走 `CrossMapReachabilityUtility.CanReach`。
- 对无缝地块的意义：这套"地图图 + 入口点对 + AStar"的跨地图寻路模型，可直接用于"Pawn 从地块 A 追击入侵者到地块 B"。只需把"入口点对"从车辆楼梯抽象为地块接缝入口。

## 1.6.2 跨地图射击（核心能力之一）

VMF 让 Pawn/炮塔能**跨地图射击**（在车辆地图内射击基地地图上的目标，反之亦然），这是"跨地块追击入侵者"时 Pawn 必须能跨地块开火的关键。

- 目标搜索：`references/VehicleMapFramework/Source/VehicleMapFramework/Combat/AttackTargetFinderOnVehicle.cs`。
  - `BestAttackTarget`：把搜索范围扩展到 `searcherThing.Map.BaseMapAndVehicleMaps(false)`（宿主地图 + 所有车辆地图），用 `PositionOnBaseMapSpawned`（把各地图坐标统一映射到宿主地图）做距离/LOS/威胁判定。
  - 用 `GenClosestCrossMap.ClosestThing_Global` / `ClosestThingReachable` 跨地图找最近目标。
  - 接入：`Patches_Combat.cs` 的 `Patch_AttackTargetFinder_BestAttackTarget`（Postfix，当 `searcher.Thing.Map.CrossMapContext` 时用跨地图版本）与 `Patch_AttackTargetFinder_CanSee`（Prefix，跨地图时用 `searcher.CanSee`）。
- 视线（LOS）：`references/VehicleMapFramework/Source/VehicleMapFramework/Utilities/GenSightOnVehicle.cs`。
  - `LineOfSight(start, end, map)`：若 `map` 是车辆地图且车辆已生成，先把坐标 `ToBaseMapCoord` 映射到宿主地图再走原版视线；否则 `LineOfSightVehicleToVehicle` 在车辆地图内部做视线。
  - `LineOfSightThingToThing` / `LineOfSightThingToTarget`：把 Thing 位置映射到宿主地图后做视线。
- 射击线：`references/VehicleMapFramework/Source/VehicleMapFramework/Utilities/VerbOnVehicleUtility.cs`。
  - `TryFindShootLineFromToOnVehicle`：跨地图版本的 `TryFindShootLineFromTo`。把 caster/target 位置映射到宿主地图（`PositionOnBaseMap`/`TargetCellOnBaseMap`），处理近战、射程、LOS、lean 射击源。
  - `ShouldConsiderCrossMap(caster, root, targ)`：判断是否需要走跨地图射击线（caster 或 target 在车辆地图上，或射线上经过车辆地图格子）。
  - 接入：`Patches_Verb.cs` 的 `Patch_Verb_TryFindShootLineFromTo`（Prefix，`ShouldConsiderCrossMap` 为真时用跨地图版本）。
- 施法位置：`references/VehicleMapFramework/Source/VehicleMapFramework/Combat/CastPositionFinderOnVehicle.cs` 的 `TryFindCastPosition`（跨地图版施法位置搜索），接入 `Patches_Combat.cs` 的 `Patch_CastPositionFinder_TryFindCastPosition`。
- 炮塔：`Combat/TargetingHelperOnVehicle.cs` 的 `BestAttackTarget`（跨地图版炮塔目标搜索），接入 `Patches_VehicleFramework.cs` 的 `Patch_TargetingHelper_BestAttackTarget`。
- 弹道：`Patches_Verb.cs` 的 `Patch_Verb_LaunchProjectile_TryCastShot` 用 Transpiler 把 `Thing.Map`/`LocalTargetInfo.Cell`/`Thing.Position` 替换为 `BaseMap`/`CellOnBaseMapSpawned`/`PositionOnBaseMapSpawned`，使弹道在宿主地图坐标系中飞行。
- 对无缝地块的意义：跨地图射击的完整链路（目标搜索 → LOS → 射击线 → 施法位置 → 弹道）都可复用。核心是把"各地图坐标统一映射到宿主地图"（`PositionOnBaseMap`/`BaseMapAndVehicleMaps`），本系统只需把"宿主地图"从车辆地图的宿主扩展为地块接缝两侧的地图。

## 1.7 地图锚点与车辆逻辑之间的耦合

- `MapParent_Vehicle`（`VehicleMap/MapParent_Vehicle.cs`）持有 `VehiclePawnWithMap vehicle` 字段。
- 口袋地图的位置由车辆位置/朝向/偏移推导（`cachedDrawPos`/`cachedExactPos`、`VehicleMapProps`）。
- `VehicleMapGrid`（`MapComponents/VehicleMapGrid.cs`）：`MapComponent`，用 `vehicleGrid` 数组把宿主地图格子映射到车辆。
- `VehiclePawnWithMapCache`（`MapComponents/VehiclePawnWithMapCache.cs`）：缓存地图上的车辆，`FinalizeInit` 调用 `VehicleMapParentsComponent.SetCachedVehicle`。
- `VehicleSectionLayerManager`（`MapComponents/VehicleSectionLayerManager.cs`）：管理车辆地图 SectionLayer，带旋转缓存（每个朝向层 4 个旋转）。
- 对无缝地块的意义：这些组件把地图生命周期绑定到"车辆"这一实体。若地块地图不依附车辆，需要把"锚点"抽象为静态宿主（如基地地图或旅行 Pocket Map），去掉旋转/偏移耦合。

## 1.8 存档和地图宿主迁移机制

- `PocketMapParent.ExposeData` 保存 `sourceMap` 与 `mapGenerator`。
- `VehiclePawnWithMap.ExposeData`（约 1397 行）保存 `interiorMap`、`allowEnter`、`allowExit`。
- 读档时 `SpawnSetup`（约 653 行）重新关联 `interiorMap.PocketMapParent.sourceMap = map`。
- `WorldComponents/VehicleMapParentsComponent.cs`：`WorldComponent`，按地图 uniqueID 缓存 `MapParent_Vehicle`。
- 宿主迁移：`sourceMap` 是普通字段，可在运行时改写。把地块地图的 `sourceMap` 从基地地图改为旅行 Pocket Map（或反之），即可迁移宿主；配合 `Find.World.pocketMaps` 的增删即可完成。
- 对无缝地块的意义：宿主迁移在底层受原生支持，是本系统"基地 → 旅行 Pocket Map → 下一地块"流程的关键依据。

## 1.9 对无缝地块系统的架构评估

- **可复用**：`PocketMapParent`/`sourceMap` 宿主机制、`Find.World.pocketMaps`、跨地图 Pawn 转移（`ToilsAcrossMaps`/`JobDriverAcrossMaps`）、入口机制（`CompVehicleEnterSpot`/`CrossMapReachabilityUtility`）、坐标反查选择逻辑、**跨地图寻路**（`CrossMapReachabilityUtility` 的"地图图 + 入口点对 + AStar"模型）、**跨地图射击**（`AttackTargetFinderOnVehicle`/`GenSightOnVehicle`/`VerbOnVehicleUtility` 的"坐标统一映射到宿主地图"模型）。
- **需自行实现**：地图叠加层渲染（VMF 的 RenderTexture/行星视图方案面向车辆小地图，不适合大尺寸静态地块地图）、把"锚点"从车辆抽象为静态宿主、六边形裁切与连续地形。
- **结论**：倾向于"复用 PocketMapParent 机制 + 自行实现叠加层渲染"，而非直接依赖 VMF 渲染管线。跨地图寻路与射击的"坐标统一映射到宿主地图"模型可直接复用，是实现"跨地块追击入侵者"（Pawn 跨地块射击）的关键。是否把 VMF 作为正式依赖，由第 2 步最小技术原型验证后决定。

## 1.10 口袋地图能否显示在宿主地图边界之外（核心限制）

这是本系统最关键的渲染约束：相邻地块地图必然有一部分位于宿主地图边界之外。调研结论如下。

**宿主地图自身的绘制被严格限制在地图边界内：**

- `Map.MapUpdate()`（`Verse/Map.cs` ~1176）只在 `drawingMap && Find.CurrentMap == this` 时绘制，绘制顺序为：
  1. `mapDrawer.DrawMapMesh()` — 只绘制 `ViewRect`（`Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map)`）内的 Section，即宿主地图自身**不会**绘制边界外的任何内容。
  2. `dynamicDrawManager.DrawDynamicThings()` — 绘制动态物体（**VMF 车辆地图正是在这一步作为 Thing 被绘制**）。
  3. `MapEdgeClipDrawer.DrawClippers(this)` — **最后**绘制地图边缘裁剪平面。
- `MapEdgeClipDrawer.DrawClippers`（`Verse/MapEdgeClipDrawer.cs`）在地图四边绘制 500 单位宽的黑色裁剪平面（`ClipMat`，颜色 0.1/0.1/0.1，`AltitudeLayer.WorldClipper` 高度），把地图边界外的区域涂黑。它由 `Map.DrawMapClippers`（`!generatorDef.disableMapClippers`）控制，可通过 `MapGeneratorDef.disableMapClippers = true` 关闭。

**VMF 车辆地图的绘制方式（关键差异）：**

- `VehiclePawnWithMap.DrawVehicleMap()`（~1046）在 `DrawAt()`（~981）和 `DynamicDrawPhaseAt()`（~1032）中调用——这两个是 **Thing 的绘制方法**，由宿主地图的 `DynamicDrawManager` 在绘制动态物体时调用。因此**不依赖 `Map.MapUpdate` 的 `Find.CurrentMap == this` 检查**。
- `DrawVehicleMapMesh(drawPos, map)`（~1093）遍历 Section，`DrawSection`（~1112）用 `Graphics.DrawMesh(subMesh.mesh, drawPos, rot, ...)` 把各 SectionLayer（`SectionLayer_TerrainOnVehicle`/`SnowOnVehicle`/`ThingsGeneral` 等）绘制到宿主地图上。`drawPos` 由 `ToBaseMapCoord` 计算（车辆地图局部坐标 → 宿主地图坐标），`rot` 为车辆朝向旋转。**网格顶点是车辆地图局部坐标，通过 drawPos 平移 + rot 旋转定位到宿主地图任意位置。**
- `SectionLayer_TerrainOnVehicle` 继承 `SectionLayer_Terrain`，网格在车辆地图局部坐标中生成，绘制时再偏移。
- VMF 自己的 `DrawClippers(map)`（~1307）只在车辆地图聚焦时（`FocusLockedVehicle`/`FocusedVehicle`）绘制车辆地图边界裁剪，且**不调用宿主地图的 `MapEdgeClipDrawer.DrawClippers`**（原调用被注释掉）。

**结论：**

1. **技术上可以让口袋地图显示在宿主地图边界之外**。VMF 证明了一条可行路径：把口袋地图作为宿主地图上的一个 **Thing** 绘制（`DrawAt`/`DynamicDrawPhaseAt` + `Graphics.DrawMesh` 带 drawPos 偏移），完全绕开 `Map.MapUpdate` 的 `Find.CurrentMap == this` 限制和 `MapDrawer` 的 ViewRect 裁剪。
2. **但存在一个必须处理的遮挡问题**：宿主地图的 `MapEdgeClipDrawer.DrawClippers` 在 `Map.MapUpdate` 中**最后**绘制黑色裁剪平面，会覆盖宿主边界外的口袋地图区域。处理方案：
   - **方案 A**：宿主地图 `generatorDef.disableMapClippers = true`（关闭整个宿主地图的边界裁剪，影响面大）。
   - **方案 B（最终采用）**：Harmony patch `MapEdgeClipDrawer.DrawClippers`，从四块原版裁剪矩形中精确减去所有已加载/可见相邻地图的 footprint，再绘制剩余矩形。不能在相交时直接取消整块裁剪平面，否则 footprint 外的像素可能缺少本帧颜色写入，并在拖动时保留上一帧残影。
   - **方案 C**：完全复用 VMF 的 `DrawVehicleMap` 思路，把口袋地图作为 Thing 绘制并自行处理裁剪（VMF 已证明可行，但怀疑VMF的地图也会因默认的`MapEdgeClipDrawer.DrawClippers`被覆盖，所以优先方案B）。
3. **操作（选取/移动命令）不受边界限制**：VMF 的 `Patch_Selector_SelectableObjectsUnderMouse` 用 `TryGetVehicleMap` + `ToVehicleMapCoord` 反查，把宿主地图上的鼠标位置映射到口袋地图坐标，与边界无关。
4. **接缝重叠与无缝传送（关键设计）**：对端地图与本地地图在接缝处**必须有物理重叠**，而不是恰好相接。这样本端地图的传送点（exit spot）与对端地图的传送点（enter spot）在宿主地图坐标（drawPos）上**重合于同一 tile**。玩家单位走到本端传送点后，通过跨地图转移（`ToilsAcrossMaps.GotoTargetMap`）无缝传送到对端地图**相同 drawPos 的 tile** 上的对端传送点，视觉上单位位置不跳变，实现无缝移动。这要求：
   - 两图在接缝方向上有重叠带（overlap band），重叠宽度需容纳传送点对。
   - 传送点对在宿主坐标上严格对齐（`ToBaseMapCoord` 映射后坐标一致）。
   - 重叠带内的地形/物体需在两图间保持一致，避免视觉断裂。
5. **对最小技术原型（第 2 步）的意义**：原型已经验证相邻地图可显示在殖民地地图边界之外，并采用方案 B 的精确矩形差集处理裁剪。叠加渲染最终使用主相机背景 `CommandBuffer`，只重放精确类型的主 Terrain；传送点重合与无缝转移仍待验证，详见[第二阶段：最小技术原型](第二阶段-最小技术原型.md)。
