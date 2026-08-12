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
        /// </summary>
        public int borderPreloadDistance = 15;

        /// <summary>开档时是否预加载锚点地块的全部世界邻居。</summary>
        public bool preloadAllNeighborsOnStart = false;

        /// <summary>详细诊断日志开关。</summary>
        public bool verboseLogging = false;

        /// <summary>多边形边内侧 N 格禁建（阶段4 安全约束）。</summary>
        public int borderNoBuildDistance = 3;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref borderPreloadDistance, "borderPreloadDistance", 15);
            Scribe_Values.Look(ref preloadAllNeighborsOnStart, "preloadAllNeighborsOnStart", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref borderNoBuildDistance, "borderNoBuildDistance", 3);
            base.ExposeData();
        }
    }
}
