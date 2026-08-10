# CheckLocalEnterSpots 性能优化：事件驱动替代轮询

## 问题
`SeamlessMapTransferTrigger.MapComponentTick` 每 tick 轮询所有传送点（~600 个），即使用 `Dictionary` 优化后仍每 tick 遍历 600 spot + N pawn。用户反馈这是核心性能瓶颈。

## 方案：Harmony Postfix on `Pawn_PathFollower.TryEnterNextPathCell`
调研确认 RimWorld 无原生"pawn 进入格子"事件。`Pawn_PathFollower.TryEnterNextPathCell`（:638 `this.pawn.Position = nextCell`）是 pawn 跨格的唯一钩子点。Postfix 它实现真正的事件驱动检测。

## 实施步骤

### 1. 新增 `Patches_PawnPathFollower.cs`
Harmony Postfix on `Pawn_PathFollower.TryEnterNextPathCell`（private 方法，用 `___pawn` 访问实例字段）：
- Postfix 读 `pawn.Position`（已更新为新格）
- `map.thingGrid.ThingsAt(pos)` 或 `ThingsOfDef` 查是否有 `RimExodus_SeamlessEnterSpot`
- 若有且 `comp.CounterpartSpot != null` 且 pawn 未被锁 → 触发 transfer（调 `SeamlessMapTransferTrigger.TryTriggerTransfer`）
- 只对 Spawned 的 pawn 有效（Postfix 内检查 `pawn.Spawned && pawn.Map != null`）

### 2. 修改 `SeamlessMapTransferTrigger.cs`
- `CheckLocalEnterSpots` 改为 `internal static TryTriggerTransfer(Pawn pawn, IntVec3 cell, Map map)`——接受单个 pawn + 单个 cell，只检查该格。
- `MapComponentTick` 移除 `CheckLocalEnterSpots()` 调用（不再轮询）。
- 保留 `PurgeInvalidArrivalLocks`（仍每 tick 检查锁清理，但 arrivalLocks 为空时快速返回）。
- 保留 `RecordArrival`（跨图到达锁）。

### 3. 覆盖性保证
- **玩家征召移动**：pather 跨格 → Postfix 触发 ✓
- **撤退敌人**：pather 跨格 → Postfix 触发 ✓
- **续程 Goto**：pather 跨格 → Postfix 触发 ✓
- **跨图落地（GenSpawn.Spawn）**：不走 pather → Postfix 不触发。但此时 pawn 在 `arrivalLocks` 里（防回弹），即使触发也会被锁住。pawn 后续自己迈步走回 spot 时走 pather → Postfix 触发，此时锁已解除 ✓

### 4. 性能对比
- **之前**：每 tick O(spots + pawns) ≈ O(600) 每地图
- **现在**：每 pawn 跨格 1 次 O(1) 查询（`thingGrid` 或 dict）。pawn 不跨格时零开销。

## 关键风险
- `TryEnterNextPathCell` 是 private 方法，跨版本有签名变更风险（用 `AccessTools.Method` 或 `[HarmonyPatch]` + `___pawn`）。
- 需确保 Postfix 不在 pather 内部状态不一致时触发（如 `BuildingBlockingNextPathCell` 返回非 null 时方法提前 return，Postfix 仍会执行——需检查 pawn 是否真的移动了）。

## 验证
- pawn 走到传送点上仍能正常触发转移
- 性能显著改善（profiler 不再显示 Trigger.MapComponentTick 为热点）
- 跨图到达后不回弹（arrivalLocks 仍有效）