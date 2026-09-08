using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// PerspectiveShift（ferny.PerspectiveShift，第一/第三人称自由移动）兼容层（2026-08 v1）。
    ///
    /// 机制背景（references/PerspectiveShift.decompile 调研结论）：
    /// PS 的 WASD 移动完全绕过 job/pather 系统——每帧 Avatar.ProcessMovement 直写 pawn.Position
    /// + Notify_Teleported + pather.nextCell，并 StopDead 杀寻路。因此 RimExodus 挂在
    /// Pawn_JobTracker.StartJob（playerForced Goto 触发预加载）与 Pawn_PathFollower.TryEnterNextPathCell
    /// （nextCell 踩传送点分派 Grant）上的两大移动触发器对 avatar 全部失明：走向接缝不预加载、
    /// 踩传送点不传送，avatar 被 void 挡住（无危害但无缝性失效）。
    /// PS 的右键菜单/左键交互走原版 FloatMenu + TryTakeOrderedJob（job 一律 playerForced=true），
    /// 该链对 RimExodus 既有钩子天然生效，零改动。
    ///
    /// 本兼容层只补 WASD 缺口：Postfix 挂 PS 的 Avatar.ProcessMovement（每帧，avatar 位移换格时）——
    /// ① 换格进边界带（非接缝带/传送点格）→ 触发邻图预加载（判据链镜像 SeamlessBorderPreloader.CheckPawnGoto，
    /// 仅日志口径改为 verbose 门控——帧频调用下 Message 刷屏）；
    /// ② 换格踩传送点且对端为活跃邻居 → 即席登记 Bridge Grant（绑定该 spot；NextJob/FinalDest 均空 →
    /// ContinueBridgeMove 天然 no-op，不给 PS 控制的 avatar 下发 Goto——avatar 由玩家 WASD 驱动），
    /// 经 SeamlessMapTransferTrigger.TryTriggerTransfer 既有分派链传送（AfterTransfer 的相机聚焦/
    /// governor sweep/追击者扫描全复用；相机由 PS 锁 avatar 跟随 pawn 对象自然切换）。
    /// 对端未生成不即席生成（与战斗踩点资格同口径：预加载兜底，玩家走近的过程即生成过程）。
    ///
    /// 防镜像回传（两层）：传送落点（cachedArrivalCell）本身就是对端接缝带上的 spot 格，仅靠
    /// "落地帧豁免"（第一层：换图帧只重置基线不处理）不够——落地带内的后续踩点仍会被当成新的
    /// 跨缝意图，把 pawn 弹到同一世界位置的镜像格，恒定按住方向下形成沿缝振荡（2026-09 实测：
    /// 家园图↔邻图连环弹射、Throttle 刷屏、影子 scrub 副产物）。第二层 = **方向意图门**（用户
    /// 方案 2026-09，rev3 符号修正）：只在命令方向与邻居链 offsetDir（本图中心→对侧中心，实测
    /// 传送对数值定案的方向契约）同向的点积 ≥ MinCrossSeamDot 时才触发传送。非 PS 流不需要此门
    /// ——其传送许可由跨图移动命令开启、传送完成即消耗（一次性意图语义）；PS 流移动连续、"踩点"
    /// 会反复成立，用命令方向重建同等的一次性语义。恒定按住方向时，传送后该方向相对新图恒为
    /// 向内 → 带内后续踩点全不触发，pawn 自然走进图内；玩家真掉头立即放行（无重新武装代价）；
    /// 沿缝平行移动（分量≈0）不触发——平行于缝的移动本就不是跨缝意图。数据全现成、零多边形
    /// 计算：命令方向 = PS 的 moveInput（其 ProcessMovement 直接把它加到 physicsPosition 上 =
    /// 实际位移方向）；接缝法线 = 邻居表 offset（中心连线 ⟂ 共享边，见 TryGetCrossSeamDot）。
    ///
    /// 绑定纪律（AGENTS：手动绑定必须 try/catch，兼容绝不杀死 mod；离线验证器不覆盖手动绑定）：
    /// 软反射检测 mod 已加载（未装 = available=false 短路零开销零红字，SeamlessLandformsCompat 同款）；
    /// PS 类型/字段/方法签名变更 → 降级 Warning 不崩；Postfix 体全程 try/catch。
    /// physicsPosition（PS 的亚格浮点位置）在传送后置 null：否则 PS 下帧 desync 检测会以旧图坐标
    /// 重置并刷一条 Physics desync 警告（功能无损，纯日志噪音，此处预防性清掉）。
    /// </summary>
    public static class SeamlessPerspectiveShiftCompat
    {
        private static bool _initialized;
        private static FieldInfo _pawnField;
        private static FieldInfo _physicsPositionField;
        private static FieldInfo _moveInputField;
        private static PropertyInfo _stateAvatarProp;

        /// <summary>每 pawn 上次处理的格 + 所在图（只对换格帧做正事；跨图换格只重置基线）。</summary>
        private static readonly Dictionary<Pawn, IntVec3> LastCell = new Dictionary<Pawn, IntVec3>();
        private static readonly Dictionary<Pawn, Map> LastMap = new Dictionary<Pawn, Map>();

        /// <summary>PS 驾驶 VF 载具时的换格基线（key = 载具 pawn，与 avatar 步行基线分开存）。</summary>
        private static readonly Dictionary<Pawn, IntVec3> VehicleLastCell = new Dictionary<Pawn, IntVec3>();
        private static readonly Dictionary<Pawn, Map> VehicleLastMap = new Dictionary<Pawn, Map>();

        /// <summary>
        /// 检测 PerspectiveShift 已加载并手动绑定 Avatar.ProcessMovement Postfix（avatar 步行）
        /// + ModCompatibility.ProcessVehicleMovement Postfix（PS×VF：avatar 驾驶载具，双装才绑）。
        /// 由 RimExodusMod 构造器调用（时点 = PatchAll 之后、绑定报告之前，计数含本 patch）。
        /// </summary>
        public static void Register(Harmony harmony)
        {
            try
            {
                var psLoaded = false;
                var vfLoaded = false;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    var id = mod.PackageIdPlayerFacing;
                    if (id == null) continue;
                    var idLower = id.ToLower();
                    if (idLower == "ferny.perspectiveshift") psLoaded = true;
                    if (idLower == "smashphil.vehicleframework") vfLoaded = true;
                }
                if (!psLoaded) return;

                var avatarType = AccessTools.TypeByName("PerspectiveShift.Avatar");
                if (avatarType == null)
                {
                    Log.Warning("[RimExodus] PS compat: PerspectiveShift is loaded but type PerspectiveShift.Avatar was not found — compat disabled.");
                    return;
                }
                var processMovement = AccessTools.Method(avatarType, "ProcessMovement");
                _pawnField = AccessTools.Field(avatarType, "pawn");
                _physicsPositionField = AccessTools.Field(avatarType, "physicsPosition");
                // moveInput（方向意图门用）：拿不到不算绑定失败——门 fail-open 放行（维持旧行为），
                // 只是丧失防镜像第二层（ReadAvatarMoveInput 返回 zero → TryGetCrossSeamDot 放行）。
                _moveInputField = AccessTools.Field(avatarType, "moveInput");
                // State.Avatar 静态属性（FocusIfAvatarAboard 取 avatar pawn 用；拿不到 = 该功能降级，其余不受影响）。
                _stateAvatarProp = AccessTools.Property(AccessTools.TypeByName("PerspectiveShift.State"), "Avatar");
                var postfix = AccessTools.Method(typeof(SeamlessPerspectiveShiftCompat), nameof(ProcessMovementPostfix));
                if (processMovement == null || _pawnField == null || postfix == null)
                {
                    Log.Warning($"[RimExodus] PS compat: signature drift (ProcessMovement={processMovement}, pawn={_pawnField}, postfix={postfix}) — compat disabled.");
                    return;
                }

                harmony.Patch(processMovement, postfix: new HarmonyMethod(postfix));
                _initialized = true;
                // 修订标记（rev N: 机制名）= 加载验证标志（2026-09 教训：三轮实测全跑在旧二进制上——
                // 测试前必须先在启动日志确认本行含当前 rev 标记，否则观察结果无效）。
                Log.Message("[RimExodus] PS compat: bound Avatar.ProcessMovement postfix (PerspectiveShift WASD movement joins seamless world) (rev 4: direction intent gate).");

                // PS×VF 驾驶（2026-08）：avatar 进舱当驾驶员后 UpdatePhysics 走车内分支先 return——
                // ProcessMovement 不再跑（步行 Postfix 静默），WASD 由 PS 的 ModCompatibility.
                // ProcessVehicleMovement 每帧反射驱动 vehiclePather.StartPath（无 job，playerForced
                // 预加载链也失明）。双装才补绑驾驶 Postfix，否则驾驶开向接缝 = 无预加载 + 踩点无许可。
                if (vfLoaded) RegisterVehicleDriving(harmony);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: binding failed (mod continues, PerspectiveShift stays unadapted): {ex}");
            }
        }

        /// <summary>
        /// PS 驾驶态兼容（PS×VF 双装）：Postfix 挂 PS 的 ModCompatibility.ProcessVehicleMovement
        /// （public static bool(Pawn vehicle, Vector3 inputDir)，玩家按住方向键期间每帧调用）。
        /// 对载具做换格检测：①进边界带（非接缝带/传送点格）→ 预加载/唤醒对端；②踩传送点且对端
        /// 活跃 → 即席 Bridge Grant + 立即传送。Grant 刻意不带 NextJob/FinalDest（ContinueBridgeMove
        /// 天然 no-op）——载具由玩家 WASD 持续驾驶，传送后玩家继续开即续程，不对其下发 Goto。
        /// 传送后的 CurrentMap 切换不经此（驾驶传送经 VF 侧 TryEnterNextPathCell Prefix 或本 Postfix
        /// 即席路径，最终都过 AfterTransfer → FocusIfAvatarAboard 统一聚焦）。
        /// </summary>
        private static void RegisterVehicleDriving(Harmony harmony)
        {
            try
            {
                var modCompatType = AccessTools.TypeByName("PerspectiveShift.ModCompatibility");
                var processVehicleMovement = modCompatType == null ? null : AccessTools.Method(modCompatType, "ProcessVehicleMovement");
                var postfix = AccessTools.Method(typeof(SeamlessPerspectiveShiftCompat), nameof(ProcessVehicleMovementVehiclePostfix));
                if (processVehicleMovement == null || postfix == null)
                {
                    Log.Warning($"[RimExodus] PS compat: vehicle driving signature drift (ProcessVehicleMovement={processVehicleMovement}) — WASD vehicle driving stays unadapted.");
                    return;
                }
                harmony.Patch(processVehicleMovement, postfix: new HarmonyMethod(postfix));
                Log.Message("[RimExodus] PS compat: bound ProcessVehicleMovement postfix (PS-driven vehicles join seamless world).");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: vehicle driving binding failed (driving stays unadapted): {ex}");
            }
        }

        private static void ProcessVehicleMovementVehiclePostfix(Pawn vehicle, Vector3 inputDir)
        {
            if (!_initialized) return;
            try
            {
                if (vehicle == null || !vehicle.Spawned || vehicle.Map == null) return;
                var map = vehicle.Map;
                var cell = vehicle.Position;

                if (VehicleLastMap.TryGetValue(vehicle, out var lastMap) && lastMap == map
                    && VehicleLastCell.TryGetValue(vehicle, out var lastCell) && lastCell == cell)
                    return; // 同图同格：帧频热路径早退。

                var mapChanged = VehicleLastMap.TryGetValue(vehicle, out var prevMap) && prevMap != map;
                VehicleLastMap[vehicle] = map;
                VehicleLastCell[vehicle] = cell;
                if (mapChanged) return; // 跨图传送落地帧：只重置基线（防镜像回传，与 avatar 步行同口径）。

                // ① 边界带预加载（判据链与 avatar 步行/TryPreloadAt 同口径，主体换成载具）。
                TryPreloadAt(vehicle, map, cell);

                // ② 踩传送点即席 Bridge 传送（对端活跃才触发；不即席生成，预加载兜底）。
                TryVehicleCrossSeamAt(vehicle, map, cell, inputDir);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: ProcessVehicleMovement postfix error (swallowed): {ex}");
            }
        }

        /// <summary>PS 驾驶的载具踩传送点且对端活跃 → 即席 Bridge Grant + 既有分派链传送（inputDir = WASD 命令方向，方向意图门用）。</summary>
        private static void TryVehicleCrossSeamAt(Pawn vehicle, Map map, IntVec3 cell, Vector3 inputDir)
        {
            if (!SeamlessBoundaryRules.IsCrossMapOrderable(vehicle)) return;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;

                // 对端未生成/未加载：不即席生成（与 avatar 步行/战斗踩点资格同口径）。
                if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out _)) return;

                // 方向意图门（与 avatar 步行同款，rev4 定稿；方向用 postfix 现成的 inputDir 参数，零反射）。
                if (TryGetCrossSeamDot(map, comp.targetWorldTile, inputDir, out var vCrossDot)
                    && vCrossDot < MinCrossSeamDot)
                {
                    if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                        Log.Message($"[RimExodus] PS compat: PS-driven vehicle {vehicle.LabelShort} stepped on spot {thing.Position} on map {map.uniqueID} "
                            + $"but movement is not crossing outward (dot={vCrossDot:F2} < {MinCrossSeamDot:F2}) — not transferring.");
                    return;
                }

                // 驾驶意图由"玩家操纵载具驶上传送点"本身保证。Grant 不带 NextJob/FinalDest：
                // 传送后 ContinueBridgeMove no-op，玩家继续 WASD 驾驶（对载具下发续程 Goto 会脱离
                // 玩家控制——载具是正常 pather 驱动不假，但目的地已无意义，PS 也会 StopDead 干扰）。
                var grant = SeamlessTransferGrants.Create(vehicle, SeamlessTransferGrants.GrantKind.Bridge);
                grant.BoundSpot = thing.Position;

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    Log.Message($"[RimExodus] PS compat: PS-driven vehicle {vehicle.LabelShort} stepped on enter spot {thing.Position} "
                        + $"on map {map.uniqueID}, dispatching cross-seam transfer.");

                // 传送收尾（含 avatar 在舱内的 CurrentMap 聚焦）由 AfterTransfer → FocusIfAvatarAboard
                // 统一承担；pather 清理与网格就绪化在 TryTransferPawn 车辆分支内完成（v2 机制归一）。
                SeamlessMapTransferTrigger.TryTriggerTransfer(vehicle, cell, map);
                return;
            }
        }

        private static void ProcessMovementPostfix(object __instance)
        {
            if (!_initialized) return;
            try
            {
                var pawn = _pawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || !pawn.Spawned) return;
                var map = pawn.Map;
                if (map == null) return;

                var cell = pawn.Position;
                if (LastMap.TryGetValue(pawn, out var lastMap) && lastMap == map
                    && LastCell.TryGetValue(pawn, out var lastCell) && lastCell == cell)
                    return; // 同图同格：帧频热路径早退。

                var mapChanged = lastMap != map;
                LastMap[pawn] = map;
                LastCell[pawn] = cell;
                if (mapChanged) return; // 跨图传送落地帧：只重置基线（防镜像回传，见类注释）。

                // ① 边界带预加载（判据链镜像 SeamlessBorderPreloader.CheckPawnGoto）。
                TryPreloadAt(pawn, map, cell);

                // ② 踩传送点即席 Bridge 传送。
                TryCrossSeamAt(pawn, map, cell, __instance);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: ProcessMovement postfix error (swallowed): {ex}");
            }
        }

        /// <summary>avatar 换格进边界带（且非接缝带/传送点格 = 撤离口径排除）→ 预加载/唤醒对端。</summary>
        private static void TryPreloadAt(Pawn pawn, Map map, IntVec3 cell)
        {
            var lookup = map.GetComponent<SeamlessBorderLookup>();
            if (lookup == null) return;
            if (!lookup.TryGetPreloadTarget(cell, out int worldTile) || worldTile < 0) return;

            // 接缝带格排除与 CheckPawnGoto 同口径：带内 = 撤离意图。对 avatar 而言意义是
            // "已到缝边，预加载来不及/无意义"——传送由 ② 即席处理，对端未就绪则被 void 挡住无事。
            if (SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x)?.Band.Contains(cell) == true
                || SeamlessEdgeCells.IsSeamEdgeCell(map, cell))
                return;

            if (SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, worldTile, out _)) return; // 已加载。

            if (SeamlessDormancyManager.TryWakeByWorldTile(worldTile, $"PS avatar border band ({pawn.LabelShort} at {cell})"))
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    Log.Message($"[RimExodus] PS compat: woke dormant neighbor worldTile {worldTile} (avatar approaching).");
                return;
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                Log.Message($"[RimExodus] PS compat: avatar {pawn.LabelShort} entered border band at {cell}, queuing preload for worldTile {worldTile}.");
            SeamlessTilePreloader.QueuePreload(map, worldTile, cell);
        }

        /// <summary>
        /// 方向意图门阈值 = **噪声地板**（用户定夺 2026-09 rev4：曾有 0.5 的"意图角"版本实测把
        /// dot=+0.24 的真实斜向跨越拦在缝上三十多格——阈值并非防振荡所需，防振荡由镜像对称
        /// 保证：对侧 offsetDir 恒为本侧反向量，同一恒定方向的点积两侧互为相反数，恒定方向
        /// 数学上不可能双侧过门，任意正值阈值皆然）。现值只排除数值噪声与严格平行（dot≈0），
        /// 只要命令方向带正向跨缝分量就触发；真掉头立即放行同样成立。
        /// </summary>
        private const float MinCrossSeamDot = 0.05f;

        /// <summary>
        /// avatar 当前 WASD 命令向量（世界轴）。moveInput 正是 PS 在 ProcessMovement 里直接加到
        /// physicsPosition 上的向量——按定义就是实际位移方向，无需另行推导。反射漂移/类型变更 →
        /// zero（TryGetCrossSeamDot 对零向量 fail-open 放行，绝不因反射失败卡死过缝）。
        /// </summary>
        private static Vector3 ReadAvatarMoveInput(object avatarInstance)
        {
            try
            {
                return _moveInputField != null && avatarInstance != null
                    ? (Vector3)_moveInputField.GetValue(avatarInstance)
                    : Vector3.zero;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// 方向意图门核心：算命令方向相对邻居链 offsetDir 的点积。NeighborLink 契约 offset = 本图
        /// 中心→对侧中心的连线方向（neighborLocal + offset = myLocal；2026-09 用实测传送对数值
        /// 定案：map0(34,180)↔map1(216,69) 的 offset=(−182,+111) 恰为 C1−C0；rev2 曾按"对侧→本图"
        /// 的误记写成 dot ≤ −阈值——"向上走被拦/向下走反而传"的 diag 数据实锤符号反了，rev3 翻正为
        /// dot ≥ +阈值）。正六边形下中心连线严格垂直于共享边 → offsetDir ≈ 接缝"朝对侧"方向。
        /// 返回 false = 数据不可用（无输入/邻居链缺失/offset 退化）→ 调用方放行（fail-open：最坏 =
        /// 旧行为振荡，不引入"卡死过不了缝"的新失败面）。开销：踩上 spot 的那一格一次邻居链字典
        /// 查询 + 一次点积，仅主控 avatar/驾驶载具触达。
        /// </summary>
        private static bool TryGetCrossSeamDot(Map map, int targetWorldTile, Vector3 moveInput, out float dot)
        {
            dot = 0f;
            var moveDir = new Vector2(moveInput.x, moveInput.z);
            if (moveDir.sqrMagnitude < 0.0001f) return false;
            if (!SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, targetWorldTile, out var info)) return false;
            var offsetDir = new Vector2(info.offset.x, info.offset.z);
            if (offsetDir.sqrMagnitude < 0.0001f) return false;
            dot = Vector2.Dot(moveDir.normalized, offsetDir.normalized);
            return true;
        }

        /// <summary>avatar 踩传送点且对端活跃 → 即席 Bridge Grant + 既有分派链传送（对端未生成不触发）。</summary>
        private static void TryCrossSeamAt(Pawn pawn, Map map, IntVec3 cell, object avatarInstance)
        {
            if (!SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return;

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var things = map.thingGrid.ThingsListAt(cell);
            for (var i = 0; i < things.Count; i++)
            {
                var thing = things[i];
                if (thing.def != enterSpotDef) continue;
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null) continue;

                // 对端未生成/未加载：不即席生成（与 TryRegisterCombatStepTransfer 同口径）。
                if (!comp.hasArrival || !SeamlessTileGraph.TryGetMapByWorldTile(comp.targetWorldTile, out _)) return;

                // 方向意图门（防镜像第二层，2026-09 rev4 定稿）：命令方向与 offsetDir（本图中心
                // →对侧中心）同向的点积 ≥ MinCrossSeamDot（噪声地板）才传送。镜像对称下对侧
                // offsetDir 为本侧相反向量 → 同一恒定方向的点积两侧互为相反数，一侧过门另一侧
                // 必被拦——恒定方向连环弹射在数学上不可能；真掉头立即放行；沿缝平行（dot≈0）
                // 不触发。数据不可用 → 放行（fail-open，见 TryGetCrossSeamDot）。
                if (TryGetCrossSeamDot(map, comp.targetWorldTile, ReadAvatarMoveInput(avatarInstance), out var crossDot)
                    && crossDot < MinCrossSeamDot)
                {
                    if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                        Log.Message($"[RimExodus] PS compat: avatar {pawn.LabelShort} stepped on spot {thing.Position} on map {map.uniqueID} "
                            + $"but movement is not crossing outward (dot={crossDot:F2} < {MinCrossSeamDot:F2}) — not transferring.");
                    return;
                }

                // 玩家意图由"控制 avatar 走到缝边传送点上"本身保证（与撤离链"事件即意图"同哲学）。
                // Grant 不带 NextJob/FinalDest：ContinueBridgeMove 对空目的地天然 no-op——avatar 由
                // 玩家 WASD 驱动，不能下发 Goto job（会与 PS 的物理移动冲突，PS 也会 StopDead 它）。
                var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Bridge);
                grant.BoundSpot = thing.Position;

                if (RimExodusLog.Enabled(RimExodusLogModule.Compat))
                    Log.Message($"[RimExodus] PS compat: avatar {pawn.LabelShort} stepped on enter spot {thing.Position} "
                        + $"on map {map.uniqueID}, dispatching cross-seam transfer.");

                SeamlessMapTransferTrigger.TryTriggerTransfer(pawn, cell, map);

                // 传送成功（pawn 已换图）：把聚焦切到落地图。既有 TryAutoFocusOnArrival 只覆盖
                // "首个殖民者进新地块"（autoFocused 每地块一次 + 仅 MapParent_SeamlessTile，原生
                // 家族图被排除），avatar 第二次进同一地块/进据点图都不切——视角留在旧图 = 玩家
                // "丢失控制"（征召状态还在，但输入作用于别的图）。PS 相机锁 avatar 跟随 pawn 对象，
                // CurrentMap 一切下帧自动跟上，无需手工调相机。CurrentMap setter 同时触发休眠唤醒链。
                var arrivalMap = pawn.Map;
                if (arrivalMap != null && arrivalMap != map)
                {
                    FocusArrivalMapWithZoom(map, arrivalMap, pawn.Position);

                    // 清 PS 的亚格物理位置，防下帧 desync 警告（见类注释）。
                    if (_physicsPositionField != null)
                        _physicsPositionField.SetValue(avatarInstance, null);
                }
                return;
            }
        }

        /// <summary>
        /// 切 CurrentMap 到落地图并保留缩放（avatar 一切传送入口的统一收尾，PS 相机/输入依赖它）：
        /// CurrentMap setter 触发原生 Notify_SwitchedMap 用落地图 rememberedCameraPos 同时恢复
        /// 位置+缩放——PS 每帧只写 rootPos（跟随 avatar/载具）不写 RootSize，不补这一手缩放会
        /// 跳到落地图上次记住的值。位置按接缝 offset 换算（PS 下帧就会重写，只为消灭切换帧的
        /// 一帧跳变）；查不到 offset（非直接邻居，理论不可达）回落 fallbackCell。
        /// </summary>
        private static void FocusArrivalMapWithZoom(Map departureMap, Map arrivalMap, IntVec3 fallbackCell)
        {
            if (Find.CurrentMap == arrivalMap) return; // 已聚焦（幂等：多入口重复调用无害）。
            var camPos = Find.CameraDriver.MapPosition.ToVector3();
            var camSize = Find.CameraDriver.RootSize;
            Current.Game.CurrentMap = arrivalMap;
            var targetPos = SeamlessViewProjection.TryProject(arrivalMap, IntVec3.Zero, departureMap, out var offset)
                ? camPos - offset.ToVector3()
                : new UnityEngine.Vector3(fallbackCell.x, 0f, fallbackCell.z);
            Find.CameraDriver.SetRootPosAndSize(targetPos, camSize);
        }

        /// <summary>
        /// 跨缝传送的 PS 侧统一聚焦钩子（AfterTransfer 调用，PS 未装 = no-op）：
        /// 被传走的 pawn 是 avatar 本人、或 avatar 正乘在其上（VF 载具舱内 / 被扛抬——沿
        /// ParentHolder 链判定）→ 切 CurrentMap 到落地图 + 缩放保留。
        /// PS×VF 驾驶场景的命脉：avatar 在舱内时 avatar.Map（holder 链解析）随车变到新图，
        /// 若 CurrentMap 留在旧图，Avatar.UpdatePhysics 首行 "pawn.Map != Find.CurrentMap"
        /// 会冻结驾驶输入且永不自愈，相机（TryGetSpawnedContainer 需 thing.Map == CurrentMap）
        /// 同步卡死。avatar 本人步行踩点路径已由 TryCrossSeamAt 聚焦，此处 CurrentMap 相等
        /// 幂等早退；跨图右键下令（桥接 grant）传送的 avatar 此前无人聚焦，这里一并覆盖。
        /// </summary>
        public static void FocusIfAvatarAboard(Pawn transferred, Map departureMap, Map arrivalMap)
        {
            if (!_initialized) return;
            try
            {
                if (transferred == null || arrivalMap == null || arrivalMap == departureMap) return;
                var avatar = GetAvatarPawn();
                if (avatar == null) return;

                if (avatar != transferred && !IsAboard(avatar, transferred)) return;

                FocusArrivalMapWithZoom(departureMap, arrivalMap, transferred.Position);

                // 亚格物理位置兜底清理：avatar 步行路径由 TryCrossSeamAt 清；舱内 PS 每帧自清；
                // 桥接下令传送的 avatar 无人清——此处统一（对 avatar 实例，被传的可能是载具）。
                var avatarInstance = _stateAvatarProp?.GetValue(null, null);
                if (avatarInstance != null && _physicsPositionField != null)
                    _physicsPositionField.SetValue(avatarInstance, null);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: FocusIfAvatarAboard error (swallowed): {ex}");
            }
        }

        /// <summary>PS 当前 avatar pawn（State.Avatar.pawn 反射；拿不到 = null，功能降级）。</summary>
        private static Pawn GetAvatarPawn()
        {
            try
            {
                var avatarInstance = _stateAvatarProp?.GetValue(null, null);
                return _pawnField?.GetValue(avatarInstance) as Pawn;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>pawn 是否乘在 carrier 上（沿 ParentHolder 链找 carrier——VF 舱内/被扛抬通用）。</summary>
        private static bool IsAboard(Pawn pawn, Pawn carrier)
        {
            for (var holder = pawn.ParentHolder; holder != null; holder = holder.ParentHolder)
            {
                if (ReferenceEquals(holder, carrier)) return true;
            }
            return false;
        }
    }
}
