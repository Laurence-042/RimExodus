#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal static partial class SeamlessEndlessHorizonCompat
    {
        private static readonly Dictionary<MethodBase, (MethodInfo replacement, int count, int index)> cellQueries = new();
        private static readonly MethodInfo stockInBounds = AccessTools.Method(typeof(GenGrid), "InBounds", new[] { typeof(IntVec3), typeof(Map) });
        private static MethodInfo peek, markDirty;
        private static FieldInfo drawerMap, meshDirtySuppressed;

        private sealed record Patch(MethodBase Target, HarmonyMethod Prefix = null,
            HarmonyMethod Postfix = null, HarmonyMethod Transpiler = null);

        private static List<Patch> BindPatches()
        {
            var list = new List<Patch>();
            void Prefix(Type type, string method, string hook) => list.Add(new(Method(type, method), Prefix: Hook(hook)));
            void Postfix(Type type, string method, string hook) => list.Add(new(Method(type, method), Postfix: Hook(hook)));
            void ReplaceQuery(MethodBase method, string replacement, int count, int callIndex = 0)
            {
                cellQueries.Add(method, (Method(typeof(SeamlessEndlessHorizonCompat), replacement), count, callIndex));
                list.Add(new(method, Transpiler: Hook(nameof(VisualCellQueryTranspiler))));
            }

            var state = Type("HorizonState");
            var field = Type("HorizonField");
            var chunk = Type("HorizonChunk");
            var things = Type("HorizonThings");
            var river = Type("RiverExtension");
            peek = Method(Type("HorizonRegistry"), "Peek");
            markDirty = Method(state, "MarkSeamDirty");
            drawerMap = Field(typeof(MapDrawer), "map");
            meshDirtySuppressed = Field(Type("Patch_MapMeshDirty"), "Suppress");
            list.Add(new(AccessTools.Method(typeof(MapDrawer), "MapMeshDirty",
                new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) }), Postfix: Hook(nameof(MeshDirtyPostfix))));

            list.Add(new(Iterator(state, "BuildSteps"), Transpiler: Hook(nameof(ChunkLoopTranspiler))));
            list.Add(new(Method(state, "Draw"), Transpiler: Hook(nameof(HorizonViewTranspiler))));
            ReplaceQuery(Iterator(field, "BuildSteps"), nameof(OutsideFieldCell), 1);
            ReplaceQuery(Method(field, "AnyWaterIn"), nameof(PlayableCell), 1);
            ReplaceQuery(Method(field, "FrozenAt"), nameof(PlayableCell), 1);
            ReplaceQuery(Method(chunk, "TerrainAt"), nameof(PlayableCell), 1);
            ReplaceQuery(Method(chunk, "Regenerate"), nameof(PlayableCell), 3);
            ReplaceQuery(Iterator(things, "BuildSteps"), nameof(PlayableCell), 3);
            ReplaceQuery(Method(things, "AddWeatherMask"), nameof(PlayableCell), 1);
            ReplaceQuery(Method(Type("SnowLayer"), "BuildChunk"), nameof(PlayableCell), 1);
            Prefix(field, "Clamp", nameof(ClampPrefix));
            Prefix(field, "OutsideTerrain", nameof(SampleSeamPrefix));
            Prefix(field, "OutsideFogAt", nameof(OutsideFogPrefix));
            Postfix(field, "Covers", nameof(CoversPostfix));

            BindRiver(river);
            Prefix(river, "FindCrossings", nameof(FindCrossingsPrefix));
            Prefix(river, "Trace", nameof(SkipRectangularBoundary));
            Prefix(river, "MatchWorldLink", nameof(SkipRectangularBoundary));
            list.Add(new(Method(river, "Stamp"), Transpiler: Hook(nameof(RiverStampTranspiler))));
            BindCoast(field);
            Prefix(field, "ApplyCoastBoundary", nameof(CoastPrefix));
            Prefix(field, "CaptureBaseBoundary", nameof(CaptureBoundaryPrefix));
            var fog = Type("HorizonFog");
            BindFog(fog);
            Prefix(fog, "ComputeBase", nameof(FogDistancePrefix));
            list.Add(new(AccessTools.Constructor(fog, new[] { typeof(Map), typeof(int), typeof(int) }),
                Postfix: Hook(nameof(FogMeshPostfix))));
            // The stock far lighting plane dims every map outside the rectangular ring.
            Prefix(Type("HorizonLighting"), "Draw", nameof(SkipRectangularBoundary));
            return list;
        }

        private static IEnumerable<CodeInstruction> VisualCellQueryTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            var (replacement, count, index) = cellQueries[__originalMethod];
            var sites = codes.Where(code => code.Calls(stockInBounds)).ToList();
            if (sites.Count != count) throw new InvalidOperationException($"Unexpected InBounds sites in {__originalMethod}: {sites.Count}");
            sites[index].operand = replacement;
            return codes;
        }

        private static IEnumerable<CodeInstruction> HorizonViewTranspiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            var codes = instructions.ToList();
            var getter = AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect));
            var sites = codes.Where(code => code.Calls(getter)).ToList();
            if (sites.Count != 1) throw new InvalidOperationException($"Unexpected camera view sites in {__originalMethod}: {sites.Count}");
            sites[0].opcode = OpCodes.Call;
            sites[0].operand = Method(typeof(SeamlessEndlessHorizonCompat), nameof(HorizonView));
            return codes;
        }

        // The stock iterator skips chunks strictly within the four rectangle sides. Keep this path for stock maps.
        private static IEnumerable<CodeInstruction> ChunkLoopTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = instructions.ToList();
            var start = codes.FindIndex(code => code.LoadsField(Field(typeof(CellRect), "minX"))) - 2;
            if (start < 1 || codes[start + 1].opcode != OpCodes.Ldflda || codes[start + 1].operand is not FieldInfo rect
                || rect.FieldType != typeof(CellRect) || codes[start + 4].operand is not Label drawChunk
                || codes[start + 29].opcode != OpCodes.Blt || codes[start + 29].operand is not Label skipChunk)
                throw new InvalidOperationException("Unexpected Endless Horizon chunk boundary loop");
            var stock = generator.DefineLabel();
            var first = CodeInstruction.Call(typeof(SeamlessEndlessHorizonCompat), nameof(HasDrawingGeometry));
            first.labels.AddRange(codes[start].labels);
            codes[start].labels.Clear();
            codes[start].labels.Add(stock);
            codes.InsertRange(start, new[]
            {
                first, new(OpCodes.Brfalse, stock), new(OpCodes.Ldarg_0), new(OpCodes.Ldfld, rect),
                CodeInstruction.Call(typeof(SeamlessEndlessHorizonCompat), nameof(IncludeChunk)),
                new(OpCodes.Brtrue, drawChunk), new(OpCodes.Br, skipChunk)
            });
            return codes;
        }

        // Stamp has an InGrid check followed by a separate rectangle exclusion. Preserve the buffer bounds check.
        private static IEnumerable<CodeInstruction> RiverStampTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = instructions.ToList();
            var inGrid = codes.FindIndex(code => code.Calls(Method(Type("RiverExtension"), "InGrid")));
            var start = inGrid + 2;
            if (inGrid < 3 || codes[inGrid + 1].operand is not Label skipCell
                || codes[start + 2].opcode != OpCodes.Blt_S || codes[start + 2].operand is not Label stampCell)
                throw new InvalidOperationException("Unexpected Endless Horizon river stamp boundary");
            var stock = generator.DefineLabel();
            var first = CodeInstruction.Call(typeof(SeamlessEndlessHorizonCompat), nameof(HasDrawingGeometry));
            first.labels.AddRange(codes[start].labels);
            codes[start].labels.Clear();
            codes[start].labels.Add(stock);
            codes.InsertRange(start, new[]
            {
                first, new(OpCodes.Brfalse, stock), new(codes[inGrid - 2].opcode, codes[inGrid - 2].operand),
                new(codes[inGrid - 1].opcode, codes[inGrid - 1].operand),
                CodeInstruction.Call(typeof(SeamlessEndlessHorizonCompat), nameof(IncludeRiverCell)),
                new(OpCodes.Brtrue, stampCell), new(OpCodes.Br, skipCell)
            });
            return codes;
        }

        private static bool HasDrawingGeometry() => drawing != null;
        private static bool IncludeChunk(CellRect rect) => drawing.DrawChunk(rect, drawing.Radius);
        private static bool IncludeRiverCell(int x, int z) => drawing.DrawCell(new IntVec3(x, 0, z), drawing.Radius);
        private static bool SkipRectangularBoundary() => drawing == null;
    }
}
#endif
