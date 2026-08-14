using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 河流接缝对齐：Patch <c>TileMutatorWorker_River.GetMapEdgeNodes</c>（protected），
    /// 把河的端点从"随机直线的图外交点"替换为"接缝边中点锚点"。
    ///
    /// 【为什么】原生 river 生成对每张地图完全独立随机：
    /// - 河是过 <c>riverCenter</c>（<c>Rand.Range(0.3,0.7)×Size</c> 随机点）的直线 + Perlin 弯曲；
    /// - 两端只共享"流向角"（世界图首尾邻居连线 heading），入口/出口在边上的落点纯随机不相关；
    /// - 弯曲 bell 函数在地图边缘处可漂移 ±11 格；
    /// - 无任何原生跨 tile 对齐保证，SeamOverride 又跳过水格不兜底 → 河在接缝处必然错位。
    ///
    /// 【做法】GetMapEdgeNodes 返回 (heading 反向端, heading 正向端)——
    /// - 上游端 = <c>FindClosestEdgeByAngle(angle+180)</c> 边中点（对应上游邻居）；
    /// - 下游端 = <c>FindClosestEdgeByAngle(angle)</c> 边中点（对应下游邻居）。
    /// 端点用边中点本身（offsetCells=0）：河延伸到六边形边上，接缝处两端水直接相接。
    /// 弯曲 bell 函数端点=0 → 接缝处河是直线段，天然对齐友好。
    ///
    /// 【调用方兼容】所有三个调用方都拿元组后按 <c>IsFlowingAToB(a,b,angle)</c>（整数度容差）
    /// 决定 start/end 顺序：
    /// - 普通 River / Confluence 干流：两端都用作 start/end（本 patch 的主场景）。
    /// - Confluence 流入支流：取 heading 反向端做 start（连汇合点）——正是上游边锚点。
    /// - Confluence 流出支流：取 heading 正向端做 end（连汇合点）——正是下游边锚点。
    /// 投影扭曲可能让两锚点连线偏离世界 angle 数度 → IsFlowingAToB 整数度检查失败 → 调用方
    /// 交换 start/end。只影响流向标记，河形由对称 bell 决定不受影响，可接受。
    ///
    /// 【时序】river 在 order 220（MutatorPostTerrain）生成，早于 void(1400)。锚点计算用
    /// 多边形几何（不依赖 void），与 FindRoadExitCell patch（order 390）同套基础设施。
    ///
    /// 【守卫】<c>worldTile &lt; 0</c> 是防御性放行（防异常态），正常地图一律走接缝对齐
    /// （见 AGENTS.md 铁律：所有地图一视同仁）。
    /// </summary>
    static class Patch_TileMutatorWorker_River_GetMapEdgeNodes
    {
        internal static bool Prefix(TileMutatorWorker_River __instance, Map map, float angle,
            ref (Vector3 start, Vector3 end) __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return true; // 防御性：异常态放行原版

            var mapSize = map.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return true; // 几何异常放行

            // 下游边（angle 方向，对应下游邻居）+ 上游边（angle+180° 方向，对应上游邻居）。
            var downEdge = SeamlessPolygonGeometry.FindClosestEdgeByAngle(verts, mapSize, angle);
            var upEdge = SeamlessPolygonGeometry.FindClosestEdgeByAngle(verts, mapSize, angle + 180f);
            if (downEdge < 0 || upEdge < 0 || downEdge == upEdge) return true; // 退化（五边形顶点边界等）放行原版

            // 端点 = 边中点（offsetCells=0，河延伸到边）：depth map 网格 (Size+50)² 坐标 -25..Size+25，
            // 锚点在图内 → t∈[0,1] 深度覆盖正常，河恰好止于边中点。
            var down = EdgeMidpoint(verts, downEdge);
            var up = EdgeMidpoint(verts, upEdge);
            if (down == up) return true;

            // 返回 (start=上游, end=下游)：调用方 IsFlowingAToB(up, down, angle) 判断 (down-up) 方向 == angle，
            // 成立时 start=up/end=down（正确流向）。整数度容差不满足时调用方交换（无实质影响，见类注释）。
            __result = (up, down);
            return false; // 跳过原版（不再用随机直线交点）
        }

        /// <summary>多边形边中点（Vector3，y=0 平面）。</summary>
        private static Vector3 EdgeMidpoint(List<Vector2> verts, int edgeIdx)
        {
            var n = verts.Count;
            var v0 = verts[edgeIdx];
            var v1 = verts[(edgeIdx + 1) % n];
            var mid = (v0 + v1) * 0.5f;
            return new Vector3(mid.x, 0f, mid.y);
        }
    }
}
