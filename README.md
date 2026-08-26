# RimExodus — 无缝世界地块探索

一个 RimWorld 1.6 mod：使相邻世界地块的局部地图在视觉与操作上连续连接，Pawn 可以直接从一张地图走入相邻地图，无需组建远行队。

核心原则是**所有地图一视同仁**——任务地点、遭遇、派系基地、定居点、营地等一切拥有合法 `MapParent` 的地图都走同一条无缝链。RimExodus 地图本质上就是标准任务地图生成 + 我们的裁切步骤（void + 接缝带 + 传送点），将其包装成可连续通行的地块。

## 核心特性

- **无缝地块互连**：每张地块地图按六边形裁切，相邻地块通过接缝带对齐（地形/岩体/屋顶跨缝混合，河流与道路跨缝对齐）；边界外是 void，邻居地图的实时地形渲染作为背景。
- **直接跨图行走**：接缝处铺传送点，Pawn 走到接缝即无感切换到相邻地图；玩家下令（移动/攻击/开采/搬运等）可直接指向邻图区域。
- **跨图战斗**：跨缝视线、目标搜索、射击与弹道交接（子弹飞过接缝在正确的地图上结算）；敌对 NPC 可跨缝追击。
- **地图滚动软休眠**：远离玩家的地图自动休眠（不 tick、内容完好保留），更远的按距离策略删除重建；玩家家园不休眠不删除。
- **原生内容接入**：世界地图上的 POI（其他派系据点、远古机械师建筑群、机会地点等 Site 类目标）走近时经原版地图生成管线自动生成为无缝地块，mod 自定义结构原生兼容；据点可与驻军中的"贸易商"对话交易与送礼，对话面板包含原版远行队站在据点上的全部交互（含 mod 添加的）；"设立营地"生成的也是无缝地块。
- **分帧增量生成**：预加载的邻接地图在主线程分帧生成（每帧约 8ms 预算），不暂停游戏、无加载画面。

## Mod 兼容性

### 已适配（游戏内验证）

| Mod | 状态 |
|-----|------|
| **Geological Landforms**（实测 1.7.13.1） | 完整适配，两轮：①分帧增量生成路径软反射复刻其生成上下文（其 Harmony patch 挂在原生同步管线上，分帧路径天然绕过）；②启动时把我们的地块 parent 注册进其 `IgnoredWorldObjects` 白名单——否则 GL 会把无派系的地块 parent 当作外来 site，把该地块的全部 landform 概率归零（症状：营地图 landform 缺失且被永久写入空数据、地块存在期间预览丢 landform）。 |
| **MapPreview** | 预览兼容：RimExodus 裁切/接缝 genStep 进预览白名单（预览可见六边形与接缝效果），预览线程守卫防组件裁剪 NRE；增量分帧生成与后台预览互相避让（预览进行中预加载自动排队，代价 1-2 tick）。 |
| **Perspective Shift**（ferny.PerspectiveShift，第一人称自由移动） | 适配：其 WASD 移动绕过原版 job/寻路系统，RimExodus 在其移动处理上补挂了"走近接缝自动预加载邻图 + 踩传送点无缝过缝"；其右键下令走原版菜单链，天然兼容。第一人称下鼠标瞄准解析看起来已经随常规模式的适配生效而适配了。 |
| **Vehicle Framework**（SmashPhil.VehicleFramework，载具） | 适配：跨图传送复刻 VF 官方进图管线——传送前同步就绪化对图载具网格 + 整车矩形落点校验（找不到可站地块时如实拒绝，属真实地形限制）；载具征召驶近传送点即跨缝、点击邻图可跨图下令、菜单可达性与执行同口径。载具（含乘员）在地图滚动休眠中视同 pawn。与 Perspective Shift 双装支持第一人称 WASD 驾驶跨缝。已知边界：NPC 敌对载具不跨图追击；跨图不保留朝向；对端接缝若整圈无该载具可站地块则无法从该边跨越。 |

### 共存（无运行时依赖）

- **Vehicle Map Framework**：仅作架构调研参考，无依赖、无兼容承诺（但实际上兼容性还行）。**Vehicle Framework 已升级为已适配**（见上表）。
- **Odyssey 空间层地图与其他口袋图**（Pocket Map，含 VMF 载具内部图）：被排除出表面无缝语义——这类地图回到原生行为（轨道 tileId 与表面地块撞号、口袋图硬编码 tile 会污染邻居解析，接入反而出错）。

