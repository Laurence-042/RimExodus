using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 据点图上的贸易商登记（MapComponent，由 Map.FillComponents 自动挂到每张图，仅在 Settlement 图上被赋值）。
    /// <see cref="Scribe_References"/> 序列化人选——读档不丢指定；图被滚动删除即随图消失（再生成时由
    /// <see cref="SeamlessSettlementTrader.EnsureTraderAssigned"/> 重选）。
    /// 不写 <c>pawn.trader</c> / <c>wantsToTradeWithColony</c> 等原版字段（2026-08 用户定夺：对话面板方案，
    /// 不走行商化——防原生 FloatMenuOptionProvider_Trade 双入口与 wantsToTrade 的 AI 副作用）。
    /// </summary>
    public class SeamlessSettlementTraderComp : MapComponent
    {
        /// <summary>本图登记的据点贸易商（对话入口；对应 Settlement = map.Parent as Settlement）。</summary>
        public Pawn traderPawn;

        public SeamlessSettlementTraderComp(Map map) : base(map) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref traderPawn, "traderPawn");
        }
    }

    /// <summary>
    /// 据点贸易商指定器（2026-08 Settlement 无缝接入，用户设计草案见 doc/TODO.md）。
    /// 好感度可交易的据点在图生成后从驻军中按 7 条件选"价格最高（MarketValue）"者为贸易商——
    /// 玩家右键其对话（"远行者，你想要做什么？"面板 = 交易 + 据点 gizmo 集合，见 SettlementTraderDialog）。
    ///
    /// 指定时机 = 仅图生成时一次性（用户定夺 2026-08）：不因死亡/倒地/被俘/敌对化补选——失效则本图
    /// 存续期内无对话入口，图被滚动删除后重生成会重选。敌对/无 TraderKind 的据点不指定
    /// （"好感度低到敌对的，正常结束，玩家看到全是敌人的据点"）。
    /// </summary>
    public static class SeamlessSettlementTrader
    {
        /// <summary>意识/移动能力下限（"当前可以行动"——与原版 CanCasuallyInteractNow 的 0.3 阈值同源）。</summary>
        private const float MinCapableCapacityLevel = 0.3f;

        /// <summary>pawn 是否其所在图的登记贸易商；是则给出对应 Settlement。</summary>
        public static bool TryGetSettlement(Pawn pawn, out Settlement settlement)
        {
            settlement = null;
            if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Map.Disposed) return false;
            var comp = pawn.Map.GetComponent<SeamlessSettlementTraderComp>();
            if (comp == null || comp.traderPawn != pawn) return false;
            settlement = pawn.Map.Parent as Settlement;
            return settlement != null;
        }

        /// <summary>贸易商当前能否对话（镜像原版 Pawn_TraderTracker.CanTradeNow 的身体/交互门槛）。</summary>
        public static bool CanTalkNow(Pawn trader)
        {
            return trader != null && trader.Spawned && !trader.Dead && !trader.Downed
                && !trader.IsPrisoner && trader.CanCasuallyInteractNow();
        }

        /// <summary>据点图（若登记过）的贸易商；供交易 patch 决定成交物掉落位置。</summary>
        public static Pawn TraderPawnOf(Map map)
        {
            return map?.GetComponent<SeamlessSettlementTraderComp>()?.traderPawn;
        }

        /// <summary>生成后指定贸易商（幂等：已有指定直接返回——一次性语义）。门槛与候选条件见类注释。</summary>
        public static void EnsureTraderAssigned(Settlement settlement)
        {
            if (settlement == null) return;
            var map = settlement.Map;
            if (map == null || map.Disposed) return;
            var comp = map.GetComponent<SeamlessSettlementTraderComp>();
            if (comp == null || comp.traderPawn != null) return; // 生成时一次性（用户定夺）：已有指定不重选。

            var faction = settlement.Faction;
            if (faction == null || faction.IsPlayer) return;
            if (settlement.TraderKind == null) return; // baseTraderKinds 为空的派系天然无贸易商。
            // 好感度可交易口径 = 原版 CaravanArrivalAction_Trade.CanTradeWith 同款（非永久敌对且非敌对）。
            if (faction.def.permanentEnemy || faction.HostileTo(Faction.OfPlayer)) return;

            // 候选过滤（用户 7 条件：派系成员由枚举保证）。
            var candidates = new List<Pawn>();
            foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(faction))
            {
                if (pawn == null || pawn.Dead || !pawn.Spawned) continue;
                if (!pawn.RaceProps.Humanlike) continue; // 非动物、非机械体。
                if (pawn.WorkTagIsDisabled(WorkTags.Social)) continue; // 可以承担社交工作。
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) continue; // Talking 原版在具体工作里单独查。
                if (pawn.Downed) continue; // 当前可以行动：
                if (pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) < MinCapableCapacityLevel) continue;
                if (pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < MinCapableCapacityLevel) continue;
                if (pawn.trader != null && pawn.trader.traderKind != null) continue; // 并非已经是贸易商。
                if (pawn.IsPrisoner || pawn.IsSlave) continue; // 并非囚犯、奴隶。
                candidates.Add(pawn);
            }
            if (candidates.Count == 0)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Settlement at tile {settlement.Tile.tileId}: no pawn meets the trader requirements, none assigned.");
                return;
            }
            // 价格最高（MarketValue）优先，逐个探测可达性——首个玩家走得到者当选（避免对全部候选跑可达性）。
            candidates.Sort((a, b) => b.MarketValue.CompareTo(a.MarketValue));

            var edgeCells = SeamlessEdgeCells.GetSeamEdgeCells(map);
            foreach (var pawn in candidates)
            {
                if (!ReachableFromSeam(map, pawn, edgeCells)) continue; // 在可达位置（可在室内）。
                comp.traderPawn = pawn;
                Log.Message($"[RimExodus] Settlement at tile {settlement.Tile.tileId} trader assigned: " +
                            $"{pawn.LabelShortCap} (market value {pawn.MarketValue:F0}, kind {settlement.TraderKind.defName}).");
                return;
            }
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Settlement at tile {settlement.Tile.tileId}: all {candidates.Count} trader candidates unreachable from seam, none assigned.");
        }

        /// <summary>
        /// 可达性（"玩家的 pawn 必须能正常走到其所在位置"）：任一传送圈格能走到 pawn 所在格（Touch，可在室内）。
        /// TraverseMode.PassDoors + Danger.Some——殖民者可开 NPC 据点的门、绕开重度危险，
        /// 与原版 Pawn_TraderTracker.ReachableForTrade 同款口径。
        /// </summary>
        private static bool ReachableFromSeam(Map map, Pawn pawn, List<IntVec3> edgeCells)
        {
            if (edgeCells.NullOrEmpty()) return false;
            foreach (var cell in edgeCells)
            {
                if (map.reachability.CanReach(cell, pawn.Position, PathEndMode.Touch, TraverseMode.PassDoors, Danger.Some))
                    return true;
            }
            return false;
        }
    }
}
