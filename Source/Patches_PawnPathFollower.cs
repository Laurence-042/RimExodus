using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 事件驱动的跨地图传送检测（替代每 tick 轮询所有传送点）。
    ///
    /// <see cref="Pawn_PathFollower.TryEnterNextPathCell"/> 是 pawn 跨格移动的唯一钩子点。
    /// 必须用 <b>Prefix + pather.nextCell</b>（即将进入的格），不能是 Postfix：
    /// 进入<b>终点格</b>时原方法体内部会同步跑 PatherArrived → 当前 job 完成 →
    /// think tree 立刻发新 job（如 Wait_Combat）→ StartJob 钩子按"job 被替换"清掉传送许可——
    /// Postfix 永远晚于这条链，终点格传送永不触发（实测：Bridge issued 后紧跟
    /// "Grant cleared ... replaced by job Wait_Combat"）。Prefix 同时抢先于撤离 job
    /// 到达 toil 的原生 TryExitMap：对端已加载时撤离者应传送续链而非原生离场。
    ///
    /// 性能：每 pawn 跨格 1 次许可字典查询（无许可直接返回），pawn 不跨格时零开销。
    /// 覆盖性：所有 pathed 移动（玩家征召/撤离/续程/追击/跟随）都经此；跨图落地
    /// （GenSpawn.Spawn）不走 pather → 不触发，天然无"落地即弹回"（防回弹由许可制保证）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class Patch_Pawn_PathFollower_TryEnterNextPathCell
    {
        public static bool Prefix(Pawn ___pawn)
        {
            if (___pawn == null || !___pawn.Spawned || ___pawn.Map == null)
            {
                return true;
            }

            var nextCell = ___pawn.pather.nextCell;
            if (!nextCell.IsValid || !nextCell.InBounds(___pawn.Map))
            {
                return true;
            }

            var mapBefore = ___pawn.Map;
            SeamlessMapTransferTrigger.TryTriggerTransfer(___pawn, nextCell, mapBefore);

            // 触发器把 pawn 搬离了本图（跨图传送 / ExitMap 转世界）→ 跳过原方法体：
            // pather 的 path/nextCell 仍是旧图状态，pawn.Map 已是新图，跑方法体必然错乱（实测 NRE）。
            // 不能用 pawn.Spawned 判定——传送后 pawn 在新图上仍 Spawned。
            // 调用方 PatherTick 后续只有 lastMovedTick（无害）与 TrySetMovePercent
            // （DeSpawn 时 pather.StopDead 已置 moving=false，其 moving 条件直接短路），安全。
            return ___pawn.Map == mapBefore;
        }
    }
}
