using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

static class RiverSeamChecks
{
    internal static void Run(Assembly mod, Harmony harmony, Action<bool, string> check)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        Type Type(string name) => mod.GetType("RimExodus." + name, throwOnError: true)!;
        var workerType = typeof(TileMutatorWorker_River);
        foreach (var target in new[]
        {
            AccessTools.DeclaredMethod(workerType, "IsFlowingAToB"),
            AccessTools.DeclaredMethod(workerType, "GeneratePostTerrain"),
            AccessTools.DeclaredMethod(workerType, "GeneratePostFog"),
            AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverConfluence), "GenerateRiverGraph"),
            AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverDelta), "GenerateRiverGraph"),
            AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverDelta), "SplitRiver"),
            AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverIsland), "GetDisplacedPoint")
        })
            check(Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) == true, "river hook bound: " + target);

        var edgeNodes = Type("Patch_TileMutatorWorker_River_GetMapEdgeNodes");
        var guide = edgeNodes.GetMethod("TryBuildDeltaGuide", statics)!;
        var vertices = new List<Vector2> { new(20, 20), new(230, 20), new(230, 230), new(20, 230) };
        var inward = new Vector2(1, 0);
        object Anchor(Vector2 crossing, Vector2 normal, int tileId = 0) => Activator.CreateInstance(Type("RiverSeamAnchor"),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new PlanetTile(tileId, -1), crossing, normal }, null)!;
        void CheckDeltaGuide(Vector2 crossing, Vector2 bearing)
        {
            object[] args = { vertices, 250, Anchor(crossing, inward), bearing, null };
            check((bool)guide.Invoke(null, args)!, "delta guide is available");
            var (start, end) = ((Vector3, Vector3))args[4]!;
            var start2 = new Vector2(start.x, start.z);
            var end2 = new Vector2(end.x, end.z);
            var axis = end2 - start2;
            var crossingT = Vector2.Dot(crossing - start2, axis) / axis.sqrMagnitude;
            check(start.x < crossing.x && crossingT > 0 && crossingT < 0.2f, "delta root enters before its shortest possible end");
            check((start2 + axis * crossingT - crossing).sqrMagnitude < 1e-5f, "delta root passes exactly through its inlet");
            var longest = Vector2.Lerp(start2, end2, 0.5f);
            check(longest.x > 20 && longest.x < 230 && longest.y > 20 && longest.y < 230, "delta root ends inside even at maximum length");
        }
        CheckDeltaGuide(new Vector2(20, 100), new Vector2(0.8f, 0.6f));
        CheckDeltaGuide(new Vector2(20, 226), new Vector2(0.6f, 0.8f));

        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        var worker = (TileMutatorWorker_River)RuntimeHelpers.GetUninitializedObject(typeof(TileMutatorWorker_RiverDelta));
        var anchors = new SeamlessRiverCrossings(map);
        map.info = new MapInfo { Size = new IntVec3(250, 1, 250) };
        map.waterInfo = new WaterInfo(map);
        map.components = new List<MapComponent> { anchors };
        var contextType = Type("RiverGenerationContext");
        var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { worker, map }, null);
        var current = Type("Patch_TileMutatorWorker_River_Generation").GetField("Current", statics)!;
        var getBend = Type("Patch_TileMutatorWorker_River_GetDisplacedPoint").GetMethod("Postfix", statics)!;
        var direction = AccessTools.DeclaredMethod(workerType, "IsFlowingAToB");
        var node = (RiverNode)RuntimeHelpers.GetUninitializedObject(typeof(RiverNode));
        node.start = new Vector3(10, 0, 0);
        node.end = new Vector3(110, 0, 0);
        try
        {
            current.SetValue(null, context);
            AccessTools.Field(contextType, "OrderedEndpoints").SetValue(context, (node.start, node.end));
            check(!(bool)direction.Invoke(worker, new object[] { node.end, node.start, 60f })!,
                "other endpoint pairs keep vanilla direction checks");
            check((bool)direction.Invoke(worker, new object[] { node.start, node.end, 60f })!,
                "known world river direction survives a different map bearing");
            check(!(bool)direction.Invoke(worker, new object[] { node.start, node.end, 60f })!,
                "direction override is consumed by its own pair");

            var single = (Dictionary<RiverNode, float>)AccessTools.Field(typeof(SeamlessRiverCrossings), "singleAnchors").GetValue(anchors)!;
            var chords = (List<(Vector2, Vector2, float, float)>)AccessTools.Field(typeof(SeamlessRiverCrossings), "riverChords").GetValue(anchors)!;
            float Bend(float t)
            {
                object[] args = { node, t, new Vector2(10 + 100 * t, 10) };
                getBend.Invoke(null, args);
                return ((Vector2)args[2]).y;
            }

            single.Add(node, 0.25f);
            check(Math.Abs(Bend(0.25f)) < 1e-5 && Math.Abs(Bend(0.75f) - 10) < 1e-5,
                "single inlet removes bend at the seam and returns to the vanilla bend inland");
            single.Clear();
            chords.Add((new Vector2(10, 0), new Vector2(110, 0), 0.25f, 0.75f));
            float expected = 10f * (2f / (1f + Mathf.Exp(-0.5f)) - 1f);
            check(Math.Abs(Bend(0.25f)) < 1e-5 && Math.Abs(Bend(0.75f)) < 1e-5 &&
                Math.Abs(Bend(0.5f) - expected) < 1e-5, "ordinary rivers keep both sigmoid roots and the interior weight");

            var anchorBranch = Type("Patch_TileMutatorWorker_River_BranchedGraph").GetMethod("AnchorConfluenceInlet", statics)!;
            var join = new Vector3(120, 0, 120);
            foreach (var (tileId, crossing, normal) in new[]
            {
                (1, new Vector2(20, 75), inward),
                (2, new Vector2(150, 20), new Vector2(0, 1))
            })
            {
                var branch = (RiverNode)RuntimeHelpers.GetUninitializedObject(typeof(RiverNode));
                branch.start = new Vector3(0, 0, 0);
                branch.end = join;
                anchorBranch.Invoke(null, new[] { context, branch, Anchor(crossing, normal, tileId) });
                var point = Vector3.Lerp(branch.start, branch.end, single[branch]);
                check(branch.end.Equals(join) && (new Vector2(point.x, point.z) - crossing).sqrMagnitude < 1e-5f,
                    "each confluence branch crosses its own inlet and keeps the vanilla join");
            }

            var branchedGraph = Type("Patch_TileMutatorWorker_River_BranchedGraph");
            var addTributary = branchedGraph.GetMethod("AddDeltaTributary", statics)!;
            var registerAnchor = branchedGraph.GetMethod("RegisterAnchor", statics)!;
            // The two inlets on tile 52665 met at the first downstream split, producing an X.
            var deltaRoot = (RiverNode)RuntimeHelpers.GetUninitializedObject(typeof(RiverNode));
            deltaRoot.start = new Vector3(133.0404f, 0, 243.8365f);
            deltaRoot.end = new Vector3(69.84252f, 0, 205.6003f);
            deltaRoot.width = 14f;
            AccessTools.Field(workerType, "riverBendNoise").SetValue(worker, new Verse.Noise.Const(0.75));
            registerAnchor.Invoke(null, new[] { context, deltaRoot,
                Anchor(new Vector2(115.4804f, 233.2122f), new Vector2(0, -1), 118437) });
            var outlet = (RiverNode)RuntimeHelpers.GetUninitializedObject(typeof(RiverNode));
            outlet.start = deltaRoot.end;
            outlet.end = new Vector3(15.4f, 0, 93.8f);
            var secondOutlet = (RiverNode)RuntimeHelpers.GetUninitializedObject(typeof(RiverNode));
            secondOutlet.start = deltaRoot.end;
            secondOutlet.end = new Vector3(30, 0, 168);
            map.waterInfo.riverGraph.AddRange(new[] { deltaRoot, outlet, secondOutlet });
            var northeast = new Vector2(213.3f, 192.1f);
            var northeastAnchor = Anchor(northeast, new Vector2(-0.866f, -0.5f), 282340);
            int nextRandom;
            using (new RandBlock(4567)) nextRandom = Rand.Int;
            using (new RandBlock(4567))
            {
                addTributary.Invoke(null, new[] { context, deltaRoot, northeastAnchor, (object)6f });
                check(Rand.Int == nextRandom, "adding a delta tributary preserves the vanilla random stream");
            }

            var deltaGraph = map.waterInfo.riverGraph;
            var tributary = deltaGraph[3];
            var northeastPoint = Vector3.Lerp(tributary.start, tributary.end, single[tributary]);
            check(tributary.start.x > northeast.x && tributary.width == 6f &&
                (new Vector2(northeastPoint.x, northeastPoint.z) - northeast).sqrMagnitude < 1e-5f,
                "the northeast delta inlet still flows through its seam");
            var mergeT = Mathf.Lerp(single[deltaRoot], 1f, 0.5f);
            var merge = (Vector2)AccessTools.DeclaredMethod(workerType, "GetDisplacedPoint")
                .Invoke(worker, new object[] { deltaRoot, mergeT })!;
            var joinPoint = new Vector3(merge.x, 0, merge.y);
            check(tributary.end.Equals(joinPoint) && Vector3.Distance(joinPoint, deltaRoot.end) > 20f &&
                Vector3.Distance(joinPoint, Vector3.Lerp(deltaRoot.start, deltaRoot.end, mergeT)) > 1f,
                "delta inlet joins the curved north trunk well upstream of the delta split");
            check(deltaGraph.Count == 4 && deltaGraph[0] == deltaRoot && deltaGraph[1] == outlet && deltaGraph[2] == secondOutlet &&
                outlet.start.Equals(deltaRoot.end) && outlet.end.Equals(new Vector3(15.4f, 0, 93.8f)) &&
                secondOutlet.start.Equals(deltaRoot.end) && secondOutlet.end.Equals(new Vector3(30, 0, 168)),
                "adding the northeast inlet preserves both original delta outlets");

            // Tile 52665 has two confirmed land inlets and one water outlet. For that topology, even when
            // vanilla chose RiverDelta, the generated trunk must go to the only outlet without splitting.
            var northAnchor = Anchor(new Vector2(115.4804f, 233.2122f), new Vector2(0, -1), 118437);
            var southwest = new Vector2(15.4f, 93.8f);
            var southwestAnchor = Anchor(southwest, new Vector2(0.866f, 0.5f), 282371);
            var getChord = edgeNodes.GetMethod("TryGetChordEndpoints", statics)!;
            object[] chordArgs = { northAnchor, southwestAnchor, 250, null };
            check((bool)getChord.Invoke(null, chordArgs)!, "north-to-southwest outlet chord is available");
            var (chordStart, chordEnd) = ((Vector3, Vector3))chordArgs[3]!;
            var root = new RiverNode { start = chordStart, end = Vector3.Lerp(chordStart, chordEnd, 0.3f), width = 14f };
            deltaGraph.Clear();
            deltaGraph.Add(root);
            var split = AccessTools.DeclaredMethod(typeof(TileMutatorWorker_RiverDelta), "SplitRiver");
            var patchSplit = Type("Patch_TileMutatorWorker_RiverDelta_SplitRiver").GetMethod("Prefix", statics)!;
            check((bool)patchSplit.Invoke(null, new object[] { worker, map, root })!,
                "ordinary delta topology still runs vanilla SplitRiver");

            var anchorType = northAnchor.GetType();
            var outletPlan = typeof(ValueTuple<,,>).MakeGenericType(anchorType, typeof(Vector3), typeof(Vector3));
            AccessTools.Field(contextType, "DeltaSingleOutlet").SetValue(context,
                Activator.CreateInstance(outletPlan, southwestAnchor, chordStart, chordEnd));
            AccessTools.Field(contextType, "DeltaInlet").SetValue(context, northAnchor);
            var tributariesField = AccessTools.Field(contextType, "DeltaTributaries");
            var tributaries = (System.Collections.IList)Activator.CreateInstance(tributariesField.FieldType)!;
            var tributaryType = tributariesField.FieldType.GetGenericArguments()[0];
            tributaries.Add(Activator.CreateInstance(tributaryType, northeastAnchor, 6f));
            tributariesField.SetValue(context, tributaries);

            split.Invoke(worker, new object[] { map, root, 211.2f });
            branchedGraph.GetMethod("Postfix", statics)!.Invoke(null, new object[] { worker, map });
            check(deltaGraph.Count == 2 && root.childNodes.Count == 0 && root.end.Equals(chordEnd),
                "two inlets and one water outlet leave only the trunk and its tributary, without a downstream fork");
            var outletChord = chords[chords.Count - 1];
            var outletPoint = Vector3.Lerp(root.start, root.end, outletChord.Item4);
            check((new Vector2(outletPoint.x, outletPoint.z) - southwest).sqrMagnitude < 1e-5f,
                "the single channel goes to the shared southwest outlet");
            var newInlet = deltaGraph[1];
            var entryPoint = Vector3.Lerp(newInlet.start, newInlet.end, single[newInlet]);
            check((new Vector2(entryPoint.x, entryPoint.z) - northeast).sqrMagnitude < 1e-5f &&
                Vector3.Distance(newInlet.end, outletPoint) > 50f, "the northeast tributary joins upstream of the southwest outlet");
        }
        finally { current.SetValue(null, null); }
    }
}
