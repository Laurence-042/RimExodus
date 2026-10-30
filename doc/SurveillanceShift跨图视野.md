# Surveillance Shift 跨图视野

## 分工与范围

REF 提供 `MapRuntime.Spatial` / `MapCellSpace`，方法论见同级 `RimExodusFramework/doc/跨图空间查询.md`。RE 的 `SeamlessSpatialQueries` 登记表面直接邻居，以 `SeamlessViewProjection` 取得平移，用地块多边形判断归属；参考图优先，与现有 `SeamlessTileRegistry` 边界重合规则一致。口袋图、非 Surface、休眠和生成中地图排除。查询不会预加载、唤醒地图或启用跨图战斗。

RE 在新游戏/读档配置阶段以 `RimExodus.SeamlessWorld` 登记 provider，不依赖是否安装 SS。其他适配可通过 REF 的 `Spatial.TryCapture` 使用同一 owner 查询 RE 几何；SS 的早期调用另有幂等登记兜底。

SS 适配自动软检测，适用于 legacy 和 REF 两种底层。视野计算与渲染开关独立；要看到完整 SS 邻图迷雾和隐藏效果，需要高兼容性原版邻图渲染。用户已确认跨图视野验证完成；未增加设置或存档字段。

## 保留 SS 算法

参考为 `_references/Surveillance-Shift.installed/1.6/Source`；安装快照包含 1.7 shader，但仍使用该 C# 实现。保留 SS 的观察者选择、SightRadius、视力倍率、倒地视距、Perspective Shift 模式、对讲机、监控/供电/值守条件。只把 `ShadowCaster.ComputeFieldOfView` / `ComputeConeFieldOfView` 的输入映射到联合障碍数据，并将结果回写真实图。

联合数据使用一张**未登记的 Map 数据容器**满足 SS 原算法直接读取 `Size`、`cellIndices`、`edificeGrid`、`roofGrid` 的签名。它不进入 Find.Maps，不初始化游戏组件，不运行 tick、生成、存读档或 Map.Dispose。建筑引用仍指向真实图中的建筑，不改变 Map/Position，不复制 SS 的 shadow-casting 算法。未解析格使用专用不透明占位数据；高塔忽略墙体时另检查沿途是否都有真实地址，防止跨过不存在的地图。

普通 Pawn、载员载具、圆形摄像头、壁挂摄像头锥形视野、高塔、对讲机都消费原 ShadowCaster 入口。墙角夹缝、透明墙兼容和高塔屋顶规则沿用 SS。独立的 `CanWatchtowerSee` 行为查询不自动跨图化；摄像头建造/选择时的 `CameraViewPreview` 只读本图预览数组，仍显示原本本图范围，不写跨图迷雾。

望远镜/双筒望远镜沿用原 `ScopeView.ApplyCircle` 射线算法，在联合数据上计算；允许中心越过源图矩形。光学模式替换该来源的普通视野时，同时删除其先前跨图贡献，并阻止其他图的普通视野再次扩大光学源图的可见范围。暂停时移动观察中心触发整组可见性刷新；输入始终归真实聚焦图，邻图绘制作用域不能消费观察中心。

**保留原 Mod 特殊政策**：该图没有自由殖民者且没有载员载具时，SS 原逻辑将整图设为可见；本适配不擅自改成全雾。Gravship 显示分支也保留。这与“没有观察者应当全雾”的推测不同，验收需区分这一原生政策。

## 更新、开销与失败

SS 通常每 12 ticks 触发重算。首个成员触发时，协调当前连通的活跃地图各调用一次 SS 可见性重算；每个观察者的几何范围仍限于本图及直接邻图。仅重放可见性计算，不重放 Map/MapComponent tick 或 update。同 tick 后续周期调用去重，显式重算和暂停光学刷新不被周期去重挡住。

每张图先执行自身清空/计算，跨图贡献按来源暂存，全部完成后合并，再各调用一次 SS ApplyFog。下一轮重新构造贡献，因此移动、死亡或关摄像头后不会永久留下可见格。SS 自己的 discovered 历史探索记录仍按原规则保存。

不触及邻图矩形的普通视野直接跑原算法；需跨图时按视距捕获局部数组，单次上限 262,144 格。超预算或运行时异常明确告警并关闭本次会话的跨图视野，重新调用各图本地算法修复中间状态。恢复跨图适配需修正原因并重启。联合数据有额外分配成本，尚无实机性能结论。

安装在 long event 主线程收尾，避开 SS 材质静态初始化时点。8 个目标精确按参数类型绑定；失败撤销本适配已装钩子。初始化日志：`[RimExodus:Compat] Surveillance Shift: bound 8 vision methods`。SS 未装时不安装这些补丁。

## 验证

`just test-ss` 从统一参考目录原样链接 SS 的 ShadowCaster 和 ScopeView；其他 SS 宿主服务使用夹具，真实 1.7 Map 网格和生产 RE/REF 代码参与运行。23 项检查包括圆/锥/光学射线、邻图墙体/屋顶、透明墙、未知区域、高塔跨空白保护、真实 Harmony 8 目标绑定与参数重定向、更新顺序互换、离场清理、覆盖模式清除及异常后恢复本图可见性。它不等同于实际 SS DLL 初始化或 GPU 实测。该入口会先构建 REF/RE 并更新匹配引用。

游戏内确认加载本轮 DLL、Mod 实例化行、GetPatchedMethods 和上述 SS 绑定日志，再验收：

1. 接缝两侧各有殖民者，观察者靠近/远离接缝，对侧建墙、开关门、透明墙、高塔屋顶对照；相同地点切换聚焦前后相同。
2. 两侧摄像头供电/监控、塔楼值守、壁挂摄像头四方向；撤走观察者后动态可见性消退，探索记忆保留。
3. 暂停移动望远镜中心、退出光学模式、PS authentic 与对讲机联动；无其他图旧视野残留或输入被邻图误用。
4. 休眠、唤醒、删除、换档以及两种底层；不可穿过未加载区域，也不因视野主动生成或唤醒地图。
5. 无殖民者/无载员载具、Gravship 分支与未装 RE 时的 SS 原行为比较，避免把原政策误报为适配泄漏。
