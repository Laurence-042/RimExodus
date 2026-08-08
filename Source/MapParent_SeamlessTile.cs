using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝世界地块的 MapParent。
    /// 继承原生 PocketMapParent，通过 sourceMap 字段指向锚点地图（家园 A）。
    /// 与 VMF 的 MapParent_Vehicle 不同，本类不依赖车辆实体，
    /// 而是把"锚点"抽象为静态宿主（基地地图或旅行 Pocket Map）。
    ///
    /// 地块间邻接关系由 <see cref="neighbors"/> 直接邻居表维护，
    /// 不依赖 sourceMap/IsPocketMap（对称架构：A→B、B→A、B→C 同等处理）。
    /// </summary>
    public class MapParent_SeamlessTile : PocketMapParent
    {
        /// <summary>该地块在世界地图上的 tile 索引。</summary>
        public int worldTile = -1;

        /// <summary>
        /// 该地块相对宿主地图的方向（六边形方向）：
        /// 0=北, 1=东北, 2=东南, 3=南, 4=西南, 5=西北。
        /// 原型阶段四向兼容：1/2 暂时当作东，4/5 暂时当作西。
        /// </summary>
        public int direction = -1;

        /// <summary>该地块地图相对生成源地块的固定平移偏移（源地块坐标系）。</summary>
        public IntVec3 hostOffset;

        /// <summary>该地块的六个世界邻居 tile（按方向索引 0-5）。</summary>
        public List<int> neighborTiles = new List<int>();

        /// <summary>
        /// 6 向直接邻居表。每槽存邻居引用 + 邻居相对本地块的偏移。
        /// 索引 = 方向（0=北,1=东北,2=东南,3=南,4=西南,5=西北）。
        /// null 或 neighbor==null 表示该方向无邻居。
        /// 偏移 = 邻居本地坐标 → 本地块本地坐标的平移（绘制邻居时用）。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>(6);

        /// <summary>该地块是否已被自动聚焦过（首个自有 pawn 入境时聚焦，仅一次）。</summary>
        public bool autoFocused;

        public override string Label => "Seamless Tile Map";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_Values.Look(ref direction, "direction", -1);
            Scribe_Values.Look(ref hostOffset, "hostOffset");
            Scribe_Collections.Look(ref neighborTiles, "neighborTiles", LookMode.Value);
            Scribe_Values.Look(ref autoFocused, "autoFocused", false);

            // 邻居表序列化：用 IExposable 的 NeighborLink 列表。
            // Scribe_Collections 对 IExposable 元素用 LookMode.Deep，会调用每个元素的 ExposeData。
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 存档前剔除空槽，只存有效邻居。
                neighbors.RemoveAll(n => n == null || n.neighbor == null);
            }
            Scribe_Collections.Look(ref neighbors, "neighbors", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                neighbors ??= new List<NeighborLink>(6);
                // 清理加载后可能残留的空引用。
                neighbors.RemoveAll(n => n == null || n.neighbor == null);
            }
        }

        /// <summary>获取指定方向的邻居连接（null 表示该方向无邻居）。</summary>
        public NeighborLink GetNeighborInDirection(int dir)
        {
            if (dir < 0 || dir >= 6)
            {
                return null;
            }
            foreach (var link in neighbors)
            {
                if (link != null && link.direction == dir && link.neighbor != null)
                {
                    return link;
                }
            }
            return null;
        }

        /// <summary>设置指定方向的邻居连接（覆盖或新增）。neighbor 可为锚点 MapParent 或 MapParent_SeamlessTile。</summary>
        public void SetNeighbor(int dir, MapParent neighbor, IntVec3 offset)
        {
            neighbors.RemoveAll(n => n != null && n.direction == dir);
            if (neighbor == null)
            {
                return;
            }
            neighbors.Add(new NeighborLink { direction = dir, neighbor = neighbor, offset = offset });
        }
    }

    /// <summary>
    /// 一条直接邻居连接：邻居地块引用 + 该邻居相对本地块的偏移。
    /// 偏移语义：邻居本地坐标 + offset = 本地块坐标系的坐标（绘制邻居内容时平移用）。
    /// neighbor 类型为 MapParent 基类，可容纳锚点地图（如 Settlement）和无缝地块 MapParent_SeamlessTile。
    /// </summary>
    public class NeighborLink : IExposable
    {
        /// <summary>邻居所在的方向（0=北,1=东北,2=东南,3=南,4=西南,5=西北）。</summary>
        public int direction;

        /// <summary>邻居地块的 MapParent 引用（锚点或无缝地块）。</summary>
        public MapParent neighbor;

        /// <summary>邻居相对本地块的偏移（邻居本地 → 本地块）。</summary>
        public IntVec3 offset;

        public NeighborLink() { }

        public NeighborLink(int direction, MapParent neighbor, IntVec3 offset)
        {
            this.direction = direction;
            this.neighbor = neighbor;
            this.offset = offset;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref direction, "direction", -1);
            Scribe_References.Look(ref neighbor, "neighbor");
            Scribe_Values.Look(ref offset, "offset");
        }
    }
}
