using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨地图传送的全局记录表：记录每个 Pawn 最近一次传送的 时间戳-来源传送点-目标传送点。
    ///
    /// 用途：当某个传送点发现 Pawn 站在自己上面时，先查询这张表：
    /// - 若该 Pawn 是在冷却期内从"别的"传送点传送到"当前"传送点的（即当前传送点正是它
    ///   刚落地的目标传送点），说明这是转移后的正常落地状态，只刷新时间戳，不重复触发。
    /// - 否则（无记录、记录已过期、或站在的不是它刚落地的那个传送点），说明这是一次新的
    ///   触发：删除旧记录，执行传送，并写入新记录。
    ///
    /// 原型阶段作为全局静态表实现，不随存档持久化。
    /// </summary>
    public static class SeamlessTransferRegistry
    {
        public const int DefaultCooldownTicks = 60;

        private class Record
        {
            public Thing FromSpot;
            public Thing ArrivalSpot;
            public int Tick;
        }

        private static readonly Dictionary<Pawn, Record> records = new Dictionary<Pawn, Record>();

        /// <summary>
        /// 检查 Pawn 是否在冷却期内从其他传送点刚传送到 currentSpot。
        /// 若是，刷新时间戳并返回 true（调用方应跳过本次传送）。
        /// 否则返回 false（调用方应正常触发传送并调用 RecordTransfer）。
        /// </summary>
        public static bool IsRecentArrival(Pawn pawn, Thing currentSpot, int cooldownTicks = DefaultCooldownTicks)
        {
            if (pawn == null || currentSpot == null)
            {
                return false;
            }

            if (!records.TryGetValue(pawn, out var record))
            {
                return false;
            }

            if (record.ArrivalSpot != currentSpot)
            {
                return false;
            }

            if (Find.TickManager.TicksGame - record.Tick > cooldownTicks)
            {
                return false;
            }

            record.Tick = Find.TickManager.TicksGame;
            return true;
        }

        /// <summary>记录一次传送：删除该 Pawn 的旧记录，写入 时间戳-来源传送点-目标传送点。</summary>
        public static void RecordTransfer(Pawn pawn, Thing fromSpot, Thing arrivalSpot)
        {
            records[pawn] = new Record
            {
                FromSpot = fromSpot,
                ArrivalSpot = arrivalSpot,
                Tick = Find.TickManager.TicksGame
            };
        }

        /// <summary>清理已销毁 Pawn 的陈旧记录，避免字典无限增长。</summary>
        public static void PurgeStaleEntries()
        {
            List<Pawn> stale = null;
            foreach (var kv in records)
            {
                if (kv.Key == null || kv.Key.Destroyed)
                {
                    stale ??= new List<Pawn>();
                    stale.Add(kv.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (var pawn in stale)
            {
                records.Remove(pawn);
            }
        }
    }
}
