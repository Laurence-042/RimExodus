# AGENTS.md — RimExodus 开发约束与架构事实

本文件位于 `D:\SteamLibrary\steamapps\common\RimWorld\Mods\RimExodus`。

它只记录后续开发必须遵守的规则、当前仍有效的架构不变量，以及少量容易复犯且难从代码表面看出的教训。功能介绍、设置、兼容性和已知限制不在这里重复；历史尝试、回归清单和待办也不在这里维护。

开始工作前必须完整阅读本文件和 `README.md`。如果工具输出截断，继续分段读取到文件末尾；首次回复中给出上述目录，以确认读取的是正确仓库。长期信息应写入本文件、对应专题文档或代码注释，不依赖 agent/harness 的临时记忆。

## 1. 信息归属与维护规则

按以下顺序确定事实归属：

- `README.md`：对外功能、设置项、Mod 兼容性、已知限制、源码目录结构的单一事实源。
- `doc/接缝带定义.md`：接缝几何与圈层。
- `doc/地图生成步骤.md`：genStep 顺序、注入点与生成路径。
- `doc/地图滚动休眠.md`：休眠、降频、删除、封存、天气域和手动管理。
- `doc/边界行为表.md`：不同主体与移动来源的边缘行为。
- `doc/跨图命令适配审计.md`：跨图命令覆盖矩阵。
- `doc/第X阶段-*.md` 与 `doc/无缝世界地块探索.md`：设计叙事与阶段结果。
- `doc/TODO.md`：未完成工作和待验证项；不得把其中的排期当成发版状态。
- 代码注释：只承载紧贴实现、离开代码就容易误改的局部约束。

维护时遵守以下规则：

- 不在 `AGENTS.md` 保留“旧方案 + 覆盖说明”。方案被替换时直接删除旧结论；有复盘价值的过程移到对应阶段文档或代码注释。
- 同一事实只保留一个权威位置，其他地方用链接和一句摘要引用。
- 功能改动必须同步对应专题文档；对外兼容性、设置或已知限制变化还要同步 `README.md`。
- `Changes.txt` 面向玩家，只写可感知的症状与变化，中英逐条对照，禁止内部类名和机制行话。
- 所有玩家可见 UI、消息和对话都使用 Keyed 翻译；英语与简体中文文件必须同步。
- 玩家可见简中术语沿用官方：Settlement = 派系基地，Camp = 营地。Site 按具体显示名翻译；“定居点”不是 Settlement 的通用译名。

## 2. 提交、发版与工作方式

- 未经用户明确指示，不得 `git commit` 或 `git push`。完成实现后只做与风险相称的验证，等待用户实测并明确要求提交。
- 用户要求“提交”时检查 `Changes.txt`。玩家可见的修复、优化、新行为或设置变化应加入当前最新一节。
- 永远不要主动修改 `About.xml` 的 `modVersion`，也不要自行创建新的 `Changes.txt` 版本节。版本 bump 和新版本节均由用户明确发起；不确定归属时询问用户。
- 开工前检查工作树；用户已有改动必须保留，不能为方便而 reset、checkout 或覆盖无关文件。
- 查询优先使用环境提供的 Grep/Glob/Read。若这些工具对已知存在内容静默失败，可用 PowerShell `Select-String` 后备；不要为普通检索混用多层 shell 转义。
- 新源码按 `README.md` 的九个功能目录放置，Harmony patch 与所属功能域同目录，统一使用 `RimExodus` namespace；不要把文件重新平铺到 `Source/` 根。
- 编译命令为仓库根目录下 `just build`，等价于 `dotnet build Source/RimExodus.csproj -c Debug`，产物为 `1.6/Assemblies/RimExodus.dll`。

## 3. 外部源码与兼容性参考

涉及 RimWorld 或外部 Mod 内部行为时，只能以 `references/` 中已有源码/反编译内容为施工依据。若缺少目标版本/Mod 的可靠参考，先明确告知用户，不要自行扫描 SteamLibrary、下载或反编译其他内容。

建议在进行底层机制调整时，提醒用户确保 `references/` 包含 RimWorld、MapPreview、Geological Landforms、Vehicle Framework、Vehicle Map Framework、Perspective Shift、Giddy-Up、Vanilla Expanded 系列等调查材料；实际使用前先核对目录和版本。兼容性对外结论以 `README.md` 为准。

## 4. 总体架构：表面地图对等

