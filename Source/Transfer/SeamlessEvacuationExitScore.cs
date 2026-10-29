namespace RimExodus
{
    /// <summary>
    /// 撤离出口排序：未访问且向前 > 未访问但侧向/后退 > 已访问兜底。
    /// 向前候选比较 progress / pathCost，不混合角度、距离与加载状态的任意权重。
    /// direction 不必归一化：同一次选择共享其长度，交叉相乘即可精确比较效率。
    /// </summary>
    internal readonly struct SeamlessEvacuationExitScore
    {
        internal readonly long Progress;
        internal readonly int PathCost;
        internal readonly bool Visited;

        internal SeamlessEvacuationExitScore(long progress, int pathCost, bool visited)
        {
            Progress = progress;
            PathCost = pathCost;
            Visited = visited;
        }

        private int Tier => Visited ? 2 : Progress > 0 ? 0 : 1;

        // 调用方只提交正的有限成本（排除脚下格与不可达格）。负值表示本候选更优。
        internal int CompareTo(SeamlessEvacuationExitScore other)
        {
            var order = Tier.CompareTo(other.Tier);
            if (order != 0) return order;
            if (Tier == 0)
            {
                order = (other.Progress * PathCost).CompareTo(Progress * other.PathCost);
                if (order != 0) return order;
                // 同效率优先推进更远，避免离散边角上短小等价段反复换图。
                order = other.Progress.CompareTo(Progress);
                if (order != 0) return order;
            }
            return PathCost.CompareTo(other.PathCost);
        }
    }
}
