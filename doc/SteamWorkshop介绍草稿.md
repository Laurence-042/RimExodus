# Steam Workshop 介绍草稿

> 用途：创意工坊页面描述文案（Steam 支持 BBCode，下方正文即按 BBCode 写）。中文为主版本 + 英文版本。事实依据 = `README.md`（对外单一事实源）；发布前请按需增删截图/视频占位与最终打磨措辞。

---

## 中文版（BBCode）

```bbcode
[h1]RimExodus — 无缝世界地块探索[/h1]

告别"撤离地图 → 世界地图 → 重新加载"的割裂旅行。RimExodus 让相邻世界地块的局部地图在视觉与操作上连续连接：你的殖民者可以从基地门口直接走进荒野，穿过一条看不见的世界地块边界，继续走向下一片荒野——不需要远行队，不需要加载画面。

[color=#7ee787][b]⚠ 请在新存档使用本 mod（不做旧档兼容）。需要 RimWorld 1.6 + Harmony。[/b][/color]

[h1]它做了什么[/h1]

每张地块地图按世界地块形状裁切成六边形（五边形地块同样支持），相邻地块通过"接缝带"对齐：地形、岩石、屋顶跨缝混合，河流与道路跨缝对接。走到接缝处，你会看到邻居地块的实时地形已经渲染在边界之外——那边的人、动物和建筑都是真实存在的游戏实体，不是背景画。

[list]
[*][b]直接跨图行走[/b]：走近接缝即无感切换到相邻地图，继续原来的移动方向。玩家下令（移动/攻击/开采/搬运/驯服等）也可以直接指向邻图区域。
[*][b]跨图战斗[/b]：跨缝视线与索敌、射击与弹道交接——子弹飞过接缝，在正确的地图上命中正确的目标；敌人也会跨缝追击你。可以在设置中整体关闭回到原版战斗语义。
[*][b]分帧增量生成[/b]：邻居地块在你靠近边界时开始生成，主线程分帧推进（左上角有进度提示），不暂停游戏、无加载画面。生成进行中无法存档（会收到一封（威胁级）提醒信），几秒后生成完成即可正常存档。
[*][b]地图滚动休眠[/b]：远离玩家的地图自动休眠（停止 tick，内容完好保留、可随时唤醒折返，遗留物品与地形修改都在），更远的地图按距离策略删除、再访时重新生成。家园地图永不休眠、永不删除。休眠/删除距离均可在设置中调整。
[*][b]原生内容接入[/b]：世界地图上的 POI——其他派系据点、远古机械师建筑群、埋伏、机会地点——走近时经原版地图生成管线生成为无缝地块，与其他 mod 的自定义结构天然兼容；"设立营地"生成的也是无缝地块。
[*][b]据点贸易商[/b]：友好据点会从驻军中指定一位"贸易商"（头顶问号标记），右键即可对话交易——买用据点库存、卖随身物品、白银随身支付，完整的原版远行队交易语义，但不需要真的组远行队。据点对话面板同时收录该据点的全部交互选项（mod 添加的据点特殊交互也会出现）。
[*][b]世界地图三态图标[/b]：地块图标实时显示状态——实心橙 = 活跃有人 / 实心蓝 = 活跃无人 / 灰色空心 = 休眠（色盲友好 + 形状冗余区分）。选中地块可手动休眠（不随距离自动唤醒，随存档保留）或手动删除。
[/list]

[h1]Mod 兼容性[/h1]

[b]已适配：[/b]
[list]
[*][b]Geological Landforms[/b] — 完整适配（landform 在地块图上正常生成，含预览）。
[*][b]MapPreview[/b] — 预览可见六边形与接缝效果，互不干扰。
[*][b]Vehicle Framework[/b] — 载具可跨缝：驶近传送点即跨越、可跨图下令，多格大型载具做了整车落点校验（对端确实放不下时会如实拒绝）。载具在滚动休眠中视同 pawn。
[*][b]Perspective Shift[/b] — 第一人称 WASD 移动与驾驶均可无缝跨缝，视角自动跟随切换。
[/list]

[b]共存（回到原生行为）：[/b] Odyssey 空间层地图与各类口袋图（Pocket Map，含载具内部图）不参与表面无缝语义，保持原版行为。

[b]预期不兼容：[/b] 空岛（skyblock）类 mod（玩法调性冲突，不计划支持）。深度修改地图生成管线/地图边界/世界地块的其他 mod 未系统测试——若遇冲突，可关闭设置中的"Gradual map generation"让普通地块图改走原版同步生成管线（其他 mod 的生成 patch 原生生效，代价是生成时短暂冻结）。

[h1]已知限制[/h1]

[list]
[*]爆炸 AoE 与迫击炮类越顶投射物不跨缝（弹着点地图原生结算）。
[*]跨图寻路为单跳——不支持一次下令穿越多张地图，多段行程逐图下令即可。
[*]挖矿/砍伐等"designation 前置型"命令不出现在跨图右键菜单（走近过缝后一切正常）。
[*]据点交易仅支持随身物品（随行囚犯/奴隶出售暂不支持）；贸易商为生成时一次性指定，不补选。
[*]跨图下令穿越未生成的地块时需等待分帧生成完成（进度显示在屏幕左上角）。
[*]邻居地图渲染为近似实现（无阴影/水波等细节），跨天气域的邻居天色跟随当前图。
[/list]

[h1]性能[/h1]

热路径全部按"无跨图交互即早退"设计：不做跨图操作时近乎零开销；远端地图休眠实际上是净省（不再 tick）。主要可感成本是邻居地图渲染与邻图生成期间的 TPS 下降（生成分帧推进、总量不变，平滑优先）。每项开销的完整说明见 GitHub README。

[h1]设置[/h1]

设置窗口按功能分四个 tab（地图生成 / 滚动休眠 / 跨图战斗 / 高级诊断），全部条目带 tooltip：预加载距离、禁建带宽度、分帧生成开关与批次大小、休眠/删除距离（最低 1/1 = 离开即休眠并销毁）、休眠扫描间隔、跨图战斗总开关等。另有地图右下角全局控制条的沉浸模式开关（隐藏撤离带与接缝划线，为录视频/截图设计）。

[h1]链接与反馈[/h1]

源码与完整文档：[url=GitHub仓库地址]GitHub[/url]
发现问题欢迎在评论区或 GitHub Issues 反馈，附上日志与复现步骤会非常有帮助。
```