- RimExodus 的目标是所有合法表面 `MapParent` 地图连续通行。任务地点、遭遇、派系基地、营地和普通地块都走裁切、接缝与传送链；不得新增“非 RimExodus 地图放行原版”的类型分支。
- 唯一类别排除是口袋图（`PocketMapParent`，包括 VMF 载具内部图）和非 Surface 空间层图。`GetMapWorldTile(map) < 0` 对它们表示排除出表面无缝语义；对 parent 缺失或 worldTile 未初始化则是防御性失败。所有按 tileId 的天气域、邻接、边界和 governor 查询都应收口到该入口，避免 tileId 冲突污染。
- 普通地块使用 `MapParent_SeamlessTile : MapParent`，其 `Tile` 是真实 `PlanetTile`；没有 `sourceMap`、`IsPocketMap` 或宿主口袋图语义。
- 家园没有生成、天气或邻接特权。仅有两项工程差异：原生 parent 的邻接/基础快照存于 `SeamlessTileManager`，地块 parent 存于自身；家园生命周期受 `SeamlessMapGovernance.IsProtectedHome` 保护，不休眠、不删除。
- 数据载体分支只能经 `SeamlessMapData`；管辖、家园保护和可删除性只能经 `SeamlessMapGovernance`。不要复活 `IsAnchorMap` 或散落的类型判断。

## 5. 邻接与坐标契约

- `NeighborLink` 主键为 `worldTile`，保存 `neighbor` 与 `offset`。
- `offset` 的唯一含义是“邻居局部坐标到本地图局部坐标”的平移：`neighborLocal + offset = myLocal`。反向映射为 `neighborLocal = myLocal - offset`。
- 邻接登记必须通过 `SeamlessNeighborRegistry` 双向维护；查询通过 `SeamlessTileGraph`。休眠图在运行时邻接/显示口径中视为不可用，但其 Map 仍存在。
- 地图间绘制投影只使用 `SeamlessViewProjection.TryProject(sourceMap, sourceLocal, destinationMap, out projected)`。调用方必须显式给出目标坐标系；不要增加含糊的 `ToCurrent` 包装、手写 `+offset`，或在调用处自行遍历邻居。
- 8 邻环使用 `GenAdj.AdjacentCells`，含中心 9 格使用 `GenAdj.AdjacentCellsAndInside`，切比雪夫距离使用 `SeamlessGridMath.ChebyshevDistance`。半径大于 1 的方窗若需显式循环，注释应指明同一口径。

## 6. 接缝几何、地形与生成

几何细节以 `doc/接缝带定义.md` 为准，当前不变量如下：

- 圈层由核心区向外依次为：带内圈、离散边圈、带外圈、void；三圈合称接缝带 B。传送圈是离散边圈与带外圈，void 位于接缝带外。
- `SeamlessPolygonGeometry.BuildSeamBand` 是 void、传送点、混合范围和边界查询的唯一几何实现。不要在消费方复制边界算法。
- 等边投影模型同时支持五边形与六边形；不要回退到外接圆半径固定为 `0.5 * mapSize` 的旧模型。
- 道路/河流穿越点必须由世界边和固定 FNV-1a 对称哈希确定，不能使用跨进程不稳定的 `System.HashCode`。道路、河流、中心走廊使用特征几何保护；不要恢复按水体类型一刀切的例外。
- `TileMutatorWorker_RiverIsland` 覆写了 `GetDisplacedPoint`，基类 patch 不会覆盖它；必须保留单独 Postfix 并复用普通河流的弯曲归零实现。

当前关键 genStep 顺序为：

`CenterCorridor(100) → CoastalEdgeFill(230) → SeamlessTile(389) → Roads(390) → SeamOverride(392) → ZoneRestore(1100) → EnterSpots(1490) → Fog(1500)`。

生成相关约束：

