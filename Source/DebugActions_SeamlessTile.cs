using LudeonTK;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块的调试命令（Dev 菜单）。
    /// 用于在游戏内手动生成/卸载无缝地块口袋地图，验证渲染与接缝。
    ///
    /// 六边形方向：0=北,1=东北,2=东南,3=南,4=西南,5=西北。
    /// 原型阶段四向兼容：1/2 暂时当作东，4/5 暂时当作西。
    /// </summary>
    public static class DebugActions_SeamlessTile
    {
        private const string Category = "RimExodus";

        private const int DefaultMapSize = 50;

        /// <summary>获取当前宿主地图上的 SeamlessTileManager。</summary>
        private static SeamlessTileManager CurrentManager
        {
            get
            {
                var map = Find.CurrentMap;
                if (map == null)
                {
                    return null;
                }
                return map.GetComponent<SeamlessTileManager>();
            }
        }

        /// <summary>生成指定方向的相邻地块地图。</summary>
        private static void GenerateInDirection(int direction)
        {
            var manager = CurrentManager;
            if (manager == null)
            {
                Log.Warning("[RimExodus] No SeamlessTileManager on current map.");
                return;
            }

            if (manager.GetTileMapInDirection(direction) != null)
            {
                Log.Message($"[RimExodus] Tile map in direction {direction} already exists.");
                return;
            }

            var mapSize = new IntVec3(DefaultMapSize, 1, DefaultMapSize);
            var parent = manager.GenerateTileMap(direction, mapSize, SeamlessTileManager.DefaultOverlapBand);
            if (parent != null)
            {
                Log.Message($"[RimExodus] Generated seamless tile map in direction {direction} (hostOffset={parent.hostOffset}).");
            }
        }

        [DebugAction(Category, "Generate North Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateNorth()
        {
            GenerateInDirection(0);
        }

        [DebugAction(Category, "Generate NorthEast Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateNorthEast()
        {
            GenerateInDirection(1);
        }

        [DebugAction(Category, "Generate SouthEast Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateSouthEast()
        {
            GenerateInDirection(2);
        }

        [DebugAction(Category, "Generate South Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateSouth()
        {
            GenerateInDirection(3);
        }

        [DebugAction(Category, "Generate SouthWest Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateSouthWest()
        {
            GenerateInDirection(4);
        }

        [DebugAction(Category, "Generate NorthWest Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateNorthWest()
        {
            GenerateInDirection(5);
        }

        [DebugAction(Category, "Generate All 6 Tile Maps", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateAll()
        {
            for (var d = 0; d < 6; d++)
            {
                GenerateInDirection(d);
            }
        }

        [DebugAction(Category, "Remove All Tile Maps", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAll()
        {
            var manager = CurrentManager;
            if (manager == null)
            {
                return;
            }

            // 收集当前地图的所有直接邻居（对称：不再依赖 sourceMap）。
            var currentMap = Find.CurrentMap;
            var neighbors = SeamlessTileGraph.GetAllNeighbors(currentMap);
            var toRemove = new System.Collections.Generic.List<MapParent_SeamlessTile>();
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
