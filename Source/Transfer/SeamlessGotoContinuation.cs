using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 玩家纯 Goto 被转换成 Bridge 后仍需保留的移动语义。这里只复制 Goto 段实际消费的字段；
    /// exitMapOnArrival 有意不复制——原值可能是第三方把邻图局部格误当源图格后查 exitMapGrid
    /// 得出的伪阳，跨图玩家移动必须由 Bridge Grant 决定是否过缝，而不是在缝上原生离场。
    /// </summary>
    internal sealed class SeamlessGotoContinuation
    {
        private readonly LocomotionUrgency locomotionUrgency;
        private readonly bool collideWithPawns;
        private readonly bool canBashDoors;
        private readonly bool canBashFences;
        private readonly int expiryInterval;
        private readonly bool checkOverrideOnExpire;

        private SeamlessGotoContinuation(Job source)
        {
            locomotionUrgency = source.locomotionUrgency;
            collideWithPawns = source.collideWithPawns;
            canBashDoors = source.canBashDoors;
            canBashFences = source.canBashFences;
            expiryInterval = source.expiryInterval;
            checkOverrideOnExpire = source.checkOverrideOnExpire;
        }

        internal static SeamlessGotoContinuation Capture(Job source)
        {
            return source?.def == RimWorld.JobDefOf.Goto ? new SeamlessGotoContinuation(source) : null;
        }

        /// <summary>Transit 段只继承移动能力；它仍是 Grant 自驱动 Job，不伪装成玩家原命令。</summary>
        internal void ApplyToTransit(Job job)
        {
            ApplyMovement(job);
        }

        /// <summary>落地图终段恢复玩家强制移动语义，让原版设置 ignoreForbidden/Designations。</summary>
        internal void ApplyToFinal(Job job)
        {
            ApplyMovement(job);
            job.playerForced = true;
            job.expiryInterval = expiryInterval;
            job.checkOverrideOnExpire = checkOverrideOnExpire;
        }

        private void ApplyMovement(Job job)
        {
            if (job == null) return;
            job.locomotionUrgency = locomotionUrgency;
            job.collideWithPawns = collideWithPawns;
            job.canBashDoors = canBashDoors;
            job.canBashFences = canBashFences;
        }
    }
}
