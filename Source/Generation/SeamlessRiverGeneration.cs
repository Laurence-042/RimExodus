using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    // The base river worker owns both consumers of the centerline: the depth field and the flow texture.
    // Keep temporary endpoint decisions scoped to one worker call, and keep the anchors on the generated map.
    [HarmonyPatch]
    static class Patch_TileMutatorWorker_River_Generation
    {
        [ThreadStatic] internal static RiverGenerationContext Current;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_River), nameof(TileMutatorWorker_River.GeneratePostTerrain));
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_River), nameof(TileMutatorWorker_River.GeneratePostFog));
        }

        static void Prefix(TileMutatorWorker_River __instance, Map map, out RiverGenerationContext __state)
        {
            __state = Current;
            Current = !SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread &&
                SeamlessTileRegistry.GetMapWorldTile(map).Valid
                ? new RiverGenerationContext(__instance, map)
                : null;
        }

        static void Finalizer(RiverGenerationContext __state) => Current = __state;
    }

    sealed class RiverGenerationContext
    {
        internal readonly TileMutatorWorker_River Worker;
        internal readonly Map Map;
        internal readonly int FirstNodeIndex;
        internal SeamlessRiverCrossings Anchors;
        internal bool RequestedEndpoints;
        internal (Vector3 start, Vector3 end)? OrderedEndpoints;
        internal RiverSeamAnchor? DeltaInlet;
        internal List<(RiverSeamAnchor anchor, float width)> DeltaTributaries;
        internal (RiverSeamAnchor anchor, Vector3 start, Vector3 end)? DeltaSingleOutlet;
        internal float? DeltaMergeT;
        internal List<PlanetTile> ConfluenceInlets;

        internal RiverGenerationContext(TileMutatorWorker_River worker, Map map)
        {
            Worker = worker;
            Map = map;
            FirstNodeIndex = map.waterInfo.riverGraph.Count;
            Anchors = map.GetComponent<SeamlessRiverCrossings>();
        }

        internal SeamlessRiverCrossings GetAnchors() => Anchors ??= SeamlessRiverCrossings.Ensure(Map);
    }

    [HarmonyPatch(typeof(TileMutatorWorker_RiverDelta), "SplitRiver")]
    static class Patch_TileMutatorWorker_RiverDelta_SplitRiver
    {
        static bool Prefix(TileMutatorWorker_RiverDelta __instance, Map map, RiverNode prevSegment)
        {
            var context = Patch_TileMutatorWorker_River_Generation.Current;
            if (context?.Worker != __instance || context.Map != map || !context.DeltaSingleOutlet.HasValue) return true;

            var graph = map.waterInfo.riverGraph;
            var outlet = context.DeltaSingleOutlet.Value;
            if (graph.Count != context.FirstNodeIndex + 1 || graph[context.FirstNodeIndex] != prevSegment ||
                !prevSegment.start.Equals(outlet.start)) return true;

            prevSegment.end = outlet.end;
            return false;
        }
    }

    readonly struct RiverSeamAnchor
    {
        internal readonly PlanetTile Neighbor;
        internal readonly Vector2 Crossing;
        internal readonly Vector2 Inward;

        internal RiverSeamAnchor(PlanetTile neighbor, Vector2 crossing, Vector2 inward)
        {
            Neighbor = neighbor;
            Crossing = crossing;
            Inward = inward;
        }
    }

    [HarmonyPatch(typeof(TileMutatorWorker_River), "IsFlowingAToB")]
    static class Patch_TileMutatorWorker_River_IsFlowingAToB
    {
        static bool Prefix(TileMutatorWorker_River __instance, Vector3 a, Vector3 b, ref bool __result)
        {
            var context = Patch_TileMutatorWorker_River_Generation.Current;
            if (context?.Worker != __instance || !context.OrderedEndpoints.HasValue) return true;

            var (start, end) = context.OrderedEndpoints.Value;
            if (!start.Equals(a) || !end.Equals(b)) return true;

            // The pair already has a direction from the world links. The chord need not match the world bearing.
            context.OrderedEndpoints = null;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    static class Patch_TileMutatorWorker_River_BranchedGraph
    {
        private static readonly Func<TileMutatorWorker_River, RiverNode, float, Vector2> SampleCenterline =
            AccessTools.MethodDelegate<Func<TileMutatorWorker_River, RiverNode, float, Vector2>>(
                AccessTools.DeclaredMethod(typeof(TileMutatorWorker_River), "GetDisplacedPoint"));

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverConfluence), "GenerateRiverGraph");
            yield return AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverDelta), "GenerateRiverGraph");
        }

        static void Postfix(TileMutatorWorker_River __instance, Map map)
        {
            var context = Patch_TileMutatorWorker_River_Generation.Current;
            if (context?.Worker != __instance || context.Map != map) return;

            var graph = map.waterInfo.riverGraph;
            if (graph.Count <= context.FirstNodeIndex) return;

            if (context.DeltaInlet is RiverSeamAnchor inlet)
            {
                var root = graph[context.FirstNodeIndex];
                if (context.DeltaSingleOutlet is { } outlet && root.end.Equals(outlet.end))
                {
                    context.DeltaMergeT = Patch_TileMutatorWorker_River_GetMapEdgeNodes.RegisterChord(
                        context, inlet, outlet.anchor, root.start, root.end);
                    if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                        Log.Message($"[RimExodus] RiverDelta single outlet tile={map.Tile} " +
                                    $"inlet={inlet.Neighbor} outlet={outlet.anchor.Neighbor} start={root.start} end={root.end}");
                }
                else RegisterAnchor(context, root, inlet);

                foreach (var (anchor, width) in context.DeltaTributaries)
                    AddDeltaTributary(context, root, anchor, width);
                return;
            }

            var inlets = context.ConfluenceInlets;
            if (inlets == null || inlets.Count == 0 ||
                graph.Count != context.FirstNodeIndex + ((SurfaceTile)map.TileInfo).Rivers.Count - 1) return;

            // Vanilla places the land inlets immediately after the main channel, all ending at the same join.
            // Move only their external endpoints. The water outlets, if any, follow them in the graph.
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var vertices = SeamlessPolygonGeometry.BuildPolygonVertices(worldTile, map.Size.x);
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, neighbors);
            for (var i = 0; i < inlets.Count; i++)
            {
                if (!Patch_TileMutatorWorker_River_GetMapEdgeNodes.TryResolveAnchor(
                    map, worldTile, vertices, neighbors, inlets[i], out var anchor)) continue;

                AnchorConfluenceInlet(context, graph[context.FirstNodeIndex + 1 + i], anchor);
            }
        }

        internal static void AnchorConfluenceInlet(RiverGenerationContext context, RiverNode node, RiverSeamAnchor anchor)
        {
            if (!TryGetInletStart(context.Map.Size.x, node.end, anchor, out var start)) return;

            node.start = start;
            RegisterAnchor(context, node, anchor);
        }

        internal static void AddDeltaTributary(RiverGenerationContext context, RiverNode root, RiverSeamAnchor anchor, float width)
        {
            var mergeT = context.DeltaMergeT;
            if (!mergeT.HasValue)
            {
                if (!context.GetAnchors().singleAnchors.TryGetValue(root, out var inletT)) return;
                mergeT = Mathf.Lerp(inletT, 1f, 0.5f);
            }

            // Sample the same displaced centerline the depth field uses, leaving an inland reach below the join.
            var merge = SampleCenterline(context.Worker, root, mergeT.Value);
            var join = new Vector3(merge.x, 0f, merge.y);
            if (!TryGetInletStart(context.Map.Size.x, join, anchor, out var start)) return;

            RiverNode tributary;
            using (new RandBlock(Gen.HashCombineInt(root.seed, anchor.Neighbor.tileId)))
                tributary = new RiverNode { start = start, end = join, width = width };
            context.Map.waterInfo.riverGraph.Add(tributary);
            RegisterAnchor(context, tributary, anchor);
        }

        private static bool TryGetInletStart(int mapSize, Vector3 join, RiverSeamAnchor anchor, out Vector3 start)
        {
            start = default;
            var towardJoin = new Vector2(join.x, join.z) - anchor.Crossing;
            if (Vector2.Dot(towardJoin, anchor.Inward) <= 0.01f) return false;
            var (outside, _) = Patch_TileMutatorWorker_River_GetMapEdgeNodes.ExtendToRect(
                anchor.Crossing, towardJoin, mapSize);
            start = new Vector3(outside.x, 0f, outside.y);
            return true;
        }

        private static void RegisterAnchor(RiverGenerationContext context, RiverNode node, RiverSeamAnchor anchor)
        {
            var start = new Vector2(node.start.x, node.start.z);
            var end = new Vector2(node.end.x, node.end.z);
            var direction = end - start;
            var lengthSquared = direction.sqrMagnitude;
            if (lengthSquared < 1e-6f) return;

            var crossingT = Vector2.Dot(anchor.Crossing - start, direction) / lengthSquared;
            if (crossingT <= 0f || crossingT >= 1f) return;
            if ((start + direction * crossingT - anchor.Crossing).sqrMagnitude > 1e-4f) return;

            var anchors = context.GetAnchors();
            anchors.crossings[anchor.Neighbor] = anchor.Crossing;
            anchors.singleAnchors[node] = crossingT;
            if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                Log.Message($"[RimExodus] {context.Worker.GetType().Name} tile={context.Map.Tile} " +
                            $"crossing={anchor.Crossing} neighbor={anchor.Neighbor} start={node.start} end={node.end} t={crossingT:F3}");
        }
    }
}
