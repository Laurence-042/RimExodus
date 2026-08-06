using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝世界地块的 MapParent。
    /// 继承原生 PocketMapParent，通过 sourceMap 字段指向宿主地图。
    /// 与 VMF 的 MapParent_Vehicle 不同，本类不依赖车辆实体，
    /// 而是把"锚点"抽象为静态宿主（基地地图或旅行 Pocket Map）。
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

        /// <summary>该地块地图相对宿主地图的固定平移偏移（宿主坐标）。</summary>
        public IntVec3 hostOffset;

        /// <summary>该地块的六个世界邻居 tile（按方向索引 0-5）。</summary>
        public List<int> neighborTiles = new List<int>();

        public override string Label => "Seamless Tile Map";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_Values.Look(ref direction, "direction", -1);
            Scribe_Values.Look(ref hostOffset, "hostOffset");
            Scribe_Collections.Look(ref neighborTiles, "neighborTiles", LookMode.Value);
        }
    }
}
