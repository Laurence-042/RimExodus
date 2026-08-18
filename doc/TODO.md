优化geStep，别让动物生成在void上……按理说void生成应该在动物生成前的啊

优化寻路，当前跨地图寻路似乎完全不是AStar该有的结果（总是经过最近传送点的路径，而不是跨地图移动距离最短的路径）

优化地图加载提示，或者优化加载

SeamOverride似乎遗漏了 压埋的钢铁 之类的非岩石building，需要对应'snapshot层

邻接地图的光照遮罩似乎缺失，导致超出当前地图边界后的邻接地图看起来亮度完全不同（已修 2026-08：①收集 SectionLayer_LightingOverlay + SkyManager 宿主跟随 CurrentMap；②天色/glow 分层——邻图 overlay 换 (1,1,1,0) 克隆材质（只出 glow/roof）+ 非重叠 L 形区全零 quad 补天色，消除 void 透明圈 sky² 双染暗带。待游戏内验证：夜晚邻图变暗且跨缝无暗带/岩顶黑暗/灯光 glow/聚焦非宿主图后昼夜 tint 持续变化/邻图核心深处昼夜染色正常）