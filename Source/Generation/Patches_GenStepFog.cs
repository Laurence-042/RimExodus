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
    /// - **邻接生成图**（预加载链：IncrementalMapGenerator 分帧 ∨ POI 原生同步预加载）→
    ///   接管原版体：全雾 → 全图 void 格直接揭雾（void 无内容非探索对象，雾留 void 唯一效果是
    ///   跨缝看邻图被 fog mesh 挡视线）→ 对**生成方向**共享边（<see cref="SeamlessTileManager.NeighborGenerationSourceTile"/>）
    ///   的**全部接缝格逐个**发起 <see cref="FloodFillerFog.FloodUnfog"/>（不预筛 Standable/地形：
    ///   原版本体只被 MakeFog edifice 阻断，深水应当可揭；2026-08 多根化：
    ///   每个室外连通域需要至少一个洪水根——山体延伸到接缝带把源边室外区分割成多段"走廊"时，
    ///   单最优根只揭穿根所在的那一段，其余段即使从接缝可直接走入也留雾；全 spots 发根天然覆盖
    ///   各段，同连通域的后续洪水因 PassCheck 要求 fogged 立即空转，开销可忽略。同款原版口径
    ///   继承：洪水只被实心 edifice[Fillage Full] 阻挡，水/关着的门挡不住、且为 4 邻遍历——被水
    ///   围住的谷地会被揭雾、只留对角缝的墙缝挡洪水，属原版语义不改）。源边没有 spot 或逐根
    ///   调用后实际揭雾数仍为 0（整边均被 MakeFog edifice 阻挡）时，降级为**全部活跃边**
    ///   （休眠过滤口径）再发根。洪水揭穿整个室外连通域、停在围墙房间（室内留雾待探索）。
    ///   rootsToUnfog 镜像原版处理（增量链应为空，防御）。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Fog), nameof(GenStep_Fog.Generate))]
    static class Patch_GenStep_Fog_SeamOrigin
    {
        static bool Prefix(Map map)
        {
            var isNeighborGeneration = IncrementalMapGenerator.IsGenerating(map)
                || SeamlessTileManager.GeneratingNativeSeamlessly;
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

            // ② 生成方向的全部有效接缝格逐个发根（通常要求 Standable；Ocean 内置补全允许深海格；
            //    多根化：每个被山分隔的室外连通域各需一个根，
            //    同连通域的后续洪水因 PassCheck 要求 fogged 立即空转天然去重）；源边零根或 source
            //    校验失败时降级为全部活跃边（休眠过滤口径）再收集一轮。
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var rootCount = 0;
            var cellsUnfoggedFromRoots = 0;
            var roots = new List<IntVec3>();
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
                        CollectRootsOnEdge(map, verts[edgeIdx], verts[(edgeIdx + 1) % verts.Count], roots);
                    }

                    // 先让原版 FloodUnfog 自己判断来源边：它只被 MakeFog edifice 阻断，不看地形
                    // Standable，所以深海水等特殊主体通路可以正常揭雾。
                    cellsUnfoggedFromRoots += FloodRoots(roots, map, ref rootCount);

                    // 来源边没有 spot，或所有根实际都被 MakeFog edifice 堵死 → 降级其他活跃边。
                    // 不能再用 roots.Count 判断：几何 spot 存在不代表 FloodUnfog 真揭开了任何格。
                    if (cellsUnfoggedFromRoots == 0 && sourceValid)
                    {
                        roots.Clear();
                        foreach (var neighborTile in worldNeighbors)
                        {
                            if (neighborTile.tileId == worldTile || neighborTile.tileId == sourceTile) continue;
                            if (!SeamlessTileGraph.TryGetMapByWorldTile(neighborTile.tileId, out _)) continue;
                            var edgeIdx = WorldTileGeometry.FindNeighborIndex(worldTile, neighborTile.tileId);
                            if (edgeIdx < 0 || edgeIdx >= verts.Count) continue;
                            CollectRootsOnEdge(map, verts[edgeIdx], verts[(edgeIdx + 1) % verts.Count], roots);
                        }
                        cellsUnfoggedFromRoots += FloodRoots(roots, map, ref rootCount);
                    }
                }
            }

            // ③ rootsToUnfog 镜像原版处理（防御——增量链 Start 已重置，理论为空）。
            var vanillaRoots = MapGenerator.rootsToUnfog;
            for (var i = 0; i < vanillaRoots.Count; i++)
            {
                FloodFillerFog.FloodUnfog(vanillaRoots[i], map);
                map.fogGrid.Unfog(vanillaRoots[i]);
            }

            // ④ 前哨保留·封存区域揭雾（2026-09）：zone = 原 home area 字面快照 = 封存时的已探索区
            //（玩家住过的地方）。GenStep_ZoneRestore(1100) 在本步之前恢复了建筑——接缝洪水被
            // 恢复的墙正确阻挡（密封房间留雾 = 原版一致）；但区域整体本是已探索区，此处整区补揭
            // 恢复原探索态。记录此时尚未被消费（onComplete 的 FinishZoneRestore 才清），直接读。
            var zoneUnfogged = 0;
            var preserveRecord = (map.Parent as MapParent_SeamlessTile)?.preserveRecord;
            if (preserveRecord != null)
            {
                foreach (var c in preserveRecord.zoneCells)
                {
                    if (map.fogGrid.IsFogged(c))
                    {
                        map.fogGrid.Unfog(c);
                        zoneUnfogged++;
                    }
                }
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                Log.Message($"[RimExodus] GenStep_Fog (seam-origin, map={map.uniqueID} wt={worldTile}): voidCells={unfoggedVoid} " +
                            $"outdoorFloodRoots={rootCount} cellsUnfoggedFromRoots={cellsUnfoggedFromRoots} " +
                            $"preserveZoneUnfogged={zoneUnfogged} source={SeamlessTileManager.NeighborGenerationSourceTile} " +
                            $"(initial maps keep vanilla center unfog; neighbor-generated maps unfog from the seam only; " +
                            $"roots = all enter-spots on the source edge, fallback = all active edges if none actually unfog).");
            return false;
        }

        /// <summary>
        /// 收集该边线段附近的全部接缝格作洪水根（不预筛 Standable/地形，交给原版 FloodUnfog
        /// 的 MakeFog edifice 判据。多根：山体延伸到接缝带把源边室外区
        /// 分割成多段时各段各需一个根；同连通域的后续洪水因 PassCheck 要求 fogged 立即空转）。
        /// </summary>
        private static void CollectRootsOnEdge(Map map, Vector2 edgeA, Vector2 edgeB, List<IntVec3> roots)
        {
            var enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (enterSpotDef == null) return;

            var spots = map.listerThings.ThingsOfDef(enterSpotDef);
            foreach (var spot in spots)
            {
                var p = spot.Position;
                // 只认属于该边的 spot（到边线段距离阈值，越过即归邻边）。
                if (DistToSegment(new Vector2(p.x + 0.5f, p.z + 0.5f), edgeA, edgeB) > 6f) continue;
                roots.Add(p);
            }
        }

        /// <summary>逐根调用原版洪水并返回实际揭雾格总数；已被前一根揭开的连通域会立即空转。</summary>
        private static int FloodRoots(List<IntVec3> roots, Map map, ref int rootCount)
        {
            var cellsUnfogged = 0;
            foreach (var root in roots)
            {
                cellsUnfogged += FloodFillerFog.FloodUnfog(root, map).cellsUnfogged;
                rootCount++;
            }
            return cellsUnfogged;
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
