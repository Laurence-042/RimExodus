using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// Vehicle Framework（SmashPhil.VehicleFramework，载具框架）兼容层（2026-08 v2，机制重写）。
    ///
    /// 机制背景（references/Vehicle-Framework 调研结论，全部经源码核实）：
    /// VF 的 VehiclePawn : Pawn——jobs/inventory/drafter/选中/注册进 mapPawns 全原版语义 → 休眠
    /// governor 的玩家图源收集天然覆盖载具，"载具视为 pawn"零成本；下令链走原版 JobDefOf.Goto +
    /// TryTakeOrderedJob（playerForced 成立）→ StartJob 边界预加载钩子天然命中。但移动被 VF 整体
    /// 接管：VF 用 Prefix 把 Pawn_PathFollower.StartPath 重定向到 vehicle.vehiclePather.StartPath
    /// （Patch_VehiclePathing.StartVehiclePath），逐格推进在 VehiclePathFollower 自有 PatherTick →
    /// TryEnterNextPathCell（private）→ 我方挂在 Pawn_PathFollower 上的两大触发器对载具失明，
    /// 且 VF 的 Prefix 返回 false 会短路我方挂同方法的跨图包装。
    ///
    /// ==== 跨图传送机制（v2，唯一权威叙述，勿再叠加补丁）====
    /// 一辆车跨缝 = 复刻 VF 官方进图管线（EnterMapUtilityVehicles / Skyfaller 的模式），全部车辆
    /// 特殊逻辑收敛在 <see cref="SeamlessMapTransfer.TryTransferPawn"/> 的车辆分支三步：
    ///   ① <see cref="EnsureGridsReady"/>——对图 pathGrid.Enabled / regionGrid !Suspended 就绪判定，
    ///      未就绪则 RequestGridsFor(def, **Urgency.Urgent**)：主线程**同步**生成路径网格+区域网格
    ///     （VF 官方对非玩家派系载具与 Skyfaller 的同款做法；玩家载具平时 Deferred 只为防卡顿，
    ///      低频传送事件同步生成可接受）。已就绪零成本跳过。
    ///   ② <see cref="TryResolveVehicleArrivalCell"/>——落点 = spot 镜像格 → 整车矩形 + 他车占用
    ///      判定（VF 官方判据 MapHelper.NonStandableOrVehicleBlocked = CellRectStandable ∪
    ///      VehicleInhabitingCells）→ 不过则径向搜索（上限 2×车长）→ 找不到 = 拒绝传送并明确
    ///      记录（对端接缝无车辆可站地块，真实地形限制）。
    ///   ③ 原 DeSpawn/GenSpawn.Spawn + pather.Notify_Teleported()（清旧图路径状态）。
    /// 传送后网格已就绪 → 续程（ContinueBridgeMove）与 pawn 完全同路，无需任何等待/重试机制。
    ///
    /// ==== 历史教训（v1 五轮补丁为何全错，勿重蹈）====
    /// v1 在"对图网格未生成"的状态下判定/传送/下令：未生成时 Drivable 假阳性（未初始化 costGrid
    /// 默认全 0=可走）、CanReachVehicle 恒 false（区域网格无区域 → 搜索空队列 → "Tried to queue
    /// null region"）、TryFindNearestStandableCell 自指失效（从坏格出发，非自身格全被其内部的
    /// CanReachVehicle 否决、自身格过不了 Standable 检查 → 恒 false）——于是"等待队列/看护踢发/
    /// 落点校正/网格自愈锤"逐个失效循环。根因只有一个：**先同步就绪化网格，再做一切判定**。
    ///
    /// 本层四个 patch（全部手动绑定——VF 是可选依赖不能编译期引用；离线验证器不覆盖，须盯启动
    /// 日志 "VF compat: bound" 行）：
    /// A. VehiclePathFollower.TryEnterNextPathCell Prefix——镜像踩点触发器（vehicle/nextCell 都是
    ///    public 字段）。含近距兜底：VF 的 Goto 终点按整车矩形解析成 spot 邻近格（A* 路径不正好
    ///    止于 spot 格），活动 Bridge 许可 + BoundSpot 距载具切比雪夫 ≤2 时直接以 BoundSpot 触发
    ///    传送——传送坐标映射只依赖 spot（"不校验格距"既有契约）。
    /// B. VehiclePathFollower.StartPath Prefix——镜像跨图桥接包装（playerForced job 目标在邻图 →
    ///    TryBridgeJob 以 NextJob=curJob 桥接）；仅 playerForced 分支。
    /// C. FloatMenuOptionProvider_OrderVehicle.PawnGotoAction Prefix——载具右键 GoHere 跨图桥接
    ///    （终点用登记格 ct.cell 邻图框架，gotoLoc 是宿主图同数字坐标格对邻图无意义）。
    /// D. VehicleCanGotoPostfix——重放会话中跨图点击的灰显判定：pawn 保持强制放行（桥接有完整
    ///    兜底链）；载具改 CanBridgeTo 实测（内部 CanReachLocal = VF 口径，与执行侧选点同口径）。
    ///
    /// 绑定纪律（AGENTS）：软检测 mod id（未装短路零开销）；类型/签名漂移降级 Warning 不崩；
    /// 各 Prefix/Postfix 体全程 try/catch 吞异常放行原生。
    /// </summary>
    public static class SeamlessVehiclesCompat
    {
        private static bool _initialized;

        private static Type _vehiclePawnType;
        private static Type _vehicleDefType;
        private static Type _pathFollowerType;
        private static Type _pathingSystemType;

        private static FieldInfo _followerVehicleField;
        private static FieldInfo _followerNextCellField;
        private static FieldInfo _vehiclePatherField;
        private static FieldInfo _vehicleDefSizeField;
        private static PropertyInfo _vehicleDefProp;
        private static MethodInfo _notifyTeleportedMethod;
        private static MethodInfo _requestGridsForMethod;
        private static MethodInfo _canReachVehicleMethod;
        private static MethodInfo _nonStandableBlockedMethod;
        private static MethodInfo _tryFindNearestStandableMethod;

        // 重入守卫：patch E 的 Prefix 会经选点链调用被 patch 的同一方法（TryFindNearestStandableCell），
        // 包装器 Invoke 期间置位、Prefix 看到即放行原方法体，防无限递归。
        private static bool _inVehicleStandableSearch;

        // 就绪判定链：VehiclePathingSystem[def]（get_Item）→ pathData.Suspended + pathData.VehiclePathGrid.Enabled。
        private static MethodInfo _pathDataIndexerMethod;
        private static PropertyInfo _pathGridProp;
        private static PropertyInfo _pathGridEnabledProp;
        private static PropertyInfo _pathDataSuspendedProp;
        private static MethodInfo _pathGridWalkableMethod;

        // =====================================================================================
        // 绑定（RimExodusMod 构造器调用，PatchAll 之后）
        // =====================================================================================

        public static void Register(Harmony harmony)
        {
            try
            {
                var vfLoaded = false;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    var id = mod.PackageIdPlayerFacing;
                    if (id != null && id.ToLower() == "smashphil.vehicleframework") { vfLoaded = true; break; }
                }
                if (!vfLoaded) return;

                _vehiclePawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");
                _vehicleDefType = AccessTools.TypeByName("Vehicles.VehicleDef");
                _pathFollowerType = AccessTools.TypeByName("Vehicles.VehiclePathFollower");
                _pathingSystemType = AccessTools.TypeByName("Vehicles.VehiclePathingSystem");
                if (_vehiclePawnType == null || _pathFollowerType == null)
                {
                    Log.Warning($"[RimExodus] VF compat: Vehicle Framework is loaded but types not found (VehiclePawn={_vehiclePawnType}, VehiclePathFollower={_pathFollowerType}) — compat disabled.");
                    return;
                }

                // 字段/方法缓存（vehicle 与 nextCell 都是 public——VF 源码 "TODO fix access modifiers"）。
                _followerVehicleField = AccessTools.Field(_pathFollowerType, "vehicle");
                _followerNextCellField = AccessTools.Field(_pathFollowerType, "nextCell");
                _vehiclePatherField = AccessTools.Field(_vehiclePawnType, "vehiclePather");
                _notifyTeleportedMethod = AccessTools.Method(_pathFollowerType, "Notify_Teleported");
                _vehicleDefProp = AccessTools.Property(_vehiclePawnType, "VehicleDef");
                // VehicleDef 的尺寸成员是小写 size 字段（VF 源码直接 size.x/size.z）；
                // 首版反射取 "Size" 恒 null → 落点搜索半径退化为默认 6（2026-08 实测
                // "no standable arrival cell within radius 6" 对大型载具过小）。双名兜底。
                _vehicleDefSizeField = _vehicleDefType == null ? null
                    : (AccessTools.Field(_vehicleDefType, "size") ?? AccessTools.Field(_vehicleDefType, "Size"));

                // 传送三步的反射面。
                var mapHelperType = AccessTools.TypeByName("Vehicles.MapHelper");
                _nonStandableBlockedMethod = mapHelperType == null ? null
                    : AccessTools.Method(mapHelperType, "NonStandableOrVehicleBlocked",
                        new[] { _vehiclePawnType, typeof(Map), typeof(IntVec3), typeof(Rot4) });
                _requestGridsForMethod = _pathingSystemType == null ? null
                    : AccessTools.Method(_pathingSystemType, "RequestGridsFor", new[] { _vehicleDefType, AccessTools.TypeByName("Vehicles.DeferredGridGeneration")?.GetNestedType("Urgency") });
                if (_pathingSystemType != null && _vehicleDefType != null)
                {
                    // 勿按名查找 VehiclePathData——它是嵌套类（VehiclePathingSystem.VehiclePathData），
                    // 点分隔全名 TypeByName 恒 null（2026-08 实测教训：判定链静默缺失 → EnsureGridsReady
                    // 复查恒 false → "still not ready" 无限拒传）。Suspended/VehiclePathGrid 声明在非嵌套
                    // 基类 Vehicles.PathData 上，从索引器返回值取类型对继承 public 属性天然可见。
                    _pathDataIndexerMethod = AccessTools.Method(_pathingSystemType, "get_Item", new[] { _vehicleDefType });
                    var pathDataType = _pathDataIndexerMethod?.ReturnType;
                    if (pathDataType != null)
                    {
                        _pathGridProp = AccessTools.Property(pathDataType, "VehiclePathGrid");
                        _pathDataSuspendedProp = AccessTools.Property(pathDataType, "Suspended");
                        var pathGridType = _pathGridProp?.PropertyType;
                        if (pathGridType != null)
                        {
                            _pathGridEnabledProp = AccessTools.Property(pathGridType, "Enabled");
                            _pathGridWalkableMethod = AccessTools.Method(pathGridType, "Walkable", new[] { typeof(IntVec3) });
                        }
                    }
                    if (_pathDataIndexerMethod == null || _pathGridProp == null || _pathDataSuspendedProp == null || _pathGridEnabledProp == null)
                    {
                        Log.Warning($"[RimExodus] VF compat: grid readiness reflection chain incomplete "
                            + $"(indexer={_pathDataIndexerMethod != null}, pathGrid={_pathGridProp != null}, "
                            + $"suspended={_pathDataSuspendedProp != null}, enabled={_pathGridEnabledProp != null}) "
                            + "— vehicle cross-map transfer will always be rejected.");
                    }
                }
                _canReachVehicleMethod = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleReachabilityUtility"), "CanReachVehicle");

                // VF 原生"就近合法终点"（PathingHelper.cs:460）：Standable + DrivableRectOnCell(AnyRotation)
                // + 无他车 + CanReachVehicle——整车矩形口径，菜单 GoHere 的 gotoLoc 即来自它（2026-08 第三轮
                // 核心判据：region 可达是单格语义，单独用它选终点会被 VF A* 的终点矩形门槛截断 →
                // "ran out of path nodes" PatherFailed）。
                _tryFindNearestStandableMethod = AccessTools.Method(AccessTools.TypeByName("Vehicles.PathingHelper"),
                    "TryFindNearestStandableCell");

                var bound = 0;

                // A. 逐格推进 → 踩传送点传送（镜像 Patch_Pawn_PathFollower_TryEnterNextPathCell + 近距兜底）。
                var tryEnter = AccessTools.Method(_pathFollowerType, "TryEnterNextPathCell");
                var tryEnterPrefix = AccessTools.Method(typeof(SeamlessVehiclesCompat), nameof(TryEnterNextPathCellPrefix));
                if (tryEnter != null && tryEnterPrefix != null)
                {
                    harmony.Patch(tryEnter, prefix: new HarmonyMethod(tryEnterPrefix));
                    bound++;
                }

                // B. playerForced job 目标在邻图 → 桥接（镜像 Patch_Pawn_PathFollower_StartPath_CrossMap）。
                var startPath = AccessTools.Method(_pathFollowerType, "StartPath");
                var startPathPrefix = AccessTools.Method(typeof(SeamlessVehiclesCompat), nameof(StartPathPrefix));
                if (startPath != null && startPathPrefix != null)
                {
                    harmony.Patch(startPath, prefix: new HarmonyMethod(startPathPrefix));
                    bound++;
                }

                // C. 载具右键 GoHere 的跨图桥接（镜像 Patch_DraftedMove_PawnGotoAction_CrossMap）。
                var orderType = AccessTools.TypeByName("Vehicles.FloatMenuOptionProvider_OrderVehicle");
                var gotoAction = orderType == null ? null : AccessTools.Method(orderType, "PawnGotoAction");
                var gotoActionPrefix = AccessTools.Method(typeof(SeamlessVehiclesCompat), nameof(PawnGotoActionPrefix));
                if (gotoAction != null && gotoActionPrefix != null)
                {
                    harmony.Patch(gotoAction, prefix: new HarmonyMethod(gotoActionPrefix));
                    bound++;
                }

                // D. 重放会话中跨图点击的灰显判定（载具 = CanBridgeTo 实测）。
                var canGoto = orderType == null ? null : AccessTools.Method(orderType, "VehicleCanGoto");
                var canGotoPostfix = AccessTools.Method(typeof(SeamlessVehiclesCompat), nameof(VehicleCanGotoPostfix));
                if (canGoto != null && canGotoPostfix != null)
                {
                    harmony.Patch(canGoto, postfix: new HarmonyMethod(canGotoPostfix));
                    bound++;
                }

                // E. 菜单门槛（2026-08-25 回程灰显修复）：VF GetSingleOption 拿重放邻图框架坐标在本图
                // 跑 TryFindNearestStandableCell——投影区域对大型载具不友好时 GoHere 直接灰显
                //（VehicleCanGoto 都没被调，Postfix D 无从介入；去程能过纯靠投影区域恰好开阔的地形运气，
                // 方向不对称）。重放窗口内改用"指向目标图的桥接 goto 格"（同一 VF 整车矩形判据选出）。
                if (_tryFindNearestStandableMethod != null)
                {
                    var standablePrefix = AccessTools.Method(typeof(SeamlessVehiclesCompat), nameof(TryFindNearestStandableCellPrefix));
                    if (standablePrefix != null)
                    {
                        harmony.Patch(_tryFindNearestStandableMethod, prefix: new HarmonyMethod(standablePrefix));
                        bound++;
                    }
                }

                if (bound == 0)
                {
                    Log.Warning($"[RimExodus] VF compat: signature drift, no patches bound (TryEnterNextPathCell={tryEnter != null}, StartPath={startPath != null}, PawnGotoAction={gotoAction != null}, VehicleCanGoto={canGoto != null}) — compat disabled.");
                    return;
                }

                _initialized = true;
                Log.Message($"[RimExodus] VF compat: bound {bound} vehicle patches "
                    + $"(grids={_requestGridsForMethod != null}, arrivalCheck={_nonStandableBlockedMethod != null}, reachability={_canReachVehicleMethod != null}).");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: binding failed (mod continues, Vehicle Framework stays unadapted): {ex}");
            }
        }

        // =====================================================================================
        // A. 逐格推进 → 踩传送点跨缝传送（+ 近距兜底）
        // =====================================================================================

        private static bool TryEnterNextPathCellPrefix(object __instance)
        {
            if (!_initialized) return true;
            try
            {
                var vehicle = _followerVehicleField?.GetValue(__instance) as Pawn;
                if (vehicle == null || !vehicle.Spawned || vehicle.Map == null) return true;

                var nextCell = (IntVec3)_followerNextCellField.GetValue(__instance);
                if (!nextCell.IsValid || !nextCell.InBounds(vehicle.Map)) return true;

                var mapBefore = vehicle.Map;
                SeamlessMapTransferTrigger.TryTriggerTransfer(vehicle, nextCell, mapBefore);

                if (vehicle.Map == mapBefore
                    && SeamlessTransferGrants.TryGet(vehicle, out var vGrant)
                    && vGrant.BoundSpot.IsValid
                    && SeamlessGridMath.ChebyshevDistance(vehicle.Position, vGrant.BoundSpot) <= VehicleNearRadius(vehicle))
                {
                    // 近距兜底：VF 的 A* 对多格载具把 Goto 终点解析成"整车可站"的 spot 邻近格，
                    // 路径不正好止于 spot 格——距 BoundSpot 在车长口径内（车头已探进传送圈）
                    // 直接以 BoundSpot 触发传送（坐标映射只依赖 spot，"不校验格距"既有契约）。
                    // 半径按整车最长边/2 缩放（2026-08：固定 ≤2 对大型载具过窄）。
                    SeamlessMapTransferTrigger.TryTriggerTransfer(vehicle, vGrant.BoundSpot, mapBefore);
                }

                // 传送发生（判据 = Map 前后变化，同原版教训——不能用 Spawned 判定）：跳过原方法体，
                // 旧图 path/nextCell 状态跑 VF 方法体必然错乱。传送后的 pather 清理
                // （Notify_Teleported）与网格就绪化统一在 TryTransferPawn 车辆分支内完成。
                return vehicle.Map == mapBefore;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: TryEnterNextPathCell prefix error (swallowed, vanilla continues): {ex}");
                return true;
            }
        }

        // =====================================================================================
        // B. playerForced job 目标在邻图 → 桥接（传送后由 Grant.NextJob 续跑原 job）
        // =====================================================================================

        private static bool StartPathPrefix(object __instance, LocalTargetInfo dest)
        {
            if (!_initialized) return true;
            try
            {
                var vehicle = _followerVehicleField?.GetValue(__instance) as Pawn;
                if (vehicle?.Map == null || vehicle.Destroyed) return true;
                var job = vehicle.jobs?.curJob;
                if (job == null || !job.playerForced) return true;
                if (!SeamlessBoundaryRules.IsCrossMapOrderable(vehicle)) return true;

                Map targetMap = null;
                var finalCell = IntVec3.Invalid;
                if (dest.HasThing && dest.Thing != null)
                {
                    targetMap = dest.Thing.Map;
                    finalCell = dest.Thing.Position;
                }
                else if (dest.Cell.IsValid
                    && SeamlessCommandTargets.TryGet(vehicle, out var ct)
                    && ct.map != vehicle.Map
                    && dest.Cell == ct.cell)
                {
                    targetMap = ct.map;
                    finalCell = ct.cell;
                }
                if (targetMap == null || targetMap == vehicle.Map) return true;
                if (!SeamlessCombatCoords.TryGetCombatLink(vehicle.Map, targetMap, out _)) return true;

                if (SeamlessCrossMapOrders.TryBridgeJob(vehicle, targetMap, finalCell, job))
                {
                    if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                        Log.Message($"[RimExodus] VF compat: cross-map job wrap: {vehicle.LabelShort} {job.def.defName} -> map {targetMap.uniqueID}; resume after transfer.");
                    return false;
                }
                return true; // 桥接不可达：放行原生（对齐不可达表现）。
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: StartPath prefix error (swallowed, vanilla continues): {ex}");
                return true;
            }
        }

        // =====================================================================================
        // C. 载具右键 GoHere 跨图桥接
        // =====================================================================================

        private static bool PawnGotoActionPrefix(IntVec3 clickCell, object vehicle, IntVec3 gotoLoc)
        {
            if (!_initialized) return true;
            try
            {
                var pawn = vehicle as Pawn;
                if (pawn?.Map == null) return true;
                var verbose = RimExodusLog.Enabled(RimExodusLogModule.Compat);

                Map targetMap = null;
                IntVec3 targetCell = IntVec3.Invalid;
                if (SeamlessCommandTargets.TryGet(pawn, out var ct) && ct.map != pawn.Map)
                {
                    targetMap = ct.map;
                    targetCell = ct.cell;
                }
                else if (SeamlessMapUtility.TryResolveMapPosition(clickCell.ToVector3Shifted(), pawn.Map, out var ownerMap, out var ownerCell)
                    && ownerMap != pawn.Map)
                {
                    // 登记缺失恢复（2026-08-25"无畏舰走本图同数字坐标"症状）：VF 传入的 clickCell
                    // 是重放邻图框架格，登记被中间某次本图菜单清除时按本图坐标解析兜底——数字恰
                    // 落在渲染邻图区域（void 带）则恢复出目标图/格；解析不出（=真本图命令）才放行。
                    targetMap = ownerMap;
                    targetCell = ownerCell;
                    if (verbose)
                        Log.Message($"[RimExodus] VF compat: PawnGotoAction registration missing for {pawn.LabelShort}, "
                            + $"recovered map {targetMap.uniqueID} cell {targetCell} from clickCell {clickCell}.");
                }
                else if (verbose)
                {
                    Log.Message($"[RimExodus] VF compat: PawnGotoAction decline for {pawn.LabelShort}: no cross-map registration "
                        + $"(clickCell {clickCell} resolves locally), letting vanilla Goto proceed.");
                }

                if (targetMap == null) return true;
                if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, targetMap, out var link))
                {
                    if (verbose)
                        Log.Message($"[RimExodus] VF compat: PawnGotoAction decline for {pawn.LabelShort}: no combat link "
                            + $"map {pawn.Map.uniqueID} -> map {targetMap.uniqueID}.");
                    return true;
                }

                // 不可跨图下令的主体吞掉（对齐原版"不可对其下令移动"）；可下令主体无论桥接成败
                // 都吞原生——原生会在本图坐标上发 job + 误读 IsExitCell 组队。终点用登记格
                // ct.cell（邻图框架）而非 gotoLoc（VF 在宿主图上解析的同数字坐标格）。
                if (!SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return false;

                if (SeamlessCrossMapOrders.TryBridgeJob(pawn, targetMap, targetCell))
                {
                    FleckMaker.Static(targetCell.ToVector3Shifted() + Patches_CombatVisuals.OffsetVector(in link),
                        pawn.Map, FleckDefOf.FeedbackGoto);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: PawnGotoAction prefix error (swallowed, vanilla continues): {ex}");
                return true;
            }
        }

        // =====================================================================================
        // D. 重放会话中跨图点击的灰显判定
        // =====================================================================================

        private static void VehicleCanGotoPostfix(object vehicle, ref AcceptanceReport __result)
        {
            if (!_initialized || !SeamlessReplayContext.Active) return;
            try
            {
                var pawn = vehicle as Pawn;
                if (pawn == null || !SeamlessCommandTargets.TryGet(pawn, out var ct) || ct.map == pawn.Map) return;
                if (!SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return;
                if (!IsVehicle(pawn))
                {
                    // pawn 桥接有完整兜底链，保持强制放行。
                    __result = AcceptanceReport.WasAccepted;
                    return;
                }
                // 载具：VF 的 CanReachVehicle 在宿主图网格判邻图坐标格，答案无意义（恒 false 灰显），
                // 但也不能无条件放行（选点对 VehicleDef 可能真不可达）。改 CanBridgeTo 实测——
                // 内部走 CanReachLocal（VF 口径），与执行侧 TryPickVehicleBridgeSpot 同判据；
                // 不可达时保持 VF 的"无法通过此处"（诚实状态）。
                if (SeamlessCrossMapOrders.CanBridgeTo(pawn, SeamlessReplayContext.Target))
                {
                    __result = AcceptanceReport.WasAccepted;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: VehicleCanGoto postfix error (swallowed): {ex}");
            }
        }

        // =====================================================================================
        // E. 菜单门槛：重放窗口内的载具 GoHere 亮暗/gotoLoc 改用桥接 goto 格
        // =====================================================================================

        /// <summary>
        /// Prefix 挂 VF 的 <c>PathingHelper.TryFindNearestStandableCell</c>（GetSingleOption 用它把
        /// clickCell 解析成 gotoLoc——重放时 clickCell 是邻图框架坐标，读在本图上对大型载具常是
        /// 敌意地形 → 恒 false → GoHere 灰显且 Postfix D 无从介入，去程能过纯靠地形运气 = 方向
        /// 不对称的根源）。重放激活且载具在宿主图上时改答"指向目标图的桥接 goto 格"（与执行侧
        /// 同一 VF 整车矩形判据）；选不出 = 本图该 VehicleDef 真到不了缝，诚实灰显（原生在无意义
        /// 的框架坐标上搜索只会撞运气）。重入守卫见 _inVehicleStandableSearch。
        /// </summary>
        private static bool TryFindNearestStandableCellPrefix(object vehicle, IntVec3 cell, ref IntVec3 result, ref bool __result)
        {
            if (!_initialized || _inVehicleStandableSearch) return true;
            if (!SeamlessReplayContext.Active) return true;
            try
            {
                var pawn = vehicle as Pawn;
                if (pawn?.Map == null || pawn.Map != SeamlessReplayContext.Host) return true;
                if (!IsVehicle(pawn) || !SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return true;

                if (SeamlessCrossMapOrders.TryFindVehicleMenuGoto(pawn, SeamlessReplayContext.Target, out var gotoCell))
                {
                    result = gotoCell;
                    __result = true;
                    return false;
                }
                __result = false;
                result = IntVec3.Invalid;
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: TryFindNearestStandableCell prefix error (swallowed, vanilla continues): {ex}");
                return true;
            }
        }

        // =====================================================================================
        // 传送三步（TryTransferPawn 车辆分支消费；未装 VF / 非vehicles = no-op）
        // =====================================================================================

        /// <summary>该 pawn 是否 VF 载具（未装 VF 恒 false）。</summary>
        public static bool IsVehicle(Pawn pawn)
        {
            return _initialized && pawn != null && _vehiclePawnType.IsInstanceOfType(pawn);
        }

        /// <summary>VF 规范"就近合法终点"反射是否在位（缺失时选点退回 region 口径环扫）。</summary>
        public static bool VehicleStandableReflectionAvailable => _tryFindNearestStandableMethod != null;

        /// <summary>
        /// 载具的"近 spot 统一半径"（锚格距 spot 的最大切比雪夫距离，选点搜索/触发兜底/菜单共用）：
        /// R = max(min(边)×2, ⌈最长边/2⌉+1)。min(边)×2 = VF 原生 TryFindNearestStandableCell 的默认
        /// 搜索半径；⌈最长边/2⌉+1 = 长条车朝核心侧内偏的余量（接缝带 ~3 格宽、OuterRing 之外即 void，
        /// 大车矩形只能向内安放）。三处必须同口径，否则"选得到 goto 格但触发兜底够不着"。
        /// 反射失败回落 2（旧口径）。
        /// </summary>
        public static int VehicleNearRadius(Pawn vehicle)
        {
            if (!_initialized || vehicle == null) return 2;
            try
            {
                var def = _vehicleDefProp?.GetValue(vehicle);
                var size = def == null ? null : _vehicleDefSizeField?.GetValue(def);
                if (size is IntVec2 s && (s.x > 0 || s.z > 0))
                {
                    var r = Math.Max(Math.Min(s.x, s.z) * 2, (Math.Max(s.x, s.z) + 1) / 2 + 1);
                    return Math.Max(r, 2);
                }
            }
            catch { /* 尺寸反射失败回落旧口径 */ }
            return 2;
        }

        /// <summary>
        /// VF 原生"就近合法终点"包装（整车矩形口径）：Standable + DrivableRectOnCell(AnyRotation) +
        /// 无他车阻挡 + CanReachVehicle（从载具当前位置）。半径用 <see cref="VehicleNearRadius"/> 统一口径。
        /// 反射缺失返回 false（调用方回落旧环扫逻辑）。
        /// </summary>
        public static bool TryFindVehicleStandableNear(Pawn vehicle, IntVec3 cell, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (!_initialized || vehicle == null || vehicle.Map == null || _tryFindNearestStandableMethod == null) return false;
            try
            {
                var args = new object[] { vehicle, cell, IntVec3.Invalid, (float)VehicleNearRadius(vehicle) };
                _inVehicleStandableSearch = true;
                try
                {
                    var ok = (bool)_tryFindNearestStandableMethod.Invoke(null, args);
                    if (ok) result = (IntVec3)args[2];
                    return ok;
                }
                finally
                {
                    _inVehicleStandableSearch = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: TryFindNearestStandableCell invoke failed (falling back to ring scan): {ex}");
                return false;
            }
        }

        /// <summary>
        /// ①对图网格同步就绪化：pathGrid.Enabled 且 regionGrid 未 Suspended 即就绪；未就绪则
        /// RequestGridsFor(def, Urgency.Urgent)——主线程同步生成（VF 官方模式，见类注释）。
        /// 返回 false = 反射面缺失/生成后仍未就绪（异常态），调用方应放弃本次传送。
        /// </summary>
        public static bool EnsureGridsReady(Pawn vehicle, Map map)
        {
            if (!_initialized || !IsVehicle(vehicle) || map == null) return false;
            try
            {
                if (IsGridsReady(vehicle, map)) return true;
                var system = GetPathingSystem(map);
                var def = _vehicleDefProp?.GetValue(vehicle);
                if (system == null || def == null || _requestGridsForMethod == null)
                {
                    Log.Warning($"[RimExodus] VF compat: pathing system reflection unavailable on map {map.uniqueID} — vehicle transfer rejected.");
                    return false;
                }
                var urgencyType = _requestGridsForMethod.GetParameters()[1].ParameterType;
                var urgent = Enum.Parse(urgencyType, "Urgent");
                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    Log.Message($"[RimExodus] VF compat: synchronously generating VF grids for {vehicle.LabelShort} on map {map.uniqueID} (Urgent).");
                _requestGridsForMethod.Invoke(system, new[] { def, urgent });
                if (!IsGridsReady(vehicle, map))
                {
                    Log.Warning($"[RimExodus] VF compat: VF grids still not ready after Urgent generation on map {map.uniqueID} — vehicle transfer rejected.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: EnsureGridsReady failed: {ex}");
                return false;
            }
        }

        /// <summary>就绪判定：VehiclePathingSystem[def].VehiclePathGrid.Enabled 且 !Suspended（MapHelper.UnfogMapFromEdge 同款范式）。</summary>
        private static bool IsGridsReady(Pawn vehicle, Map map)
        {
            if (_pathDataIndexerMethod == null || _pathGridEnabledProp == null || _pathDataSuspendedProp == null) return false;
            var system = GetPathingSystem(map);
            var def = _vehicleDefProp?.GetValue(vehicle);
            if (system == null || def == null) return false;
            var pathData = _pathDataIndexerMethod.Invoke(system, new[] { def });
            if (pathData == null) return false;
            var pathGrid = _pathGridProp?.GetValue(pathData);
            return pathGrid != null
                && (bool)_pathGridEnabledProp.GetValue(pathGrid)
                && !(bool)_pathDataSuspendedProp.GetValue(pathData);
        }

        /// <summary>
        /// ②落点解析：arrivalCell（spot 镜像格）按 <see cref="IsBlockedAt"/>（VF Drivable 口径：
        /// 目标图 pathGrid 整车矩形 + 他车占用，容忍 thing）不可用时径向找最近可用格（上限 2×车长，
        /// 防把车挪去远处）。每个候选格对 <see cref="Rot4"/> 四向放宽（当前朝向优先、命中即写回
        /// rotation 供 Spawn 落地）——不能复用 TryFindNearestStandableCell：它内部全走
        /// vehicle.Map（源图）与 CanReachVehicle（从当前位置），跨图落点判定必须显式传目标 map。
        /// 返回 false = 半径内无任何整车可站格 → 调用方拒绝传送（对端接缝无车辆可站地块）。
        /// 反射缺失时返回 true 保持旧行为（不校验）。
        /// </summary>
        public static bool TryResolveVehicleArrivalCell(Pawn vehicle, Map map, ref IntVec3 cell, ref Rot4 rotation)
        {
            if (!_initialized || !IsVehicle(vehicle) || _nonStandableBlockedMethod == null) return true;
            try
            {
                if (!IsBlockedAt(vehicle, map, cell, rotation)) return true;

                var cap = 6;
                try
                {
                    var def = _vehicleDefProp?.GetValue(vehicle);
                    var size = def == null ? null : _vehicleDefSizeField?.GetValue(def);
                    if (size is IntVec2 s)
                    {
                        cap = Math.Max(6, 2 * Math.Max(s.x, s.z));
                    }
                }
                catch { /* Size 反射失败用默认半径 */ }

                foreach (var offset in GenRadial.RadialPattern)
                {
                    if (offset == IntVec3.Zero) continue;
                    var candidate = cell + offset;
                    if (!candidate.InBounds(map)) continue;
                    if (SeamlessGridMath.ChebyshevDistance(candidate, cell) > cap) break;
                    var rot = ResolveStandableRotation(vehicle, map, candidate, rotation);
                    if (!rot.IsValid) continue;
                    if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                        Log.Message($"[RimExodus] VF compat: arrival cell {cell} unusable for {vehicle.LabelShort}, "
                            + $"resolved to {candidate} (rot {rot.AsInt}) on map {map.uniqueID}.");
                    cell = candidate;
                    rotation = rot;
                    return true;
                }

                Log.Warning($"[RimExodus] VF compat: no standable arrival cell within radius {cap} of {cell} on map {map.uniqueID} "
                    + $"for {vehicle.LabelShort} (any rotation) — transfer rejected (seam terrain impassable for this vehicle).");
                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    LogArrivalDiagnostics(vehicle, map, cell, cap);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: TryResolveVehicleArrivalCell failed (cell kept as-is): {ex}");
                return true;
            }
        }

        /// <summary>候选格上首个可站朝向（当前朝向优先；四向全败返回 Invalid）。</summary>
        private static Rot4 ResolveStandableRotation(Pawn vehicle, Map map, IntVec3 cell, Rot4 current)
        {
            if (!IsBlockedAt(vehicle, map, cell, current)) return current;
            for (int i = 0; i < 4; i++)
            {
                var rot = new Rot4(i);
                if (rot == current) continue;
                if (!IsBlockedAt(vehicle, map, cell, rot)) return rot;
            }
            return Rot4.Invalid;
        }

        /// <summary>
        /// 拒绝时的裁决诊断（verbose）：采样最近若干界内候选格，报告 VF pathGrid（Drivable 口径）
        /// 可走比例——grid-blocked 为主 = 地形/网格对该车型不可走（真实地形限制）；grid-ok 为主但仍拒
        /// = 他车占用或矩形越界（2026-08-25 回程拒绝长期无法定位的直接补课）。
        /// </summary>
        private static void LogArrivalDiagnostics(Pawn vehicle, Map map, IntVec3 center, int cap)
        {
            try
            {
                var pathGrid = GetPathGridFor(vehicle, map);
                var samples = 0;
                var gridWalkable = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var offset in GenRadial.RadialPattern)
                {
                    if (offset == IntVec3.Zero) continue;
                    var candidate = center + offset;
                    if (!candidate.InBounds(map)) continue;
                    if (SeamlessGridMath.ChebyshevDistance(candidate, center) > cap) break;
                    if (samples >= 8) break;
                    samples++;
                    bool walkable = pathGrid == null || _pathGridWalkableMethod == null
                        ? false
                        : (bool)_pathGridWalkableMethod.Invoke(pathGrid, new object[] { candidate });
                    if (walkable) gridWalkable++;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(candidate).Append(walkable ? ":grid-ok" : ":grid-blocked");
                }
                Log.Message($"[RimExodus] VF compat: arrival diagnostics near {center} on map {map.uniqueID}: "
                    + $"{gridWalkable}/{samples} sampled center cells VF-grid walkable ({sb}). "
                    + "grid-blocked 为主 = 地形/网格对该车型不可走；grid-ok 为主但仍拒 = 他车占用或整车矩形越界。");
            }
            catch (Exception ex)
            {
                Log.Message($"[RimExodus] VF compat: arrival diagnostics failed: {ex.Message}");
            }
        }

        /// <summary>③传送后清 vehiclePather 旧图路径状态（Notify_Teleported = StopDead + Reset，VF 原生语义）。</summary>
        public static void ResetVehiclePather(Pawn vehicle)
        {
            if (!_initialized || !IsVehicle(vehicle)) return;
            try
            {
                _notifyTeleportedMethod?.Invoke(_vehiclePatherField?.GetValue(vehicle), null);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: ResetVehiclePather failed (swallowed): {ex}");
            }
        }

        /// <summary>
        /// 整车阻挡判定（跨图复刻 VF <c>Drivable</c> 口径，2026-08-25 第五轮修正）：
        /// 矩形每格 = 目标图该车型 VF pathGrid 可走 + 无他车占用——**刻意不扫 thingGrid**
        /// （VF 原生 <c>Drivable</c>/<c>DrivableRectOnCell</c> 同款宽容：树/植物在 costGrid 里只是
        /// 通行代价非 Impassable，"树卡车底"合法；NonStandableOrVehicleBlocked 的 CellRectStandable
        /// 对 passability != Standable 的 thing 全拒 = 野外接缝环上一棵树就毙掉整个候选，是回程
        /// 四连拒的真根因之一）。矩形枚举零反射复刻 VehicleDef.VehicleRect（= GenAdj.OccupiedRect
        /// + West→East/South→North 朝向归一化）。pathGrid 反射缺失时回落旧 NonStandableOrVehicleBlocked
        /// （固定朝向调用，保底不崩）。
        /// </summary>
        private static bool IsBlockedAt(Pawn vehicle, Map map, IntVec3 cell, Rot4 rot)
        {
            var pathGrid = GetPathGridFor(vehicle, map);
            var def = _vehicleDefProp?.GetValue(vehicle);
            var size = def == null ? null : _vehicleDefSizeField?.GetValue(def) as IntVec2?;
            if (pathGrid == null || _pathGridWalkableMethod == null || size == null)
            {
                return (bool)_nonStandableBlockedMethod.Invoke(null, new object[] { vehicle, map, cell, rot });
            }
            if (rot == Rot4.West) rot = Rot4.East;
            if (rot == Rot4.South) rot = Rot4.North;
            var rect = GenAdj.OccupiedRect(cell, rot, size.Value);
            foreach (var rectCell in rect.Cells)
            {
                if (!rectCell.InBounds(map)) return true;
                if (!(bool)_pathGridWalkableMethod.Invoke(pathGrid, new object[] { rectCell })) return true;
            }
            foreach (var other in map.mapPawns.AllPawnsSpawned)
            {
                if (other == vehicle || !_vehiclePawnType.IsInstanceOfType(other)) continue;
                if (other.OccupiedRect().Overlaps(rect)) return true;
            }
            return false;
        }

        /// <summary>取目标图上该车型 VF pathGrid 实例（网格未生成/反射缺失 = null）。</summary>
        private static object GetPathGridFor(Pawn vehicle, Map map)
        {
            var system = GetPathingSystem(map);
            var def = _vehicleDefProp?.GetValue(vehicle);
            if (system == null || def == null || _pathDataIndexerMethod == null || _pathGridProp == null) return null;
            var pathData = _pathDataIndexerMethod.Invoke(system, new[] { def });
            return pathData == null ? null : _pathGridProp.GetValue(pathData);
        }

        /// <summary>取 map 上的 VehiclePathingSystem 组件（找不到 = null）。</summary>
        private static MapComponent GetPathingSystem(Map map)
        {
            if (_pathingSystemType == null) return null;
            var components = map.components;
            for (int i = 0; i < components.Count; i++)
            {
                if (_pathingSystemType.IsInstanceOfType(components[i])) return components[i];
            }
            return null;
        }

        // =====================================================================================
        // 其他辅助出口（未装 VF / 未绑定 = 无害 no-op / 原版值）
        // =====================================================================================

        /// <summary>
        /// 对 pawn 所在图请求该载具的 VF 路径网格（Deferred 异步热身——边界预加载窗口用，
        /// 让网格在载具走到传送点前就开始生成；传送时的同步就绪化见 EnsureGridsReady）。
        /// </summary>
        public static void RequestGridsOnMapFor(Pawn pawn)
        {
            RequestGridsOnMapFor(pawn, pawn?.Map);
        }

        /// <summary>同上，但指定目标图（预加载窗口载具还在本图上）。</summary>
        public static void RequestGridsOnMapFor(Pawn pawn, Map map)
        {
            if (!_initialized || _requestGridsForMethod == null) return;
            try
            {
                if (map == null || !_vehiclePawnType.IsInstanceOfType(pawn)) return;
                var system = GetPathingSystem(map);
                var def = _vehicleDefProp?.GetValue(pawn);
                if (system == null || def == null) return;
                var urgencyType = _requestGridsForMethod.GetParameters()[1].ParameterType;
                _requestGridsForMethod.Invoke(system, new[] { def, Enum.Parse(urgencyType, "Deferred") });
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VF compat: RequestGridsOnMapFor failed (swallowed): {ex}");
            }
        }

        /// <summary>
        /// VF 口径的图内可达性（载具 → VehicleReachabilityUtility.CanReachVehicle，内部走该
        /// VehicleDef 的 VF 网格）；非载具或 VF 反射不可用时回落原版 reachability。
        /// 供 SeamlessCrossMapOrders 的选点过滤与菜单探测——原版 CanReach + TraverseParms.For(pawn)
        /// 对载具的悬浮/轮式/涉水判定不成立。注意：依赖网格已生成（未生成时恒 false），
        /// 传送路径上必须先 EnsureGridsReady。
        /// </summary>
        public static bool CanReachLocal(Pawn pawn, Map map, IntVec3 start, IntVec3 dest)
        {
            if (_initialized && _canReachVehicleMethod != null && pawn != null && _vehiclePawnType.IsInstanceOfType(pawn))
            {
                try
                {
                    return (bool)_canReachVehicleMethod.Invoke(null,
                        new object[] { pawn, new LocalTargetInfo(dest), PathEndMode.OnCell, Danger.Deadly, TraverseMode.ByPawn });
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] VF compat: CanReachVehicle invoke failed (falling back to vanilla): {ex}");
                }
            }
            return map.reachability.CanReach(start, dest, PathEndMode.OnCell, TraverseParms.For(pawn));
        }
    }
}
