using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 海岸补铺：给临海地块的边缘（朝海洋邻居的那几条多边形边）铺海水，解决原版 Coast mutator
    /// 用"平均海岸角度"导致多面环海地块只铺一面海、其余面是土地、跨地块海岸不连续的问题。
    ///
    /// **与原版 Coast mutator 的区别**：原版 <see cref="TileMutatorWorker_Coast"/> 用
    /// <see cref="World.CoastAngleAt"/>（所有海洋邻居方向的 MeanAngle）+ FalloffAtAngle 朝单一平均
    /// 方向衰减。本类按"多边形每条边 ↔ 世界邻居"的不变量，对每条海洋邻居边独立铺海——三面环海时
    /// 三条边都铺，西北/东北角不再漏。
    ///
    /// **铺法**：对每个六边形内格，算到最近海洋边的浮点距离，加 Perlin 位移噪声抖动（海岸线自然），
    /// 按阈值（深水/浅水）铺地形。参考 Coast mutator 的阈值语义，但用"到海洋边距离"（格）替代
    /// "FalloffAtAngle 噪声值"。只改非 Stone 格（避免覆盖岩石山），用 MapGenUtility 取 biome 对应
    /// 水地形（兼容 mod 自定义水地形）。
    ///
    /// **调用时机**：必须在 void 铺设（<see cref="SeamlessTerrainFill.ApplyPolygonTerrain"/>）之前——
    /// 本类用 <see cref="SeamlessPolygonGeometry.IsCellInPolygon"/> 几何判定六边形内（void 未铺），
    /// 铺的水在六边形外部分随后由 void 覆盖。原版 Coast mutator（MutatorPostTerrain, order=220）先跑，
    /// 本类补铺 Coast 漏掉的海洋边；重叠区都是水，无冲突。
    /// </summary>
    public static class CoastalEdgeFill
    {
        /// <summary>
        /// 对 map 的所有海洋邻居边铺海岸海水。幂等（重铺时水覆盖水）。
        /// </summary>
        public static void Apply(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var settings = RimExodusMod.Settings;
            var deepRatio = settings?.coastalEdgeDeepWaterDistance ?? 0.15f;
            var shallowRatio = settings?.coastalEdgeShallowWaterDistance ?? 0.25f;
            var sandRatio = settings?.coastalEdgeBeachSandDistance ?? 0.35f;
            if (deepRatio <= 0f && shallowRatio <= 0f && sandRatio <= 0f) return; // 都关 = 关闭海岸补铺。
            // 保证单调：sand ≥ shallow ≥ deep。
            if (shallowRatio < deepRatio) shallowRatio = deepRatio;
            if (sandRatio < shallowRatio) sandRatio = shallowRatio;

            var mapSize = map.Size.x;
            var deepThreshold = deepRatio * mapSize;       // 格距。
            var shallowThreshold = shallowRatio * mapSize; // 格距。
            var sandThreshold = sandRatio * mapSize;       // 格距。

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;

            // 世界邻居列表（顺序与多边形顶点环绕一致：边 j ↔ 邻居 j）。
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);

            // 标记海洋边（邻居 PrimaryBiome == Ocean）+ 收集这些边的顶点对。
            var oceanEdges = new List<(Vector2 v0, Vector2 v1)>();
            for (var j = 0; j < verts.Count && j < worldNeighbors.Count; j++)
            {
                var neighborTile = Find.WorldGrid[worldNeighbors[j]];
                if (neighborTile.PrimaryBiome == BiomeDefOf.Ocean)
                {
                    oceanEdges.Add((verts[j], verts[(j + 1) % verts.Count]));
                }
            }
            if (oceanEdges.Count == 0) return; // 内陆地块，无海洋邻居。

            // Perlin 位移噪声（抖动海岸线，参考 Coast mutator 的 AddDisplacementNoise）。
            // 用两路 Perlin 分别给 x/z 加位移，让到边距离的判定边界不规则。
            var dispFreq = 0.02f;
            var dispStrength = mapSize * 0.04f; // 位移幅度 ~4% 地图边长。
            var seedA = Rand.Int;
            var seedB = Rand.Int + 1;
            var dispX = new Perlin(dispFreq, 2.0, 0.5, 4, seedA, QualityMode.Medium);
            var dispZ = new Perlin(dispFreq, 2.0, 0.5, 4, seedB, QualityMode.Medium);

            var terrainGrid = map.terrainGrid;
            var size = map.Size;
            var verbose = settings?.verboseLogging ?? false;
            var deepCount = 0;
            var shallowCount = 0;
            var sandCount = 0;

            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    // 只铺六边形内格（边外会被 void 覆盖，无需处理）。
                    if (!SeamlessPolygonGeometry.IsCellInPolygon(verts, size.x, cell)) continue;

                    var cellCenter = new Vector2(x + 0.5f, z + 0.5f);

                    // 到最近海洋边的浮点距离。
                    var dMin = float.MaxValue;
                    for (var i = 0; i < oceanEdges.Count; i++)
                    {
                        var d = SeamlessPolygonGeometry.DistanceToEdge(cellCenter, oceanEdges[i].v0, oceanEdges[i].v1);
                        if (d < dMin) dMin = d;
                    }

                    // Perlin 位移：把格中心按噪声偏移后再判距离，海岸线不规则。
                    var offsetX = (float)dispX.GetValue(cellCenter);
                    var offsetZ = (float)dispZ.GetValue(cellCenter);
                    var displaced = dMin + (offsetX + offsetZ) * dispStrength * 0.5f;

                    // 三档地形（完全对齐 Coast mutator 的 CoastTerrainAt 语义，只是用距离替代 noise 值）：
                    //   < deepThreshold → 深水；< shallowThreshold → 浅水；< sandThreshold && ShouldGenerateBeachSand → 沙滩。
                    TerrainDef coastDef = null;
                    if (displaced < deepThreshold)
                    {
                        coastDef = MapGenUtility.DeepOceanWaterTerrainAt(cell, map);
                    }
                    else if (displaced < shallowThreshold)
                    {
                        coastDef = MapGenUtility.ShallowOceanWaterTerrainAt(cell, map);
                    }
                    else if (displaced < sandThreshold && MapGenUtility.ShouldGenerateBeachSand(cell, map))
                    {
                        coastDef = MapGenUtility.BeachTerrainAt(cell, map);
                    }

                    if (coastDef == null) continue;

                    var current = cell.GetTerrain(map);
                    if (current == null) continue;
                    // 不重复铺（已是该地形）。
                    if (current == coastDef) continue;

                    // 覆盖判定（完全照搬 Coast mutator GeneratePostTerrain:63）：
                    //   当前地形非 Stone → 可覆盖（铺水/沙）；
                    //   当前地形是 Stone → 仅当"无 edifice 且新地形是水"才覆盖（无建筑的石头山可被水淹没，沙不覆盖石头）。
                    // 此处 void 未铺，判定对普通土/水/石成立。
                    if (current.categoryType == TerrainDef.TerrainCategoryType.Stone
                        && !(cell.GetEdifice(map) == null && coastDef.IsWater))
                    {
                        continue;
                    }

                    terrainGrid.SetTerrain(cell, coastDef);
                    if (coastDef.IsWater)
                    {
                        if (displaced < deepThreshold) deepCount++; else shallowCount++;
                    }
                    else sandCount++;
                }
            }

            if (verbose)
                Log.Message($"[RimExodus] CoastalEdgeFill wt={worldTile} map={map.uniqueID} oceanEdges={oceanEdges.Count} " +
                    $"deep={deepThreshold:F1} shallow={shallowThreshold:F1} sand={sandThreshold:F1} " +
                    $"deepCells={deepCount} shallowCells={shallowCount} sandCells={sandCount}");
        }
    }
}
