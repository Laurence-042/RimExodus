using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 在无缝接缝处把 Pawn 从本图转移到对端地图的指定坐标。
    /// 目标坐标由传送点的缓存对端坐标（offset 算出）提供，不依赖对端 spot 绑定。
    /// </summary>
    public static class SeamlessMapTransfer
    {
        /// <summary>
        /// 把 pawn 从 departureSpot 所在地图转移到 arrivalMap 的 arrivalCell。
        /// arrivalCell 由调用方（trigger）从传送点的缓存对端坐标读取（容纳投影扭曲的 offset 映射）。
        /// </summary>
        public static bool TryTransferPawn(Pawn pawn, Thing departureSpot, Map arrivalMap, IntVec3 arrivalCell)
        {
            if (pawn == null || departureSpot == null || arrivalMap == null)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn, departure spot, or arrival map is null.");
                return false;
            }
            if (!pawn.Spawned || !departureSpot.Spawned)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn or departure spot is not spawned.");
                return false;
            }

            var departureMap = departureSpot.Map;
            if (departureMap == null || departureMap.Disposed || departureMap == arrivalMap)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: departure map invalid or same as arrival map.");
                return false;
            }
            if (pawn.Map != departureMap || pawn.Position != departureSpot.Position)
            {
                Log.Warning("[RimExodus] Seamless transfer rejected: pawn is not on the departure spot.");
                return false;
            }

            if (!arrivalCell.InBounds(arrivalMap))
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: arrival cell {arrivalCell} out of bounds on map {arrivalMap.uniqueID}.");
                return false;
            }
            // 按"容纳扭曲"设计，重叠带保证 arrivalCell 可通行。若不可通行，说明寻路本就该不可达——
            // 不做兜底，记录警告以暴露几何问题，仍拒绝转移。
            if (!arrivalCell.Walkable(arrivalMap))
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: arrival cell {arrivalCell} on map {arrivalMap.uniqueID} is not walkable (overlap band should guarantee walkability).");
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

            // === 转移前：保存需要跨 DeSpawn/Spawn 保留的状态 ===
            // DeSpawn 会销毁整个 Pawn_DraftController（RemoveComponentsOnDespawned 把 drafter 置 null），
            // Spawn 会新建一个 draftedInt=false 的新实例，必须手动保存恢复。
            var wasDrafted = pawn.drafter?.Drafted ?? false;
            var wasFireAtWill = pawn.drafter?.FireAtWill ?? true;

            // Detach 旧 lord：DeSpawn 不清 pawn.lord 字段，跨图后若仍指向旧地图的 lord，
            // GetGizmos 的 AllowsDrafting 会走旧 lord 判定（可能禁用征召按钮），think tree 也受干扰。
            var prevLord = pawn.GetLord();
            prevLord?.Notify_PawnLost(pawn, PawnLostCondition.Vanished);

            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, arrivalCell, arrivalMap, rotation);

            // === 转移后：恢复状态 ===
            // 确保 lord 字段干净（DeSpawn 不清它，上面已 Notify_PawnLost，这里确保字段为 null）。
            pawn.lord = null;

            // 恢复征召状态。Drafted setter 会 ClearQueuedJobs + EndCurrentJob，所以必须在续程 StartJob 之前恢复。
            if (wasDrafted && pawn.drafter != null)
            {
                pawn.drafter.Drafted = true;
                pawn.drafter.FireAtWill = wasFireAtWill;
            }

            // 必须在目标地图本 tick 扫描入口前写入，避免同 tick 立即弹回。
            // 锁状态是 pawn 级的：pawn 只要还在接缝带上就不会再被任何接缝传送点触发传送。
            targetTrigger.RecordArrival(pawn);

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Seamless transfer: {pawn.LabelShort} "
                    + $"map {departureMap.uniqueID} {departureCell} -> map {arrivalMap.uniqueID} {arrivalCell}");
            return true;
        }
    }
}
