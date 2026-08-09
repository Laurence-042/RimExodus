# 阶段4a：邻居预加载 + 多跳传送点（预铺+延迟绑定）

## 用户确认的全部决策

| 决策项 | 选择 |
|---|---|
| 阈值配置 | ModSettings 可调（距离 + 是否开档加载所有邻居） |
| 扫描策略 | 事件驱动（Hook StartJob，仅 playerForced 的 Goto） |
| 多跳机制 | 预铺+延迟绑定：沿全部世界邻居边预铺单端 spot（对端 null + targetWorldTile 标记），邻居加载后按 targetWorldTile 匹配+坐标校验互绑 |
| Comp 标记 | 加 targetWorldTile 字段（持久化） |
| 边界带构建算法 | **算法 C：凸多边形内缩 + 扫描线差集**（复用现成 ScanlineFill） |
| 存储结构 | **Dictionary<IntVec3,int>**（考虑未来高频查询如撤退袭击者） |
| 开档行为 | 预铺全部边传送点；邻居默认不加载（ModSettings 可配开档加载全部） |
| 防重入 | generatingTiles HashSet 记录生成中的 worldTile |

---

## 实现步骤（9 步）

### 第1步：ModSettings 配置载体
**新增 `Source/RimExodusSettings.cs`**：
```csharp
public class RimExodusSettings : ModSettings {
    public int borderPreloadDistance = 15;       // 触发预加载的边界距离（格）
    public bool preloadAllNeighborsOnStart = false; // 开档加载所有邻居
    public override void ExposeData() {
        Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
        Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
    }
}
```
**修改 `Source/RimExodusMod.cs`**：从 static class 改为继承 Mod 的实例类（保留 `[StaticConstructorOnStartup]` 做 PatchAll），持 `public static RimExodusSettings Settings`，构造器 `Settings = GetSettings<RimExodusSettings>()`。

### 第2步：CompSeamlessTileEnterSpot 扩展标记
**修改 `Source/CompSeamlessTileEnterSpot.cs`**：
- 加 `public int targetWorldTile = -1;`（该 spot 指向的对端世界地块；-1=未设置）
- `PostExposeData` 加 `Scribe_Values.Look(ref targetWorldTile, "targetWorldTile", -1)`

### 第3步：凸多边形内缩 + 边界带构建（算法 C，几何核心）
**修改 `Source/SeamlessPolygonGeometry.cs`**，新增两个方法：

**(a) `InsetPolygon(List<Vector2> verts, float insetDist)`** — 凸多边形各边沿内法向平移 insetDist，求相邻平移边交点得到内缩多边形：
```
对每条边 j（v[j]→v[j+1]）：
  edge = v[j+1] - v[j]
  normal = normalize(perp(edge))  // 垂直且朝内（朝中心方向）
  平移边 j：v'[j] = v[j] + insetDist*normal, v'[j+1] = v[j+1] + insetDist*normal
内缩多边形顶点 k = 平移边 (k-1) 与平移边 k 的交点（2×2 线性方程组）
```
- 凸性保证：内缩后仍凸（insetDist < apothem 时）
- 保护：`if (insetDist >= minApothem) 返回空多边形`（整图都是边界带）

**(b) `ComputeEdgeBand(int worldTile, int mapSize, int bandWidth, Dictionary<IntVec3,int> result, List<int> neighborWorldTiles)`** — 用算法 C 构建边界带：
```
1. verts = BuildPolygonVertices(worldTile, mapSize)
2. innerVerts = InsetPolygon(verts, bandWidth)
3. 两次 ScanlineFill：origRow[z]=(a,b), innerRow[z]=(c,d)
4. 每行差集：band = [a, c-1] ∪ [d+1, b]
   对 band 内每个格：判定它属于哪条边（用格中心方位角匹配 edgeAngle，或算到 6 条边最近的那条）
   result[cell] = neighborWorldTiles[bestEdge]
```
**关键：格属于哪条边的判定**——边界带格必靠近某条原边，算格中心到 6 条原边的最近距离，取最近的那条边的 edgeIdx（对应 neighborWorldTiles[edgeIdx]）。这是 O(6)/格，但只在边界带格上算（约 1.5 万格 × 6 = 9 万次，可接受，一次性构建）。

### 第4步：边界带速查表 MapComponent
**新增 `Source/SeamlessBorderLookup.cs`**（MapComponent，每图一份）：
- 字段：`Dictionary<IntVec3,int> borderCells`（边界带格 → neighbor worldTile）
- 构建：`MapGenerated()` 延迟 1 tick（避开 mapBeingGenerated），取 worldTile + 世界邻居列表，调 `SeamlessPolygonGeometry.ComputeEdgeBand(...)`
- 查询 API：`bool TryGetPreloadTarget(IntVec3 cell, out int worldTile)` → `borderCells.TryGetValue(cell, out worldTile)`
- 阈值从 `RimExodusMod.Settings.borderPreloadDistance` 读

