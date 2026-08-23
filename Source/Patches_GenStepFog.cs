using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 揭雾分径（2026-08 用户定夺，替代已删除的自搓 genStep RimExodus_SeamOriginUnfog——"新生成的
    /// 地图的 fog 应该且只应该从接缝处 unfog"，不自建第二套清除、也不与原版中心揭雾并存）：
    ///
    /// - **初始地图**（游戏开始、扎营、定居、逆重飞船降落等原生入口）→ 放行原版体
    ///   （PlayerStartSpot 中心 FloodUnfog / UnfogMapFromEdge fallback / rootsToUnfog——现状不变）。
    /// - **邻接生成图**（预加载链：IncrementalMapGenerator 分帧 ∨ Settlement 原生同步预加载）→
    ///   接管原版体：全雾 → 全图 void 格直接揭雾（void 无内容非探索对象，雾留 void 唯一效果是
    ///   跨缝看邻图被 fog mesh 挡视线）→ 从**生成方向**共享边（<see cref="SeamlessTileManager.NeighborGenerationSourceTile"/>，
    ///   容错校验失败回退全部活跃边）的可站立接缝格发起 <see cref="FloodFillerFog.FloodUnfog"/>——
    ///   洪水揭穿整个室外连通域、停在围墙房间（室内留雾待探索）；被山分隔的其他室外连通域经
    ///   回退边或多边发根覆盖。rootsToUnfog 镜像原版处理（增量链应为空，防御）。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Fog), nameof(GenStep_Fog.Generate))]
    static class Patch_GenStep_Fog_SeamOrigin
    {
        static bool Prefix(Map map)
        {
            var isNeighborGeneration = IncrementalMapGenerator.IsGenerating(map)
                || SeamlessTileManager.GeneratingSettlementSeamlessly;
            if (!isNeighborGeneration) return true; // 初始地图/原生入口：原版中心揭雾。

            // ===== 接管原版体（邻接生成：fog 只从生成方向的接缝开始揭）=====
            // 等价原版首行 SetAllFogged（internal 不可访问，用公开的 Refog(WholeMap) 替代）。
            map.fogGrid.Refog(CellRect.WholeMap(map));

            // ① 全图 void 格直接揭雾（方向无关——跨缝视野要透过 void 带）。
            var unfoggedVoid = 0;
            var voidDef = VoidTerrainDef;
            if (voidDef != null)
            {
                var terrainGrid = map.terrainGrid;
                foreach (var cell in map.AllCells)
                {
                    if (terrainGrid.TerrainAt(cell) == voidDef)
                    {
                        map.fogGrid.Unfog(cell);
                        unfoggedVoid++;
                    }
                }
            }

            // ② 生成方向（容错回退全部活跃边）从接缝格发起室外洪水。
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var floodedEdges = 0;
            if (worldTile >= 0)
            {
                var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
                if (verts != null && verts.Count >= 3)
                {
                    var worldNeighbors = new List<PlanetTile>();
                    Find.WorldGrid.GetTileNeighbors(new PlanetTile(worldTile), worldNeighbors);

                    var sourceTile = SeamlessTileManager.NeighborGenerationSourceTile;
                    var sourceValid = false;
                    foreach (var n in worldNeighbors)
                    {
                        if (n.tileId == sourceTile)
                        {
                            sourceValid = true;
                            break;
                        }
                    }

                    foreach (var neighborTile in worldNeighbors)
                    {
                        if (neighborTile.tileId == worldTile) continue;
                        // 优先只揭生成方向；static source 失效（异常悬挂/非本图）时回退：对端活跃的边都发根。
                        if (sourceValid)
                        {
                            if (neighborTile.tileId != sourceTile) continue;
                        }
                        else if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile.tileId, out _)) continue;

                        var edgeIdx = WorldTileGeometry.FindNeighborIndex(worldTile, neighborTile.tileId);
                        if (edgeIdx < 0 || edgeIdx >= verts.Count) continue;
                        var root = FindStandableRootOnEdge(map, verts[edgeIdx], verts[(edgeIdx + 1) % verts.Count]);
                        if (!root.IsValid) continue;
                        FloodFillerFog.FloodUnfog(root, map);
                        floodedEdges++;
                    }
                }
            }

            // ③ rootsToUnfog 镜像原版处理（防御——增量链 Start 已重置，理论为空）。
            var roots = MapGenerator.rootsToUnfog;
            for (var i = 0; i < roots.Count; i++)
            {
                FloodFillerFog.FloodUnfog(roots[i], map);
                map.fogGrid.Unfog(roots[i]);
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] GenStep_Fog (seam-origin, map={map.uniqueID} wt={worldTile}): voidCells={unfoggedVoid} " +
                            $"outdoorFloodRoots={floodedEdges} source={SeamlessTileManager.NeighborGenerationSourceTile} " +
                            $"(initial maps keep vanilla center unfog; neighbor-generated maps unfog from the seam only).");
            return false;
        }

        /// <summary>在该边线段附近找一个可站立的接缝格作洪水根（无则 Invalid——该边留雾）。</summary>
        private static IntVec3 FindStandableRootOnEdge(Map map, Vector2 edgeA, Vector2 edgeB)
        {
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return IntVec3.Invalid;

            var mid = (edgeA + edgeB) * 0.5f;
            var center = new Vector2(map.Size.x / 2f, map.Size.z / 2f);
            var inward = (center - mid).normalized;
            var best = IntVec3.Invalid;
            var bestDist = float.MaxValue;
            var spots = map.listerThings.ThingsOfDef(enterSpotDef);
            foreach (var spot in spots)
            {
                var p = spot.Position;
                if (!p.Standable(map)) continue;
                // 只认属于该边的 spot（到边线段距离阈值，越过即归邻边）。
                if (DistToSegment(new Vector2(p.x + 0.5f, p.z + 0.5f), edgeA, edgeB) > 6f) continue;
                var d = Vector2.Distance(new Vector2(p.x, p.z), mid - inward * 3f);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>点到线段距离（2D 地图坐标）。</summary>
        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var sqrLen = ab.sqrMagnitude;
            if (sqrLen < 1e-6f) return Vector2.Distance(p, a);
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLen);
            return Vector2.Distance(p, a + ab * t);
        }

        private static TerrainDef _cachedVoidDef;
        private static TerrainDef VoidTerrainDef
        {
            get
            {
                if (_cachedVoidDef == null)
                    _cachedVoidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
                return _cachedVoidDef;
            }
        }
    }
}
