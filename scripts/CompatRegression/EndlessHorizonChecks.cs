#nullable enable
using System.Reflection;
using System.Runtime.Loader;
using UnityEngine;
using Verse;

internal static class EndlessHorizonChecks
{
    internal static void CheckGeometry(Map map, Action<bool, string> check)
    {
        var compat = typeof(RimExodus.RimExodusMod).Assembly
            .GetType("RimExodus.SeamlessEndlessHorizonCompat", true)!;
        var type = compat.GetNestedType("MapRegion", BindingFlags.NonPublic)!;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = type.GetConstructors(instance).Single();
        var neighbors = RimExodus.SeamlessTileGraph.GetAllNeighbors(map);
        var isolated = constructor.Invoke(new object[]
            { map, map.Tile, new List<RimExodus.SeamlessTileGraph.NeighborInfo>(), null! });
        var joined = constructor.Invoke(new object[] { map, map.Tile, neighbors, null! });
        bool Playable(object region, IntVec3 cell) => (bool)type.GetMethod("Playable", instance)!
            .Invoke(region, new object[] { cell })!;
        var drawArea = (IReadOnlyList<Vector2>)type.GetField("DrawArea", instance)!.GetValue(joined)!;
        var skyTintMesh = (Mesh?)type.GetField("SkyTintMesh", instance)!.GetValue(joined);

        var diagonal = new IntVec3(8, 0, 16);
        while (Playable(isolated, diagonal)) diagonal += new IntVec3(-1, 0, -1);
        check(diagonal.InBounds(map) && !Playable(isolated, diagonal)
            && Playable(isolated, diagonal + new IntVec3(1, 0, 1)),
            "Endless Horizon API region begins in the first in-bounds void cell outside the seam band");
        check(Contains(drawArea, new Vector2(27.9f, 16.5f))
            && !Contains(drawArea, new Vector2(28.1f, 16.5f)),
            "Endless Horizon API draw areas split neighboring maps on their shared seam plane");
        check(drawArea.Count >= 3 && drawArea.All(p => float.IsFinite(p.x) && float.IsFinite(p.y)),
            "Endless Horizon receives a finite convex draw domain");
        check(skyTintMesh != null && skyTintMesh.vertices.All(v =>
                v.y < AltitudeLayer.LightingOverlay.AltitudeFor()),
            "Endless Horizon sky tint reuses the finite convex draw domain below glow/roof depth");

        var other = neighbors.Single().map;
        var plans = new List<RimExodus.Maps.MapRenderPlan>
        {
            new(map, Matrix4x4.identity, new CellRect(0, 0, 32, 32)),
            new(other, Matrix4x4.Translate(new Vector3(24.5f, 0f, 0f)), new CellRect(0, 0, 32, 32))
        };
        var composite = (Dictionary<Map, IReadOnlyList<Vector2>>)compat
            .GetMethod("BuildCompositeDrawAreas", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { map, plans })!;
        var sourceArea = composite[map];
        var otherArea = composite[other];
        check(Contains(sourceArea, new Vector2(28.24f, 16.5f))
            && !Contains(sourceArea, new Vector2(28.26f, 16.5f))
            && !Contains(otherArea, new Vector2(3.74f, 16.5f))
            && Contains(otherArea, new Vector2(3.76f, 16.5f)),
            "Endless Horizon composite draw areas share the exact rendered fractional bisector");

    }

    internal static void Run(string assemblyPath)
    {
        var assembly = typeof(RimExodus.RimExodusMod).Assembly;
        Audit.CheckSeamlessLayerTopology(assembly, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            (ok, label) => { if (!ok) throw new InvalidOperationException(label); }, checkHorizon: true);
        var endlessHorizon = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var info = endlessHorizon.GetType("EndlessHorizon.Api.HorizonInfo", true)!;
        var space = endlessHorizon.GetType("EndlessHorizon.Api.HorizonSpace", true)!;
        var view = endlessHorizon.GetType("EndlessHorizon.Api.HorizonView", true)!;
        var extensions = endlessHorizon.GetType("EndlessHorizon.Api.HorizonExtensions", true)!;
        var version = (int)info.GetProperty("Version", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        if (version < 1) throw new InvalidOperationException($"Endless Horizon API v{version} is too old.");
        Require(info, "IsActive", typeof(Map));
        Require(info, "FogShown", typeof(Map));
        Require(info, "RingWidth", typeof(Map));
        Require(space, "DrawExtent", typeof(Map));
        Require(space, "SetRegion", typeof(Map), typeof(string), typeof(Predicate<IntVec3>),
            typeof(IReadOnlyList<Vector2>));
        Require(space, "ClearRegion", typeof(Map), typeof(string));
        Require(view, "SetExternalDrawing", typeof(Map), typeof(string), typeof(bool));
        Require(view, "Draw", typeof(Map), typeof(CellRect));
        Require(extensions, "RegisterDraw", typeof(string), typeof(string), typeof(Action<Map, CellRect>));

        var compat = assembly.GetType("RimExodus.SeamlessEndlessHorizonCompat", true)!;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var bound = (int)compat.GetMethod("BindApi", statics)!.Invoke(null, null)!;
        if (bound != version) throw new InvalidOperationException("RimExodus did not bind the loaded API version.");
        if (compat.GetMethod("BindPatches", statics) != null
            || assembly.GetType("RimExodus.EndlessHorizonGeometry") != null)
            throw new InvalidOperationException("Legacy Endless Horizon internal patching is still present.");
        if (compat.GetMethod("TryDrawClippers", statics) == null
            || compat.GetMethod("DrawBackground", statics) == null
            || compat.GetMethod("TryPrepareSeparatedLighting", statics) == null
            || compat.GetMethod("DrawSkyTint", statics) == null
            || compat.GetProperty("SkyTintMaterial", statics) == null
            || compat.GetMethod("VisibilityPadding", statics) == null)
            throw new InvalidOperationException("Endless Horizon external draw phases are incomplete.");
        var nativeRendering = assembly.GetType("RimExodus.SeamlessNativeRendering", true)!;
        if (nativeRendering.GetMethod("TryGetEndlessHorizonView", statics) == null
            || nativeRendering.GetMethod("TryGetSeparatedSkyTint", statics) == null)
            throw new InvalidOperationException("Endless Horizon has no uncut REF neighbor view or split sky tint.");

        Console.WriteLine($"Endless Horizon: public API v{version}, seam region, split draw domain, "
            + "separated sky tint, and external neighbor drawing checks passed. No internal Harmony patches.");
    }

    private static void Require(Type type, string name, params Type[] parameters)
    {
        if (type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null) == null)
            throw new MissingMethodException(type.FullName, name);
    }

    private static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        const float epsilon = .001f;
        var sign = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var cross = (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
            if (Mathf.Abs(cross) <= epsilon) continue;
            if (sign == 0f) sign = Mathf.Sign(cross);
            else if (Mathf.Sign(cross) != sign) return false;
        }
        return polygon.Count >= 3;
    }
}
