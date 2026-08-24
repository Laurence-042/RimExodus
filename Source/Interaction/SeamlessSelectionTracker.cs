using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨图选中状态保持器。
    ///
    /// 问题：多 pawn 跨图是逐个的（每个 pawn 各自踩传送点，跨多个 tick）。
    /// <see cref="SeamlessMapTransferTrigger"/> 在首个殖民者跨图时调
    /// <see cref="SeamlessCameraFocus.TryAutoFocusOnArrival"/> 切图，而切图触发原版
    /// <c>MapInterface.Notify_SwitchedMap</c> → <c>selector.ClearSelection()</c>，
    /// 把**尚未跨图**的其余 pawn 从选中列表清掉。它们后续跨图时 IsSelected 返回 false，
    /// 不被 re-Select，导致跨图后丢失选中。
    ///
    /// 修复：本跟踪器维护一个"应保持选中的 pawn"集合。玩家发起跨图移动时（前端菜单），
    /// 把当前选中的 pawn 登记进来；每个 pawn 实际跨图后，只要在集合里就 re-Select 并移除。
    /// 这样无论切图清空几次，集合始终记得"这批 pawn 要保持选中"。
    /// </summary>
    public static class SeamlessSelectionTracker
    {
        private static readonly HashSet<Pawn> pending = new HashSet<Pawn>();

        /// <summary>
        /// 登记一个应保持选中的 pawn（幂等）。
        /// 由前端跨图命令路径在玩家下达移动指令时调用。
        /// </summary>
        public static void Register(Pawn pawn)
        {
            if (pawn != null) pending.Add(pawn);
        }

        /// <summary>
        /// 判断 pawn 是否在保持集中（应跨图后 re-Select），若是则从集合移除（一次性消费）。
        /// 由转移流程在 re-Select 决策时调用。
        /// </summary>
        public static bool Consume(Pawn pawn)
        {
            return pawn != null && pending.Remove(pawn);
        }

        /// <summary>清除已失效的登记（pawn 死亡/销毁/不在任何地图），防止残留。</summary>
        public static void PurgeInvalid()
        {
            pending.RemoveWhere(p => p == null || p.Destroyed || p.Dead || !p.Spawned);
        }
    }
}
