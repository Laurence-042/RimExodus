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

        /// <summary>
        /// 详细诊断日志开关（默认 false）。
        /// 开启后打印所有 [RimExodus] Log.Message 诊断信息（每 tick/每次玩家操作的高频日志），
        /// 关闭时只保留 Log.Warning / Log.Error 等真正的问题日志。
        /// </summary>
        public bool verboseLogging = false;

        /// <summary>
        /// 多边形边内侧 N 格禁建（阶段4 安全约束，防接缝卡死）。
        /// 玩家可在接缝重叠带及其内侧建造建筑，用建筑改变寻路把 pawn 困在 void 一侧——
        /// 一旦 pawn 站上指向"已被建筑封死对端"的传送点，就会卡死。
        /// 故多边形边内侧必须禁止建造，从根上杜绝这种滥用。
        /// 默认 <see cref="SeamlessTileManager.SeamOverlap"/>(2) + 1 = 3，与预加载触发距离（borderPreloadDistance=15）语义不同、独立配置。
        /// 0 表示禁用此约束。
        /// </summary>
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
