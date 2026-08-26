using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图内据点交易（2026-08 Settlement 无缝接入，用户定夺："直接走原版远行队交易，patch 让地图内的
    /// 交易识别 pawn 身上的内容，并在交易后直接在贸易商身边掉落交易品、白银之类的"）。
    ///
    /// 统一守卫：谈判者在**真实**远行队（GetCaravan() != null）→ 放行原版——**世界地图远行队交易零回归**。
    /// 注意（2026-08 影子远行队 v2）：据点图殖民者的 holdingOwner 指向常驻影子，但 Pawn.GetCaravan 的
    /// patch 对影子返回 null（还原地图语义），故本守卫对影子成员恒走下方地图分支——正确。
    /// 地图内谈判者（走到据点里与贸易商对话的 spawned pawn）时：
    /// - ColonyThingsWillingToBuy：原版体开头解引用远行队（AllInventoryItems(null) 会 NRE），必须 Prefix
    ///   接管——改为枚举谈判者所在图全部玩家阵营 pawn 的 inventory（"识别 pawn 身上的内容"；随身白银即支付来源）；
    /// - GiveSoldThingToPlayer：成交物/白银在贸易商身边掉落（GenPlace.Near——原版 Pawn_TraderTracker 的
    ///   落地方式；买入 pawn 则落回地图，PreTraded 已处理派系转换）；
    /// - TradeDeal.InSellablePosition：随身物非 Spawned，原版对非远行队谈判者直接判不可卖——放行玩家 pawn
    ///   库存内的物品。
    /// GiveSoldThingToTrader 不 patch：其物品分支不碰远行队（只有卖 pawn 分支解引用 caravan），v1 只支持
    /// 随身物品交易，卖入据点 stock 走原版 stock.TryAdd（与远行队交易同语义）。随行囚犯/奴隶出售为后续扩展。
    /// </summary>
    internal static class SettlementTradeInMap
    {
        /// <summary>谈判者所在图全部玩家阵营 spawned pawn 的随身物品（"远行队的地图版"——走来的队伍就是商队）。</summary>
        public static IEnumerable<Thing> PlayerPawnInventoriesOnMap(Map map)
        {
            if (map == null) yield break;
            foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                if (pawn?.inventory == null) continue;
                foreach (var thing in pawn.inventory.innerContainer)
                {
                    yield return thing;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.ColonyThingsWillingToBuy))]
    static class Patch_SettlementTrade_ColonyThingsWillingToBuy
    {
        static bool Prefix(Pawn playerNegotiator, ref IEnumerable<Thing> __result)
        {
            if (playerNegotiator == null || playerNegotiator.GetCaravan() != null) return true;
            __result = SettlementTradeInMap.PlayerPawnInventoriesOnMap(playerNegotiator.Map);
            return false;
        }
    }

    [HarmonyPatch(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.GiveSoldThingToPlayer))]
    static class Patch_SettlementTrade_GiveSoldThingToPlayer
    {
        static bool Prefix(Settlement_TraderTracker __instance, Pawn playerNegotiator, Thing toGive, int countToGive)
        {
            if (playerNegotiator == null || playerNegotiator.GetCaravan() != null) return true;

            // 买入 pawn（从据点 stock 买奴隶/动物）：落回地图谈判者附近；PreTraded(PlayerBuys) 已做
            // 派系转换（SetFaction 玩家 / 奴隶 guest 状态），镜像远行队 AddPawn 的地图版。
            if (toGive is Pawn boughtPawn)
            {
                boughtPawn.PreTraded(TradeAction.PlayerBuys, playerNegotiator, __instance.settlement);
                if (boughtPawn.IsWorldPawn())
                {
                    Find.WorldPawns.RemovePawn(boughtPawn);
                }
                var pawnMap = playerNegotiator.Map;
                var cell = CellFinder.RandomClosewalkCellNear(playerNegotiator.Position, pawnMap, 5);
                GenSpawn.Spawn(boughtPawn, cell, pawnMap);
                return false;
            }

            // 物品：掉落在贸易商身边（用户定夺）；贸易商缺失（理论不可达——对话由其发起）回落谈判者身边。
            var dropNear = SeamlessSettlementTrader.TraderPawnOf(playerNegotiator.Map) ?? playerNegotiator;
            Thing thing = toGive.SplitOff(countToGive);
            thing.PreTraded(TradeAction.PlayerBuys, playerNegotiator, __instance.settlement);
            if (!GenPlace.TryPlaceThing(thing, dropNear.Position, dropNear.MapHeld, ThingPlaceMode.Near))
            {
                Log.Error($"[RimExodus] Could not place bought thing {thing} near the settlement trader.");
                thing.Destroy();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "InSellablePosition")]
    static class Patch_TradeDeal_InSellablePosition
    {
        // 目标为 private 方法；out 参数在 Prefix 以 ref 声明（Harmony 惯例），return false 前必须赋值。
        static bool Prefix(Thing t, ref bool __result, ref string reason)
        {
            if (t.ParentHolder is Pawn_InventoryTracker tracker && tracker.pawn.Faction == Faction.OfPlayer)
            {
                __result = true;
                reason = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 贸易商头顶问号（2026-08，用户定夺"参考原生 Creepjoiner 机制、勿自绘图标"）。
    /// 原版 <see cref="Pawn.ShouldShowQuestionMark"/>（public virtual，仅 <c>PawnUIOverlay</c> 一处
    /// 消费）的 fallback 分支 <c>return CanTradeNow</c> 正是访客商人头顶问号的来源（Creepjoiner 的
    /// 标记走同一函数的 Anomaly 分支）——注册的据点贸易商在此返回 true，整条原生绘制链
    /// （OverlayTypes.QuestionMark 脉冲材质/altitude/雾与视野裁剪）零成本复用，一行绘制代码不写。
    /// 显隐天然正确：图上 parent 仍为 Settlement（败亡后 TryGetSettlement 落空 → 问号自动消失）、
    /// pawn 未绘制（雾外/视野外）时整条 overlay 链本就不画。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ShouldShowQuestionMark))]
    static class Patch_Pawn_ShouldShowQuestionMark_SettlementTrader
    {
        static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__result) return;
            if (SeamlessSettlementTrader.TryGetSettlement(__instance, out _))
            {
                __result = true;
            }
        }
    }
}
