using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨地图转移触发组件。
    /// 挂在宿主地图上，每 tick 检查是否有 Pawn 站在接缝传送点上，
    /// 若是则触发跨地图转移。
    ///
    /// 原型阶段采用简单的 tick 轮询触发（而非 VMF 的完整 JobDriver 系统），
    /// 以验证"Pawn 走到接缝 → 无缝转移到相邻地块"的核心机制。
    /// </summary>
    public class SeamlessMapTransferTrigger : MapComponent
    {
        private int tickCounter;

        public SeamlessMapTransferTrigger(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            // 每 30 tick 检查一次，降低开销
            tickCounter++;
            if (tickCounter < 30)
            {
                return;
            }
            tickCounter = 0;

            // 只处理当前地图（玩家正在查看的地图）
            if (Find.CurrentMap != map)
            {
                return;
            }

            // 收集宿主地图上的所有传送点
            var allThings = map.listerThings.AllThings;
            foreach (var thing in allThings)
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null)
                {
                    continue;
                }

                // 检查是否有 Pawn 站在传送点上
                var pawns = map.mapPawns.AllPawnsSpawned;
                foreach (var pawn in pawns)
                {
                    if (pawn.Position != thing.Position)
                    {
                        continue;
                    }
                    if (pawn.Downed || pawn.Dead)
                    {
                        continue;
                    }

                    // 触发转移
                    var enterParent = comp.AdjacentTileParent;
                    if (enterParent != null)
                    {
                        SeamlessMapTransfer.TransferPawnToTile(pawn, thing, enterParent);
                    }
                }
            }
        }
    }
}
