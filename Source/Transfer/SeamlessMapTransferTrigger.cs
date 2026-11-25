using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>Executes a single seam crossing for a live AI job or a bound movement order.</summary>
    public class SeamlessMapTransferTrigger : MapComponent
    {
        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        internal static void TryTriggerTransfer(Pawn pawn, IntVec3 cell, Map map)
        {
            if (pawn == null || map == null) return;
            if (pawn.Downed || pawn.Dead) return;

            var job = pawn.CurJob;
            if (SeamlessEvacuationRouting.IsOrdinaryExit(job) && job.targetA.Cell == cell)
            {
                AllowNativeWithdrawal(pawn, map, cell);
                return;
            }

            // 登记制：无传送许可不传（行为表"收紧"的结构性结果）。
            // 例外（行为表行 17/25/33"AI 战斗移动"落地，2026-08 阶段5）：有具体追击目标的任务踩
            // 已加载传送点且追击目标在对端 → 即席 Pursue 许可传送（防玩家跨图甩追兵）。
            if (!SeamlessTransferGrants.TryGet(pawn, out var grant))
            {
                if (!TryRegisterCombatStepTransfer(pawn, cell, map)) return;
                if (!SeamlessTransferGrants.TryGet(pawn, out grant)) return;
            }
            if (grant.Driver != null && grant.Driver != job)
            {
                SeamlessTransferGrants.Remove(pawn);
                return;
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

                if (grant.BoundSpot != thing.Position) continue;
                HandleBoundTransfer(pawn, map, thing, comp, grant);
                return;
            }
            if (missingSpotComp) WarnTransferRejected(pawn, map, cell);
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

        private static bool TryRegisterCombatStepTransfer(Pawn pawn, IntVec3 cell, Map map)
        {
            if (!SeamlessCombatCoords.Enabled) return false;
            var probe = RimExodusPerfProbe.Now();
            try
            {
                var job = pawn.CurJob;
                var target = SeamlessPursuit.Target(pawn, job);
                var targetMap = target?.Map;
                if (targetMap == null || targetMap == map || !SeamlessPursuit.CanCross(pawn, target, targetMap))
                    return false;
                var enterSpotDef = SeamlessTransferGrants.EnterSpotDef;
                if (enterSpotDef == null) return false;

                var things = map.thingGrid.ThingsListAt(cell);
                for (int i = 0; i < things.Count; i++)
                {
                    var thing = things[i];
                    if (thing.def != enterSpotDef) continue;
                    var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                    if (comp?.hasArrival != true
                        || !SeamlessTileGraph.TryGetMapByWorldTile(comp.TargetTile, out var arrivalMap)
                        || arrivalMap != targetMap) continue;

                    var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Pursue);
                    grant.BoundSpot = thing.Position;
                    grant.Driver = job;
                    SeamlessTransferGrants.BindPursuit(grant, pawn, target, job);
                    return true;
                }
                return false;
            }
            finally
            {
                RimExodusPerfProbe.Record(RimExodusPerfProbe.CombatStepProbe, probe);
            }
        }

        /// <summary>Bridge/Pursue/Follow：对端已加载 → 传送 + 传送完成事件；对端卸载 → 许可作废。</summary>
        private static void HandleBoundTransfer(Pawn pawn, Map map, Thing thing, CompSeamlessTileEnterSpot comp, SeamlessTransferGrants.Grant grant)
        {
            if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.TargetTile, out var arrivalMap))
            {
                if (!comp.hasArrival && SeamlessTileGraph.TryGetMapByWorldTile(comp.TargetTile, out _))
                    WarnTransferRejected(pawn, map, thing.Position);
                // Unavailable destination. The AI will re-evaluate when its local movement ends.
                if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                    Log.Message($"[RimExodus] [caravan-exit] {grant.Kind} grant dropped: pawn {pawn.LabelShort} at {thing.Position} "
                        + $"on map {map.uniqueID} — opposite unavailable (hasArrival={comp.hasArrival}); pawn will idle at seam until think tree re-decides.");
                SeamlessTransferGrants.Remove(pawn);
                return;
            }

            if (grant.PursuitTarget != null && !StillPursuing(pawn, grant, arrivalMap))
            {
                SeamlessTransferGrants.Remove(pawn);
                return;
            }
            if (grant.Kind == SeamlessTransferGrants.GrantKind.Follow
                && (grant.FollowTarget?.Spawned != true || grant.FollowTarget.Map != arrivalMap
                    || !SeamlessTransferGrants.MatchesAiPhase(pawn, grant)
                    || !SeamlessRaidContinuity.CanFollowAcross(pawn.GetLord())))
            {
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

        private static bool StillPursuing(Pawn pawn, SeamlessTransferGrants.Grant grant, Map arrivalMap)
        {
            return SeamlessPursuit.CanCross(pawn, grant.PursuitTarget, arrivalMap)
                && SeamlessTransferGrants.MatchesAiPhase(pawn, grant);
        }

        // True lets the native driver perform a real exit. A successful transfer
        // stops the source job during DeSpawn and lets the destination think tree run.
        internal static bool AllowNativeWithdrawal(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn.Dead || pawn.Downed) return true;
            var spot = FindEnterSpot(map, cell);
            var comp = spot?.TryGetComp<CompSeamlessTileEnterSpot>();
            if (comp == null || !SeamlessRaidContinuity.CanWithdrawAcross(pawn.GetLord())
                || SeamlessEvacuationRouting.WasVisited(pawn, comp.TargetTile)
                || !SeamlessTileGraph.TryGetMapByWorldTile(comp.TargetTile, out var arrival))
                return true;

            if (!comp.hasArrival) comp.ComputeAndCacheArrival(map);
            if (!comp.hasArrival
                || !SeamlessMapTransfer.TryTransferPawn(pawn, spot, arrival, comp.cachedArrivalCell,
                    null, out _, withdrawing: true))
            {
                SeamlessEvacuationRouting.Reject(pawn, cell);
                WarnTransferRejected(pawn, map, cell);
                return false;
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.CaravanExit))
                Log.Message($"[RimExodus] [caravan-exit] Evacuation transfer: {pawn.LabelShort} from map "
                    + $"{map.uniqueID} to {arrival.uniqueID}; destination AI will choose its next job.");
            SeamlessTransferGrants.Remove(pawn);
            SeamlessEvacuationRouting.Arrive(pawn, map, cell, arrival);
            AfterTransfer(pawn, map, spot, arrival, null);
            return false;
        }

        internal static bool HasEnterSpotAt(Map map, IntVec3 cell) => FindEnterSpot(map, cell) != null;

        private static Thing FindEnterSpot(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map)) return null;
            var spotDef = SeamlessTransferGrants.EnterSpotDef;
            if (spotDef == null) return null;
            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
                if (things[i].def == spotDef) return things[i];
            return null;
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