### 预期不兼容 / 未深入测试

- **空岛（skyblock）类 mod**：空岛生成可能清掉传送点与 void 裁切；且无缝远行与空岛玩法本身冲突，不计划支持。
- **异象天气链**：未细究（例如未激活夜光柱就离开，异常黑暗天气可能无法解除）。
- **外太空地图**的工作方式未细究。
- 其他深度修改**地图生成管线 / 地图边界 / 世界地块**的 mod 未系统测试。

## 已知限制

### 跨图战斗
- 爆炸 AoE 不跨缝（弹着点在落点地图原生结算）；迫击炮类越顶投射物不跨缝。
    - 设计如此：我不想patch太多导致兼容性下降
- 跨图寻路/选点为**单跳**（不支持一次下令穿越多张地图；多段行程需逐图下令）。
    - 设计如此：rimworld的地图运行开销不算小，我不建议玩家同时开太多地图。而且要实现视野内同时显示多格地图不仅会带来额外的常驻渲染开销，还需要改很多摄像机相关内容，可能和SimpleCameraSetting之类的产生冲突

### 跨图命令
- **designation 前置型命令**（挖矿/砍伐/收割/拆除/开关等需要先在本图下标记的）在跨图右键菜单中不出现——走近过缝后一切恢复正常。此为暂不适配的已知边界，后续方向见 `doc/跨图命令适配审计.md`。
    - 实现限制：这种前置命令我没想到很合理的以统一patch解决的方案，而对每个进行patch……roi太低

### 据点贸易
- 对话面板的选项 = 原版"远行队站在友方据点上"可用的全部交互，同时聚合远行队 gizmo（`GetCaravanGizmos`）与远行队右键菜单（`GetFloatMenuOptions`，金鸢尾兰等 mod 的挂载点）两条管线——交易/送礼/满足贸易请求/mod 交互；攻击与进入地图被过滤（前者按设计排除，后者因玩家已在图上）。据点地图上的玩家殖民者即被视为远行队（影子远行队机制：常驻投影实例，mod 的即时/延迟交互均可用；不进存档、零 tick 副作用、不影响 pawn 需求与地图行为）；同 tile 有真实远行队停留也不冲突（原生支持多远行队同 tile）。
- 仅支持**随身物品**交易；随行囚犯/奴隶出售暂不支持。
  - 实现限制：这个得patch不少远行队判定，而且说实话我觉得这玩法不太适合搞奴隶贸易，毕竟频繁跨图，人越多管理越麻烦，而囚犯不能牵着走，奴隶工作效率又不如殖民者，所以我觉得没啥玩家会有带着奴隶周游世界的需求
- 贸易商仅在据点生成时一次性指定；其死亡/倒地/被俘后不补选（据点地图删除重生成时才重新指定）。
  - 设计如此：你总得给他们点时间去重新跳大神选举新的贸易商吧

### 地图形状

- 六边形地块有时候看起来有那么一格偏差
  - 实现限制：把球面上的六边形投影到二维平面必然伴随着扭曲，而为了尽量控制扭曲，我选择原样投影+偏移拼接，也就是说两个相邻六边形其实是有微妙角度差别的（从赤道走到极点会发现六边形在逐渐旋转，到极点时累计会旋转30度）
- 五边形地块附近的六边形地块拼接看起来很奇怪
  - 实现限制：如前一条所述，这个投影是靠偏移拼接的。通常来说从赤道到极点的路上相邻地图的边偏移程度不到1度，所以看不出来啥。但是那12个五边形地块的偏移可太大了，为常规偏移设计的宽度为3的接缝带兜不住这种巨大偏移，所以在五边形地块上看周围的五个六边形地块似乎都不连续。但我们的渲染是以当前地块为主的，所以当走到周围的六边形地块上时，就会发现其和邻居的六边形和五边形地块都能连起来了（代价是五边形的部分看起来被遮住了一些）。但考虑到这并不影响正常游玩（整个世界就12块这样的地块），相较于将接缝带扩大到7+格导致可建造区域被进一步压缩，我觉得这是可以接受的

