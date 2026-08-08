using Verse;

namespace RimExodus
{
    /// <summary>
    /// 多边形地形铺设工具（阶段3：多边形裁切）。
    /// 用凸多边形扫描线填充，在地图内铺设可通行地形（多边形内）与虚空地形（多边形外）。
    /// 供 GenStep（口袋地块）和 SeamlessTileManager.EnsureAnchorVoidApplied（锚点家园 A）复用。
    /// </summary>
    public static class SeamlessTerrainFill
    {
        /// <summary>
        /// 按 worldTile 的多边形所有权区域铺设地形：
        /// 多边形内铺可通行地形（保留原地形或铺 Soil），多边形外铺 RimExodus_Void 虚空。
        /// </summary>
        public static void ApplyPolygonTerrain(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null)
            {
                Log.Error("[RimExodus] RimExodus_Void terrain not found, cannot apply polygon fill.");
                return;
            }

            var size = map.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, size.x);
            if (verts.Count == 0) return;

            // 扫描线填充：每行输出可通行区间 [xLeft, xRight]。
            SeamlessPolygonGeometry.ScanlineFill(verts, size.x, (z, xLeft, xRight) =>
            {
                for (var x = 0; x < size.x; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    var inPolygon = x >= xLeft && x <= xRight;
                    if (!inPolygon)
                    {
                        map.terrainGrid.SetTerrain(cell, voidDef);
                    }
                    // 多边形内的地形：口袋地块由 GenStep_SeamlessTile 先铺 Soil 再挖虚空；
                    // 锚点地图保留原生地形（只在外部铺虚空），不改内部。
                }
            });
        }
    }
}
