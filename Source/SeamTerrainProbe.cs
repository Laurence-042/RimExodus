using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝带地形诊断探针（开发用，受 <see cref="RimExodusSettings.seamOverrideDiag"/> 开关控制）。
    ///
    /// 在地图生成各阶段（每个关键 genStep 边界 + snapshot 拍摄前后 + SeamOverride 卷积前后）取样
    /// "接缝带"格的地形分布，输出一条精简日志，用于精确定位 snapshot 内容与 topGrid 漂移的根因。
    ///
    /// "接缝带"定义与 <see cref="SeamlessSeamOverride"/> 的混合带一致：用
    /// <see cref="SeamlessPolygonGeometry.ComputeVoidBand"/> 取距 void 边界 bandWidth 格的环形带，
    /// bandWidth 取 <see cref="RimExodusSettings.seamOverrideRatio"/> 对应值，与 SeamOverride 同源。
    /// </summary>
    public static class SeamTerrainProbe
    {
        /// <summary>
        /// 记录 map 当前 topGrid 在接缝带的地形分布。
        /// 在 genStep 边界调用，追踪接缝带地形随生成阶段的演变。
        /// </summary>
        public static void LogBandTerrain(Map map, int worldTile, string stageLabel)
        {
            if (!Enabled) return;
            if (map == null || worldTile < 0) return;

            var bandCells = GetBandCells(map, worldTile);
            if (bandCells == null || bandCells.Count == 0) return;

            var tally = TallyFromTopGrid(map, bandCells);
            Log.Message($"[RimExodus-SeamProbe] stage={stageLabel} wt={worldTile} bandCells={bandCells.Count} | {FormatTally(tally)}");
        }

        /// <summary>
        /// 记录 snapshot 数组在接缝带的地形分布。
        /// 在 snapshot 拍摄后立即调用，验证 snapshot 内容与当时 topGrid 一致。
        /// </summary>
        public static void LogSnapshotBand(Map map, int worldTile, TerrainDef[] snapshot, string stageLabel)
        {
            if (!Enabled) return;
            if (map == null || worldTile < 0 || snapshot == null) return;

            var bandCells = GetBandCells(map, worldTile);
            if (bandCells == null || bandCells.Count == 0) return;

            var tally = TallyFromArray(snapshot, map.cellIndices, bandCells);
            Log.Message($"[RimExodus-SeamProbe] stage={stageLabel}-SNAPSHOT wt={worldTile} bandCells={bandCells.Count} | {FormatTally(tally)}");
        }

        /// <summary>
        /// 记录整个 snapshot 数组的地形分布（top 5，按计数降序），用于判断拍摄时 topGrid 整体状态
        /// （如"全是 GrasslandSoil"= 拍在 Terrain 之前；"含沙水"= 拍在海岸填充之后）。
        /// </summary>
        public static void LogSnapshotFullMap(Map map, int worldTile, TerrainDef[] snapshot, string stageLabel)
        {
            if (!Enabled) return;
            if (map == null || worldTile < 0 || snapshot == null) return;

            var tally = new Dictionary<TerrainDef, int>();
            for (var i = 0; i < snapshot.Length; i++)
            {
                var t = snapshot[i];
                if (t == null) continue;
                tally.TryGetValue(t, out var c);
                tally[t] = c + 1;
            }
            // 只输出 top 5（避免日志过长）。
            var top = new List<KeyValuePair<TerrainDef, int>>(tally);
            top.Sort((a, b) =>
            {
                var c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return string.Compare(a.Key.defName, b.Key.defName, System.StringComparison.Ordinal);
            });
            var sb = new StringBuilder();
            var isPocket = map.Parent is MapParent_SeamlessTile;
            sb.Append($"[RimExodus-SeamProbe] stage={stageLabel}-FULLMAP wt={worldTile} map={map.uniqueID} ({(isPocket ? "POCKET" : "ANCHOR")}) total={snapshot.Length} | ");
            var n = System.Math.Min(5, top.Count);
            for (var i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(top[i].Key.defName).Append('=').Append(top[i].Value);
            }
            Log.Message(sb.ToString());
        }

        /// <summary>
        /// 记录六边形外格的 topGrid 地形分布（不依赖 void，可在 void 铺设前的 genStep 阶段使用）。
        /// 用于追踪 Coast mutator / CoastalEdgeFill 在六边形外的实际铺水情况——
        /// snapshot 需要六边形外的真实海岸地形，但 CoastalEdgeFill 只铺六边形内。
        /// </summary>
        public static void LogOutsidePolygonTerrain(Map map, int worldTile, string stageLabel)
        {
            if (!Enabled) return;
            if (map == null || worldTile < 0) return;

            var size = map.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, size.x);
            if (verts.Count < 3) return;

            var tally = new Dictionary<TerrainDef, int>();
            var total = 0;
            var topGrid = map.terrainGrid.topGrid;
            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (SeamlessPolygonGeometry.IsCellInPolygon(verts, size.x, cell)) continue; // 只统计六边形外
                    var t = topGrid[map.cellIndices.CellToIndex(cell)];
                    if (t == null) continue;
                    tally.TryGetValue(t, out var c);
                    tally[t] = c + 1;
                    total++;
                }
            }
            Log.Message($"[RimExodus-SeamProbe] stage={stageLabel}-OUTSIDE wt={worldTile} map={map.uniqueID} outsideCells={total} | {FormatTally(tally)}");
        }

        /// <summary>探针是否启用（复用 seamOverrideDiag 开关）。</summary>
        private static bool Enabled => RimExodusMod.Settings?.seamOverrideDiag ?? false;

        /// <summary>
        /// 取 map 的接缝带格集合（与 SeamOverride 混合带同源：ComputeVoidBand）。
        /// 复用静态列表避免每次分配（探针只在 diag 开启时跑，低频）。
        /// </summary>
        private static List<IntVec3> GetBandCells(Map map, int worldTile)
        {
            var ratio = RimExodusMod.Settings?.seamOverrideRatio ?? 0.25f;
            if (ratio <= 0f) return null;
            var bandWidth = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(ratio * map.Size.x * 0.5f));

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            var band = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeVoidBand(map, bandWidth, neighborWorldTiles, band, null);
            if (band.Count == 0) return null;

            var cells = new List<IntVec3>(band.Count);
            foreach (var cell in band.Keys) cells.Add(cell);
            return cells;
        }

        /// <summary>从 topGrid 统计接缝带格的地形分布。</summary>
        private static Dictionary<TerrainDef, int> TallyFromTopGrid(Map map, List<IntVec3> bandCells)
        {
            var tally = new Dictionary<TerrainDef, int>();
            var topGrid = map.terrainGrid.topGrid;
            var cellIndices = map.cellIndices;
            foreach (var cell in bandCells)
            {
                var t = topGrid[cellIndices.CellToIndex(cell)];
                if (t == null) continue;
                tally.TryGetValue(t, out var c);
                tally[t] = c + 1;
            }
            return tally;
        }

        /// <summary>从 snapshot 数组统计接缝带格的地形分布。</summary>
        private static Dictionary<TerrainDef, int> TallyFromArray(TerrainDef[] snapshot, CellIndices cellIndices, List<IntVec3> bandCells)
        {
            var tally = new Dictionary<TerrainDef, int>();
            foreach (var cell in bandCells)
            {
                var idx = cellIndices.CellToIndex(cell);
                if (idx < 0 || idx >= snapshot.Length) continue;
                var t = snapshot[idx];
                if (t == null) continue;
                tally.TryGetValue(t, out var c);
                tally[t] = c + 1;
            }
            return tally;
        }

        /// <summary>把地形分布格式化为 "defName=count, defName=count"（按计数降序）。</summary>
        private static string FormatTally(Dictionary<TerrainDef, int> tally)
        {
            if (tally.Count == 0) return "(empty)";
            var sb = new StringBuilder();
            // 按 count 降序，再按 defName 升序稳定排序。
            var items = new List<KeyValuePair<TerrainDef, int>>(tally);
            items.Sort((a, b) =>
            {
                var c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return string.Compare(a.Key.defName, b.Key.defName, System.StringComparison.Ordinal);
            });
            var first = true;
            foreach (var kv in items)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key.defName).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