- 389 在清理前保存完整基础 terrain/自然岩/roof 快照，再铺 void 并刷新 PathGrid；392 混合并再次刷新 PathGrid。任何直接写 `terrainGrid.topGrid` 的路径都必须立即调用 `RecalculateAllPerceivedPathCosts()`。
- `GenStepDef.preventsGenSteps` 必须先执行互斥过滤，语义是保留声明者、移除被声明步骤；不要和 biome/mutator 的 `preventGenSteps` 混淆。
- 普通邻接地块可走分帧增量生成；原生 POI 保持原生同步 `GetOrGenerateMap` 契约。不得全局异步化 `MapGenerator.GenerateMap`，因为调用方同步解引用其返回值。
- 分帧生成的唯一对外入口是 `SeamlessTilePreloader.QueuePreload`；请求串行消费，忙态重排队。不要新增同步直调私有 `GenerateTileMap` 的旁路。
- 增量生成成功统一由 `FinishGeneration` 收尾；准备、genStep 和收尾异常统一进 `CleanupFailedGeneration`，负责清防重入状态、半成品 Map、邻接反链及已登记 parent。不要把清理分散进 onComplete 子步骤。
- 生成期间禁止存档。保存对话框层负责避免“显示成功但实际未存”，`GameDataSaveLoader.SaveGame` 层负责自动存档兜底。
- 增量路径没有调用原生 `MapGenerator.GenerateMap` 本体，因此只在成功完成后复放该方法的第三方 Harmony Postfix；Prefix、transpiler、finalizer 不得脱离原方法单独复放。同步/POI 路径本来会执行 patch，不得重复复放。
- MapPreview 后台线程只允许预览安全步骤。使用 `IsGeneratingPreviewOnCurrentThread` 区分当前线程，主线程正式生成仅在预览占用共享 static 时排队避让；临时采样集合必须为 `[ThreadStatic]`。
- 内置 Ocean 补全在 Mod 构造器读取设置后锁存，Def 修改延迟到 long event 收尾，并用 `DefDatabase.GetNamedSilentFail` 获取 Def；不要在构造器时点直接使用尚未注入的 DefOf。只补空表，不覆盖第三方已有配置；关闭后 Ocean 专用补全和步骤过滤整体退出。

## 7. 接缝参考与快照

- `SeamStripData` 已删除，禁止重新引用或持久化派生条带。
- 新图只参考世界相邻且当前实际存在于 `Find.Maps` 的 Surface Map。软休眠 Map 因仍在 `Find.Maps` 可作为数据来源；封存/删除图没有 Map，不参与参考。
- 映射格优先读取邻图当前 terrain/自然岩/roof；当前 terrain 为 null/void 时回落该图 389 前的完整基础快照。读取结果必须区分“无数据”和“有数据但岩/顶明确为 null”。
- 同步 `MapGenerator.GenerateMap` Prefix 与 `IncrementalMapGenerator.Start` 都必须在占用生成 static 之前调用 `EnsureNeighborSnapshots(targetTile)`，为目标周围实际加载的参考图补快照。MapPreview 当前线程、口袋图和空间层跳过；失败 Warning 后仅缺失的 void 侧采样降级跳过。
- 地块图基础快照只驻内存并可按需重建；原生图的紧凑快照可持久化，用于卸载恢复。旧存档中的 `seamStrip` XML 自然忽略，下次保存消失，不做迁移或版本 bump。
- 快照 Def 表索引 0 保留为空；读取兼容旧零基格式。

## 8. 地图生命周期与天气域

完整规则见 `doc/地图滚动休眠.md`，实现必须保持以下边界：

- 软休眠不卸载 Map：Map 仍在 `Find.Maps` 并随档序列化，但 Map/Thing tick 停止、运行时邻接与跨图入口关闭。唤醒负责幂等恢复 tick 注册。休眠状态本身不序列化；手动休眠 tile 集合序列化，并在读档 `LoadedGame`、首 tick 前重新入睡。
- 距离源是所有玩家阵营 pawn 所在图与玩家远行队所在 tile 的并集；空源时本轮不睡不删。距离每轮用世界网格 BFS 现算，不维护会过期的距离图。
- `dormancySleepHops` 的 UI 与运行时下限都是 2；删除距离运行时至少等于休眠距离。保活条件集中在 governor：CurrentMap、含玩家 pawn、`IsProtectedHome`、已追踪活跃威胁。
- pawn 位置变动事件只调用 `SeamlessPawnLocationTracker.NotifyChanged()` 置脏；影子远行队维护和休眠 Sweep 在下一 `GameComponentTick` 的固定顺序执行。不要在 Thing tick 回调中直接睡图、删图或拆影子。
- 活跃威胁判定集中在 `SeamlessMapGovernance.HasActiveThreat`；发现走休眠 Sweep，清零走独立高频复查。二者节奏不同，不要合并。休眠图不因威胁自动唤醒。
- 降频是活跃与休眠之间的中间态。0% 时快速区外 Thing 从 tick 表摘除；恢复必须先 `RemoveAllFromMap` 再统一注册，避免 `TickList.RegisterThing` 双注册。Sleep 调 Unthrottle 时使用 `restoreTicks:false`。接缝快速区内 Thing 在 0% 仍全速。
- 殖民者栏过滤后必须同时重建 `cachedDrawLocs`、`cachedReorderableGroups` 和 `cachedScale`；反射 FieldInfo 静态缓存，不在每次 recache 重取。
- 原生 `ShouldRemoveMapNow` 家族由 governor 接管删除时机，但删除语义仍调用原版偏好决定是否同时移除 WorldObject。主动 WorldObject 销毁不属于这条被动接管路径。
- 前哨封存只适用于有 Home area 的地块图，保存 `ZoneMapRecord`；封存图没有 Map，不提供接缝参考。恢复顺序是自然生成、与当时活邻图混合、再在 1100 重放记录。所有自动、gizmo 和 Dev 删除必须经 `RemoveRollingMap`，由该入口统一决定封存、弹窗或删除。
- 可重建的运行时缓存及其 built/stale 标志应整体不序列化，并提供首 tick 自愈。禁止只持久化 built 标志而丢失字典内容。传送点的 arrival 缓存同理，读档后由首 tick 刷新。
- `seamBandCache`/`polygonCache` 在图删除时释放，换档时全清；不得改成进程内永不失效。

