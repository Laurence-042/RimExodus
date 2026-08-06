using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块入口点组件。
    /// 挂在宿主地图接缝处的传送点 Thing 上，标记"从此处可进入相邻无缝地块"。
    ///
    /// 与 VMF 的 CompVehicleEnterSpot 不同，本组件不依赖车辆实体，
    /// 而是通过 SeamlessTileManager 查询相邻地块的入口位置。
    /// </summary>
    public class CompSeamlessTileEnterSpot : ThingComp
    {
        public CompProperties_SeamlessTileEnterSpot Props =>
            (CompProperties_SeamlessTileEnterSpot)props;

        /// <summary>该入口点对应的方向（0=北,1=东,2=南,3=西）。</summary>
        public int Direction => Props.direction;

        /// <summary>
        /// 该入口点对应的相邻无缝地块 MapParent。
        /// 通过宿主地图的 SeamlessTileManager 查询。
        /// </summary>
        public MapParent_SeamlessTile AdjacentTileParent
        {
            get
            {
                var manager = parent.Map?.GetComponent<SeamlessTileManager>();
                return manager?.GetTileMapInDirection(Direction);
            }
        }
    }

    public class CompProperties_SeamlessTileEnterSpot : CompProperties
    {
        /// <summary>该入口点对应的方向（0=北,1=东,2=南,3=西）。</summary>
        public int direction;

        public CompProperties_SeamlessTileEnterSpot()
        {
            compClass = typeof(CompSeamlessTileEnterSpot);
        }
    }
}
