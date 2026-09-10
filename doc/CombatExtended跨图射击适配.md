# Combat Extended 跨图射击适配

## 状态与参考版本

实现基于 `references/CombatExtended` 中的 CE 16.7.3.0 源码，采用 RimExodus 单 DLL 软反射绑定，当前状态为“已实现，待游戏内回归”。CE 未安装时不解析 CE 类型、不注册相关 patch。

源码布局：`Source/Compat/SeamlessCombatExtendedCompat.cs` 只保留软检测、反射绑定、公共能力和共享缓存；`Source/Compat/CombatExtended/` 下的 partial 文件分别承载瞄准/命中报告、炮塔/CIWS、弹丸/RayCast。它们仍编译进同一个 DLL，不构成额外程序集或硬引用。

## 支持矩阵

- 支持普通 `Verb_ShootCE`、`Verb_ThrowGrenade`、`Verb_ShootMortarCE`、`VerbCIWS`，以及 one-use、changeable 和 Ability 发射路径。
- 支持玩家 Pawn、NPC、snap 与 non-snap CE 炮塔的跨图选择、射界/射程判断和开火。
- 支持普通、`flyOverhead`、guided/homing `ProjectileCE` 的跨缝飞行，以及 instant `LaserBeamCE`/`ProjectileCE.RayCast` 的分图碰撞、压制与目标图结算。
- 支持只有 cell、没有目标 Thing 的手雷、迫击炮、压制射击和 Ability 目标；Targeter 另存真实目标图，不向 CE 注入假 Thing。
- 枪口朝向、Pawn 面向、暖机 mote/瞄准指示器与 Job 攻击线全部消费同一个 Thing/cell 统一目标投影；不得再以 `HasThing` 作为跨图视觉修正的入口条件。
- 支持三类 CE CIWS（Projectile、Skyfaller、CompSkyfaller）从活跃邻图补搜候选，并在统一坐标中运行 CE 原预测与拦截判定。
- 原版 `Verb_LaunchProjectile` 迫击炮复用通用纯格目标和 `Projectile` 交接链。爆炸 AoE 不跨地图扩散，只在弹丸实际落点图由原版/CE 结算。
- 不接管 CE 世界炮击（`globalTargetInfo` 有效的地图外发射）；它继续走 CE 的 TravelingShell/TravelingRaycast 世界对象流程。

## 实现不变量

