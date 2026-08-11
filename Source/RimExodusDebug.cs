using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// RimExodus 调试日志中枢。
    /// 集中承载几何/噪声诊断日志的格式化逻辑，避免分散在各 genStep/provider 内联。
    ///
    /// 日志受 RimExodusMod.Settings.verboseLogging 控制；debug dirt/water 模式下强制输出（即使 verbose 关闭）。
    /// 所有日志带 <c>[RimExodusDebug]</c> 前缀，便于在日志窗口过滤。
    /// </summary>
    public static class RimExodusDebug
    {
        /// <summary>
        /// 输出一个地块在 debug dirt/water 模式下的完整噪声采样信息。
        /// 供 GenStep_DebugDirtWater.Generate 调用，让用户精确判断每个地块使用的噪声范围、tileOrigin。
        /// </summary>
        /// <param name="worldTile">世界地块 id。</param>
        /// <param name="mapSize">地图边长（格）。</param>
        /// <param name="tileOrigin">该 tile 在全局平面坐标系的原点（噪声采样用）。</param>
        /// <param name="threshold">dirt/water 二值化阈值。</param>
        /// <param name="polygonVerts">多边形顶点（局部 2D，来自 BuildPolygonVertices）。</param>
        /// <param name="cornerSamples">四角 + 中心的全局平面采样坐标 + 噪声值。</param>
        public static void LogDirtWaterTile(int worldTile, int mapSize, Vector2 tileOrigin,
            float threshold, List<Vector2> polygonVerts, List<CornerSample> cornerSamples)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RimExodusDebug] ===== DebugDirtWater tile =====");
            sb.AppendLine($"  tile={worldTile} mapSize={mapSize} noiseGenType={RimExodusMod.Settings?.noiseGenType} seed={SeamlessNoiseProvider.FixedSeed} threshold={threshold}");
            sb.AppendLine($"  tileOrigin=({tileOrigin.x:F1},{tileOrigin.y:F1}) (全局平面坐标原点)");
            sb.Append($"  polygonVerts({polygonVerts.Count})=[");
            for (var i = 0; i < polygonVerts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"({polygonVerts[i].x:F1},{polygonVerts[i].y:F1})");
            }
            sb.AppendLine("]");

            // 四角 + 中心的全局平面采样坐标与噪声值
            sb.AppendLine("  corner samples (cell offset → global plane coord → noise → elev):");
            foreach (var cs in cornerSamples)
            {
                sb.AppendLine($"    cell({cs.cellX},{cs.cellZ}) off=({cs.offX:+0.0;-0.0},{cs.offZ:+0.0;-0.0}) → global=({cs.globalX:F2},{cs.globalZ:F2}) noise={cs.noise:F4} elev={cs.elev:F2}");
            }

            // 全局坐标范围诊断：相邻 tile 的全局坐标范围应有重叠区（共享边连续）。
            if (cornerSamples.Count >= 4)
            {
                var minX = float.PositiveInfinity; var maxX = float.NegativeInfinity;
                var minZ = float.PositiveInfinity; var maxZ = float.NegativeInfinity;
                foreach (var cs in cornerSamples)
                {
                    if (cs.globalX < minX) minX = cs.globalX;
                    if (cs.globalX > maxX) maxX = cs.globalX;
                    if (cs.globalZ < minZ) minZ = cs.globalZ;
                    if (cs.globalZ > maxZ) maxZ = cs.globalZ;
                }
                sb.AppendLine($"  global bbox: x[{minX:F2}..{maxX:F2}] z[{minZ:F2}..{maxZ:F2}]");
                sb.AppendLine($"  (相邻 tile 的 global bbox 应有重叠区，否则 tileOrigin 传播错误)");
            }

            Log.Message(sb.ToString());
        }

        /// <summary>四角/中心采样点的完整信息（cell 坐标、全局平面坐标、噪声值、elevation）。</summary>
        public struct CornerSample
        {
            public int cellX;
            public int cellZ;
            public float offX;
            public float offZ;
            public float globalX;
            public float globalZ;
            public double noise;
            public float elev;
        }
    }
}
