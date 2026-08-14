using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 河流接缝对齐：两个 patch 配合，把河端点从"随机直线的图外交点"替换为
    /// "接缝边中点沿外法向延伸到方形边"，并用压弯窗口保住接缝对齐。
    ///
    /// 【为什么端点要延伸到方形边（而非停在边中点）】
    /// 河端点若停在六边形边中点，河源头后方（边中点到方形边 ~15-40 格）会出现无水走廊——
    /// order 200 patch 已清掉六边形外岩石、void 1400 才铺，道路 A*（order 390）的第一级
    /// NoPassClosedDoorsOrWater（一切水=硬不可走）经走廊直接成功 → 路**必然**绕经河源头
    /// （实测：三叉河地图的路绕到北河口再大转弯）。端点延伸到方形边后，臂+方形边把地图
    /// 真正分割成两半（原版语义），A* 无任何绕行路径 → 第二级正常涉水过河+铺桥。
    ///
    /// 【尽量用原版生成（避免宽度/水深突变）】
    /// 端点外延后，深度场（GenerateDepthMaps 按 GetTValue∈[0,1] 判定）、宽度噪声
    /// （GetRiverWidthAt）、水深分级（RiverTerrainAt）全部由**原版逻辑**自然延伸到走廊段——
    /// 不自铺水、无人工水带，宽度/水深连续渐变，SeamOverride 混合无突变源。
    ///
    /// 【压弯窗口（GetDisplacedPoint Postfix）——保住接缝对齐】
    /// 原版弯曲偏移 = Perlin × 幅度 × bell(t)，bell 在端点=0 → 河端点位置固定。
    /// 端点外延后接缝处（边中点）t=tSeam≠0 → bell≠0 → 两侧地图噪声 seed 独立、
    /// 接缝漂移可达 ±19-32 格。Postfix 把偏移乘窗口：走廊段（t<tSeamStart 或 t>tSeamEnd）
    /// 偏移=0（直线），图内渐升到 1（原版行为），接缝处有效 bell=0 → 对齐保持。
    /// 窗口参数 = 各边中点在本节点线段上的投影 t（按边中点缓存计算，非硬编码）。
    ///
    /// 【调用方兼容】GetMapEdgeNodes 的三个调用方（普通 River 干流 / Confluence 干流 /
    /// 流入流出支流）都按 IsFlowingAToB 的整数度容差决定 start/end 顺序——角度差几度时
    /// 交换 start/end，只影响流向标记，河形由对称 bell 决定不受影响。
    ///
    /// 【时序】river 在 order 220（MutatorPostTerrain）生成，早于 void(1400)。走廊段的
    /// 河水在 1400 被 void 覆盖（视觉上河止于六边形边），snapshot（void 前 Clone）含水——
    /// SeamOverride 的 Convolve3x3 跳过 IsWater 格，走廊水不污染混合带。
    ///
    /// 【守卫】<c>worldTile &lt; 0</c> 是防御性放行（防异常态），正常地图一律走接缝对齐
    /// （见 AGENTS.md 铁律：所有地图一视同仁）。
    /// </summary>
    static class Patch_TileMutatorWorker_River_GetMapEdgeNodes
    {
        /// <summary>
        /// 当前生成地图的 worldTile/mapSize 上下文（供 <see cref="Patch_TileMutatorWorker_River_GetDisplacedPoint"/>
        /// 查边中点）。生成期主线程单地图顺序执行，static 安全；GetMapEdgeNodes 必先于
        /// GetDisplacedPoint（GenerateRiverGraph 先于 GenerateDepthMaps）被调用。
        /// </summary>
        internal static int currentWorldTile = -1;
        internal static int currentMapSize;

        /// <summary>边中点缓存（键：worldTile；进程级，同 BuildPolygonVertices 缓存语义）。</summary>
        private static readonly Dictionary<int, List<Vector2>> edgeMidCache = new();

        internal static List<Vector2> GetEdgeMidpoints(int worldTile, int mapSize)
        {
            if (edgeMidCache.TryGetValue(worldTile, out var cached)) return cached;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            var mids = new List<Vector2>(verts.Count);
            for (var j = 0; j < verts.Count; j++)
                mids.Add((verts[j] + verts[(j + 1) % verts.Count]) * 0.5f);
            edgeMidCache[worldTile] = mids;
            return mids;
        }

        internal static bool Prefix(TileMutatorWorker_River __instance, Map map, float angle,
            ref (Vector3 start, Vector3 end) __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return true; // 防御性：异常态放行原版

            var mapSize = map.Size.x;
            var mids = GetEdgeMidpoints(worldTile, mapSize);
            if (mids.Count < 3) return true; // 几何异常放行

            // 记录上下文（GetDisplacedPoint Postfix 用）。
            currentWorldTile = worldTile;
            currentMapSize = mapSize;

            // 下游边 + 上游边：世界邻居 heading 匹配 + 邻居→边精确映射。
            // angle = far→near 流向 ≈ me→near（河流链近似直，原版同假设）→ 匹配到下游邻居；
            // angle+180 → 上游邻居。勿用本地边中点角度近似——六边形边方向离散 60° + 投影扭曲
            // 会把"正南流向"的河锚到 SE/SW 边（南北河被画成东北-西南，下方连不上）。
            var downEdge = SeamlessPolygonGeometry.FindEdgeByWorldHeading(worldTile, angle);
            var upEdge = SeamlessPolygonGeometry.FindEdgeByWorldHeading(worldTile, angle + 180f);
            if (downEdge < 0 || upEdge < 0 || downEdge >= mids.Count || upEdge >= mids.Count) return true;
            if (downEdge == upEdge) return true; // 退化（顶点边界等）放行原版

            // 端点 = 边中点沿外法向延伸到方形矩形边界（解析 slab 求交）。
            // 深度网格覆盖 -25..Size+25，端点在方形边上 t∈[0,1] 完整覆盖走廊段。
            var center = new Vector2(mapSize * 0.5f, mapSize * 0.5f);
            var down = ExtendToRectEdge(mids[downEdge], center, mapSize);
            var up = ExtendToRectEdge(mids[upEdge], center, mapSize);
            if (down == up) return true;

            // 返回 (start=上游, end=下游)：调用方 IsFlowingAToB(up, down, angle) 判断 (down-up) 方向 == angle，
            // 成立时 start=up/end=down（正确流向）。整数度容差不满足时调用方交换（无实质影响，见类注释）。
            __result = (new Vector3(up.x, 0f, up.y), new Vector3(down.x, 0f, down.y));
            return false; // 跳过原版（不再用随机直线交点）
        }

        /// <summary>
        /// 从 origin 沿外法向（origin-center 方向）射线求与方形矩形 (0,0)-(mapSize,mapSize)
        /// 边界的交点（slab 算法，取最小正 k）。保证河延伸到方形边（A* 屏障完整）。
        /// </summary>
        private static Vector2 ExtendToRectEdge(Vector2 origin, Vector2 center, int mapSize)
        {
            var dir = (origin - center).normalized;
            var k = float.MaxValue;
            if (dir.x > 1e-6f) k = Mathf.Min(k, (mapSize - origin.x) / dir.x);
            else if (dir.x < -1e-6f) k = Mathf.Min(k, -origin.x / dir.x);
            if (dir.y > 1e-6f) k = Mathf.Min(k, (mapSize - origin.y) / dir.y);
            else if (dir.y < -1e-6f) k = Mathf.Min(k, -origin.y / dir.y);
            if (k == float.MaxValue || k < 0f) return origin;
            return origin + dir * k;
        }
    }

    /// <summary>
    /// 压弯窗口：Postfix <c>GetDisplacedPoint</c>（protected virtual，返回 Vector2）——
    /// 把原版弯曲偏移乘窗口：走廊段（六边形外延伸段）偏移归零、接缝处（边中点）有效
    /// bell=0 → 接缝两侧河都是直线段 → 对齐；图内深处窗口=1，原版弯曲行为不变。
    /// 详见 <see cref="Patch_TileMutatorWorker_River_GetMapEdgeNodes"/> 类注释。
    /// protected 方法不可 nameof（外部不可访问），用字符串声明；无重载。
    /// Confluence 未 override 此方法，全部调用进基类方法体，均命中本 patch。
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_River), "GetDisplacedPoint")]
    static class Patch_TileMutatorWorker_River_GetDisplacedPoint
    {
        /// <summary>窗口渐变带宽（占线段全长比例）。从接缝直线到原版弯曲的过渡区。</summary>
        private const float RiseRatio = 0.15f;

        internal static void Postfix(RiverNode riverNode, float t, ref Vector2 __result)
        {
            var worldTile = Patch_TileMutatorWorker_River_GetMapEdgeNodes.currentWorldTile;
            if (worldTile < 0) return; // 上下文未初始化（异常）放行
            var mids = Patch_TileMutatorWorker_River_GetMapEdgeNodes.GetEdgeMidpoints(
                worldTile, Patch_TileMutatorWorker_River_GetMapEdgeNodes.currentMapSize);

            var s = new Vector2(riverNode.start.x, riverNode.start.z);
            var e = new Vector2(riverNode.end.x, riverNode.end.z);
            var d = e - s;
            var lenSqr = d.sqrMagnitude;
            if (lenSqr < 1e-6f) return;

            // 两侧边中点在线段上的投影参数：start 侧取最大投影 tSeamStart，end 侧取最小 tSeamEnd。
            var tSeamStart = 0f;
            var tSeamEnd = 1f;
            foreach (var m in mids)
            {
                var tm = Vector2.Dot(m - s, d) / lenSqr;
                if (tm < 0.5f)
                {
                    if (tm > tSeamStart) tSeamStart = tm;
                }
                else if (tm < tSeamEnd) tSeamEnd = tm;
            }

            // 窗口：走廊段 0 → 渐升 → 图内 1。
            var w = Mathf.Clamp01((t - tSeamStart) / RiseRatio) * Mathf.Clamp01((tSeamEnd - t) / RiseRatio);
            if (w >= 1f) return; // 图内深处：原版偏移原样。

            // __result = 直线插值点 + (原偏移) × w。
            var p = s + d * t;
            __result = p + (__result - p) * Mathf.Clamp01(w);
        }
    }
}
