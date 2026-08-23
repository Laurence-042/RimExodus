远古机械师建筑群之类的poi没有生成，它们应该和据点一样走原版生成。所有site应该都有相同的底层机制，所以我们不该对每个site单独patch（也许这种occupied统一走原版生成就行？毕竟可能有mod在原版地图生成管线上做手脚在生成自己结构时得到额外信息，用我们的管线可能导致兼容问题）
[RimExodus] World tile 68232 is occupied by Site without a live map, skip generation.

当前奴隶或似乎没法正常通过接缝撤离的方式成为远行队一员
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

偶现下面的报错
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


Geological Landforms的地形似乎没有生成，需要检查下相关代码（2026-08 已修已回归：根因 = 增量生成路径绕过 MapGenerator.GenerateContentsIntoMap，GL 挂在该方法的 Harmony Prefix（Landform.Prepare 静态上下文）从未执行、worker 首行守卫恒早退；修复 = SeamlessLandformsCompat 软反射复刻 Prepare/CleanUp/分帧竞争守卫/genStep 注入，见 AGENTS.md"邻居预加载与异步加载"节 GL 兼容条）

Geological Landforms 第二轮（2026-08 已修已回归）：①建营地进 GL 地块 → 营地图 landform 永久失效；②邻接进入后地块存在期间 MapPreview 预览丢 GL、图销毁后恢复。同一根因 = GL 的 CheckWorldObject 把无派系的 RimExodus_SeamlessTileMap parent 当外来 site 过滤掉该 tile 全部 landform（营地在生成前 Add parent 触发 + GL 的 CommitDirectly 把空列表烧进世界数据造成"永久"；地块图 parent 常驻触发预览过滤；基线对照已坐实：同 tile 预览正常 vs 营地生成 None 且 Topology 正常，差异变量唯一 = parent 在场）。修复 = 启动时反射注册 defName 进 GL 的 IgnoredWorldObjects 白名单 + EnsureContextAlive 改实例引用归属判据 + 增量启动避让在飞预览（IsPreviewInFlight 并入忙判据，忙态统一 QueuePreload 重排队）。首版曾因反射类型全名笔误（文件路径 Nodes/UI 段误当 namespace）静默失败，修正版 = 正确全名 + 简单名扫程序集兜底 + 注册后读回自检。注意：修复前已毒化的旧档 tile 不会自动恢复（用户定夺不清理），需 GL 工具手动 Reset。回归标志 = 启动日志 "GL compat: registered RimExodus_SeamlessTileMap in IgnoredWorldObjects (verified...)"。

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑还需要哪些配置

优化视觉效果
- 三条接缝带使用不同颜色来标记“对侧在这一格会有传送点但本侧没有”、“接缝本身，本侧和对侧这一条会对齐，对不齐那就是地块投影角度偏移造成的正常细微偏差”、“接缝外侧用于补接缝漏洞的，本侧有传送点但对侧没有”。同时撤离带拓宽到3条接缝带的范围，这样切换地图时接缝和撤离带不会跳变
- 调研是否可以优化岩石显示，避免本侧和对侧本该连续的岩石在接缝带外侧出现明显边界（因为本质上本侧void边上的岩石看void上没岩石就会显示对应的边界，但实际上当对侧也有岩石时不该出现边界）。这个实现可能十分繁琐，所以如果我们评估代价过大会放弃做这个

QoL
添加玩家主动销毁地图的方案
玩家主动命令pawn走到接缝带上时，不论对侧地图是否已经生成，都转为远行队（否则玩家点偏了还得去另一个接缝上组远行队，这样不合适）