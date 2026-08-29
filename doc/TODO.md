# TODO

- 待游戏内回归：**分级休眠中间档**（2026-08，无玩家空图降频 tick + 接缝快速区，`SeamlessTickThrottle`/`Patches_TickThrottle`）——回归清单见 `doc/地图滚动休眠.md` 第五节第 9 条：跑图时周围空图 `Throttle ON`、跨缝进图当 tick 恢复全速、100%/0% 档行为、接缝快速区对照（缝侧全速/core 侧慢速）、多图常驻 F5 性能对比。
- 待游戏内回归：**影子持有链完整性修复 + 统一 pawn 所在地追踪底座（2026-08-29，两名玩家报告"pawn 同时在两个地点"的根治件）**——①跨缝传送/组队/远行队进图后 ≤1 tick 影子名单即时正确（新入图殖民者即时可当外交官——统一底座事件触发）；②残余源（抱起倒地者/死亡）≤60 ticks 内被影子轮询清扫（日志 `Scrubbed N stale spawnedThings ...`，兼野外诊断器——报告玩家看到即坐实）；③据点图 Sleep→Wake 往返：殖民者需求速率正常（无双重 tick）；④离开 ≥deleteHops 令据点图删除：殖民者无鬼影/Discard、无 mapIndex 漂移（点殖民者栏头像相机跳对图）；⑤影子功能回归（据点对话/贸易/外交官查找、跨缝传送）；⑥原 RequestSweepSoon 时序无回归（Dev Dormancy Report 各事件后下一 tick 生效）。机制见 `AGENTS.md` 影子远行队第六件 + `doc/地图滚动休眠.md` 2.14。
