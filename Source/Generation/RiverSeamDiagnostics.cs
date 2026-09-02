using System.Collections.Generic;
using System.Text;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 河流接缝诊断（2026-08-30 "河还是歪的"排查，verbose 门控只读诊断，勿用于逻辑）：
    /// 对每个 river-link 邻居边，扫描缝中点邻域的实际水格（TerrainAt IsWater），把水带位置
    /// 分解到【边法向】（中点→地图中心）与【边切向】两个分量上报——
    ///
    /// 判读：理想垂直接近 = 水带切向中心 ≈ 中点（offset 切向 ≈ 0）、法向从 0 向内延伸
    /// halfWidth、切向宽度 ≈ 河宽。歪 = 切向 offset 显著非 0（河道没穿过中点）或水带走向
    /// 与法向夹角大（斜穿）。preMix 与 postMix 各报一次可区分"生成期就歪"vs"混合期弄歪"。
    /// </summary>
    internal static class RiverSeamDiagnostics
    {
        /// <summary>采样半径（切比雪夫，覆盖接缝带 3 圈 + 余量）。</summary>
        private const int ScanRadius = 10;

        internal static void Report(Map map, int worldTile, string phase)
        {
            if (!(RimExodusLog.Enabled(RimExodusLogModule.Generation))) return;

            var mapSize = map.Size.x;
            var edgeCount = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize).Count;

            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, neighbors);
            var sb = new StringBuilder();
            sb.Append($"[RimExodus] River seam diag [{phase}]: map={map.uniqueID} tile={worldTile}");

            var any = false;
            for (var j = 0; j < neighbors.Count && j < edgeCount; j++)
            {
                if (Find.WorldGrid.GetRiverDef(worldTile, neighbors[j]) == null) continue;
                any = true;
                // 探测中心 = 本图实际穿越点记录（river Prefix 写入；无记录回落对称哈希）。
                var rec = map.GetComponent<SeamlessRiverCrossings>();
                var mid = rec != null && rec.crossings.TryGetValue(neighbors[j].tileId, out var recorded)
                    ? recorded
                    : SeamlessPolygonGeometry.SeamCrossingPoint(worldTile, j, mapSize, SeamlessPolygonGeometry.SeamLink.River);
                var normal = -SeamlessPolygonGeometry.EdgeOutwardNormal(
                    SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize), j); // 内法线（向图内）
                var tangent = new Vector2(normal.y, -normal.x); // 边切向

                // 扫中点邻域水格，收集法向/切向坐标（相对中点）。
                var nCoords = new List<float>();
                var tCoords = new List<float>();
                var mc = new IntVec3(Mathf.RoundToInt(mid.x), 0, Mathf.RoundToInt(mid.y));
                for (var dx = -ScanRadius; dx <= ScanRadius; dx++)
                {
                    for (var dz = -ScanRadius; dz <= ScanRadius; dz++)
                    {
                        var cell = new IntVec3(mc.x + dx, 0, mc.z + dz);
                        if (!cell.InBounds(map)) continue;
                        var terrain = map.terrainGrid.TerrainAt(cell);
                        if (terrain == null || !terrain.IsWater) continue;
                        var rel = new Vector2(cell.x - mid.x, cell.z - mid.y);
                        nCoords.Add(Vector2.Dot(rel, normal));
                        tCoords.Add(Vector2.Dot(rel, tangent));
                    }
                }

                if (nCoords.Count == 0)
                {
                    sb.Append($" | edge{j}->tile{neighbors[j]} crossing=({mid.x:F0},{mid.y:F0}) NO WATER in r{ScanRadius}");
                    continue;
                }
                var nMin = Mathf.Min(nCoords.ToArray()); var nMax = Mathf.Max(nCoords.ToArray());
                var tMin = Mathf.Min(tCoords.ToArray()); var tMax = Mathf.Max(tCoords.ToArray());
                var tAvg = 0f; foreach (var v in tCoords) tAvg += v; tAvg /= tCoords.Count;
                // 水带走向 = 主轴方向与法向的夹角（对 n/t 坐标做 2×2 协差的斜率近似）。
                // normal = 内法线：normalRange 正值 = 向图内的水带纵深。
                sb.Append($" | edge{j}->tile{neighbors[j]} crossing=({mid.x:F0},{mid.y:F0}) water={nCoords.Count} " +
                    $"normalRange=[{nMin:F0},{nMax:F0}] tangentRange=[{tMin:F0},{tMax:F0}] tAvg={tAvg:F1} " +
                    $"tWidth={tMax - tMin:F0}");
            }
            if (any) Log.Message(sb.ToString());
        }
    }
}
