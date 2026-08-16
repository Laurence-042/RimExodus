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
        /// 通过 <paramref name="wasSelected"/> 返回转移前 pawn 是否被玩家选中——调用方应在切图后据此 re-Select，
        /// 因为 DeSpawn 会自动 Deselect（<see cref="Verse.Thing.DeSpawn"/> 调 Selector.Deselect），Spawn 不会自动恢复。
        /// </summary>
        public static bool TryTransferPawn(Pawn pawn, Thing departureSpot, Map arrivalMap, IntVec3 arrivalCell, out bool wasSelected)
        {
            wasSelected = false;
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
            // 不校验 pawn 与 spot 的格距（教训勿回退）：Prefix 触发读 pather.nextCell，而原版
            // pather 在路径重建/节点去重（SetupMoveIntoNextCell 的 ConsumeNextNode 双消费）后
            // 可以合法地以"nextCell 距 pawn.Position ≥2 格"调用 TryEnterNextPathCell 并直接多格
            // 跳入——"nextCell 恒相邻"假设不成立（实测被曼哈顿 ≤1 校验误拒）。
            // 真正的不变量由调用方保证：触发器在 nextCell 上找到了该 spot（= 正在进入此格）；
            // 坐标映射只依赖 spot（arrivalCell = spot.Position - offset），与 pawn 当前站哪无关。
            if (pawn.Map != departureMap)
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: pawn (map {pawn.Map?.uniqueID}) "
                    + $"is not on the departure spot's map ({departureMap.uniqueID}).");
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

            var departureCell = pawn.Position;
            var rotation = pawn.Rotation;

            // === 转移前：保存需要跨 DeSpawn/Spawn 保留的状态 ===
            // DeSpawn 会销毁整个 Pawn_DraftController（RemoveComponentsOnDespawned 把 drafter 置 null），
            // Spawn 会新建一个 draftedInt=false 的新实例，必须手动保存恢复。
            var wasDrafted = pawn.drafter?.Drafted ?? false;
            var wasFireAtWill = pawn.drafter?.FireAtWill ?? true;

            // 记录选中状态：Thing.DeSpawn 会无条件 Selector.Deselect（Thing.cs:974-977），Spawn 不会恢复。
            // 调用方在切图后据 wasSelected 重新 Select，避免玩家跨图后丢失选中。
            wasSelected = Find.Selector?.IsSelected(pawn) ?? false;

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

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Seamless transfer: {pawn.LabelShort} "
                    + $"map {departureMap.uniqueID} {departureCell} -> map {arrivalMap.uniqueID} {arrivalCell}");
            return true;
        }
    }
}
