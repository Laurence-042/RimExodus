using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 坐标投影 util（供 void 多边形几何用）。
    ///
    /// 注意：此类只提供 void 体系需要的"球面顶点 → 2D 方向"投影（VertexToDir2D）。
    /// 噪声采样不再用球面坐标（见 <see cref="SeamlessNoiseProvider"/> 全局平面坐标方案），
    /// 故本类不提供任何 cell→球面 的映射。
    ///
    /// 唯一持有 <c>Find.WorldGrid.GetTileCenter</c> + <c>WorldRendererUtility.GetTangentsToPlanet</c> 调用的地方。
    /// </summary>
    public static class TileProjection
    {
        /// <summary>
        /// 地块中心的切平面基。anchor = 球面位置向量；first/second = 切平面两个正交单位向量；
        /// latLong = (纬度, 经度) 弧度。值类型，避免按 tile 分配堆对象。
        /// </summary>
        public struct TileBasis
        {
            public Vector3 anchor;
            public Vector3 first;
            public Vector3 second;
            public Vector2 latLong;
        }

        private static readonly Dictionary<int, TileBasis> basisCache = new();

        /// <summary>
        /// 取 worldTile 中心的切平面基（带进程级缓存）。
        /// 唯一的 <c>GetTileCenter</c> + <c>GetTangentsToPlanet</c> 调用点。
        /// </summary>
        public static TileBasis GetBasis(int worldTile)
        {
            if (!basisCache.TryGetValue(worldTile, out var basis))
            {
                basis = new TileBasis
                {
                    anchor = Find.WorldGrid.GetTileCenter(worldTile)
                };
                WorldRendererUtility.GetTangentsToPlanet(basis.anchor, out basis.first, out basis.second);
                var n = basis.anchor.normalized;
                basis.latLong = new Vector2(Mathf.Asin(n.y), Mathf.Atan2(n.z, n.x));
                basisCache[worldTile] = basis;
            }
            return basis;
        }

        /// <summary>
        /// 球面顶点 → 局部 2D 方向（用于 void 多边形顶点投影，内切圆模型）。
        ///
        /// 公式：<c>dx = -Dot(d, second), dz = Dot(d, first)</c>，再归一化。
        /// 返回的单位向量乘 0.5S 即得内切圆上的多边形顶点（见 SeamlessPolygonGeometry.BuildPolygonVertices）。
        /// </summary>
        public static Vector2 VertexToDir2D(TileBasis basis, Vector3 worldVertex)
        {
            var d = worldVertex - basis.anchor;
            var dir = new Vector2(
                -Vector3.Dot(d, basis.second),
                Vector3.Dot(d, basis.first));
            dir.Normalize();
            return dir;
        }
    }
}