天气域采用“决策集中、执行各图”：

- 同 PrimaryBiome 且世界邻接连通的已登记 Surface Map 属同一域。每图保留自己的 WeatherManager/WeatherDecider/SkyManager；仅决策和 Transition 事件共享。
- 域激活图是持久注册记录，不是每次重算时按 tile 编号推断。新域第一张报到图成为激活图；休眠、0% 凝固、删除或强制天气注册按既定事件交接。
- 被动成员的 WeatherDecider tick 整体短路；TransitionTo 与 DisableRainFor 在域内广播并用 try/finally 重入保护。执行类 patch 用 `Priority.Last` 尊重第三方取消，决策门使用 `Priority.First`。
- 口袋图、空间层图和未报到图 fail-open 走原版。

## 9. 传送、交互与战斗

- 传送资格使用 `SeamlessTransferGrants` 许可登记制。踩点热路径只查许可并匹配；无许可不传。玩家跨图命令、NPC 撤离、追击和跟随分别登记 Bridge/Evacuation/Pursue/Follow。
- 触发器必须在 `Pawn_PathFollower.TryEnterNextPathCell` Prefix 读取 `pather.nextCell`。Postfix 会晚于 job 完成和许可清理；传送成功后以 Map 是否变化决定是否跳过旧图方法体，不能用 `pawn.Spawned`。
- `TryTransferPawn` 不自行校验 pawn 与 spot 的格距；调用方负责保证正在进入/绑定该 spot，坐标映射只依赖 spot。
- 跨图命令的架构是“点击在真实邻图重放 + 公共函数层跨图化 + StartPath 桥接”。不要恢复整方法接管 FloatMenu 或为每个 provider 手工注入选项。designation 前置型命令的覆盖边界以审计文档为准。
- 跨图寻路只支持单跳。桥点选择使用双侧代价场；普通 Pawn 与 VF 载具必须使用各自的路径成本源和准入规则。
- 外部 Mod 若用多个 spawned Pawn 表示一个移动主体，使用 `SeamlessTransferAssociations` provider 扩展；关联 Pawn 复用 `TryTransferPawnInternal(captureAssociations:false)`，不要在核心硬编码外部字段。Giddy-Up 恢复骑乘前必须结束旧图的 Mounted job，再调用其即时 GoMount。
- NPC 撤离目标是传送点时，`JobDriver_Goto.TryExitMap` 在到达 spot 前推迟原生离场；对端活跃则跨图续链，对端未加载/休眠则在缝线上交还原生离场。不要按 lord 类型拆分这条共同链。
- 跨图战斗覆盖原版/CE 的移动 projectile（含 flyOverhead）、CE instant RayCast 与纯格目标。坐标、LOS、射程和弹道以射手图统一坐标计算，逐格阻挡按归属图路由；爆炸 AoE 只在实际落点图原生结算，休眠邻图不可参与战斗。
- 弹丸在 `Projectile.TickInterval` Prefix 于越缝前迁移 Map，保持 origin/destination 平移和剩余飞行状态；交接后命中与爆炸交回原版目标图处理。
- `SeamlessVirtualTeleporter` 的评估窗口必须直写并恢复 `mapIndexOrState` 与 `positionInt`，禁止用 `Position` setter 触发两图网格簿记。
- lord 是 map-anchored 状态，默认跨图后不续；只有与地图无关且已明确支持的行为在 `TryContinueLordOnArrival` 集中重建/合流。mental state 是 pawn-local，不要清理。

