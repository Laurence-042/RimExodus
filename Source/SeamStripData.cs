using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 接缝条带快照：一张地图"接缝带 B ∪ 过渡带 T ∪ 接缝带外条带"区域的定格地形、建筑、屋顶记录。
    /// **平行三层**：terrain 层（TerrainDef）、building 层（岩石体 BuildingDef，null=无）、
    /// roof 层（RoofDef，null=无）——三层同点位、同来源、同生命周期，消费方对三层做同样的
    /// 查询（无单层特判）。
    ///
    /// 【用途】新地图 C 生成时（GenStep 392）参考所有已生成邻居的接缝状态：
    /// - B ∪ T 格：接缝混合完成后的**最终实况**（重叠区"三层完全一致"的参考源）；
    /// - 接缝带外条带格（void 侧浅层）：void 裁切前的**原生快照**（过渡混合参考源，权重随
    ///   深度衰减）——三层都取原生（同源；岩体/屋顶实况已被 void 铺设清除，不可用）。
    ///
    /// 【存储与生命周期（地图滚动加载卸载预埋）】地块图挂 <see cref="MapParent_SeamlessTile.seamStrip"/>
    /// （WorldObject，随存档序列化，**地图卸载后数据存活**）；锚点图挂
    /// <see cref="SeamlessTileManager.anchorSeamStrip"/>（锚点=家园常驻不卸载）。
    /// 统一取数入口 <see cref="SeamlessTileGraph.TryGetNeighborSeamStrip"/>：活图优先，
    /// 无活图时回落到 WorldObject 上的快照——未来"滚动卸载（卸 Map、留 WorldObject）"时
    /// 新图参考链路零改动。Dev 完全卸载（RemoveTileMap）销毁 WorldObject 即数据消失
    /// （"完全卸载 = 从未出现过"）。
    ///
    /// 【序列化】cells（Value）+ terrains（Def）+ buildings（Def）+ roofs（Def，可 null）。
    /// 查询字典（terrainLookup/buildingLookup/roofLookup）非序列化，写入后 / PostLoadInit 重建。
    /// </summary>
    public class SeamStripData : IExposable
    {
        /// <summary>捕获时地图尺寸（邻居带几何判定需要：带缓存键 = (worldTile, mapSize)）。</summary>
        public int mapSize;

        /// <summary>条带格（本地坐标）。</summary>
        public List<IntVec3> cells;

        /// <summary>terrain 层：与 cells 一一对应（B∪T 格=最终值；外条带格=原生值）。</summary>
        public List<TerrainDef> terrains;

        /// <summary>building 层：与 cells 一一对应的岩石体 BuildingDef（null=无岩体）。跨缝 spawn 用对端 def——岩色也连续。</summary>
        public List<ThingDef> buildings;

        /// <summary>roof 层：与 cells 一一对应的 RoofDef（null=无屋顶）。照抄区屋顶照抄对端——岩壁带原生岩顶（Thick/Thin），不裸顶。</summary>
        public List<RoofDef> roofs;

        /// <summary>深度层：与 cells 一一对应——B=0、T=过渡深度、外条带=外深度（距 B 的切比雪夫距离）。SeamOverride 权重衰减轴（接近源六边形高 → 源方形边 0）。</summary>
        public List<int> depths;

        /// <summary>cell → terrain 查询字典（非序列化，重建）。</summary>
        [NonSerialized] public Dictionary<IntVec3, TerrainDef> terrainLookup;

        /// <summary>cell → 岩石 BuildingDef 查询字典（非序列化，重建；无记录 = null）。</summary>
        [NonSerialized] public Dictionary<IntVec3, ThingDef> buildingLookup;

        /// <summary>cell → RoofDef 查询字典（非序列化，重建；无记录 = null）。</summary>
        [NonSerialized] public Dictionary<IntVec3, RoofDef> roofLookup;

        /// <summary>cell → 深度查询字典（非序列化，重建；无记录 = 0）。</summary>
        [NonSerialized] public Dictionary<IntVec3, int> depthLookup;

        public void RebuildLookup()
        {
            terrainLookup = new Dictionary<IntVec3, TerrainDef>(cells?.Count ?? 0);
            buildingLookup = new Dictionary<IntVec3, ThingDef>(cells?.Count ?? 0);
            roofLookup = new Dictionary<IntVec3, RoofDef>(cells?.Count ?? 0);
            depthLookup = new Dictionary<IntVec3, int>(cells?.Count ?? 0);
            if (cells == null || terrains == null) return;
            var count = Math.Min(cells.Count, terrains.Count);
            for (var i = 0; i < count; i++)
            {
                terrainLookup[cells[i]] = terrains[i];
                buildingLookup[cells[i]] = buildings != null && i < buildings.Count ? buildings[i] : null;
                roofLookup[cells[i]] = roofs != null && i < roofs.Count ? roofs[i] : null;
                depthLookup[cells[i]] = depths != null && i < depths.Count ? depths[i] : 0;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapSize, "mapSize", 0);
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            Scribe_Collections.Look(ref terrains, "terrains", LookMode.Def);
            Scribe_Collections.Look(ref buildings, "buildings", LookMode.Def);
            Scribe_Collections.Look(ref roofs, "roofs", LookMode.Def);
            Scribe_Collections.Look(ref depths, "depths", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RebuildLookup();
            }
        }

        /// <summary>
        /// 捕获 map 的接缝条带快照并写入存储位（地块图 → MapParent_SeamlessTile.seamStrip；
        /// 锚点图 → SeamlessTileManager.anchorSeamStrip）。幂等（覆盖旧快照）。
        ///
        /// 三层同来源分两段：B∪T → 最终实况（topGrid + 岩石 edifice def + RoofAt）；
        /// 外条带 → 原生快照（baseTerrain/baseBuilding/baseRoofSnapshot，391 同点位备份）。
        ///
        /// 调用时机：GenStep_SeamOverride.Generate（392）末尾——此时接缝混合已完成。
        /// </summary>
        public static void CaptureAndStore(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var size = map.Size.x;
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, size);
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var topGrid = map.terrainGrid.topGrid;
            var cellIndices = map.cellIndices;
            var roofGrid = map.roofGrid;

            MapParent_SeamlessTile tileParent = null;
            SeamlessTileManager manager = null;
            if (map.Parent is MapParent_SeamlessTile p) tileParent = p;
            else manager = map.GetComponent<SeamlessTileManager>();

            var baseTerrain = tileParent != null ? tileParent.baseTerrainSnapshot : manager?.anchorBaseTerrainSnapshot;
            var baseBuilding = tileParent != null ? tileParent.baseBuildingSnapshot : manager?.anchorBaseBuildingSnapshot;
            var baseRoof = tileParent != null ? tileParent.baseRoofSnapshot : manager?.anchorBaseRoofSnapshot;

            var data = new SeamStripData { mapSize = size };
            var cells = new List<IntVec3>(band.Band.Count + band.OuterStrip.Count + 1024);
            var terrains = new List<TerrainDef>(cells.Capacity);
            var buildings = new List<ThingDef>(cells.Capacity);
            var roofs = new List<RoofDef>(cells.Capacity);
            var depths = new List<int>(cells.Capacity);
            var seen = new HashSet<IntVec3>();

            void Add(IntVec3 c, TerrainDef t, ThingDef b, RoofDef r, int d)
            {
                if (t == null || t == voidDef) return; // null/void 地形无参考价值，不入快照
                if (seen.Add(c))
                {
                    cells.Add(c);
                    terrains.Add(t);
                    buildings.Add(b);
                    roofs.Add(r);
                    depths.Add(d);
                }
            }

            // 接缝带 B（含带外圈——新定义下为实地形）：最终实况（三层），深度 0。
            foreach (var c in band.Band)
                Add(c, topGrid[cellIndices.CellToIndex(c)], RockDefAt(map, c), roofGrid.RoofAt(c), 0);

            // 过渡带 T：最终实况，**限深 SeamStripInnerDepth**（只服务错位 ≤3 格的浅层照抄参考）。
            foreach (var kv in band.TransitionDepth)
            {
                if (kv.Value > SeamlessPolygonGeometry.SeamStripInnerDepth) continue;
                Add(kv.Key, topGrid[cellIndices.CellToIndex(kv.Key)], RockDefAt(map, kv.Key), roofGrid.RoofAt(kv.Key), kv.Value);
            }

            // 接缝带外条带（void 侧）：原生快照三层（391 备份，同源），**全深到方形边**
            // （权重衰减参考数据：接近源六边形高 → 源方形边 0，数据必须覆盖到边）。
            foreach (var kv in band.OuterStripDepth)
            {
                var c = kv.Key;
                var idx = cellIndices.CellToIndex(c);
                Add(c,
                    baseTerrain != null ? baseTerrain[idx] : null,
                    baseBuilding != null ? baseBuilding[idx] : null,
                    baseRoof != null ? baseRoof[idx] : null,
                    kv.Value);
            }

            data.cells = cells;
            data.terrains = terrains;
            data.buildings = buildings;
            data.roofs = roofs;
            data.depths = depths;
            data.RebuildLookup();

            if (tileParent != null) tileParent.seamStrip = data;
            else if (manager != null) manager.anchorSeamStrip = data;

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] SeamStripData captured: map={map.uniqueID}(wt={worldTile}) cells={cells.Count}");
        }

        /// <summary>格上的岩石体 BuildingDef（edifice 是岩石体则返回其 def，否则 null）。SeamOverride 层委托共用。</summary>
        internal static ThingDef RockDefAt(Map map, IntVec3 c)
        {
            var edifice = c.GetEdifice(map);
            return edifice != null && edifice.def.building != null && edifice.def.building.naturalTerrain != null
                ? edifice.def
                : null;
        }
    }
}
