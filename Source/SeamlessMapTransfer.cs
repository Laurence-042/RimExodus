using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨地图 Pawn 转移工具。
    /// 处理 Pawn 从宿主地图走到接缝传送点后，无缝转移到相邻地块地图。
    ///
    /// 参考 VMF 的 ToilsAcrossMaps.GotoTargetMap（计划书 1.10 结论 8）：
    /// 走到出口 → 视觉过渡 → DeSpawnWithoutJobClear() + GenSpawn.Spawn()。
    ///
    /// 关键设计（计划书 1.10 结论 4）：
    /// 本端传送点与对端传送点在宿主坐标（drawPos）上重合于同一 tile，
    /// 因此 Pawn 转移后视觉位置不跳变。
    /// </summary>
    public static class SeamlessMapTransfer
    {
        /// <summary>
        /// 把 Pawn 从宿主地图转移到相邻地块地图。
        /// </summary>
        /// <param name="pawn">要转移的 Pawn（当前在宿主地图上）。</param>
        /// <param name="exitSpot">宿主地图上的出口传送点 Thing。</param>
        /// <param name="enterParent">目标地块的 MapParent。</param>
        public static bool TransferPawnToTile(Pawn pawn, Thing exitSpot, MapParent_SeamlessTile enterParent)
        {
            if (pawn == null || exitSpot == null || enterParent == null)
            {
                Log.Warning("[RimExodus] TransferPawnToTile: null arg.");
                return false;
            }

            var hostMap = exitSpot.Map;
            var targetMap = enterParent.Map;
            if (hostMap == null || targetMap == null || targetMap.Disposed)
            {
                Log.Warning($"[RimExodus] TransferPawnToTile: bad maps hostMap={hostMap != null} targetMap={targetMap != null} disposed={targetMap?.Disposed}");
                return false;
            }

            // 计算目标地图上的进入位置。
            // 本端传送点与对端传送点在宿主坐标上重合，因此：
            // 目标局部坐标 = 宿主坐标 - hostOffset
            var hostCell = exitSpot.Position;
            var targetLocalCell = SeamlessMapUtility.ToLocalCoord(hostCell, enterParent);

            // 确保目标位置可通行
            if (!targetLocalCell.InBounds(targetMap) || !targetLocalCell.Walkable(targetMap))
            {
                Log.Warning($"[RimExodus] Target cell {targetLocalCell} not walkable on tile map.");
                return false;
            }

            // 视觉过渡：Pawn 在宿主地图上走到出口后，直接转移到目标地图
            var rot = pawn.Rotation;

            // 转移
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, targetLocalCell, targetMap, rot);

            // 让 Pawn 在目标地图上继续执行移动/待命
            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            pawn.mindState?.Reset(clearInspiration: false, clearMentalState: true);

            Log.Message($"[RimExodus] TransferPawnToTile: {pawn.LabelShort} host {hostCell} -> tile {targetLocalCell} on {targetMap.uniqueID}");
            return true;
        }

        /// <summary>
        /// 把 Pawn 从地块地图转移回宿主地图。
        /// </summary>
        public static bool TransferPawnToHost(Pawn pawn, MapParent_SeamlessTile exitParent)
        {
            if (pawn == null || exitParent == null)
            {
                Log.Warning("[RimExodus] TransferPawnToHost: null arg.");
                return false;
            }

            var hostMap = exitParent.sourceMap;
            var tileMap = exitParent.Map;
            if (hostMap == null || tileMap == null || tileMap.Disposed)
            {
                Log.Warning($"[RimExodus] TransferPawnToHost: bad maps hostMap={hostMap != null} tileMap={tileMap != null} disposed={tileMap?.Disposed}");
                return false;
            }

            // 目标宿主坐标 = 当前局部坐标 + hostOffset
            var localCell = pawn.Position;
            var hostCell = SeamlessMapUtility.ToHostCoord(localCell, exitParent);

            if (!hostCell.InBounds(hostMap) || !hostCell.Walkable(hostMap))
            {
                Log.Warning($"[RimExodus] Host cell {hostCell} not walkable.");
                return false;
            }

            var rot = pawn.Rotation;
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, hostCell, hostMap, rot);

            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            pawn.mindState?.Reset(clearInspiration: false, clearMentalState: true);

            Log.Message($"[RimExodus] TransferPawnToHost: {pawn.LabelShort} tile {localCell} -> host {hostCell} on {hostMap.uniqueID}");
            return true;
        }
    }
}
