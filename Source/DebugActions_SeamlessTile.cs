using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块的调试命令（Dev 菜单）。
    /// 用于在游戏内手动生成/卸载无缝地块口袋地图，验证渲染与接缝。
    ///
    /// 阶段3：邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 命令改为"生成当前地块的指定世界邻居地块"。
    /// </summary>
    public static class DebugActions_SeamlessTile
    {
        private const string Category = "RimExodus";

        private const int DefaultMapSize = 250;

        /// <summary>获取当前地图上的 SeamlessTileManager。</summary>
        private static SeamlessTileManager CurrentManager
        {
            get
            {
                var map = Find.CurrentMap;
                if (map == null) return null;
                return map.GetComponent<SeamlessTileManager>();
            }
        }

        /// <summary>生成当前地块的指定世界邻居地块。</summary>
        private static void GenerateForNeighbor(int neighborWorldTile)
        {
            var manager = CurrentManager;
            if (manager == null)
            {
                Log.Warning("[RimExodus] No SeamlessTileManager on current map.");
                return;
            }

            var currentMap = Find.CurrentMap;
            var currentWorldTile = SeamlessTileRegistry.GetMapWorldTile(currentMap);
            if (currentWorldTile < 0)
            {
                Log.Warning("[RimExodus] Current map has no valid world tile.");
                return;
            }

            if (manager.GetNeighborByWorldTile(neighborWorldTile) != null)
            {
                Log.Message($"[RimExodus] World tile {neighborWorldTile} already has a seamless tile map.");
                return;
            }

            // void 铺设在 GenerateTileMap 内部邻居登记后自动刷新。
            var mapSize = new IntVec3(currentMap.Size.x, 1, currentMap.Size.z);
            var parent = manager.GenerateTileMap(currentWorldTile, neighborWorldTile, mapSize);
            if (parent != null)
            {
                Log.Message($"[RimExodus] Generated seamless tile map for world tile {neighborWorldTile}.");
            }
        }

        [DebugAction(Category, "Generate All Seamless Neighbors", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateAllNeighbors()
        {
            var currentMap = Find.CurrentMap;
            var currentWorldTile = SeamlessTileRegistry.GetMapWorldTile(currentMap);
            if (currentWorldTile < 0)
            {
                Log.Warning("[RimExodus] Current map has no valid world tile.");
                return;
            }

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(currentWorldTile, worldNeighbors);
            foreach (var neighborTile in worldNeighbors)
            {
                GenerateForNeighbor(neighborTile.tileId);
            }
        }

        [DebugAction(Category, "Remove All Tile Maps", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAll()
        {
            var manager = CurrentManager;
            if (manager == null) return;

            var currentMap = Find.CurrentMap;
            var neighbors = SeamlessTileGraph.GetAllNeighbors(currentMap);
            var toRemove = new List<MapParent_SeamlessTile>();
            foreach (var info in neighbors)
            {
                if (info.map.Parent is MapParent_SeamlessTile neighborParent)
                {
                    toRemove.Add(neighborParent);
                }
            }

            foreach (var parent in toRemove)
            {
                manager.RemoveTileMap(parent);
            }

            Log.Message($"[RimExodus] Removed {toRemove.Count} seamless tile maps.");
        }

        /// <summary>
        /// 调试工具（Dev 地图工具）：点击地图格，输出该格的 snapshot 值 + 接缝条带快照 + SeamOverride 混合时引用的邻居格信息。
        /// 用于精确定位"某格 snapshot 是水/沙，但被邻居土卷积成了泥"等海岸侵蚀问题。
        /// 输出 self snapshot（本地 snapshot 在该格的值）vs neighbor（邻居对应格的条带快照值），
        /// 以及 NeighborLink offset、邻居对应格坐标（与传送点同源，已验证正确）。
        /// 末尾附 <see cref="SeamlessSeamOverride.DescribeCellMixing"/> 重放段：圈层归属、跳过保护、
        /// 各邻居参考（重叠带 w=1 完全一致 / 外条带深度衰减）、卷积分布与覆写判定。
        /// </summary>
        [DebugAction(Category, "Inspect Snapshot At Position", false, false, false, false, false, 0, false,
            actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectSnapshotAtPosition()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            var cell = UI.MouseCell();
            if (!cell.InBounds(map)) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var cellIndices = map.cellIndices;
            var idx = cellIndices.CellToIndex(cell);
            var currentTerrain = map.terrainGrid.topGrid[idx];

            // 本地 snapshot（地块读 MapParent_SeamlessTile，锚点读 Manager）。
            TerrainDef[] selfSnapshot = null;
            if (map.Parent is MapParent_SeamlessTile tile)
                selfSnapshot = tile.baseTerrainSnapshot;
            else
                selfSnapshot = map.GetComponent<SeamlessTileManager>()?.anchorBaseTerrainSnapshot;

            var selfSnapDef = (selfSnapshot != null && idx < selfSnapshot.Length) ? selfSnapshot[idx] : null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[RimExodus-SnapshotInspect] cell=({cell.x},{cell.z}) wt={worldTile}");
            sb.AppendLine($"  current topGrid: {TerrainName(currentTerrain)}");
            sb.AppendLine($"  self snapshot:   {TerrainName(selfSnapDef)}{(selfSnapDef != null && currentTerrain != null && selfSnapDef != currentTerrain ? "  <<< DIFFERS from topGrid（原生与当前不同：若本格在混合范围内，通常是生成期 SeamOverride 已把原生覆写成了当前值——重放的 self 采样用的就是当前值）" : "")}");

            if (worldTile < 0)
            {
                sb.AppendLine("  (no valid worldTile, no polygon/neighbor info)");
                Log.Message(sb.ToString().TrimEnd());
                return;
            }

            // 最近多边形边 + 对应邻居 worldTile。
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3)
            {
                sb.AppendLine("  (polygon verts < 3, no neighbor info)");
                Log.Message(sb.ToString().TrimEnd());
                return;
            }

            var edgeIdx = SeamlessPolygonGeometry.FindClosestEdgeIndex(verts, cell.x + 0.5f, cell.z + 0.5f);
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTile = (edgeIdx >= 0 && edgeIdx < worldNeighbors.Count) ? worldNeighbors[edgeIdx].tileId : -1;

            sb.AppendLine($"  nearestEdge={edgeIdx}  neighborWT={neighborWorldTile}");

            // 邻居是否已加载（用 NeighborLink，与传送点同源）。最近边邻居仅作上下文参考，
            // 重放段自行遍历全部已加载邻居，故此处不再提前返回。
            if (neighborWorldTile < 0 || !SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, neighborWorldTile, out var info))
            {
                sb.AppendLine("  neighbor loaded: NO");
            }
            else
            {
                var neighborMap = info.map;
                var neighborCell = cell - info.offset;
                sb.AppendLine($"  neighbor loaded: YES  (map={neighborMap?.uniqueID})");
                sb.AppendLine($"    offset={info.offset}  (NeighborLink，与传送点同源)");
                sb.AppendLine($"    neighborCell=({neighborCell.x},{neighborCell.z}) = cell - offset");

                if (neighborMap == null || !neighborCell.InBounds(neighborMap))
                {
                    sb.AppendLine($"    neighborCell out of bounds → SeamOverride 卷积时会跳过(oob)");
                }
                else
                {
                    // 邻居对应格当前地形。
                    var neighborCurrent = neighborMap.terrainGrid.topGrid[neighborMap.cellIndices.CellToIndex(neighborCell)];
                    sb.AppendLine($"    neighbor current:  {TerrainName(neighborCurrent)}");

                    // 邻居对应格在邻居 snapshot 的值。
                    TerrainDef[] neighborSnapshot = null;
                    if (neighborMap.Parent is MapParent_SeamlessTile neighborTile)
                        neighborSnapshot = neighborTile.baseTerrainSnapshot;
                    else
                        neighborSnapshot = neighborMap.GetComponent<SeamlessTileManager>()?.anchorBaseTerrainSnapshot;

                    var nIdx = neighborMap.cellIndices.CellToIndex(neighborCell);
                    var neighborSnapDef = (neighborSnapshot != null && nIdx < neighborSnapshot.Length) ? neighborSnapshot[nIdx] : null;
                    sb.AppendLine($"    neighbor snapshot: {TerrainName(neighborSnapDef)}");

                    // 邻居接缝条带快照在对应格的值（SeamOverride 实际参考源：B_A=最终值 / 外条带=原生值）。
                    if (SeamlessTileGraph.TryGetNeighborSeamStrip(neighborWorldTile, out var nStrip) && nStrip.terrainLookup != null)
                    {
                        nStrip.terrainLookup.TryGetValue(neighborCell, out var stripDef);
                        nStrip.buildingLookup.TryGetValue(neighborCell, out var stripRock);
                        nStrip.roofLookup.TryGetValue(neighborCell, out var stripRoof);
                        sb.AppendLine($"    neighbor seam-strip: {TerrainName(stripDef)}  岩体={stripRock?.defName ?? "无"}  屋顶={stripRoof?.defName ?? "无"}{(stripDef == null ? "  (不在条带区域)" : "")}");
                    }
                    else
                    {
                        sb.AppendLine("    neighbor seam-strip: 无快照（未生成或无数据）");
                    }
                }
            }

            // SeamOverride 混合重放（圈层归属/跳过保护/各邻居参考权重/卷积分布/覆写判定）。
            sb.Append(SeamlessSeamOverride.DescribeCellMixing(map, worldTile, cell));

            Log.Message(sb.ToString().TrimEnd());
        }

        /// <summary>TerrainDef 的简短名（null 安全）。</summary>
        private static string TerrainName(TerrainDef t) => t == null ? "(null)" : t.defName;
    }
}
