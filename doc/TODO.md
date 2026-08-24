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

优化视觉效果（2026-08 已实现，待游戏内回归：三色撤离带 + void 边界岩隐形 link 延续体，见 AGENTS.md"边缘行为统一"/"void 渲染"节）
- ~~三条接缝带使用不同颜色来标记“对侧在这一格会有传送点但本侧没有”、“接缝本身，本侧和对侧这一条会对齐，对不齐那就是地块投影角度偏移造成的正常细微偏差”、“接缝外侧用于补接缝漏洞的，本侧有传送点但对侧没有”。同时撤离带拓宽到3条接缝带的范围，这样切换地图时接缝和撤离带不会跳变~~
- ~~调研是否可以优化岩石显示，避免本侧和对侧本该连续的岩石在接缝带外侧出现明显边界（因为本质上本侧void边上的岩石看void上没岩石就会显示对应的边界，但实际上当对侧也有岩石时不该出现边界）。这个实现可能十分繁琐，所以如果我们评估代价过大会放弃做这个~~（评估结论：代价中低，已实现——void 格铺隐形 `RimExodus_VoidRockLink` 向 LinkGrid 提供 Rock flag，对端岩石实况读条带快照 building 层；已知限制：对侧岩石后被开采本侧仍显示连续，接受）

QoL
添加玩家主动销毁地图的方案（在大世界地图上选中地块删除？）
添加一个 PlaySettings 的 Global Controls，用于控制是否显示接缝带并启用撤离功能，默认开启（现在的表现，显示接缝带并允许从接缝带撤离），关闭后不再可以通过接缝带撤离为大地图远行队也看不到接缝带
玩家主动命令pawn走到接缝带上时，不论对侧地图是否已经生成，都转为远行队（否则玩家点偏了还得去另一个接缝上组远行队，这样不合适）

计划适配
- MapPreview
  - 目前大部分适配，但是void区的部分曾经存在的岩石显示在了Preview里
- PerspectiveShift
揭雾多根化（2026-08 已实现，待游戏内回归）：用户发现"邻接图从接缝揭雾"偶发不工作——根因 = 每条边只选 1 个最优洪水根，山体延伸到接缝带把源边室外区分割成多段"走廊"时只有根所在段被揭雾、其余段留雾。修复 = 对源边全部 Standable 传送点逐个发根（同连通域后续洪水空转去重）+ 源边零可站 spot 降级全部活跃边（旧版源边失败不回退会整图留雾）。见 AGENTS.md 揭雾分径条与 Patches_GenStepFog.cs。回归点：找一张山体延伸到接缝带、源边室外区分段的地块图，确认各段均揭雾、被围死的中间空位仍留雾；verbose 日志 outdoorFloodRoots 应 >1。