## 10. 原生 POI、派系基地与影子远行队

- 已有表面原生 parent 占位使用原生同步 `GetOrGenerateMap`，保留 Settlement/Site/第三方生成语义；生成后再做邻接、天气、传送点和缓存接线。不要按具体 SitePart 建白名单。
- 中立/友方派系基地必须阻止原版 `CheckDefeated` 的“无敌对威胁即败亡”误判；敌对基地仍走原版。真败亡后邻接链接要重接到同 tile 的废墟 parent。
- 派系基地贸易商只在生成时选一次，不写原版 `pawn.trader` 或 `wantsToTradeWithColony`。对话复用原生 `Dialog_NodeTreeWithFactionInfo`，选项同时采集 `GetCaravanGizmos` 与 `GetFloatMenuOptions`，并显式过滤攻击、进入、访问等会二次进图的命令。
- 影子远行队是未加入 `Find.WorldObjects` 的常驻投影，用于兼容即时与延迟的远行队交互；不 tick、不序列化、不出现在世界图。不要回退到“加入世界对象表再逐面隐藏”。
- 影子成员不能经 `Caravan.AddPawn`/`ThingOwner.TryAdd` 注入；当前实现直接维护内部列表与 holdingOwner，因此所有 DeSpawn/真组队入口必须先释放影子持有关系，`GetRootMap` 对影子持有链返回实际地图。
- 影子维护与 spawnedThings 陈旧条目清扫依赖 `SeamlessPawnLocationTracker`；Wake 重注册只处理 `t.Map == map`，避免活 Pawn 双 tick、mapIndex 错位或被错误 Discard。

## 11. 渲染与效果

- `SeamlessTileRenderer` 只在合法 Surface Map 工作；口袋图/空间图必须早退，避免清色覆盖星空或载具内部背景。
- 邻图静态层当前收集 Terrain、ThingsGeneral、LightingOverlay、FogOfWar，并把 Watergen 几何送入原版 WaterDepth 子相机。水面必须保留原 `TerrainWater` shader 与深度几何配对，禁止回退到静态 TerrainFade/Hard 贴图。
- WaterDepth 视区使用“当前视区 + 当前缩放半屏”的保守余量，解决快速横移时 RT 边缘 Clamp 拉伸；不要改成全图提交或第二套相机/RT。
- 邻图动态物单独绘制 Pawn、Projectile、RealtimeOnly Thing 和 Mote。Mote 保持源图归属与生命周期，仅在最终 `Mote.DrawMote` 出口通过 `SeamlessCompositeDrawContext` 平移。
- 当前不重复运行邻图整套 FleckManager；这样避免水波/水花使用 CurrentMap 水深而错层，也避免额外每帧成本。切到该图后 Fleck 恢复原版绘制。
- 邻图 section 只在进入视区且 dirty 时调用原版 `Section.TryUpdate(neighborView)`；不得恢复“任意脏就全图全层 RegenerateAllLayers 并清标记”。视区外 dirtyFlags 必须保留。
- 即使没有邻居，void 渲染分支也要清色并清 CommandBuffer，防止上一帧残影。
- 地图删除和换图时，terrain 与 water 两组 CommandBuffer 必须同步 clear/release。

## 12. 兼容层通用纪律

