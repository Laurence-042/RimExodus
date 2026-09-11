using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
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
        /// - **跨图生成**（host == B）：敌对 raid 族经 Patch_RaidStrategyWorker_MakeLords 的
        ///   Postfix（此时 pawn 已在 A 生成、lord 已建好且自洽）把整组 pawn 迁往 B 的选定格，
        ///   A 图 lord 随清空自毁（Vanished 通知），在 B 上重建袭击 lord——纯突击族重建
        ///   AssaultColony（镜像 vanilla ImmediateAttack，工兵/破墙旗标透传——掘进语义在宿主图
        ///   无对象，自然退化为走接缝突击），StageThenAttack 在宿主图重选扎营点
        ///   重建 LordJob_StageThenAttack（扎营计时 → 攻击子图），Siege 在宿主图重选炮兵阵地
        ///   重建 LordJob_Siege（迫击炮经既有跨图索敌/弹丸越缝链轰击目标图）——三者均 2026-09
        ///   用户定夺纳入。机械体围攻/PsychicRitualSiege（覆写 MakeLords）与邪教徒仍出局。
        ///   发狂动物/
        ///   食尸鬼/奇美拉不走 MakeLords——迁移在各自 worker 的
        ///   TryExecuteWorker Postfix（pawn 组由 WindowPawns 捕获），按类型重建：发狂动物无 lord
        ///   （ManhunterPermanent mental state 跨图保留）、食尸鬼 = 无超时 AssaultColony、
        ///   奇美拉 = ChimeraAssault（保持原版 stalk 起步与 MTB 等待机制）。**行军不发放任何
        ///   Grant**——完全交给现有推进链（跨图近战桥接 Goto / GotoNearestHostile 跨图 →
        ///   StartPath 结构判据桥接 → 过缝），与"殖民者跨缝逃跑后袭击者追过去"同一形状（阶段 5
        ///   已实测）。
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
        /// 事件级"仅本图生成"判定（设置 raidOuterSpawnLocalIncidents，2026-09 反转默认：全员
        /// 跨图、本名单为逐事件退出通道）。默认 false = 跨图（含 mod 扩展事件）。
        /// </summary>
        internal static bool IsLocalOnlyIncident(string incidentDefName)
        {
            return incidentDefName != null
                && RimExodusMod.Settings?.raidOuterSpawnLocalIncidents != null
                && RimExodusMod.Settings.raidOuterSpawnLocalIncidents.Contains(incidentDefName);
        }

        /// <summary>
        /// 结构性本图的 worker 类型（2026-09，UI 与计划入口共用的硬编码原版清单）：
        /// PsychicRitualSiege——灵能仪式围攻的仪式点/角色分配/效果全部锚定被袭击图
        ///（LordJob_PsychicRitualRepeating 以 spawnCenter 为仪式核心），跨图要么仪式打空、
        /// 要么降级普通突击抹掉整个玩法，保持本图且不可在设置里改。mod 子类同样命中。
        /// </summary>
        internal static bool IsStructurallyLocalWorker(System.Type workerType)
        {
            return workerType != null
                && typeof(IncidentWorker_PsychicRitualSiege).IsAssignableFrom(workerType);
        }

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

        /// <summary>
        /// 跨图计划窗口内经 PawnGenerator 生成的 pawn（发狂动物/食尸鬼/奇美拉三个**不走
        /// RaidStrategyWorker.MakeLords** 的 worker 由此捕获生成组——它们的 worker 直接
        /// GenSpawn.Spawn + LordMaker.MakeNewLord，没有携带 pawn 列表的公共执行点可挂）。
        /// Begin 前/End 时清空；raid 族不消费（MakeLords Postfix 自带 pawn 列表）。
        /// </summary>
        internal static readonly List<Pawn> WindowPawns = new List<Pawn>();

        /// <summary>PawnGenerator.GeneratePawn Postfix 的捕获入口（仅跨图计划窗口内生效）。</summary>
        internal static void NotifyWindowPawnGenerated(Pawn pawn)
        {
            if (Current?.CrossHostMap != null && pawn != null) WindowPawns.Add(pawn);
        }

        /// <summary>读 LordJob_AssaultColony 私有旗标（工兵/破墙策略的行军语义 map-local，不跨图）。</summary>
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> sappersRef;
        private static AccessTools.FieldRef<LordJob_AssaultColony, bool> breachersRef;

        // =====================================================================
        // 入口（Patches_RaidOuterSpawn 调用）
        // =====================================================================

        /// <summary>
        /// 袭击 worker 的 TryExecuteWorker Prefix：枚举资格缝并建立计划。
        /// allowCrossMap = false 限同图约束（发狂动物/食尸鬼）；true 允许跨图生成（raid 族）。
        /// incidentDefName = worker.def.defName（事件级"仅本图生成"判据）；workerType =
        /// worker 实际类型（结构性本图判据—— PsychicRitualSiege 等建了计划也无消费者，
        /// 直接短路省掉误导性的"cross-map plan"日志）。
        /// 任何异常 = 无计划（原版行为）。
        /// </summary>
        internal static void BeginIncident(IncidentParms parms, bool allowCrossMap, string incidentDefName = null,
            System.Type workerType = null)
        {
            End(); // 防御性清陈旧（上一次异常泄漏的窗口）
            if (!Enabled) return;
            if (IsLocalOnlyIncident(incidentDefName)) return;
            if (IsStructurallyLocalWorker(workerType)) return;
            try
            {
                if (!(parms?.target is Map mapA) || mapA.Disposed) return;
                if (!SeamlessEdgeCells.HasSeamEdge(mapA)) return; // 无缝语义不在场（异常态）→ 原版
                var tileA = SeamlessTileRegistry.GetMapWorldTile(mapA);
                if (tileA < 0) return; // 口袋/空间层图（GetMapWorldTile 收口口径）

                // 跨图资格的 parms 层守卫（仅查调用前已稳定的字段）：faction 故意不查——raid 族
                // worker 在 TryExecuteWorker **内部**才解析 faction（TryGenerateRaidInfo →
                // TryResolveRaidFaction），Prefix 时点恒为 null，查了等于全员判否（2026-09 实测
                // "18 候选全部不可用"的根因）。敌对性由 TryRelocateToHost 在 MakeLords 时点复验，
                // 友好援军在那里被安全拦下（计划被弃 = 原版落点）。
                var crossAllowed = allowCrossMap
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

        /// <summary>worker 的 TryExecuteWorker Postfix：清计划与窗口捕获。</summary>
        internal static void End()
        {
            Current = null;
            WindowPawns.Clear();
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
        /// raid 族跨图迁移（MakeLords Postfix 入口）：守卫（walkIn/敌对/lord 纯 AssaultColony）+
        /// 共享迁移核心 + 在宿主图重建 AssaultColony lord。全部守卫通过才动手；半途失败 = 原版。
        /// 返回 false = 不迁移（MakeLords 原生结果保持）。
        /// </summary>
        internal static bool TryRelocateToHost(IncidentParms parms, List<Pawn> pawns)
        {
            var plan = Current;
            if (plan?.CrossHostMap == null) return false;
            // 静默守卫的可诊断化（Combat 模块门控）：拒绝迁移的原因必须可见——"按设计排除"
            //（空投/围攻/工兵等）与"坏了"在日志上不可区分的话，回归排查会绕远路。
            bool Decline(string reason)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: cross plan declined at MakeLords — {reason}.");
                return false;
            }
            try
            {
                // arrival mode 此时已解析（MakeLords 晚于 TryGenerateRaidInfo）——仅步行族跨图。
                if (parms.raidArrivalMode == null || !parms.raidArrivalMode.walkIn)
                    return Decline($"arrival mode {parms.raidArrivalMode?.defName ?? "null"} is not walk-in");
                if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer))
                    return Decline($"faction {parms.faction?.def?.defName ?? "null"} is not hostile to player");
                if (parms.controllerPawn != null) return Decline("controlled raid (controllerPawn)");
                if (parms.attackTargets != null && parms.attackTargets.Count > 0) return Decline("targeted raid (attackTargets)");
                if (pawns == null || pawns.Count == 0) return Decline("empty pawn group");

                // 允许的 lord 形状（2026-09 三轮扩展，用户定夺）：
                // ① LordJob_AssaultColony（含工兵/破墙旗标——掘进语义是"朝本图殖民地定向挖墙"，
                //   宿主图上没有殖民地对象，跨图后自然退化为"走接缝过来再突击"；旗标仍原样
                //   透传重建，若途中真被挡住语义照旧）；② LordJob_StageThenAttack（扎营计时 →
                //   AssaultColony 子图——攻击期推进链已跨图化，扎营期 Defend duty 地图无关，
                ///  扎营点在宿主图重选）；③ LordJob_Siege——迫击炮的跨图索敌/射击线/弹丸越缝
                //   均已有（flyOverhead 免 LOS、炮塔走 BestAttackTarget 跨图 Postfix），扎营建在
                //   宿主图、炮弹跨缝轰击目标图。仍出局：机械体围攻/PsychicRitualSiege（覆写
                //   MakeLords，基类 Postfix 结构性不命中）与邪教徒（分批布点+吟唱）。
                var isStageThenAttack = false;
                var isSiege = false;
                var isShamblerAssault = false;
                var wasSappers = false;
                var wasBreachers = false;
                sappersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("sappers");
                breachersRef ??= AccessTools.FieldRefAccess<LordJob_AssaultColony, bool>("breachers");
                foreach (var pawn in pawns)
                {
                    if (pawn == null || !pawn.Spawned || !(parms.target is Map) || pawn.Map != (Map)parms.target)
                        return Decline($"pawn {pawn?.LabelShort ?? "null"} is not on the target map");
                    var lordJob = pawn.GetLord()?.LordJob;
                    if (lordJob is LordJob_AssaultColony assault)
                    {
                        wasSappers |= sappersRef(assault);
                        wasBreachers |= breachersRef(assault);
                    }
                    else if (lordJob is LordJob_StageThenAttack)
                    {
                        isStageThenAttack = true;
                    }
                    else if (lordJob is LordJob_Siege)
                    {
                        isSiege = true;
                    }
                    else if (lordJob is LordJob_ShamblerAssault)
                    {
                        // 蹒跚怪突击（2026-09）：lord 不原样重建——LordToil_ShamblerAssault 扫不到
                        // 本图非实体目标会 RemoveLord 自毁，宿主图上无人即散伙。重建为无旗标
                        // AssaultColony（OfEntities；蹒跚怪战至消亡，kidnap/steal/timeout 全关），
                        // 行军走已跨图化的突击链；Shambler hediff 寿命随 pawn 跨图保留。
                        isShamblerAssault = true;
                    }
                    else if (lordJob != null)
                    {
                        // 通用降级（2026-09 用户定夺：白名单反转——默认全员跨图）：mod 自定义策略等
                        // 未知 lord 形态不再拒绝，销毁原 lord、按 parms 旗标重建 AssaultColony 迁移
                        //（mod 策略的专属行为会丢失；玩家可在设置的"仅本图生成"名单里按事件退出）。
                        // 覆写 MakeLords 的策略（机械体围攻/PsychicRitualSiege）本 Postfix 不命中，
                        // 结构性保持本图，与本分支无关。
                        RimExodusLog.Message(RimExodusLogModule.Combat,
                            $"Raid outer spawn: unrecognized lord {lordJob.GetType().Name} (strategy "
                            + $"{parms.raidStrategy?.defName ?? "?"}) downgraded to AssaultColony for cross-map relocation.");
                    }
                    else
                    {
                        return Decline($"pawn {pawn.LabelShort} has no lord");
                    }
                }

                if (!TryTransferGroupToHost(parms, pawns, out var hostMap)) return false;

                // B 上重建袭击 lord（镜像 vanilla MakeLords 收尾）。
                LordJob rebuiltJob;
                if (isStageThenAttack)
                {
                    // 扎营点镜像原 worker 选点（FindSiegePositionFrom 的宿主图版；失败回落 pawn
                    // 站位）。ctor 旗标镜像 MakeLordJob 默认（原 worker 也不读 parms 旗标）；
                    // raidSeed 只影响延迟抽取与消息去重，新种子即可。
                    var stageLoc = RCellFinder.FindSiegePositionFrom(pawns[0].PositionHeld, hostMap,
                        allowRoofed: true, errorOnFail: false);
                    if (!stageLoc.IsValid) stageLoc = pawns[0].PositionHeld;
                    rebuiltJob = new LordJob_StageThenAttack(parms.faction, stageLoc, Rand.Range(0, int.MaxValue));
                }
                else if (isSiege)
                {
                    // 镜像 RaidStrategyWorker_Siege.MakeLordJob：宿主图重选扎营点，
                    // 蓝图点数 = points × 0.2~0.3（下限 60）。建成的迫击炮由既有跨图索敌/射击线/
                    // 弹丸越缝链跨缝轰击目标图。
                    var siegeSpot = RCellFinder.FindSiegePositionFrom(pawns[0].PositionHeld, hostMap);
                    var blueprintPoints = Mathf.Max(parms.points * Rand.Range(0.2f, 0.3f), 60f);
                    rebuiltJob = new LordJob_Siege(parms.faction, siegeSpot, blueprintPoints);
                }
                else if (isShamblerAssault)
                {
                    rebuiltJob = new LordJob_AssaultColony(parms.faction, canKidnap: false,
                        canTimeoutOrFlee: false, sappers: false, useAvoidGridSmart: false, canSteal: false);
                }
                else
                {
                    rebuiltJob = new LordJob_AssaultColony(parms.faction, canKidnap: parms.canKidnap,
                        canTimeoutOrFlee: parms.canTimeoutOrFlee, sappers: wasSappers,
                        useAvoidGridSmart: false, canSteal: parms.canSteal, breachers: wasBreachers);
                }
                var lord = LordMaker.MakeNewLord(parms.faction, rebuiltJob, hostMap, pawns);
                lord.inSignalLeave = parms.inSignalEnd;
                if (!string.IsNullOrEmpty(parms.questTag)) QuestUtility.AddQuestTag(lord, parms.questTag);

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: relocated {pawns.Count} raiders of {parms.faction.def.defName} from map " +
                    $"{((Map)parms.target).uniqueID} to host map {hostMap.uniqueID} (facing wt={plan.CrossFacingTile}); " +
                    "march to target handled by existing cross-map pursuit chain.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] RaidOuterSpawn relocation failed, raid stays on target map: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 宿主图 lord 重建工厂：入参 = 首个已迁移 pawn（落点即其在宿主图的站位——EntitySwarm 族
        /// 的 lord job 需要宿主图上的 entry/dest 格，由工厂自行从落点推导）。
        /// </summary>
        internal delegate LordJob LordJobFactory(Pawn firstPawn);

        /// <summary>
        /// 非 raid 族跨图迁移（发狂动物/食尸鬼/奇美拉 worker 的 TryExecuteWorker Postfix 入口）：
        /// 取窗口捕获组（仍在被袭击图上者）→ 共享迁移核心 → 按需在宿主图重建 lord。
        /// 发狂动物传 null 工厂（无 lord，ManhunterPermanent mental state 跨图保留，由
        /// Patch_JobGiver_Manhunter_CrossMapMelee 的桥接推进接管行军）。
        /// onLordCreated：lord 建成后的调校钩子（保留的扩展点；奇美拉曾用它直入攻击模式，
        /// 2026-09 按用户反馈回退——stalk 等待机制保持原版，MTB/被打切换均 lord 级、跨图可用）。
        /// </summary>
        internal static bool TryRelocateWindowGroup(IncidentParms parms, LordJobFactory lordFactory,
            System.Action<Lord> onLordCreated = null)
        {
            var plan = Current;
            if (plan?.CrossHostMap == null) return false;
            try
            {
                if (!(parms?.target is Map mapA)) return false;
                if (WindowPawns.Count == 0) return false;
                var pawns = new List<Pawn>();
                for (int i = 0; i < WindowPawns.Count; i++)
                {
                    var p = WindowPawns[i];
                    if (p != null && p.Spawned && !p.Destroyed && p.Map == mapA) pawns.Add(p);
                }
                if (pawns.Count == 0) return false;

                if (!TryTransferGroupToHost(parms, pawns, out var hostMap)) return false;

                if (lordFactory != null)
                {
                    // 镜像各 worker 的原生 lord：食尸鬼 = 无超时 AssaultColony、奇美拉 = ChimeraAssault
                    //（stalk 起步与 MTB 等待保持原版）、EntitySwarm 族 = 反射调 worker 自己的
                    // GenerateLordJob（宿主图 entry/dest 重选）。
                    var lord = LordMaker.MakeNewLord(pawns[0].Faction, lordFactory(pawns[0]), hostMap, pawns);
                    onLordCreated?.Invoke(lord);
                }

                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: relocated {pawns.Count} spawned pawns ({pawns[0].KindLabel}) from map " +
                    $"{mapA.uniqueID} to host map {hostMap.uniqueID} (facing wt={plan.CrossFacingTile}); " +
                    "march to target handled by cross-map melee/pursuit chain.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[RimExodus] RaidOuterSpawn window-group relocation failed, group stays on target map: " + ex);
                return false;
            }
            finally
            {
                WindowPawns.Clear(); // 即取即清：成功失败都不留到下一窗口（End 亦兜底清）
            }
        }

        /// <summary>
        /// 迁移核心（两类入口共享）：时效守卫 + host 仍活跃 + 预放置（全有或全无：任一 pawn 无处
        /// 可放 = 整体放弃，A 上已生成的单位与 lord 原样保留）+ 宿主图解除降频 + 整组 DeSpawn/Spawn。
        /// 迁移镜像 SeamlessMapTransfer.TryTransferPawn 的簿记，无 Grant：本 tick 全新生成的敌人，
        /// 无征召/选中/预约/许可状态；mental state 刻意不动——同传送契约（发狂动物由此保住
        /// ManhunterPermanent）。旧图 lord 由 Notify_PawnLost(Vanished) 清空自毁。
        /// </summary>
        private static bool TryTransferGroupToHost(IncidentParms parms, List<Pawn> pawns, out Map hostMap)
        {
            hostMap = null;
            var plan = Current;
            // 时效守卫（与 TryGetSameMapFacingCells 同款）：计划生命周期 = 本次 TryExecuteWorker。
            if (GenTicks.TicksGame - plan.CreatedTick > 120)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Raid outer spawn: cross plan expired at relocation (plan tick leak — vanilla kept).");
                Current = null;
                return false;
            }
            hostMap = plan.CrossHostMap;
            if (hostMap.Disposed || SeamlessDormancyManager.IsDormant(hostMap))
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    $"Raid outer spawn: host map {hostMap.uniqueID} no longer active at relocation (disposed={hostMap.Disposed}).");
                return false;
            }
            if (!(parms?.target is Map mapA) || mapA == hostMap)
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Raid outer spawn: relocation skipped — parms.target is not a valid target map.");
                return false;
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

            // 宿主图先解除降频（行军期按活跃模拟；下轮 Sweep 无玩家 pawn 会再降频，接缝快速区
            // 与移动补偿兜住行军质量）。
            SeamlessTickThrottle.Unthrottle(hostMap, "raid outer spawn (host of cross-map raid)");
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.Vanished); // 旧图 lord 清空自毁
                pawn.DeSpawn();
                var rot = Rot4.FromAngleFlat((hostMap.Center - cells[i]).AngleFlat);
                GenSpawn.Spawn(pawn, cells[i], hostMap, rot);
                pawn.lord = null;
            }
            return true;
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
