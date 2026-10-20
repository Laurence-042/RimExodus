using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 边界带速查表（阶段4a：邻居预加载）。
    /// 每张无缝地图持有一份；预加载距离改变后，在下次预加载查询前按新距离重建。
    ///
    /// 存储：Dictionary&lt;IntVec3,int&gt;（边界带格 → 该格最近边对应的世界邻居 tile id）。
    /// 表含核心侧预加载带与接缝带；查询时按撤离开关决定是否允许接缝带触发。
    /// 查询 O(1)，适合未来高频场景（如撤退袭击者批量检查）。
    ///
    /// 预加载带复用 BuildSeamBand 的 TransitionDepth，从接缝带核心侧第一圈算距离 1。
    /// 阈值从 <see cref="RimExodusSettings.borderPreloadDistance"/> 读取，0 表示关闭。
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

        /// <summary>当前预加载表使用的距离；与表一起仅驻内存，不序列化。</summary>
        private int builtPreloadBandWidth = -1;

        public SeamlessBorderLookup(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // 刻意什么都不序列化：borderCells/noBuildBandCells/built/pendingBuildTicks 全是运行时状态，
            // 读档后由 MapComponentTick 的 !built 分支首 tick 补建（多边形几何可重现，无需存档）。
            // 教训（2026-08）：曾持久化 built——读档恢复 built==true 而 borderCells==null，
            // 堵死补建分支 → TryGetPreloadTarget 恒 false → 读档后邻图预加载/生成整链断裂。
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

        /// <summary>普通移动与 PS 共用的预加载资格查询；撤离开启时排除接缝带，距离 0 全部关闭。</summary>
        public bool TryGetPreloadTarget(IntVec3 cell, out int worldTile)
        {
            worldTile = -1;
            // 按需刷新：暂停时下令、休眠后唤醒及 PS 移动均使用当前距离；拖滑条不逐帧重扫地图。
            // 首次构建仍由原有首 tick 路径负责，避免在生成尚未铺 void 时提前构建。
            if (built && builtPreloadBandWidth != (RimExodusMod.Settings?.borderPreloadDistance ?? 15))
            {
                BuildBorderLookup(force: true);
            }
            if (borderCells == null || borderCells.Count == 0) return false;
            if (!borderCells.TryGetValue(cell, out worldTile) || worldTile < 0) return false;

            // 开关实时读取，不参与建表：暂停时切换也立即生效，无需重新扫描地图。
            // 本图接缝带（含传送点兜底）只在允许撤离时表示离场意图；沉浸模式下允许预加载。
            // worldTile 是待加载邻居，几何必须使用本图的 tile。
            if (SeamExitBandGating.Enabled
                && (SeamlessPolygonGeometry.BuildSeamBand(SeamlessTileRegistry.GetMapWorldTile(map), map.Size.x)
                        .Band.Contains(cell)
                    || SeamlessEdgeCells.IsSeamEdgeCell(map, cell)))
            {
                worldTile = -1;
                return false;
            }
            return true;
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

            // verbose 计时（2026-08 收尾段用时统计）：本方法在生成完成后的下一 tick 执行，
            // 预加载带读取缓存几何，禁建带仍扫描 void 实况。
            var sw = (RimExodusLog.Enabled(RimExodusLogModule.Core))
                ? System.Diagnostics.Stopwatch.StartNew() : null;

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
                builtPreloadBandWidth = preloadBandWidth;
                built = true;
                return;
            }

            // 构建预加载带（borderCells：格 → 最近边对应的邻居 worldTile）。
            // 从接缝带核心侧起算，不能从 void 起算再排除 Band：后者会吞掉设置值的前三圈。
            // 直接复用权威几何的深度，避免固定 +3 在斜边/顶角处与真实接缝厚度不一致。
            borderCells = new Dictionary<IntVec3, int>();
            if (preloadBandWidth > 0)
            {
                var seamBand = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x);
                // Band 不占用设置的 N 圈；距离 >0 时预存映射，查询时再按撤离开关放行。
                foreach (var cell in seamBand.Band) AddPreloadCell(cell);
                foreach (var entry in seamBand.TransitionDepth)
                {
                    if (entry.Value < 1 || entry.Value > preloadBandWidth) continue;
                    AddPreloadCell(entry.Key);
                }

                void AddPreloadCell(IntVec3 cell)
                {
                    var edgeIndex = SeamlessPolygonGeometry.FindClosestEdgeIndex(verts, cell.x + 0.5f, cell.z + 0.5f);
                    if (edgeIndex >= 0 && edgeIndex < neighborWorldTiles.Count)
                        borderCells[cell] = neighborWorldTiles[edgeIndex];
                }
            }

            // 构建不可建造带（noBuildBandCells：只需格集合，不需 worldTile 值）。
            // 仍用 ComputeVoidBand（void 边界同源），不随预加载距离的起算点调整。
            noBuildBandCells = new HashSet<IntVec3>();
            if (noBuildBandWidth > 0)
            {
                var tmpDict = new Dictionary<IntVec3, int>();
                SeamlessPolygonGeometry.ComputeVoidBand(map, noBuildBandWidth, neighborWorldTiles, tmpDict);
                foreach (var kv in tmpDict)
                {
                    noBuildBandCells.Add(kv.Key);
                }
            }

            builtPreloadBandWidth = preloadBandWidth;
            built = true;

            if (RimExodusLog.Enabled(RimExodusLogModule.Core))
                Log.Message($"[RimExodus] SeamlessBorderLookup built for map {map.uniqueID} (worldTile={worldTile}, " +
                    $"preloadBand={preloadBandWidth} cells={borderCells.Count}, " +
                    $"noBuildBand={noBuildBandWidth} cells={noBuildBandCells.Count}, " +
                    $"elapsed={sw?.ElapsedMilliseconds ?? 0}ms).");
        }
    }
}
