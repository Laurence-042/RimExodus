using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 当前地图上的无缝入口触发器。
    /// 每张地图只扫描自己的入口；入口通过 CompSeamlessTileEnterSpot.cachedArrivalCell + targetWorldTile
    /// 解析对端（阶段4b 传送机制重构：废弃互绑，改用 offset 算对端坐标并缓存到 spot）。
    ///
    /// 阶段5 边界行为（doc/边界行为表.md）：传送资格由 <see cref="SeamlessTransferGrants"/> 在状态变更时
    /// 登记并绑定传送点；本类只做"许可查询 + 匹配分派"——无许可不传（闲逛/工作/无 flag 逃跑一律无事）。
    /// 历史"到达锁"已随登记制删除：无许可不传 + 撤离链 VisitedTiles 防回弹，锁的"站在任一传送点保持"
    /// 语义反而会卡死沿相邻边带内行走的撤离者。
    /// </summary>
    public class SeamlessMapTransferTrigger : MapComponent
    {
        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            // 历史职责迁移（2026-08 软休眠）：静态登记表的周期清扫原挂"仅家园图 tick"——
            // 家园无玩家 pawn 时可随软休眠冻结，图组件 tick 不再可靠，已迁至
            // SeamlessDormancyGovernor（GameComponent 恒 tick）。本组件不再承担全局清扫。
        }

        /// <summary>
        /// 事件驱动的传送检测：由 <see cref="Patch_Pawn_PathFollower_TryEnterNextPathCell"/> 在 pawn
        /// 即将进入新格（Prefix，pather.nextCell）时调用——必须先于原版的 PatherArrived/job 链，见该 patch 注释。
        /// 凭 <see cref="SeamlessTransferGrants"/> 登记的传送许可分派（无许可即返回，热路径一次字典查询）：
        /// - Bridge/Pursue/Follow：对端已加载 → 传送；对端卸载 → 许可作废（跟丢）。
        /// - Evacuation：对端已加载且非 ForceExit → 传送续链；否则原生离场
        ///   （SelfDriven/ForceExit 由本类调 ExitMap；原版 flag job 驱动交还原版 JobDriver 撤离）。
        /// </summary>
        internal static void TryTriggerTransfer(Pawn pawn, IntVec3 cell, Map map)
        {
            if (pawn == null || map == null) return;
            if (pawn.Downed || pawn.Dead) return;

            // 登记制：无传送许可不传（行为表"收紧"的结构性结果）。
            // 例外（行为表行 17/25/33"AI 战斗移动"落地，2026-08 阶段5）：NPC 战斗体的战斗移动踩
            // 已加载传送点且追击目标在对端 → 即席 Pursue 许可传送（防玩家跨图甩追兵）。
            if (!SeamlessTransferGrants.TryGet(pawn, out var grant))
            {
                if (!TryRegisterCombatStepTransfer(pawn, cell, map)) return;
                if (!SeamlessTransferGrants.TryGet(pawn, out grant)) return;
            }

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var things = map.thingGrid.ThingsListAt(cell);
            bool missingSpotComp = false;
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;

                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null)
                {
                    missingSpotComp = true;
                    continue;
                }

                // 许可绑定匹配：Evacuation 首跳（BoundSpot 无效）任意传送点皆可；
                // 其余许可 / 撤离续程恒为登记时选定的具体格。
                if (grant.BoundSpot.IsValid && grant.BoundSpot != thing.Position) continue;

                switch (grant.Kind)
                {
                    case SeamlessTransferGrants.GrantKind.Evacuation:
                        HandleEvacuation(pawn, map, thing, comp, grant);
                        return;
                    default:
                        HandleBoundTransfer(pawn, map, thing, comp, grant);
                        return;
                }
            }
            if (missingSpotComp) WarnTransferRejected(pawn, map, cell);
        }

        /// <summary>
        /// 同格 Goto 不会调用 TryEnterNextPathCell；在 StartJob 原方法开始运行 GotoCell toil 前
        /// 补做一次与踩点相同的许可分派。返回 true 表示 pawn 已跨图或离场，调用方应跳过旧 job。
        /// 未加载对端保持原生到站离场；传送拒绝时刷新出口网格，让原生到站兜底可识别该点。
        /// </summary>
        internal static bool TryTriggerStationaryGoto(Pawn pawn)
        {
            var map = pawn?.Map;
            if (map == null || !pawn.Spawned || pawn.Downed || pawn.Dead) return false;
            var cell = pawn.Position;
            if (!SeamlessTransferGrants.TryGet(pawn, out var grant)
                || (grant.BoundSpot.IsValid && grant.BoundSpot != cell)
                || !HasEnterSpotAt(map, cell)) return false;

            TryTriggerTransfer(pawn, cell, map);
            if (pawn.Map != map) return true;

            if (grant.Kind == SeamlessTransferGrants.GrantKind.Evacuation)
            {
                if (grant.SelfDriven)
                {
                    // 续程 Goto 没有 exitMapOnArrival；传送被拒后不能让它到站再无限重发。
                    // 与 HandleEvacuation 的“对端未加载”分支使用相同的原生离场入口。
                    pawn.ExitMap(allowedToJoinOrCreateCaravan: true,
                        CellRect.WholeMap(map).GetClosestEdge(cell));
                    SeamlessTransferGrants.Remove(pawn);
                    return true;
                }

                // 本次 job 仍会走原版 Goto 到站检查。spot 是 Ethereal，生成时不自动使网格失效；
                // 这里按需标脏，防旧的方形网格让“已经在点上”的 Goto 立即成功却无法离场。
                map.exitMapGrid.Notify_LOSBlockerSpawned();
            }
            return false;
        }

        private static readonly Dictionary<int, int> LastTransferWarningTickByMap = new();

        private static void WarnTransferRejected(Pawn pawn, Map map, IntVec3 cell)
        {
            int tick = GenTicks.TicksGame;
            if (LastTransferWarningTickByMap.TryGetValue(map.uniqueID, out var last)
                && tick >= last && tick - last < 600) return;
            if (LastTransferWarningTickByMap.Count >= 64) LastTransferWarningTickByMap.Clear();
            LastTransferWarningTickByMap[map.uniqueID] = tick;
            Messages.Message("RimExodus_SeamTransferRejected".Translate(pawn.LabelShort, cell.ToString()),
                MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>
        /// 战斗踩点资格（行为表行 17/25/33"敌方主体战斗移动踩已加载传送点 → 传送（跨图追击）"的
        /// 实现时细化版）：NPC 战斗体 + 战斗 job（AttackMelee / AttackStatic / 战斗 lord duty 下的
        /// Goto）且追击目标（job.targetA 或 mindState.enemyTarget）在对端图，才即席登记 Pursue
        /// 许可——"目标在对端"的意图判据防止沿接缝带走位路径的袭击者被误甩过缝。
        /// 对端未生成不登记（行为表"预加载=否"，不触发生成）。
        /// </summary>
        private static bool TryRegisterCombatStepTransfer(Pawn pawn, IntVec3 cell, Map map)
        {
            // 诊断：无许可战斗 pawn 的每跨格探测（含每跨格字符串 def 查询——评估其真实占比）。
            var probeT0 = RimExodusPerfProbe.Now();
            try
            {
                return TryRegisterCombatStepTransferInner(pawn, cell, map);
            }
            finally
            {
                RimExodusPerfProbe.Record(RimExodusPerfProbe.CombatStepProbe, probeT0);
            }
        }

        private static bool TryRegisterCombatStepTransferInner(Pawn pawn, IntVec3 cell, Map map)
        {
            if (!SeamlessCombatCoords.Enabled || pawn.Downed || pawn.Dead) return false;
            if (!SeamlessBoundaryRules.IsNpcCombatant(pawn)) return false;

            var job = pawn.CurJob;
            if (job == null || !IsCombatJob(pawn, job)) return false;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return false;

            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;

                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;

                // 对端未加载：无事（不触发生成）。
                if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out var arrivalMap))
                {
                    return false;
                }

                // 追击意图：战斗目标确在对端图。
                var jobTargetMap = job.targetA.Thing?.Map;
                var enemyMap = pawn.mindState?.enemyTarget?.Map;
                if (jobTargetMap != arrivalMap && enemyMap != arrivalMap) return false;

                var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Pursue);
                grant.BoundSpot = thing.Position;

                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Combat step transfer: {pawn.LabelShort} (job {job.def.defName}) "
                        + $"pursues across map {map.uniqueID} -> {arrivalMap.uniqueID}.");
                return true;
            }
            return false;
        }

        private static bool IsCombatJob(Pawn pawn, Job job)
        {
            if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic) return true;
            if (job.def == JobDefOf.Goto)
            {
                var duty = pawn.mindState?.duty;
                if (duty == null) return false;
                return duty.def == DutyDefOf.AssaultColony || duty.def == DutyDefOf.AssaultThing
                    || duty.def == DutyDefOf.Defend || duty.def == DutyDefOf.DefendBase
                    || duty.def == DutyDefOf.PrisonerAssaultColony
                    // ChimeraAttack（2026-09）：奇美拉攻击模式的推进/追击 Goto 同为战斗意图——
                    // 不登记 Pursue 则落地无 lord 保护（见 NotifyPawnTransferred Bridge 分支注）。
                    || duty.def == DutyDefOf.ChimeraAttack;
            }
            return false;
        }

        /// <summary>Bridge/Pursue/Follow：对端已加载 → 传送 + 传送完成事件；对端卸载 → 许可作废。</summary>
        private static void HandleBoundTransfer(Pawn pawn, Map map, Thing thing, CompSeamlessTileEnterSpot comp, SeamlessTransferGrants.Grant grant)
        {
            if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out var arrivalMap))
            {
                if (!comp.hasArrival && SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out _))
                    WarnTransferRejected(pawn, map, thing.Position);
                // 对端已卸载/休眠，或对端仍在但 arrival 缓存尚未就绪：许可作废。
                // 2026-09 插桩：作废后 TransitGoto 走到终点即站住，靠 think tree 自谋出路——
                // NPC 战斗体外的 pawn（商队驮兽/盟友随从）不进 StrayNpcs 宽限，是"卡在边界"候选之一。
                // 日志统一走 [caravan-exit] 标签（2026-09 用户报告 "caravans stuck on the border"
                // 排查：玩家日志里一眼可辨是商队/访客离场链行为；成因证据不足，仅插桩不改行为）。
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] {grant.Kind} grant dropped: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} — opposite unavailable (hasArrival={comp.hasArrival}); pawn will idle at seam until think tree re-decides.");
                SeamlessTransferGrants.Remove(pawn);
                return;
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Seamless trigger ({grant.Kind}): pawn {pawn.LabelShort} at {thing.Position} "
                    + $"on map {map.uniqueID} targeting {comp.cachedArrivalCell} on map {arrivalMap.uniqueID}");

            if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, arrivalMap, comp.cachedArrivalCell, grant, out _))
            {
                SeamlessTransferGrants.Remove(pawn);
                AfterTransfer(pawn, map, thing, arrivalMap, grant);
                return;
            }

            // 传送被拒（TryTransferPawn 内已 Warning：对端接缝无整车可站地块 / 网格异常态等）：
            // 撤销许可止住"站在 spot 上每步重试"的循环（2026-08 实测：许可残留 → Bridge
            // immediate + 拒绝日志往复刷屏）。拒绝是确定性判据，玩家换边/换点重下令即可。
            SeamlessTransferGrants.Remove(pawn);
            WarnTransferRejected(pawn, map, thing.Position);
            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Bridge grant revoked for {pawn.LabelShort}: transfer rejected at seam (see warning above), re-order to retry.");
        }

        /// <summary>
        /// Evacuation 分流（行为表待办①）：对端已加载 → 传送 + 撤离链续程（方向性/防回弹见
        /// <see cref="SeamlessTransferGrants"/> 的 TryFindEvacuationExit）；
        /// 对端未生成 → 放行原生撤离；只剩已访问出口（ForceExit）或续程驱动（SelfDriven）→ 本类直接原生离场。
        /// </summary>
        private static void HandleEvacuation(Pawn pawn, Map map, Thing thing, CompSeamlessTileEnterSpot comp, SeamlessTransferGrants.Grant grant)
        {
            if (comp.hasArrival && !grant.ForceExit && SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out var arrivalMap))
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] Evacuation transfer: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} continues evacuation toward map {arrivalMap.uniqueID}");

                if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, arrivalMap, comp.cachedArrivalCell, grant, out _))
                {
                    SeamlessTransferGrants.Remove(pawn);
                    AfterTransfer(pawn, map, thing, arrivalMap, grant);
                }
                else
                {
                    WarnTransferRejected(pawn, map, thing.Position);
                    // 2026-09 插桩（"caravans stuck on the border"排查）：传送被拒（拒绝原因见上方 Warning），
                    // grant 保留。后续行为按驱动方式分叉：原版 flag job 驱动（SelfDriven=false）有
                    // JobDriver pre-tick IsExitCell 兜底（站上格即原生离场）；SelfDriven 续程 job
                    // 不带 flag；同格 Goto 现由 TryTriggerStationaryGoto 兜底离场。
                    if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                        Log.Message($"[RimExodus] [caravan-exit] Evacuation transfer rejected: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID}; selfDriven={grant.SelfDriven} "
                        + (grant.SelfDriven ? "(stationary Goto will use direct exit fallback)" : "(flag job will exit vanilla)"));
                }
                return;
            }

            if (!grant.ForceExit && !comp.hasArrival
                && SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out _))
                WarnTransferRejected(pawn, map, thing.Position);

            if (grant.SelfDriven || grant.ForceExit)
            {
                // 续程 Goto 不带 exitMapOnArrival（防落地瞬间原生离场），离场动作由这里补上。
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] Evacuation force-exit: pawn {pawn.LabelShort} at {thing.Position} on map {map.uniqueID} "
                        + $"exits map now (selfDriven={grant.SelfDriven}, forceExit={grant.ForceExit}).");
                pawn.ExitMap(allowedToJoinOrCreateCaravan: true, CellRect.WholeMap(map).GetClosestEdge(pawn.Position));
                SeamlessTransferGrants.Remove(pawn);
                return;
            }

            // 原版 flag job 驱动 + 对端未生成：交还原生——JobDriver pre-tick 在此出口格原生撤离
            //（行为表"踩传送点(未生成) = 原生撤离（视为跑出视野）"）。许可保留至 job 结束被 StartJob 清理。
            if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                Log.Message($"[RimExodus] [caravan-exit] Evacuation vanilla handoff: pawn {pawn.LabelShort} at {thing.Position} on map {map.uniqueID} "
                    + $"— opposite unavailable (hasArrival={comp.hasArrival}), JobDriver will handle native exit.");
        }

        /// <summary>
        /// cell 上是否有 RimExodus 传送点 thing。撤离推迟 patch（Patches_EvacuationExitDefer）的判据，
        /// 与 <see cref="TryTriggerTransfer"/> 的 spot 识别同口径（thingGrid 逐 thing 比 def）。
        /// </summary>
        internal static bool HasEnterSpotAt(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map)) return false;
            var spotDef = SeamlessTransferGrants.EnterSpotDef;
            if (spotDef == null) return false;
            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                if (things[i].def == spotDef) return true;
            }
            return false;
        }

        /// <summary>传送成功后的公共收尾：相机聚焦、选中恢复、传送完成事件（续程 + 追击/跟随扫描）。</summary>
        private static void AfterTransfer(Pawn pawn, Map departureMap, Thing departureSpot, Map arrivalMap, SeamlessTransferGrants.Grant grant)
        {
            SeamlessCameraFocus.TryAutoFocusOnArrival(pawn, arrivalMap);
            // PS 兼容聚焦（2026-08，PS 未装 = no-op）：被传走的是 PS avatar 本人或 avatar 所乘单位
            // （VF 载具舱内/被扛抬）→ 切 CurrentMap 到落地图 + 缩放保留。PS×VF 驾驶场景的命脉：
            // 不切图则 Avatar.UpdatePhysics 首行 "avatar.Map != CurrentMap" 冻结驾驶输入且永不自愈。
            SeamlessPerspectiveShiftCompat.FocusIfAvatarAboard(pawn, departureMap, arrivalMap);
            // 切图后恢复选中状态：若 pawn 在选中保持集里（玩家下达跨图指令时登记）则 re-Select。
            if (SeamlessSelectionTracker.Consume(pawn) && Find.Selector != null)
            {
                Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
            }
            SeamlessTransferGrants.NotifyPawnTransferred(pawn, departureMap, departureSpot, arrivalMap, grant);
            // 玩家 pawn 落图 = 即时恢复全速（2026-08 分级休眠）：不等 governor 下轮 Sweep——
            // 降频相位内玩家 pawn 会走走停停，落图当 tick 就该全速。
            SeamlessTickThrottle.Unthrottle(arrivalMap, "player pawn transferred in");
            // pawn 所在地变动 → 统一追踪底座（2026-08-29 收拢架构）：下一 GameComponentTick 位置刷新段
            // 即时同步影子名单 + 重算休眠距离（离图端可能失去最后一个玩家 pawn）。
            SeamlessPawnLocationTracker.NotifyChanged();
        }
    }
}
