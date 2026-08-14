using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 邻居预加载的静态入口（阶段4a）。
    /// 屏蔽"源地块是锚点还是地块地图"的差异：每张地图都有 <see cref="SeamlessTileManager"/> 组件
    /// （由 Map.FillComponents 自动实例化），从源地块取其 Manager 再调 <see cref="SeamlessTileManager.TryPreloadNeighbor"/>。
    ///
    /// 异步化：预加载请求登记到队列（<see cref="QueuePreload"/>），由 <see cref="SeamlessTileManager.MapComponentTick"/>
    /// 在下一 tick 消费（<see cref="ConsumeQueued"/>)。避免在 Pawn_JobTracker.StartJob 调用栈内同步生成地图
    /// （MapGenerator.GenerateMap 是重操作，同步执行会阻塞当前 tick 数百毫秒）。
    /// </summary>
    public static class SeamlessTilePreloader
    {
        /// <summary>待处理的预加载请求队列（originMap, targetWorldTile）。每条请求由首个 tick 的 Manager 消费。</summary>
        private static readonly List<PreloadRequest> pendingQueue = new List<PreloadRequest>();

        /// <summary>已入队的 (originMap, targetWorldTile) 集合，防重复入队。</summary>
        private static readonly HashSet<int> queuedHashes = new HashSet<int>();

        private struct PreloadRequest
        {
            public Map originMap;
            public int targetWorldTile;
        }

        /// <summary>
        /// 登记一个预加载请求到队列（异步）。不立即生成，由 MapComponentTick 在下一 tick 消费。
        /// 幂等：同一 (originMap, targetWorldTile) 重复入队只保留一条。
        /// </summary>
        public static void QueuePreload(Map originMap, int targetWorldTile)
        {
            if (originMap == null || targetWorldTile < 0) return;
            var hash = HashKey(originMap, targetWorldTile);
            if (queuedHashes.Contains(hash)) return;
            queuedHashes.Add(hash);
            pendingQueue.Add(new PreloadRequest { originMap = originMap, targetWorldTile = targetWorldTile });
        }

        /// <summary>
        /// 由 SeamlessTileManager.MapComponentTick 调用：消费队列中所有待处理请求（每 tick 处理一次，可能含多条）。
        /// 生成是重操作，但放在 tick 之间执行不阻塞玩家输入响应。
        /// </summary>
        public static void ConsumeQueued()
        {
            if (pendingQueue.Count == 0) return;

            // 快照后清空（消费过程中可能因绑定/刷新产生新的间接请求，下一 tick 再处理）。
            var snapshot = new List<PreloadRequest>(pendingQueue);
            pendingQueue.Clear();
            queuedHashes.Clear();

            foreach (var req in snapshot)
            {
                if (req.originMap == null) continue;
                var manager = req.originMap.GetComponent<SeamlessTileManager>();
                if (manager == null) continue;
                manager.TryPreloadNeighbor(req.targetWorldTile);
            }
        }

        /// <summary>同步预加载入口（保留供 Dev 命令等需要立即生成的场景使用）。</summary>
        public static bool TryPreload(Map originMap, int targetWorldTile)
        {
            if (originMap == null) return false;
            var manager = originMap.GetComponent<SeamlessTileManager>();
            if (manager == null) return false;
            return manager.TryPreloadNeighbor(targetWorldTile);
        }

        private static int HashKey(Map originMap, int targetWorldTile)
        {
            var mapId = originMap?.uniqueID ?? 0;
            return (mapId * 73856093) ^ (targetWorldTile * 19349663);
        }
    }
}
