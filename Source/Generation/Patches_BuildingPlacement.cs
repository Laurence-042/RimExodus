using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 建筑选址避开六边形边缘：三个 patch 覆盖全部建筑选址根原语。
    ///
    /// 【为什么】六边形可活动区内接于方形地图（边中点距方形边 ~17 格，顶点处 0 格），
    /// order 389 会把六边形外铺 void + 清实体。原版建筑选址全部按**方形边界**收缩
    /// （如 <c>GenStep_Settlement.CanScatterAt</c> 的 <c>BoundsRect(12)</c>——距方形边 12 格
    /// 的候选位置在六边形边中点方向仍跨六边形边），不感知六边形 → 建筑被 void 切半。
    ///
    /// 【根原语收敛】（调研结论）所有建筑选址收敛到 3 个根原语：
    /// - <see cref="GenStep_Scatterer.CanScatterAt"/>（protected virtual）：全部 Scatterer 子类
    ///   的公共验证层（Settlement/BanditCamp 400、ItemStash、ScatterRuinsSimple/Shrines 750、
    ///   ScatterThings→Geysers 950、ScatterGroups、ScattererBestFit——子类 override 都先调 base，
    ///   base 调用命中本 patch 的基类方法体）。
    /// - <see cref="MapGenUtility.GetClearRects"/>：清晰矩形路（Outpost、AncientComplex/LargeRuins、
    ///   AncientStockpile/Gravcore、SurveySite、GravshipWreckage、TryGetStructureRect、
    ///   TileMutatorWorker Harbor/Stockpile/AncientUplink/AncientStructure）。Burst 内核不可
    ///   patch，此托管入口是唯一可行点；调用方自带多级 fallback。
    /// - <see cref="CellFinder.RandomNotEdgeCell"/>：Scatterer 采样 + FindPlayerStartSpot(850)
    ///   兜底路径（tightness 降级后全图随机）+ 运行时 CompDeepScanner/incident（此时六边形外
    ///   是 void，判 Invalid 语义正确）。
    ///
    /// 【守卫】<c>worldTile &lt; 0</c> 防御性放行（异常态），正常地图一律生效——所有地图
    /// 一视同仁（见 AGENTS.md 铁律）。几何判定用多边形顶点（BuildPolygonVertices），纯几何、
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

        /// <summary>清晰矩形角点距六边形边的最小余量（防矩形主体跨边）。</summary>
        internal const float ClearRectMargin = 10f;
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
    /// Patch B：Postfix <c>MapGenUtility.GetClearRects</c>——过滤四角任一距六边形边
    /// &lt; <see cref="BuildingPlacementConstants.ClearRectMargin"/> 格或六边形外的矩形。
    /// 无 Map 参数，用 <c>MapGenerator.mapBeingGenerated</c>（生成期静态，与原方法体同源）。
    /// </summary>
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.GetClearRects))]
    static class Patch_MapGenUtility_GetClearRects
    {
        static void Postfix(ref List<CellRect> __result)
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
                // 四角 + 边中点采样：全部在六边形内且距边 ≥ 余量才保留。
                // 用角点+边中点而非逐格（矩形是凸的、六边形是凸的，角在内不能保证边在内，
                // 边中点采样补足最常见的"边中段跨界"形态；10 格余量进一步兜底）。
                var safe = true;
                var corners = new[]
                {
                    new IntVec3(rect.minX, 0, rect.minZ),
                    new IntVec3(rect.maxX, 0, rect.minZ),
                    new IntVec3(rect.minX, 0, rect.maxZ),
                    new IntVec3(rect.maxX, 0, rect.maxZ),
                    new IntVec3((rect.minX + rect.maxX) / 2, 0, rect.minZ),
                    new IntVec3((rect.minX + rect.maxX) / 2, 0, rect.maxZ),
                    new IntVec3(rect.minX, 0, (rect.minZ + rect.maxZ) / 2),
                    new IntVec3(rect.maxX, 0, (rect.minZ + rect.maxZ) / 2),
                };
                foreach (var c in corners)
                {
                    if (!SeamlessPolygonGeometry.ContainsPoint(verts, map.Size.x, c) ||
                        SeamlessPolygonGeometry.DistanceToNearestEdge(verts, c) < BuildingPlacementConstants.ClearRectMargin)
                    {
                        safe = false;
                        break;
                    }
                }
                if (safe) kept.Add(rect);
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
}
