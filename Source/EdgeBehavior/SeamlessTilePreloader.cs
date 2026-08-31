using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 邻居预加载的静态入口（阶段4a）。
    /// 屏蔽"源图是原生 parent 图（家园/原生家族）还是地块图"的差异：每张地图都有
    /// <see cref="SeamlessTileManager"/> 组件（由 Map.FillComponents 自动实例化），从源图取其
    /// Manager 再调 <see cref="SeamlessTileManager.TryPreloadNeighbor"/>。
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

        /// <summary>
        /// 进度提示预热帧门（2026-08-31）：上次入队帧 +2 之前不消费。重操作（快照精简重生成 /
        /// 原版同步生成 / POI 原生生成）单帧冻结，冻结帧内 OnGUI 无从重绘——进度标签必须先
        /// 渲染过至少一帧，冻结期间屏幕保持的才是带标签画面。不设门则入队与消费可能落在同一
        /// 渲染帧（同 tick 的 MapPostTick 消费 / 高速档一帧多 tick / 玩家点击在 OnGUI 事件段入队
        /// 且该帧 Repaint 已过），玩家看到的就是无标签画面直接卡住（2026-08-31 实测教训）。
        /// +2 而非 +1：覆盖"OnGUI 事件段入队、首绘在下一帧"的最晚情形（Update 先于 OnGUI）。
        /// </summary>
        private static int minConsumeFrame = -1;

        private struct PreloadRequest
        {
            public Map originMap;
            public int targetWorldTile;
            // 触发位置（2026-08-30，中心走廊端点算法）：玩家 goto 的目标格——走廊在触发图 A 侧的
            // 可达性锚点（"代表格须可达触发位置"，比 A 中心更贴场景：pawn 就在那）。null = 未知
            // （Dev / governor 兜底触发），走廊算法回落 A 中心。
            public IntVec3? triggerCell;
        }

        /// <summary>
        /// 登记一个预加载请求到队列（异步）。不立即生成，由 MapComponentTick 在下一 tick 消费。
        /// 幂等：同一 (originMap, targetWorldTile) 重复入队只保留一条。
        /// </summary>
        public static void QueuePreload(Map originMap, int targetWorldTile, IntVec3? triggerCell = null)
        {
            if (originMap == null || targetWorldTile < 0) return;
            var hash = HashKey(originMap, targetWorldTile);
            if (queuedHashes.Contains(hash)) return;
            queuedHashes.Add(hash);
            pendingQueue.Add(new PreloadRequest { originMap = originMap, targetWorldTile = targetWorldTile, triggerCell = triggerCell });
            // 进度提示点亮（2026-08-31）+ 帧门（标签须先渲染过一帧，见 minConsumeFrame 注释）。
            MapGenerationProgressUI.NotifyQueued();
            minConsumeFrame = System.Math.Max(minConsumeFrame, UnityEngine.Time.frameCount + 2);
        }

        /// <summary>队列中是否还有待处理请求（进度提示的熄灭判据之一）。</summary>
        internal static bool HasPendingRequests => pendingQueue.Count > 0;

        /// <summary>
        /// 由 SeamlessTileManager.MapComponentTick 调用：消费队列中所有待处理请求（每 tick 处理一次，可能含多条）。
        /// 生成是重操作，但放在 tick 之间执行不阻塞玩家输入响应。
        /// </summary>
        public static void ConsumeQueued()
        {
            if (pendingQueue.Count == 0) return;
            // 帧门（进度提示预热）：标签渲染过至少一帧后才进重操作，见 minConsumeFrame 注释。
            if (UnityEngine.Time.frameCount < minConsumeFrame) return;

            // 快照后清空（消费过程中可能因绑定/刷新产生新的间接请求，下一 tick 再处理）。
            var snapshot = new List<PreloadRequest>(pendingQueue);
            pendingQueue.Clear();
            queuedHashes.Clear();

            foreach (var req in snapshot)
            {
                if (req.originMap == null) continue;
                var manager = req.originMap.GetComponent<SeamlessTileManager>();
                if (manager == null) continue;
                manager.TryPreloadNeighbor(req.targetWorldTile, req.triggerCell);
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
