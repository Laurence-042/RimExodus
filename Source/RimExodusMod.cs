using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// RimExodus 主入口。
    /// 在游戏启动时应用所有 Harmony patch。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RimExodusMod
    {
        static RimExodusMod()
        {
            var harmony = new Harmony("RimExodus.SeamlessWorld");
            harmony.PatchAll();
            Log.Message("[RimExodus] Harmony patches applied.");
        }
    }
}
