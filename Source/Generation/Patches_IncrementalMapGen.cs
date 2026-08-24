using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 【实验分支】分帧生成期间，generating map 不参与主线程 tick/渲染。
    /// patch Map.MapPreTick/MapPostTick/MapUpdate 三个入口，对 generating map 早退。
    /// 这样玩家可继续操作其他 map，generating map 的 grid/region 在分帧 genStep 中逐步构建，不被 tick 干扰。
    ///
    /// 注意：全局 TickList（TickManager）仍可能 tick 到 generating map 上 spawn 的 Thing
    /// （RegisterAllTickAbilityFor 是全局的）。但生成期间 spawn 的 Thing 主要是岩石/植物/动物，
    /// 短暂 tick 影响极小（岩石/植物 tick 只生长，动物 AI 在 region 未建时寻路失败但不崩）。
    /// 若实测有问题，再 patch TickList 过滤 generating map 的 Thing。
    /// </summary>
    public static class Patches_IncrementalMapGen
    {
        [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
        static class Patch_Map_MapPreTick
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance); // generating map 跳过 tick。
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
        static class Patch_Map_MapPostTick
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        static class Patch_Map_MapUpdate
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance); // generating map 跳过渲染更新。
            }
        }
    }
}
