using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝方向揭雾（order 1505，Fog(1500) 之后；2026-08 用户设计"从接缝开始解除所有室外迷雾"——
    /// 首版 ±15 平带是错误理解，已按用户纠正改为洪水语义）。
    ///
    /// 原版 <c>GenStep_Fog</c> 对所有图全雾后，仅从 <c>MapGenerator.PlayerStartSpot</c>（392 预设 =
    /// 六边形中心）做 <c>FloodFillerFog.FloodUnfog</c>——洪水会被围墙挡住：无墙的地块图洪水铺满全图
    /// （历史无感），有墙的据点图只剩中心房间，跨缝看邻图全黑、雾中的贸易商不可点击。
    ///
    /// 本步两件事：
    /// ① **每条"对端已有活跃图"的共享边，从其接缝格发起 FloodUnfog**——洪水穿过整个室外连通域
    ///    并停在围墙房间（与原版中心洪水同款机制），即"从接缝解除所有室外迷雾"；室内留雾待探索。
    ///    多条活跃边各自发根，天然覆盖被山体分隔的多个室外连通域。
    /// ② **全图 void 格直接揭雾**（方向无关）——void 无内容非探索对象，雾留在 void 上唯一的
    ///    效果是跨缝看邻图时被 fog mesh 挡视线（首版平带特意含 void 即为此，现改为全揭更本质）。
    /// 玩家未到过的方向：室外雾保留（无活跃边 → 无洪水根），探索感不破坏。
    /// 无活跃邻居（首图/世界图入口生成）时仅 ② 生效，揭雾主体由原版中心洪水兜底。
    /// </summary>
    public class GenStep_SeamOriginUnfog : GenStep
    {
        public override int SeedPart => 743197811;

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

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            // ① void 全揭（视觉必需——雾留 void 只会在跨缝看邻图时挡视线）。
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

            // ② 收集"对端活跃"的共享边（边索引 = 世界邻居序号），每边从可站立的接缝格发起室外洪水。
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            if (verts == null || verts.Count < 3)
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] SeamOriginUnfog: map={map.uniqueID}(wt={worldTile}) voidOnly (no polygon): unfogged {unfoggedVoid} void cells.");
                return;
            }

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(new PlanetTile(worldTile), worldNeighbors);
            var floodedEdges = 0;
            foreach (var neighborTile in worldNeighbors)
            {
                if (neighborTile.tileId == worldTile) continue;
                if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile.tileId, out _)) continue; // 玩家未到过的方向保持雾。
                var edgeIdx = WorldTileGeometry.FindNeighborIndex(worldTile, neighborTile.tileId);
                if (edgeIdx < 0 || edgeIdx >= verts.Count) continue;

                // 该边半平面内取一个可站立的接缝格作洪水根（spot 铺设无通行性过滤，可能落在水上）。
                var root = FindStandableRootOnEdge(map, verts[edgeIdx], verts[(edgeIdx + 1) % verts.Count]);
                if (!root.IsValid) continue;
                FloodFillerFog.FloodUnfog(root, map);
                floodedEdges++;
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] SeamOriginUnfog: map={map.uniqueID}(wt={worldTile}) voidCells={unfoggedVoid} " +
                            $"outdoorFloodRoots={floodedEdges} (flood reveals all outdoor connected regions from active seam edges; indoor rooms stay fogged).");
        }

        /// <summary>在该边线段附近找一个可站立的接缝格作洪水根（就近接缝圈格扫描；无则 Invalid——该边留雾）。</summary>
        private static IntVec3 FindStandableRootOnEdge(Map map, Vector2 edgeA, Vector2 edgeB)
        {
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return IntVec3.Invalid;

            // 边中点向地图中心方向收缩一圈，在附近找最近的接缝格（接缝圈贴边，收缩 2 格内必有）。
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
                // 只认属于该边的 spot：spot 到边线段的距离 < 到其他边的距离（用最近边身份判定）。
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
    }
}
