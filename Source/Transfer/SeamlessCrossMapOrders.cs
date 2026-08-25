using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨地图移动指令的桥接引擎（2026-08 架构重构后 = 纯执行层）：
    /// 下发入口 = 公共函数层（Patches_CrossMapCommon 的 PawnGotoAction 桥接 / StartPath 跨图
    /// 包装）与菜单可达性探测（CanBridgeTo）。选项产出全部原生（点击重放，Patches_ClickReplay），
    /// 本类不再参与菜单生成。
    /// </summary>
    public static class SeamlessCrossMapOrders
    {
        /// <summary>
        /// 跨图移动下发：单跳联合最优选点（<see cref="TryFindBestBridgeSpot"/>）+ Bridge 许可
        /// （绑定 exitSpot + 携带最终目的地）+ TransitTag Goto（踩点凭许可传送，传送后由
        /// 许可携带的最终目的地/NextJob 续程）。失败 = 本图无任何可达桥接点，调用方据此禁用选项。
        /// nextJob 非空 = StartPath 跨图包装路径（传送后续跑原 job，玩家点击下令的任意 job 通用）。
        /// </summary>
        internal static bool TryBridgeJob(Pawn pawn, Map targetMap, IntVec3 targetLocalCell, Job nextJob = null)
        {
            if (!TryFindBestBridgeSpot(pawn, targetMap, targetLocalCell, out var exitSpot, out var costDebug))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Cross-map move rejected: no reachable seamless enter spot bridges map {pawn.Map.uniqueID} to map {targetMap.uniqueID}.");
                return false;
            }

            var grant = SeamlessTransferGrants.Create(pawn, SeamlessTransferGrants.GrantKind.Bridge);
            grant.BoundSpot = exitSpot.Position;
            grant.FinalDestMap = targetMap;
            grant.FinalDestCell = targetLocalCell;
            grant.NextJob = nextJob;

            // 已站在 spot 上（联合选点 C1=0 的退化情形，2026-08 实测）：Goto 目标即当前格 →
            // JobDriver 即时完成、不产生任何 pather 步进 → 触发器永不运行 → 许可被 think tree
            // 下一个 job 清掉（日志表现 = Bridge issued 后紧跟 Grant cleared）。直接以当前格触发传送。
            if (pawn.Position == exitSpot.Position)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Bridge immediate: {pawn.LabelShort} already on spot {exitSpot.Position}, transferring now.");
                SeamlessMapTransferTrigger.TryTriggerTransfer(pawn, exitSpot.Position, pawn.Map);
                return true;
            }

            var job = JobMaker.MakeJob(JobDefOf.Goto, exitSpot.Position);
            job.dutyTag = SeamlessTransferGrants.TransitTag;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Bridge issued: {pawn.LabelShort} on map {pawn.Map.uniqueID} "
                    + $"-> spot {exitSpot.Position} ({costDebug}), final dest map {targetMap.uniqueID} cell {targetLocalCell}.");
            return true;
        }

        /// <summary>
        /// 单跳跨图选点（跨图 A*，用户定夺 2026-08 仅单跳、不做多跳地图图）：
        /// 对候选 spot 取 total = C1(本图 pawn→spot) + C2(对图 dest→落点) 的最小者——
        /// 单跳总代价在过缝点处可分解，这等价于两图在 spot↔落点缝零代价边后的全局最优过缝点。
        /// 平手取 C1 小者（早过缝，稳定决胜）。回退链：无有限 total（对端落点不可走或 dest 不可达）
        /// → 最小 C1（保持"走到最近可达点后停下"的降级）；C1 全 ∞ → false（本图无任何可达 spot）。
        /// 代价口径见 <see cref="SeamlessPathCostField"/>；段内执行仍由原版 A* 完成。
        /// </summary>
        private static bool TryFindBestBridgeSpot(Pawn pawn, Map toMap, IntVec3 destCell, out Thing exitSpot, out string costDebug)
        {
            exitSpot = null;
            costDebug = null;
            var fromMap = pawn.Map;
            // 菜单生成与选项执行之间目标图可能被休眠删除策略 Dispose，
            // 代价场要读对图 PathGrid 的 NativeArray，必须挡在已 Dispose 的图外。
            if (toMap == null || toMap.Disposed)
            {
                return false;
            }

            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null)
            {
                return false;
            }

            var toMapWorldTile = SeamlessTileRegistry.GetMapWorldTile(toMap);

            // 候选与 TryFindNearestReachableBridgeSpot 同源：本图上指向 toMap 的已缓存 spot（def 索引 O(1)）。
            var spots = new List<Thing>();
            var arrivals = new List<IntVec3>();
            foreach (var thing in fromMap.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null || !comp.hasArrival || comp.targetWorldTile != toMapWorldTile)
                {
                    continue;
                }
                spots.Add(thing);
                arrivals.Add(comp.cachedArrivalCell);
            }
            if (spots.Count == 0)
            {
                return false;
            }

            var spotCells = new List<IntVec3>(spots.Count);
            foreach (var spot in spots)
            {
                spotCells.Add(spot.Position);
            }

            // 双侧代价场：本图从 pawn 出发到各 spot；对图从 dest 出发到各落点（反向泛洪即"落点→dest"成本）。
            // VF 载具已知近似（2026-08）：代价场按 pawn 权重泛洪，不感知 VehicleDef 的通行差异
            // （悬浮/轮式/涉水）——仅选点启发式，段内执行与可达性均由 VF 口径判定，接受。
            var costs1 = SeamlessPathCostField.FloodCosts(fromMap, pawn.Position, spotCells, pawn);
            var costs2 = SeamlessPathCostField.FloodCosts(toMap, destCell, arrivals, pawn);

            // VF 载具（2026-08-25"无法通过此处"修复）：pawn 权重选出的 spot 对 VehicleDef 可能不可达
            // （带外圈地形/网格状态），对 VF 的 A* 是 "ran out of cells to process" → PatherFailed →
            // 许可被清、循环刷红字。车辆分支改按 total 代价序逐个过 CanReachLocal（VF 口径可达性，
            // region 缓存）验证，首个真可达者当选；全部不可达 → 请求 VF 网格惰性重建（状态自愈）后
            // 本轮如实返回 false（菜单侧同步诚实化，见 VehicleCanGotoPostfix）。pawn 分支保持原逻辑零变化。
            if (SeamlessVehiclesCompat.IsVehicle(pawn))
            {
                return TryPickVehicleBridgeSpot(pawn, fromMap, spots, costs1, costs2, out exitSpot, out costDebug);
            }

            int bestIdx = -1;
            long bestTotal = long.MaxValue;
            long bestC1 = long.MaxValue;
            int fallbackIdx = -1;
            long fallbackC1 = long.MaxValue;
            for (int i = 0; i < spots.Count; i++)
            {
                long c1 = costs1[i];
                if (c1 == int.MaxValue)
                {
                    continue;
                }
                if (c1 < fallbackC1)
                {
                    fallbackC1 = c1;
                    fallbackIdx = i;
                }
                long c2 = costs2[i];
                if (c2 == int.MaxValue)
                {
                    continue;
                }
                long total = c1 + c2;
                if (total < bestTotal || (total == bestTotal && c1 < bestC1))
                {
                    bestTotal = total;
                    bestC1 = c1;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                exitSpot = spots[bestIdx];
                costDebug = $"C1={costs1[bestIdx]} C2={costs2[bestIdx]} total={bestTotal} of {spots.Count} spots";
                return true;
            }
            if (fallbackIdx >= 0)
            {
                exitSpot = spots[fallbackIdx];
                costDebug = $"fallback C1-only={fallbackC1} of {spots.Count} spots, dest unreachable from all arrivals";
                return true;
            }
            return false;
        }

        /// <summary>
        /// 载具版桥接选点：按"单跳总代价"升序（平手取 C1 小者）逐个过 VF 口径可达性
        /// （<see cref="SeamlessVehiclesCompat.CanReachLocal"/>——对 VehicleDef 的 region 图判真），
        /// 首个真可达者当选；无有限 total 的按 C1 序兜底（"走到最近可达点停下"降级）。
        /// 全部不可达 = 本图该 VehicleDef 无路可到接缝（真实地形/网格状态），如实返回 false
        /// （菜单侧 VehicleCanGotoPostfix 同口径实测，不会出现"放行后空转"）。
        /// </summary>
        private static bool TryPickVehicleBridgeSpot(Pawn pawn, Map fromMap, List<Thing> spots,
            int[] costs1, int[] costs2, out Thing exitSpot, out string costDebug)
        {
            exitSpot = null;
            costDebug = null;

            var ordered = new List<int>();
            var fallback = new List<int>();
            for (var i = 0; i < spots.Count; i++)
            {
                if (costs1[i] == int.MaxValue) continue;
                if (costs2[i] != int.MaxValue) ordered.Add(i);
                fallback.Add(i);
            }
            ordered.Sort((a, b) =>
            {
                var ta = costs1[a] + costs2[a];
                var tb = costs1[b] + costs2[b];
                return ta != tb ? ta.CompareTo(tb) : costs1[a].CompareTo(costs1[b]);
            });
            fallback.Sort((a, b) => costs1[a].CompareTo(costs1[b]));

            foreach (var idx in ordered)
            {
                if (!SeamlessVehiclesCompat.CanReachLocal(pawn, fromMap, pawn.Position, spots[idx].Position)) continue;
                exitSpot = spots[idx];
                costDebug = $"VF-reached C1={costs1[idx]} C2={costs2[idx]} total={costs1[idx] + costs2[idx]} (rank {ordered.IndexOf(idx) + 1}/{ordered.Count})";
                return true;
            }
            foreach (var idx in fallback)
            {
                if (!SeamlessVehiclesCompat.CanReachLocal(pawn, fromMap, pawn.Position, spots[idx].Position)) continue;
                exitSpot = spots[idx];
                costDebug = $"VF-reached fallback C1-only={costs1[idx]}";
                return true;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Cross-map move: no VF-reachable bridge spot for {pawn.LabelShort} on map {fromMap.uniqueID} "
                    + $"({ordered.Count + fallback.Count} candidates) — seam not traversable for this vehicle.");
            return false;
        }

        /// <summary>
        /// 检查 pawn 是否能从本图桥接到 toMap（是否本图存在 pawn 可到达的、对端指向 toMap 的传送点）。
        /// 仅供前端菜单判断"跨图移动是否可行"用——不 Record pending、不返回 spot、不触发桥接。
        /// 与 <see cref="TryFindNearestReachableBridgeSpot"/> 共用同一可达性判定逻辑。
        /// </summary>
        public static bool CanBridgeTo(Pawn pawn, Map toMap)
        {
            return TryFindNearestReachableBridgeSpot(pawn, toMap, out _);
        }

        /// <summary>
        /// 在 fromMap 上找到所有能桥接到 toMap 的传送点，按到 pawn 的距离排序，
        /// 返回第一个 pawn 能到达的。满铺接缝后候选很多，最近的通常可达即返回。
        /// 候选判定：传送点的 <see cref="CompSeamlessTileEnterSpot.hasArrival"/> 且
        /// <see cref="CompSeamlessTileEnterSpot.targetWorldTile"/> 等于 toMap 的 worldTile（O(1) 读缓存/字段）。
        /// 仅供 <see cref="CanBridgeTo"/> 菜单探测（无目标格的轻量最近口径，菜单热路径不跑代价场）；
        /// 实际跨图指令的选点走 <see cref="TryFindBestBridgeSpot"/> 联合最优。
        /// </summary>
        private static bool TryFindNearestReachableBridgeSpot(Pawn pawn, Map toMap, out Thing exitSpot)
        {
            exitSpot = null;
            var fromMap = pawn.Map;
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null)
            {
                return false;
            }

            var toMapWorldTile = SeamlessTileRegistry.GetMapWorldTile(toMap);

            // 用 def 索引查询（O(1)），避免全量遍历 AllThings。
            var candidates = new List<(Thing spot, int distSq)>();
            foreach (var thing in fromMap.listerThings.ThingsOfDef(enterSpotDef))
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                // 对端已缓存可用坐标 + targetWorldTile 指向 toMap。
                if (comp == null || !comp.hasArrival || comp.targetWorldTile != toMapWorldTile)
                {
                    continue;
                }
                candidates.Add((thing, pawn.Position.DistanceToSquared(thing.Position)));
            }

            // 按距离升序排序，依次尝试可达性。
            candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            foreach (var (spot, _) in candidates)
            {
                // VF 载具（2026-08）：可达性走 VF 口径（CanReachVehicle，按该 VehicleDef 的
                // 悬浮/轮式/涉水网格判定）——原版 CanReach + TraverseParms.For(pawn) 对载具不成立。
                if (SeamlessVehiclesCompat.CanReachLocal(pawn, fromMap, pawn.Position, spot.Position))
                {
                    exitSpot = spot;
                    return true;
                }
            }
            return false;
        }
    }
}