### 第5步：传送点预铺（全部世界邻居边，单端，留空对端）
**修改 `Source/SeamlessTileManager.cs`**：
- **新增静态 `PlaceEnterSpotsAllNeighbors(Map map, int worldTile)`**：
  - 取 `verts` + 世界邻居列表（`GetTileNeighbors`）
  - 对每条边 j：`EnumerateEdgeCells(verts, j, mapSize)` 枚举格
  - 每个 `cell`：若 `InBounds && Walkable` 且该格无同 def spot（幂等查重）→ Spawn 单端 spot，设 `comp.targetWorldTile = neighbors[j].tileId`，`comp.CounterpartSpot = null`
  - 锚点和口袋都适用（不依赖 MapParent 类型）
- **修改 `GenerateTileMap`**：移除原 `PlaceEnterSpots(parent, interiorMap, source, new)` 调用，新地块 GenStep 后调 `PlaceEnterSpotsAllNeighbors(interiorMap, newWorldTile)`
- **GenStep_SeamlessTile**：`ApplyPolygonTerrain` 后调 `PlaceEnterSpotsAllNeighbors(map, worldTile)`（口袋地块自己的全部边）
- **锚点 A**：`MapGenerated` 延迟后调 `PlaceEnterSpotsAllNeighbors(map, map.Tile)`

### 第6步：延迟绑定（邻居加载后互绑对端）
**新增 `Source/SeamlessEnterSpotBinder.cs`**（静态工具）：
- `BindUnboundSpotsBetween(Map mapA, int worldTileA, Map mapB, int worldTileB, IntVec3 offsetAtoB)`：
  - 收集 mapA 上 `targetWorldTile==worldTileB && CounterpartSpot==null` 的 spot，按格位置建临时字典 `cellA → spot`
  - 收集 mapB 上 `targetWorldTile==worldTileA && CounterpartSpot==null` 的 spot，建 `cellB → spot`
  - 对 mapA 每个 spot at `cellA`：
    - `expectedCellB = cellA - offsetAtoB`（两端世界坐标重合契约）
    - 查 mapB 字典是否有 `expectedCellB` 的 spot
    - 有则互绑 `CounterpartSpot`
- **在 `GenerateTileMap` 内**：新地块铺完自己的边后，遍历新地块所有**已存在**邻居（`SeamlessTileGraph.GetAllNeighbors(interiorMap)`），对每个调 `BindUnboundSpotsBetween(interiorMap, newWorldTile, neighborMap, neighborWorldTile, offsetNewToNeighbor)`（offset 从 NeighborInfo 取，注意方向）

