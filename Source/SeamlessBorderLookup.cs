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
        /// <summary>边界带格 → 最近边对应的邻居 worldTile（不在表 = 非边界带，不触发预加载）。</summary>
        private Dictionary<IntVec3, int> borderCells;

        /// <summary>
        /// 不可建造带格集合（阶段4 安全约束）。
        /// 多边形边内侧 <see cref="RimExodusSettings.borderNoBuildDistance"/> 格的环形带，禁止玩家建造（防接缝卡死）。
        /// 只存格坐标（不需要 neighbor worldTile 值），故用 HashSet 而非 Dictionary。
        /// 不持久化，读档后由几何重建（同 borderCells）。
        /// </summary>
        private HashSet<IntVec3> noBuildBandCells;

        /// <summary>延迟构建的 tick 计数（MapGenerated 时 mapBeingGenerated 仍非空，需延迟到下一 tick）。</summary>
        private int pendingBuildTicks = -1;

        /// <summary>是否已完成构建（避免重复构建）。供外部（如 CanPlaceBlueprintAt patch）判断速查表是否就绪。</summary>
        public bool IsBuilt => built;

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
        /// 查询某格是否在不可建造带内（多边形边内侧 borderNoBuildDistance 格）。
        /// 供 <see cref="Patch_GenConstruct_CanPlaceBlueprintAt"/> 拦截玩家建造。
        /// 速查表未就绪（built=false）时返回 false（放行，避免误拒）。
        /// </summary>
        public bool IsInNoBuildBand(IntVec3 cell)
        {
            return built && noBuildBandCells != null && noBuildBandCells.Contains(cell);
        }

        /// <summary>
        /// 构建边界带速查表（预加载带 + 不可建造带）。读档/新地图首次 tick 或 MapGenerated 延迟后调用。
        /// 幂等：已构建则跳过（除非 force=true）。
        /// 两张表独立构建，各自带宽独立（预加载带=borderPreloadDistance，禁建带=borderNoBuildDistance）。
        /// </summary>
        public void BuildBorderLookup(bool force = false)
        {
            if (built && !force) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0)
            {
                return;
            }

            var preloadBandWidth = RimExodusMod.Settings?.borderPreloadDistance ?? 15;
            var noBuildBandWidth = RimExodusMod.Settings?.borderNoBuildDistance ?? 3;

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

            // 构建预加载带（borderCells：格 → 最近边对应的邻居 worldTile）。
            borderCells = new Dictionary<IntVec3, int>();
            if (preloadBandWidth > 0)
            {
                SeamlessPolygonGeometry.ComputeEdgeBand(verts, map.Size.x, preloadBandWidth, neighborWorldTiles, borderCells);

                // 兜底：把所有传送点格也加入预加载带（传送点在接缝上，应触发预加载）。
                // ComputeEdgeBand 的扫描线差集可能在边附近有 ±1 格误差漏掉某些接缝格，
                // 传送点格必在接缝上，用它们的 targetWorldTile 补全 borderCells。
                var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
                if (enterSpotDef != null)
                {
                    foreach (var thing in map.listerThings.ThingsOfDef(enterSpotDef))
                    {
                        if (thing == null) continue;
                        var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                        if (comp == null) continue;
                        var cell = thing.Position;
                        if (!borderCells.ContainsKey(cell))
                        {
                            borderCells[cell] = comp.targetWorldTile;
                        }
                    }
                }
            }

            // 构建不可建造带（noBuildBandCells：只需格集合，不需 worldTile 值）。
            noBuildBandCells = new HashSet<IntVec3>();
            if (noBuildBandWidth > 0)
            {
                // ComputeEdgeBand 输出 Dictionary<IntVec3,int>，这里只需 key，用临时字典接收后取 key。
                var tmpDict = new Dictionary<IntVec3, int>();
                SeamlessPolygonGeometry.ComputeEdgeBand(verts, map.Size.x, noBuildBandWidth, neighborWorldTiles, tmpDict);
                foreach (var kv in tmpDict)
                {
                    noBuildBandCells.Add(kv.Key);
                }
            }

            built = true;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] SeamlessBorderLookup built for map {map.uniqueID} (worldTile={worldTile}, " +
                    $"preloadBand={preloadBandWidth} cells={borderCells.Count}, " +
                    $"noBuildBand={noBuildBandWidth} cells={noBuildBandCells.Count}).");
        }
    }
}
