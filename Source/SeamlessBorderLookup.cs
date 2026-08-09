using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 边界带速查表（阶段4a：邻居预加载）。
    /// 每张无缝地图持有一份，构建一次后只读查询（地图生成时多边形固定，邻居表不变）。
    ///
    /// 存储：Dictionary&lt;IntVec3,int&gt;（边界带格 → 该格最近边对应的世界邻居 tile id）。
    /// 不在表中的格 = 非边界带，不触发预加载。
    /// 查询 O(1)，适合未来高频场景（如撤退袭击者批量检查）。
    ///
    /// 构建算法：算法 C（凸多边形内缩 + 扫描线差集），见 <see cref="SeamlessPolygonGeometry.ComputeEdgeBand"/>。
    /// 阈值从 <see cref="RimExodusSettings.borderPreloadDistance"/> 读取。
    /// </summary>
    public class SeamlessBorderLookup : MapComponent
    {
        /// <summary>边界带格 → 最近边对应的邻居 worldTile（不在表 = 非边界带）。</summary>
        private Dictionary<IntVec3, int> borderCells;

        /// <summary>延迟构建的 tick 计数（MapGenerated 时 mapBeingGenerated 仍非空，需延迟到下一 tick）。</summary>
        private int pendingBuildTicks = -1;

        /// <summary>是否已完成构建（避免重复构建）。</summary>
        private bool built;

        public SeamlessBorderLookup(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pendingBuildTicks, "pendingBuildTicks", -1);
            Scribe_Values.Look(ref built, "built", false);
            // borderCells 不持久化：读档后由 MapGenerated/首次 tick 重建（多边形几何可重现，无需存档）。
        }

        public override void MapGenerated()
        {
            base.MapGenerated();
            // 延迟到下一 tick：MapGenerated 在 MapGenerator.GenerateMap 内部调用，此时 mapBeingGenerated 仍非空。
            if (!built && pendingBuildTicks < 0)
            {
                pendingBuildTicks = 1;
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (pendingBuildTicks > 0)
            {
                pendingBuildTicks--;
                if (pendingBuildTicks == 0)
                {
                    pendingBuildTicks = -1;
                    BuildBorderLookup();
                }
            }
            else if (!built && pendingBuildTicks < 0)
            {
                // 读档后若未构建（MapGenerated 不触发），在首次 tick 时补建。
                BuildBorderLookup();
            }
        }

        /// <summary>查询某格是否在边界带内，若是返回应预加载的邻居 worldTile。</summary>
        public bool TryGetPreloadTarget(IntVec3 cell, out int worldTile)
        {
            worldTile = -1;
            if (borderCells == null || borderCells.Count == 0) return false;
            return borderCells.TryGetValue(cell, out worldTile) && worldTile >= 0;
        }

        /// <summary>
        /// 构建边界带速查表。读档/新地图首次 tick 或 MapGenerated 延迟后调用。
        /// 幂等：已构建则跳过（除非 force=true）。
        /// </summary>
        public void BuildBorderLookup(bool force = false)
        {
            if (built && !force) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0)
            {
                return;
            }

            var bandWidth = RimExodusMod.Settings?.borderPreloadDistance ?? 15;
            if (bandWidth <= 0)
            {
                built = true;
                borderCells = new Dictionary<IntVec3, int>();
                return;
            }

            // 取世界邻居列表（顺序与多边形顶点环绕一致）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors)
            {
                neighborWorldTiles.Add(nt.tileId);
            }

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts.Count < 3)
            {
                built = true;
                return;
            }

            borderCells = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeEdgeBand(verts, map.Size.x, bandWidth, neighborWorldTiles, borderCells);
            built = true;

            Log.Message($"[RimExodus] SeamlessBorderLookup built for map {map.uniqueID} (worldTile={worldTile}, " +
                $"bandWidth={bandWidth}, borderCells={borderCells.Count}).");
        }
    }
}
