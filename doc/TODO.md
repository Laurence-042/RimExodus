（2026-08 已实现，待游戏内回归）其他派系 Settlement 无缝接入：走近原生生成（GetOrGenerateMap，
复用既有 WO）+ 好感度可交易时按 7 条件选身价最高驻军为贸易商（一次性指定）+ 右键对话面板
"远行者，你想要做什么？"（交易 + 据 gizmo 集合，天然适配 mod 扩展交互）+ 地图内交易三 patch
（随身物识别/贸易商身边掉落）。设计与勿回退要点见 doc/地图滚动休眠.md 2.11 末段；回归清单见
同文件第五节第 11 条。

（2026-08 已修复，待游戏内回归）据点图 fog：揭雾改为 `Patch_GenStep_Fog_SeamOrigin` 分径
（Patch 挂 GenStep_Fog.Generate 本体）——初始地图（开局/扎营/定居/gravship）原版中心揭雾；
邻接生成图只从生成方向的接缝洪水揭雾（揭穿室外连通域、围墙房间留雾）+ 全图 void 格直接揭雾
（雾留 void 只会在跨缝看邻图时挡视线）。首版自建 1505 genStep（±15 平带 → 洪水）已删
（用户定夺"不自搓第二套清除、不与中心揭雾并存"）。

（待定夺）派系交易对话框样式：creepjoiner 的"对话"本体是 ChoiceLetter_AcceptCreepJoiner
（原版信件系统的 DiaOption 面板，标准信件 UI 非自绘定制）——我们的"远行者"面板目前是自写
Window，若要样式一致需转为 ChoiceLetter/Dialog_NodeTree（DiaOption 按钮 + Disable 禁用态，
信件会留在信件栈可回看）。

（2026-08 已实现，待游戏内回归）地图加载左上角进度：MapGenerationProgressUI
（GameComponentOnGUI，读 IncrementalMapGenerator.GenerationProgressDescription 显示
"Generating map... (步数/步骤)"，仅覆盖分帧增量路径——Settlement 原生生成是同步单帧冻结）。

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑还需要哪些配置
