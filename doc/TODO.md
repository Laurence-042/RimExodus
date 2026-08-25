# TODO / 回归清单

## VF 载具兼容 v2（2026-08 机制重写后全链回归）

方案 = 复刻 VF 官方进图管线（传送三步：同步就绪化网格 → 整车矩形落点解析 → 原传送+pather 重置；续程与 pawn 同路）。v1 的续程队列/看护踢发/落点校正/网格自愈锤已全部删除。

> 2026-08 修复：就绪判定链曾因 `VehiclePathData`（嵌套类）按名查找恒 null 而静默缺失 → Urgent 生成成功但复查恒 false → "grids still not ready" 无限拒传（车卡接缝）。已改为从 `get_Item` 返回值取类型 + 绑定期缺失告警（见 AGENTS VF compat 条教训）。

1. 启动日志 `VF compat: bound 4 vehicle patches (grids=True, arrivalCheck=True, reachability=True)`；双装 VF/VMF 无 "[VehicleMapFramework] Error while apply patching" 红字。
2. 跨图右键下令载具 → 驶近接缝 → 传送 → **到达即续走到目标格**（无停顿、无卡死、无需手动改令）。首次进入某图时 verbose 可见 `synchronously generating VF grids ... (Urgent)`（之后跳过）。
3. 传送落点对该载具不可站时自动挪最近可站格（verbose = `arrival cell ... resolved to ...`）；对端整圈无地块时菜单如实"无法通过此处"且下令被拒绝（Warning 日志 `no standable arrival cell`）——换条边试。
4. 载具载乘客跨缝（乘客存活）；下船无我们标签的报错。
5. PS WASD 驾驶跨缝：视角/缩放自动切、输入不冻结；传送后继续驾驶正常。
6. 休眠：有玩家载具的图不睡不删；全员驾离后本图按策略休眠。
7. 卸载 VF 零回归。

## PS×VF 驾驶共存（随 v2 一并回归）

8. WASD 驾驶驶向接缝 → 邻图预加载/唤醒正常。
9. 驾驶碾过传送点 → 跨缝传送（同上 5）。
10. avatar 步行跨图原有行为回归（聚焦逻辑重构后幂等）。
