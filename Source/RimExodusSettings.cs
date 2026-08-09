using Verse;

namespace RimExodus
{
    /// <summary>
    /// RimExodus 的 Mod 设置（阶段4a：邻居预加载）。
    /// 持久化在 mod 自有存档文件中（GetSettings&lt;T&gt; 机制）。
    /// </summary>
    public class RimExodusSettings : ModSettings
    {
        /// <summary>
        /// pawn 距多边形边界多少格内时触发对应邻居地块的预加载。
        /// 阈值越大越早预加载（更流畅但更费内存），越小越懒（省内存但接近边界才加载）。
        /// </summary>
        public int borderPreloadDistance = 15;

        /// <summary>
        /// 开档时是否预加载锚点地块的全部世界邻居（默认 false：仅预铺传送点，等 pawn 接近边界再加载）。
        /// 高配玩家可开启以获得更流畅体验。
        /// </summary>
        public bool preloadAllNeighborsOnStart = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            base.ExposeData();
        }
    }
}
