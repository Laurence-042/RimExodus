using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 袭击外缘生成（2026-09，用户规划）：步行类袭击不再从"已加载区内部边界"（朝向活跃邻图的
    /// 接缝）凭空出现，而是出现在外侧接缝——对侧未被玩家看见（无图或休眠）的边上。
    ///
    /// 资格三分支（对被袭击图 A 的世界网格一跳邻居逐个判定，纯拓扑、不区分五/六边形数量）：
    /// ① 一跳邻居 B 活跃、且 B 的某条边对侧 C 无图或休眠 → 生成在 B 朝 C 的边上
    ///    （叙事：袭击者从玩家没看到的 C 进入 B，再从 B 向 A 进军）；
    /// ② 一跳邻居 B 无图或休眠 → 生成在 A 朝 B 的边上（叙事：从没被看到的 B 直接进入 A）；
    /// ③ fallback（一跳二跳全活跃）→ 任意 B-C 边可用。
    /// "不活跃" = 无图或 <see cref="SeamlessDormancyManager.IsDormant"/>；**降频算活跃**
    /// （可见即被看见，与 SeamlessTileGraph 邻接口径一致）。
    ///
    /// 两种执行形态（host = 实际生成图）：
    /// - **同图约束**（host == A）：raid 族 / 发狂动物 / 食尸鬼 worker 的 Prefix 设置
    ///   <see cref="Current"/>（窗口 = 本次 TryExecuteWorker，Postfix 清），由
    ///   Patch_CellFinder_TryFindRandomEdgeCellWith 的 4 参 Prefix 消费——候选池过滤为
    ///   "朝向允许 tile 的传送点格"，**过滤池空回落整圈池**（绝不比现状更容易失败，保护
    ///   EdgeWalkInDarkness 等带额外谓词的模式）。validator 链原样复用（含 CanReachColony）。
    /// - **跨图生成**（host == B，仅敌对 raid 族）：Patch_RaidStrategyWorker_MakeLords 的
    ///   Postfix（此时 pawn 已在 A 生成、lord 已建好且自洽）把整组 pawn 迁往 B 的选定格，
    ///   A 图 lord 随清空自毁（Vanished 通知），在 B 上按 parms 旗标重建 LordJob_AssaultColony
    ///   （镜像 vanilla ImmediateAttack；siege/stage/工兵/破墙/邪教徒等非纯 AssaultColony 策略
    ///   结构性出局）。**行军不发放任何 Grant**——完全交给现有推进链（GotoNearestHostile 跨图 →
    ///   StartPath 结构判据桥接 → 过缝 → NotifyPawnTransferred 的 TryAttachAssaultLord 在 A
    ///   收编），与"殖民者跨缝逃跑后袭击者追过去"同一形状（阶段 5 已实测）。
    ///   动物 / 食尸鬼不做跨图（非 NPC 战斗体、近战索敌不跨图，无法行军——已知限制）。
    ///
    /// 传送点朝未生成邻居也预铺（<see cref="SeamlessEnterSpotPlacer"/> 铺全部世界邻居边，
    /// hasArrival=false 不影响取格），故"朝向某 tile 的格"统一按 spot 的 targetWorldTile
    /// 过滤即可，无需几何锚点。枚举一律走世界网格邻居（GetTileNeighbors），**不用地图邻居表**
    /// （只含已登记链接，看不到未生成邻居）。
    ///
    /// 安全纪律：一切入口 try/catch，任何失败 = 原版行为 + Error（绝不断袭击）；ambient 由
    /// Prefix 设 / Postfix 清，Begin 前防御性清陈旧（覆盖异常泄漏窗口）。
    /// </summary>
    internal static class SeamlessRaidOuterSpawn
    {
        /// <summary>总开关（设置 raidOuterSpawnEnabled，默认开）。</summary>
        internal static bool Enabled => RimExodusMod.Settings?.raidOuterSpawnEnabled ?? true;

        /// <summary>
        /// 本次袭击的落点计划（ambient，窗口 = 本次 TryExecuteWorker 执行期）。
        /// CrossHostMap == null → 同图约束（AllowedFacingTiles 生效，CellFinder 侧消费）；
        /// CrossHostMap != null → 跨图生成（MakeLords Postfix 消费 CrossAnchors 迁移整组）。
        /// </summary>
        internal sealed class Plan
        {
            public Map TargetMap;
            public HashSet<int> AllowedFacingTiles;
            public Map CrossHostMap;
            public int CrossFacingTile;
            public List<IntVec3> CrossAnchors;
            /// <summary>建立时的 tick——窗口 = 本次 TryExecuteWorker（同 tick）；消费侧时效守卫用
            /// （Postfix 在原方法异常时不跑，防陈旧计划泄漏到后续无关的边缘格查询）。</summary>
            public int CreatedTick;
        }

        internal static Plan Current;

        /// <summary>读 LordJob_AssaultColony 私有旗标（工兵/破墙策略的行军语义 map-local，不跨图）。</summary>
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> sappersRef;
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> breachersRef;

        // =====================================================================
        // 入口（Patches_RaidOuterSpawn 调用）
        // =====================================================================

        /// <summary>
        /// 袭击 worker 的 TryExecuteWorker Prefix：枚举资格缝并建立计划。
        /// allowCrossMap = false 限同图约束（发狂动物/食尸鬼）；true 允许跨图生成（raid 族）。
        /// 任何异常 = 无计划（原版行为）。
        /// </summary>
        internal static void BeginIncident(IncidentParms parms, bool allowCrossMap)
        {
            End(); // 防御性清陈旧（上一次异常泄漏的窗口）
            if (!Enabled) return;
            try
            {
                if (!(parms?.target is Map mapA) || mapA.Disposed) return;
                if (!SeamlessEdgeCells.HasSeamEdge(mapA)) return; // 无缝语义不在场（异常态）→ 原版
                var tileA = SeamlessTileRegistry.GetMapWorldTile(mapA);
                if (tileA < 0) return; // 口袋/空间层图（GetMapWorldTile 收口口径）

                // 跨图资格的 parms 层守卫（lord 层校验在 MakeLords Postfix 补齐）：
                // 仅敌对袭击跨图——友好援军/被控袭击保持原版落点。
                var crossAllowed = allowCrossMap
                    && parms.faction != null && parms.faction != Faction.OfPlayer
                    && parms.faction.HostileTo(Faction.OfPlayer)
                    && parms.controllerPawn == null
                    && (parms.attackTargets == null || parms.attackTargets.Count == 0);

                // ---- 枚举候选缝（三分支 + fallback）----
                var ring1 = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(tileA, ring1);
                var ring2 = new List<PlanetTile>();
                var candidates = new List<(Map host, int facing, bool cross)>();
                foreach (var n in ring1)
                {
                    var mapB = MapAt(n.tileId);
                    if (!IsActive(mapB))
                    {
                        // ② 一跳邻居无图或休眠 → A 朝它的边（同图）。
                        candidates.Add((mapA, n.tileId, false));
                        continue;
                    }
                    // ① B 活跃：其世界邻居 C 无图或休眠 → B 朝 C 的边（跨图）。
                    ring2.Clear();
                    Find.WorldGrid.GetTileNeighbors(n, ring2);
                    foreach (var u in ring2)
                    {
                        if (u.tileId == tileA) continue; // A 恒活跃（事件目标过滤），结构性不满足
                        if (!IsActive(MapAt(u.tileId))) candidates.Add((mapB, u.tileId, true));
                    }
                }
                var fallback = candidates.Count == 0;
                if (fallback)
                {
                    // ③ 一跳二跳全活跃：任意 B-C 边（用户 fallback 定夺）。朝向 A 的边不入选——
                    // 生成在 B-A 缝 = 出现在玩家眼皮底下的接缝上，恰是要避免的形态。
                    foreach (var n in ring1)
                    {
                        var mapB = MapAt(n.tileId);
                        if (!IsActive(mapB)) continue;
                        ring2.Clear();
                        Find.WorldGrid.GetTileNeighbors(n, ring2);
                        foreach (var u in ring2)
                        {
                            if (u.tileId == tileA) continue;
                            candidates.Add((mapB, u.tileId, true));
                        }
                    }
                }
                if (candidates.Count == 0) return;

                if (RimExodusLog.Enabled(RimExodusLogModule.Combat))
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"Raid outer spawn: target map {mapA.uniqueID} (wt={tileA}), {candidates.Count} candidate seams");
                    if (fallback) sb.Append(" (all-active fallback)");
                    sb.Append(':');
                    foreach (var c in candidates)
                        sb.Append($" [host={c.host.uniqueID} facing={c.facing} cross={c.cross}]");
                    RimExodusLog.Message(RimExodusLogModule.Combat, sb.ToString());
                }

                // ---- 随机打乱后取第一个"能落地"的候选 ----
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = Rand.Range(0, i + 1);
                    (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                }
                var facingScratch = new List<IntVec3>();
                foreach (var cand in candidates)
                {
                    if (cand.cross && !crossAllowed) continue;

                    if (!cand.cross)
                    {
                        // 同图候选：朝向格非空即可（可走性/可达由 CellFinder 侧原版 validator 把关）。
                        SeamlessEdgeCells.PopulateFacingCells(mapA, cand.facing, facingScratch);
                        if (facingScratch.Count == 0) continue;
                        Current = new Plan
                        {
                            TargetMap = mapA,
                            AllowedFacingTiles = new HashSet<int> { cand.facing },
                            CreatedTick = GenTicks.TicksGame,
                        };
                        RimExodusLog.Message(RimExodusLogModule.Combat,
                            $"Raid outer spawn: same-map plan on map {mapA.uniqueID}, facing wt={cand.facing} " +
                            $"({facingScratch.Count} facing cells).");
                        return;
                    }

                    // 跨图候选：host 仍活跃 + 朝向格中有可站且能走到 A 侧缝的锚点（防密封口袋）。
                    if (!TryBuildCrossAnchors(cand.host, cand.facing, tileA, facingScratch)) continue;
                    Current = new Plan
                    {
                        TargetMap = mapA,
                        CrossHostMap = cand.host,
                        CrossFacingTile = cand.facing,
                        CrossAnchors = new List<IntVec3>(facingScratch),
                        CreatedTick = GenTicks.TicksGame,
                    };
                    RimExodusLog.Message(RimExodusLogModule.Combat,
                        $"Raid outer spawn: cross-map plan, host map {cand.host.uniqueID} facing wt={cand.facing}, " +
                        $"{facingScratch.Count} anchors; pawns will march to target via existing pursuit chain.");
                    return;
                }

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Raid outer spawn: no workable candidate seam, vanilla spawn kept.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] RaidOuterSpawn plan failed, vanilla spawn behavior kept: " + ex);
                Current = null;
            }
        }

        /// <summary>worker 的 TryExecuteWorker Postfix：清计划。</summary>
        internal static void End()
        {
            Current = null;
        }

        // =====================================================================
        // 同图约束消费（Patches_CellFinder 4 参 Prefix）
        // =====================================================================

        /// <summary>
        /// 本次调用是否处于"同图约束"窗口且 map 是被袭击图：把允许朝向的传送点格填入 result
        /// （可为空——空池由调用方回落整圈池）。跨图计划/非目标图一律返回 false（不过滤）。
        /// </summary>
        internal static bool TryGetSameMapFacingCells(Map map, List<IntVec3> result)
        {
            var plan = Current;
            if (plan == null || plan.CrossHostMap != null || plan.AllowedFacingTiles == null) return false;
            if (map != plan.TargetMap) return false;
            // 时效守卫：计划生命周期 = 本次 TryExecuteWorker（同 tick）；原方法异常时 Postfix 不跑，
            // 超窗的计划按泄漏丢弃（防陈旧约束泄漏到后续无关的边缘格查询）。
            if (GenTicks.TicksGame - plan.CreatedTick > 120)
            {
                Current = null;
                return false;
            }
            result.Clear();
            foreach (var t in plan.AllowedFacingTiles)
                SeamlessEdgeCells.PopulateFacingCells(map, t, result);
            return true;
        }

        // =====================================================================
        // 跨图生成消费（RaidStrategyWorker.MakeLords Postfix）
        // =====================================================================

        /// <summary>
        /// 把整组袭击 pawn 从 A 迁往宿主图 B 并在 B 上重建袭击 lord。全部守卫通过才动手；
        /// 预放置全有或全无（任一 pawn 无处可放 = 整体放弃，A 上已建好的 lord 与生成位原样保留）。
        /// 返回 false = 不迁移（MakeLords 原生结果保持）。
        /// </summary>
        internal static bool TryRelocateToHost(IncidentParms parms, List<Pawn> pawns)
        {
            var plan = Current;
            if (plan?.CrossHostMap == null) return false;
            try
            {
                // 时效守卫（与 TryGetSameMapFacingCells 同款）：计划生命周期 = 本次 TryExecuteWorker。
                if (GenTicks.TicksGame - plan.CreatedTick > 120)
                {
                    Current = null;
                    return false;
                }
                var hostMap = plan.CrossHostMap;
                if (hostMap.Disposed || SeamlessDormancyManager.IsDormant(hostMap)) return false;
                if (!(parms?.target is Map mapA) || mapA == hostMap) return false;
                // arrival mode 此时已解析（MakeLords 晚于 TryGenerateRaidInfo）——仅步行族跨图。
                if (parms.raidArrivalMode == null || !parms.raidArrivalMode.walkIn) return false;
                if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer)) return false;
                if (parms.controllerPawn != null) return false;
                if (parms.attackTargets != null && parms.attackTargets.Count > 0) return false;
                if (pawns == null || pawns.Count == 0) return false;

                // 所建 lord 必须全部是"非工兵/非破墙的 AssaultColony"（读实际 job——
                // siege/stage/HateChant/mod 策略天然出局，保持原版落点）。
                sappersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("sappers");
                breachersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");
                foreach (var pawn in pawns)
                {
                    if (pawn == null || !pawn.Spawned || pawn.Map != mapA) return false;
                    if (!(pawn.GetLord()?.LordJob is LordJob_AssaultColony assault)) return false;
                    if (sappersRef(assault) || breachersRef(assault)) return false;
                }

                // 预放置（全有或全无）：每 pawn 从打乱后的锚点散布。
                var anchors = plan.CrossAnchors;
                var cells = new List<IntVec3>(pawns.Count);
                foreach (var pawn in pawns)
                {
                    var placed = IntVec3.Invalid;
                    for (int i = anchors.Count - 1; i >= 0; i--)
                    {
                        int j = Rand.Range(0, i + 1);
                        (anchors[i], anchors[j]) = (anchors[j], anchors[i]);
                        var c = CellFinder.RandomClosewalkCellNear(anchors[i], hostMap, 8);
                        if (c.IsValid && c.Standable(hostMap))
                        {
                            placed = c;
                            break;
                        }
                    }
                    if (!placed.IsValid)
                    {
                        RimExodusLog.Message(RimExodusLogModule.Combat,
                            $"Raid outer spawn: relocation aborted, no standable cell on host map {hostMap.uniqueID} for {pawn.LabelShort}.");
                        return false;
                    }
                    cells.Add(placed);
                }

                // 迁移（镜像 SeamlessMapTransfer.TryTransferPawn 的簿记，无 Grant：袭击者是本 tick
                // 全新生成的敌人，无征召/选中/预约/许可状态；mental state 刻意不动——同传送契约）。
                // 宿主图先解除降频（行军期按活跃模拟；下轮 Sweep 无玩家 pawn 会再降频，接缝快速区
                // 与移动补偿兜住行军质量）。
                SeamlessTickThrottle.Unthrottle(hostMap, "raid outer spawn (host of cross-map raid)");
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.Vanished); // A 图 lord 清空自毁
                    pawn.DeSpawn();
                    var rot = Rot4.FromAngleFlat((hostMap.Center - cells[i]).AngleFlat);
                    GenSpawn.Spawn(pawn, cells[i], hostMap, rot);
                    pawn.lord = null;
                }

                // B 上重建袭击 lord（镜像 vanilla RaidStrategyWorker_ImmediateAttack + MakeLords 收尾）。
                var lord = LordMaker.MakeNewLord(parms.faction,
                    new LordJob_AssaultColony(parms.faction, canKidnap: parms.canKidnap,
                        canTimeoutOrFlee: parms.canTimeoutOrFlee, sappers: false,
                        useAvoidGridSmart: false, canSteal: parms.canSteal),
                    hostMap, pawns);
                lord.inSignalLeave = parms.inSignalEnd;
                if (!string.IsNullOrEmpty(parms.questTag)) QuestUtility.AddQuestTag(lord, parms.questTag);

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: relocated {pawns.Count} raiders of {parms.faction.def.defName} from map " +
                    $"{mapA.uniqueID} to host map {hostMap.uniqueID} (facing wt={plan.CrossFacingTile}); " +
                    "march to target handled by existing cross-map pursuit chain.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] RaidOuterSpawn relocation failed, raid stays on target map: " + ex);
                return false;
            }
        }

        // =====================================================================
        // 内部
        // =====================================================================

        /// <summary>取世界 tile 上的 MapParent 图（原生口径，不过滤休眠——勿用 TryGetMapByWorldTile）。</summary>
        private static Map MapAt(int worldTile)
        {
            var map = Find.World.worldObjects.MapParentAt(new PlanetTile(worldTile))?.Map;
            return map != null && !map.Disposed ? map : null;
        }

        /// <summary>活跃 = 有图且未休眠（降频算活跃）。</summary>
        private static bool IsActive(Map map)
        {
            return map != null && !SeamlessDormancyManager.IsDormant(map);
        }

        /// <summary>
        /// 跨图候选的锚点集：hostMap 朝 facingTile 的传送点格中，可站且能走到"朝 A 侧缝"的格
        /// （防把袭击者放进密封口袋——行军必须能到达 host→A 的接缝）。通过 anchors 返回。
        /// </summary>
        private static bool TryBuildCrossAnchors(Map hostMap, int facingTile, int tileA, List<IntVec3> anchors)
        {
            anchors.Clear();
            var facing = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(hostMap, facingTile, facing);
            if (facing.Count == 0) return false;

            // host 朝 A 侧的可站 spot（行军可达性判定的目的地集合）。
            var aSide = new List<IntVec3>();
            SeamlessEdgeCells.PopulateFacingCells(hostMap, tileA, aSide);
            var aSideStandable = new List<IntVec3>();
            foreach (var c in aSide)
            {
                if (c.Standable(hostMap)) aSideStandable.Add(c);
            }
            if (aSideStandable.Count == 0) return false;

            foreach (var anchor in facing)
            {
                if (!anchor.Standable(hostMap)) continue;
                foreach (var dest in aSideStandable)
                {
                    // 口径对齐原版 EdgeWalkIn 谓词（NoPassClosedDoors + Danger.Some）——
                    // 只验证"能走到 A 侧缝"，段内实际寻路由原版 A* 与桥接链完成。
                    if (hostMap.reachability.CanReach(anchor, dest, PathEndMode.Touch,
                            TraverseMode.NoPassClosedDoors, Danger.Some))
                    {
                        anchors.Add(anchor);
                        break;
                    }
                }
            }
            return anchors.Count > 0;
        }
    }
}
