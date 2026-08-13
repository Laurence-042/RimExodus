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
    /// 【方案】生成时 Terrain(210) 铺好后、void 裁切前备份完整 terrainGrid（baseTerrainSnapshot）。
    /// 新生成 tile C 完成后，对每个已加载邻居 A，C 的混合带 cell 用卷积（3×3 邻域 terrainDef 分布）
    /// 加权混合：混合分布 = A 分布 × w + C 分布 × (1-w)，取众数。w 从边界(→1 取 A)到中心(→0 取 C)递减。
    ///
    /// 【单向】只改 C（新生成 tile），不改 A（已生成 tile 保持原样）。
    /// A 生成时 C 还不存在，A 没参考任何人——这是自然的。
    ///
    /// 【为什么卷积】terrainDef 离散，不能直接加权平均。卷积把每个 cell 的 terrainDef
    /// 变成"周围 3×3 邻域的 terrainDef 分布"，分布可以加权平均，再取众数 → 平滑过渡。
    ///
    /// 【权重噪声】w 是纯线性距离函数时，GetMode 离散跳变会在带内形成规则等距的 self↔neighbor 过渡线。
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
            var ratio = RimExodusMod.Settings?.seamOverrideRatio ?? 0.25f;
            if (ratio <= 0f) return; // 0 = 关闭接缝覆写。

            var mapSize = map.Size.x;
            var bandWidth = Mathf.Max(1, Mathf.RoundToInt(ratio * mapSize * 0.5f));

            // 权重噪声：低频空间 Perlin，dither 掉 GetMode 离散跳变（把规则等距过渡线打散成弯曲斑块）。
            // 基于 worldTile 的稳定 seed → 同一地块多次生成噪声一致，不同地块噪声不同。
            // 频率 0.04 让带内（bandWidth≈30）能跨约 1 个噪声周期，相邻格噪声值接近（空间相关），
            // 不会逐格雪花。幅度可配（seamOverrideNoiseAmplitude，默认 0.15，0=关闭）。
            var noiseAmp = RimExodusMod.Settings?.seamOverrideNoiseAmplitude ?? 0.15f;
            Perlin weightNoise = null;
            if (noiseAmp > 0f)
            {
                weightNoise = new Perlin(0.04f, 2.0, 0.5, 4, worldTile * 31 + 7919, QualityMode.Medium);
            }

            // 本端 snapshot（void 裁切前的完整地形）。
            var selfSnapshot = GetSnapshot(map);
            if (selfSnapshot == null) return;

            // 本端多边形顶点 + 世界邻居列表。
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            var neighborWorldTiles = new List<int>(worldNeighbors.Count);
            foreach (var nt in worldNeighbors) neighborWorldTiles.Add(nt.tileId);

            // 混合带：格 → 最近边对应的邻居 worldTile。bandDistances：格 → 到 void 的切比雪夫距离（供权重 w 用）。
            // 用 ComputeVoidBand（void 边界 + 多轮膨胀）而非旧的 ComputeEdgeBand（浮点多边形边距离）——
            // 后者与 void 边界（格角检测）口径不一致，会让最外圈擦边格漏出 band 外不被卷积覆写，
            // 保持原生土壤，形成"岩石（band 内）→ 土（band 外最外圈）→ void"的泥土带。
            // 同时 w 也用 void 切比雪夫距离（而非浮点多边形边距离），保证权重随到 void 的距离单调，
            // 避免浮点距离在多边形顶点附近振荡导致卷积在 self/neighbor 间翻转产生条带（土/岩交替）。
            var band = new Dictionary<IntVec3, int>();
            var bandDistances = new Dictionary<IntVec3, int>();
            SeamlessPolygonGeometry.ComputeVoidBand(map, bandWidth, neighborWorldTiles, band, bandDistances);

            if (band.Count == 0) return;

            // 按邻居分组（每个邻居的混合带 cell 一组），减少重复算 offset。
            var cellsByNeighbor = new Dictionary<int, List<IntVec3>>();
            foreach (var kv in band)
            {
                if (!cellsByNeighbor.TryGetValue(kv.Value, out var list))
                {
                    list = new List<IntVec3>();
                    cellsByNeighbor[kv.Value] = list;
                }
                list.Add(kv.Key);
            }

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid;
            var mapDrawer = map.mapDrawer;
            var diag = RimExodusMod.Settings?.seamOverrideDiag ?? false;

            foreach (var kv in cellsByNeighbor)
            {
                var neighborTile = kv.Key;
                var bandCells = kv.Value;

                // 邻居是否已加载。
                if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile, out var neighborMap)) continue;
                if (neighborMap == null || neighborMap.Disposed || neighborMap == map) continue;

                // 邻居 snapshot。
                var neighborSnapshot = GetSnapshot(neighborMap);
                if (neighborSnapshot == null) continue;

                // offset：本端 cell = 邻居 cell + offset。故邻居 cell = 本端 cell - offset。
                var offset = SeamlessTileManager.ComputeNeighborOffset(worldTile, neighborTile, map);
                if (offset == IntVec3.Zero) continue;

                var neighborSize = neighborMap.Size.x;
                var neighborIndices = neighborMap.cellIndices;

                // 诊断取样统计（每邻居一条汇总日志）。
                int diagTotal = 0, diagOutOfBounds = 0, diagNeighborDistNull = 0, diagVoidCell = 0, diagUnchanged = 0, diagWritten = 0;
                float diagWMin = 1f, diagWMax = 0f;
                int diagWrittenToRock = 0, diagWrittenToSoil = 0;
                // chosen defName 分布（不只 toRock/toSoil 二分，精确定位写入的是泥土/沙/水/岩石）。
                var diagWrittenDefNames = diag ? new Dictionary<string, int>() : null;

                foreach (var cell in bandCells)
                {
                    diagTotal++;
                    var chebyDist = bandDistances.TryGetValue(cell, out var cd) ? cd : 1;
                    var wBase = 1f - Mathf.Clamp01((chebyDist - 1f) / Mathf.Max(1, bandWidth - 1));
                    // 叠加空间噪声：w' = clamp01(wBase + n×amp)，n∈[-1,1]。clamp01 保证 w∈[0,1]，
                    // 不破坏"最内圈偏邻居、中心偏 self"的总体单调，只是把过渡线抖弯。
                    var w = weightNoise != null
                        ? Mathf.Clamp01(wBase + (float)weightNoise.GetValue(cell) * noiseAmp)
                        : wBase;
                    if (diag) { if (w < diagWMin) diagWMin = w; if (w > diagWMax) diagWMax = w; }

                    var neighborCell = cell - offset;
                    if (!neighborCell.InBounds(neighborMap)) { diagOutOfBounds++; continue; }

                    var selfDist = Convolve3x3(selfSnapshot, mapSize, cell);
                    var neighborDist = Convolve3x3(neighborSnapshot, neighborSize, neighborCell);
                    if (selfDist == null || neighborDist == null) { diagNeighborDistNull++; continue; }

                    var localIdx = cellIndices.CellToIndex(cell);
                    var localTerrain = topGrid[localIdx];
                    if (localTerrain == null || (voidDef != null && localTerrain == voidDef)) { diagVoidCell++; continue; }

                    var blended = BlendDistributions(neighborDist, selfDist, w);
                    var chosen = GetMode(blended);
                    if (chosen == null || chosen == localTerrain)
                    {
                        diagUnchanged++;
                        continue;
                    }

                    topGrid[localIdx] = chosen;
                    mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
                    if (diag)
                    {
                        if (IsRockTerrain(chosen)) diagWrittenToRock++; else diagWrittenToSoil++;
                        diagWrittenDefNames.TryGetValue(chosen.defName, out var cn);
                        diagWrittenDefNames[chosen.defName] = cn + 1;
                    }
                    diagWritten++;

                    SyncRockBuilding(map, cell, chosen, out _);
                }

                if (diag)
                {
                    // written chosen defName 分布（降序）。
                    var writtenDist = diagWrittenDefNames.Count == 0 ? "(none)"
                        : FormatDefNameTally(diagWrittenDefNames);
                    Log.Message($"[RimExodus-SeamDiag] wt={worldTile} nbr={neighborTile} offset={offset} bandCells={diagTotal} " +
                        $"noiseAmp={noiseAmp:F2} w[{diagWMin:F2}..{diagWMax:F2}] oob={diagOutOfBounds} nbrDistNull={diagNeighborDistNull} " +
                        $"voidCell={diagVoidCell} unchanged={diagUnchanged} " +
                        $"written={diagWritten}(toRock={diagWrittenToRock},toSoil={diagWrittenToSoil}) writtenDist={writtenDist}");
                    // 两端 snapshot 在本邻居接缝带的分布：直接看出卷积输入是沙/水还是泥土。
                    SeamTerrainProbe.LogSnapshotBand(map, worldTile, selfSnapshot, $"1410-self-nbr{neighborTile}");
                    SeamTerrainProbe.LogSnapshotBand(neighborMap, neighborTile, neighborSnapshot, $"1410-neighbor-nbr{neighborTile}");
                }
            }
        }

        /// <summary>获取 map 的基础地形 snapshot（RimExodus tile 从 MapParent，锚点从 Manager）。</summary>
        private static TerrainDef[] GetSnapshot(Map map)
        {
            if (map.Parent is MapParent_SeamlessTile pocket) return pocket.baseTerrainSnapshot;
            var manager = map.GetComponent<SeamlessTileManager>();
            return manager?.anchorBaseTerrainSnapshot;
        }

        /// <summary>
        /// 3×3 卷积：统计 centerCell 周围 3×3 邻域（含自身）每种 terrainDef 的出现次数。
        /// 越界格跳过。void 格跳过（不参与统计）。
        /// 返回归一化占比（和=1）。
        /// </summary>
        private static Dictionary<TerrainDef, float> Convolve3x3(TerrainDef[] snapshot, int mapSize, IntVec3 center)
        {
            var counts = new Dictionary<TerrainDef, int>();
            var total = 0;
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var nx = center.x + dx;
                    var nz = center.z + dz;
                    if (nx < 0 || nx >= mapSize || nz < 0 || nz >= mapSize) continue;
                    var idx = nz * mapSize + nx;
                    var t = snapshot[idx];
                    if (t == null) continue;
                    if (voidDef != null && t == voidDef) continue;
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

        /// <summary>把 defName→count 字典格式化为 "defName=count, defName=count"（按 count 降序）。</summary>
        private static string FormatDefNameTally(Dictionary<string, int> tally)
        {
            if (tally == null || tally.Count == 0) return "(none)";
            var sb = new System.Text.StringBuilder();
            var items = new List<KeyValuePair<string, int>>(tally);
            items.Sort((a, b) =>
            {
                var c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return string.Compare(a.Key, b.Key, System.StringComparison.Ordinal);
            });
            var first = true;
            foreach (var kv in items)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 岩石类 TerrainDef 集合（延迟初始化）。岩石 TerrainDef = 某 BuildingDef 的 building.naturalTerrain
        /// （GenStep_Terrain 在 elevation≥0.61 格铺这种地形，对应 RocksFromGrid spawn 的岩石 Building）。
        /// 用于诊断日志区分"改成岩石 vs 改成土壤"，定位"一侧空地/对侧岩石"。
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
