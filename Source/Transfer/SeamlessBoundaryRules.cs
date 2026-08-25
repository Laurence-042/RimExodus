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
        /// 该 pawn 是否可接收玩家跨图 goto 指令（桥接注入对象，行为表行 3/10/55）：
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

        /// <summary>
        /// NPC 战斗体（追击传送的主体，行为表行 15/23/31）：人形或机械族且非玩家阵营。
        /// 覆盖敌方人形/敌方机械族/盟友增援（表对盟友战斗移动同样要求传送）。
        /// 野生动物（含狂猎动物）不属战斗体——行为表无对应行，不登记追击传送（已知边界）。
        /// </summary>
        internal static bool IsNpcCombatant(Pawn pawn)
        {
            if (pawn == null || pawn.Faction == Faction.OfPlayer) return false;
            return pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid;
        }

        /// <summary>玩家阵营驯养动物（跟随传送的主体，行为表行 60）。</summary>
        internal static bool IsColonyAnimal(Pawn pawn)
        {
            return pawn != null && pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer;
        }
    }
}
