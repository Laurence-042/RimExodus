using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块入口端点。
    /// 每个端点持有对端入口引用（双方地位完全相同，不感知宿主或口袋地图身份）。
    ///
    /// 阶段4a：预铺 + 延迟绑定。spot 在地图生成时沿多边形边预铺（单端），
    /// 此时 <see cref="CounterpartSpot"/> 为 null，<see cref="targetWorldTile"/> 记录对端世界地块。
    /// 对应邻居加载后，由 <see cref="SeamlessEnterSpotBinder"/> 按 targetWorldTile + 坐标校验互绑。
    /// </summary>
    public class CompSeamlessTileEnterSpot : ThingComp
    {
        private Thing counterpartSpot;

        /// <summary>
        /// 与本端点配对的另一端入口。预铺未绑定时为 null；绑定后双方互指。
        /// </summary>
        public Thing CounterpartSpot
        {
            get => counterpartSpot;
            set => counterpartSpot = value;
        }

        /// <summary>
        /// 本端点指向的对端世界地块 tile id（预铺时由世界邻居序号确定）。
        /// -1 表示未设置。用于延迟绑定：邻居加载后扫描两端的 spot 按 targetWorldTile 匹配。
        /// </summary>
        public int targetWorldTile = -1;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref counterpartSpot, "counterpartSpot");
            Scribe_Values.Look(ref targetWorldTile, "targetWorldTile", -1);
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
