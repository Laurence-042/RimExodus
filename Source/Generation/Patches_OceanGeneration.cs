using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 内置 Ocean 地图补全启用时，跳过 Base_Player 链中只对陆地图有意义的步骤。
    /// 深海图经接缝进入，不需要 Heavy 承载力的 PlayerStartSpot；原版 Ocean 也没有陆生动物生态。
    /// 通用 Scatterer 包含神殿、围栏、遗迹等陆地散布器，纯深海上只会反复选点失败。
    /// 设置关闭并重启后全部 Prefix 放行原版/第三方行为。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_FindPlayerStartSpot), nameof(GenStep_FindPlayerStartSpot.Generate))]
    internal static class Patch_GenStep_FindPlayerStartSpot_Ocean
    {
        private static bool Prefix(Map map) => !SeamlessOceanMapSupport.Handles(map);
    }

    [HarmonyPatch(typeof(GenStep_Animals), nameof(GenStep_Animals.Generate))]
    internal static class Patch_GenStep_Animals_Ocean
    {
        private static bool Prefix(Map map) => !SeamlessOceanMapSupport.Handles(map);
    }

    [HarmonyPatch(typeof(GenStep_Scatterer), nameof(GenStep_Scatterer.Generate))]
    internal static class Patch_GenStep_Scatterer_Ocean
    {
        private static bool Prefix(Map map) => !SeamlessOceanMapSupport.Handles(map);
    }
}
