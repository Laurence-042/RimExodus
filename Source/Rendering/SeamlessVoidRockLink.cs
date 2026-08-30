using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// void 边界岩石的隐形 link 延续体铺设（2026-08 视觉优化，用户需求"对侧有岩石时
    /// 本侧 void 边界岩不该显示断崖"）。
    ///
    /// 机理：自然岩的 atlas 选片由 <c>Graphic_Linked.LinkedDrawMatFrom</c> 查本图 4 邻
    /// <c>LinkGrid.LinkFlagsAt</c> 决定——void 格上岩石被 389 清掉 → flag None →
    /// 边界岩格选带断崖侧面的子材质。修复 = 在 void 格上 spawn
    /// <c>RimExodus_VoidRockLink</c>（drawerType=None 不渲染、只向 LinkGrid 提供 Rock flag，
    /// Spawn 自动以 regenAdjacentCells 脏化邻格 Things mesh），边界岩即选"连续"片。
    ///
    /// **挂接时机 = genStep 392 末（<see cref="GenStep_SeamOverride.Generate"/> 混合之后、
    /// 条带快照捕获之后）**：紧贴 void 的那圈接缝带岩石多数是 392 混合 B 照抄
    /// SyncRockBuildingTo 才 spawn 的（原生无岩），389 时点"4 邻含岩石"条件全灭一格都铺不出
    /// （2026-08 实测教训，首版曾挂 389）；own baseBuildingSnapshot 虽非序列化但生成期仍在
    /// 内存可读，双分支判据不受挂接时点影响。**单向原则（用户定夺 2026-08，勿回退）**：
    /// 一切决策发生在本图自己的生成期内，不做任何"对端生成后回头改本图"的回铺
    /// （首版 propagate 方案已被用户否决）。
    ///
    /// 铺设规则（用户逻辑 2026-08，四条闭环）：
    /// 1. **范围 = 邻接接缝带的 void**（接缝带外侧切比雪夫距离 1 = OuterStripDepth==1 那一圈），
    ///    本地不做岩石邻接过滤——放不放只看参考源；
        /// 2. **判据 = 参考源镜像格的 3×3（切比雪夫 1）窗口内有岩**（对角邻也参与 rock 贴图角部，
        ///    与混合权重衰减的切比雪夫口径一致）：
        ///    - 归属邻居（按最近多边形边，"边 j ↔ 邻居 j"架构）**已生成** → 对端条带快照 building 层
        ///      镜像格 a = c − offset 的 3×3（外条带存对端 389 清理前的原生岩体，对端图无需活跃）；
        ///    - **未生成** → own baseBuildingSnapshot（本图 389 备份的清理前原生岩体）镜像位 3×3
        ///      ——本侧被截断的 rock 只能按本侧 snapshot 假设延续（初始家园图场景）；
        /// 3. **复刻闭环**：own-snapshot 假设要求后生成的对端在对应位置同样有岩——由 SeamOverride
        ///    的照抄区（B ∪ {T·1}）承担：本图 T depth=1 与对侧 OuterStrip depth=1 是同一条空间带，
        ///    对端在此字面照抄本侧原生岩体（外条带段快照）= 先生成侧假设成真（含真实山体延续，
        ///    2026-08 实测确认该复刻是设计语义——曾误删致延续山体断裂，已恢复）；
        /// 4. 对应关系（距缝切比雪夫距离）：0 = 两图离散边互为镜像；1 = 本图 Inner/OuterRing ↔
        ///    对侧 Outer/InnerRing（空间重叠）；2 = 本图 T·1 ↔ 对侧 OuterStrip·1（本条铺设带）。
    ///
    /// 已知限制（用户接受，观察项）：分支假设与后来实际生成的对端不一致时（如按 own snapshot
    /// 铺了 link 但对端该处无岩，或对端岩石后被开采），该处显示连续（视觉滞后，无功能影响）。
    /// 不追对端动态变化。
    /// </summary>
    internal static class SeamlessVoidRockLink
    {
        // 一致性采样窗口 = 原版权威数组 GenAdj.AdjacentCellsAndInside（含中心 9 格 = 切比雪夫 1，
        // SeamlessGridMath 统一口径，勿自建）。
        private static readonly IntVec3[] _window1 = GenAdj.AdjacentCellsAndInside;

        /// <summary>per-邻居参考（offset 现算与邻居表登记同公式恒等；strip 为 null = 该邻居未生成）。</summary>
        private struct NeighborRef
        {
            public int worldTile;
            public IntVec3 offset;
            public SeamStripData strip;
        }

        private static readonly List<NeighborRef> _neighborRefs = new();

        /// <summary>
        /// 392 末调用（<see cref="GenStep_SeamOverride.Generate"/> 混合完成后，快照捕获之后——
        /// 挂点理由见类注释：392 照抄才 spawn 的接缝带岩石是"4 邻含岩石"条件的主角）。
        /// </summary>
        public static void PlaceAfterSeamOverride(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return; // 预览图不 spawn thing
            var linkDef = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_VoidRockLink");
            if (linkDef == null) return;
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null) return;

            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x);
            if (band.OuterStripDepth.Count == 0) return;

            // own 原生岩体快照（389 备份于清岩之前）——邻居未生成时的延续判据。
            var ownBuildingSnapshot = SeamlessMapData.GetBaseBuildingSnapshot(map);

            // 邻居参考（含未生成者——strip 留 null 走 own snapshot 分支；按最近边归属时要用
            // 全部世界邻居，不能像 SeamOverride 那样只收已生成的）。
            if (!CollectNeighborRefs(map, worldTile)) return;

            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            var placed = 0;
            // 范围 = 邻接接缝带的 void（OuterStripDepth==1 那一圈，距缝切比雪夫 2 的空间带）。
            foreach (var kv in band.OuterStripDepth)
            {
                if (kv.Value != 1) continue;
                var cell = kv.Key;

                // 实况校验：外条带应全为 void（几何与铺设同口径，防御异常态）。
                if (map.terrainGrid.TerrainAt(cell) != voidDef) continue;

                // 幂等查重。
                var existing = map.thingGrid.ThingsListAtFast(cell);
                var hasLink = false;
                for (var i = 0; i < existing.Count; i++)
                {
                    if (existing[i].def == linkDef) { hasLink = true; break; }
                }
                if (hasLink) continue;

                // 条件 2：岩体延续判据。按最近边归属邻居（找不到边的顶点楔形区退化为
                // 逐邻居探测，任一命中即铺——与已生成分支语义一致）。
                var edgeIdx = SeamlessPolygonGeometry.FindClosestEdgeIndex(verts, cell.x + 0.5f, cell.z + 0.5f);
                var decided = false;
                if (edgeIdx >= 0 && edgeIdx < _neighborRefs.Count)
                {
                    var nref = _neighborRefs[edgeIdx];
                    decided = nref.strip != null
                        ? NeighborHasRockAt(nref, cell)
                        : OwnSnapshotHasRockAt(ownBuildingSnapshot, map, cell);
                }
                else
                {
                    for (var i = 0; i < _neighborRefs.Count && !decided; i++)
                    {
                        var nref = _neighborRefs[i];
                        decided = nref.strip != null
                            ? NeighborHasRockAt(nref, cell)
                            : OwnSnapshotHasRockAt(ownBuildingSnapshot, map, cell);
                    }
                }

                if (decided)
                {
                    GenSpawn.Spawn(ThingMaker.MakeThing(linkDef), cell, map);
                    placed++;
                }
            }

            if (placed > 0 && (RimExodusMod.Settings?.verboseLogging ?? false))
                Log.Message($"[RimExodus] VoidRockLink: placed {placed} invisible rock linkers on map {map.uniqueID} (wt={worldTile}).");
        }

        /// <summary>
        /// 收集全部世界邻居到 <see cref="_neighborRefs"/>（顺序与多边形边环绕一致，索引即边索引）。
        /// 与 EnterSpotPlacer 的邻居枚举同款；strip 命中失败（未生成）保留 null 占位。
        /// </summary>
        private static bool CollectNeighborRefs(Map map, int worldTile)
        {
            _neighborRefs.Clear();
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            if (worldNeighbors.Count == 0) return false;
            foreach (var nt in worldNeighbors)
            {
                var neighborTile = nt.tileId;
                if (neighborTile == worldTile) { _neighborRefs.Clear(); return false; } // 防御：序结构异常
                SeamStripData strip = null;
                if (SeamlessTileGraph.TryGetNeighborSeamStrip(neighborTile, out var s)
                    && s != null && s.buildingLookup != null && s.buildingLookup.Count > 0)
                {
                    strip = s;
                }
                var offset = strip != null
                    ? SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTile, map)
                    : IntVec3.Zero;
                if (strip != null && offset == IntVec3.Zero) strip = null; // offset 算不出 = 参考不可用，退 own 分支
                _neighborRefs.Add(new NeighborRef { worldTile = neighborTile, offset = offset, strip = strip });
            }
            return _neighborRefs.Count > 0;
        }

        /// <summary>
        /// 对端分支：strip building 层镜像格 a = c − offset 的**3×3（切比雪夫 1）邻域**内有岩
        /// （外条带 = 对端 389 前原生岩体）。3×3 窗口与本地条件 1 的 8 邻候选口径一致——
        /// 本地按对角判候选、对端只查镜像单格会错位。
        /// </summary>
        private static bool NeighborHasRockAt(NeighborRef nref, IntVec3 cell)
        {
            var aX = cell.x - nref.offset.x;
            var aZ = cell.z - nref.offset.z;
            for (var i = 0; i < _window1.Length; i++)
            {
                var probe = new IntVec3(aX + _window1[i].x, 0, aZ + _window1[i].z);
                if (nref.strip.buildingLookup.TryGetValue(probe, out var rockDef) && rockDef != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>own 分支：本图 389 备份的清理前原生岩体在 c 的 3×3 邻域内有岩（邻居未生成时的延续假设）。</summary>
        private static bool OwnSnapshotHasRockAt(ThingDef[] ownBuildingSnapshot, Map map, IntVec3 cell)
        {
            if (ownBuildingSnapshot == null) return false;
            var w = map.Size.x; var h = map.Size.z;
            for (var i = 0; i < _window1.Length; i++)
            {
                var x = cell.x + _window1[i].x;
                var z = cell.z + _window1[i].z;
                if (x < 0 || z < 0 || x >= w || z >= h) continue;
                if (ownBuildingSnapshot[map.cellIndices.CellToIndex(new IntVec3(x, 0, z))] != null) return true;
            }
            return false;
        }
    }
}
