using System.Collections.Generic;
using LudeonTK;
using RimWorld.Planet;
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
                Log.Message($"[RimExodus] Generated seamless tile map for world tile {neighborWorldTile} (hostOffset={parent.hostOffset}).");
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
    }
}
