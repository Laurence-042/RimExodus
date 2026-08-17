using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块入口端点。
    ///
    /// 架构：传送点不与对端 spot 双向互绑。每个 spot 记录指向的对端世界地块
    /// <see cref="targetWorldTile"/>；邻居关系建立后，由 <see cref="SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals"/>
    /// 按 offset 算出"本格在对端地图的对应坐标"并缓存到 <see cref="cachedArrivalCell"/>。
    /// 之后传送触发与寻路查询都 O(1) 读缓存，不再现算 offset、不依赖对端 spot 是否存在。
    ///
    /// 中点对齐的取整残差与投影残差（±1 格）由对侧 3 圈接缝带吸收：传送圈只有外侧 2 圈
    /// （离散边圈 ∪ 带外圈），落点偏移最多落到对侧带内/带外圈——均为实地形，不进 void
    /// （权威定义见 doc/接缝带定义.md）。
    /// </summary>
    public class CompSeamlessTileEnterSpot : ThingComp
    {
        /// <summary>
        /// 本端点指向的对端世界地块 tile id（预铺时由世界邻居序号确定）。
        /// -1 表示未设置。用于运行时解析对端 Map + 寻路过滤。
        /// </summary>
        public int targetWorldTile = -1;

        /// <summary>
        /// 本格在对端地图的对应坐标（缓存）。由 <see cref="ComputeAndCacheArrival"/> 算出：
        /// <c>cachedArrivalCell = Position - offset</c>（NeighborLink 契约 cellNeighbor + offset = cellMy 的逆）。
        /// 邻居关系建立后由 <see cref="SeamlessTileManager"/> 刷新一次，之后只读。
        /// 不序列化：邻居关系重建时可重算。
        /// </summary>
        public IntVec3 cachedArrivalCell;

        /// <summary>是否已缓存有效对端坐标（对端邻居已加载且 offset 已确定）。</summary>
        public bool hasArrival;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref targetWorldTile, "targetWorldTile", -1);
            // cachedArrivalCell/hasArrival 不序列化：邻居关系建立后刷新即可。
        }

        /// <summary>
        /// 按 targetWorldTile 查邻居表得 offset，算出本格在对端地图的对应坐标并缓存。
        /// 邻居未加载或邻居关系未建立时，<see cref="hasArrival"/> 置 false（传送/寻路时跳过）。
        /// </summary>
        public void ComputeAndCacheArrival(Map ownerMap)
        {
            hasArrival = false;
            if (ownerMap == null || targetWorldTile < 0) return;

            if (!SeamlessTileGraph.TryGetNeighborLinkByWorldTile(ownerMap, targetWorldTile, out var info))
            {
                // 对端邻居尚未加载/未登记：暂不可用。
                return;
            }

            // NeighborLink 契约：cellNeighbor + offset = cellMy，故 cellNeighbor(对端) = cellMy - offset。
            cachedArrivalCell = parent.Position - info.offset;
            hasArrival = true;
        }
    }

    public class CompProperties_SeamlessTileEnterSpot : CompProperties
    {
        public CompProperties_SeamlessTileEnterSpot()
        {
            compClass = typeof(CompSeamlessTileEnterSpot);
        }
    }
}
