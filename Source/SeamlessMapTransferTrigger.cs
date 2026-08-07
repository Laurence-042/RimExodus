using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨地图转移触发组件。
    /// 挂在宿主地图上，每 tick 检查是否有 Pawn 站在接缝传送点上，若是则触发跨地图转移。
    ///
    /// 关键设计：口袋地图是作为宿主地图的叠加层渲染的，玩家始终在查看宿主地图
    /// （Find.CurrentMap = 宿主），因此口袋地图上的 MapComponentTick 不会因
    /// Find.CurrentMap 检查而触发。转移触发必须集中在宿主地图上，同时处理两个方向：
    /// - 宿主地图上的本端传送点 → TransferPawnToTile（宿主→地块）。
    /// - 各口袋地图上的对端传送点 → TransferPawnToHost（地块→宿主）。
    ///
    /// 防止刚落地立即被传回去：每次传送后写入 SeamlessTransferRegistry 的
    /// 时间戳-来源传送点-目标传送点记录；若 Pawn 在冷却期内仍站在刚落地的那个
    /// 传送点上，只刷新时间戳不重复传送；否则（无记录、已过期、或站在不同传送点）
    /// 正常触发传送并写入新记录。
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
            // 只在宿主地图上运行（口袋地图的触发由宿主统一处理）。
            if (map.IsPocketMap)
            {
                return;
            }

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

            // 清理已销毁 Pawn 的陈旧传送记录。
            SeamlessTransferRegistry.PurgeStaleEntries();

            // 方向一：宿主地图上的本端传送点 → 宿主→地块
            CheckHostSideSpots();

            // 方向二：各口袋地图上的对端传送点 → 地块→宿主
            CheckTileSideSpots();
        }

        /// <summary>检查宿主地图上的本端传送点，把站在上面的 Pawn 转移到相邻地块。</summary>
        private void CheckHostSideSpots()
        {
            // 遍历快照副本：TransferPawnToTile 内部会 DeSpawn/Spawn，
            // 直接遍历 AllThings 会在枚举期间修改列表导致异常。
            var allThings = new List<Thing>(map.listerThings.AllThings);
            foreach (var thing in allThings)
            {
                var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                if (comp == null)
                {
                    continue;
                }
                if (!comp.IsHostSide)
                {
                    continue;
                }

                var pawn = PawnOnCell(thing.Position, map);
                if (pawn == null)
                {
                    continue;
                }
                if (SeamlessTransferRegistry.IsRecentArrival(pawn, thing))
                {
                    continue;
                }

                Log.Message($"[RimExodus] Host-side trigger: pawn {pawn.LabelShort} at {pawn.Position} on enter spot at {thing.Position} dir={comp.Direction}");
                var enterParent = comp.AdjacentTileParent;
                if (enterParent != null)
                {
                    if (SeamlessMapTransfer.TransferPawnToTile(pawn, thing, enterParent))
                    {
                        SeamlessTransferRegistry.RecordTransfer(pawn, thing, comp.CounterpartSpot);
                    }
                }
                else
                {
                    Log.Warning("[RimExodus] Host-side trigger: AdjacentTileParent is null.");
                }
            }

        }

        /// <summary>检查各口袋地图上的对端传送点，把站在上面的 Pawn 转移回宿主地图。</summary>
        private void CheckTileSideSpots()
        {
            foreach (var pocketMap in Find.World.pocketMaps)
            {
                if (pocketMap is not MapParent_SeamlessTile parent || parent.sourceMap != map)
                {
                    continue;
                }

                var tileMap = parent.Map;
                if (tileMap == null || tileMap.Disposed)
                {
                    continue;
                }

                // 遍历快照副本：TransferPawnToHost 内部会 DeSpawn/Spawn，
                // 直接遍历 AllThings 会在枚举期间修改列表导致异常。
                var allThings = new List<Thing>(tileMap.listerThings.AllThings);
                foreach (var thing in allThings)
                {
                    var comp = thing.TryGetComp<CompSeamlessTileEnterSpot>();
                    if (comp == null || comp.IsHostSide)
                    {
                        continue;
                    }

                    var pawn = PawnOnCell(thing.Position, tileMap);
                    if (pawn == null)
                    {
                        continue;
                    }
                    if (SeamlessTransferRegistry.IsRecentArrival(pawn, thing))
                    {
                        continue;
                    }

                    Log.Message($"[RimExodus] Tile-side trigger: pawn {pawn.LabelShort} at {pawn.Position} on enter spot at {thing.Position} dir={comp.Direction}");
                    if (SeamlessMapTransfer.TransferPawnToHost(pawn, parent))
                    {
                        SeamlessTransferRegistry.RecordTransfer(pawn, thing, comp.CounterpartSpot);
                    }
                }
            }
        }

        /// <summary>返回指定格子上站立的、可转移的 Pawn（非倒地、非死亡）。</summary>
        private static Pawn PawnOnCell(IntVec3 cell, Map targetMap)
        {
            var pawns = targetMap.mapPawns.AllPawnsSpawned;
            foreach (var pawn in pawns)
            {
                if (pawn.Position != cell)
                {
                    continue;
                }
                if (pawn.Downed || pawn.Dead)
                {
                    continue;
                }
                return pawn;
            }
            return null;
        }
    }
}
