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
    /// 中心走廊（2026-08）：邻接生成图 B 在 <see cref="MapGenerator.Caves"/> 浮点网格上挖一条
    /// "B 中心 → 生成源方向共享边端点"的走廊，让 RocksFromGrid(200) 在走廊格上不 spawn 岩石——
    /// 原版洞窟（MapGenCavesUtility → GenStep_RocksFromGrid 的 caves&lt;=0 守卫）的同一条无后效
    /// 通道，零 building Destroy、零屋顶支撑重算。
    ///
    /// **走廊形态（用户定夺）**：方向性直线骨架 + 自研 Perlin 变形——单纯最短路"太挫"、
    /// 单纯洞窟随机游走不符方向性前提。蜿蜒用垂直于行进方向的低频 Perlin 偏移（端点正弦包络
    /// 归零防起终点漂移）、宽度 Perlin 浮动、若干短分支，参数量级参考 CaveGenParms.Default。
    ///
    /// **端点定义（用户定夺）**：源边传送圈格 c，A 侧映射格 a = c − offset（邻居 offset 契约，
    /// <see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/> 现算，与 CollectNeighborRefs 同公式）
    /// 须在源图 A 上真实可达 A 中心（A 的 reachability 查询，NoPassClosedDoors 口径）——
    /// 单纯 B 侧可站不代表玩家能从 A 走到那里。B 侧不做地形判定（Terrain(210) 未铺，
    /// 且需求只排除岩石不排除水——深水不算阻挡）。
    ///
    /// **只挖 B 侧**（单向原则）：不修改源图 A。
    /// </summary>
    public static class SeamlessCenterCorridor
    {
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
        private const int MaxEndpointProbes = 60;

        public static void Apply(Map map, int worldTile)
        {
            // 初始图生成（开局/扎营/定居等）无生成源，无走廊义务。
            var sourceTile = SeamlessTileManager.NeighborGenerationSourceTile;
            if (sourceTile < 0 || sourceTile == worldTile) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(new PlanetTile(worldTile), worldNeighbors);
            var sourceValid = false;
            foreach (var n in worldNeighbors)
            {
                if (n.tileId == sourceTile) { sourceValid = true; break; }
            }
            if (!sourceValid) return; // 异常悬挂容错（与 GenStep_Fog 接缝揭雾同口径）

            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var sw = verbose ? Stopwatch.StartNew() : null;

            var mapSize = map.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            var edgeIdx = WorldTileGeometry.FindNeighborIndex(worldTile, sourceTile);
            if (verts == null || verts.Count < 3 || edgeIdx < 0 || edgeIdx >= verts.Count) return;
            var edgeMid = (verts[edgeIdx] + verts[(edgeIdx + 1) % verts.Count]) * 0.5f;

            var mapA = FindSourceMap(sourceTile);
            var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, sourceTile, map);

            IntVec3 endpoint;
            if (mapA != null && offset != IntVec3.Zero)
            {
                endpoint = PickEndpoint(map, worldTile, mapSize, edgeMid, mapA, offset);
            }
            else
            {
                endpoint = IntVec3.Invalid;
            }

            var fallback = false;
            if (!endpoint.IsValid)
            {
                // 回落：离边中点最近的传送圈格（不校验 A 侧可达——源图不在场（休眠图无碍，
                // 数据查询口径含休眠，此分支只会在源图真的查不到时进入）或 offset 失败）。
                fallback = true;
                endpoint = NearestRingCell(worldTile, mapSize, edgeMid);
            }
            if (!endpoint.IsValid)
            {
                if (verbose)
                    Log.Warning($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile}): no transport-ring cell near source edge mid {edgeMid}, corridor skipped.");
                return;
            }

            var caves = MapGenerator.Caves;
            var elevation = MapGenerator.Elevation;
            var carved = CarveCorridor(map, caves, elevation, map.Center, endpoint, worldTile);

            if (verbose)
                Log.Message($"[RimExodus] CenterCorridor map={map.uniqueID}(wt={worldTile} src={sourceTile}): " +
                            $"endpoint={endpoint}{(fallback ? " (fallback, A-side unchecked)" : "")} " +
                            $"carvedRockCells={carved} in {sw?.ElapsedMilliseconds ?? 0}ms.");
        }

        /// <summary>
        /// 端点双侧筛选：源边传送圈格按离边中点距离排序，逐个探测 A 侧映射格
        /// （a = c − offset，邻居 offset 契约）能否真实走到 A 中心。
        /// </summary>
        private static IntVec3 PickEndpoint(Map map, int worldTile, int mapSize, Vector2 edgeMid, Map mapA, IntVec3 offset)
        {
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
            if (band.TransportRing.Count == 0) return IntVec3.Invalid;

            var candidates = new List<IntVec3>(band.TransportRing);
            candidates.Sort((a, b) => DistSq(a, edgeMid).CompareTo(DistSq(b, edgeMid)));

            var traverse = TraverseParms.For(TraverseMode.NoPassClosedDoors);
            var probes = 0;
            foreach (var c in candidates)
            {
                if (probes++ >= MaxEndpointProbes) break;
                var aCell = new IntVec3(c.x - offset.x, 0, c.z - offset.z);
                if (!aCell.InBounds(mapA)) continue;
                if (mapA.reachability.CanReach(aCell, mapA.Center, PathEndMode.OnCell, traverse))
                    return c;
            }
            return IntVec3.Invalid;
        }

        private static IntVec3 NearestRingCell(int worldTile, int mapSize, Vector2 edgeMid)
        {
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
            if (band.TransportRing.Count == 0) return IntVec3.Invalid;
            IntVec3 best = IntVec3.Invalid;
            var bestD = float.MaxValue;
            foreach (var c in band.TransportRing)
            {
                var d = DistSq(c, edgeMid);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private static float DistSq(IntVec3 c, Vector2 p)
        {
            var dx = c.x - p.x;
            var dz = c.z - p.y;
            return dx * dx + dz * dz;
        }

        /// <summary>源图查找（数据查询口径：含休眠图——软休眠不卸载、region 网格仍可读）。</summary>
        private static Map FindSourceMap(int sourceTile)
        {
            foreach (var m in Find.Maps)
            {
                if (m.Disposed) continue;
                if (SeamlessTileRegistry.GetMapWorldTile(m) == sourceTile) return m;
            }
            return null;
        }

        /// <summary>
        /// 直线骨架 + Perlin 变形挖掘。返回实际写入 Caves 的"将来会有岩石"格数（elevation&gt;0.7）。
        /// 噪声 seed 由世界 seed ⊕ worldTile 派生（同 tile 重生成走廊形态一致，与 GL Prepare 同款纪律）。
        /// </summary>
        private static int CarveCorridor(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, IntVec3 startCell, IntVec3 endCell, int worldTile)
        {
            var seed = Gen.HashCombineInt(Find.World.info.Seed, worldTile.GetHashCode());
            var meanderNoise = new Perlin(0.02f, 2.0, 0.5, 4, seed, QualityMode.Medium);
            var widthNoise = new Perlin(0.05f, 2.0, 0.5, 4, Gen.HashCombineInt(seed, 0x9e37), QualityMode.Medium);

            var start = startCell.ToVector3();
            var end = endCell.ToVector3();
            var total = Vector3.Distance(start, end);
            if (total < 1f) return 0;
            var dirVec = (end - start) / total;
            var perp = new Vector3(-dirVec.z, 0f, dirVec.x);

            var carved = 0;
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

                var width = Mathf.Clamp(BaseWidth + (float)widthNoise.GetValue(p.x, p.z, 0f) * WidthNoiseAmplitude, WidthMin, WidthMax);
                carved += WriteCaveDisc(map, caves, elevation, p.ToIntVec3(), width);

                // 分支：走过一段后各发至多一条短支洞（纯删岩，不影响主走廊保证）。
                if (cellsTraveled >= BranchAfterCells && width > WidthMin + BranchWidthPenalty)
                {
                    if (!branchedLeft && Rand.Chance(BranchChance))
                    {
                        branchedLeft = true;
                        carved += WalkBranch(map, caves, elevation, p, dirVec, 1f, width - BranchWidthPenalty);
                    }
                    if (!branchedRight && Rand.Chance(BranchChance))
                    {
                        branchedRight = true;
                        carved += WalkBranch(map, caves, elevation, p, dirVec, -1f, width - BranchWidthPenalty);
                    }
                }
                cellsTraveled++;
            }
            return carved;
        }

        /// <summary>分支支洞：从主走廊某点朝侧向 ±40~90° 直线走 5-10 格（MapGenCavesUtility 分支量级）。</summary>
        private static int WalkBranch(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, Vector3 origin, Vector3 mainDir, float sideSign, float width)
        {
            var angle = Rand.Range(40f, 90f) * sideSign;
            var dir = Quaternion.AngleAxis(angle, Vector3.up) * mainDir;
            var length = Rand.RangeInclusive(BranchMinLength, BranchMaxLength);
            var carved = 0;
            for (var t = 0.5f; t <= length; t += StepLength)
            {
                carved += WriteCaveDisc(map, caves, elevation, (origin + dir * t).ToIntVec3(), width);
            }
            return carved;
        }

        /// <summary>
        /// 径向图案写入（SetCaveAround 同款手法，MapGenCavesUtility.SetCaveAround 是 private 故仿写）：
        /// 只在"将来会生成岩石"（elevation&gt;0.7）的格上写，非岩格写无意义（RocksFromGrid 本就不 spawn）。
        /// </summary>
        private static int WriteCaveDisc(Map map, MapGenFloatGrid caves, MapGenFloatGrid elevation, IntVec3 center, float width)
        {
            if (!center.InBounds(map)) return 0;
            var count = 0;
            var num = GenRadial.NumCellsInRadius(width / 2f);
            for (var i = 0; i < num; i++)
            {
                var cell = center + GenRadial.RadialPattern[i];
                if (!cell.InBounds(map)) continue;
                if (elevation[cell] <= 0.7f) continue;
                caves[cell] = Mathf.Max(caves[cell], width);
                count++;
            }
            return count;
        }
    }
}
