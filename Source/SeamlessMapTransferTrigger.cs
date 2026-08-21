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
            // 历史职责迁移（2026-08 软休眠）：静态登记表的周期清扫原挂"仅锚点图 tick"——
            // 家园无玩家 pawn 时可随软休眠冻结，锚点 tick 不再可靠，已迁至
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
            // 例外（行为表行 15/23/31"AI 战斗移动"落地，2026-08 阶段5）：NPC 战斗体的战斗移动踩
            // 已加载传送点且追击目标在对端 → 即席 Pursue 许可传送（防玩家跨图甩追兵）。
            if (!SeamlessTransferGrants.TryGet(pawn, out var grant))
            {
                if (!TryRegisterCombatStepTransfer(pawn, cell, map)) return;
                if (!SeamlessTransferGrants.TryGet(pawn, out grant)) return;
            }

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;

                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;

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
        }

        /// <summary>
        /// 战斗踩点资格（行为表行 15/23/31"敌方主体战斗移动踩已加载传送点 → 传送（跨图追击）"的
        /// 实现时细化版）：NPC 战斗体 + 战斗 job（AttackMelee / AttackStatic / 战斗 lord duty 下的
        /// Goto）且追击目标（job.targetA 或 mindState.enemyTarget）在对端图，才即席登记 Pursue
        /// 许可——"目标在对端"的意图判据防止沿接缝带走位路径的袭击者被误甩过缝。
        /// 对端未生成不登记（行为表"预加载=否"，不触发生成）。
        /// </summary>
        private static bool TryRegisterCombatStepTransfer(Pawn pawn, IntVec3 cell, Map map)
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

                if (RimExodusMod.Settings?.verboseLogging ?? false)
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
                    || duty.def == DutyDefOf.PrisonerAssaultColony;
            }
            return false;
        }

        /// <summary>Bridge/Pursue/Follow：对端已加载 → 传送 + 传送完成事件；对端卸载 → 许可作废。</summary>
        private static void HandleBoundTransfer(Pawn pawn, Map map, Thing thing, CompSeamlessTileEnterSpot comp, SeamlessTransferGrants.Grant grant)
        {
            if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out var arrivalMap))
            {
                // 对端已卸载（许可登记后邻居被移除）：跟丢，许可作废。
                SeamlessTransferGrants.Remove(pawn);
                return;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Seamless trigger ({grant.Kind}): pawn {pawn.LabelShort} at {thing.Position} "
                    + $"on map {map.uniqueID} targeting {comp.cachedArrivalCell} on map {arrivalMap.uniqueID}");

            if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, arrivalMap, comp.cachedArrivalCell, out _))
            {
                SeamlessTransferGrants.Remove(pawn);
                AfterTransfer(pawn, map, thing, arrivalMap, grant);
            }
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
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Seamless trigger (Evacuation): pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} continues evacuation toward map {arrivalMap.uniqueID}");

                if (SeamlessMapTransfer.TryTransferPawn(pawn, thing, arrivalMap, comp.cachedArrivalCell, out _))
                {
                    SeamlessTransferGrants.Remove(pawn);
                    AfterTransfer(pawn, map, thing, arrivalMap, grant);
                }
                return;
            }

            if (grant.SelfDriven || grant.ForceExit)
            {
                // 续程 Goto 不带 exitMapOnArrival（防落地瞬间原生离场），离场动作由这里补上。
                pawn.ExitMap(allowedToJoinOrCreateCaravan: true, CellRect.WholeMap(map).GetClosestEdge(pawn.Position));
                SeamlessTransferGrants.Remove(pawn);
                return;
            }

            // 原版 flag job 驱动 + 对端未生成：交还原生——JobDriver pre-tick 在此出口格原生撤离
            //（行为表"踩传送点(未生成) = 原生撤离（视为跑出视野）"）。许可保留至 job 结束被 StartJob 清理。
        }

        /// <summary>传送成功后的公共收尾：相机聚焦、选中恢复、传送完成事件（续程 + 追击/跟随扫描）。</summary>
        private static void AfterTransfer(Pawn pawn, Map departureMap, Thing departureSpot, Map arrivalMap, SeamlessTransferGrants.Grant grant)
        {
            SeamlessCameraFocus.TryAutoFocusOnArrival(pawn, arrivalMap);
            // 切图后恢复选中状态：若 pawn 在选中保持集里（玩家下达跨图指令时登记）则 re-Select。
            if (SeamlessSelectionTracker.Consume(pawn) && Find.Selector != null)
            {
                Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
            }
            SeamlessTransferGrants.NotifyPawnTransferred(pawn, departureMap, departureSpot, arrivalMap, grant);
            // 源集合变化（离图端可能失去最后一个玩家 pawn）：请求 governor 尽快重算距离。
            SeamlessDormancyGovernor.RequestSweepSoonStatic();
        }
    }
}
