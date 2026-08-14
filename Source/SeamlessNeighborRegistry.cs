using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块邻居表登记工具（静态，无状态）。
    /// 封装"双向登记邻居关系 + offset 计算 + 卸载清理"，供 <see cref="SeamlessTileManager"/> 生成流程调用。
    ///
    /// 邻居表存储位置由 <see cref="SetNeighborOnMap"/> 统一屏蔽：
    /// 锚点地图（家园 A）存 <see cref="SeamlessTileManager.neighbors"/>（MapComponent），
    /// 地块地图（<see cref="MapParent_SeamlessTile"/>）存自身 neighbors 字段。
    /// </summary>
    internal static class SeamlessNeighborRegistry
    {
        /// <summary>
        /// 双向登记两个地块的邻居关系（支持任意组合：锚点-地块、地块-地块、地块-锚点）。
        /// 源地块 → 新地块：用源地块多边形上指向 newWorldTile 的边角度。
        /// 新地块 → 源地块：偏移 = -offset。
        /// 存储位置由 <see cref="SetNeighborOnMap"/> 统一屏蔽（锚点存 Manager.neighbors，地块存 MapParent_SeamlessTile.neighbors）。
        /// </summary>
        public static void RegisterNeighborBidirectional(Map originMap, MapParent newParent,
            int sourceWorldTile, int newWorldTile, IntVec3 offset)
        {
            var sourceParent = originMap.info.parent;
            var newMap = newParent.Map;

            SetNeighborOnMap(originMap, newWorldTile, newParent, offset);
            if (newMap != null)
            {
                SetNeighborOnMap(newMap, sourceWorldTile, sourceParent, -offset);
            }

            // offset 在此确定且不再变：刷新两端所有传送点的对端坐标缓存，供传送/寻路 O(1) 读取。
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(originMap);
            if (newMap != null)
            {
                SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(newMap);
            }
        }

        /// <summary>在 map 上登记一条邻居连接（锚点存 Manager.neighbors，地块存 MapParent_SeamlessTile.neighbors）。</summary>
        public static void SetNeighborOnMap(Map map, int worldTile, MapParent neighbor, IntVec3 offset)
        {
            if (map == null || neighbor == null) return;
            if (map.Parent is MapParent_SeamlessTile tileParent)
            {
                tileParent.SetNeighbor(worldTile, neighbor, offset);
            }
            else
            {
                map.GetComponent<SeamlessTileManager>()?.SetNeighbor(worldTile, neighbor, offset);
            }
        }

        /// <summary>
        /// 计算从 sourceWorldTile 到 newWorldTile，新地块相对源地块的偏移。
        /// offset = round(2 × (边中点 - 中心) - SeamOverlap × 方向单位向量)，边中点取自源地块多边形（内切圆模型）。
        /// 边由 newWorldTile 在源地块邻居表中的位置确定。
        /// 沿 offset 方向收缩 <see cref="SeamlessTileManager.SeamOverlap"/> 格，使邻居多边形相对源地图多叠 2 格（接缝重叠带），
        /// 容纳投影扭曲。
        /// </summary>
        public static IntVec3 ComputeNeighborOffset(int sourceWorldTile, int newWorldTile, Map originMap)
        {
            var sourceSize = originMap.Size;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(sourceWorldTile, sourceSize.x);
            if (verts.Count == 0) return IntVec3.Zero;

            var edgeIdx = WorldTileGeometry.FindNeighborIndex(sourceWorldTile, newWorldTile);
            if (edgeIdx < 0) return IntVec3.Zero;

            var n = verts.Count;
            var center = new Vector2(sourceSize.x * 0.5f, sourceSize.z * 0.5f);
            var mid = (verts[edgeIdx] + verts[(edgeIdx + 1) % n]) * 0.5f;
            var offsetVec = 2f * (mid - center);
            // 沿 offset 方向收缩 SeamOverlap 格，形成接缝重叠带（容纳投影扭曲）。
            var mag = offsetVec.magnitude;
            if (mag > 1e-6f)
            {
                offsetVec -= offsetVec / mag * SeamlessTileManager.SeamOverlap;
            }
            return new IntVec3(Mathf.RoundToInt(offsetVec.x), 0, Mathf.RoundToInt(offsetVec.y));
        }

        /// <summary>移除 parent 与其所有邻居之间的双向邻居表引用，并刷新受影响剩余邻居的传送点缓存。</summary>
        public static void CleanupNeighborLinks(MapParent_SeamlessTile parent)
        {
            var linksToRemove = new List<NeighborLink>(parent.neighbors);
            parent.neighbors.Clear();

            // 收集受影响的剩余邻居 Map（去重），清理后需刷新其传送点缓存，
            // 否则指向被卸载地块的 spot 仍保留陈旧的 hasArrival/cachedArrivalCell。
            var affectedMaps = new HashSet<Map>();

            foreach (var link in linksToRemove)
            {
                if (link?.neighbor == null) continue;
                if (link.neighbor is MapParent_SeamlessTile neighborTile)
                {
                    neighborTile.neighbors.RemoveAll(n => n != null && n.neighbor == parent);
                }
                else if (link.neighbor.Map != null)
                {
                    link.neighbor.Map.GetComponent<SeamlessTileManager>()?.neighbors
                        .RemoveAll(n => n != null && n.neighbor == parent);
                }

                // 记录受影响的邻居 Map（neighbor.Map 在地块被卸载场景下可能为 null，跳过）。
                if (link.neighbor.Map != null)
                {
                    affectedMaps.Add(link.neighbor.Map);
                }
            }

            // 刷新剩余邻居的传送点缓存：指向已卸载地块的 spot 会重算 hasArrival=false，缓存自然失效。
            foreach (var affectedMap in affectedMaps)
            {
                SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(affectedMap);
            }
        }
    }
}
