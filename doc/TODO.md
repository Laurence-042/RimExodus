偶现奴隶没法正常通过接缝撤离的方式成为远行队一员
Exception in JobDriver fixed tick for pawn McTodd driver=JobDriver_Wait (toilIndex=0) driver.job=(Wait (Job_8224))
System.NullReferenceException: Object reference not set to an instance of an object
[Ref F6426D47] Duplicate stacktrace, see ref for original
UnityEngine.StackTraceUtility:ExtractStackTrace ()
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Error_Patch1 (string)
Verse.AI.JobUtility:TryStartErrorRecoverJob (Verse.Pawn,string,System.Exception,Verse.AI.JobDriver)
Verse.AI.JobDriver:DriverTick ()
Verse.AI.Pawn_JobTracker:JobTrackerTick ()
Verse.Pawn:Tick ()
Verse.Thing:DoTick ()
Verse.TickList:Tick ()
Verse.TickManager:DoSingleTick ()
Verse.TickManager:TickManagerUpdate ()
Verse.Game:UpdatePlay ()
Verse.Root_Play:Update ()

同时偶现下面的报错，疑似和奴隶那次相同原因
Root level exception in OnGUI(): System.NullReferenceException: Object reference not set to an instance of an object
[Ref B3A524A9]
  at Verse.PawnCollisionTweenerUtility.GetPawnsStandingAtOrAboutToStandAt (Verse.IntVec3 at, Verse.Map map, System.Int32& pawnsCount, System.Int32& pawnsWithLowerIdCount, System.Boolean& forPawnFound, Verse.Pawn forPawn) [0x0008b] in <61e4173561894da49d210260257b5097>:0 
  at Verse.PawnCollisionTweenerUtility.PawnCollisionPosOffsetFor (Verse.Pawn pawn) [0x00087] in <61e4173561894da49d210260257b5097>:0 
  at Verse.PawnTweener.TweenedPosRoot () [0x0012b] in <61e4173561894da49d210260257b5097>:0 
  at Verse.PawnTweener.ResetTweenedPosToRoot () [0x00000] in <61e4173561894da49d210260257b5097>:0 
  at Verse.PawnTweener.PreDrawPosCalculation () [0x00053] in <61e4173561894da49d210260257b5097>:0 
  at Verse.Pawn_DrawTracker.get_DrawPos () [0x00000] in <61e4173561894da49d210260257b5097>:0 
  at Verse.Pawn.get_DrawPos () [0x00006] in <61e4173561894da49d210260257b5097>:0 
  at Verse.TooltipGiverList.DispenseAllThingTooltips () [0x000c3] in <61e4173561894da49d210260257b5097>:0 
  at RimWorld.MapInterface.MapInterfaceOnGUI_BeforeMainTabs () [0x0008a] in <61e4173561894da49d210260257b5097>:0 
  at RimWorld.UIRoot_Play.UIRootOnGUI () [0x00024] in <61e4173561894da49d210260257b5097>:0 
    - PREFIX Dubwise.PerformanceAnalyzer: Void Analyzer.H_KeyPresses:OnGUI()
  at Verse.Root.OnGUI () [0x00040] in <61e4173561894da49d210260257b5097>:0 
UnityEngine.StackTraceUtility:ExtractStackTrace ()
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Error_Patch1 (string)
Verse.Root:OnGUI ()


已修复（2026-08，游戏内初验未复现——**无法排除偶现，持续观察**）：重组远行队时偶发 "Exception in BreadthFirstTraverse ... ThingFromRegionListerReachable NRE"（动物 herd 游荡的 ClosestThingReachable BFS 触发，被杀动物只是旁观者）。根因 = `SeamlessVirtualTeleporter` 用 `thing.Position = ...` **属性赋值**写虚拟位置——Position setter 带完整网格簿记（region lister/thingGrid 注销+重注册），而传送器时序是先切 mapIndexOrState 再写 Position：ctor 把 pawn 注册进**邻图**的 thingGrid+region lister（本图原注册未动），Dispose 又在**本图**按邻图坐标注销错过真实注册位 → 两张图各泄漏陈旧条目，pawn 后续 DeSpawn（死亡/跨图/组队）清不掉，残留 pawn 变"未生成且无 holder"后 BFS 扫到即 NRE。修复 = 直写后备字段 `positionInt`（与 mapIndexOrState 同款 FieldRef 纪律），评估窗口对两张图零写入。曾加 null 守卫 patch 作诊断兜底，用户定夺删除（patch 越多越不稳定越难维护，根因已修）。**若再出现同款 NRE = 存在另一条独立污染路径，从日志重新定位**。回归点：杀殖民地动物 → 重组远行队 → 无红字；跨图下令/跨图射击（传送器两大消费点）功能不回归。

计划适配
- PerspectiveShift —— 已适配（2026-08，`SeamlessPerspectiveShiftCompat`，待游戏内回归）：WASD 移动绕过 job/pather → 挂其 Avatar.ProcessMovement Postfix 补"边界带预加载 + 踩传送点即席 Bridge 传送"（换图帧豁免防镜像回传；Grant 不带续程 job——avatar 由玩家驱动）；右键下令走原版 FloatMenu 链零改动。回归项：①装 PS 启动日志有 `PS compat: bound Avatar.ProcessMovement postfix` 行，不装 PS 零日志零变化；②avatar WASD 走向接缝 → 邻图预加载/休眠唤醒（verbose `PS compat: avatar ... border band / woke dormant`）；③踩传送点跨缝传送（`PS compat: avatar ... stepped on enter spot` → `Seamless transfer`），传送后无 PS "Physics desync" 警告刷屏、不回弹、相机跟随正常；④avatar 右键跨缝下令/工作/攻击既有跨图链不回归；⑤第一人称瞄准限当前图 = 已知边界。

