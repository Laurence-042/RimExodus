# TODO / 回归清单

## 当前问题

- 存档读档后无法触发邻接地图的生成了（用户实测 2026-08-25；疑与下条口袋图污染同源——读档后指向口袋 parent 的邻居链接使 TryPreloadNeighbor 去重跳过生成，本轮 HealPocketNeighborLinks 应已覆盖，待回归确认）。

## VF 载具兼容 v2（2026-08 机制重写后全链回归）

1. 启动日志 `VF compat: bound 4 vehicle patches (grids=True, arrivalCheck=True, reachability=True)`；双装 VF/VMF 无 "[VehicleMapFramework] Error while apply patching" 红字。
2. 跨图右键下令载具 → 驶近接缝 → 传送 → 到达即续走到目标格。首次进入某图时 verbose 可见 `synchronously generating VF grids ... (Urgent)`（之后跳过）。
2a. 大型多格载具（无畏舰，2026-08-25 近 spot 选格修复）：跨图右键不再"始终无法到达"/"走本图同数字坐标"。回归点：无畏舰/巡沙车跨图下令、传送、续程。
2b. **VMF 载具内部图污染邻居表（2026-08-25 修复"邻图上不显示家园"）**：VMF `SetTile()` 把载具内部图 Parent.Tile 同步成载具所在图 tile → 旧版 SetupNativeParentMap 裸读 map.Tile 把内部图当该 tile 原生家族图接线（AutoConnect 覆盖各邻图对真家园的链接 + 读档后预加载去重误跳过生成）。修复 = MapGenerated/SetupNativeParentMap 口袋图双点早退 + MapComponentTick 自愈（口袋链接改指真实图/无则删链 + 刷新传送点缓存，日志标志 `Healed neighbor link`）。回归点：带 VMF 内部图载具（无畏舰）时邻图上应显示家园地形；旧毒化档读入后下几 tick 出现 `Healed neighbor link` 且邻接生成恢复。
2c. **大/中型多格载具跨图（2026-08 第三轮：整车矩形判据统一）**：前两轮症状链——①goto 格只过 region 可达（单格语义）→ VF A* 终点整车矩形门槛截断 → "ran out of path nodes" PatherFailed 循环；②回程 GoHere 灰显 = VF GetSingleOption 拿重放坐标在本图跑 TryFindNearestStandableCell、投影区域对大车敌意（去程能过纯地形运气，方向不对称）。修复 = 选点/续程/菜单全换 VF 原生"就近合法终点"（TryFindVehicleStandableNear，半径统一 R=max(min边×2,⌈长边/2⌉+1)）；新增 patch E（第 5 手动绑定）在重放窗口内改答桥接 goto 格。回归点：无畏舰去程+回程（GoHere 应亮、无 ran out of nodes）、传送、续程到目标格；小车道；无 VF 零变化；启动日志 `bound 5 vehicle patches`。
3. 传送落点对该载具不可站时自动挪最近可站格（verbose = `arrival cell ... resolved to ...`）；对端整圈无地块时菜单如实"无法通过此处"。
3a. **落点判据 Drivable 化 + 四向放宽（2026-08-25 第四/五轮"能去回不来"修复）**：回程（邻图→家园）曾在传送点四连拒 `no standable arrival cell within radius 32`——两个根因：①落点判定用 `NonStandableOrVehicleBlocked`（CellRectStandable 逐格扫 thingGrid，植物 PassThroughOnly ≠ Standable → 野外接缝环上一棵树就毙掉候选；VF 行驶侧 Drivable 口径不扫 thingGrid，"树卡车底"合法）；②固定 vehicle.Rotation 单一朝向，16 格长边在接缝带结构性放不下。修复 = `IsBlockedAt` 跨图复刻 VF Drivable（目标图 pathGrid 整车矩形 + 他车 OccupiedRect 重叠，容忍 thing）+ 环扫候选格 Rot4 四向取或、命中朝向写回落地 Spawn；拒绝时 verbose `arrival diagnostics` 采样区分地形 vs 他车/越界。回归点：无畏舰回程传送成功（verbose 应见 `resolved to ... (rot N)`），去程无回归；若仍拒，diagnostics 行可直接裁决剩余原因。
4. 载具载乘客跨缝（乘客存活）；下船无我们标签的报错。
5. PS WASD 驾驶跨缝：视角/缩放自动切、输入不冻结。
6. 休眠：有玩家载具的图不睡不删；全员驾离后本图按策略休眠。
7. 卸载 VF 零回归。
