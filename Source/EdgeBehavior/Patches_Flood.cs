using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 原版 1.6 洪水（Odyssey：暴雨天气 TorrentialRain 的 TorrentialRainFlood 与 SeasonalFlooding 事件的
    /// SeasonalFlood，均继承 <see cref="RimWorld.Flood"/>）扩散准入排除 void 格——void 泛洪免疫的唯一收口
    /// （玩家报告：洪水漫过接缝带外圈淹掉整圈 void，在邻图视角下也显示为水面）。
    ///
    /// 【根因】原版准入 <c>CanFloodSpreadInto</c>/<c>CanFloodPotentiallySpreadInto</c> 只排除三类目标：
    /// 已是水地形（<c>TerrainAt.IsWater</c>，含已淹格——洪水地形在 tempGrid，TerrainAt 优先读它）、
    /// 有 foundation、有 edifice。RimExodus_Void 三条全不满足 → 洪水从接缝带水格一路扫过整个 void 环。
    /// 洪水写格走 <c>TerrainGrid.SetTempTerrain</c>（只写 tempGrid），不经 <c>SetTerrain</c>，生成期
    /// void 守卫（<see cref="Patch_TerrainGrid_SetTerrain"/>）拦不到——本 patch 是它的运行期补位。
    ///
    /// 【勿为洪水改接缝参考链】1.6 地形分层：洪水只进 tempGrid，topGrid（基础地形）在洪水期间保持干净
    /// （SetTerrain 对 temporary 地形自动改道 SetTempTerrain，常规路径不可能把洪水 def 写进 topGrid）；
    /// 接缝参考（SeamlessSeamOverride.ReadReference）与 389 快照读的都是裸 topGrid，与 tempGrid 永不相交，
    /// 洪水天然进不了参考链。邻图渲染走原版 section 层（读有效地形），洪水对邻图"可见"是正确表现。
    ///
    /// 【勿改成拦 SetTempTerrain】Flood.SpreadFlood 调用后无条件 floodedTileCount++ 并把格记进
    /// floodedCells/退水队列——拦写入层会空烧泛洪预算、留无效退水记录，可玩区洪水行为相对原版漂移；
    /// 准入级拒绝让洪水算法根本不把 void 当目标，语义精确。两个私有方法是全部扩散/前沿保留/建筑与
    /// 地形变动重开路径的共同原语，Prefix 单点全覆盖（字符串绑定私有方法，先例
    /// Patches_TileMutatorRiver.RiverTerrainAt）。
    ///
    /// 非 RimExodus 图（口袋/空间图）topGrid 无 void def，检查自然通过，零行为变化。旧档已淹的
    /// void 格照常退水（TempTerrainManager 队列与准入无关）。
    /// </summary>
    [HarmonyPatch(typeof(RimWorld.Flood), "CanFloodSpreadInto")]
    [HarmonyPatch(typeof(RimWorld.Flood), "CanFloodPotentiallySpreadInto")]
    static class Patch_Flood_VoidImmunity
    {
        private static TerrainDef voidDef;

        static bool Prefix(RimWorld.Flood __instance, IntVec3 cell, ref bool __result)
        {
            var map = __instance.Map;
            if (map == null || !cell.InBounds(map)) return true;
            voidDef ??= DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null) return true;
            if (map.terrainGrid.TerrainAt(cell) == voidDef)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
