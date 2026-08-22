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
            var links = SeamlessMapData.Neighbors(map);
            if (links != null)
            {
                foreach (var l in links)
                {
                    if (l != null && l.worldTile == worldTile && l.neighbor != null)
                    {
                        link = l;
                        break;
                    }
                }
            }

            if (link?.neighbor == null) return false;

            var neighborMap = link.neighbor.Map;
            // 休眠过滤（2026-08 软休眠）：休眠邻居不算"有效邻接"——渲染不画其背景、
            // 传送点坐标缓存（hasArrival）失效、跨图菜单关闭。link 本身保留（唤醒后即恢复）。
            if (neighborMap == null || neighborMap.Disposed || SeamlessDormancyManager.IsDormant(neighborMap)) return false;

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

            var links = SeamlessMapData.Neighbors(map);
            if (links == null) return;

            foreach (var link in links)
            {
                if (link?.neighbor == null) continue;
                var neighborMap = link.neighbor.Map;
                // 休眠过滤：见 TryGetNeighborLinkByWorldTile——休眠邻居不进枚举（渲染/相机/选中随之退化）。
                if (neighborMap == null || neighborMap.Disposed || SeamlessDormancyManager.IsDormant(neighborMap)) continue;
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
        /// 全局查询：指定 worldTile 是否已有任意**活跃**的地图（含玩家家园与所有受管辖图），不限于直接邻居。
        /// 遍历 Find.Maps（含所有已加载地图），用 SeamlessTileRegistry.GetMapWorldTile 统一取 worldTile。
        /// 用于预加载去重：A 和 C 虽非直接邻居（隔了 B），但 A 的地图已存在，从 C 预加载 A 的 worldTile 时应复用而非重复生成。
        ///
        /// 休眠口径（2026-08 软休眠）：休眠图虽仍在 Find.Maps，但本查询视为"未加载"——全 mod
        /// "对端已加载"判定的统一语义源（撤离链 tier0 / 传送许可对端校验 / 预加载去重）。
        /// 消费方若需要感知休眠图的存在（如生成守卫防重复 WorldObject），请直接查
        /// Find.World.worldObjects.MapParentAt 或 <see cref="SeamlessDormancyManager.TryWakeByWorldTile"/>。
        /// </summary>
        public static bool TryGetMapByWorldTile(int worldTile, out Map existing)
        {
            existing = null;
            if (worldTile < 0) return false;
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

        /// <summary>
        /// 统一取数入口：获取 worldTile 已生成地块的接缝条带快照（新图接缝混合的邻居参考数据）。
        /// ① 图在 Find.Maps（**含休眠图**——本方法是数据查询而非邻接交互，快照数据不因休眠失效；
        ///    载体差异由 <see cref="SeamlessMapData.GetSeamStrip"/> 屏蔽，两条路径读到的都是
        ///    同一份生成期捕获数据）；
        /// ② 图不在（未来"卸 Map 留 WorldObject"的滚动卸载语义）但地块 WorldObject 还在 → 读其快照
        ///    （原生家族 parent 无 WorldObject 侧快照挂点——图删即失，再生成时由对端单侧照抄补连续）；
        /// ③ 都没有（从未生成 / 删除已销毁 WorldObject）→ 返回 false（不参考，"删除 = 从未出现过"）。
        /// 刻意不走 <see cref="TryGetMapByWorldTile"/>（那是休眠过滤后的交互口径）。
        /// </summary>
        public static bool TryGetNeighborSeamStrip(int worldTile, out SeamStripData strip)
        {
            strip = null;
            if (worldTile < 0) return false;

            foreach (var map in Find.Maps)
            {
                if (SeamlessTileRegistry.GetMapWorldTile(map) != worldTile || map.Disposed) continue;
                strip = SeamlessMapData.GetSeamStrip(map);
                return strip != null;
            }

            var parent = Find.World.worldObjects.MapParentAt(new PlanetTile(worldTile)) as MapParent_SeamlessTile;
            strip = parent?.seamStrip;
            return strip != null;
        }
    }
}
