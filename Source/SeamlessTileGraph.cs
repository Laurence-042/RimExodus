using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地块直接邻居表的统一查询入口。
    /// 屏蔽"锚点地图（普通 Map，邻居表存 MapComponent）"与"口袋地图（邻居表存 MapParent_SeamlessTile）"的差异。
    /// 所有上层逻辑（渲染/转移/交互）通过本类查邻居，不直接依赖 sourceMap/IsPocketMap。
    /// </summary>
    public static class SeamlessTileGraph
    {
        /// <summary>邻居查询结果：邻居 Map + 该邻居相对当前地图的偏移（绘制/坐标转换用）。</summary>
        public struct NeighborInfo
        {
            public Map map;
            public IntVec3 offset;
        }

        /// <summary>获取 map 指定方向的邻居连接信息。返回 false 表示该方向无邻居。</summary>
        public static bool TryGetNeighborLink(Map map, int dir, out NeighborInfo info)
        {
            info = default;
            if (map == null || dir < 0 || dir >= 6)
            {
                return false;
            }

            NeighborLink link = null;
            if (map.Parent is MapParent_SeamlessTile pocketParent)
            {
                link = pocketParent.GetNeighborInDirection(dir);
            }
            else
            {
                // 锚点地图（普通 Map）：邻居表存于 SeamlessTileManager MapComponent。
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager != null)
                {
                    link = manager.GetNeighborInDirection(dir);
                }
            }

            if (link?.neighbor == null)
            {
                return false;
            }

            var neighborMap = link.neighbor.Map;
            if (neighborMap == null || neighborMap.Disposed)
            {
                return false;
            }

            info = new NeighborInfo { map = neighborMap, offset = link.offset };
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
            if (map == null || result == null)
            {
                return;
            }

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

            if (links == null)
            {
                return;
            }

            foreach (var link in links)
            {
                if (link?.neighbor == null)
                {
                    continue;
                }
                var neighborMap = link.neighbor.Map;
                if (neighborMap == null || neighborMap.Disposed)
                {
                    continue;
                }
                result.Add(new NeighborInfo { map = neighborMap, offset = link.offset });
            }
        }

        /// <summary>判断 b 是否为 a 的直接邻居（对称：a 是 b 的邻居 ⟺ b 是 a 的邻居）。</summary>
        public static bool AreNeighbors(Map a, Map b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            foreach (var info in GetAllNeighbors(a))
            {
                if (info.map == b)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断 map 是否为锚点地图（非口袋地图，即家园 A 或未来的旅行宿主）。</summary>
        public static bool IsAnchorMap(Map map)
        {
            return map != null && !map.IsPocketMap;
        }

        /// <summary>
        /// 获取 map 用于 sourceMap/skyManager 共享的锚点地图。
        /// 锚点地图自身返回自身；口袋地图返回其 sourceMap（扁平化：始终指向锚点）。
        /// </summary>
        public static Map GetAnchorMap(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (!map.IsPocketMap)
            {
                return map;
            }
            // 口袋地图的 sourceMap 即锚点（扁平化：所有地块 sourceMap 指向锚点）。
            if (map.Parent is PocketMapParent pocketParent)
            {
                return pocketParent.sourceMap;
            }
            return null;
        }

        /// <summary>计算方向 dir 的反方向（0↔3, 1↔4, 2↔5）。</summary>
        public static int OppositeDirection(int dir)
        {
            return (dir + 3) % 6;
        }
    }
}
