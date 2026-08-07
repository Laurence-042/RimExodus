using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块入口端点。
    /// 每个端点只持有对端入口引用；双方地位完全相同，不感知宿主或口袋地图身份。
    /// </summary>
    public class CompSeamlessTileEnterSpot : ThingComp
    {
        private Thing counterpartSpot;

        /// <summary>与本端点配对的另一端入口。</summary>
        public Thing CounterpartSpot
        {
            get => counterpartSpot;
            set => counterpartSpot = value;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref counterpartSpot, "counterpartSpot");
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
