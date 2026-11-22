#nullable enable
using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static class EndlessHorizonChecks
{
    internal static void CheckGeometry(Map map, Action<bool, string> check)
    {
        var type = typeof(RimExodus.RimExodusMod).Assembly.GetType("RimExodus.EndlessHorizonGeometry", true)!;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = type.GetConstructors(instance).Single();
        var neighbors = RimExodus.SeamlessTileGraph.GetAllNeighbors(map);
        var isolated = constructor.Invoke(new object[] { map, map.Tile, 32, new List<RimExodus.SeamlessTileGraph.NeighborInfo>() });
        var joined = constructor.Invoke(new object[] { map, map.Tile, 32, neighbors });
        bool Draw(object geometry, IntVec3 cell) => (bool)type.GetMethod("DrawCell", instance)!
            .Invoke(geometry, new object[] { cell, 16 })!;
        bool Inside(object geometry, IntVec3 cell) => (bool)type.GetMethod("Inside", instance)!
            .Invoke(geometry, new object[] { cell })!;
        bool Owns(object geometry, Vector2 point) => (bool)type.GetMethod("Owns", instance)!
            .Invoke(geometry, new object[] { point })!;

        var diagonal = new IntVec3(8, 0, 16);
        while (Inside(isolated, diagonal)) diagonal += new IntVec3(-1, 0, -1);
        check(diagonal.InBounds(map) && Draw(isolated, diagonal) && !Draw(isolated, diagonal + new IntVec3(1, 0, 1)),
            "Endless Horizon begins in the first rectangular in-bounds void cell outside a hexagonal seam");
        var beyondRectangle = new IntVec3(-1, 0, map.Size.z / 2);
        check(!beyondRectangle.InBounds(map) && Draw(isolated, beyondRectangle),
            "Endless Horizon continues past the original rectangular map boundary");
        var sharedSide = new IntVec3(31, 0, 16);
        check(neighbors.Count == 1 && Draw(isolated, sharedSide) && !Draw(joined, sharedSide)
            && Owns(joined, new Vector2(27.9f, 16.5f)) && !Owns(joined, new Vector2(28.1f, 16.5f)),
            "Endless Horizon splits two independent draw domains on their shared seam plane");
        check(Draw(joined, diagonal) && !Draw(joined, new IntVec3(-40, 0, 16)),
            "Endless Horizon preserves uncovered seam cells and respects its distance limit");
    }

    internal static void Run(string assemblyPath)
    {
        var assembly = typeof(RimExodus.RimExodusMod).Assembly;
        Audit.CheckSeamlessLayerTopology(assembly, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            (ok, label) => { if (!ok) throw new InvalidOperationException(label); }, checkHorizon: true);
        var endlessHorizon = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var registry = endlessHorizon.GetType("EndlessHorizon.HorizonRegistry", true)!;
        _ = registry.GetMethod("Get")!.CreateDelegate<Func<Verse.Map, object>>();
        var compat = assembly.GetType("RimExodus.SeamlessEndlessHorizonCompat", true)!;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        if (compat.GetMethod("TryDrawClippers", statics) == null || compat.GetMethod("DrawBackground", statics) == null
            || compat.GetMethod("VisibilityPadding", statics) == null)
            throw new InvalidOperationException("Endless Horizon clipper/background phases are not separated.");
        var nativeRendering = assembly.GetType("RimExodus.SeamlessNativeRendering", true)!;
        if (nativeRendering.GetMethod("TryGetEndlessHorizonView", statics) == null
            || nativeRendering.GetMethod("TryGetFocusedSkyTint", statics) == null)
            throw new InvalidOperationException("Endless Horizon has no uncut REF neighbor view or focused sky tint.");
        var patches = (IEnumerable)compat.GetMethod("BindPatches", statics)!.Invoke(null, null)!;
        var harmony = new Harmony("RimExodus.Regression.EndlessHorizon");
        var count = 0;
        foreach (var patch in patches)
        {
            var type = patch.GetType();
            var target = (MethodBase)type.GetProperty("Target")!.GetValue(patch)!;
            HarmonyMethod? Hook(string name) => (HarmonyMethod?)type.GetProperty(name)!.GetValue(patch);
            try
            {
                harmony.Patch(target, prefix: Hook("Prefix"), postfix: Hook("Postfix"), transpiler: Hook("Transpiler"));
                if (Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) != true)
                    throw new InvalidOperationException("Harmony did not report the patch owner.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Endless Horizon binding failed for {target.DeclaringType}.{target.Name}", ex);
            }
            count++;
        }
        Console.WriteLine($"Endless Horizon: hexagonal seam, projected-neighbor, split draw-phase, and native neighbor-view checks passed. {count} patches bound to the Workshop assembly.");
    }
}
