using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>在两个地位对等的无缝入口端点之间转移 Pawn。</summary>
    public static class SeamlessMapTransfer
    {
        public static bool TryTransferPawn(Pawn pawn, Thing departureSpot, Thing arrivalSpot)
        {
            if (pawn == null || departureSpot == null || arrivalSpot == null)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn or endpoint is null.");
                return false;
            }
            if (!pawn.Spawned || !departureSpot.Spawned || !arrivalSpot.Spawned)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn or endpoint is not spawned.");
                return false;
            }
            if (departureSpot == arrivalSpot)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: both endpoints are the same Thing.");
                return false;
            }

            var departureMap = departureSpot.Map;
            var arrivalMap = arrivalSpot.Map;
            if (departureMap == null || arrivalMap == null || departureMap.Disposed || arrivalMap.Disposed
                || departureMap == arrivalMap)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: endpoint maps are invalid or identical.");
                return false;
            }
            if (pawn.Map != departureMap || pawn.Position != departureSpot.Position)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn is not on the departure endpoint.");
                return false;
            }

            var departureComp = departureSpot.TryGetComp<CompSeamlessTileEnterSpot>();
            var arrivalComp = arrivalSpot.TryGetComp<CompSeamlessTileEnterSpot>();
            if (departureComp?.CounterpartSpot != arrivalSpot || arrivalComp?.CounterpartSpot != departureSpot)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: endpoint references are not reciprocal.");
                return false;
            }

            var arrivalCell = arrivalSpot.Position;
            if (!arrivalCell.InBounds(arrivalMap) || !arrivalCell.Walkable(arrivalMap))
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: target cell {arrivalCell} is not walkable.");
                return false;
            }

            var targetTrigger = arrivalMap.GetComponent<SeamlessMapTransferTrigger>();
            if (targetTrigger == null)
            {
                Log.Error("[RimExodus] Seamless transfer rejected: target map has no transfer trigger.");
                return false;
            }

            var departureCell = pawn.Position;
            var rotation = pawn.Rotation;

            // 保存征召状态：DeSpawn 会销毁整个 Pawn_DraftController（RemoveComponentsOnDespawned 把
            // drafter 置 null），Spawn 会新建一个 draftedInt=false 的新实例。必须跨 DeSpawn/Spawn 保存。
            var wasDrafted = pawn.drafter?.Drafted ?? false;
            var wasFireAtWill = pawn.drafter?.FireAtWill ?? true;

            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, arrivalCell, arrivalMap, rotation);

            // 恢复征召状态。Drafted setter 会 EndCurrentJob（清队列），所以必须在续程 StartJob 之前恢复。
            if (wasDrafted && pawn.drafter != null)
            {
                pawn.drafter.Drafted = true;
                pawn.drafter.FireAtWill = wasFireAtWill;
            }

            // 必须在目标地图本 tick 扫描入口前写入，避免同 tick 立即弹回。
            targetTrigger.RecordArrival(pawn, arrivalSpot);

            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            pawn.mindState?.Reset(clearInspiration: false, clearMentalState: true);

            Log.Message($"[RimExodus] Seamless transfer: {pawn.LabelShort} "
                + $"map {departureMap.uniqueID} {departureCell} -> map {arrivalMap.uniqueID} {arrivalCell}");
            return true;
        }
    }
}
