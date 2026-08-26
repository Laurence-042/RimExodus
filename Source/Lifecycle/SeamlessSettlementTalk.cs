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
    /// 选项 = 影子远行队的 <see cref="Settlement.GetCaravanGizmos"/> 枚举项（原版远行队站在据点上的
    /// 全部交互：交易/送礼/满足贸易请求/mod comp 交互，2026-08 用户定夺"图上玩家 pawn 就是远行队"，
    /// 机制与硬约束见 <see cref="SeamlessShadowCaravan"/>）+ <see cref="Settlement.GetGizmos"/> 枚举项
    /// （mod 经 WorldObjectComp 加的据点交互天然出现）+ 离开。影子失败时回退自建交易项（零回归）。
    /// 排除项：原版"查看地图"（我们就站在图上）与"组建远行队"教学项（语境不适用）。
    /// </summary>
    public static class SettlementTraderDialog
    {
        public static void Open(Pawn negotiator, Settlement settlement, Pawn trader)
        {
            var node = new DiaNode($"{trader.LabelCap} — {settlement.Label} ({settlement.Faction?.Name})");

            // 影子远行队（2026-08 用户定夺"图上玩家 pawn 就是远行队"；v2 常驻模型）：取本图常驻影子采集
            // Settlement.GetCaravanGizmos——原版远行队站在据点上的全部交互（交易 = CaravanVisitUtility.
            // TradeCommand[谈判者 = 图上最优谈判者，交易会话由 Patches_SettlementTrade 的图上库存 patch
            // 接住]、送礼、满足贸易请求、mod 经 WorldObjectComp 挂载的交互如金鸢尾兰）。攻击 gizmo 的
            // Attackable 门控在可贸易据点上恒 false，天然不出现。
            // 影子常驻（TickInterval 被抑制、GetGizmos 被清空），mod 的即时/延迟回调都能查到成员。
            // 无影子（图上无玩家 pawn）→ 回退自建交易项，零回归。
            var caravanCommands = new List<Command>();
            var caravanFloatOptions = new List<FloatMenuOption>();
            SeamlessShadowCaravan.TryGetForMap(negotiator.Map, out var shadow);
            if (shadow != null)
            {
                foreach (var gizmo in settlement.GetCaravanGizmos(shadow))
                {
                    if (gizmo is Command command)
                    {
                        caravanCommands.Add(command);
                        Log.Message($"[RimExodus] TraderDialog: caravan gizmo '{command.LabelCap}' ({gizmo.GetType().Name})" +
                                    (command.Disabled ? $" DISABLED: {command.disabledReason}" : " enabled") +
                                    $" | SettlementVisitedNow={CaravanVisitUtility.SettlementVisitedNow(shadow) == settlement}.");
                    }
                    else
                    {
                        Log.Message($"[RimExodus] TraderDialog: caravan gizmo skipped (not a Command): {gizmo?.GetType().Name ?? "null"}.");
                    }
                }
                // 金鸢尾兰等 mod 的挂载点在 GetFloatMenuOptions（Harmony Postfix 追加 CaravanArrivalAction
                // 选项），不在 GetCaravanGizmos——两条管线都要采。
                foreach (var fmo in settlement.GetFloatMenuOptions(shadow))
                {
                    if (fmo == null) continue;
                    caravanFloatOptions.Add(fmo);
                }
            }
            Log.Message($"[RimExodus] TraderDialog: harvested {caravanCommands.Count} caravan commands, {caravanFloatOptions.Count} caravan float menu options" +
                        (shadow == null ? " (shadow unavailable — see ShadowCaravan log above)" : "") +
                        $"; settlement def={settlement.def.defName}, Attackable={settlement.Attackable}.");

            if (caravanCommands.Count > 0)
            {
                foreach (var command in caravanCommands)
                {
                    node.options.Add(CommandToOption(command, delegate
                    {
                        command.ProcessInput(Event.current);
                        // 常驻影子下 mod 的延迟回调（如金鸢尾兰浮菜单确认）也能查到成员，无需窗口包装。
                        if (SeamlessShadowCaravan.TryGetForMap(negotiator.Map, out var s))
                        {
                            LogDiplomatDiagnostics(s, $"after gizmo '{command.LabelCap}'");
                        }
                    }));
                }
            }
            else
            {
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
            }

            // 远行队右键菜单选项（GetFloatMenuOptions 管线，含 mod Postfix 追加项）。
            // 过滤：攻击（用户定夺排除另生成地图的）与进入地图（pawn 已在图上，对影子执行 Enter 会
            // 把图上 pawn 二次入场）；与 gizmo 管线标签重复的（交易双管线各出一条）。
            var existingLabels = new HashSet<string>();
            foreach (var command in caravanCommands) existingLabels.Add(command.LabelCap);
            foreach (var fmo in caravanFloatOptions)
            {
                var attackLabel = "AttackSettlement".Translate(settlement.Label);
                var enterLabel = "EnterMap".Translate(settlement.Label);
                if (fmo.Label == attackLabel || fmo.Label == enterLabel)
                {
                    Log.Message($"[RimExodus] TraderDialog: caravan float option filtered (attack/enter): '{fmo.Label}'.");
                    continue;
                }
                if (!existingLabels.Add(fmo.Label))
                {
                    Log.Message($"[RimExodus] TraderDialog: caravan float option filtered (duplicate label): '{fmo.Label}'.");
                    continue;
                }
                var option = new DiaOption(fmo.Label);
                if (fmo.Disabled) // FloatMenuOption 的禁用原因内联在 Label 里（"标签（原因）"），无独立字段。
                {
                    option.Disable("DisabledCommand".Translate());
                    Log.Message($"[RimExodus] TraderDialog: caravan float option '{fmo.Label}' DISABLED.");
                }
                else
                {
                    var captured = fmo; // 迭代变量闭包
                    option.action = delegate
                    {
                        captured.action();
                        if (SeamlessShadowCaravan.TryGetForMap(negotiator.Map, out var s))
                        {
                            LogDiplomatDiagnostics(s, $"after float option '{captured.Label}'");
                        }
                    };
                    Log.Message($"[RimExodus] TraderDialog: caravan float option '{fmo.Label}' enabled.");
                }
                option.resolveTree = true;
                node.options.Add(option);
            }

            // 据点 gizmo → 选项。
            foreach (var gizmo in settlement.GetGizmos())
            {
                if (gizmo is not Command command)
                {
                    Log.Message($"[RimExodus] TraderDialog: settlement gizmo skipped (not a Command): {gizmo?.GetType().Name ?? "null"}.");
                    continue;
                }
                if (command.icon == Settlement.FormCaravanCommand) // 教学项，语境不适用。
                {
                    Log.Message("[RimExodus] TraderDialog: settlement gizmo filtered (form caravan tutorial).");
                    continue;
                }
                if (command.defaultLabel == "CommandShowMap".Translate()) // 查看地图（站在图上，无意义）。
                {
                    Log.Message("[RimExodus] TraderDialog: settlement gizmo filtered (show map).");
                    continue;
                }
                Log.Message($"[RimExodus] TraderDialog: settlement gizmo '{command.LabelCap}' ({command.GetType().Name})" +
                            (command.Disabled ? $" DISABLED: {command.disabledReason}" : " enabled") + ".");
                node.options.Add(CommandToOption(command, delegate { command.ProcessInput(Event.current); }));
            }

            node.options.Add(new DiaOption("RimExodus_SettlementTraderLeave".Translate())
            {
                resolveTree = true
            });

            Find.WindowStack.Add(new Dialog_NodeTreeWithFactionInfo(
                node, settlement.Faction, delayInteractivity: false, radioMode: false,
                title: "RimExodus_SettlementTraderDialogTitle".Translate()));
        }

        /// <summary>Command → DiaOption（含 Disabled 灰显）；action 由调用方提供（影子命令需借出窗口包装）。</summary>
        private static DiaOption CommandToOption(Command command, Action action)
        {
            var option = new DiaOption(command.LabelCap);
            if (command.Disabled)
            {
                option.Disable(command.disabledReason.NullOrEmpty() ? "DisabledCommand".Translate() : command.disabledReason);
            }
            else
            {
                option.action = action;
            }
            option.resolveTree = true;
            return option;
        }

        /// <summary>
        /// 影子诊断（2026-08 金鸢尾兰"没有合适的外交官"排查）：逐 pawn 复现
        /// BestCaravanPawnUtility.FindPawnWithBestStat 的资格链（IsConsciousOwner → IsOwner → 谈判能力
        /// 未禁用），任一失败即打印原因——定位哪个条件毙掉了影子成员。
        /// </summary>
        private static void LogDiplomatDiagnostics(Caravan caravan, string context)
        {
            var best = BestCaravanPawnUtility.FindBestDiplomat(caravan);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[RimExodus] TraderDialog: diplomat check {context}: FindBestDiplomat={(best?.LabelShort ?? "NULL")}, " +
                      $"shadow pawns={caravan.PawnsListForReading.Count}, Spawned={caravan.Spawned}.");
            foreach (var p in caravan.PawnsListForReading)
            {
                string why = null;
                if (p.Dead) why = "dead";
                else if (p.Downed) why = "downed";
                else if (p.InMentalState) why = "mental state";
                else if (!caravan.IsOwner(p)) why = "not owner";
                else if (StatDefOf.NegotiationAbility.Worker.IsDisabledFor(p)) why = "negotiation disabled";
                if (why != null) sb.Append($" [{p.LabelShort}: {why}]");
            }
            Log.Message(sb.ToString());
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