### 地图生命周期
- 地块图按距离策略删除后，下次进入走完整生成链重建（等同"从未出现过"，新地形种子确定性一致但生物群落细节会重新生成）。
  - 设计如此：我考虑过持久化地图到存档，但是说实话一张地图数据量还挺大的……考虑结果就是别给存档增肥了
- 原版"全员离开即删图"的地图族（据点/site/战场等）改为距离策略接管；真败亡的据点删图后世界对象保留（原版重访语义）。
  - 设计如此：你也不想通过设立营地生成个地图后，前脚走出去地图就没了吧
- 事件不会打到休眠地图；空着的玩家家保留原版"可被袭击"语义。
  - 设计如此：休眠地图就是不占用任何计算资源的
- 世界地图上选中地块可手动**休眠**（手动休眠不按距离自动唤醒，只有进图/命令 pawn 走近其接缝才唤醒，且随存档保留；删除距离策略照常）或**删除**（确认后销毁重建）；家园图不受手动休眠/删除影响。地块状态以**贴合地块真实形状的渐变填充**标记（中心较实、边缘渐透，不遮原版选中/聚焦描边）：**橙 = 活跃有人 / 蓝 = 活跃无人 / 灰 = 休眠**（蓝橙为色盲友好配色）。
  - 逃生通道：这个是考虑到有玩家可能意外生成某些邻接地图需要删除的场景
- 沉浸模式（全局控制条"Seam band & evacuation toggle"关闭）下无法经接缝带组建远行队（含组队界面），只有跨缝步行可用——属预期设计，非正常游玩用途。
  - 设计如此：你也不想拍着rimworld远行vlog突然无意间点到接缝导致变远行队吧

### 性能与杂项
- 分帧
  - **分帧生成进行中无法存档**（存档会被直接拒绝，并以一封威胁级信件告知；生成完成约几秒后可正常存档）：生成中途存下的档会包含一张无法读取的半成品地图。
  - 分帧增量生成不暂停游戏，但把一次生成的墙钟时间拉长到数百帧（CPU 总量不变，平滑优先），而且加载过程tps会剧烈下降。
    - 设计如此：我不太能接受陶德的加载狂魔星空，我也不能接受我的mod这样。但加载地图确实是个重计算量的活，tps下降是不可避免的
    - 兼容性限制：原版地图生成方法设计上就是同步的，我要做异步分帧只能自己复刻了一个地图生成管线。虽然它在表现上和原版管线没啥区别，但patch原版管线的方法不会影响这个分帧生成的管线。
    - 逃生通道：若与其他修改地图生成的 mod 冲突，或者单纯宁可单帧冻结也不想tps下降，可在设置中关闭"Gradual map generation"，然后普通地块图届时也会改走原版同步管线，其他 mod 对生成管线的 Harmony patch 原生生效（会短暂冻结游戏一帧）。
  - 首次走近其他派系据点、机械师遗迹等POI时的生成是原生同步单帧（短暂冻结，等同原版无加载屏版本）。
    - 兼容性限制：如前文所述，其他mod可能会patch一些生成逻辑来保证他们的POI能具备一些酷炫特性，所以我只能用原版管线单帧加载POI
- 邻居渲染
  - 跨天气域（目前是同一个连续biome是一个天气域）的邻居地图天色渲染跟随当前图
    - 实现限制：我没找到可用的天气遮罩的蒙版，我要实现跨天气域邻居地图天色渲染过渡的话就得完全重写相关天气遮罩——这显然问题很大
  - 邻居渲染是近似实现，而且缺乏阴影、水波之类的细节。
    - 兼容性限制：之前听其他玩家说rimworld的光照渲染比较坑，性能消耗比较大，所以永夜地图tps明显更低。所以出于性能考量，在渲染邻居的时候用了简化版本
  - 接缝带上的岩石
    - 种类会随着切图产生跳变
      - 兼容性限制：原版岩石生成就是这样，而我不想patch太多genStep，那样侵入性太大
    - 接缝带两侧岩石边界看着怪怪的，尤其是南侧是void的场景
      - 实现限制：为了保证接缝带外侧的岩石不显示石壁，我额外做了个可以放在void上的隐形岩石来保证贴图连续，但出乎我预料的是，岩石的连接方向的贴图也是有纹理的，而不是纯色块。原版岩石看着连续，实际上是下方的岩石贴图覆盖了上方岩石底部的纹理，而我的隐形岩石无法遮盖这个顶部纹理
      - ROI低：当然这个不是没法解决，比如调整原版贴图啊，让隐形岩石的贴图可以动态根据邻居的变啊……但属实过于麻烦了，而这个小瑕疵我觉得可以接受，暂时不想做
    - 接缝带一侧的岩石被开采完后对侧仍然显示连续的贴图
      - ROI低：我没做隐形岩石的开采侦测，我觉得这个是个边缘场景，为此搞更多的跨图thing comp关联容易出事
