# TODO / 回归清单

## 影子远行队 v2（2026-08-26，常驻实例模型，待游戏内回归）

- [ ] 据点图常驻影子：进入有玩家殖民者的据点图后，日志出现 `ShadowCaravan: created at tile ...`；离开（无玩家 pawn）后出现 `dismantled ... (condition lost)`。
- [ ] 金鸢尾兰"外交交互"→"深入交流"：不再弹"无外交官"，好感/助攻点/物品正常到账（物品进图上殖民者随身库存）。
- [ ] 贸易商对话"贸易"照常工作（GetCaravan patch 不影响地图内交易 patch 链）。
- [ ] 存档/读档：存档无影子相关 "referenced but not deep-saved" 大量警告；读档后首 60 ticks 内影子自动重建（日志 created 行）。
- [ ] 世界图：影子的 GetGizmos 已清空（选中影子无"移动远行队"等指令）；AllIncidentTargets 过滤生效（无事件打影子）。
- [ ] 图上殖民者行为无回归：心情/娱乐/殖民者栏正常（GetCaravan 对影子返回 null，地图语义还原）。
- [ ] 启动日志绑定报告：新增 Caravan.TickInterval / CaravanUtility.GetCaravan / Caravan.GetGizmos 三 patch 确认活跃。
- [ ] 观察项：远行队类 Alert（如闲置/缺粮）是否对影子误报；金鸢尾兰"外交争锋"（CreateFixedCaravan）行为待观察（用户定夺不拦截）。
