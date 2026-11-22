using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 边界行为表的主体判定谓词（doc/边界行为表.md，状态：定稿 2026-08）。
    /// 供传送许可登记（<see cref="SeamlessTransferGrants"/> 的状态变更登记点）与跨图菜单
    /// （<see cref="Patches_FloatMenuMakerMap"/>）共用。全部为纯谓词，无副作用。
    /// </summary>
    internal static class SeamlessBoundaryRules
    {
        /// <summary>
        /// 该 pawn 是否可接收玩家跨图 goto 指令（桥接注入对象，行为表行 3/10/57）：
        /// 殖民者 / 殖民地机械族 / 玩家阵营驯养动物。
        /// 机械族保留原版 !IsColonyMech 例外（征召 goto 出口格不设 flag、不自行离图），
        /// 跨图移动统一走跨图 goto 桥接。
        /// </summary>
        internal static bool IsCrossMapOrderable(Pawn pawn)
        {
            if (pawn == null) return false;
            // VF 载具（2026-08）：VehiclePawn 不是 IsColonist/Animal，无此分支会被一切跨图
            // 下单链拒绝（菜单灰显 + 桥接 Prefix 放行原生）。
            if (SeamlessVehiclesCompat.IsVehicle(pawn)) return pawn.Faction == Faction.OfPlayer;
            if (pawn.IsColonist || pawn.IsColonyMech) return true;
            return pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer;
        }

        /// <summary>玩家阵营驯养动物（跟随传送的主体，行为表行 62；机械族 Escort 跟随见行为表行 13）。</summary>
        internal static bool IsColonyAnimal(Pawn pawn)
        {
            return pawn != null && pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer;
        }
    }
}