- 运行时性能开销
  - mod 的持续开销主要来自对原版高频方法（每帧渲染、每 tick、pawn 每次跨格移动、每次索敌）的 Harmony patch 和少量周期轮询。设计上这些热路径全部是"同图/无活跃邻居即几次引用比较就早退 + 缓存 + 事件驱动替代轮询"，不做跨图交互时近乎零开销。当前清单：
  - 每帧（渲染/UI 级）
    - 邻居地图渲染（地形/物体/光照/雾四层）：**最重的每帧开销**。有视区裁剪（不可见 section 直接跳过）、section 脏标记惰性重建（邻图不变不重算 mesh）、渲染命令缓冲仅在相机变更时重建、列表全程复用零分配；只有本图存在已加载邻居且邻居在视野内才有实际成本。
      - 设计如此：无缝地图就得显示邻接地图，这个不能完全省掉，我只能在渲染邻接地图时做可视区域裁剪和精简后处理效果来减少性能损耗
    - 接缝中心线勾勒与浅绿撤离带绘制：每帧少量绘制提交，接缝几何按地块缓存，沉浸模式开关可整体隐藏。
      - 设计如此：渲染就是这么搞的
    - 天气域幂等守卫：每图每帧/每 tick 一次弱表查询加帧号比较，量级极小；副作用是共享天气实例同帧只推进一次，实际上是净省。
      - 设计如此：这个最初是我觉得隔一个地块一个天气不合适，所以做了天气共享。因此只要没跨群系，那么多个地图就只需要处理一次天气，实际上是优化性能
    - 鼠标拾取：点击命中本图范围即放行原版零开销；悬停/点击到邻图渲染区域时才有一次归属解析和少量对象分配。
      - 实现限制：不然没法很好处理跨图命令，这个也没啥消耗
  - 每 tick / pawn 步进
    - 休眠地图整体跳过 tick 与更新：滚动休眠的净省大头，休眠判定是常数时间集合查询。
      - 设计如此：实际上是个优化手段，维持较远地图的变更的同时避免其消耗性能
    - pawn 每次跨格移动的传送检测：无传送许可时只做一次字典查询即返回——这是全 mod 最热的路径，刻意做到最薄；有许可（正在跨缝）才做进一步检查。
      - 实现限制：考虑到pawn远比地图边界的传送点少，那么把跨图检查做到pawn跨格移动上总比做到传送点上好。而且从信息角度上说，总得有个地方判断pawn是不是要跨图，这个在我看来属于是优化不掉的
    - job 启动 / 飞行中弹丸 / 瞄准视觉：非跨图战斗时 2-3 次引用比较即早退。
      - 实现限制：必须做这个我才能判断是否要触发邻接地图生成、跨图移动、弹丸跨图、瞄准方向修正等等一大堆真正重计算的东西，按理说正常同图没啥可感知的性能损耗
    - 跨缝索敌与射击 patch 群：本图已有目标时零成本放行；
      - 实现限制：必须patch每tick的索敌才能保证敌人真的能跨图索敌到殖民者
      - 逃生通道：可以在设置里关闭来回到原版行为，让索敌和射击不能跨图，同时省下这些开销
  - 周期轮询
    - 地图滚动 governor：每 600 ticks（约 1 倍速下最快 10 现实秒，或者5倍速下最快 0.6 现实秒；间隔可在设置中调 1-60 游戏秒）一次世界网格广度优先搜索（深度受删除距离限界，访问的地块数有界）；pawn 跨图/远行队变动会事件即时触发，不靠加密集轮询弥补。每 tick 的伴随检查在无待处理项时是空集零成本。
      - 实现限制：理论上如果我对rimworld的机制是全知的，那么我可以patch跨图、远行队变动、人员归属变动、人员状态变动等一系列触发器，只靠pawn的状态变化触发地块上有没有玩家角色的殖民者、动物、机械体之类的，但是很遗憾我不是全知的，所以我需要做这个检查兜底，避免出现状态不一致
    - 预加载队列与分帧生成推进：每 tick 一次队列消费，队列空时就是一次计数比较。
      - 实现限制：总得有个地方判断是不是有新的分帧地图生成要跑，显然这是个全新特性，没有现成基础计算让我复用，所以一个计数比较已经是最小性能消耗了，而且这已经小到不可感知了
  - 生成期 / 读档一次性（不构成持续开销）
    - 区域重建的接缝化、边界速查表构建、条带快照与接缝混合——只在新图生成和读档重建时发生。
  - 已知小额开销点（后续微优化项）
    - 地形写入守卫与传送点 def 查找中有几处未缓存的按名 Def 查询（主要影响生成期高频路径，单次量级很小）；拾取命中邻图时有少量 GC 分配。
