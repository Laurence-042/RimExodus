# Combat Extended 跨图射击适配

## 状态与参考版本

实现基于 `references/CombatExtended` 中的 CE 16.7.3.0 源码，采用 RimExodus 单 DLL 软反射绑定，当前状态为“已实现，待游戏内回归”。CE 未安装时不解析 CE 类型、不注册相关 patch。

## 支持矩阵

- 支持普通 `Verb_ShootCE` 直射链，以及不改变核心发射流程的 one-use、ability、changeable 包装。
- 支持玩家 Pawn、NPC、snap 与 non-snap CE 炮塔的跨图选择、射界/射程判断和开火。
- 支持非 `flyOverhead`、非 instant、非 guided/homing 的 `ProjectileCE` 跨缝飞行、碰撞及目标图结算。
- 不支持迫击炮、跨图手雷、instant raycast、guided/homing、CIWS、世界炮击和只有 cell、没有目标 Thing 的跨图压制目标。这些调用保持 CE 原拒绝语义。

## 实现不变量

- 同图 CE 调用始终执行 CE 原方法；兼容层只接管目标 Thing 位于活跃相邻 Surface Map 的调用。
- CE verb 不走 RimExodus 的通用 `Verb.TryFindShootLineFromTo` 接管；唯一判定入口是 CE 自身的 `TryFindCEShootLineFromTo` 底层钩子，避免通用射程/LOS 与 CE 射击状态机形成两套结论。
- CE 的浮点射线枚举只在非负坐标域内可靠。跨图统一坐标可能为负，因此调用原 CE 枚举器前对射线两端施加同一个整数平移，枚举结果再反向平移；该变换不改变方向、交点或拐角格语义。枚举数量另设与射线长度相关的有限上限，异常时退回已经通过的分图 LOS，禁止热循环卡死或把实现故障误报成不可射击。
- 射程、射线与散布使用射手图统一坐标：`targetLocal + offset = shooterUnified`。逐格环境查询按格归属路由回真实地图。
- `ShiftVecReport` 保留 CE 原生精度、瞄具、天气、弹速、摇摆和散布计算；跨图修正距离，并分别从射手图/目标图采样光照，从射线路由采样烟雾、屋顶与掩体。掩体候选严格沿用 CE 的部分填充、非植物、非爆炸陷阱规则；树木和墙仍可以在弹道中拦截子弹，但不能被误用为抬高瞄准点的掩体。
- `ProjectileCE.MoveForward` 算出下一位置后、CE 越界/碰撞前执行交接。迁移时平移 origin、Destination、OriginIV3、ExactPosition、LastPos 和预测位置；不修改速度、高度、飞行计数、能量、伤害或目标。
- 右键和武器 gizmo 的攻击反馈仍由原版 `FeedbackShoot`/`FeedbackMelee` 产生；当反馈归属于邻图时，仅在创建入口把位置投影到当前视图并交给当前图 FleckManager，避免为此重复运行邻图整套 FleckManager。
- 休眠图不参与瞄准、索敌或弹丸交接。签名漂移按单项安全降级处理，并在启动日志输出绑定结果。
- CE 炮塔类型的静态构造器会创建 Unity 材质；炮塔相关 Harmony detour 必须经 `LongEventHandler.ExecuteWhenFinished` 在主线程注册，禁止在 Mod 构造器所在的异步加载线程首次编译该类型。

## 游戏内回归重点

验证同图 CE 无回归；跨图最小/最大射程与失败文本；两侧墙体、掩体、烟雾、屋顶及昼夜光照；移动目标与高速弹丸；NPC 还击；snap/non-snap 炮塔手动及自动索敌；休眠/唤醒切换。另逐项确认不支持类型仍被 CE 正常拒绝且无错误日志。

弹道问题使用设置“高级 / 诊断 → Combat Extended 专项诊断”。日志以 `[RimExodus:CombatExtended]` 开头，只记录 `ProjectileCE.LaunchCore`、`ProjectileCE.Handoff`、`ProjectileCE.Impact` 三个离散事件，并用弹丸 `thingID` 关联；禁止在 `CanHitTarget`、射线、索敌、炮塔角度或 UI hover 等每 tick/每帧热路径逐次输出，以免低信息密度日志触发 RimWorld 日志抑制。诊断开关默认关闭。

弹丸计数以 `ProjectileCE.LaunchCore` 为准：它挂在 CE 普通移动弹丸最终进入的三参数核心 Launch，不受跨图能力过滤、目标 Thing 是否仍有效或八参数包装重载覆写影响。跨图发射行还附带统一目标坐标、真实距离和弹道相对目标的水平角误差，用于区分 CE 正常散布与跨图坐标偏转，不增加日志事件数。
