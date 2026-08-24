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

QoL
添加玩家主动销毁地图的方案（在大世界地图上选中地块用gizmo休眠，休眠后gizmo替换成删除？）
添加一个 PlaySettings 的 Global Controls，用于控制是否显示接缝带并启用撤离功能，默认开启（现在的表现，显示接缝带并允许从接缝带撤离），关闭后不再可以通过接缝带撤离为大地图远行队也看不到接缝带（包括划线）。这个默认开启，关闭后虽然沉浸感提升，但是毕竟接缝带附近是需要特殊处理的，有各种限制，不推荐正常游戏中将其关闭，但如果想拍点rimworld视频啥的可以关了
玩家主动命令pawn走到接缝带上时，不论对侧地图是否已经生成，都转为远行队（否则玩家点偏了还得去另一个接缝上组远行队，这样不合适）

已处置（2026-08，待游戏内回归）：
- 世界图 gizmo 休眠/删除：地块图世界图三态图标（休眠灰/有人绿/无人蓝紫，useDynamicDrawer 动态层）+ 选中 gizmo——活跃非家园图"休眠此图"（手动休眠锁：不被 governor 按距离唤醒，只有进图/命令 pawn 走近其接缝才唤醒；删除距离照常）、休眠图"删除此图"（确认后 RemoveRollingMap）。回归点：手动睡后玩家 pawn 走近（<sleepHops）不被自动唤醒；"查看地图"进图唤醒；命令 pawn goto 该图侧边界带唤醒。
- PlaySettings 沉浸开关（seamExitBandEnabled，默认开）：右下角全局控制条 toggle，关闭 = 隐藏绿带+划线、禁玩家经接缝带成队（征召 job 清 exitMapOnArrival + 组队出口放行原版必失败）——跨缝步行/跨图下令/传送零影响，NPC 撤离链不门控。回归点：关闭时点接缝带 pawn 走过去站着不成队；开启零回归。
- 征召 goto 接缝带成队复查：传送点格路径已实现（原生 ExitMap 组队，不看对侧）；修复带内圈非传送点格白预加载对侧图的口径缺口（CheckPawnGoto 排除集扩到整个 Band）。机械族站原地 = 原版例外保留（用户定夺）。
- 地块图加"重组远行队"按钮：RimExodus_SeamlessTileMap def 加 WorldObjectCompProperties_FormCaravan（原版按钮链的挂载条件），出口走既有 TryFindClosestEdgeCellTo 重定向。回归点：选中地块 → 重组远行队 → 队伍从传送点离场。

已修复（2026-08，游戏内初验未复现——**无法排除偶现，持续观察**）：重组远行队时偶发 "Exception in BreadthFirstTraverse ... ThingFromRegionListerReachable NRE"（动物 herd 游荡的 ClosestThingReachable BFS 触发，被杀动物只是旁观者）。根因 = `SeamlessVirtualTeleporter` 用 `thing.Position = ...` **属性赋值**写虚拟位置——Position setter 带完整网格簿记（region lister/thingGrid 注销+重注册），而传送器时序是先切 mapIndexOrState 再写 Position：ctor 把 pawn 注册进**邻图**的 thingGrid+region lister（本图原注册未动），Dispose 又在**本图**按邻图坐标注销错过真实注册位 → 两张图各泄漏陈旧条目，pawn 后续 DeSpawn（死亡/跨图/组队）清不掉，残留 pawn 变"未生成且无 holder"后 BFS 扫到即 NRE。修复 = 直写后备字段 `positionInt`（与 mapIndexOrState 同款 FieldRef 纪律），评估窗口对两张图零写入。曾加 null 守卫 patch 作诊断兜底，用户定夺删除（patch 越多越不稳定越难维护，根因已修）。**若再出现同款 NRE = 存在另一条独立污染路径，从日志重新定位**。回归点：杀殖民地动物 → 重组远行队 → 无红字；跨图下令/跨图射击（传送器两大消费点）功能不回归。

计划适配
- PerspectiveShift
揭雾多根化（2026-08 已实现，待游戏内回归）：用户发现"邻接图从接缝揭雾"偶发不工作——根因 = 每条边只选 1 个最优洪水根，山体延伸到接缝带把源边室外区分割成多段"走廊"时只有根所在段被揭雾、其余段留雾。修复 = 对源边全部 Standable 传送点逐个发根（同连通域后续洪水空转去重）+ 源边零可站 spot 降级全部活跃边（旧版源边失败不回退会整图留雾）。见 AGENTS.md 揭雾分径条与 Patches_GenStepFog.cs。回归点：找一张山体延伸到接缝带、源边室外区分段的地块图，确认各段均揭雾、被围死的中间空位仍留雾；verbose 日志 outdoorFloodRoots 应 >1。
