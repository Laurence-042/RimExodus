using System;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 格网格上的切比雪夫（Chebyshev）几何统一实现（2026-08 用户纪律：相似功能统一实现，勿再手搓）。
    ///
    /// **邻格遍历的权威实现 = 原版 <see cref="GenAdj"/> 两个静态数组**，全项目一律引用它们：
    /// - 8 邻环（含对角、不含中心）→ <see cref="GenAdj.AdjacentCells"/>（偏移数组，cell + offset 用）；
    /// - 含中心 9 格窗口 → <see cref="GenAdj.AdjacentCellsAndInside"/>（[0] 是中心 Zero）。
    /// 历史（2026-08 教训）：接缝带膨胀（BuildSeamBand）/权重深度（ComputeVoidBand）/卷积采样
    /// （Convolve3x3 族）/void 岩石判邻（VoidRockLink）各自手搓 dx/dz 循环与方向表，口径漂移
    /// （正交 4 邻 vs 切比雪夫 8 邻曾混用）——统一到本类与 GenAdj 数组后新增消费者禁止再自建。
    ///
    /// **消费口径地图**（改语义须全链路同步）：
    /// - 接缝带 B = Cheb(D,1) 膨胀、过渡带/外条带多轮膨胀：BuildSeamBand；
    /// - 权重衰减 dOut/dSq（参考域深度、方形边距离）：SeamBandInfo 深度字典（膨胀时逐轮记深度）；
    /// - void 岩石判邻与对端一致性 3×3 窗口：SeamlessVoidRockLink；
    /// - 混合 3×3 卷积采样：Convolve3x3 / Convolve3x3FromStrip。
    /// 半径 &gt;1 的切比雪夫方形窗口（如道路保护 RoadGuardRadius=3）无原版数组可用，保留显式
    /// dx/dz 双层循环，但必须注释指回本类口径。
    /// </summary>
    internal static class SeamlessGridMath
    {
        /// <summary>两格间的切比雪夫距离（x/z 轴差的最大绝对值；对角邻 = 1）。</summary>
        public static int ChebyshevDistance(IntVec3 a, IntVec3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Math.Abs(dx) > Math.Abs(dz) ? Math.Abs(dx) : Math.Abs(dz);
        }
    }
}
