using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 建筑选址避开六边形边缘：四个 patch 覆盖建筑选址的根原语与结构矩形搜索。
    ///
    /// 【为什么】六边形可活动区内接于方形地图（边中点距方形边 ~17 格，顶点处 0 格），
    /// order 389 会把六边形外铺 void + 清实体。原版建筑选址全部按**方形边界**收缩
    /// （如 <c>GenStep_Settlement.CanScatterAt</c> 的 <c>BoundsRect(12)</c>——距方形边 12 格
    /// 的候选位置在六边形边中点方向仍跨六边形边），不感知六边形 → 建筑被 void 切半。
    ///
    /// 【根原语收敛】（调研结论）建筑选址收敛到以下入口：
    /// - <see cref="GenStep_Scatterer.CanScatterAt"/>（protected virtual）：全部 Scatterer 子类
    ///   的公共验证层（Settlement/BanditCamp 400、ItemStash、ScatterRuinsSimple/Shrines 750、
    ///   ScatterThings→Geysers 950、ScatterGroups、ScattererBestFit——子类 override 都先调 base，
    ///   base 调用命中本 patch 的基类方法体）。
    /// - <see cref="MapGenUtility.GetClearRects"/>：清晰矩形路（Outpost、AncientComplex/LargeRuins、
    ///   AncientStockpile/Gravcore、SurveySite、GravshipWreckage、TryGetStructureRect ①②③级、
    ///   TileMutatorWorker Harbor/Stockpile/AncientUplink/AncientStructure）。Burst 内核不可
    ///   patch，此托管入口是唯一可行点；调用方自带多级 fallback。
    /// - <see cref="CellFinder.RandomNotEdgeCell"/>：Scatterer 采样 + FindPlayerStartSpot(850)
    ///   兜底路径（tightness 降级后全图随机）+ 运行时 CompDeepScanner/incident（此时六边形外
    ///   是 void，判 Invalid 语义正确）。
    /// - <see cref="MapGenUtility.TryGetStructureRect"/>：AncientStructure 族地标（精炼厂/仓库/
    ///   驻防地/发射场等，Odyssey）与 Harbor/Stockpile 等 worker 的主体结构矩形搜索。其①②③级
    ///   重试走 GetClearRects，但**④级 fallback 直接 CellFinder.TryFindRandomCell 全图随机采样**
    ///   （MapGenUtility.cs:794）——绕过 GetClearRects 即绕过上面全部拦截；其 Validator 只查
    ///   方形边界收缩 10 格 / 水占比 ≤25% 即放行 / UsedRects / 距中心 0.75×S，对 void（非水、
    ///   无 affordance）全盲。小图（UI 最小 200，内切半径 86.6）+ 大地标（含围栏 46~56，仓库
    ///   最大 61~81）+ 山地图（elevation 清晰矩形碎小）→ ①②③全败 → ④把结构矩形压进 void 角落
    ///   → sketch 以 forceTerrainAffordance 落地时 void 写入被 TerrainGrid 守卫拒绝、部件静默
    ///   跳过 = 地标缺角（2026-09 Icarus 报告）。修法 = 零拒绝改写（见 Patch D），**勿改成
    ///   extraValidator 拒绝注入**——那会在小图上把缺角换成 "Failed to find location structure"
    ///   红字激增（与 GetClearRects 整块移除同类的饥饿，2026-09 用户否决）。
    ///
    /// 【GetClearRects 裁剪语义（2026-09，勿回退为"整块移除"）】旧版把跨六边形边的矩形**整块
    /// 删除**——vanilla maximal 矩形天然延向方形边缘，小图上几乎全被删 → LargeRuins 族
    /// （CrashedMechanoidPlatform/OrbitalWreck → GetLargestClearRects）三级降级全拿空列表 →
    /// "did not find any valid rects to generate ruins" 警告 + 内容整块缺失；而 vanilla 本有
    /// 逐格收缩降级（TryFixInvalidRects 对无效格收缩矩形），矩形被上游删光后它无从工作。
    /// 现改为**半平面裁剪**：矩形收缩进六边形（含余量），仍满足调用方 minWidth/minHeight 才
    /// 保留——"保留矩形必在六边形内且距边 ≥余量"的保证不变，候选数量恢复（净拒绝压力下降）。
    ///
    /// 【守卫】<c>worldTile &lt; 0</c> 防御性放行（异常态），正常地图一律生效——所有地图一
    /// 视同仁（见 AGENTS.md 铁律）。几何判定用多边形顶点（BuildPolygonVertices），纯几何、
    /// 与地形实况无关（2026-08 起 void 已在 389 先于选址铺好，但几何判定与其等价且不读地形，
    /// 保持确定性与低开销；勿用 SeamlessEdgeCells.HasSeamEdge，那是运行时接缝带入口、
    /// 传送点在全部 genStep 之后才铺）。
    ///
    /// 【残余缺口（接受）】<c>GenStep_Outpost.GetOutpostRect</c> 贴附矩形、
    /// <c>GenStep_Settlement.GenerateLandingPadNearby</c>——锚点已安全后贴边矩形溢出概率低，先观察
    /// （2026-08 void 提前后这些缺口处地板可盖住混合带结果，观察项见 SeamlessTileGenerator.xml）。
    /// </summary>
    internal static class BuildingPlacementConstants
    {
        /// <summary>
        /// Scatterer 锚点距六边形边的最小距离。取最大建筑半宽 + 1：Settlement 最大 38×38
        /// （半宽 19）→ 20。拒绝的采样由上层 1000 次重试自然消化（六边形+边距内区域约占方形 55-60%）。
        /// </summary>
        internal const float ScatterEdgeMargin = 20f;

        /// <summary>
        /// 清晰矩形/结构矩形距六边形边的最小余量（防矩形主体跨边；接缝带 ~3 圈 + 缓冲）。
        /// GetClearRects 裁剪（Patch B）与 TryGetStructureRect 安全判定（Patch D）共用同口径。
        /// </summary>
        internal const float ClearRectMargin = 10f;
    }

    /// <summary>
    /// 建筑选址六边形几何 helper（Patch B 裁剪 / Patch D 安全判定共用）。
    /// 坐标约定跟随 <see cref="SeamlessPolygonGeometry"/>：顶点为地图局部 Vector2
    /// （x=东向格，y=北向格），格点判定一律用格中心（cell + 0.5）。
    /// </summary>
    internal static class BuildingPlacementGeometry
    {
        /// <summary>
        /// 矩形六边形安全判定：四角格 + 四边中点格共 8 点采样，全部在多边形内且距边 ≥ margin。
        /// 矩形与六边形皆凸，四角在内即整矩形在内；边中点补足距边余量的鲁棒性（与旧版过滤同口径）。
        /// </summary>
        public static bool IsRectSafe(List<Vector2> verts, int mapSize, CellRect rect, float margin)
        {
            var midX = (rect.minX + rect.maxX) / 2;
            var midZ = (rect.minZ + rect.maxZ) / 2;
            var samples = new[]
            {
                new IntVec3(rect.minX, 0, rect.minZ),
                new IntVec3(rect.maxX, 0, rect.minZ),
                new IntVec3(rect.minX, 0, rect.maxZ),
                new IntVec3(rect.maxX, 0, rect.maxZ),
                new IntVec3(midX, 0, rect.minZ),
                new IntVec3(midX, 0, rect.maxZ),
                new IntVec3(rect.minX, 0, midZ),
                new IntVec3(rect.maxX, 0, midZ),
            };
            for (var i = 0; i < samples.Length; i++)
            {
                if (!SeamlessPolygonGeometry.ContainsPoint(verts, mapSize, samples[i]) ||
                    SeamlessPolygonGeometry.DistanceToNearestEdge(verts, samples[i]) < margin)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 半平面裁剪：把轴对齐矩形收缩进凸多边形（含 margin 余量），仍满足 minWidth/minHeight
        /// 才返回 true。几何不可用（顶点缺失）fail-open 原样返回，交由上层守卫兜底。
        /// </summary>
        public static bool TryClipRectToPolygon(List<Vector2> verts, int mapSize, CellRect rect,
            float margin, int minWidth, int minHeight, out CellRect clipped)
        {
            clipped = rect;
            if (verts == null || verts.Count < 3) return true; // 防御性 fail-open（上层已有守卫）

            // 常规一轮即过 8 点复核；第二轮消化取整残差（防御性），仍失败丢弃。
            for (var round = 0; round < 2; round++)
            {
                if (!ClipOnce(verts, ref clipped, margin)) return false;
                if (clipped.Width < minWidth || clipped.Height < minHeight) return false;
                if (IsRectSafe(verts, mapSize, clipped, margin)) return true;
            }
            return false;
        }

        /// <summary>
        /// 单遍半平面裁剪。对每条多边形边：内法线 n（朝多边形中心一侧）、边线常数 c = n·v0，
        /// 内部约束 n·p ≥ c + margin；矩形上 n·p 的最小值由各轴按 n 分量符号取对应下界侧贡献，
        /// 违反量为 v 时把该侧沿 |n| 分量较大的轴内移 ceil(v/|n·axis|) 格（格中心坐标系，移动 d 格
        /// 恰好提升 d·|n·axis|）。收缩只会提高其余半平面的最小值（矩形变小是原矩形的子集），
        /// 已满足的边不会被重新违反——单遍即收敛。矩形与多边形无有效交集 → false。
        /// </summary>
        private static bool ClipOnce(List<Vector2> verts, ref CellRect rect, float margin)
        {
            var cx = 0f;
            var cy = 0f;
            for (var i = 0; i < verts.Count; i++)
            {
                cx += verts[i].x;
                cy += verts[i].y;
            }
            cx /= verts.Count;
            cy /= verts.Count;

            var n = verts.Count;
            for (var j = 0; j < n; j++)
            {
                var v0 = verts[j];
                var v1 = verts[(j + 1) % n];
                var ex = v1.x - v0.x;
                var ey = v1.y - v0.y;
                var elen = Mathf.Sqrt(ex * ex + ey * ey);
                if (elen < 1e-6f) continue;
                var nx = -ey / elen;
                var ny = ex / elen;
                if (nx * (cx - v0.x) + ny * (cy - v0.y) < 0f)
                {
                    nx = -nx;
                    ny = -ny;
                }
                var c = nx * v0.x + ny * v0.y;

                // 格中心坐标系下的矩形四界。
                var minDot = (nx > 0f ? nx * (rect.minX + 0.5f) : nx * (rect.maxX + 0.5f))
                           + (ny > 0f ? ny * (rect.minZ + 0.5f) : ny * (rect.maxZ + 0.5f));
                var violation = c + margin - minDot;
                if (violation <= 0f) continue;

                if (Mathf.Abs(nx) >= Mathf.Abs(ny))
                {
                    var d = Mathf.CeilToInt(violation / Mathf.Abs(nx));
                    if (nx > 0f) rect.minX += d;
                    else rect.maxX -= d;
                }
                else
                {
                    var d = Mathf.CeilToInt(violation / Mathf.Abs(ny));
                    if (ny > 0f) rect.minZ += d;
                    else rect.maxZ -= d;
                }
                if (rect.maxX < rect.minX || rect.maxZ < rect.minZ) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Patch A（主）：Prefix <c>GenStep_Scatterer.CanScatterAt</c>——锚点在六边形外或距边
    /// &lt; <see cref="BuildingPlacementConstants.ScatterEdgeMargin"/> 格时直接拒绝。
    /// protected virtual（字符串声明，子类 base 调用命中基类方法体即命中本 patch）。
    ///
    /// （2026-09 前哨保留的"区域内拒绝散布"层已随 GenStep_ZoneRestore order 后置到 1100
    ///（全部常规建筑生成源之后）整体拆除——权威置换在最后跑，源头禁布只剩省功价值，属 patch
    /// 思路。勿再加回。）
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Scatterer), "CanScatterAt")]
    static class Patch_GenStep_Scatterer_CanScatterAt
    {
        static bool Prefix(IntVec3 loc, Map map, ref bool __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return true; // 防御性：异常态放行原版

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3) return true; // 几何异常放行原版

            // 六边形外 → 拒绝（将来是 void）。
            if (!SeamlessPolygonGeometry.ContainsPoint(verts, map.Size.x, loc))
            {
                __result = false;
                return false;
            }

            // 距六边形边不足 → 拒绝（防建筑跨边被 void 切半；建筑半宽可达 19）。
            if (SeamlessPolygonGeometry.DistanceToNearestEdge(verts, loc) < BuildingPlacementConstants.ScatterEdgeMargin)
            {
                __result = false;
                return false;
            }

            return true; // 锚点安全，放行原版验证链
        }
    }

    /// <summary>
    /// Patch B：Postfix <c>MapGenUtility.GetClearRects</c>——**半平面裁剪**（2026-09，勿回退为
    /// 整块移除）：每个返回矩形收缩进六边形（含 <see cref="BuildingPlacementConstants.ClearRectMargin"/>
    /// 余量），仍满足调用方 minWidth/minHeight 才保留，否则丢弃。无 Map 参数，用
    /// <c>MapGenerator.mapBeingGenerated</c>（生成期静态，与原方法体同源）。
    /// 动机见文件头【GetClearRects 裁剪语义】——整块移除曾在小图上饿死 LargeRuins 族三级降级链
    /// （"did not find any valid rects" 警告 + 机械体平台等内容整块缺失）。
    /// </summary>
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.GetClearRects))]
    static class Patch_MapGenUtility_GetClearRects
    {
        static void Postfix(int minWidth, int minHeight, ref List<CellRect> __result)
        {
            var map = MapGenerator.mapBeingGenerated;
            if (map == null) return; // 防御性：非生成期调用（原方法此时也会 Error）放行

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3) return;

            var kept = new List<CellRect>(__result.Count);
            foreach (var rect in __result)
            {
                if (BuildingPlacementGeometry.TryClipRectToPolygon(verts, map.Size.x, rect,
                        BuildingPlacementConstants.ClearRectMargin, minWidth, minHeight, out var clipped))
                {
                    kept.Add(clipped);
                }
                // 裁剪后低于调用方最小尺寸（含完全在六边形外的矩形）→ 丢弃。
                // 例行路径不记日志（每图数十个矩形、纯几何结果，verbose 亦会刷屏）。
            }
            __result = kept;
        }
    }

    /// <summary>
    /// Patch C（双保险）：Postfix <c>CellFinder.RandomNotEdgeCell</c>——采样落在六边形外时
    /// 返回 Invalid（上层重试循环自动继续）。覆盖 FindPlayerStartSpot 的 tightness 降级兜底
    /// 与运行时消费者（此时六边形外是 void，语义正确）。
    /// </summary>
    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomNotEdgeCell))]
    static class Patch_CellFinder_RandomNotEdgeCell
    {
        static void Postfix(int minEdgeDistance, Map map, ref IntVec3 __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3) return;

            if (!__result.IsValid) return;
            if (!SeamlessPolygonGeometry.ContainsPoint(verts, map.Size.x, __result))
            {
                __result = IntVec3.Invalid;
            }
        }
    }

    /// <summary>
    /// Patch D（2026-09）：Postfix <c>MapGenUtility.TryGetStructureRect</c>——结构矩形六边形
    /// 安全改写（**零拒绝**）。原方法原样运行（四级重试、失败频率与 vanilla 完全一致，不新增
    /// 任何饥饿）；Postfix 仅在 <c>__result == true</c> 且返回矩形未过 8 点安全检查时改写：
    /// 沿矩形中心→地图中心逐格滑动取首个安全位置（保持 vanilla 选址局部性）；滑到中心仍不安全
    /// （病态巨构/超小图）则中心锚定接受——vanilla 终端 fallback（map.Center.RectAbout 零校验）
    /// 同款语义，中心锚定在 S≥200 恒容纳最大地标（AncientWarehouse 81 见方，半对角 ~57 ≪
    /// 内切半径 86.6−余量）。①②③级矩形来自（Patch B 裁剪后的）安全清晰矩形内接，不会进改写
    /// 分支；改写后的矩形由调用方 worker 照常登记 UsedRects。改写不重验调用方 extraValidator
    /// （vanilla 终端 fallback 同样不验，接受边界）。
    /// 根因与④级 fallback 机制见文件头根原语清单。Generation 模块 verbose 记录每次改写。
    /// </summary>
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.TryGetStructureRect))]
    static class Patch_MapGenUtility_TryGetStructureRect
    {
        static void Postfix(Map map, ref bool __result, ref CellRect rect)
        {
            if (!__result) return; // 失败路径：worker 自己的中心兜底（六边形安全），不动
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3) return;

            if (BuildingPlacementGeometry.IsRectSafe(verts, map.Size.x, rect, BuildingPlacementConstants.ClearRectMargin))
            {
                return; // ①②③级（安全清晰矩形内接）或④级恰好安全——不动
            }

            var original = rect;
            var delta = map.Center - rect.CenterCell;
            var steps = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.z));
            // 终端兜底 = 中心锚定；t=steps 的候选恰等于它，滑动循环只探 1..steps-1。
            var final = rect.MovedBy(delta);
            for (var t = 1; t < steps; t++)
            {
                var candidate = rect.MovedBy(
                    Mathf.RoundToInt(delta.x * (float)t / steps),
                    Mathf.RoundToInt(delta.z * (float)t / steps));
                if (BuildingPlacementGeometry.IsRectSafe(verts, map.Size.x, candidate, BuildingPlacementConstants.ClearRectMargin))
                {
                    final = candidate;
                    break;
                }
            }

            rect = final;
            RimExodusLog.Message(RimExodusLogModule.Generation,
                $"Structure rect re-anchored for hexagon safety (tile {worldTile}): {original} -> {final}");
        }
    }
}
