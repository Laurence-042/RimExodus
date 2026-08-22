（2026-08 已修复，待游戏内回归）倾斜六边形图组建远行队两连修复：
①"你的远行队无法离开此区域。确保有任一方向可抵达"——根因 = 5 参（Rot4）
CellFinder.TryFindRandomEdgeCellWith 无 patch（曾落地后被"未倾斜图 4/6 tiles 正常"误判回退为
纯诊断——那只是角点恰好轴对齐触方形边时原版的侥幸），原版候选格钉死方形边，倾斜图
（顶点朝向来自世界球面，θ≠0 mod 30°）整圈 void → AvailableExitTilesAt 0 tiles → startingTile
无效 → CheckForErrors 弹 MessageNoValidExitTile。修复 = Patch_CellFinder_TryFindRandomEdgeCellWithRot4
（Patches_CellFinder.cs，候选池 = 方向半平面接缝格）。
②"未发现有效的打包点。确保所选择的离开位置（西）是可以到达的"——两处独立缺口：打包点
validator 要求 district.TouchesMapEdge（唯一计算点 RegionMaker.AddCell 只认方形边，倾斜图无任何
区域触边 → 恒 false）+ TryFindClosestEdgeCellTo patch 未过滤可达（原版是 root 区域 BFS 保证可达）。
修复 = Patch_RegionMaker_AddCell（区域含传送圈格即触边，"地图边缘"对区域系统整体变为六边形边，
顺带修好袭击入口 TryFindRandomPawnEntryCell 同款 validator）+ FindNearestEnterSpot 补
requireReachableFromRoot（PassDoors 从 root CanReach）。
回归点：①倾斜图组队全流程（路线接受 AvailableExitTilesAt N>0 → Send 不弹错 → 打包点找到 →
集结出走）；②未倾斜图组队不回归；③袭击/野生动物入口正常从接缝圈进图（同根修复——修复前
倾斜图 WildAnimalSpawner 静默死亡、袭击 EdgeWalkIn 选点失败）；④初始野生动物不再偶现生成在
void 上（RandomAnimalSpawnCell_MapGen 不再走 fallback RandomCell——曾因 district.TouchesMapEdge
倾斜图恒 false，随机格落进方形角部 void 区（距六边形 ≈69 格 > 搜索半径 45）时复现）；
⑤动物栏舍贴接缝圈/void 边会被视为开放（语义辐射，预期行为）；⑥NPC 撤离/行人穿越（4 参链）
不回归。已知噪音（不修）：飞鸟落 void 被原版传送自愈 + Pawn_FlightTracker.Notify_JobStarted
单次 NRE（鸟自动恢复，AGENTS 观察项）。详见 AGENTS.md 边缘行为统一节。

（2026-08 已实现，待游戏内回归）其他派系 Settlement 无缝接入：走近原生生成（GetOrGenerateMap，
复用既有 WO）+ 好感度可交易时按 7 条件选身价最高驻军为贸易商（一次性指定）+ 右键对话面板
"远行者，你想要做什么？"（交易 + 据 gizmo 集合，天然适配 mod 扩展交互）+ 地图内交易三 patch
（随身物识别/贸易商身边掉落）。设计与勿回退要点见 doc/地图滚动休眠.md 2.11 末段；回归清单见
同文件第五节第 11 条。

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑还需要哪些配置
