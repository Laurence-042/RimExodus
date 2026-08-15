using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写：相邻地块地图在混合带做 terrainDef 卷积混合。
    ///
    /// 【混合带由 A snapshot 决定（核心设计）】
    /// 决定"哪些格需要混合"的不是 C（新生成 tile）自身的某个固定 bandWidth，
    /// 而是 A（已生成邻居）被 void 裁掉的区域（void 条带）有多少。A 的 void 条带在
    /// snapshot（void 裁切前备份的完整矩形 topGrid）里有真实地形数据——这正是 C 接缝处
    /// 应该"继承"的地形。遍历 A 六边形外的格（= A 被 void 裁掉的部分），映射到 C 的
    /// cCell = aCell + offset 做卷积混合。混合带宽度 = A 被裁掉的实际深度，无固定配置。
    ///
        /// 【w 归一化必须逐格局部（两代方案的教训）】第一代用全图 void 条带最大深度、第二代用
        /// 候选集最大深度（maxDepth）归一——两者都被 A 方形角落格污染：角落深度可达 40-68 格
        /// （远超接缝边中点 ~17 格），且与接缝相邻的方形角落能绕过六边形顶点投影进 C 六边形的
        /// 侧向楔形区（"角落格投影到 C 后多在 C 六边形外被过滤"的假设已被实测证伪），
        /// maxDepth 被抬高 2-3 倍 → 整条混合带 w≥0.45、邻居众数全带通吃（A 全岩石时混合带被
        /// 整条写成岩石）。现行方案 dSq/(dSq+dHex) 逐格比例（见下），无全局统计量，此类污染
        /// 结构性不可能。
    ///
    /// 【窄结构保护（权威元数据判据，非局部模式识别）】3×3 众数卷积天然抹掉 ≤2 格宽的
    /// 线性结构（窗口内少数派）。保护规则：
    /// - 道路（双层判据）：①本格已是 IsRoad/bridge 地形精确跳过；②<see cref="SeamlessRoadPaths"/>
    ///   （GenStep_Roads A* 路径快照）±3 格切比雪夫缓冲兜底 Gravel 等无 Road tag 路面。
    ///   生成器自己知道路在哪，零歧义；从局部地形模式反推（"细线检测"）无法区分
    ///   2 格宽土径和常规地形的边缘条带（局部模式同构）。
    /// - 水格：C 当前地形 IsWater 跳过。水的连续由 CoastalEdgeFill(230)/river mutator
    ///   按世界图两端独立保证，不需要 SeamOverride 继承。
    /// 只要 road/river 的生成对齐正确，两端自然衔接——SeamOverride 不做 A 侧结构继承。
    ///
    /// 【单向】只改 C（新生成 tile），不改 A（已生成 tile 保持原样）。
    /// A 生成时 C 还不存在，A 没参考任何人——这是自然的。
    ///
    /// 【为什么卷积】terrainDef 离散，不能直接加权平均。卷积把每个 cell 的 terrainDef
    /// 变成"周围 3×3 邻域的 terrainDef 分布"，分布可以加权平均，再取众数 → 平滑过渡。
    ///
        /// 【权重 w】w = wCap × dSq/(dSq+dHex)：dSq = aCell 到 A 方形边界的切比雪夫格距（解析），
        /// dHex = 到 A 六边形边的欧氏垂距。贴 A 六边形边（紧贴 A 可见区域）→ w≈wCap（强继承
        /// A snapshot，代表性越强），贴方形外缘 → w≈0（弱继承）。逐格局部归一化还保证窄条带
        /// 区段接缝侧 w 同样接近 wCap（连续性沿整条边一致，与条带深浅无关），深角落格只压低
        /// 自己的 w。dSq 切比雪夫 / dHex 欧氏的度量混用为有意取舍：≤√2 单调偏差，dither 下不可见。
    ///
    /// 【权重噪声】w 是纯线性距离函数时，GetMode 离散跳变会在带内形成规则等距的过渡线。
    /// 在 w 上叠加低频空间 Perlin 噪声（基于 worldTile 稳定 seed，幅度可配 seamOverrideNoiseAmplitude），
    /// dither 掉离散跳变 → 过渡线变弯曲不规则斑块。Clamp01 保证不破坏总体单调趋势。
    ///
    /// 【兼容性】不碰 elevation/fertility grid、不碰 genStep 内部逻辑。
    /// 纯 terrainDef snapshot 操作，兼容所有地形扩展 mod。河流/海岸（mutator/TerrainPatchMaker 改的
    /// terrainGrid）在备份时已包含，卷积自然覆盖。
    /// </summary>
    public static class SeamlessSeamOverride
    {
        /// <summary>
        /// 对 map 的所有已加载邻居做单向接缝覆写（只改 map 自身，不改邻居）。
        /// 在 GenStep_SeamOverride.Generate 里调用（order=1410，void 裁切 1400 之后、Fog 1500 之前）。
        /// </summary>
        public static void ApplyOneWay(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var mapSize = map.Size.x;

            // 权重噪声：低频空间 Perlin，dither 掉 GetMode 离散跳变（把规则等距过渡线打散成弯曲斑块）。
            // 基于 worldTile 的稳定 seed → 同一地块多次生成噪声一致，不同地块噪声不同。
            // 频率 0.04 让带内能跨约 1 个噪声周期，相邻格噪声值接近（空间相关），不会逐格雪花。
            // 幅度可配（seamOverrideNoiseAmplitude，默认 0.15，0=关闭）。
            var noiseAmp = RimExodusMod.Settings?.seamOverrideNoiseAmplitude ?? 0.15f;
            Perlin weightNoise = null;
            if (noiseAmp > 0f)
            {
                weightNoise = new Perlin(0.04f, 2.0, 0.5, 4, worldTile * 31 + 7919, QualityMode.Medium);
            }

            // 本端多边形顶点 + 世界邻居列表。
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid;
            var mapDrawer = map.mapDrawer;
            // 硬边界来源②修复：最内圈 w 不再硬夹到 1.0，cap 到此值系统保留 (1-wCap) self 分布。
            var wCap = Mathf.Clamp01(RimExodusMod.Settings?.seamOverrideWeightCap ?? 0.9f);
            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var written = 0;

            // C 的道路保护集：GenStep_Roads 路径快照 ±3 格缓冲（覆盖 Bezier 平滑相对 A* 折线的偏离）。
            // 窄路（1-2 格宽）在 3×3 卷积里永远是少数派，不保护会被周围地形卷没。
            var roadGuard = BuildRoadGuard(map);

            foreach (var neighborTile in neighborWorldTiles)
            {
                // 邻居是否已加载。
                if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile, out var neighborMap)) continue;
                if (neighborMap == null || neighborMap.Disposed || neighborMap == map) continue;

                // 邻居 snapshot（void 裁切前的完整矩形 topGrid）。
                var neighborSnapshot = GetSnapshot(neighborMap);
                if (neighborSnapshot == null) continue;

                // offset：本端 cell = 邻居 cell + offset。故邻居 cell = 本端 cell - offset，本端 cell = 邻居 cell + offset。
                var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTile, map);
                if (offset == IntVec3.Zero) continue;

                var neighborSize = neighborMap.Size.x;

                // A（邻居）的多边形顶点——用于判定 aCell 是否在 A 六边形外（= A 被 void 裁掉的条带）。
                var neighborVerts = SeamlessPolygonGeometry.BuildPolygonVertices(neighborTile, neighborSize);
                if (neighborVerts.Count < 3) continue;

                // 第一遍：收集候选混合格（A 六边形外的 aCell，投影 cCell 落在 C 可见区域内）。
                // 候选集不做走廊/角落限制：A 方形角落格能绕过六边形顶点投影进 C 六边形的侧向
                // 楔形区（实测数据点 A(249,249)→C(59,146)，距 C 中心仅 ~69 格 ≪ 内切圆 ~108 格），
                // 会进候选集——w 已是逐格局部归一化（见第二遍），角落深格只压低自己的 w，
                // 不影响其他格的权重。
                candidates.Clear();
                foreach (var aCell in CellRect.WholeMap(neighborMap))
                {
                    // 只处理 A 被 void 裁掉的格（A 六边形外）。A 可见区域（六边形内）跳过。
                    if (SeamlessPolygonGeometry.IsCellInPolygon(neighborVerts, neighborSize, aCell)) continue;

                    // 映射到 C。
                    var cCell = new IntVec3(aCell.x + offset.x, 0, aCell.z + offset.z);
                    if (!cCell.InBounds(map)) continue;
                    // 只混合 C 可见区域（C 六边形内）。C 六边形外是 C 的 void，不混合。
                    if (!SeamlessPolygonGeometry.IsCellInPolygon(verts, mapSize, cCell)) continue;

                    candidates.Add((aCell, cCell, SeamlessPolygonGeometry.DistanceToNearestEdge(neighborVerts, aCell)));
                }
                if (candidates.Count == 0) continue;

                // 第二遍：混合。
                foreach (var (aCell, cCell, distToEdge_A) in candidates)
                {
                    var cIdx = cellIndices.CellToIndex(cCell);
                    var localTerrain = topGrid[cIdx];
                    if (localTerrain == null || (voidDef != null && localTerrain == voidDef)) continue;

                    // 道路保护①（地形判据，精确）：本格已是路/桥地形 → 不混合。窄路（1-2 格宽）
                    // 在 3×3 卷积里永远是少数派，混合必然失真；路面可铺到距 A* 中线 2-3 格处
                    // （材质曲线 fromRoad≤1.4 + 抗锯齿）+ Bezier 偏离折线最多 3-4 格，仅靠路径
                    // 缓冲判据会漏（用户实测：snapshot=BrokenAsphalt 的格被卷积成 Sand）。
                    if (localTerrain.IsRoad || localTerrain.bridge) continue;
                    // 道路保护②（路径缓冲判据，兜底）：A* 路径 ±3 格内的格不混合——覆盖
                    // Gravel 等无 Road tag 的路面（曲线同样到 fromRoad 1.4）。
                    if (roadGuard.Contains(cCell)) continue;
                    // 河流/海岸保护：C 的水格不混合。水的连续性由 CoastalEdgeFill(230) 和
                    // river mutator 按世界图两端独立保证，不需要 SeamOverride 继承。
                    if (localTerrain.IsWater) continue;

                    // w 权重：逐格局部归一化 aCell 在本地 void 条带内的相对位置。
                    // dSq = 到 A 方形边界的切比雪夫格距（解析），distToEdge_A = 到 A 六边形边的
                    // 欧氏垂距。贴六边形边 → w≈wCap（强继承 A snapshot），贴方形外缘 → w≈0。
                    // 无全局 maxDepth（弃用原因见文件头）：角落深格只压低自己的 w。
                    // 分母守卫：六边形顶点贴方形边时 dSq 与 dHex 同时≈0，取 w=0。
                    var dSq = Mathf.Min(Mathf.Min(aCell.x, aCell.z),
                        Mathf.Min(neighborSize - 1 - aCell.x, neighborSize - 1 - aCell.z));
                    var wBase = (dSq + distToEdge_A) > 1e-3f
                        ? wCap * (dSq / (dSq + distToEdge_A))
                        : 0f;
                    // 叠加空间噪声：w' = clamp01(wBase + n×amp)，n∈[-1,1]。clamp01 保证 w∈[0,1]，
                    // 不破坏"靠近边偏邻居、深入 void 偏 self"的总体单调，只是把过渡线抖弯。
                    var w = weightNoise != null
                        ? Mathf.Clamp01(wBase + (float)weightNoise.GetValue(cCell) * noiseAmp)
                        : wBase;

                    // 3×3 卷积：self 用 C 当前 topGrid（裁切后真实状态），neighbor 用 A snapshot（裁切前完整地形）。
                    var selfDist = Convolve3x3(topGrid, mapSize, voidDef, cCell);
                    var neighborDist = Convolve3x3(neighborSnapshot, neighborSize, null, aCell);
                    if (selfDist == null || neighborDist == null) continue;

                    var blended = BlendDistributions(neighborDist, selfDist, w);
                    var chosen = GetMode(blended);
                    if (chosen == null || chosen == localTerrain) continue;

                    topGrid[cIdx] = chosen;
                    mapDrawer.MapMeshDirty(cCell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
                    SyncRockBuilding(map, cCell, chosen, out _);
                    written++;

                    if (verbose)
                        Log.Message($"[RimExodus] SeamOverride cCell={cCell} aCell={aCell} wBase={wBase:F3} w={w:F3} distEdge={distToEdge_A:F1} dSq={dSq} local={localTerrain.defName} chosen={chosen.defName}");
                }
            }

            if (verbose)
                Log.Message($"[RimExodus] SeamOverride map={map.uniqueID}(wt={worldTile}) written={written} cells.");
        }

        /// <summary>候选混合格缓存（aCell, cCell, distToEdge_A），复用避免每邻居分配。</summary>
        private static readonly List<(IntVec3 aCell, IntVec3 cCell, float dist)> candidates = new();

        /// <summary>
        /// 构建 C 的道路路径缓冲保护集：<see cref="SeamlessRoadPaths"/> 快照的 A* 路径节点，
        /// 每节点 ±<see cref="RoadGuardRadius"/> 格切比雪夫膨胀。有 Road tag / bridge 的路面
        /// 由混合循环的地形判据精确保护，本缓冲兜底 Gravel 等无 tag 路面。
        /// </summary>
        private static HashSet<IntVec3> BuildRoadGuard(Map map)
        {
            var guard = new HashSet<IntVec3>();
            var comp = map.GetComponent<SeamlessRoadPaths>();
            if (comp == null) return guard;

            foreach (var path in comp.paths)
            {
                foreach (var node in path)
                {
                    for (var dx = -RoadGuardRadius; dx <= RoadGuardRadius; dx++)
                    {
                        for (var dz = -RoadGuardRadius; dz <= RoadGuardRadius; dz++)
                        {
                            var c = new IntVec3(node.x + dx, 0, node.z + dz);
                            if (c.InBounds(map)) guard.Add(c);
                        }
                    }
                }
            }
            return guard;
        }

        /// <summary>
        /// 道路路径缓冲保护半径（格，切比雪夫）。3 = Bezier 偏离折线（最多 3-4 格）+
        /// 无 tag 路面半宽（Gravel 曲线到 fromRoad 1.4）。有 Road tag / bridge 的路面
        /// 由地形判据（IsRoad/bridge）精确保护，本缓冲只兜底无 tag 路面。
        /// </summary>
        private const int RoadGuardRadius = 3;

        /// <summary>获取 map 的基础地形 snapshot（地块从 MapParent_SeamlessTile，锚点从 Manager）。</summary>
        private static TerrainDef[] GetSnapshot(Map map)
        {
            if (map.Parent is MapParent_SeamlessTile tile) return tile.baseTerrainSnapshot;
            var manager = map.GetComponent<SeamlessTileManager>();
            return manager?.anchorBaseTerrainSnapshot;
        }

        /// <summary>
        /// 3×3 卷积：统计 centerCell 周围 3×3 邻域（含自身）每种 terrainDef 的出现次数。
        /// 越界格 clamp 到边界格（硬边界来源①修复：不再 skip，假设边界外与边界格同地形，
        /// 消除 snapshot 矩形边界处卷积窗口截断导致的残缺分布）。
        /// void 格跳过（voidDef != null 时，地形数组里可能含 void 的格不参与统计——用于 C 当前 topGrid）。
        /// **水格跳过**：河走廊延伸水/海岸水会大片出现在 A 的 void 条带 snapshot 里，
        /// 不跳过的话混合带众数可能被卷成水——水的连续性由 river/CoastalEdgeFill 两端
        /// 独立保证，卷积不采样水。
        /// 返回归一化占比（和=1）。
        /// </summary>
        /// <param name="voidDef">void TerrainDef，null 表示该数组无 void（如 A snapshot 是裁切前备份）。</param>
        private static Dictionary<TerrainDef, float> Convolve3x3(TerrainDef[] snapshot, int mapSize, TerrainDef voidDef, IntVec3 center)
        {
            var counts = new Dictionary<TerrainDef, int>();
            var total = 0;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    // 来源①修复：越界 clamp 到边界格（而非 skip），避免 snapshot 矩形边界处窗口截断。
                    var nx = center.x + dx;
                    if (nx < 0) nx = 0; else if (nx >= mapSize) nx = mapSize - 1;
                    var nz = center.z + dz;
                    if (nz < 0) nz = 0; else if (nz >= mapSize) nz = mapSize - 1;
                    var idx = nz * mapSize + nx;
                    var t = snapshot[idx];
                    if (t == null) continue;
                    if (voidDef != null && t == voidDef) continue;
                    if (t.IsWater) continue;
                    counts.TryGetValue(t, out var c);
                    counts[t] = c + 1;
                    total++;
                }
            }

            if (total == 0) return null;
            var result = new Dictionary<TerrainDef, float>(counts.Count);
            foreach (var kv in counts)
                result[kv.Key] = (float)kv.Value / total;
            return result;
        }

        /// <summary>加权混合两个分布：result[t] = distA[t] × w + distB[t] × (1-w)。</summary>
        private static Dictionary<TerrainDef, float> BlendDistributions(
            Dictionary<TerrainDef, float> distA, Dictionary<TerrainDef, float> distB, float w)
        {
            var result = new Dictionary<TerrainDef, float>();
            var oneMinusW = 1f - w;
            foreach (var kv in distA)
            {
                var v = kv.Value * w;
                distB.TryGetValue(kv.Key, out var bv);
                result[kv.Key] = v + bv * oneMinusW;
            }
            // distB 中有但 distA 中没有的。
            foreach (var kv in distB)
            {
                if (!result.ContainsKey(kv.Key))
                    result[kv.Key] = kv.Value * oneMinusW;
            }
            return result;
        }

        /// <summary>
        /// 取分布中占比最大的 terrainDef（众数）。平局时按 defName 稳定决胜（消除 Dictionary 遍历序的随机性，
        /// 避免相邻 cell 因遍历序不同而在占比相同时翻转，产生斑驳）。
        /// </summary>
        private static TerrainDef GetMode(Dictionary<TerrainDef, float> dist)
        {
            TerrainDef best = null;
            var bestVal = -1f;
            foreach (var kv in dist)
            {
                if (kv.Value > bestVal + 1e-6f ||
                    (Mathf.Abs(kv.Value - bestVal) <= 1e-6f && best != null && string.Compare(kv.Key.defName, best.defName, System.StringComparison.Ordinal) < 0))
                {
                    bestVal = kv.Value;
                    best = kv.Key;
                }
            }
            return best;
        }

        /// <summary>
        /// 岩石类 TerrainDef 集合（延迟初始化）。岩石 TerrainDef = 某 BuildingDef 的 building.naturalTerrain
        /// （GenStep_Terrain 在 elevation≥0.61 格铺这种地形，对应 RocksFromGrid spawn 的岩石 Building）。
        /// 用于 SyncRockBuilding 同步岩石 Building 与地形一致。
        /// </summary>
        private static HashSet<TerrainDef> rockTerrains;
        private static bool IsRockTerrain(TerrainDef t)
        {
            if (t == null) return false;
            if (rockTerrains == null)
            {
                rockTerrains = new HashSet<TerrainDef>();
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.building != null && def.building.naturalTerrain != null)
                        rockTerrains.Add(def.building.naturalTerrain);
                }
            }
            return rockTerrains.Contains(t);
        }

        /// <summary>
        /// 同步 cell 上的岩石 Building 与 TerrainDef 一致。out action: 1=spawn, -1=destroy, 0=无操作。
        /// </summary>
        private static void SyncRockBuilding(Map map, IntVec3 cell, TerrainDef chosen, out int action)
        {
            action = 0;
            var existing = cell.GetEdifice(map);
            var hasRockBuilding = existing != null && existing.def.building != null && existing.def.building.naturalTerrain != null;
            var wantRock = IsRockTerrain(chosen);

            if (wantRock && !hasRockBuilding)
            {
                // chosen 是岩石类但无岩石 Building → spawn。
                // 只在无 edifice 的格 spawn（避免覆盖已存在的非岩石 edifice）。
                if (existing == null)
                {
                    var rockDef = GenStep_RocksFromGrid.RockDefAt(cell);
                    if (rockDef != null)
                    {
                        GenSpawn.Spawn(rockDef, cell, map);
                        action = 1;
                    }
                }
            }
            else if (!wantRock && hasRockBuilding)
            {
                // chosen 是非岩石但有岩石 Building → 清除（Vanish 无掉落）。
                existing.Destroy(DestroyMode.Vanish);
                action = -1;
            }
        }

    }
}
