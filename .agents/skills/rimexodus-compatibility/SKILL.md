---
name: rimexodus-compatibility
description: Integrate an external RimWorld mod with RimExodus. Use when exposing your mod's world layer to seamless travel, querying RimExodus neighbors or real cells across seams, or adapting your mod's overlays for REF rendering.
---

# 第三方 Mod 接入 RimExodus

在用户自己的 Mod 或兼容补丁中实现接入。先确认目标游戏版本、RimExodus/REF 版本和是否允许玩家单独使用该 Mod。先查你的地图创建入口或算法需要的数据，再选接口。

| 你的 Mod 需要什么 | 使用的入口 |
| --- | --- |
| 让自己新增的世界层支持无缝通行 | `SeamlessMapProviders.Register` |
| 查询两张已加载地图是否直接相连，或换算局部坐标 | `SeamlessTileGraph`、`SeamlessViewProjection` |
| 让视野等格子算法读取邻图，知道每格属于哪张真实地图 | REF `MapRuntime.Current.Spatial.TryCapture` |
| 裁切你自己的 REF 渲染计划中的平面覆盖层 | `MapRenderOptions.OverlayFootprint`、`OverlayClipSelector` |

## 配置依赖

以下接口以 RimExodus 1.1.5 和 RimExodus Framework（REF）1.0.5 为基线。用目标 RimWorld 版本的程序集编译。需要地图提供者或邻接 API 时引用 `RimExodus.dll`，使用 REF 的格子查询或绘制 API 时引用 `RimExodus.Maps.dll`。让相应 Mod 提供运行时程序集，发布你的 Mod 时只打包自己的 DLL。

正式版包 ID 为 `RimExodus.SeamlessWorld` 和 `RimExodus.Framework`，开发版为 `RimExodus.SeamlessWorld.Dev` 和 `RimExodus.Framework.dev`。若无缝功能是硬依赖，在你的 Mod 元数据中声明依赖。若接入是可选功能，把直接引用 RimExodus/REF 类型的代码隔离到按条件加载的兼容程序集，或提供独立的兼容补丁 Mod。在 RimExodus 或所需 API 缺失时继续使用你自己的单图路径。

## 让你的世界层支持无缝通行

1. 从你已有的地图入口确认 `Map.Tile` 对应真实世界地块，地图由普通 `MapParent` 承载。一个 `PlanetLayerDef` 只能注册一个提供者。原版根地表已有内置规则，地下仓库、载具内部等 `PocketMapParent` 仍走宿主 Mod 的入口。
2. 在你的 Mod 初始化中，于 Def 加载后、创建或读入世界前注册一次。用你项目的 Def 和资格判断替换示例中的 `MyDefOf` 与 `MyWorld`。`Register` 返回 `false` 时检查该层是否已有提供者。

```csharp
using RimExodus;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal sealed class MySeamlessLayer : SeamlessMapProvider
{
    internal static void RegisterWhenReady()
        => LongEventHandler.ExecuteWhenFinished(() =>
            SeamlessMapProviders.Register(MyDefOf.WorldLayer, new MySeamlessLayer()));

    public override bool Accepts(PlanetTile tile, MapParent parent)
        => MyWorld.IsPlayable(tile) && (parent == null || MyWorld.CanJoin(parent));

    public override WorldObjectDef GetMapParentDef(PlanetTile tile)
        => MyDefOf.EmptyTileSite;
}
```

