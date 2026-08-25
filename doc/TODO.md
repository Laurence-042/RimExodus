# TODO / 回归清单

## 当前问题

- **分帧生成中途存档 → 读档雪崩（用户实测 2026-08-25，已修复待回归）**：半成品图随档整体保存但 parent WO 未登记（onComplete 才 Add），读档后 `Map.TileInfo → WorldGrid[invalid tile]` 越界、VacuumComponent/温度/植物 tick/FinalizeLoading 全线异常且无人丢弃该图。修复 = `GameDataSaveLoader.SaveGame` Prefix：`IsAnyGenerating` 时直接拒绝存档，用 **ThreatBig 信件**告知（左上角消息条不显眼被用户否决；文案带玩笑"请勿在地图加载过程中存档来测试本mod可靠性，这个mod的作者已经深刻理解到它的不可靠了"）。回归点：分帧生成进行中手动/自动存档被拒并收威胁信、生成完成后存档正常；正常时机存读档无回归。**已被毒化的旧档（生成中途存的）不可救，重开档**——genStep 共享数据在进程级 static 不序列化，恢复生成结构性不可行。
- 存档读档后无法触发邻接地图的生成（用户实测 2026-08-25；**根因已确诊并修复 2026-08-25**）：`SeamlessBorderLookup.ExposeData` 持久化了 `built` 旗标而 `borderCells` 不持久化——读档恢复 built==true + 字典 null，`MapComponentTick` 的 `!built` 补建分支永不进 → `TryGetPreloadTarget` 恒 false → `CheckPawnGoto` 永远 skip，邻图预加载/生成整链第一环即断。次生：`IsInNoBuildBand` 同因失效（读档后禁建带放行建造）。修复 = ExposeData 清空（运行时状态全不序列化，读档首 tick 补建）。回归点：读档 → 下令 pawn 走向接缝带 → verbose 出 `SeamlessBorderLookup built for map ...` 补建日志 + 邻图预加载/生成恢复；读档后接缝带内侧禁建仍生效；新档零回归。原猜疑（口袋图污染/HpL 同源）不是本症状根因——整条链序列化面已全面审计，BorderLookup 是唯一断点。
- **手动休眠锁两修复（2026-08-25，已修复待回归）**：①距离回落分支漏 `IsManuallyDormant` 守卫——手动睡的图在玩家 pawn 走到旁 1 跳时被 `BFS dist=1 < sleepHops` 兜底唤醒（保活分支有守卫、回落分支漏加）；②手动锁随档保留（用户要求）——持久化面 = governor 的 `manualDormantTiles`（世界 tile id，GameComponent 序列化），读档后 governor 首轮 Sweep 对锁内图以 manual:true 重新入睡。**第二轮（同日实测 + 方案 3 收口）**：①缺口 = 锁内图若读档后恰在活跃圈（d < sleepHops，玩家在隔壁）则 Sleep 分支不触发、唤醒守卫又只挡已休眠图 → 永远醒着；②首版 Sweep 兜底重睡有横跳窗口（读档后图先活跃进场几十 tick 再被睡回，玩家可能误读为休眠漏洞——用户定夺消除）。收口 = **主路径 `GameComponent.LoadedGame()` 读档即睡**（maps.FinalizeLoading 之后、首 tick 之前，锁内图从第一 tick 起即休眠），Sweep 重睡分支降级为幂等安全网；日志 reason 均为 `manual dormancy restored from save`。回归点：手动休眠 → 走到邻图晃动 → 无 WAKE 日志；手动休眠 → 存档读档 → **读档完成时（首个 tick 前）即出 SLEEP restored 日志**、该图无活跃窗口；进图/命令 pawn 走近接缝两类钥匙仍可唤醒。
- **三态世界图图标重绘（2026-08-25 待游戏内目测）**：统一六边形去中心点，有人实心橙 / 无人实心蓝 / 休眠灰空心（色盲友好 + 形状冗余）。回归点：世界图三态颜色形状符合、休眠/有人/无人转换即时刷新。

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

- **等边多边形模型（2026-08）**：`SeamlessPolygonGeometry.PopulatePolygonVertices` 半径从恒 0.5S 改为按平均弦长归一边长 0.5S（六边形零变化、五边形半径 ≈0.4253S）。回归点：①五边形图边长与六边形目测一致；②五-六邻接缝混合无断裂/无 void 裸露（旧模型共享边投影差 17.6%）；③普通六边形地图生成与混合零回归。旧档已按旧几何生成的五边形图建议"删除此图"重建。
