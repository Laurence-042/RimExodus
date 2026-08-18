using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝世界地块的 MapParent（阶段4前置：基础地图而非口袋地图）。
    /// 继承原生 <see cref="MapParent"/>（非 PocketMapParent），作为独立基础地图存在，
    /// 无 sourceMap 父子关系，所有地块对等。
    ///
    /// 基础地图的 <c>map.Tile</c> = 真实 PlanetTile，原生 Coast/River/Delta 等 TileMutator
    /// 在口袋地图上无法生效的问题自然消失（mutator.Init 读 map.Tile 拿到真实邻居数据）。
    ///
    /// 地块间邻接关系由 <see cref="neighbors"/> 直接邻居表维护，不依赖 sourceMap/IsPocketMap。
    /// 邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// </summary>
    public class MapParent_SeamlessTile : MapParent
    {
        /// <summary>该地块在世界地图上的 tile 索引。</summary>
        public int worldTile = -1;

        /// <summary>
        /// 直接邻居表。每槽存邻居引用 + 该邻居相对本地块的偏移 + 对应世界邻居 tile。
        /// 偏移 = 邻居本地坐标 → 本地块坐标的平移（绘制邻居时用）。
        /// worldTile 作为主键查询（稳定，无角度歧义）。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>该地块是否已被自动聚焦过（首个自有 pawn 入境时聚焦，仅一次）。</summary>
        public bool autoFocused;

        /// <summary>
        /// 该地块在全局平面坐标系里的原点（阶段4 接缝覆写预留）。
        ///
        /// 锚点 tile（玩家家园）的 tileOrigin 固定为 (0,0)。
        /// 口袋 tile 的 tileOrigin = 源 tile 的 tileOrigin + ComputeNeighborOffset（生成时确定）。
        /// 多跳场景（A→B→C）沿邻居链自然累加。
        ///
        /// 当前暂无消费者（连续 Perlin 方案已回退）。接缝覆写 genStep 将用它确定接缝位置。
        /// </summary>
        public Vector2 tileOrigin = Vector2.zero;

        /// <summary>
        /// 基础地形快照（阶段4 接缝覆写）：void 裁切前的完整矩形地形备份。
        /// 在 GenStep_SeamlessTile（order=391）开头备份——此时全部基础地形写入步骤（Terrain 210 /
        /// Coast 220 / CoastalEdgeFill 230 / Roads 390）已跑完，Plants(900)/Animals(1200) 尚未
        /// （它们不写 topGrid）；400+ 的 BaseGen 地板写入被选址 patch 拦在接缝带之外（残余缺口见
        /// SeamlessTileGenerator.xml 观察项）。
        /// 供接缝条带快照捕获（SeamStripData.CaptureAndStore 读外条带格的原生值）。
        /// 非序列化：生成期临时数据，读档后由 SeamlessTileManager 重建（锚点）或重新生成（地块）——
        /// 跨读档的接缝参考由 <see cref="seamStrip"/>（序列化）承担。
        /// </summary>
        public TerrainDef[] baseTerrainSnapshot;

        /// <summary>
        /// 原生建筑快照（岩石体 BuildingDef，null=无；与 <see cref="baseTerrainSnapshot"/> 同点位
        /// 备份、非序列化）。与地形快照平行的第二层：void 铺设会清除接缝带外格上的岩体，
        /// 接缝条带快照对外条带格必须引用**原生**两层（同源），否则世界连续的岩壁会被误判
        /// 无岩体（2026-08 实测：(98,233) 连续岩壁到该格突然消失）。存 BuildingDef 而非 bool：
        /// 完全一致区 spawn 用对端岩石 def，跨缝岩色也连续。
        /// </summary>
        public ThingDef[] baseBuildingSnapshot;

        /// <summary>
        /// 原生屋顶快照（RoofDef，null=无；与 <see cref="baseTerrainSnapshot"/> 同点位备份、非序列化）。
        /// 第三平行层：void 铺设会清掉接缝带外格的屋顶，外条带参考须用原生屋顶——否则对侧
        /// 照抄区"有岩石没有 roof"（岩壁裸顶，不挡雨/视觉突兀）。
        /// </summary>
        public RoofDef[] baseRoofSnapshot;

        /// <summary>
        /// 接缝条带快照（接缝带 B ∪ 过渡带 T 最终值 + 接缝带外条带原生值）。
        /// 挂在 WorldObject 上（非 Map）：随存档序列化，**地图卸载后数据存活**——
        /// 地图滚动加载卸载的生命周期预埋（未来卸 Map、留 WorldObject 时，
        /// SeamlessTileGraph.TryGetNeighborSeamStrip 的 WorldObject 回落路径自动接管）。
        /// Dev 完全卸载（RemoveTileMap）销毁本对象即数据消失（"完全卸载 = 从未出现过"）。
        /// </summary>
        public SeamStripData seamStrip;

        public override string Label => "Seamless Tile Map";

        /// <summary>
        /// 阶段4前置：基础地图的 WorldObject 会进入世界视图静态绘制层（useDynamicDrawer=false）。
        /// override Print 为空操作，让地块在世界地图上不显示图标。
        /// 地块通过地图内叠加渲染呈现，不需世界视图图标。
        /// </summary>
        public override void Print(LayerSubMesh subMesh)
        {
            // 不在世界视图画图标。
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_Values.Look(ref autoFocused, "autoFocused", false);
            Scribe_Values.Look(ref tileOrigin, "tileOrigin", Vector2.zero);
            Scribe_Deep.Look(ref seamStrip, "seamStrip");

            // 邻居表序列化：用 IExposable 的 NeighborLink 列表。
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                PruneInvalidNeighbors();
            }
            Scribe_Collections.Look(ref neighbors, "neighbors", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                neighbors ??= new List<NeighborLink>();
                PruneInvalidNeighbors();
            }
        }

        /// <summary>清理邻居表中的空引用（null NeighborLink 或 neighbor 字段为 null）。</summary>
        public void PruneInvalidNeighbors()
        {
            neighbors?.RemoveAll(n => n == null || n.neighbor == null);
        }

        /// <summary>获取指向指定世界 tile 的邻居连接（null 表示无此邻居）。</summary>
        public NeighborLink GetNeighborByWorldTile(int targetWorldTile)
        {
            foreach (var link in neighbors)
            {
                if (link != null && link.worldTile == targetWorldTile && link.neighbor != null)
                {
                    return link;
                }
            }
            return null;
        }

        /// <summary>设置指向 worldTile 的邻居连接（覆盖或新增）。neighbor 可为锚点 MapParent 或 MapParent_SeamlessTile。</summary>
        public void SetNeighbor(int worldTile, MapParent neighbor, IntVec3 offset)
        {
            neighbors.RemoveAll(n => n != null && n.worldTile == worldTile);
            if (neighbor == null)
            {
                return;
            }
            neighbors.Add(new NeighborLink
            {
                worldTile = worldTile,
                neighbor = neighbor,
                offset = offset
            });
        }
    }

    /// <summary>
    /// 一条直接邻居连接：邻居地块引用 + 该邻居相对本地块的偏移 + 对应世界邻居 tile。
    /// 偏移语义：邻居本地坐标 + offset = 本地块坐标系的坐标（绘制邻居内容时平移用）。
    /// neighbor 类型为 MapParent 基类，可容纳锚点地图（如 Settlement）和无缝地块 MapParent_SeamlessTile。
    /// worldTile 是该邻居在世界地图上的 tile id，作为邻居表主键（稳定，无角度歧义）。
    /// </summary>
    public class NeighborLink : IExposable
    {
        /// <summary>该邻居在世界地图上的 tile id（主键）。</summary>
        public int worldTile;

        /// <summary>邻居地块的 MapParent 引用（锚点或无缝地块）。</summary>
        public MapParent neighbor;

        /// <summary>邻居相对本地块的偏移（邻居本地 → 本地块）。</summary>
        public IntVec3 offset;

        public NeighborLink() { }

        public NeighborLink(int worldTile, MapParent neighbor, IntVec3 offset)
        {
            this.worldTile = worldTile;
            this.neighbor = neighbor;
            this.offset = offset;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_References.Look(ref neighbor, "neighbor");
            Scribe_Values.Look(ref offset, "offset");
        }
    }
}
