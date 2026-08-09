using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 事件驱动的跨地图传送检测（替代每 tick 轮询所有传送点）。
    ///
    /// <see cref="Pawn_PathFollower.TryEnterNextPathCell"/>（:638 <c>this.pawn.Position = nextCell</c>）
    /// 是 pawn 跨格移动的唯一钩子点。Postfix 它，在 pawn 进入新格时检查该格是否有已绑定的传送点，
    /// 若有则触发跨地图转移。
    ///
    /// 性能：每 pawn 跨格 1 次 O(1) 查询（thingGrid），pawn 不跨格时零开销。
    /// 相比之前的每 tick 轮询 ~600 传送点，大幅减少开销。
    ///
    /// 覆盖性：
    /// - 玩家征召移动、撤退敌人、续程 Goto 都走 pather 跨格 → Postfix 触发。
    /// - 跨图落地（GenSpawn.Spawn）不走 pather → 不触发。但此时 pawn 在 arrivalLocks 里（防回弹），
    ///   pawn 后续自己迈步走回 spot 时走 pather → Postfix 触发，此时锁已解除。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class Patch_Pawn_PathFollower_TryEnterNextPathCell
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (___pawn == null || !___pawn.Spawned || ___pawn.Map == null)
            {
                return;
            }
            SeamlessMapTransferTrigger.TryTriggerTransfer(___pawn, ___pawn.Position, ___pawn.Map);
        }
    }
}
