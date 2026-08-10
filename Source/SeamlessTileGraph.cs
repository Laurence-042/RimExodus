using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地块直接邻居表的统一查询入口。
    /// 屏蔽"锚点地图（普通 Map，邻居表存 MapComponent）"与"口袋地图（邻居表存 MapParent_SeamlessTile）"的差异。
    /// 所有上层逻辑（渲染/转移/交互）通过本类查邻居，不直接依赖 sourceMap/IsPocketMap。
    ///
    /// 阶段3：邻居方向改为基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 查询主键改为 worldTile（稳定，无角度歧义）。OppositeDirection 已移除（双向登记保证反向关系）。
    /// </summary>
    public static class SeamlessTileGraph
    {
        /// <summary>邻居查询结果：邻居 Map + 该邻居相对当前地图的偏移（绘制/坐标转换用）。</summary>
        public struct NeighborInfo
        {
            public Map map;
            public IntVec3 offset;
            public int worldTile;
        }

        /// <summary>获取 map 上指向 worldTile 的邻居连接信息。返回 false 表示无此邻居。</summary>
        public static bool TryGetNeighborLinkByWorldTile(Map map, int worldTile, out NeighborInfo info)
        {
            info = default;
            if (map == null) return false;

            NeighborLink link = null;
            if (map.Parent is MapParent_SeamlessTile pocketParent)
            {
                link = pocketParent.GetNeighborByWorldTile(worldTile);
            }
            else
            {
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager != null) link = manager.GetNeighborByWorldTile(worldTile);
            }

            if (link?.neighbor == null) return false;

            var neighborMap = link.neighbor.Map;
            if (neighborMap == null || neighborMap.Disposed) return false;

            info = new NeighborInfo
            {
                map = neighborMap,
                offset = link.offset,
                worldTile = link.worldTile
            };
            return true;
        }

        /// <summary>获取 map 的所有有效直接邻居（含各自相对偏移）。</summary>
        public static List<NeighborInfo> GetAllNeighbors(Map map)
        {
            var result = new List<NeighborInfo>();
            PopulateNeighbors(map, result);
            return result;
        }

        /// <summary>把 map 的所有有效直接邻居填入已有的列表（避免每帧分配）。调用方负责 Clear。</summary>
        public static void PopulateNeighbors(Map map, List<NeighborInfo> result)
        {
            if (map == null || result == null) return;

            List<NeighborLink> links;
            if (map.Parent is MapParent_SeamlessTile pocketParent)
            {
                links = pocketParent.neighbors;
            }
            else
            {
                var manager = map.GetComponent<SeamlessTileManager>();
                links = manager?.neighbors;
            }

            if (links == null) return;

            foreach (var link in links)
            {
                if (link?.neighbor == null) continue;
                var neighborMap = link.neighbor.Map;
                if (neighborMap == null || neighborMap.Disposed) continue;
                result.Add(new NeighborInfo
                {
                    map = neighborMap,
                    offset = link.offset,
                    worldTile = link.worldTile
                });
            }
        }

        /// <summary>判断 b 是否为 a 的直接邻居（对称：a 是 b 的邻居 ⟺ b 是 a 的邻居）。</summary>
        public static bool AreNeighbors(Map a, Map b)
        {
            if (a == null || b == null) return false;
            foreach (var info in GetAllNeighbors(a))
            {
                if (info.map == b) return true;
            }
            return false;
        }

        /// <summary>
        /// 判断 map 是否为锚点地图（玩家家园，IsPlayerHome）。
        /// 阶段4前置：改基础地图后 IsPocketMap 恒 false，改用 IsPlayerHome 区分家园与地块。
        /// 地块地图（MapParent_SeamlessTile）不是锚点；原生家园地图是锚点。
        /// </summary>
        public static bool IsAnchorMap(Map map)
        {
            return map != null && map.IsPlayerHome && !(map.Parent is MapParent_SeamlessTile);
        }

        /// <summary>
        /// 获取 map 用于 skyManager/weatherManager 共享的锚点地图（玩家家园）。
        /// 家园地图自身返回自身；地块地图返回当前玩家家园地图（用于天气连续性共享）。
        /// 阶段4前置：不再依赖 sourceMap（基础地图无此字段），改用 Find.CurrentMap 的家园查找。
        /// </summary>
        public static Map GetAnchorMap(Map map)
        {
            if (map == null) return null;
            // 家园地图自身即锚点。
            if (map.IsPlayerHome && !(map.Parent is MapParent_SeamlessTile)) return map;
            // 地块地图：找当前玩家家园（任一 PlayerHome 且非地块）。
            foreach (var m in Find.Maps)
            {
                if (m.IsPlayerHome && !(m.Parent is MapParent_SeamlessTile)) return m;
            }
            return null;
        }

        /// <summary>
        /// 全局查询：指定 worldTile 是否已有任意已加载的地图（含锚点和所有口袋），不限于直接邻居。
        /// 遍历 Find.Maps（含所有已加载地图），用 SeamlessTileRegistry.GetMapWorldTile 统一取 worldTile。
        /// 用于预加载去重：A 和 C 虽非直接邻居（隔了 B），但 A 的地图已存在，从 C 预加载 A 的 worldTile 时应复用而非重复生成。
        /// </summary>
        public static bool TryGetMapByWorldTile(int worldTile, out Map existing)
        {
            existing = null;
            if (worldTile < 0) return false;
            foreach (var map in Find.Maps)
            {
                if (SeamlessTileRegistry.GetMapWorldTile(map) == worldTile)
                {
                    existing = map;
                    return true;
                }
            }
            return false;
        }
    }
}