---

## English Version (BBCode)

```bbcode
[h1]RimExodus — Seamless World Tiles[/h1]

No more "reach map edge → world map → reload". RimExodus stitches neighboring world tiles' local maps together, visually and mechanically: your colonists can walk straight out of the base gate into the wilderness, cross an invisible world-tile border, and keep going — no caravan, no loading screen.

[color=#7ee787][b]⚠ Use on a NEW save (no old-save migration). Requires RimWorld 1.6 + Harmony.[/b][/color]

[h1]What it does[/h1]

Each tile map is clipped to its world-tile shape (pentagon and hexagon tiles supported), and adjacent tiles are aligned through a "seam band": terrain, rock and roof blend across the seam; rivers and roads line up. As you approach a seam, the neighboring tile's live terrain is already rendered beyond your border — the pawns, animals and buildings over there are real entities, not a backdrop.

[list]
[*][b]Walk across tiles[/b] — step into the seam and you seamlessly continue into the neighboring map. Player orders (move/attack/mine/haul/tame...) can target the neighboring tile directly.
[*][b]Cross-map combat[/b] — line of sight, target acquisition and projectile handoff across the seam: bullets fly over the border and resolve on the correct map; enemies will pursue you across seams. Can be disabled in settings to restore vanilla combat.
[*][b]Gradual map generation[/b] — neighboring tiles generate frame-by-frame on the main thread as you approach (progress shown top-left), no pause, no loading screen. Saving is blocked while generation is in progress (you'll get a (threat-level!) reminder letter); a few seconds later it finishes and saving works again.
[*][b]Rolling map dormancy[/b] — maps far from players fall dormant (no ticking; contents fully preserved — items and terrain edits are all there when you return), and even farther maps are deleted and regenerated on revisit by distance policy. Player home maps never sleep and never get deleted. Distances are configurable.
[*][b]Native content integration[/b] — world-map POIs (faction settlements, ancient complexes, ambush sites, etc.) generate as seamless tiles through the vanilla pipeline when approached, so other mods' custom structures stay compatible; player camps are seamless tiles too.
[*][b]Settlement traders[/b] — friendly settlements appoint a "trader" from their garrison (question-mark marker); right-click to talk and trade — buy from settlement stock, sell from your pawns' inventories, silver paid from pockets. Full vanilla caravan-trade semantics without the caravan. The dialog also collects the settlement's other interactions (mod-added ones included).
[*][b]World map status icons[/b] — solid orange = active & occupied / solid blue = active & empty / hollow grey = dormant (colorblind-friendly with shape redundancy). Select a tile to manually dorm it (won't auto-wake by distance; persists in save) or delete it.
[/list]

[h1]Mod Compatibility[/h1]

[b]Adapted:[/b]
[list]
[*][b]Geological Landforms[/b] — fully adapted (landforms generate on tile maps, previews included).
[*][b]MapPreview[/b] — previews show the hexagon clipping and seams; neither breaks the other.
[*][b]Vehicle Framework[/b] — vehicles cross seams: drive into a transfer point to cross, cross-map orders work, and multi-cell vehicles get full hull-rect arrival checks (honestly refused if there's genuinely no room on the other side). Vehicles count as pawns for dormancy.
[*][b]Perspective Shift[/b] — first-person WASD movement and driving cross seams seamlessly, camera follows automatically.
[/list]

[b]Coexists (vanilla behavior):[/b] Odyssey space maps and pocket maps (including vehicle interiors) stay outside the surface seamless system.

[b]Expected incompatible:[/b] skyblock-style mods (design conflict, not planned). Mods deeply modifying map generation/borders/world tiles are untested — if you hit a conflict, disable "Gradual map generation" in settings and regular tile maps will use the vanilla synchronous pipeline (other mods' generation patches then apply natively, at the cost of a brief freeze).

[h1]Known Limitations[/h1]

[list]
[*]Explosion AoE and mortar-style shells don't cross seams (resolve on the impact map).
[*]Cross-map pathing is single-hop; issue orders one tile at a time.
[*]Designation-first commands (mine/chop/harvest...) don't appear in cross-map right-click menus (everything works once you walk over).
[*]Settlement trading only covers carried items (prisoner/slave selling not yet supported); the trader is appointed once at generation.
[*]Cross-map orders into an ungenerated tile wait for gradual generation (progress top-left).
[*]Neighbor-map rendering is approximate (no shadows/water detail); neighbor sky tint across weather clusters follows the current map.
[/list]

[h1]Performance[/h1]

All hot paths early-out when no cross-map interaction happens — near-zero overhead in normal play; dormant maps are a net saving (they stop ticking). The main noticeable costs are neighbor-map rendering and a TPS dip while a neighboring tile generates (frame-spread, same total work, smoothness first). Full breakdown in the GitHub README.

[h1]Settings[/h1]

Four tabs (Map Generation / Rolling Dormancy / Cross-map Combat / Advanced & Diagnostics), every entry with a tooltip: preload distance, no-build band width, gradual generation toggle & batch size, dormancy/delete distances (down to 1/1 = sleep & delete on leaving), scan interval, cross-map combat toggle, and an immersion toggle on the in-map PlaySettings bar (hides the evacuation band & seam lines — for screenshot/video purposes).

[h1]Links & Feedback[/h1]

Source & full docs: [url=GitHub repo URL]GitHub[/url]
Bug reports with logs and repro steps are much appreciated — here or on GitHub Issues.
```

---

## 发布前检查清单（不进页面正文）

- [ ] 替换两处 GitHub 链接占位（中文版"链接与反馈"、英文版"Links & Feedback"）。
- [ ] 补充截图/视频：建议至少一张"站在地图 A 看见邻居地块 B 实时地形"的全景图、一张跨缝战斗截图、一张据点贸易商对话面板图、（可选）世界地图三态图标特写。
- [ ] 标签建议：`1.6`、`Harmony`、`Gameplay`、`Utility(?)`；Visibility 公开前先朋友可见测试 BBCode 渲染。
- [ ] 页面描述与 README 的口径已对齐（本草稿全部事实取自 README 2026-08-25 版）；后续 README 兼容性/限制变更须同步页面。
- [ ] 首条评论/置顶建议附"新存档使用 + Harmony 前置"提示。
