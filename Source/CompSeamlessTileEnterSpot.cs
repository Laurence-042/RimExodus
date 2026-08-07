using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块入口点组件。
    /// 挂在接缝传送点 Thing 上，标记"从此处可进入相邻无缝地块"。
    ///
    /// 与 VMF 的 CompVehicleEnterSpot 不同，本组件不依赖车辆实体，
    /// 而是通过 SeamlessTileManager 查询相邻地块的入口位置。
    ///
    /// 每个接缝放置一对传送点：
    /// - 本端传送点挂在宿主地图上（IsHostSide = true），Pawn 走到后进入相邻地块。
    /// - 对端传送点挂在口袋地图上（IsHostSide = false），Pawn 走到后返回宿主地图。
    /// 两者在宿主坐标（drawPos）上重合于同一 tile，因此转移后视觉位置不跳变。
    /// </summary>
    public class CompSeamlessTileEnterSpot : ThingComp
    {
        private int direction;
        private bool isHostSide;
        private bool configured;

        /// <summary>该入口点对应的方向（六边形方向：0=北,1=东北,2=东南,3=南,4=西南,5=西北）。</summary>
        public int Direction => direction;

        /// <summary>该传送点是否位于宿主地图一侧（true=宿主→地块，false=地块→宿主）。</summary>
        public bool IsHostSide => isHostSide;

        public void Configure(int newDirection, bool newIsHostSide)
        {
            direction = newDirection;
            isHostSide = newIsHostSide;
            configured = true;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // Older saves created these values by mutating the shared ThingDef
            // CompProperties. Recover per-instance state from the map instead.
            if (!configured)
            {
                isHostSide = !parent.Map.IsPocketMap;
                direction = InferDirectionFromMap();
                configured = true;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref isHostSide, "isHostSide");
            Scribe_Values.Look(ref configured, "configured");
        }

        private int InferDirectionFromMap()
        {
            if (parent.Map?.Parent is MapParent_SeamlessTile tileParent)
            {
                return tileParent.direction;
            }

            var targetMap = parent.Map;
            if (targetMap == null)
            {
                return 0;
            }

            var position = parent.Position;
            var northDistance = targetMap.Size.z - 1 - position.z;
            var eastDistance = targetMap.Size.x - 1 - position.x;
            var southDistance = position.z;
            var westDistance = position.x;
            var minimum = northDistance;
            var inferredDirection = 0;

            if (eastDistance < minimum)
            {
                minimum = eastDistance;
                inferredDirection = 1;
            }
            if (southDistance < minimum)
            {
                minimum = southDistance;
                inferredDirection = 3;
            }
            if (westDistance < minimum)
            {
                inferredDirection = 4;
            }

            return inferredDirection;
        }

        /// <summary>
        /// 该入口点对应的相邻无缝地块 MapParent。
        /// 通过宿主地图的 SeamlessTileManager 查询。
        /// </summary>
        public MapParent_SeamlessTile AdjacentTileParent
        {
            get
            {
                var manager = parent.Map?.GetComponent<SeamlessTileManager>();
                return manager?.GetTileMapInDirection(Direction);
            }
        }

        /// <summary>
        /// 该入口点所在的无缝地块 MapParent（仅对端传送点有效）。
        /// 对端传送点挂在口袋地图上，其 Map.Parent 即该地块的 MapParent。
        /// </summary>
        public MapParent_SeamlessTile OwnTileParent
        {
            get
            {
                if (IsHostSide)
                {
                    return null;
                }
                return parent.Map?.Parent as MapParent_SeamlessTile;
            }
        }
    }

    public class CompProperties_SeamlessTileEnterSpot : CompProperties
    {
        public CompProperties_SeamlessTileEnterSpot()
        {
            compClass = typeof(CompSeamlessTileEnterSpot);
        }
    }
}
