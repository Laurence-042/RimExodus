using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>封存判定结果（<see cref="SeamlessMapModificationTracker.Evaluate"/>）。</summary>
    internal enum PreserveDecision
    {
        /// <summary>居住区为空 = 未修改，走普通删除。</summary>
        None,

        /// <summary>居住区格数 ≥ 阈值（或阈值 = 0 且非空）= 到达删除距离时自动封存。</summary>
        Auto,

        /// <summary>居住区非空但 &lt; 阈值 = 弹窗询问（提示未禁用时；本轮不删）。</summary>
        BelowThreshold,
    }

    /// <summary>
    /// "玩家改造过此图"判定与居住区语义的唯一出处（2026-09 前哨保留，用户定夺"语义拎出来，
    /// 不与删除策略混杂"——对照 <see cref="SeamlessMapGovernance"/> 判定归一层纪律）。
    ///
    /// 语义 = 居住区（原版 Home area）格数与可配置阈值的比较：
    /// - 居住区是原版"自动居住区"的默认产物（玩家建造自动扩展、房间闭合自动填充），
    ///   同时是玩家的显式控制手段（"画居住区 = 画保留区"）——刻意**不做任何自行膨胀**，
    ///   精细控制居住区的玩家的预期不被破坏（用户定夺 2026-09：用且仅用居住区）；
    /// - 未铺地板的采掘隧道不会自动进居住区（采矿不扩 home area）——重放时山体在该处
    ///   长回复塞洞口属预期，对策 = 玩家补画居住区（不改原版语义）；
    /// - v1 范围仅地块图（<see cref="MapParent_SeamlessTile"/>，营地自动覆盖），原生家族不纳入
    ///   （Settlement 删图留对象/再访重生成驻军的语义与整图快照恢复天然冲突，单独设计）。
    ///
    /// 本类只出判定；删除/封存/弹窗策略在 <see cref="SeamlessDormancyGovernor"/>，记录数据在 <see cref="ZoneMapRecord"/>。
    /// </summary>
    internal static class SeamlessMapModificationTracker
    {
        /// <summary>
        /// 居住区格数与阈值比较。Area.TrueCount 是 BoolGrid 维护的计数器（O(1)，Area.cs:28），
        /// 每轮 Sweep 对每张管辖图调用一次，成本可忽略。
        /// </summary>
        internal static PreserveDecision Evaluate(Map map, out int homeCells)
        {
            homeCells = 0;
            if (map == null || map.Disposed) return PreserveDecision.None;
            var home = map.areaManager?.Home;
            if (home == null) return PreserveDecision.None;
            homeCells = home.TrueCount;
            if (homeCells <= 0) return PreserveDecision.None;
            // 阈值消费层 clamp [0,500]（Settings 本体不 clamp，仓库惯例：clamp 放消费处）。
            // 0 = 关闭阈值：任何非空居住区都自动封存（弹窗永不触发）。
            var threshold = Mathf.Clamp(RimExodusMod.Settings?.dormancyPreserveHomeAreaThreshold ?? 20, 0, 500);
            return threshold <= 0 || homeCells >= threshold
                ? PreserveDecision.Auto
                : PreserveDecision.BelowThreshold;
        }
    }
}
