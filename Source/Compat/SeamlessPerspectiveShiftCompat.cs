using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    /// 防镜像回传：传送落点（cachedArrivalCell）本身就是对端传送圈上的 spot 格，落地帧的"换格"
    /// 若不豁免会立刻在对端登记新 Grant 传回去。豁免口径 = 换格同时换图（跨图传送的 DeSpawn/Spawn
    /// 必然伴随 Map 变化）→ 只重置基线不处理。连续穿多张图（每图踩下一 spot）不受影响。
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

        /// <summary>每 pawn 上次处理的格 + 所在图（只对换格帧做正事；跨图换格只重置基线）。</summary>
        private static readonly Dictionary<Pawn, IntVec3> LastCell = new Dictionary<Pawn, IntVec3>();
        private static readonly Dictionary<Pawn, Map> LastMap = new Dictionary<Pawn, Map>();

        /// <summary>
        /// 检测 PerspectiveShift 已加载并手动绑定 Avatar.ProcessMovement Postfix。
        /// 由 RimExodusMod 构造器调用（时点 = PatchAll 之后、绑定报告之前，计数含本 patch）。
        /// </summary>
        public static void Register(Harmony harmony)
        {
            try
            {
                var psLoaded = false;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    var id = mod.PackageIdPlayerFacing;
                    if (id != null && id.ToLower() == "ferny.perspectiveshift") { psLoaded = true; break; }
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
                var postfix = AccessTools.Method(typeof(SeamlessPerspectiveShiftCompat), nameof(ProcessMovementPostfix));
                if (processMovement == null || _pawnField == null || postfix == null)
                {
                    Log.Warning($"[RimExodus] PS compat: signature drift (ProcessMovement={processMovement}, pawn={_pawnField}, postfix={postfix}) — compat disabled.");
                    return;
                }

                harmony.Patch(processMovement, postfix: new HarmonyMethod(postfix));
                _initialized = true;
                Log.Message("[RimExodus] PS compat: bound Avatar.ProcessMovement postfix (PerspectiveShift WASD movement joins seamless world).");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] PS compat: binding failed (mod continues, PerspectiveShift stays unadapted): {ex}");
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
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] PS compat: woke dormant neighbor worldTile {worldTile} (avatar approaching).");
                return;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] PS compat: avatar {pawn.LabelShort} entered border band at {cell}, queuing preload for worldTile {worldTile}.");
            SeamlessTilePreloader.QueuePreload(map, worldTile);
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

                // 玩家意图由"控制 avatar 走到缝边传送点上"本身保证（与撤离链"事件即意图"同哲学）。
                // Grant 不带 NextJob/FinalDest：ContinueBridgeMove 对空目的地天然 no-op——avatar 由
                // 玩家 WASD 驱动，不能下发 Goto job（会与 PS 的物理移动冲突，PS 也会 StopDead 它）。
                var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Bridge);
                grant.BoundSpot = thing.Position;

                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] PS compat: avatar {pawn.LabelShort} stepped on enter spot {thing.Position} "
                        + $"on map {map.uniqueID}, dispatching cross-seam transfer.");

                SeamlessMapTransferTrigger.TryTriggerTransfer(pawn, cell, map);

                // 传送成功（pawn 已换图）：把聚焦切到落地图。既有 TryAutoFocusOnArrival 只覆盖
                // "首个殖民者进新地块"（autoFocused 每地块一次 + 仅 MapParent_SeamlessTile，原生
                // 家族图被排除），avatar 第二次进同一地块/进据点图都不切——视角留在旧图 = 玩家
                // "丢失控制"（征召状态还在，但输入作用于别的图）。PS 相机锁 avatar 跟随 pawn 对象，
                // CurrentMap 一切下帧自动跟上，无需手工调相机。CurrentMap setter 同时触发休眠唤醒链。
                var arrivalMap = pawn.Map;
                if (arrivalMap != null && arrivalMap != map && Find.CurrentMap != arrivalMap)
                {
                    // 缩放必须跨切图保留（TryAutoFocusOnArrival 同款教训）：CurrentMap setter 触发原生
                    // Notify_SwitchedMap 用落地图 rememberedCameraPos 同时恢复位置+缩放——PS 每帧只写
                    // rootPos（跟随 avatar）不写 RootSize，不补这一手缩放会跳到落地图上次记住的值。
                    // 位置按接缝 offset 换算（PS 下帧就会重写，只为消灭切换帧的一帧跳变）；查不到
                    // offset（非直接邻居，理论不可达）回落 avatar 落点格。
                    var camPos = Find.CameraDriver.MapPosition.ToVector3();
                    var camSize = Find.CameraDriver.RootSize;
                    Current.Game.CurrentMap = arrivalMap;
                    var offset = SeamlessCameraFocus.FindNeighborOffset(map, arrivalMap);
                    var targetPos = offset.HasValue
                        ? camPos - offset.Value.ToVector3()
                        : new UnityEngine.Vector3(pawn.Position.x, 0f, pawn.Position.z);
                    Find.CameraDriver.SetRootPosAndSize(targetPos, camSize);
                    if (RimExodusMod.Settings?.verboseLogging ?? false)
                        Log.Message($"[RimExodus] PS compat: focused arrival map {arrivalMap.uniqueID} for avatar {pawn.LabelShort}.");
                }

                // 清 PS 的亚格物理位置，防下帧 desync 警告（见类注释）。
                if (arrivalMap != map && _physicsPositionField != null)
                    _physicsPositionField.SetValue(avatarInstance, null);
                return;
            }
        }
    }
}
