using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 本地图道路 A* 路径快照（MapComponent，非序列化——生成期数据）。
    ///
    /// <c>GenStep_Roads.paths</c> 是 static，每次任何地图的 <c>Generate</c> 开头 <c>paths.Clear()</c>。
    /// 分帧增量生成（IncrementalMapGenerator）下，本地图 Roads(390) 与 SeamOverride(1410) 之间
    /// 隔多帧，期间另一张地图开始生成会把 static 清空。故 Postfix 在 Roads 跑完后立即快照到
    /// map 本地，供 SeamOverride 识别"本地图的道路格"（保护路不被接缝卷积抹掉）。
    /// </summary>
    public class SeamlessRoadPaths : MapComponent
    {
        public readonly List<List<IntVec3>> paths = new();

        public SeamlessRoadPaths(Map map) : base(map) { }
    }

    /// <summary>
    /// Postfix <c>GenStep_Roads.Generate</c>：把 static <c>GenStep_Roads.paths</c>（本地图道路
    /// A* 路径节点）快照到 <see cref="SeamlessRoadPaths"/> 组件。
    /// 所有地图一视同仁（无类型守卫）——没有路的地图快照为空列表，无副作用。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Roads), nameof(GenStep_Roads.Generate))]
    static class Patch_GenStep_Roads_SnapshotPaths
    {
        static void Postfix(Map map)
        {
            var comp = map.GetComponent<SeamlessRoadPaths>();
            if (comp == null)
            {
                comp = new SeamlessRoadPaths(map);
                map.components.Add(comp);
            }
            comp.paths.Clear();
            foreach (var p in GenStep_Roads.paths)
            {
                var copy = new List<IntVec3>(p);
                comp.paths.Add(copy);
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Patch_GenStep_Roads_SnapshotPaths: map={map.uniqueID} snapshotted {comp.paths.Count} road paths.");
        }
    }

    /// <summary>
    /// 道路穿越点对齐：Patch <c>GenStep_Roads.FindRoadExitCell</c>（private），强制把道路出口格
    /// 选在目标方向对应的六边形接缝带上，而非原版方形地图边缘。
    ///
    /// 【为什么需要这个 patch】
    /// <c>GenStep_Roads</c>（order=390）在 void 铺设（<c>RimExodus_SeamlessTile</c>, order=1400）
    /// **之前**执行。此时传送点尚未铺设 → <see cref="SeamlessEdgeCells.HasSeamEdge"/> 返回 false →
    /// <see cref="Patches_CellFinder"/> 放行原版方形边缘 → 道路出口格被选在方形地图边缘（将来多为 void）
    /// → 被 void 覆盖 + 两端不对齐。本 patch 直接用多边形几何算接缝锚点，不依赖传送点，
    /// 解决 order 390 时接缝带尚未存在的问题。
    ///
    /// 【穿越点对齐原理】
    /// 两端（A 和 C）都 patch 了 <c>FindRoadExitCell</c>，各自对自己那条边算锚点。锚点位置由
    /// 多边形几何 + offset 决定——同一世界 road link 在两端算出的角度互补，对应各自的边，
    /// 两端锚点通过 <see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/> 的 offset 对齐。
    /// 道路 A* 寻路从中心到锚点 → 道路画到接缝带 → void 只覆盖六边形外（接缝带在内不受影响）→
    /// SeamOverride 的道路保护（<see cref="SeamlessSeamOverride"/> 读 <see cref="SeamlessRoadPaths"/>）保留路面。
    ///
    /// 【兼容性】只 patch 一个 private 方法 + 一个 Generate Postfix（只读快照，不改行为）。
    /// 若其他 mod 也 patch <c>FindRoadExitCell</c>，Harmony patch 链正常叠加。
    /// 守卫 <c>worldTile &lt; 0</c> 是防御性放行（防异常态），正常地图一律走接缝对齐——
    /// 不加"非 RimExodus 放行"分支（见 AGENTS.md 铁律：所有地图一视同仁）。
    /// </summary>
    static class Patch_GenStep_Roads_FindRoadExitCell
    {
        /// <summary>
        /// Prefix：用多边形几何算目标角度对应边的接缝锚点格，替代原版随机边缘搜索。
        /// 找不到合适锚点或不可达时 return true 放行原版。
        /// </summary>
        internal static bool Prefix(Map map, float angle, IntVec3 crossroads,
            ref RoadPathingDef pathingDef, ref IntVec3 __result)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return true; // 防御性：异常态放行原版

            var mapSize = map.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return true; // 几何异常放行

            // 找 angle 对应的边。road 的 angle = GetHeadingFromTo(me, link邻居)（精确），
            // 用世界邻居 heading 匹配 + 邻居→边精确映射（勿用本地边中点角度近似——60° 离散 +
            // 投影扭曲会锚错边，见 FindEdgeByWorldHeading 注释）。
            var bestEdge = SeamlessPolygonGeometry.FindEdgeByWorldHeading(worldTile, angle);
            if (bestEdge < 0 || bestEdge >= verts.Count) return true;

            // 算该边接缝锚点格（多边形几何，不依赖传送点）。
            var exitCell = SeamlessPolygonGeometry.ComputeSeamCellForEdge(verts, bestEdge, mapSize, map,
                SeamlessTileManager.SeamOverlap);
            if (!exitCell.IsValid || !exitCell.InBounds(map)) return true;

            // 可达性校验，对齐原版 FindRoadExitCell 的两级语义：
            // 第一轮 NoPassClosedDoors（可涉水）→ 第二轮 PassAllDestroyableThings。
            // 不能用 NoPassClosedDoorsOrWater（比原版还严）——锚点被河挡住时会误放行原版，
            // 导致出口落回方形边缘（将来 void 区），路在接缝前断掉。
            if (map.reachability.CanReach(crossroads, exitCell, PathEndMode.OnCell,
                TraverseParms.For(TraverseMode.NoPassClosedDoors)) ||
                map.reachability.CanReach(crossroads, exitCell, PathEndMode.OnCell,
                TraverseParms.For(TraverseMode.PassAllDestroyableThings)))
            {
                __result = exitCell;
                return false; // 跳过原版
            }

            // 不可达放行原版。
            return true;
        }
    }

    /// <summary>
    /// Postfix <c>GenStep_Roads.ApplyDistanceField</c>（private）：接缝锚点附近强制补铺路面材质。
    ///
    /// 【为什么】原版 ApplyDistanceField 的放置是双重随机（曲线概率 × 二次 Rand 抽签），
    /// DirtPath/DirtRoad 中线有效概率仅 ~0.86/0.89——接缝最后 3 格至少一格缺材质的概率
    /// ~36%（5 格 ~53%），土路在接缝处随机断格是常态。StoneRoad 是唯一实心路（mult=0）。
    ///
    /// 【做法】每条路画完后，对"距任一接缝锚点 ≤ <see cref="SeamFillRadius"/> 格且
    /// fromRoad ≤ 1.5（中线±1.5）"的格强制铺 roadDef 的主路面 TerrainDef（跳过概率抽签）。
    ///
    /// 【跳过条件（关键）】
    /// - 水格（尚未铺桥的河格）：过河交给原版 Bridge 步骤（roadGenSteps[0]，先于本 Postfix）。
    /// - <c>terrain.bridge</c>：已铺桥的河格——桥在 foundationGrid（TerrainAt 经 foundation
    ///   遮蔽返回 Bridge），tags 只有 "Floor"（无 Water/Road tag），没有此判据会把桥格
    ///   SetTerrain 成路面：topGrid 从水变土 → 该格不再是水（水体连续性断），桥塌后露出
    ///   河里的路面。与原版 <c>RoadDefGenStep_Place</c> 的 <c>!terrainDef2.bridge</c> 守卫对齐。
    /// - Road tag 地形：已铺上的路面不覆盖。
    ///
    /// 【石路映射】place 为 FlagstoneSandstone 时原版 Place 会替换成区域岩石色（rockDef），
    /// 本 Postfix 同步映射，否则石路接缝补铺出砂岩色而非区域岩色。
    /// </summary>
    static class Patch_GenStep_Roads_ApplyDistanceField
    {
        /// <summary>接缝补铺半径（格，到最近接缝锚点的切比雪夫距离）。</summary>
        private const int SeamFillRadius = 6;

        /// <summary>补铺判定的 fromRoad 阈值（中线 ±1.5 格，覆盖常见路面宽度）。</summary>
        private const float SeamFillFromRoad = 1.5f;

        internal static void Postfix(GenStep_Roads __instance, GenStep_Roads.DistanceElement[,] distance, Map map,
            TerrainDef rockDef, RoadDef roadDef, RoadPathingDef pathingDef)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return; // 防御性：异常态跳过

            // 主路面材质：roadGenSteps 里第一个 place 为 TerrainDef 的 Place 步骤。
            TerrainDef placeTerrain = null;
            foreach (var step in roadDef.roadGenSteps)
            {
                if (step is RoadDefGenStep_Place place && place.place is TerrainDef td && !td.bridge)
                {
                    placeTerrain = td;
                    break;
                }
            }
            if (placeTerrain == null) return;

            // 石路映射：原版 Place 把 FlagstoneSandstone 替换成区域岩石色（rockDef）。
            if (placeTerrain == TerrainDefOf.FlagstoneSandstone && rockDef != null)
            {
                placeTerrain = rockDef;
            }

            // 接缝锚点集：每条多边形边的锚点（与 FindRoadExitCell 同一套几何）。
            var mapSize = map.Size.x;
            var verts = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, mapSize);
            if (verts.Count < 3) return;
            var anchors = new List<IntVec3>();
            for (var j = 0; j < verts.Count; j++)
            {
                var anchor = SeamlessPolygonGeometry.ComputeSeamCellForEdge(verts, j, mapSize, map,
                    SeamlessTileManager.SeamOverlap);
                if (anchor.IsValid) anchors.Add(anchor);
            }
            if (anchors.Count == 0) return;

            var sx = map.Size.x;
            var sz = map.Size.z;
            var filled = 0;
            for (var x = 0; x < sx; x++)
            {
                for (var z = 0; z < sz; z++)
                {
                    var de = distance[x, z];
                    if (!de.touched || de.fromRoad > SeamFillFromRoad) continue;

                    // 距最近接缝锚点 ≤ 半径才补（只补接缝端，不改变路中段的断续质感）。
                    var nearAnchor = false;
                    foreach (var a in anchors)
                    {
                        if (Mathf.Abs(x - a.x) <= SeamFillRadius && Mathf.Abs(z - a.z) <= SeamFillRadius)
                        {
                            nearAnchor = true;
                            break;
                        }
                    }
                    if (!nearAnchor) continue;

                    var cell = new IntVec3(x, 0, z);
                    var terrain = map.terrainGrid.TerrainAt(cell);
                    if (terrain == placeTerrain) continue;
                    // 水交给 Bridge；已铺桥（foundation 遮蔽，bridge=true，无 Water/Road tag）不覆盖；
                    // 已铺路材质不覆盖。
                    if (terrain != null && (terrain.IsWater || terrain.bridge || terrain.HasTag("Road"))) continue;

                    map.terrainGrid.SetTerrain(cell, placeTerrain);
                    filled++;
                }
            }

            if (filled > 0 && (RimExodusMod.Settings?.verboseLogging ?? false))
                Log.Message($"[RimExodus] Patch_GenStep_Roads_ApplyDistanceField: map={map.uniqueID} road={roadDef.defName} filled {filled} seam cells.");
        }
    }
}