3. 先用默认轮廓和原版邻接验证生成与通行，再按需要覆写规则。

   - `Accepts` 在生成前也会调用，空地块的 `parent` 为 `null`。`GetMapParentDef` 为可预加载的空地块提供你自己的地图父对象 Def，返回 `null` 时禁用空地块生成。已有地点沿用原有父对象和生成器。临时关闭的地点用 `CanGenerate` 限制。
   - `GetFootprint` 为方形地图返回界内、有序、可重复生成的局部 X/Z 凸多边形，返回 `null` 使用原版轮廓。`GetEdge` 的索引对应从该顶点到下一顶点的边。每对地图须在同一世界层，两端各有一条边互相指向。
   - `SeamlessMapEdge` 默认按边中点计算平移。若显式给出偏移，含义是「邻图局部坐标 + 偏移 = 本图局部坐标」。Y 必须为零，平面偏移与中点差的误差须在两格内，两端都指定时须互为相反数。返回 `default` 封闭该边。用 `new SeamlessMapEdge(PlanetTile.Invalid, allowVanillaExit: true)` 可保留原版离场。
   - `EnableNewMaps` 控制新图裁切和邻图预加载。用新生成的地图验证接入，已有矩形地图保持原形。生成、接线和落点由 RimExodus 处理，提供者回调负责回答规则。

## 在你的算法中使用邻图

- 已有两张 `Map` 时，调用 `SeamlessTileGraph.TryGetNeighborLink(source, target, out var link)`。用 `GetAllNeighbors(source)` 枚举已接线的活跃直邻，从 `link.map.Tile` 取完整地块地址。对于旧整数接口，`TryGetNeighborLinkByWorldTile(source, id, …)` 在来源图所在层查询，`TryGetMapByWorldTile(id, …)` 在根地表全局查询。跨层地址保留原版 `PlanetTile`。
- 将某张地图上的点转换到另一张图的坐标系时，调用 `SeamlessViewProjection.TryProject(sourceMap, sourceLocal, destinationMap, out var local)`。如果要判断投影格实际由哪张地图持有，再使用 `MapCellSpace.TryResolve`。
- 使用参考地图的**局部坐标**查询格子所有权，按返回的 `Map` 和 `Cell` 访问实际格网。`TryCapture` 在没有可用空间时返回 `false`，此时运行你的单图逻辑。已捕获空间中解析失败的格按你的算法的不可用格处理。

```csharp
using RimExodus.Maps;
using Verse;

internal static class SeamlessCells
{
    internal static bool TryCapture(Map reference, out MapCellSpace space)
    {
        space = null;
        var runtime = MapRuntime.Current;
        return runtime != null
            && runtime.Spatial.TryCapture("RimExodus.SeamlessWorld", reference, out space);
    }
}
```

在游戏主线程的同一轮计算内复用 `MapCellSpace`。需要密集数组时用有预算的 `CaptureGrid`，保留每个索引到真实地图和格子的映射。地图加载、删除、休眠或唤醒后重新捕获。查询范围为参考地图和可用直邻，无缝查询本身不会装载地图，也不会下发移动或战斗命令。

## 你的 Mod 有自定义覆盖层时

如果你的 Mod 自己向 REF 提供 `MapRenderPlan`，设置源图局部凸多边形 `OverlayFootprint`，用 `OverlayClipSelector` 选择可读的平面三角网格。返回 `true` 跳过尺寸筛选，`false` 跳过裁切，`null` 使用默认筛选。独占材质可以用 `MapDrawCommand.UsesMaterial` 匹配，共享材质应在你的图层绘制回调内标识提交。如果绘制计划由 RimExodus 管理，`SeamlessMapProvider` 只负责世界层和接缝规则，覆盖层选择需要单独的渲染适配。

## 验证你的集成

- 从你的 Mod 启动新档，查看提供者注册结果。生成两张相邻图，分别验证空地块预加载、尚未成图的已有地点和双向通行。设置了封闭的边时，验证无法从该边跨缝。
- 用同号但不同层的地块核对结果。保存并读档，再验证接缝与查询。对未加载、已休眠及口袋地图验证你的回退行为。可选集成还要用未安装 RimExodus 的组合启动一次。
- 有覆盖层的改动，在开启高兼容性邻图渲染时检查两张图同时可见、移动镜头和切换聚焦的画面。把编译通过、绑定成功与游戏内效果分别记录。