- 兼容层全部软检测：目标 Mod 缺失时零行为、零错误；类型或签名漂移时 Warning 并安全降级，不能让兼容诊断杀死 RimExodus 初始化。
- 手动 `AccessTools.Method + harmony.Patch` 不受离线 PatchAll 验证器覆盖；必须 try/catch，并输出可核对的绑定成功行。
- Geological Landforms：分帧路径复刻其 Prepare/Cleanup 上下文；RimExodus parent 必须登记进 GL 的 IgnoredWorldObjects。GL 河流通过 Path tree 钉位，不能回退到离散函数场 warp 或生成后地形修补。
- MapPreview：后台预览与正式生成共享 MapGenerator static，必须互斥；线程判定不能只看全局 IsGeneratingPreview。
- Vehicle Framework：跨图前先同步就绪化目标图 VehiclePathGrid，再用整车矩形与载具自己的成本源判断落点。不得用普通 Pawn 的 Walkable 或 thingGrid Standable 预筛深水/植被。
- Perspective Shift：WASD 绕过 job/pather，预加载和踩点需兼容钩子；传送方向以输入与邻接 `offsetDir` 的正点积门控，跨图后切 CurrentMap 并保留缩放。PS 与 VF 同装时驾驶路径走独立 vehicle movement 钩子。
- VGE 世界炮击弹丸在离开发射图时交还 VGE 原生方形边界流程；抵达目标图后 `targetTile` 无效的落地弹丸仍走普通 RimExodus 直射逻辑。
- Combat Extended：保持单 DLL 软反射；同图始终交还 CE。CE verb 必须跳过通用 `Verb.TryFindShootLineFromTo` 接管，以 CE 自身 `TryFindCEShootLineFromTo` 底层钩子为跨图射程/LOS 唯一事实源；CE 浮点射线在可能为负的统一坐标上须先整体平移到非负域再枚举。纯格目标的真实 Map 只由 `SeamlessCrossMapCellTarget` 侧表承载，不注入假 Thing。移动 `ProjectileCE`（含 flyOverhead/guided/CIWS）在 `MoveForward` 后、越界/碰撞前迁移并平移全部位置状态；制导只在基础 worker 读目标时投影坐标，不复制导引算法。instant 在 `ProjectileCE.RayCast` 逐格路由并交还 CE Impact；Ability 在 `CE_Utility.LaunchProjectileCE` 公共入口修正角度。`globalTargetInfo` 有效的世界炮击不得误纳入。`Building_TurretGunCE` 静态构造器会加载 Unity 材质，炮塔 detour 必须延迟到 long event 主线程收尾注册。完整支持矩阵见 `doc/CombatExtended跨图射击适配.md`。

具体兼容实现和版本状态以 `README.md` 与 `Source/Compat/` 代码注释为准，不在本文件保留逐轮调试记录。

## 13. Harmony、日志与验证

- 能用 Prefix/Postfix、ref 改参或收尾字段修正完成的功能，不写 transpiler。必须写 transpiler 时，先对真实游戏 DLL 解码确认 IL；原地 mutate `CodeInstruction`，不要替换对象导致 labels 丢失。CLR 验证器无法发现全部 Mono DMD 非法 IL，游戏启动日志才是最终依据。
- Harmony 按参数名绑定；不确定时使用已核实的真实签名或位置参数。手动绑定的重载必须显式参数类型消歧。
- 分模块日志使用 `RimExodusLog` 与 `RimExodusLogModule`。新诊断消息走对应模块；Warn/Error 不受开关控制。低频生命周期与显式 Dev 动作可常开，热路径和周期细节必须门控。旧 `verboseLogging` 只保留存档兼容，不新增消费点。
- 离线 PatchAll 验证器位于 `C:\Users\Laure\.zcode\tmp\rimptest\`。新增或修改 Harmony patch 后运行它；当前记录基线（2026-09-09）为 `BOUND OK 109 / FAILED 16`，16 项是已确认的 CLR 伪迹（新增 `DrawTargetHighlightWithLayer` 与既有 `GenDraw` 绘制 patch 同为 `SecurityException: ECall`）。基线变化时只更新当前数字与伪迹清单所在注释，不保留数字演进流水账。
- 最终确认必须包含游戏日志中的 Mod 实例化行与 `GetPatchedMethods` 报告。测试本地构建前先确认游戏实际加载的是刚编译 DLL，而不是 Workshop 旧版本。

## 14. 存档与卸载

- Mod 已发布，任何序列化字段、Scribe 键或 Def 删除/改名都要评估旧档；需要迁移或破坏兼容时必须先告知用户并由用户决定方案。
- 运行时可重建缓存默认不序列化；需要持久化时数据与有效性标志必须成套保存。
- 自定义 Def 的裸字段要显式赋值。`RimExodus_Void.texturePath` 即使游戏本体不渲染也必须存在，第三方小地图会读取它。
- 卸载前必须在 Mod 仍加载时运行“恢复原版兼容模式”，随后立即存档。恢复会删除无缝地块图、用基础快照回填原生图 void 区并移除传送点/隐形岩链接；无快照时只做边界地形降级恢复。
- `RimExodus_SeamlessEnterSpot` 与 `RimExodus_VoidRockLink` 为不可 Destroy Def，恢复时使用 DeSpawn；恢复写 terrain 只能在专用 `Restoring` 旗标范围内放行 void 写入守卫。
- 卸载后的首次读档可能出现残留组件类找不到的一次性红字；原版会回落为空组件，再保存一次即可清除。不要为消除此无害现象重新引入全局 SaveGame 组件剥离 patch。
