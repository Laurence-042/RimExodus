using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图数据存储访问层（2026-08 归一）。「地块图数据挂 <see cref="MapParent_SeamlessTile"/>
    /// 字段（随 WorldObject 序列化、卸图后存活），原生 parent 图（家园/原生家族 Settlement 等——
    /// 原生 MapParent 挂不了我们的字段）挂 <see cref="SeamlessTileManager"/> 组件」的载体分支
    /// 只在本类出现，上层读写邻居表 / 基础三层快照 / tileOrigin 一律经此，
    /// 不再各处手写 is 分支（历史：16 处散落分支，读写路径漂移风险）。
    /// 查询类入口见 <see cref="SeamlessTileGraph"/>（邻接/休眠口径过滤在其上）。
    /// </summary>
    internal static class SeamlessMapData
    {
        /// <summary>
        /// 直接邻居表（活引用；图无表或组件缺失返回 null）。调用方可读可写（含 RemoveAll/Clear），
        /// 对列表结构的修改即时反映到存储——与旧"各处拿裸分支读写"语义一致。
        /// </summary>
        internal static List<NeighborLink> Neighbors(Map map)
        {
            if (map == null) return null;
            if (map.Parent is MapParent_SeamlessTile tileParent) return tileParent.neighbors;
            return map.GetComponent<SeamlessTileManager>()?.neighbors;
        }

        /// <summary>全局平面坐标系原点。地块图 = parent.tileOrigin（沿邻居链累加），原生图 = Zero（生成链源点）。</summary>
        internal static Vector2 TileOrigin(Map map)
        {
            if (map == null) return Vector2.zero;
            return map.Parent is MapParent_SeamlessTile tileParent ? tileParent.tileOrigin : Vector2.zero;
        }

        /// <summary>
        /// 写入基础三层快照（void 裁切前的原生 terrain/building/roof，同点位平行三层，
        /// 地块图中仅驻内存、缺失时由 <see cref="SeamlessSnapshotRegenerator"/> 按需重建；
        /// 原生图可经 <see cref="SeamlessBaseSnapshotData"/> 持久化以服务卸载恢复）。
        /// </summary>
        internal static void SetBaseSnapshots(Map map, TerrainDef[] terrain, ThingDef[] building, RoofDef[] roof)
        {
            if (map == null) return;
            if (map.Parent is MapParent_SeamlessTile tileParent)
            {
                tileParent.baseTerrainSnapshot = terrain;
                tileParent.baseBuildingSnapshot = building;
                tileParent.baseRoofSnapshot = roof;
            }
            else
            {
                var manager = map.GetComponent<SeamlessTileManager>();
                if (manager == null) return;
                manager.baseTerrainSnapshot = terrain;
                manager.baseBuildingSnapshot = building;
                manager.baseRoofSnapshot = roof;
            }
        }

        /// <summary>基础地形快照（无则 null）。建筑/屋顶层同点位同生命周期，消费方按需配对读取。</summary>
        internal static TerrainDef[] GetBaseTerrainSnapshot(Map map)
        {
            if (map == null) return null;
            return map.Parent is MapParent_SeamlessTile tileParent
                ? tileParent.baseTerrainSnapshot
                : map.GetComponent<SeamlessTileManager>()?.baseTerrainSnapshot;
        }

        /// <summary>基础建筑快照（岩石体 BuildingDef，无则 null）。</summary>
        internal static ThingDef[] GetBaseBuildingSnapshot(Map map)
        {
            if (map == null) return null;
            return map.Parent is MapParent_SeamlessTile tileParent
                ? tileParent.baseBuildingSnapshot
                : map.GetComponent<SeamlessTileManager>()?.baseBuildingSnapshot;
        }

        /// <summary>基础屋顶快照（无则 null）。</summary>
        internal static RoofDef[] GetBaseRoofSnapshot(Map map)
        {
            if (map == null) return null;
            return map.Parent is MapParent_SeamlessTile tileParent
                ? tileParent.baseRoofSnapshot
                : map.GetComponent<SeamlessTileManager>()?.baseRoofSnapshot;
        }
    }
}
