using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// Debug dirt/water 模式专用 genStep（阶段4 连续地形诊断基础设施）。
    ///
    /// 这个 genStep 是一个纯粹的"噪声显示器"：
    /// - 它不自己实现坐标变换，而是传入 cell-local 坐标 (x, 0, z) 给 Perlin，
    ///   由 <see cref="Patches_NoiseLeafWarp"/> 统一做坐标变换（与正常 ElevationFertility 完全相同的路径）。
        /// - noise &gt; 阈值 → 铺 Soil（泥土），否则 → 铺 WaterOceanDeep（深水）。
        /// - 直接写 terrainGrid + elevation grid，绕开 ElevationFertility/Terrain 等多层 genStep，
        ///   输出纯粹的噪声二值图（"噪声显示器"）。
        /// - 后续 RimExodus_SeamlessTile genStep 仍会跑，把六边形外裁成 void。
    ///
    /// 配置：通过 RimExodus_DebugDirtWaterGenerator MapGeneratorDef 启用（只含本 genStep + void 裁切 + Fog）。
    /// noiseGenType 仍生效——切换 SphereNormal/LatLong/DiagnosticRings/Off 可显示对应模式的噪声输出。
    ///
    /// 诊断：生成后调 <see cref="RimExodusDebug.LogDirtWaterTile"/> 输出该地块的完整几何信息。
    /// </summary>
    public class GenStep_DebugDirtWater : GenStep
    {
        /// <summary>dirt/water 二值化阈值。noise &gt; 阈值 = dirt，否则 = water。</summary>
        private const float DirtWaterThreshold = 0f;

        public override int SeedPart => 82650469;

        public override void Generate(Map map, GenStepParams parms)
        {
            // 只对 RimExodus 地块地图生效（非 RimExodus 地图不应使用此 genStep，但防御性检查）。
            if (!(map.Parent is MapParent_SeamlessTile seamlessParent) || seamlessParent.worldTile < 0)
            {
                Log.Warning("[RimExodus] GenStep_DebugDirtWater: map has no valid worldTile, skipping.");
                return;
            }

            var worldTile = seamlessParent.worldTile;
            var mapSize = map.Size.x;

            // 固定 Perlin（与正常管线相同的 seed）。注意：
            // - 它的 GetValue 会被 Patches_NoiseLeafWarp 拦截（gate: mapBeingGenerated is RimExodus）。
            // - 我们传入 cell-local (x, 0, z)，patch 做 TryWarp → 全局平面坐标（cellLocal - center + tileOrigin）。
            // - 这样 debug 模式与正常 ElevationFertility 走完全相同的坐标变换路径。
            var perlin = new Perlin(0.02, 2.0, 0.5, 3, SeamlessNoiseProvider.FixedSeed, QualityMode.High);

            var elevGrid = MapGenerator.Elevation;
            var fertGrid = MapGenerator.Fertility;

            // 多边形顶点（用于诊断日志）。
            var polygonVerts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);

            // 直接写 terrainGrid（绕开 Terrain genStep）。
            // 参考 SeamlessTerrainFill.ApplyPolygonTerrain 的模式：直接写 topGrid + MapMeshDirty。
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid; // public TerrainDef[]（TerrainGrid.cs:13）
            var mapDrawer = map.mapDrawer;
            var dirtDef = TerrainDefOf.Soil;
            var waterDef = TerrainDefOf.WaterOceanDeep;

            // 采样点（四角 + 中心）用于诊断日志。
            var cornerSamples = new List<RimExodusDebug.CornerSample>();
            var sampleCells = new[]
            {
                (0, 0),
                (mapSize - 1, 0),
                (0, mapSize - 1),
                (mapSize - 1, mapSize - 1),
                (mapSize / 2, mapSize / 2)
            };
            var center = mapSize * 0.5f;

            foreach (var cell in map.AllCells)
            {
                // cell-local 坐标传给 Perlin；patch 会 warp 成球面坐标。
                var noise = perlin.GetValue(cell.x, 0, cell.z);

                bool isDirt = noise > DirtWaterThreshold;
                if (isDirt)
                {
                    elevGrid[cell] = 0.9f; // 平地
                    fertGrid[cell] = 0.5f;
                    topGrid[cellIndices.CellToIndex(cell)] = dirtDef;
                }
                else
                {
                    elevGrid[cell] = -1f; // 深水
                    fertGrid[cell] = 0f;
                    topGrid[cellIndices.CellToIndex(cell)] = waterDef;
                }
                mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);

                // 收集采样点信息（四角 + 中心）。
                for (var i = 0; i < sampleCells.Length; i++)
                {
                    var (sx, sz) = sampleCells[i];
                    if (cell.x == sx && cell.z == sz)
                    {
                        // 全局平面采样坐标（与 TryWarp 相同）。
                        var gx = cell.x - center + seamlessParent.tileOrigin.x;
                        var gz = cell.z - center + seamlessParent.tileOrigin.y;
                        cornerSamples.Add(new RimExodusDebug.CornerSample
                        {
                            cellX = cell.x,
                            cellZ = cell.z,
                            offX = cell.x - center,
                            offZ = cell.z - center,
                            globalX = gx,
                            globalZ = gz,
                            noise = noise,
                            elev = elevGrid[cell]
                        });
                    }
                }
            }

            // 诊断日志（debug 模式下强制输出，不受 verboseLogging 控制）。
            RimExodusDebug.LogDirtWaterTile(worldTile, mapSize, seamlessParent.tileOrigin, DirtWaterThreshold, polygonVerts, cornerSamples);
        }
    }
}
