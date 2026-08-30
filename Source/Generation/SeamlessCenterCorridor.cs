using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 中心走廊（2026-08）：邻接生成图 B 在 <see cref="MapGenerator.Caves"/> 浮点网格上挖
    /// "B 中心 → 各已生成邻居方向接缝端点"的走廊，让 RocksFromGrid(200) 在走廊格上不 spawn 岩石——
    /// 原版洞窟（MapGenCavesUtility → GenStep_RocksFromGrid 的 caves&lt;=0 守卫）的同一条无后效
    /// 通道，零 building Destroy、零屋顶支撑重算。
    ///
    /// **端点算法（2026-08-30 用户定稿，v2——首版"B 侧排序探测 + 盲挖回落"被实测否决：探测窗口
    /// 只覆盖源边中点 ±15 格，洞口偏一点的中心连通洞穴被误判（回落盲挖堵死），死胡同洞穴反而
    /// 被盲挖命中）**：对 B 的每个已生成邻居 N——
    /// ① 取 N 侧接缝带（三圈）中属于"朝 B 的边"（格中心到边线段距离 ≤ <see cref="EdgeMembershipDist"/>，
    /// Fog 揭雾同口径）且无自然岩 edifice 的格；
    /// ② 按 region 分集合（region = 可达性等价类，天然现成；无 region 的深水/岩格自动出局）——
    /// 边上无岩 = 1 集合，3 条洞穴伸到边上 = 3 集合；
    /// ③ 每集合随机取一个代表格，代表须可达目标（触发图 A 的目标 = goto 触发位置
    /// <see cref="SeamlessTileManager.NeighborGenerationSourceTriggerCell"/>，缺失回落 A 中心——
    /// pawn 就在那里，比 A 中心更贴场景；其余邻居 C 的目标 = C 中心），从通过者中随机取一；
    /// ④ 无通过者 → 本次不为该 N 挖洞（A 侧真死胡同时挖了也白挖——首版盲挖回落正是
    /// "死胡同洞穴反而生成通道"症状的来源）；
    /// ⑤ B 侧端点 = 代表格镜像（rN + offset，契约 N.local + offset = B.local）——镜像格在 N 侧
    /// 保证无岩，392 照抄区不会把 N 的岩体同步回 B 堵走廊口。
    /// 多邻居 = 星形走廊网，全部汇于 B 中心。
    ///
    /// **只挖 B 侧**（单向原则）：不修改任何已生成邻居。
    ///
    /// **深山洞室（Hive 宿主位）**：巢数 = round(全图 caves 格数/1000)——走廊格把计数推过阈值时
    /// 游戏"欠"一个 Hive，但窄隧道放不下守卫点（CompSpawnerPawn.CreateNewLord 5 格内找不到
    /// 可站可达格 → "Found no place for pawns to defend"）。走廊难超 1k 格、欠账最多 1 巢，
    /// 全部走廊的深山采样点中最深处挖一个直径 ~8 洞厅闭环。
    /// </summary>
    public static class SeamlessCenterCorridor
    {
        /// <summary>
        /// 走廊足迹格（运行时，不序列化——只在生成期消费）：392 混合的免疫保护集，
        /// 与道路保护（<see cref="SeamlessRoadPaths"/> → BuildRoadGuard）同机制。
        /// 不免疫则 N 侧 void 外条带快照（清 void 前的原生连绵山体）经照抄区把岩体
        /// spawn 回 B 的接缝带上，压掉走廊末端（用户实测指出，2026-08-30）。
        /// </summary>
        public class CorridorCellsComponent : MapComponent
        {
            public readonly HashSet<IntVec3> cells = new();

            public CorridorCellsComponent(Map map) : base(map) { }
        }

        private const float StepLength = 0.5f;
        private const float BaseWidth = 3f;
        private const float WidthMin = 2f;
        private const float WidthMax = 5f;
        private const float WidthNoiseAmplitude = 1.2f;
        private const float MeanderAmplitude = 4.5f;
        private const int BranchAfterCells = 10;
        private const float BranchChance = 0.3f;
        private const int BranchMinLength = 5;
        private const int BranchMaxLength = 10;
        private const float BranchWidthPenalty = 1f;

        // 接缝带"属于该边"的归属阈值（与 Patch_GenStepFog.CollectStandableRootsOnEdge 同口径，
        // 越过即归邻边——端点绝不能落到不朝 N 的别的边上）。
        private const float EdgeMembershipDist = 6f;

        // 深山洞室：直径 ~8（三盘噪声轮廓），Hive 守卫点半径 5 内必有充足地板。
        private const float ChamberWidth = 8f;
        private const float ChamberWidthNoise = 0.5f;
        private const float ChamberMinElevation = 0.7f;
        private const int ChamberMinDistToEndpoint = 10;

        public static void Apply(Map map, int worldTile)
        {
            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var sw = verbose ? Stopwatch.StartNew() : null;

            // 枚举 B 的已生成邻居（含休眠——region 随档序列化、软休眠不卸载，分组查询安全）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(new PlanetTile(worldTile), worldNeighbors);

            var caves = MapGenerator.Caves;
            var elevation = MapGenerator.Elevation;
            var sourceTile = SeamlessTileManager.NeighborGenerationSourceTile;

            var endpoints = new List<IntVec3>();
            foreach (var nt in worldNeighbors)
            {
                var neighborTile = nt.tileId;
                if (neighborTile == worldTile) continue;
                var mapN = FindNeighborMap(neighborTile);
                if (mapN == null) continue;

                // 可达性目标：触发图 A = goto 触发位置（mapId 匹配且 valid，缺失回落 A 中心）；其余 C = 中心。
                IntVec3 target;
                string targetLabel;
                if (neighborTile == sourceTile
                    && SeamlessTileManager.NeighborGenerationSourceMapId == mapN.uniqueID
                    && SeamlessTileManager.NeighborGenerationSourceTriggerCell.IsValid)
                {
                    target = SeamlessTileManager.NeighborGenerationSourceTriggerCell;
                    targetLabel = "trigger";
                }
                else
                {
                    target = mapN.Center;
                    targetLabel = "center";
                }

                var rep = PickRepresentative(map, worldTile, mapN, neighborTile, target);
                if (!rep.IsValid)
                {
                    if (verbose)
                        Log.Message($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile}): neighbor wt={neighborTile} " +
                                    $"has no representative reaching its {targetLabel} target, corridor to it skipped.");
                    continue;
                }

                // 镜像：与 CollectNeighborRefs 同约定（origin = 当前图 B，契约 N + offset = B）
                // → 端点 = rep + offset，无方向翻转（曾反方向计算再心算翻符号出错，2026-08-30）。
                var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTile, map, mapN);
                var endpoint = rep + offset;
                if (offset == IntVec3.Zero || !endpoint.InBounds(map))
                {
                    if (verbose)
                        Log.Message($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile}): neighbor wt={neighborTile} " +
                                    $"rep {rep} offset {offset} endpoint {endpoint} invalid, corridor to it skipped.");
                    continue;
                }
                endpoints.Add(endpoint);
            }

            if (endpoints.Count == 0)
            {
                if (verbose)
                    Log.Message($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile}): no valid endpoints, no corridor dug.");
                return;
            }

            // 走廊足迹记录（392 混合免疫，见 CorridorCellsComponent 注释）。
            var corridorComp = map.GetComponent<CorridorCellsComponent>();
            if (corridorComp == null)
            {
                corridorComp = new CorridorCellsComponent(map);
                map.components.Add(corridorComp);
            }

            var carved = CarveCorridors(map, caves, elevation, endpoints, worldTile, corridorComp.cells, out var chamberCenter, out var chamberDug);

            if (verbose)
                Log.Message($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile}): endpoints={endpoints.Count} " +
                            $"carvedRockCells={carved} chamber={(chamberDug ? chamberCenter.ToString() : "none")} " +
                            $"in {sw?.ElapsedMilliseconds ?? 0}ms.");
        }

        /// <summary>
        /// N 侧代表格选取：接缝带（朝 B 的边）非岩格按 region 分集合 → 每集合随机代表 →
        /// 可达目标者中随机取一。返回 <see cref="IntVec3.Invalid"/> = 无合格代表（不为该邻居挖洞）。
        /// </summary>
        private static IntVec3 PickRepresentative(Map map, int worldTile, Map mapN, int neighborTile, IntVec3 target)
        {
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(neighborTile, mapN.Size.x);
            var edgeIdx = WorldTileGeometry.FindNeighborIndex(neighborTile, worldTile);
            if (verts == null || verts.Count < 3 || edgeIdx < 0 || edgeIdx >= verts.Count) return IntVec3.Invalid;
            var edgeA = verts[edgeIdx];
            var edgeB = verts[(edgeIdx + 1) % verts.Count];

            var band = SeamlessPolygonGeometry.BuildSeamBand(neighborTile, mapN.Size.x);
            if (band.Band.Count == 0) return IntVec3.Invalid;

            // 候选：带上属于该边且无自然岩的格，按 region 分集合。
            var groups = new Dictionary<Region, List<IntVec3>>();
            foreach (var cell in band.Band)
            {
                // 归属该边（越过阈值即归邻边——勿让端点落到不朝 B 的边上）。
                if (DistToSegment(new Vector2(cell.x + 0.5f, cell.z + 0.5f), edgeA, edgeB) > EdgeMembershipDist) continue;
                // 非自然岩（镜像格在 N 侧无岩 → 392 照抄区不会回填岩堵 B 侧走廊口）。
                var edifice = cell.GetEdifice(mapN);
                if (edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock) continue;
                // region 分组（无 region = 深水/实岩等不可通行格，出局）。
                var region = cell.GetRegion(mapN);
                if (region == null) continue;
                if (!groups.TryGetValue(region, out var list))
                {
                    list = new List<IntVec3>();
                    groups[region] = list;
                }
                list.Add(cell);
            }
            if (groups.Count == 0) return IntVec3.Invalid;

            // 每集合随机代表 → 可达目标者中随机取一。
            var traverse = TraverseParms.For(TraverseMode.NoPassClosedDoors);
            var validReps = new List<IntVec3>();
            foreach (var list in groups.Values)
            {
                var rep = list[Rand.Range(0, list.Count)];
                if (mapN.reachability.CanReach(rep, target, PathEndMode.OnCell, traverse))
                    validReps.Add(rep);
            }
            if (validReps.Count == 0) return IntVec3.Invalid;
            return validReps[Rand.Range(0, validReps.Count)];
        }

        /// <summary>邻居图查找（数据查询口径：含休眠图——软休眠不卸载、region 网格仍可读）。</summary>
        private static Map FindNeighborMap(int neighborTile)
        {
            foreach (var m in Find.Maps)
            {
                if (m.Disposed) continue;
                if (SeamlessTileRegistry.GetMapWorldTile(m) == neighborTile) return m;
            }
            return null;
        }

        /// <summary>点到线段距离（2D 地图坐标，与 Patch_GenStepFog 同款）。</summary>
        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var sqrLen = ab.sqrMagnitude;
            if (sqrLen < 1e-6f) return Vector2.Distance(p, a);
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLen);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>
        /// 多端点走廊（星形，汇于 B 中心）+ 全局唯一深山洞室。返回写入 Caves 的"将来会有岩石"格数。
        /// 噪声 seed 由世界 seed ⊕ worldTile ⊕ endpoint 派生（同 tile 重生成走廊形态一致）。
        /// </summary>
        private static int CarveCorridors(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, List<IntVec3> endpoints, int worldTile, HashSet<IntVec3> footprint, out IntVec3 chamberCenter, out bool chamberDug)
        {
            chamberCenter = IntVec3.Invalid;
            chamberDug = false;
            var carved = 0;

            var bestChamber = IntVec3.Invalid;
            var bestChamberDir = Vector3.zero;
            var bestElev = ChamberMinElevation;

            foreach (var endpoint in endpoints)
            {
                var seed = Gen.HashCombineInt(Gen.HashCombineInt(Find.World.info.Seed, worldTile.GetHashCode()), endpoint.GetHashCode());
                var meanderNoise = new Perlin(0.02f, 2.0, 0.5, 4, seed, QualityMode.Medium);
                var widthNoise = new Perlin(0.05f, 2.0, 0.5, 4, Gen.HashCombineInt(seed, 0x9e37), QualityMode.Medium);

                var start = map.Center.ToVector3();
                var end = endpoint.ToVector3();
                var total = Vector3.Distance(start, end);
                if (total < 1f) continue;
                var dirVec = (end - start) / total;
                var perp = new Vector3(-dirVec.z, 0f, dirVec.x);

                var cellsTraveled = 0;
                var branchedLeft = false;
                var branchedRight = false;

                for (var t = 0f; t <= total; t += StepLength)
                {
                    var frac = t / total;
                    // 端点正弦包络：起终点偏移归零，走廊精确落在中心与端点上。
                    var envelope = Mathf.Sin(frac * Mathf.PI);
                    var meander = (float)meanderNoise.GetValue(t, worldTile * 0.13f, 0f) * MeanderAmplitude * envelope;
                    var p = start + dirVec * t + perp * meander;
                    var pCell = p.ToIntVec3();

                    var width = Mathf.Clamp(BaseWidth + (float)widthNoise.GetValue(p.x, p.z, 0f) * WidthNoiseAmplitude, WidthMin, WidthMax);
                    carved += WriteCaveDisc(map, caves, elevation, pCell, width, footprint);

                    // 深山洞室选位（全局唯一）：跨全部走廊取 elevation 最大采样点（最可能过 CaveHives
                    // 的 10 格腐蚀过滤），距任一端点 >10（勿跨接缝带照抄区——392 的 SyncRockBuildingTo
                    // 会按对端快照把洞室岩填回）。
                    if (pCell.InBounds(map)
                        && SeamlessGridMath.ChebyshevDistance(pCell, endpoint) > ChamberMinDistToEndpoint
                        && elevation[pCell] > bestElev)
                    {
                        bestElev = elevation[pCell];
                        bestChamber = pCell;
                        bestChamberDir = dirVec;
                    }

                    // 分支：走过一段后各发至多一条短支洞（纯删岩，不影响主走廊保证）。
                    if (cellsTraveled >= BranchAfterCells && width > WidthMin + BranchWidthPenalty)
                    {
                        if (!branchedLeft && Rand.Chance(BranchChance))
                        {
                            branchedLeft = true;
                            carved += WalkBranch(map, caves, elevation, p, dirVec, 1f, width - BranchWidthPenalty, footprint);
                        }
                        if (!branchedRight && Rand.Chance(BranchChance))
                        {
                            branchedRight = true;
                            carved += WalkBranch(map, caves, elevation, p, dirVec, -1f, width - BranchWidthPenalty, footprint);
                        }
                    }
                    cellsTraveled++;
                }
            }

            if (bestChamber.IsValid)
            {
                // 洞厅 = 沿该走廊方向三个盘（直径 ~8±噪声、纵向 ~12 的不规则轮廓），
                // 与走廊同一条 caves 通道；Hive 落此则 5 格内守卫点必成。
                var w = Mathf.Clamp(ChamberWidth + widthNoiseForChamber(worldTile, bestChamber),
                    ChamberWidth - ChamberWidthNoise, ChamberWidth + ChamberWidthNoise);
                for (var i = -1; i <= 1; i++)
                {
                    var c = (bestChamber.ToVector3() + bestChamberDir * (i * 2f)).ToIntVec3();
                    carved += WriteCaveDisc(map, caves, elevation, c, w, footprint);
                }
                chamberCenter = bestChamber;
                chamberDug = true;
            }
            return carved;
        }

        /// <summary>洞室盘宽噪声（与走廊宽度噪声同族，独立小函数避免主循环持引用）。</summary>
        private static float widthNoiseForChamber(int worldTile, IntVec3 center)
        {
            var noise = new Perlin(0.05f, 2.0, 0.5, 4,
                Gen.HashCombineInt(Gen.HashCombineInt(Find.World.info.Seed, worldTile.GetHashCode()), 0x51ed), QualityMode.Medium);
            return (float)noise.GetValue(center.x, center.z, 0f) * ChamberWidthNoise;
        }

        /// <summary>分支支洞：从主走廊某点朝侧向 ±40~90° 直线走 5-10 格（MapGenCavesUtility 分支量级）。</summary>
        private static int WalkBranch(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, Vector3 origin, Vector3 mainDir, float sideSign, float width, HashSet<IntVec3> footprint)
        {
            var angle = Rand.Range(40f, 90f) * sideSign;
            var dir = Quaternion.AngleAxis(angle, Vector3.up) * mainDir;
            var length = Rand.RangeInclusive(BranchMinLength, BranchMaxLength);
            var carved = 0;
            for (var t = 0.5f; t <= length; t += StepLength)
            {
                carved += WriteCaveDisc(map, caves, elevation, (origin + dir * t).ToIntVec3(), width, footprint);
            }
            return carved;
        }

        /// <summary>
        /// 径向图案写入（SetCaveAround 同款手法，MapGenCavesUtility.SetCaveAround 是 private 故仿写）：
        /// 只在"将来会生成岩石"（elevation&gt;0.7）的格上写 caves，非岩格写无意义（RocksFromGrid 本就不
        /// spawn）；**足迹集记录一切盘内 in-bounds 格**（含低 elevation 格——392 照抄区对端快照有岩
        /// 时无视本端 elevation 也会 spawn，免疫保护必须覆盖整条走廊宽度）。
        /// </summary>
        private static int WriteCaveDisc(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, IntVec3 center, float width, HashSet<IntVec3> footprint)
        {
            if (!center.InBounds(map)) return 0;
            var count = 0;
            var num = GenRadial.NumCellsInRadius(width / 2f);
            for (var i = 0; i < num; i++)
            {
                var cell = center + GenRadial.RadialPattern[i];
                if (!cell.InBounds(map)) continue;
                footprint.Add(cell);
                if (elevation[cell] <= 0.7f) continue;
                caves[cell] = Mathf.Max(caves[cell], width);
                count++;
            }
            return count;
        }
    }
}
