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
        /// <paramref name="grant"/>：驱动本次传送的许可，剥离 lord 前把 LordJob 引用统一备份进它
        /// （一切 lord 类型、一切 pawn，捕获与剥离同点——任何新传送入口都不可能"剥了没备份"；
        /// 落地续接策略唯一出处 = <see cref="SeamlessTransferGrants"/> 的 lord 续接分支）。
        /// </summary>
        internal static bool TryTransferPawn(Pawn pawn, Thing departureSpot, Map arrivalMap, IntVec3 arrivalCell, SeamlessTransferGrants.Grant grant, out bool wasSelected)
        {
            return TryTransferPawnInternal(pawn, departureSpot, arrivalMap, arrivalCell, grant, out wasSelected,
                captureAssociations: true);
        }

        private static bool TryTransferPawnInternal(Pawn pawn, Thing departureSpot, Map arrivalMap, IntVec3 arrivalCell,
            SeamlessTransferGrants.Grant grant, out bool wasSelected, bool captureAssociations)
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
            if (arrivalMap.Disposed)
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: arrival map {arrivalMap.uniqueID} is disposed.");
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
            // 按"容纳扭曲"设计，重叠带保证 arrivalCell 对 **pawn** 可通行；载具另走 VF 口径的
            // 三步管线（网格同步就绪化 → 整车矩形落点解析），见 SeamlessVehiclesCompat 类注释。
            if (!arrivalCell.Walkable(arrivalMap))
            {
                Log.Warning($"[RimExodus] Seamless transfer rejected: arrival cell {arrivalCell} on map {arrivalMap.uniqueID} is not walkable (overlap band should guarantee walkability).");
                return false;
            }
            var departureCell = pawn.Position;
            var rotation = pawn.Rotation;
            var associationSessions = captureAssociations ? SeamlessTransferAssociations.Capture(pawn) : null;
            if (SeamlessVehiclesCompat.IsVehicle(pawn))
            {
                // ① 对图 VF 网格同步就绪化（Urgent，官方模式）——未就绪时一切 VF 判定都是错的
                //   （Drivable 假阳性 / CanReach 恒 false，v1 五轮补丁的根因）。
                if (!SeamlessVehiclesCompat.EnsureGridsReady(pawn, arrivalMap))
                {
                    return false;
                }
                // ② 落点解析：整车矩形 + 他车占用（VF 官方判据，四向放宽），不行就径向找；找不到 =
                //   对端接缝无车辆可站地块，如实拒绝（真实地形限制）。解析可能改写落地朝向
                //   （长边车换朝向才放得下——与 VF DrivableRectOnCell(AnyRotation) 同口径）。
                if (!SeamlessVehiclesCompat.TryResolveVehicleArrivalCell(pawn, arrivalMap, ref arrivalCell, ref rotation))
                {
                    return false;
                }
            }

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
            // 剥离前把 LordJob 引用备份进 grant（一切 lord 类型、一切 pawn 统一捕获，与剥离同点——
            // 任何新传送入口都不可能"剥了没备份"；pawn 至多一个 lord：pawn.lord 单引用，多叛奴
            // 各自捕获同一引用，落地由续接策略合流）。续接策略唯一出处 = SeamlessTransferGrants
            // 的 TryContinueLordOnArrival。
            // mental state 刻意不清：mental state 是 pawn-local 行为状态（DeSpawn/Spawn 均不触碰它，
            // Pawn.DeSpawn 只清 mindState.droppedWeapon），think tree 在任何图上自动按其重发 job——
            // 发狂行凶/精神崩溃/狂猎因此天然跨图可用，勿在此加清理逻辑。
            var prevLord = pawn.GetLord();
            if (grant != null) grant.PrevLordJob = prevLord?.LordJob;
            prevLord?.Notify_PawnLost(pawn, PawnLostCondition.Vanished);

            // 影子成员的持有善后不在传送流程内做（2026-08-29 收拢架构，用户定夺）：传送只发 pawn 所在地
            // 变动事件（AfterTransfer → SeamlessPawnLocationTracker），影子名单同步/陈旧条目清扫由底座在
            // 下一 GameComponentTick 的位置刷新段完成；同 tick 内的删图决策先经 Forget 的删前清扫，
            // 时序自洽——传送流程对影子系统零感知。
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, arrivalCell, arrivalMap, rotation);

            // VF 载具 ③：清 vehiclePather 旧图 path/nextCell 残留（Notify_Teleported =
            // StopDead + ResetToCurrentPosition，VF 原生语义）。非载具/未装 VF = no-op。
            // 网格已在传送前同步就绪化（车辆分支①）——续程 StartJob 无等待窗口。
            SeamlessVehiclesCompat.ResetVehiclePather(pawn);

            // === 转移后：恢复状态 ===
            // 确保 lord 字段干净（DeSpawn 不清它，上面已 Notify_PawnLost，这里确保字段为 null）。
            pawn.lord = null;

            // 恢复征召状态。Drafted setter 会 ClearQueuedJobs + EndCurrentJob，所以必须在续程 StartJob 之前恢复。
            if (wasDrafted && pawn.drafter != null)
            {
                pawn.drafter.Drafted = true;
                pawn.drafter.FireAtWill = wasFireAtWill;
            }

            // Restore multi-pawn relationships only after the primary pawn is safely spawned. Associated pawns
            // use this same state-preserving transfer path, but do not recursively capture the same relationship.
            SeamlessTransferAssociations.Restore(associationSessions, associatedPawn =>
                TryTransferPawnInternal(associatedPawn, departureSpot, arrivalMap, arrivalCell, null, out _,
                    captureAssociations: false));

            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Seamless transfer: {pawn.LabelShort} "
                    + $"map {departureMap.uniqueID} {departureCell} -> map {arrivalMap.uniqueID} {arrivalCell}");
            return true;
        }
    }
}
