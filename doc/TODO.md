远古机械师建筑群之类的poi没有生成，它们应该和据点一样走原版生成。所有site应该都有相同的底层机制，所以我们不该对每个site单独patch（也许这种occupied统一走原版生成就行？毕竟可能有mod在原版地图生成管线上做手脚在生成自己结构时得到额外信息，用我们的管线可能导致兼容问题）
[RimExodus] World tile 68232 is occupied by Site without a live map, skip generation.
（已处置 2026-08 并游戏内回归通过：走近原生生成全 POI 泛化——GenerateTileMap 守卫的 Settlement 分支改为"任意表面层原生 parent 占位 → 原生单帧 GetOrGenerateMap"，确立三层架构[普通地图分帧 / POI 地图原生单帧 / Settlement=POI+额外处理]；site parts/PostMapGenerate 全原版、零 per-def patch；标志改名 GeneratingNativeSeamlessly。原版 Site 全部默认 Encounter 已被裁切注入覆盖；显式设名单外 MapGeneratorDef 的表面层 def 仅终局/剧情对象与 mod 自定义——后续用"全表面层 MapGeneratorDef 通配注入"堵掉）

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


奴隶叛乱后正常跨图追击殖民者，但是叛乱奴隶跨图后不知为何不再叛乱（已处置 2026-08 待回归：根因 = 叛乱载体是 lord 非 mental state，TryTransferPawn 剥离 lord 即叛乱定义上终结；修复 = 传送层剥离点统一备份 LordJob 到 grant.PrevLordJob + 落地 TryContinueLordOnArrival 单点续接策略（并入/重建 LordJob_SlaveRebellion，激进展/逃亡展别保留）。回归标志 = verbose 日志 "Lord continuation (SlaveRebellion):" 行 + 跨图后继续攻击且被殖民者自动反击；多叛奴先后跨图应合流同一场）

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑还需要哪些配置

优化视觉效果
- 三条接缝带使用不同颜色来标记“对侧在这一格会有传送点但本侧没有”、“接缝本身，本侧和对侧这一条会对齐，对不齐那就是地块投影角度偏移造成的正常细微偏差”、“接缝外侧用于补接缝漏洞的，本侧有传送点但对侧没有”。同时撤离带拓宽到3条接缝带的范围，这样切换地图时接缝和撤离带不会跳变
- 调研是否可以优化岩石显示，避免本侧和对侧本该连续的岩石在接缝带外侧出现明显边界（因为本质上本侧void边上的岩石看void上没岩石就会显示对应的边界，但实际上当对侧也有岩石时不该出现边界）。这个实现可能十分繁琐，所以如果我们评估代价过大会放弃做这个

QoL
添加玩家主动销毁地图的方案
玩家主动命令pawn走到接缝带上时，不论对侧地图是否已经生成，都转为远行队（否则玩家点偏了还得去另一个接缝上组远行队，这样不合适）

计划适配
PerspectiveShift