- 偶现问题
  - 飞鸟落点在 void 上的一次性报错（原版飞行行为不查落点可走性，下一 tick 自愈）
    - 这个后来做了保护，但不确定会不会再次出现。

## 设置项

设置窗口按功能分 tab（地图生成 / 地图滚动休眠 / 跨图战斗 / 高级与诊断，原生 TabDrawer 样式），全部条目带 tooltip 说明；界面文本带简体中文翻译（`1.6/Languages/ChineseSimplified/Keyed/RimExodus.xml`）。

| 设置 | 默认 | 说明 |
|------|------|------|
| Border preload distance | 15 | Pawn 距边界多少格内触发邻图预加载（越大越容易触发邻图加载：加载期间拖慢 TPS、加载后持续占用直到休眠/删除） |
| Border no-build distance | 3 | 接缝带内侧禁建宽度 |
| Gradual map generation | 开 | 分帧增量生成开关。关闭后普通地块图改走**原版同步生成管线**——其他修改地图生成的 mod 的改动原生生效（特殊 mod 环境的逃生通道；仅影响普通地块图，据点等 POI 本就走原版管线） |
| Generation batch size | 64 | 分帧生成每帧处理的格数（仅分帧开启时有意义；调大 = 生成更快但每帧更卡，范围 16-512） |
| Enable map rolling dormancy | 开 | 地图滚动休眠总开关（机制目的/效果见其 tooltip；下方两个距离滑条需本项开启才生效） |
| Dormancy sleep distance | 2 | 距所有玩家 pawn ≥N 跳的图休眠（可设 1 = 离开即休眠） |
| Dormancy delete distance | 3 | 距所有玩家 pawn ≥N 跳的受管辖图删除（≥sleepHops，可相等 = 离开即销毁；当前图与家园永不删） |
| Dormancy scan interval | 10 秒 | 休眠/删除判定扫描的周期间隔（1-60 游戏秒）。调小 = 离开后地图更快休眠/清理但扫描更勤；即时事件（跨缝/远行队进出图）不受此限制、始终立刻触发 |
| Cross-map targeting & shooting | 开 | 跨缝索敌与射击总开关（关 = 战斗语义回原版，便于 A/B） |
| Seam band & evacuation toggle | 开 | **不在设置窗口，在地图右下角全局控制条（PlaySettings）**。沉浸模式开关：关闭后隐藏浅绿撤离带与接缝划线，并禁用一切经接缝带的原生离场成队（含组队界面出口）——跨缝步行/跨图下令不受影响。为录视频/截图的沉浸需求设计，非正常游玩用途（关闭期间无法主动经接缝带组队离场） |
| Verbose logging | 关 | 详细诊断日志 |
| Seam override noise amplitude | 0.15 | 接缝混合过渡带的噪声打散幅度（0 = 关闭） |
| 海岸补铺深水/浅水/沙滩距离 | 0.15 / 0.25 / 0.35 | 暂未在设置界面暴露（存档可改） |

## 依赖

