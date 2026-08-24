using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 据点贸易商对话入口（2026-08 Settlement 无缝接入，用户设计）。
    ///
    /// 右键本图（或跨缝邻图——点击重放天然支持）的登记贸易商 → "Talk to X" → 走到其身边
    /// （<see cref="JobDriver_TalkWithSettlementTrader"/>，克隆原版 TradeWithPawn 驱动结构；跨缝下令被
    /// Pawn_PathFollower.StartPath 桥接按结构判据通用包装）→ 打开 <see cref="SettlementTraderDialog"/>
    /// 构建的原生 DiaOption 面板（"远行者，你想要做什么？"）：交易 + 据点 gizmo 集合 + 离开。
    ///
    /// 交易不在此打开 Dialog_Trade——面板内"交易"按钮才开（Settlement 本体即 ITrader，全原版远行队
    /// 交易语义；地图内谈判者的随身物识别与成交物掉落见 Patches_SettlementTrade）。
    /// Provider 经 FloatMenuMakerMap.Init 的 AllSubclassesNonAbstract 扫描自动注册（公开非抽象即生效，零 patch）。
    /// </summary>
    public class FloatMenuOptionProvider_SettlementTalk : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (clickedPawn == null) yield break;
            if (!SeamlessSettlementTrader.TryGetSettlement(clickedPawn, out var settlement)) yield break;

            if (!SeamlessSettlementTrader.CanTalkNow(clickedPawn))
            {
                yield return new FloatMenuOption("RimExodus_SettlementTraderBusy".Translate(clickedPawn.LabelShortCap), null);
                yield break;
            }

            var negotiator = context.FirstSelectedPawn;
            if (negotiator == null) yield break;
            if (!negotiator.CanReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly))
            {
                yield return new FloatMenuOption("RimExodus_SettlementTraderNoPath".Translate(), null);
                yield break;
            }

            Action action = delegate
            {
                var jobDef = DefDatabase<JobDef>.GetNamedSilentFail("RimExodus_TalkWithSettlementTrader");
                if (jobDef == null)
                {
                    Log.Error("[RimExodus] JobDef RimExodus_TalkWithSettlementTrader not found (Defs/JobDefs/Jobs.xml missing?).");
                    return;
                }
                var job = JobMaker.MakeJob(jobDef, clickedPawn);
                job.playerForced = true;
                negotiator.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            };
            yield return FloatMenuUtility.DecoratePrioritizedTask(
                new FloatMenuOption("RimExodus_TalkToSettlementTrader".Translate(clickedPawn.LabelShortCap),
                    action, MenuOptionPriority.InitiateSocial, null, clickedPawn),
                negotiator, clickedPawn);
        }
    }

    /// <summary>
    /// 对话 job 驱动（JobDef = RimExodus_TalkWithSettlementTrader，见 1.6/Defs/JobDefs/Jobs.xml）。
    /// 逐语句对齐原版 JobDriver_TradeWithPawn：预约 → GotoThing(Touch) → 到位开窗——
    /// 差异仅在开的是我们的对话面板而非 Dialog_Trade。
    /// </summary>
    public class JobDriver_TalkWithSettlementTrader : JobDriver
    {
        private Pawn Trader => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Trader, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch)
                .FailOn(() => !SeamlessSettlementTrader.CanTalkNow(Trader));
            Toil talk = ToilMaker.MakeToil("MakeNewToils");
            talk.initAction = delegate
            {
                var actor = talk.actor;
                if (SeamlessSettlementTrader.TryGetSettlement(Trader, out var settlement)
                    && SeamlessSettlementTrader.CanTalkNow(Trader))
                {
                    SettlementTraderDialog.Open(actor, settlement, Trader);
                }
            };
            yield return talk;
        }
    }

    /// <summary>
    /// 据点贸易商对话面板构建器："远行者，你想要做什么？"（用户设计文本）。
    /// 载体 = 原生 DiaOption 基础设施（用户定夺 2026-08，勿回退为自绘 Window——对 UI mod/字体缩放不兼容）：
    /// <see cref="Dialog_NodeTreeWithFactionInfo"/> 是 ChoiceLetter.OpenLetter 打开信件所用的同一面板
    /// （creepjoiner 面板同款渲染链），纯对话框不进信件堆栈——玩家就在贸易商身边，重谈右键再下指令即可。
    /// 选项 = 交易（Dialog_Trade(negotiator, settlement)，Settlement 本体即 ITrader）+
    /// <see cref="Settlement.GetGizmos"/> 枚举项（mod 经 WorldObjectComp 加的据点交互天然出现——
    /// "天然适配各种给据点加特殊交互选项的 mod"；"进攻"在 GetCaravanGizmos 不在其中）+ 离开。
    /// 排除项：原版"查看地图"（我们就站在图上）与"组建远行队"教学项（语境不适用）。
    /// </summary>
    public static class SettlementTraderDialog
    {
        public static void Open(Pawn negotiator, Settlement settlement, Pawn trader)
        {
            var node = new DiaNode($"{trader.LabelCap} — {settlement.Label} ({settlement.Faction?.Name})");

            // 交易（可用性口径镜像原版贸易浮窗；Disable 的原因由原版内联显示在按钮文本上）。
            var trade = new DiaOption("RimExodus_SettlementTraderTrade".Translate());
            var tradeReport = TradeAcceptanceReport(negotiator, settlement);
            if (!tradeReport.Accepted)
            {
                trade.Disable(tradeReport.Reason);
            }
            else
            {
                trade.action = delegate
                {
                    Find.WindowStack.Add(new Dialog_Trade(negotiator, settlement));
                };
            }
            trade.resolveTree = true; // Activate 先关面板再执行 action（= 旧实现 Close(false) + Add）。
            node.options.Add(trade);

            // 据点 gizmo → 选项。
            foreach (var gizmo in settlement.GetGizmos())
            {
                if (gizmo is not Command command) continue;
                if (command.icon == Settlement.FormCaravanCommand) continue; // 教学项，语境不适用。
                if (command.defaultLabel == "CommandShowMap".Translate()) continue; // 查看地图（站在图上，无意义）。
                var option = new DiaOption(command.LabelCap);
                if (command.Disabled)
                {
                    option.Disable(command.disabledReason.NullOrEmpty() ? "DisabledCommand".Translate() : command.disabledReason);
                }
                else
                {
                    option.action = delegate { command.ProcessInput(Event.current); };
                }
                option.resolveTree = true;
                node.options.Add(option);
            }

            node.options.Add(new DiaOption("RimExodus_SettlementTraderLeave".Translate())
            {
                resolveTree = true
            });

            Find.WindowStack.Add(new Dialog_NodeTreeWithFactionInfo(
                node, settlement.Faction, delayInteractivity: false, radioMode: false,
                title: "RimExodus_SettlementTraderDialogTitle".Translate()));
        }

        /// <summary>交易可用性（门槛对齐原版 CaravanArrivalAction_Trade.CanTradeWith + 贸易浮窗的谈判者检查）。</summary>
        private static AcceptanceReport TradeAcceptanceReport(Pawn negotiator, Settlement settlement)
        {
            if (settlement.Faction == null || settlement.TraderKind == null
                || settlement.Faction.def.permanentEnemy || settlement.Faction.HostileTo(Faction.OfPlayer))
            {
                return "RimExodus_SettlementTraderNoTrade".Translate();
            }
            if (!settlement.CanTradeNow)
            {
                return "RimExodus_SettlementTraderNoStock".Translate();
            }
            if (negotiator.skills == null || negotiator.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
            {
                return "CannotPrioritizeWorkTypeDisabled".Translate(SkillDefOf.Social.LabelCap);
            }
            return negotiator.CanTradeWith(settlement.Faction, settlement.TraderKind);
        }
    }
}
