using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 阶段4 安全约束：多边形边内侧不可建造（防接缝卡死）。
    ///
    /// 动机：阶段3 的跨图传送依赖"传送点只在对侧可达时才被 goto 寻路激活"。但玩家可在接缝重叠带及其内侧
    /// 建造建筑，用建筑改变寻路把 pawn 困在 void 一侧——一旦 pawn 站上指向"已被建筑封死对端"的传送点，
    /// 就会卡死。故多边形边内侧必须禁止建造，从根上杜绝这种滥用。
    ///
    /// patch 点：<see cref="GenConstruct.CanPlaceBlueprintAt"/> 是所有玩家建造路径
    /// （手动 Designator_Build/Designator_Install、自动重建 ThingUtility、Sketch、Prefab、BaseGen）的唯一汇聚点。
    /// Prefix 内遍历 <see cref="GenAdj.OccupiedRect(IntVec3, Rot4, IntVec2)"/>，任一格落在禁建带 → 拒绝。
    ///
    /// godMode 放行（开发模式可建，与原生 InNoBuildEdgeArea 语义一致）。
    /// 不处理原版 InNoBuildEdgeArea（地图矩形边缘 10 格禁建）——两者各管各的，本 patch 只管多边形边内侧的禁建带；
    /// 用户明确要求不处理 InNoBuildEdgeArea，避免六边形边角顶到地图矩形边缘时反而能在原版禁建带建造。
    /// </summary>
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt))]
    static class Patch_GenConstruct_CanPlaceBlueprintAt
    {
        static bool Prefix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map,
            bool godMode, ref AcceptanceReport __result)
        {
            // godMode 放行（开发模式）。
            if (godMode) return true;

            // 非无缝地块地图放行（无 SeamlessBorderLookup 组件）。
            var lookup = map?.GetComponent<SeamlessBorderLookup>();
            if (lookup == null) return true;

            // 速查表未就绪时放行（避免误拒——BuildBorderLookup 延迟 1 tick 构建）。
            if (!lookup.IsBuilt) return true;

            // 遍历建筑占用的所有格，任一在禁建带则拒绝。
            var rect = GenAdj.OccupiedRect(center, rot, entDef.Size);
            foreach (var cell in rect)
            {
                if (lookup.IsInNoBuildBand(cell))
                {
                    __result = new AcceptanceReport("RimExodus_BorderNoBuild".Translate());
                    return false; // 跳过原方法。
                }
            }
            return true; // 放行原方法。
        }
    }
}
