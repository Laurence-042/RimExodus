[h1]RimExodus — 无缝世界 / RimExodus — Seamless World[/h1]

告别"撤离地图 → 世界地图 → 重新加载"的割裂旅行。RimExodus 让相邻世界地块的局部地图在视觉与操作上连续连接：你的殖民者可以从基地门口直接走进荒野，穿过一条看不见的世界地块边界，继续走向下一片荒野——不需要远行队，不需要加载画面。
Say goodbye to the disjointed "exit map as caravan → world map → reload" travel loop. RimExodus stitches the local maps of adjacent world tiles together, visually and functionally: your colonists can walk straight out of the base gate into the wilderness, cross an invisible world-tile border, and keep going into the next stretch of wild — no caravan, no loading screen.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/demo.gif[/img]

[color=#7ee787][b]⚠ 本 mod 仍在开发过程中：它对地图机制有较大改动，可能会在奇奇怪怪的地方出问题，请做好遇到 bug 的心理准备。不过存档格式已基本固定、不会有大的改动，所以可以放心游玩——出了问题欢迎反馈。由于改动较深，暂不支持加入后从存档移除，后续如有反馈可能会补充卸载功能，供玩家在移除本 mod 前主动调整地图为兼容模式。[/b][/color]
[color=#7ee787][b]⚠ This mod is still in development: it makes deep changes to the map system, so odd things may break in odd places — be mentally prepared for bugs. That said, the save format is essentially settled and won't see major changes, so you can play without worry (just bring a bug-reporting mindset). Due to the depth of the changes, the mod currently cannot be removed from an existing save; an unload/compatibility utility may be added later based on feedback, letting players convert their maps back to vanilla before removing the mod.[/b][/color]

[h1]它做了什么 / What It Does[/h1]

每张地块地图按世界地块形状裁切成六边形（五边形地块同样支持），相邻地块通过"接缝带"对齐：地形、岩石、屋顶跨缝混合，河流与道路跨缝对接。走到接缝处，你会看到邻居地块的实时地形已经渲染在边界之外，而你的殖民者可以直接跨过边界走到邻居地块——简单来说，Rimworld大世界模式，启动！
Every tile map is carved into a hexagon matching its world tile shape (pentagon tiles are supported too), and adjacent tiles are aligned through a "seam band": terrain, rock, and roofs blend across the seam, and rivers and roads connect through. When you approach a seam, you'll see the neighbor tile's live terrain already rendered beyond your border — and your colonists can simply walk across into it. Simply put: it's open-world RimWorldin' time!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/map-grid.png[/img]
地图网格化裁切与接缝对齐 / Hexagonal tile carving and seam alignment

[list]
[*][b]直接跨图行走[/b]：命令殖民者跨过两图接缝几乎无感切换到相邻地图。
[*][b]Seamless cross-map walking[/b]: order a colonist across a map seam and they switch to the adjacent map with barely a hitch.

[*][b]跨图战斗[/b]：跨缝视线与索敌、射击与弹道交接——子弹飞过接缝，在正确的地图上命中正确的目标；你可以跨地图追击逃窜的敌人，但敌人也会跨地图追击你。可以在设置中整体关闭回到原版战斗语义。
[*][b]Cross-map combat[/b]: line of sight and target acquisition work across seams, and shots are handed off between maps — bullets fly over the seam and hit the right target on the right map. You can chase fleeing enemies across maps, but they can chase you back, too. Can be disabled entirely in the settings to restore vanilla combat semantics.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/shoot.png[/img]
跨缝射击 / Shooting across a seam

[*][b]分帧增量生成[/b]：邻居地块地图在殖民者被命令靠近边界时开始生成，且加载大部分时候不会暂停游戏、也不会显示无加载画面（生成地图的过程中无法存档，但也就几秒的事）。
[*][b]Incremental frame-sliced generation[/b]: neighbor tile maps start generating when a colonist is ordered toward the border, and the load usually neither pauses the game nor shows a loading screen (saving is blocked while a map is generating, but that only takes a few seconds).

[*][b]地图滚动休眠[/b]：远离玩家的地图自动休眠（停止 tick，内容完好保留、可随时唤醒折返，遗留物品与地形修改都在），更远的地图会被删除、再访时重新生成（避免内存占用过高）。注意：被删除的地图重新生成时是全新生成，此前遗留在该地图上的物品、建造的建筑与做出的地形修改都会丢失。家园地图永不自动休眠、删除。休眠/删除距离均可在设置中调整。
[*][b]Rolling map dormancy[/b]: maps far from the player automatically go dormant (no ticking; contents fully preserved and can be reawakened at any time — dropped items and terrain edits are all still there), and maps even farther away are deleted and regenerated on revisit (keeping memory usage in check). Note: regeneration creates a fresh map — items left behind, buildings constructed, and terrain changes made on a deleted map are lost. The home map is never auto-dormified or deleted. Both dormancy and deletion distances are adjustable in the settings.

[*][b]原生内容接入[/b]：世界地图上的 POI——其他派系据点、远古机械师建筑群、埋伏、机会地点——走近时经原版地图生成管线生成为无缝地块，与其他 mod 的自定义结构天然兼容；"设立营地"生成的也是无缝地块。
[*][b]Native content integration[/b]: world-map POIs — faction settlements, ancient mechanitor compounds, ambushes, opportunity sites — generate as seamless tiles through the vanilla map generation pipeline as you approach, staying naturally compatible with other mods' custom structures. "Set up camp" maps are seamless tiles too.

[*][b]据点贸易商[/b]：友好据点会从驻军中指定一位"贸易商"（头顶问号标记），右键即可对话交易——买用据点库存、卖随身物品、白银随身支付，完整的原版远行队交易语义，但不需要真的组远行队。据点对话面板同时收录该据点的全部交互选项，包括其他 mod 添加的据点特殊交互（比如金鸢尾兰的外交选项）。
[*][b]Settlement traders[/b]: friendly settlements designate a "trader" from their garrison (marked with a question mark overhead); right-click them to talk and trade — buy from the settlement's stock, sell what you carry, pay with the silver on your body. Full vanilla caravan trading semantics, minus the caravan. The settlement dialog also gathers every interaction that settlement offers, including ones added by other mods (e.g. Oberonia Aurea's diplomacy options).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/trade.png[/img]
与据点贸易商交易 / Trading with a settlement trader

[*][b]世界地图三态图标[/b]：地块图标实时显示活跃与休眠状态，选中地块可手动休眠或手动删除。
[*][b]Three-state world map icons[/b]: tile icons show active and dormant states in real time; select a tile to manually dormify or delete it.
[/list]

[h1]Mod 兼容性 / Mod Compatibility[/h1]

[b]已适配：[/b]
[b]Adapted:[/b]
[list]
[*][b]Geological Landforms[/b] — 完整适配（landform 在地块图上正常生成，含预览）。
[*][b]Geological Landforms[/b] — fully adapted (landforms generate correctly on tile maps, previews included).

[*][b]MapPreview[/b] — 预览可见符合实际的地形生成。
[*][b]MapPreview[/b] — previews show terrain generation faithful to the real map.

[*][b]Vehicle Framework[/b] — 载具可跨缝：驶近传送点即跨越、可跨图下令，多格大型载具做了整车落点校验（对端确实放不下时会如实拒绝）。载具在滚动休眠中视同 pawn。
[*][b]Vehicle Framework[/b] — vehicles cross seams: drive up to a transfer spot to cross, and cross-map orders work. Multi-tile large vehicles get full-vehicle arrival validation (and an honest refusal if the far side genuinely has no room). Vehicles count as pawns for rolling dormancy.

[*][b]Vehicle Map Framework[/b] — 载具地图上带着殖民者时，载具可以正常跨图
[*][b]Vehicle Map Framework[/b] — a vehicle carrying colonists on its map can cross between maps normally.

[*][b]Perspective Shift[/b] — 第一人称 WASD 移动可正常跨图，跨图射击也没问题。
[*][b]Perspective Shift[/b] — first-person WASD movement crosses maps fine, and cross-map shooting works too.
[/list]

[b]预期不兼容：[/b]
[b]Expected to be incompatible:[/b]
[list]
[*][b]边缘战争空岛[/b] — 其空岛改造流程会破坏本mod的地图设置，导致空岛地图无法无缝。而且未深入调研空岛移动逻辑，可能造成额外问题。考虑到玩法调性冲突，不计划支持。
[*][b]RimSkyBlock[/b] — their sky-island conversion breaks this mod's map setup, leaving sky-island maps non-seamless. Their movement logic is also unresearched and may cause further issues. Given the gameplay tone clash, support is not planned.

[*][b]深度修改地图生成管线/地图边界/世界地块的其他 mod [/b] — 未系统测试，若遇冲突，可关闭设置中的"分帧生成开关"让普通地块图改走原版同步生成管线（保证其他 mod 在原版同步生成管线上的 patch 生效，代价是生成时短暂冻结）。
[*][b]Other mods that deeply modify the map generation pipeline, map borders, or world tiles[/b] — not systematically tested. If you hit conflicts, turn off "incremental generation" in the settings so regular tile maps use the vanilla synchronous generation pipeline instead (guaranteeing other mods' patches on the vanilla pipeline apply, at the cost of a brief freeze during generation).
[/list]

[h1]已知限制 / Known Limitations[/h1]

[list]
[*]如果天气被某种原因强制修改，而在修改源本身未被处理（如游戏条件未结束）的情况下，修改源所在的地图就被删除，天气将无法恢复正常——不是本 mod 不去恢复，而是恢复的途径随修改源一起被删除、被封死了。
[*]If the weather is force-modified by something and the source map gets deleted before the source itself is dealt with (e.g. its game condition hasn't ended), the weather can never return to normal — not because this mod won't restore it, but because the restoration path was deleted along with the source, sealing it off.

[*]挖矿/砍伐等"designation 前置型"命令不出现在跨图右键菜单（走近过缝后一切正常）。
[*]Commands that require a designation first (mining, chopping, etc.) don't appear in cross-map right-click menus (everything works once you've walked over the seam).

[*]Odyssey 空间层地图与各类口袋图（Pocket Map，比如地下仓库、巨坑、 VMF 的载具内部图之类的）不参与表面无缝语义，保持原版行为。
[*]Odyssey space-layer maps and pocket maps (underground vaults, pits, VMF vehicle interiors, etc.) stay outside the surface seamless system and keep vanilla behavior.

[*]爆炸 AoE 与迫击炮类越顶投射物不跨缝（弹着点地图原生结算）。
[*]Explosion AoE and mortar-style overhead projectiles don't cross seams (they resolve natively on the map they land on).

[*]据点交易仅支持随身物品（随行囚犯/奴隶出售暂不支持）；贸易商为生成时一次性指定，不补选。
[*]Settlement trading only supports items on your body (selling accompanying prisoners/slaves isn't supported yet); the trader is designated once at generation and is not re-picked.

[*]分帧生成邻接地图的过程中（进度显示在屏幕左上角）无法存档。
[*]Saving is blocked while an adjacent map generates via frame-sliced generation (progress shows in the top-left corner).

[*]邻居地图渲染为近似实现（无阴影/水波等细节），跨天气域的邻居天色跟随当前图。
[*]Neighbor map rendering is approximate (no shadows/water ripples and such), and neighbors in a different weather cluster follow the current map's sky.

[*]星球上的12个五边形地块周围一圈的地图连接视觉上会有点问题，但不影响跨图移动。
[*]Map connections around the planet's 12 pentagon tiles look slightly off visually, but cross-map movement is unaffected.
[/list]

[h1]性能 / Performance[/h1]

热路径全部按"无跨图交互即早退"设计，不做跨图操作时近乎零开销；主要可感成本是邻居地图渲染与邻图生成期间的 TPS 下降（默认在游戏运行过程中逐步生成地图，可以通过设置项切换为原版的一次性生成整个地图的模式）。
All hot paths are designed to early-out when no cross-map interaction is happening — near-zero overhead when you're not crossing maps. The main noticeable costs are neighbor map rendering and a TPS dip while adjacent maps generate (by default maps generate gradually during play; a settings option switches this to the vanilla generate-everything-at-once mode).

Rimworld里地图过多本身就会产生性能问题，本Mod运行时不产生额外的常驻消耗，还通过远端地图休眠时不再 tick、更远的地图自动删除等方式一定程度上消减了这个问题，但终究没法凭空变出tps，所以不建议保留太多地图。
Having too many maps is itself a performance problem in RimWorld. This mod adds no extra standing cost at runtime, and actually mitigates the problem somewhat — dormant far maps stop ticking, and even farther maps are auto-deleted — but it can't conjure TPS out of thin air, so keeping too many maps around isn't recommended.

每项开销的完整说明见 GitHub README。
See the GitHub README for a full breakdown of every cost.

[h1]设置 / Settings[/h1]

全部条目带 tooltip，鼠标在条目上悬停就可以看到说明，此处不赘述。
Every entry has a tooltip — just hover over an entry to see its explanation, so they aren't repeated here.

[h1]许可与鸣谢 / License & Credits[/h1]

本项目以 MIT 许可证开源，源码见 GitHub。
This project is open-sourced under the MIT License; see the GitHub repo for source code.

特别鸣谢 [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3426502333]Vehicle Map Framework[/url]——正是它作为先驱者证明了跨地图 mod 是可行的，本项目的最初调研也从它开始。虽然深入调研后发现架构假设差异较大（VMF 以口袋地图与载具跨图为中心，而 RimExodus 需要的是对等地块连续世界），最终未能复用其代码、从头另起炉灶，但没有它就没有 RimExodus。
Special thanks to [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3426502333]Vehicle Map Framework[/url] — as a pioneer it proved cross-map modding was possible, and it was where this project's initial research began. Deeper investigation later revealed the architectural assumptions diverge too far (VMF is built around pocket maps and vehicle crossings, while RimExodus needed peer tiles forming one continuous world), so none of its code was reused and RimExodus was built from scratch — but without it, RimExodus wouldn't exist.

[h1]链接与反馈 / Links & Feedback[/h1]

源码与完整文档：[url=https://github.com/Laurence-042/RimExodus]GitHub[/url]
Source code and full documentation: [url=https://github.com/Laurence-042/RimExodus]GitHub[/url]

发现问题欢迎在评论区或 GitHub Issues 反馈，附上日志与复现步骤会非常有帮助。
Found a problem? Please report it in the comments or on GitHub Issues — attaching your log and reproduction steps helps a lot.
