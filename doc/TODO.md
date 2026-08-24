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


优化可配置项（2026-08 已实现，待游戏内回归）：休眠/删除距离下限放宽到 1 且允许相等（1/1 = 离开即休眠并销毁；三条保活仍在）、分帧生成开关（默认开，关闭 = 普通 tile 图改调原版 MapGenerator.GenerateMap 本体，第三方 mod 对原版管线的 patch 原生生效——mod 环境逃生通道）、分帧批次大小（16-512，默认 64）；设置窗口改原生 TabDrawer 分 tab（地图生成/滚动休眠/跨图战斗/高级诊断）+ 滚动兜底 + 全条目 tooltip（休眠总开关 tooltip 写明机制目的/效果/休眠与删除语义/建议保持开启、两滑条注明需总开关开启、滑条备注默认值、取值范围一致 1-8），UI 文本走 Keyed 翻译键并附简体中文翻译（1.6/Languages/ChineseSimplified）；开局预加载（preloadAllNeighborsOnStart）整链删除。回归点：1/1 距离下离开即删、回家不删；关闭分帧开关后普通 tile 同步生成 + 邻接/传送/揭雾正常 + 装 GL 的环境 landform 原生生效；批次调大后生成加快、帧更卡；中文语言下设置窗口显示中文（含据点对话"远行者，你想要做什么？"等既有键）。

优化视觉效果
- 三条接缝带使用不同颜色来标记“对侧在这一格会有传送点但本侧没有”、“接缝本身，本侧和对侧这一条会对齐，对不齐那就是地块投影角度偏移造成的正常细微偏差”、“接缝外侧用于补接缝漏洞的，本侧有传送点但对侧没有”。同时撤离带拓宽到3条接缝带的范围，这样切换地图时接缝和撤离带不会跳变
- 调研是否可以优化岩石显示，避免本侧和对侧本该连续的岩石在接缝带外侧出现明显边界（因为本质上本侧void边上的岩石看void上没岩石就会显示对应的边界，但实际上当对侧也有岩石时不该出现边界）。这个实现可能十分繁琐，所以如果我们评估代价过大会放弃做这个

QoL
添加玩家主动销毁地图的方案
玩家主动命令pawn走到接缝带上时，不论对侧地图是否已经生成，都转为远行队（否则玩家点偏了还得去另一个接缝上组远行队，这样不合适）

计划适配
PerspectiveShift