### 第7步：邻居预加载入口（事件驱动 + 防重入）
**修改 `Source/SeamlessTileManager.cs`**：
- 新增字段：`private readonly HashSet<int> generatingTiles = new();`（防重入）
- 新增 `TryPreloadNeighbor(Map sourceMap, int targetWorldTile)`（实例方法）：
  - 防重入：`if (generatingTiles.Contains(targetWorldTile)) return false;`
  - 去重：`if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(sourceMap, targetWorldTile, out _)) return false;`（已加载）
  - 取 sourceWorldTile：`SeamlessTileRegistry.GetMapWorldTile(sourceMap)`
  - `generatingTiles.Add(targetWorldTile);` try { `GenerateTileMap(sourceWorldTile, targetWorldTile, mapSize)` } finally { `generatingTiles.Remove(targetWorldTile); }`
- 新增**静态入口** `SeamlessTilePreloader.TryPreload(Map sourceMap, int targetWorldTile)`：内部取 `sourceMap.GetComponent<SeamlessTileManager>()` 再调 `TryPreloadNeighbor`（支持从口袋地块 B 生成 C）

### 第8步：事件驱动 Hook（玩家 Goto 指令）
**修改 `Source/Patches_Job.cs`** 的 `Patch_Pawn_JobTracker_StartJob.Prefix`：在现有 `TryInterceptJob` 之后追加：
```csharp
// 仅玩家强制指令的 Goto 触发边界预加载检测（避免动物级联加载）
if (newJob.def == JobDefOf.Goto && newJob.playerForced && newJob.targetA.IsValid) {
    SeamlessBorderPreloader.CheckPawnGoto(___pawn, newJob.targetA.Cell);
}
```
**新增 `Source/SeamlessBorderPreloader.cs`**（静态）：
- `CheckPawnGoto(Pawn pawn, IntVec3 targetCell)`：
  - `var map = pawn.Map; if (map == null) return;`
  - `var lookup = map.GetComponent<SeamlessBorderLookup>(); if (lookup == null) return;`
  - `if (!lookup.TryGetPreloadTarget(targetCell, out int worldTile)) return;`
  - `if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, worldTile, out _)) return;` // 已加载
  - `SeamlessTilePreloader.TryPreload(map, worldTile);`

### 第9步：开档行为调整 + Trigger null 保护
**修改 `Source/SeamlessTileManager.cs`** 的 `TryAutoGenerateFirstNeighbor` → `TrySetupOnStart`：
- 先 `PlaceEnterSpotsAllNeighbors(map, map.Tile)`（锚点铺全部边，对端 null）
- 若 `RimExodusMod.Settings.preloadAllNeighborsOnStart`：遍历所有世界邻居调 `TryPreloadNeighbor`
- 否则：不生成邻居（纯预铺，等玩家接近边界）

**修改 `Source/SeamlessMapTransferTrigger.cs`** 的 `CheckLocalEnterSpots`：加 `if (comp.CounterpartSpot == null) continue;` 保护（预铺未绑定的 spot 被踩时不 NRE、不触发转移）。

---

## 文件清单

### 新增（4 个）
| 文件 | 职责 |
|---|---|
| `Source/RimExodusSettings.cs` | ModSettings 配置（距离阈值 + 开档加载开关） |
| `Source/SeamlessBorderLookup.cs` | 边界带速查表 MapComponent（Dictionary，O(1) 查询） |
| `Source/SeamlessEnterSpotBinder.cs` | 延迟绑定工具（按 targetWorldTile 匹配+坐标校验互绑） |
| `Source/SeamlessBorderPreloader.cs` | 预加载触发入口（静态，防重入，调 SeamlessTilePreloader） |

### 修改（6 个）
| 文件 | 改动 |
|---|---|
| `Source/RimExodusMod.cs` | 改 Mod 实例 + Settings |
| `Source/CompSeamlessTileEnterSpot.cs` | 加 targetWorldTile 字段（持久化） |
| `Source/SeamlessPolygonGeometry.cs` | 加 InsetPolygon + ComputeEdgeBand + DistanceToEdge |
| `Source/SeamlessTileManager.cs` | PlaceEnterSpotsAllNeighbors + TryPreloadNeighbor + generatingTiles + 开档行为 + 移除旧 PlaceEnterSpots |
| `Source/Patches_Job.cs` | 追加 playerForced Goto 边界检测 |
| `Source/SeamlessMapTransferTrigger.cs` | CounterpartSpot null 保护 |

### 可能微调
- `Source/GenStep_SeamlessTile.cs` — 若 GenStep 后统一调 PlaceEnterSpotsAllNeighbors

---

## 关键不变量与风险
1. **offset 契约**：`BindUnboundSpotsBetween` 依赖 `expectedCellB = cellA - offsetAtoB` 两端世界坐标重合。offset 已双向登记。
2. **凸多边形内缩正确性**：算法 C 利用凸性，内缩后仍凸，扫描线差集天然正确。N=15 << apothem=125，无退化风险。
3. **防重入**：RimWorld 单线程 tick，`HashSet<int>` 无需锁。
4. **GenerateTileMap 实例方法依赖**：口袋地块 B 生成 C 时，用 `B.GetComponent<SeamlessTileManager>()` 取 Manager（每图都有该组件，邻居表存储位置由 `RegisterNeighborBidirectional` 内部分支处理，已正确）。
5. **多跳天然支持**：新地块 C 加载后，`BindUnboundSpotsBetween` 遍历 C 的所有已存在邻居（含 B），逐一绑定。世界网格上 B↔C 是邻居且 B 已加载时，加载 C 自动绑定 B↔C 传送点。
6. **Walkable 跳过**：预铺时跳过不可站立格；绑定后若一端有 spot 对端无（对端该格不可站立），该 spot 永远 `CounterpartSpot=null`，Trigger 的 null 保护使其不触发转移。
7. **动物级联加载规避**：仅 `playerForced==true` 的 Goto 触发，动物/自动寻路不触发。

## 验证点
- 开档：锚点 A 沿全部世界邻居边铺单端 spot（对端 null），Dev 菜单可见 spot 总数
- 玩家右键 pawn 移动到边界带内（≤15格）：触发对应 worldTile 邻居加载，加载后两端 spot 自动绑定（日志输出绑定数）
- 多跳：A→B 加载后，从 B 右键接近 C 边界，加载 C，C↔B 的 spot 自动绑定
- 动物接近边界：不触发加载（playerForced=false）
- 存档读档：spot 的 targetWorldTile 和 CounterpartSpot 绑定关系保留
- ModSettings：调整阈值后边界带大小变化；开启开档加载全部则开档即生成所有邻居
- 边界带形状：内缩多边形差集，边界带宽度均匀（非阶梯状），斜边处宽度正确