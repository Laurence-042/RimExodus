using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块地图的静态注册表（基于直接邻居表，对称架构）。
    /// 提供"当前地图所有直接邻居的 footprint"查询，供 MapEdgeClipDrawer 裁剪与渲染使用。
    /// 不依赖 sourceMap/IsPocketMap，支持 A→B、B→A、B→C 对等处理。
    ///
    /// 阶段3：归属判定改为点在凸多边形内（当前地块所有权区域）。
    /// cell 在当前地块多边形内 → 归属当前地块；否则归属覆盖该格的邻居中多边形包含该格者。
    /// </summary>
    public static class SeamlessTileRegistry
    {
        /// <summary>
        /// 计算当前地图所有直接邻居的 footprint（当前地图坐标系矩形）。
        /// footprint = 邻居本地矩形 + 邻居相对偏移。
        /// </summary>
        public static List<CellRect> GetNeighborFootprints(Map currentMap)
        {
            var result = new List<CellRect>();
            foreach (var info in SeamlessTileGraph.GetAllNeighbors(currentMap))
            {
                var localRect = new CellRect(0, 0, info.map.Size.x, info.map.Size.z);
                var currentRect = localRect.MovedBy(info.offset.ToIntVec2);
                result.Add(currentRect);
            }
            return result;
        }

        /// <summary>
        /// 用多边形所有权规则反查当前地图上的一个格子属于哪个邻居。
        /// cell 在当前地块多边形内 → 归属当前地块（返回 false）。
        /// 否则遍历覆盖该格的直接邻居，取其多边形包含该格者（坐标转邻居本地后判定）。
        /// </summary>
        public static bool TryGetOwnerNeighbor(Map currentMap, IntVec3 currentCell, out Map ownerMap, out IntVec3 ownerLocalCell)
        {
            ownerMap = null;
            ownerLocalCell = default;
            if (currentMap == null) return false;

            var currentWorldTile = GetMapWorldTile(currentMap);
            if (currentWorldTile < 0) return false;

            // 当前地块多边形：cell 在内则归属当前地块。
            var currentVerts = SeamlessPolygonGeometry.BuildPolygonVertices(currentWorldTile, currentMap.Size.x);
            if (SeamlessPolygonGeometry.ContainsPoint(currentVerts, currentMap.Size.x, currentCell))
            {
                return false;
            }

            // 不在当前地块多边形内：查覆盖该格的邻居。
            foreach (var info in SeamlessTileGraph.GetAllNeighbors(currentMap))
            {
                var neighborLocal = currentCell - info.offset;
                if (!neighborLocal.InBounds(info.map)) continue;

                var neighborWorldTile = GetMapWorldTile(info.map);
                if (neighborWorldTile < 0) continue;
                var neighborVerts = SeamlessPolygonGeometry.BuildPolygonVertices(neighborWorldTile, info.map.Size.x);
                if (SeamlessPolygonGeometry.ContainsPoint(neighborVerts, info.map.Size.x, neighborLocal))
                {
                    ownerMap = info.map;
                    ownerLocalCell = neighborLocal;
                    return true;
                }
            }

            return false;
        }

        /// <summary>获取地图对应的世界 tile id。</summary>
        internal static int GetMapWorldTile(Map map)
        {
            if (map == null) return -1;
            // 阶段4前置：地块地图（MapParent_SeamlessTile）优先用 worldTile 字段（int 主键，稳定）。
            // 基础地图的 map.Tile 是真实 PlanetTile（隐式转 int == worldTile），两者一致。
            if (map.Parent is MapParent_SeamlessTile tileParent) return tileParent.worldTile;
            // 防御性排除（2026-08 收口，本方法是全部按 tileId 查找的统一入口——天气域绑定/宿主选举、
            // TileGraph 全局查找、BorderLookup、governor 源收集都经此，一处排除全链路生效）：
            // ①口袋图（原版口袋图与 VMF 载具内部图）parent.Tile 常为 0 等伪值——VMF 硬编码 Tile=0，
            //   会冒充真实地块污染邻居解析/天气域/传送落点（tile 0 恰为真实邻居或家园时）；
            // ②空间层图（Odyssey 轨道 SpaceMapParent 等）tileId 在其层自己的网格上，裸 int 与表面
            //   tileId 恒撞号（轨道层细分 5 < 表面 10，轨道 id 全落在表面范围内）——被收进表面天气域
            //   会读错群系，甚至反客为主当宿主把表面域天气锁死。与 SetupNativeParentMap /
            //   IsNativeFamily 的表面层守卫同族（勿删）。两类图对本 mod 而言"无表面世界 tile"。
            if (map.Parent is PocketMapParent) return -1;
            var tile = map.Tile;
            if (tile.LayerDef != RimWorld.PlanetLayerDefOf.Surface) return -1;
            return tile;
        }
    }
}