- 同图 CE 调用始终执行 CE 原方法；兼容层只接管目标 Thing 或已登记纯格目标位于活跃相邻 Surface Map 的调用。
- CE verb 不走 RimExodus 的通用 `Verb.TryFindShootLineFromTo` 接管；唯一判定入口是 CE 自身的 `TryFindCEShootLineFromTo` 底层钩子，避免通用射程/LOS 与 CE 射击状态机形成两套结论。
- CE 的浮点射线枚举只在非负坐标域内可靠。跨图统一坐标可能为负，因此调用原 CE 枚举器前对射线两端施加同一个整数平移，枚举结果再反向平移；该变换不改变方向、交点或拐角格语义。枚举数量另设与射线长度相关的有限上限，异常时退回已经通过的分图 LOS，禁止热循环卡死或把实现故障误报成不可射击。
- 射程、射线与散布使用射手图统一坐标：`targetLocal + offset = shooterUnified`。逐格环境查询按格归属路由回真实地图。
- `ShiftVecReport` 保留 CE 原生精度、瞄具、天气、弹速、摇摆和散布计算；跨图修正距离，并分别从射手图/目标图采样光照，从射线路由采样烟雾、屋顶与掩体。掩体候选严格沿用 CE 的部分填充、非植物、非爆炸陷阱规则；树木和墙仍可以在弹道中拦截子弹，但不能被误用为抬高瞄准点的掩体。
- `ProjectileCE.MoveForward` 算出下一位置后、CE 越界/碰撞前执行交接。迁移时平移 origin、Destination、OriginIV3、ExactPosition、LastPos 和预测位置；不修改速度、高度、飞行计数、能量、伤害或目标。
- guided/homing 不复制制导算法：在 `BallisticsTrajectoryWorker.MoveForward` 读取目标的短窗口内，把目标投影到弹丸当前图；CE 的 SmartRocket/Homing worker 仍自行调整速度。纯格目标从弹丸弱引用上下文恢复真实目标图。
- `Verb_ThrowGrenade.FindAngle` 仍使用 CE 的操控能力、重力、最高阻挡、烟雾、屋顶和引信公式，仅把水平距离与逐格采样改为统一坐标/实际归属图。
- `Verb_ShootMortarCE` 仍使用 CE 的圆误差、观测标记和 75 格直瞄/间瞄分界；派生报告只修正跨图 LOS、目标图标记与被错误清零的环境量。
- instant RayCast 不伪装成移动弹丸：`ProjectileCE.RayCast` 在统一射线上逐格路由 `thingGrid`，将 Thing Bounds 投影后调用 CE 原 `Impact`，伤害衰减、植被/建筑概率、护盾与伤害处理仍属 CE；压制按地图分段执行，光束最终归属命中图。
- CIWS 的候选池按真实地图分桶补搜；预测窗口只临时平移目标位置状态，窗口外不写两图 thingGrid。CIWS 拦截弹自身继续走通用 `ProjectileCE` 迁移，并在跨图碰撞窗口投影目标。
- Ability 的 CE Harmony 最终汇入 `CE_Utility.LaunchProjectileCE`；兼容层在该公共入口修正射角/方位，不为每个 Ability 上层类复制发射实现。原版 `CompAbilityEffect_LaunchProjectile` 使用同一纯格上下文和 `Projectile` 交接链。
- 右键和武器 gizmo 的攻击反馈仍由原版 `FeedbackShoot`/`FeedbackMelee` 产生；当反馈归属于邻图时，仅在创建入口把位置投影到当前视图并交给当前图 FleckManager，避免为此重复运行邻图整套 FleckManager。
- 休眠图不参与瞄准、索敌或弹丸交接。签名漂移按单项安全降级处理，并在启动日志输出绑定结果。
- CE 炮塔类型的静态构造器会创建 Unity 材质；炮塔相关 Harmony detour 必须经 `LongEventHandler.ExecuteWhenFinished` 在主线程注册，禁止在 Mod 构造器所在的异步加载线程首次编译该类型。
- 炮塔射界、`DeltaAngle` 与手动攻击射程门使用连续统一坐标；目标统一格允许落在射手地图矩形外，不能以 `InBounds(caster.Map)` 拒绝。相关评估只临时直写 Thing 的 Map/Position 后备字段并调用纯几何逻辑，不访问越界网格。
- `Corpse.TickRare` 会继续 tick 尸体内部 Pawn；CE tactical manager 对未生成 Pawn 仍先读取压制/蜷缩状态再检查 `Spawned`。兼容层在 `CompTacticalManager.CompTickRare` 公共入口跳过未生成或已死亡 Pawn，避免带压制状态死亡后尝试修复战术 Job；活 Pawn 与同图 CE 战术行为不变。

## 游戏内回归重点

验证同图 CE 无回归；跨图最小/最大射程与失败文本；两侧墙体、掩体、烟雾、屋顶及昼夜光照；移动目标与高速弹丸；NPC 还击；snap/non-snap 炮塔手动及自动索敌；休眠/唤醒切换。特殊路径逐项验证手雷格目标、CE/原版迫击炮、直瞄/间瞄与标记、RayCast 两侧阻挡与护盾、SmartRocket/Homing 移动目标、三类 CIWS 候选/拦截、压制目标丢失后的纯格续射、Pawn/载具 Ability。CE 世界炮击应保持原行为且无 RimExodus 接缝日志。

弹道问题使用设置“高级 / 诊断 → Combat Extended 专项诊断”。日志以 `[RimExodus:CombatExtended]` 开头；移动弹丸只记录 `ProjectileCE.LaunchCore`、`ProjectileCE.Handoff`、`ProjectileCE.Impact` 三个离散事件，instant RayCast 每束只记录一次结算摘要，并用弹丸 `thingID` 关联。禁止在 `CanHitTarget`、射线逐格、制导 tick、索敌、炮塔角度或 UI hover 等热路径逐次输出，以免低信息密度日志触发 RimWorld 日志抑制。诊断开关默认关闭。

弹丸计数以 `ProjectileCE.LaunchCore` 为准：它挂在 CE 普通移动弹丸最终进入的三参数核心 Launch，不受跨图能力过滤、目标 Thing 是否仍有效或八参数包装重载覆写影响。跨图发射行还附带统一目标坐标、真实距离和弹道相对目标的水平角误差，用于区分 CE 正常散布与跨图坐标偏转，不增加日志事件数。
