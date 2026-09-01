# TODO

## 2026-09：用户报告"caravans stuck on the border"排查（访客/旅行者/商队离场链）— 插桩完成，成因未定论（等报告者日志）

**调研结论（代码链路核实）**：NPC 离场（访客 `LordJob_VisitColony` / 商队 `LordJob_TradeWithColony` / 旅行者 / 袭击者撤退）**共用同一条撤离链**——`JobGiver_ExitMap*` → `RCellFinder.TryFindBestExitSpot/TryFindRandomExitSpot`（我方 patch → 最近 Standable+可达传送点）→ `Goto + exitMapOnArrival` → StartJob 登记 Evacuation grant → 踩传送点分流（对端已加载 → 传送过缝续链；对端未加载 → pre-tick `IsExitCell` → `Pawn.ExitMap` despawn 成 world pawn）。原版事实：NPC 商队离场本就不重建 caravan（`FindCaravanToJoinFor` 对非玩家阵营恒 null）——"走出门散在世界里"即原版语义。

**用户实测（2026-09-01，verbose 日志定案）**：对侧堵死后商队仍正常撤离。日志证实商队全员（含驮兽）在**带内圈第一格**（目标传送点 z+1）被 `JobDriver_Goto` pre-tick `IsExitCell` 原生 despawn，全程零传送分派——商队从不过缝，对侧堵不堵无关。机制 = 2026-08 两项定夺的时序冲突：exit grid 拓宽到 Band 全宽（含带内圈）使 pre-tick 离场先于传送点触达；传送分派需要踩到传送圈（Band 外两圈）spot 格。**推论（未定论）**：撤离链"传送续链"对图内出发者结构性失效（仅传送圈内站位——追击踩点/游荡转化——触达）；Steam 报告若在当前版本，这条路径不会卡，嫌疑转向玩家远行队组队集结（CheckArrived）或 Band 拓宽前旧版（exit cell 只在传送圈，spot 被堵即卡——拓宽可能已顺带修复）。**用户定夺（2026-09-01）：证据不足，暂不改行为，只保留诊断日志等报告者日志。**

**候选卡点（按嫌疑排序，全部已有 verbose 日志覆盖）**：
- **A 出口找不到**：`FindReachableEnterSpot` 无 Standable+可达 spot → JobGiver null job → 站桩（原有 passthrough 日志）。
- **B 传送拒绝循环**：`HandleEvacuation` 传送被拒（对端镜像格不可走）→ 首跳（flag job 驱动）有 pre-tick 兜底；SelfDriven 续程 job 无兜底 → 站桩/踱步循环。
- **C 落点隔离**：传送落邻图后被地形困住，`TryFindEvacuationExit` 三级候选全灭 → 无 grant 无 job → 永久站桩。
- **D 组队集结等待**：玩家 caravan 的 `CheckArrived`（每 100t）要求全员距 exitSpot 10 格内且可达 → 全队边界干等。
- **E Follow/Bind 作废**：对端卸载 → grant 作废 → TransitGoto 走到头站住，非战斗体不进 StrayNpcs 宽限。

**插桩清单（2026-09，全部 verbose 门控，纯日志零行为改动；标签统一 `[RimExodus] [caravan-exit]`——玩家日志一眼可辨商队/访客离场行为）**：
- `RegisterEvacuation`：登记时记录 lord 类型 + duty。
- `HandleEvacuation` 三分支：`Evacuation transfer` / `Evacuation transfer rejected`（区分 flag 驱动有无兜底）/ `Evacuation force-exit` / `Evacuation vanilla handoff`。
- `HandleBoundTransfer`：`{Kind} grant dropped`（对端卸载）。
- `ContinueEvacuationChain`：`Evacuation chain stuck`（落点隔离）+ `Evacuation exit search failed`（候选统计：总数/可站数/可站不可达数/visited 数）。
- `Patches_CaravanExitDiagnostics`：`Caravan gathering waiting`（CheckArrived 未满足成员明细，600t/lord 节流）+ `Pawn.ExitMap`（一切原生离场终点确认，Prefix）。验证器 BOUND OK 86（+2）。

- [ ] 取得报告者 verbose 日志（搜 `[caravan-exit]` 标签）定位具体分支
- [ ] 成因确认后评估修复方向（B/C/E：SelfDriven 兜底离场 / 落点隔离检测；D：组队出口对动物可达性校验）

## 用户反馈待办

- 穿梭机吃人有人反馈吗？我这有几个人反馈是加了这个mod后，任务自动上穿梭机，人物会消失，我自己也出现过两次
- 天气源被休眠、删除、降频（可能降频到0）的处理
- POI支持主动休眠与删除
