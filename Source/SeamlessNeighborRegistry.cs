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
    /// 邻居表存储载体由 <see cref="SeamlessMapData"/> 统一屏蔽：
    /// 地块图（<see cref="MapParent_SeamlessTile"/>）存自身字段，
    /// 原生 parent 图（家园/原生家族 Settlement 等）存 <see cref="SeamlessTileManager"/> 组件。
    /// </summary>
    internal static class SeamlessNeighborRegistry
    {
        /// <summary>
        /// 双向登记两个地块的邻居关系（支持任意组合：原生图-地块、地块-地块、地块-原生图）。
        /// 源地块 → 新地块：用源地块多边形上指向 newWorldTile 的边角度。
        /// 新地块 → 源地块：偏移 = -offset。
        /// 存储载体由 <see cref="SeamlessMapData"/> 统一屏蔽（地块图存自身字段，原生图存组件）。
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

        /// <summary>在 map 上登记一条邻居连接（存储载体由 <see cref="SeamlessMapData"/> 屏蔽：地块图存自身字段，原生图存组件）。</summary>
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
        /// 计算 sourceWorldTile（本地图）到 newWorldTile（邻居）的偏移（连续边中点对齐模型）。
        ///
        /// offset = round(midSource − midNew)：两端各用**自己**多边形上共享边的连续边中点。
        /// 契约 neighborLocal + offset = myLocal（邻居格 → 本地格），要求共享边中点满足
        /// midNew + offset = midSource，即 offset = midSource − midNew——渲染平移后两端
        /// 连续边中点精确重合（浮点级），仅取整残差 ≤1 格/分量，由 3 圈接缝带吸收。
        ///
        /// 【为何弃用镜像假设（历史教训勿回退）】旧公式 round(2·(midA−centerA) − SeamOverlap·unit)
        /// 只用源图多边形，隐含"邻居多边形 = 源多边形跨共享边的镜像"。实际两端多边形各自用
        /// 自己 tile 中心的切平面基独立投影，同一条世界共享边在两端局部坐标系中的内切距
        /// |mid−center| 不相等（顶点方向角间隔差 1° ≈ 内切距差 ~1 格）→ 系统性 ±1 格渲染错位
        /// （2026-08 实测：聚焦 C 时 C 的接缝带比 A 背景内偏一格，赤道正北侧同样复现）。
        /// 双端中点公式直接用两端真实内切距，系统误差归零。
        ///
        /// 公式天然对称：任一端计算结果一致（round(−x) = −round(x)），登记时只算一次取负即可。
        /// 无 SeamOverlap 收缩——偏差吸收职责已移交 3 圈接缝带（传送圈仅外侧 2 圈 + 落点 ±1 格
        /// 落对侧带内/带外圈均为实地形）。
        /// </summary>
        /// <param name="sourceWorldTile">本地图（originMap）的世界 tile。</param>
        /// <param name="newWorldTile">邻居的世界 tile。</param>
        /// <param name="originMap">本地图（提供本端尺寸）。</param>
        /// <param name="newMap">邻居地图（提供对端尺寸；多跳补登记场景两端图都在。null 时假设与本端同尺寸）。</param>
        public static IntVec3 ComputeNeighborOffset(int sourceWorldTile, int newWorldTile, Map originMap, Map newMap = null)
        {
            var sourceSize = originMap.Size.x;
            var newSize = newMap?.Size.x ?? sourceSize;

            var sourceVerts = SeamlessPolygonGeometry.BuildPolygonVertices(sourceWorldTile, sourceSize);
            var newVerts = SeamlessPolygonGeometry.BuildPolygonVertices(newWorldTile, newSize);
            if (sourceVerts.Count < 3 || newVerts.Count < 3) return IntVec3.Zero;

            // 共享边在各自多边形中的边索引（"边 j ↔ 邻居 j"，FindNeighborIndex 返回邻居序号即边索引）。
            var edgeIdxSource = WorldTileGeometry.FindNeighborIndex(sourceWorldTile, newWorldTile);
            var edgeIdxNew = WorldTileGeometry.FindNeighborIndex(newWorldTile, sourceWorldTile);
            if (edgeIdxSource < 0 || edgeIdxNew < 0) return IntVec3.Zero;

            var midSource = (sourceVerts[edgeIdxSource] + sourceVerts[(edgeIdxSource + 1) % sourceVerts.Count]) * 0.5f;
            var midNew = (newVerts[edgeIdxNew] + newVerts[(edgeIdxNew + 1) % newVerts.Count]) * 0.5f;

            var offsetVec = midSource - midNew;
            return new IntVec3(Mathf.RoundToInt(offsetVec.x), 0, Mathf.RoundToInt(offsetVec.y));
        }

        /// <summary>
        /// 移除 parent 与其所有邻居之间的双向邻居表引用，并刷新受影响剩余邻居的传送点缓存。
        /// 支持任意 parent（2026-08 泛化）：地块图链表读自身字段，原生家族图（Settlement/Site 等）
        /// 链表读组件——载体差异经 <see cref="SeamlessMapData.Neighbors"/> 屏蔽。
        /// </summary>
        public static void CleanupNeighborLinks(MapParent parent)
        {
            if (parent == null) return;
            var ownLinks = SeamlessMapData.Neighbors(parent.Map);
            if (ownLinks == null) return;

            var linksToRemove = new List<NeighborLink>(ownLinks);
            ownLinks.Clear();

            // 收集受影响的剩余邻居 Map（去重），清理后需刷新其传送点缓存，
            // 否则指向被卸载地块的 spot 仍保留陈旧的 hasArrival/cachedArrivalCell。
            var affectedMaps = new HashSet<Map>();

            foreach (var link in linksToRemove)
            {
                if (link?.neighbor == null) continue;
                var counterLinks = SeamlessMapData.Neighbors(link.neighbor.Map);
                counterLinks?.RemoveAll(n => n != null && n.neighbor == parent);

                // 记录受影响的邻居 Map（neighbor.Map 在对端图已卸载的场景下可能为 null，跳过）。
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
