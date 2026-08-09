using Verse;

namespace RimExodus
{
    /// <summary>
    /// 邻居预加载的静态入口（阶段4a）。
    /// 屏蔽"源地图是锚点还是口袋地块"的差异：每张地图都有 <see cref="SeamlessTileManager"/> 组件
    /// （由 Map.FillComponents 自动实例化），从源地图取其 Manager 再调 <see cref="SeamlessTileManager.TryPreloadNeighbor"/>。
    /// </summary>
    public static class SeamlessTilePreloader
    {
        /// <summary>
        /// 在 sourceMap 上预加载指向 targetWorldTile 的邻居地块。
        /// 支持从任意地图（锚点 A 或口袋地块 B）发起：内部取该地图的 SeamlessTileManager 调用实例方法。
        /// </summary>
        /// <returns>是否触发了生成。</returns>
        public static bool TryPreload(Map sourceMap, int targetWorldTile)
        {
            if (sourceMap == null) return false;
            var manager = sourceMap.GetComponent<SeamlessTileManager>();
            if (manager == null)
            {
                return false;
            }
            return manager.TryPreloadNeighbor(targetWorldTile);
        }
    }
}
