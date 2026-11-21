using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateContentsIntoMap))]
    internal static class Patch_ProviderMapGeneration
    {
        private static readonly string[] Steps = { "RimExodus_SeamlessTile", "RimExodus_SeamOverride", "RimExodus_EnterSpots" };

        static void Prefix(Map map, ref IEnumerable<GenStepWithParams> genStepDefs)
        {
            if (map == null || map.IsPocketMap || SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread
                || SeamlessMapProviders.For(map.Tile) == null || !SeamlessTileRegistry.GetMapWorldTile(map).Valid) return;

            // Inherited generator step lists are invisible to the XML patch on concrete defs.
            var steps = genStepDefs.ToList();
            foreach (var name in Steps)
            {
                var def = DefDatabase<GenStepDef>.GetNamedSilentFail(name);
                if (def != null && !steps.Any(step => step.def == def))
                    steps.Add(new GenStepWithParams(def, default));
            }
            genStepDefs = steps;
        }
    }
}
