using Verse;

namespace RimExodus
{
    /// <summary>
    /// pawn 所在地变动的统一追踪底座（2026-08-29 用户定夺收拢架构）：**影子远行队（SeamlessShadowCaravan）
    /// 与地图滚动休眠（SeamlessDormancyGovernor）本质都是"每个 pawn 现在在哪张图/tile"这一事实的派生计算**
    /// ——前者派生"据点图常驻远行队名单"，后者派生"世界网格 BFS 距离与睡/删决策"。两者必须共享：
    /// ①同一个触发系统（本类——事件源只认本类入口，不再各自登记）；
    /// ②同一个底层更新机制（governor GameComponentTick 内固定顺序的"位置刷新段"，安全执行点不变）；
    /// ③只在派生逻辑处分别实现（影子 = 期望集/成员同步/注入副作用清扫 + 60t 轮询；休眠 = BFS 距离/保活/睡删 + 600t 轮询）。
    ///
    /// 事件源全集（冻结清单，新增 pawn 所在地变动源时在此收口、勿散落直调消费者）：
    /// 跨缝传送完成（SeamlessMapTransferTrigger.AfterTransfer）/ 远行队组队离图（MapParent.Notify_CaravanFormed
    /// Postfix）/ 远行队进图（Patch_CaravanEnterMap_Enter）/ 删图（SeamlessDormancyManager.Forget → NotifyMapRemoving）。
    ///
    /// 纪律（继承原 RequestSweepSoon 的教训，2026-08）：**事件回调只置旗标、绝不直接执行刷新**——
    /// 刷新可能 Sleep（摘全局 tick 表）/ 拆影子（改写 holdingOwner），落在 thing tick 遍历中途不安全；
    /// GameComponentTick 是安全执行点（跨缝传送的事件发于 pather tick 内，但消费在下一 GameComponentTick）。
    /// </summary>
    internal static class SeamlessPawnLocationTracker
    {
        /// <summary>事件脏旗标（事件源置位，GameComponentTick 位置刷新段独占消费）。</summary>
        private static bool dirty;

        /// <summary>pawn 所在地已变动（事件源唯一入口）：下一 GameComponentTick 的位置刷新段即时刷新全部消费者。</summary>
        internal static void NotifyChanged()
        {
            dirty = true;
        }

        /// <summary>读并清脏旗标（governor GameComponentTick 位置刷新段独占调用）。返回 true = 有事件待消费。</summary>
        internal static bool ConsumeDirty()
        {
            var wasDirty = dirty;
            dirty = false;
            return wasDirty;
        }

        /// <summary>
        /// 地图即将删除（Forget 处进底座——删图 = 该图全体 pawn 所在地批量变动）。当前消费者 = 影子远行队
        /// （删图前全图清扫 spawnedThings 陈旧条目，使 Deinit 的 DecrementMapIndex 索引补偿与持有链遍历
        /// 永跑干净列表，见 SeamlessShadowCaravan.OnMapRemoving）；未来新消费者在此扩展转发。
        /// 刻意立即执行而非置旗标：调用点（两个删图叶子）本就在 DeinitAndRemoveMap 之前的最后一步，
        /// 置旗标反而引入"下一 tick 才清"的窗口——本事件是位置事件中唯一安全即时执行的（纯内存列表清理，
        /// 不碰 tick 表/holdingOwner，无 thing tick 中途风险）。
        /// </summary>
        internal static void NotifyMapRemoving(Map map)
        {
            SeamlessShadowCaravan.OnMapRemoving(map);
        }
    }
}
