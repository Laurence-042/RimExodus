using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地块直接邻居表的统一查询入口。
    /// 存储载体差异（地块图 = MapParent_SeamlessTile 字段 / 原生 parent 图 = SeamlessTileManager
    /// 组件）由 <see cref="SeamlessMapData"/> 屏蔽；本类在其上叠加邻接查询语义（休眠过滤等）。
    /// 所有上层逻辑（渲染/转移/交互）通过本类查邻居。
    ///
    /// 阶段3：邻居方向改为基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 有目标图时按地图实例查找；预加载时按带层的地块键查找。
    /// </summary>
    public static class SeamlessTileGraph
    {
        /// <summary>邻居查询结果：邻居 Map + 该邻居相对当前地图的偏移（绘制/坐标转换用）。</summary>
        public struct NeighborInfo
        {
            public Map map;
            public IntVec3 offset;
            /// <summary>Tile index on the neighbor's layer.</summary>
            public int worldTile;
        }

        /// <summary>Look up a direct neighbor by its tile index on the source map's layer.</summary>
        public static bool TryGetNeighborLinkByWorldTile(Map map, int worldTile, out NeighborInfo info)
            => TryGetNeighborLinkByWorldTile(map, SeamlessTileRegistry.InMapLayer(map, worldTile), out info);

        /// <summary>Look up a live direct neighbor using the two map instances.</summary>
        public static bool TryGetNeighborLink(Map source, Map target, out NeighborInfo info)
        {
            info = default;
            if (!CanReadLinks(source) || target == null) return false;
            var parent = target.Parent;
            if (parent == null) return false;
            var links = SeamlessMapData.Neighbors(source);
            if (links == null) return false;
            foreach (var link in links)
            {
                if (link?.neighbor == parent && TryReadNeighbor(source, link, out var result) && result.map == target)
                {
                    info = result;
                    return true;
                }
            }
            return false;
        }

        internal static bool TryGetNeighborLinkByWorldTile(Map map, PlanetTile worldTile, out NeighborInfo info)
        {
            info = default;
            if (!CanReadLinks(map) || !worldTile.Valid) return false;

            NeighborLink link = null;
            var links = SeamlessMapData.Neighbors(map);
            if (links != null)
            {
                foreach (var l in links)
                {
                    if (l?.neighbor != null && l.worldTile == worldTile.tileId && l.neighbor.Tile == worldTile)
                    {
                        link = l;
                        break;
                    }
                }
            }

            return TryReadNeighbor(map, link, out info);
        }

        private static bool CanReadLinks(Map map)
            => map != null && !map.Disposed && SeamlessTileRegistry.GetMapWorldTile(map).Valid;

        private static bool TryReadNeighbor(Map source, NeighborLink link, out NeighborInfo info)
        {
            info = default;
            var neighborMap = link?.neighbor?.Map;
            // 休眠过滤（2026-08 软休眠）：休眠邻居不算"有效邻接"——渲染不画其背景、
            // 传送点坐标缓存（hasArrival）失效、跨图菜单关闭。link 本身保留（唤醒后即恢复）。
            if (!CanReadLinks(neighborMap) || SeamlessDormancyManager.IsDormant(neighborMap)
                || neighborMap.Tile.Layer != source.Tile.Layer) return false;

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
            if (result == null || !CanReadLinks(map)) return;

            var links = SeamlessMapData.Neighbors(map);
            if (links == null) return;

            foreach (var link in links)
            {
                if (TryReadNeighbor(map, link, out var info)) result.Add(info);
            }
        }

        /// <summary>判断 b 是否为 a 的直接邻居（对称：a 是 b 的邻居 ⟺ b 是 a 的邻居）。</summary>
        public static bool AreNeighbors(Map a, Map b) => TryGetNeighborLink(a, b, out _);

        /// <summary>Find an active map by tile index on the root surface.</summary>
        public static bool TryGetMapByWorldTile(int worldTile, out Map existing)
            => TryGetMapByWorldTile(new PlanetTile(worldTile, -1), out existing);

        /// <summary>
        /// 全局查询：指定 worldTile 是否已有任意**活跃**的地图（含玩家家园与所有受管辖图），不限于直接邻居。
        /// 遍历 Find.Maps（含所有已加载地图），用 SeamlessTileRegistry.GetMapWorldTile 统一取 worldTile。
        /// 用于预加载去重：A 和 C 虽非直接邻居（隔了 B），但 A 的地图已存在，从 C 预加载 A 的 worldTile 时应复用而非重复生成。
        ///
        /// 休眠口径（2026-08 软休眠）：休眠图虽仍在 Find.Maps，但本查询视为"未加载"——全 mod
        /// "对端已加载"判定的统一语义源（撤离链 tier0 / 传送许可对端校验 / 预加载去重）。
        /// 消费方若需要感知休眠图的存在（如生成守卫防重复 WorldObject），请直接查
        /// Find.World.worldObjects.MapParentAt 或 <see cref="SeamlessDormancyManager.TryWakeByWorldTile"/>。
        /// </summary>
        internal static bool TryGetMapByWorldTile(PlanetTile worldTile, out Map existing)
        {
            existing = null;
            if (!worldTile.Valid) return false;
            foreach (var map in Find.Maps)
            {
                if (SeamlessTileRegistry.GetMapWorldTile(map) == worldTile)
                {
                    if (SeamlessDormancyManager.IsDormant(map)) continue;
                    existing = map;
                    return true;
                }
            }
            return false;
        }

    }
}
