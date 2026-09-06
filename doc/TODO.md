- 取得报告者日志（开 `logCaravanExit` 开关，看 `[caravan-exit]`）确定caravan无法正确撤离的原因
- [已处置待游戏内回归 2026-09-05] Vehicle wrecks from Vehicle Framework Tier 3 spawn on the edge of the map, leaving most (if not all) seamless maps completely empty of repairable wrecks. I DO see them spawn at the very edge of the map, but they're out of the actual playable area and can't be reached - they disappear on crossing.（根因定案：残骸生成走 VEF 的 ObjectSpawns 系统，其逐格判据不知道六边形裁切且无通行性检查 → 方形边缘 void 环是全图最大"合格空格池"（植被群系里核心区几乎全被植物/岩石占据），物体系统性落 void；且增量分帧路径不调用 `MapGenerator.GenerateMap` 方法本体 → VEF 的 Harmony Postfix 在地块图上根本不跑（零刷出）。修复两件：`Source/Compat/SeamlessVEFCompat.cs` 格级 CanSpawnAt Postfix 把 void ∪ 接缝带判不可刷（覆盖全部触发路径）+ `Source/Generation/GenerateMapPostfixReplay.cs` 通用复放 GenerateMap postfix（增量路径补齐，一切挂该方法的第三方 Postfix 受益不止 VEF）。回归清单见 AGENTS.md「邻居预加载与异步加载」节两新条 + README 兼容表 VEF 行；启动确认行 = "VEF compat: bound ObjectSpawns cell filter"。）

It would be cool if some objects were tagged as persistent, like walls and constructables near the borders, so you could have an actual seamless transition.


[已处置待游戏内回归 2026-09-05] 优化袭击生成逻辑，让袭击生成在已加载地图外边界，而非从当前地块对侧已加载地图的边界凭空出现。（调研定案：可行且大部分基础设施已在阶段5 就位——"敌对 NPC 从 B 行军到 A"与已实测的追击链同形状。落地 = `Source/Combat/SeamlessRaidOuterSpawn.cs` + `Patches_RaidOuterSpawn.cs` 4 patch[BOUND OK 94] + `Patch_CellFinder_TryFindRandomEdgeCellWith` 4 参 Prefix 的 ambient 分支；资格三分支照原规划（世界网格枚举、降频算活跃、全活跃 fallback = 任意 B-C 缝）；spot 朝未生成邻居也预铺 = 朝向格免几何锚点；跨图生成在 `RaidStrategyWorker.MakeLords` Postfix 整组迁移 + B 上重建 AssaultColony lord，行军交给现有推进链零新机制。用户定夺：宿主一视同仁含 POI 图 / 仅袭击类约束 / B 上有玩家接受涌现。邪教徒围攻机制特殊且难度顾虑 → v1 排除；发狂动物/食尸鬼仅同图约束。回归清单见 AGENTS.md「跨图索敌与射击」节袭击外缘生成条；设置 `raidOuterSpawnEnabled` 默认开。原规划全文如下留档：）

背景：当前步行类型的袭击（在地图边缘生成的敌人攻击，包括但不限于发狂的动物、异象的食尸鬼猎群、部落的步行袭击、海盗的步行袭击、异象的邪教徒围攻等等）都是直接生成在触发图的边缘——即使触发图的对应边界对面还有其他地图。这导致看起来步行袭击像是凭空出现的，不符合游戏设定

原则：我们的寻路都是不带多跳的，所以袭击者应该也不能多跳，也就是说袭击者要么在被袭击的图上出现，要么在被袭击的邻接图上出现，不会在需要跨越2次接缝的地块出现

预期逻辑：
假设地图为A触发了袭击，检查A检查周围第一圈地图B1-B6（也可能是B1-B5，考虑到有12个五边形地块。我们的实现不该显式区分具体数量，而是使用通用逻辑）和第二圈地图C1-Ck。
如果B1是活跃的而且其某条接缝对侧的Cx不活跃，那么B1和Cx的接缝就是可生成袭击的（游戏叙事是袭击者从玩家没看到的Cx进入了B1，然后袭击者从B1向A进军）
如果存在不活跃的B2，那么A和B2的接缝是可以生成袭击的（游戏叙事是袭击者从玩家没看到的B2直接进入了A）
如果第一圈所有B都是活跃的且第二圈所有C也都是活跃的，所有Bx和Cy的接缝都是可生成袭击的（fallback）
选择一个可生产袭击的接缝生成袭击

预期可能遇到的限制与预案：
邪教徒围攻的机制可能和其他聚集生成的步行袭击不同，需要确定其机制再决定做不做。而且邪教徒围攻在更大范围生成可能会带来显著的难度提升（原本玩家就需要绕地图一圈打邪教徒，绕多个地图一圈显然难度大幅上升），所以不能做也是可以接受的


- [已修复 2026-09-06，游戏内回归通过] @Touhoufanatic报告，排期0.1.10：ancient_smoke_vent在邻接地图上没有显示，按理说这应该是一个building（根因定案：报告观察到的实为 `AncientHeatVent` 等 `drawerType=RealtimeOnly` 的排放孔——其唯一绘制路径是聚焦图的 DynamicDrawManager（原版只在 CurrentMap 上跑），静态 mesh 被 `SectionLayer_Things.Regenerate` 的 RealtimeOnly 过滤结构性排除，而邻图背景只收集四个静态 section 层 + 手画 pawn/弹丸 → 跨缝整只缺失、聚焦才可见。修复 = `SeamlessTileRenderer.DrawNeighborRealtimeThings`（遍历邻图 `dynamicDrawManager.DrawThings` 只挑 RealtimeOnly、排除 pawn/弹丸防双画、视区+雾过滤与 pawn 通道同族）；烟/毒型排放孔是 MapMeshOnly 本体本就在 mesh 内正常显示，其烟柱 fleck 属邻图不画的动态粒子（已知缺口，是否补画待定夺）。诊断/定案过程与探针勘误见 AGENTS.md「void 渲染与邻居背景」节）
- @Icarus报告，排期0.1.10： 较小的地图上某些大型地标建筑（比如燃料精炼设施）可能会缺少一角
- @Orizay报告，排期0.1.11：when using turrets that can shoot cross maps like VGE 1 gauss cannon the shoots gets stuck on the neighbor maps. For example if I have all the six maps generated around my home map and I shoot in to another map far away the shoots just get's stuck on the maps around my home map. Hope u can replicate a and fix the bug, overall I'm loving this mod so far.
- @@lophothedilo报告，排期0.1.11：Giddy-Up 2 - Continued https://steamcommunity.com/workshop/filedetails/?id=3674332861 骑乘动物跨过接缝时人和动物分离了（应该是因为缺少了类似奴隶叛乱、征召状态相似的恢复处理——更统一的适配方式？）