- RimWorld 1.6
- [Harmony](https://github.com/pardeike/HarmonyRimWorld)（[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)）

## 源码目录结构

`Source/` 按功能域分为 9 个子目录，**Harmony patch 文件与所属功能域同目录放置**（不单设 Patches 目录）；所有文件同属 `RimExodus` 命名空间，csproj 为隐式 glob 编译。机制细节与勿回退要点见 `AGENTS.md`，本节只讲"哪个文件干什么"。

| 目录 | 职责 | 关键文件 |
|---|---|---|
| `Core/` | mod 入口、设置与跨域基础数据 | `RimExodusMod`（启动/PatchAll/绑定报告）、`RimExodusSettings`（设置窗口与 ModSettings）、`MapParent_SeamlessTile`（地块 WorldObject）、`SeamlessTileManager`（生成链入口 `GenerateTileMap`）、`SeamlessMapData`（邻居表/条带快照的载体分支唯一出处）、`SeamlessTileGraph`/`SeamlessTileRegistry`（邻接查询）、`SeamlessGridMath`（切比雪夫/邻格遍历统一口径）、`WorldTileGeometry`、`SeamlessMapUtility`（多边形归属解析）、`SeamlessEdgeCells`、`SeamlessBorderLookup`（边界带速查表）、`DebugActions_SeamlessTile`（Dev 菜单） |
| `Generation/` | 地图生成管线：六边形裁切、void、接缝混合、分帧增量生成 | 四个注入 genStep（`GenStep_SeamlessTile` 391 备份+铺 void / `GenStep_SeamOverride` 392 接缝混合 / `GenStep_EnterSpots` 1490 铺传送点 / `GenStep_CoastalEdgeFill` 230）、`SeamlessPolygonGeometry`（接缝带几何唯一实现）、`SeamlessTerrainFill`、`SeamlessSeamOverride`（卷积混合规则）、`SeamStripData`（三层条带快照）、`IncrementalMapGenerator`（分帧增量生成）、`MapGenerationProgressUI`、`SeamlessEnterSpotPlacer` + `CompSeamlessTileEnterSpot`（传送点铺设与标记）、配套 patch：河流/道路对齐（`Patches_TileMutatorRiver`/`Patches_GenStepRoads`）、建筑选址（`Patches_BuildingPlacement`）、揭雾分径（`Patches_GenStepFog`）、地形守卫（`Patches_TerrainGrid`）、生成计时与互斥（`Patches_MapGenTiming`/`Patches_IncrementalMapGen`）、`Patches_GenConstruct`、远行队进图出生点（`Patches_CaravanEnterMap`） |
| `Lifecycle/` | 地图滚动生命周期：软休眠、距离删除、天气域、原生家族/Settlement 接管 | `SeamlessDormancyGovernor`（距离策略 + 全局静态清扫）+ `Patches_Dormancy`、`SeamlessMapGovernance`（管辖/家园特权判定唯一出处）、`SeamlessWeatherClusterManager` + `Patches_WeatherCluster`（群系连通域共享天气）、`Patches_CampTileMap`（营地接入生成链）、`Patches_NativeMapFamily`（"人走即删"族接管 + 败亡守卫）、`SeamlessSettlementTrader`/`SeamlessSettlementTalk`/`Patches_SettlementTrade`（据点贸易商与对话/交易） |
| `Transfer/` | 跨缝传送机制：许可登记、触发、桥接下令 | `SeamlessMapTransferTrigger`（踩点热路径）、`SeamlessTransferGrants`（许可登记表）、`SeamlessMapTransfer`（传送执行）、`SeamlessCrossMapOrders`（桥接下令 + 双侧代价场选点）、`SeamlessPathCostField`（Dijkstra 代价场）、`SeamlessBoundaryRules`（主体资格判定）、`Patches_Job`（StartJob 钩子：撤离登记/许可清理）、`Patches_PawnPathFollower`（nextCell 踩点触发） |
| `Interaction/` | 跨图交互 UI 层：点击重放、选中保持、相机、虚拟传送 | `Patches_ClickReplay` + `SeamlessReplayContext`（邻图点击重放链）、`SeamlessGenUI`、`SeamlessSelectionTracker`（跨切图选中保持）、`SeamlessCameraFocus`（自动聚焦 + 无感相机）、`SeamlessVirtualTeleporter`（评估窗口零写入的坐标系虚拟传送）、`Patches_CrossMapCommon`/`Patches_ReachabilityCrossMap`（CanReach 真实化等公共函数层）、`Patches_Selector`（殖民者栏休眠过滤）、`Patches_CaravanExitDiagnostics` |
| `Combat/` | 跨图索敌与射击 | `SeamlessCrossMapSight`（分段 LOS）、`SeamlessCombatCoords`（统一坐标与归属路由）、`Patches_CombatTargetSearch`（索敌两层模型跨图化）、`Patches_CombatTargeting`（TryCastShot 门/ShotReport）、`Patches_Projectile`（弹丸缝交接）、`Patches_CombatVisuals`（朝向/瞄准角/连线等视觉修正） |
| `EdgeBehavior/` | 地图边缘语义接缝化与预加载 | `SeamlessBorderPreloader`/`SeamlessTilePreloader`（边界带预加载队列）、`Patches_CellFinder`/`Patches_RCellFinder`（边缘出口格接缝池）、`Patches_Reachability`（CanReachMapEdge）、`Patches_RegionMaker`（区域触边判定）、`Patches_ExitMapGrid`（撤离带）、`Patches_PlaySettings`（沉浸模式开关 + 缩放门控） |
| `Rendering/` | void 渲染与邻居背景 | `SeamlessTileRenderer`（CommandBuffer 邻图四层收集 + 光照天色分层）、`SeamlessVoidRockLink`（void 边界岩隐形 link 延续体）、`Patch_MapEdgeClipDrawer_DrawClippers` |
| `Compat/` | 第三方 mod 兼容层（全部软检测，缺 mod 时零开销） | `SeamlessLandformsCompat`（Geological Landforms 分帧路径复刻）、`SeamlessMapPreviewCompat`（预览线程守卫与避让）、`SeamlessPerspectiveShiftCompat`（Perspective Shift 第一人称移动接入）、`SeamlessVehiclesCompat`（Vehicle Framework 载具移动/下令接入无缝世界） |

## 项目文档索引

设计文档与实现记录都在 `doc/` 下（中文）：

- [无缝世界地块探索.md](doc/无缝世界地块探索.md) — 主计划：长期设计与五阶段路线图、当前阶段结果。
- [接缝带定义.md](doc/接缝带定义.md) — 接缝带几何权威定义。
- [地图生成步骤.md](doc/地图生成步骤.md) — 完整 genStep 执行顺序与 RimExodus 注入点。
- [地图滚动休眠.md](doc/地图滚动休眠.md) — 地图滚动生命周期权威文档。
- [边界行为表.md](doc/边界行为表.md) — 传送点/接缝带行为规范（主体 × 移动来源全枚举）。
- [跨图命令适配审计.md](doc/跨图命令适配审计.md) — 跨图命令 job 类型适配矩阵。
- [第五阶段-跨图寻路与射击.md](doc/第五阶段-跨图寻路与射击.md) 等第 X 阶段文档 — 各阶段实现叙事与决策记录。
- [TODO.md](doc/TODO.md) — 游戏内测试问题备忘。
- [SteamWorkshop介绍草稿.md](doc/SteamWorkshop介绍草稿.md) — 创意工坊页面描述文案草稿（中英双语 BBCode，事实以本 README 为源）。
- `AGENTS.md` — 开发工作文档（架构事实与勿回退要点，面向协作者/agent）。

**维护约定**：对外可见的兼容性结论与已知限制变化，除各权威文档外须同步更新本 README（见 `AGENTS.md`"文档同步范围"条）。

## 许可与鸣谢

本项目以 [MIT 许可证](LICENSE) 开源。

特别鸣谢 [Vehicle Map Framework](https://github.com/Vehicle-Map-Framework/Vehicle-Map-Framework)——正是它作为先驱者证明了跨地图 mod 是可行的，本项目的最初调研也从它开始。虽然深入调研后发现架构假设差异较大（VMF 以口袋地图与载具跨图为中心，而 RimExodus 需要的是对等地块连续世界），最终未能复用其代码、从头另起炉灶，但没有它就没有 RimExodus。
