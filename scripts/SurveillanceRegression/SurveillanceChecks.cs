using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using RimExodus.Maps;
using Verse;

internal static class SurveillanceChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
        // Compile the unmodified reference algorithms against 1.7. Host-only SS services are fixtures.
        var ss = typeof(SurveillanceShift.ShadowCaster).Assembly;
        var shadow = ss.GetType("SurveillanceShift.ShadowCaster", true)!;
        var full = AccessTools.Method(shadow, "ComputeFieldOfView");
        var cone = AccessTools.Method(shadow, "ComputeConeFieldOfView");
        var circle = AccessTools.Method(ss.GetType("SurveillanceShift.ScopeView", true), "ApplyCircle");
        var re = typeof(RimExodus.RimExodusMod).Assembly;
        var adapter = re.GetType("RimExodus.SeamlessSurveillanceGrid", true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
        object Capture(MapCellSpace space, CellRect bounds) => Activator.CreateInstance(adapter, flags, null, new object[] { space, bounds }, null)!;
        T Read<T>(object grid, string field) => (T)adapter.GetField(field, flags)!.GetValue(grid)!;
        Map Map(int width, int height)
        {
            var map = new Map(); map.info.Size = new IntVec3(width, 1, height);
            map.cellIndices = new CellIndices(map); map.edificeGrid = new EdificeGrid(map); map.roofGrid = new RoofGrid(map);
            return map;
        }
        var a = Map(12, 12); var b = Map(12, 12);
        var space = new MapCellSpace(a, new[] { new MapCellPlacement(a, IntVec3.Zero, _ => true),
            new MapCellPlacement(b, new IntVec3(12, 0, 0), _ => true) });
        var bounds = new CellRect(-1, -1, 27, 15);
        var origin = new IntVec3(10, 0, 6);
        bool[] Calculate(bool high = false, Rot4? rot = null)
        {
            object grid = Capture(space, bounds);
            var localOrigin = Read<MapCellGrid>(grid, "Addresses").ToGrid(origin);
            var map = Read<Map>(grid, "Data"); var output = Read<bool[]>(grid, "Visible");
            if (rot.HasValue) cone.Invoke(null, new object[] { localOrigin, 9, rot.Value, 0.5f, map, output, high });
            else full.Invoke(null, new object[] { localOrigin, 9, map, output, high });
            var source = new bool[a.cellIndices.NumGridCells]; var target = new bool[b.cellIndices.NumGridCells];
            Action<MapCellAddress> publish = cell => { if (cell.Map == b) target[b.cellIndices.CellToIndex(cell.Cell)] = true; };
            adapter.GetMethod("Publish", flags)!.Invoke(grid, new object[] { a, source, origin, high, publish });
            return target;
        }
        int At(int x, int z) => b.cellIndices.CellToIndex(new IntVec3(x, 0, z));
        Check(Calculate()[At(3, 6)], "Actual SS circular FOV crosses seam");
        Check(Calculate(rot: Rot4.East)[At(3, 6)], "Actual SS cone preserves facing across seam");
        Check(!Calculate(rot: Rot4.West)[At(3, 6)], "Opposite cone does not reveal neighbor");
        var wall = new Building { def = new ThingDef { defName = "TestOpaqueWall", fillPercent = 1 } };
        for (int z = 0; z < 12; z++) b.edificeGrid.InnerArray[At(1, z)] = wall;
        var masked = Calculate();
        Check(masked[At(1, 6)] && !masked[At(3, 6)], "Target wall visible, cells behind target wall occluded");
        var originalMap = wall.Map; var originalPosition = wall.Position;
        Check(Calculate(true)[At(3, 6)], "High tower retains SS wall-overlook behavior");
        Check(wall.Map == originalMap && wall.Position == originalPosition, "Snapshot never moves real buildings");
        var roofs = (RoofDef[])AccessTools.Field(typeof(RoofGrid), "roofGrid").GetValue(b.roofGrid)!;
        roofs[At(3, 6)] = new RoofDef();
        Check(!Calculate(true)[At(3, 6)], "High tower respects target roof");
        var glassDefs = (HashSet<ThingDef>)AccessTools.Field(ss.GetType("SurveillanceShift.ModCompatibility"), "SeeThroughWallDefs").GetValue(null)!;
        glassDefs.Add(wall.def);
        Check(Calculate()[At(3, 6)], "SS transparent-wall compatibility is reused");
        glassDefs.Remove(wall.def);
        space = new MapCellSpace(a, new[] { new MapCellPlacement(a, IntVec3.Zero, _ => true),
            new MapCellPlacement(b, new IntVec3(14, 0, 0), _ => true) });
        Check(!Calculate(true)[At(0, 6)], "High tower cannot see across unavailable map gap");
        Check(!Calculate()[At(0, 6)], "Ordinary vision cannot see across unavailable map gap");
        space = new MapCellSpace(a, new[] { new MapCellPlacement(a, IntVec3.Zero, _ => true),
            new MapCellPlacement(b, new IntVec3(12, 0, 0), _ => true) });
        object optical = Capture(space, bounds);
        var addresses = Read<MapCellGrid>(optical, "Addresses");
        circle.Invoke(null, new object[] { Read<Map>(optical, "Data"), addresses.ToGrid(origin), addresses.ToGrid(new IntVec3(14, 0, 6)), 4, Read<bool[]>(optical, "Visible") });
        Check(Read<bool[]>(optical, "Visible")[(6 - bounds.minZ) * bounds.Width + 13 - bounds.minX], "Actual SS optical ray reaches neighbor wall");
        Check(!Read<bool[]>(optical, "Visible")[(6 - bounds.minZ) * bounds.Width + 15 - bounds.minX], "Actual SS optical ray stops behind neighbor wall");

        var batchType = re.GetType("RimExodus.SeamlessSurveillanceCompat+Batch", true)!;
        var batch = Activator.CreateInstance(batchType, true)!;
        var components = (IDictionary)batchType.GetField("Components", flags)!.GetValue(batch)!;
        var ca = new FogFixture(a); var cb = new FogFixture(b);
        components.Add(a, ca); components.Add(b, cb);
        batchType.GetField("Source", flags)!.SetValue(batch, a);
        batchType.GetMethod("Add", flags)!.Invoke(batch, new object[] { new MapCellAddress(b, new IntVec3(2, 0, 2)) });
        Array.Clear(cb.Cells); // Destination recalculation happens after source calculation.
        batchType.GetMethod("Merge", flags)!.Invoke(batch, new object[] { typeof(FogFixture).GetField(nameof(FogFixture.Cells))! });
        Check(cb.Cells[At(2, 2)], "Destination recalculation cannot erase staged source contribution");
        batchType.GetMethod("ClearSource", flags)!.Invoke(batch, null); Array.Clear(cb.Cells);
        batchType.GetMethod("Merge", flags)!.Invoke(batch, new object[] { typeof(FogFixture).GetField(nameof(FogFixture.Cells))! });
        Check(!cb.Cells[At(2, 2)], "Optical replacement removes earlier ordinary cross-map contribution");
        batchType.GetField("Source", flags)!.SetValue(batch, b);
        batchType.GetMethod("Add", flags)!.Invoke(batch, new object[] { new MapCellAddress(a, new IntVec3(2, 0, 2)) });
        batchType.GetMethod("Merge", flags)!.Invoke(batch, new object[] { typeof(FogFixture).GetField(nameof(FogFixture.Cells))! });
        Check(!ca.Cells[a.cellIndices.CellToIndex(new IntVec3(2, 0, 2))], "Incoming neighbor visibility cannot widen an optical replacement");
        var compat = re.GetType("RimExodus.SeamlessSurveillanceCompat", true)!;
        var harmony = new Harmony("RE.Regression.Surveillance");
        AccessTools.Method(compat, "Bind").Invoke(null, new object[] { harmony });
        Check((bool)AccessTools.Field(compat, "installed").GetValue(null)!, "Production binder installs all eight hooks");
        Check(harmony.GetPatchedMethods().Count() == 8, "Eight methods actually patched by Harmony");
        // Exercise ref-argument routing through the real unmodified shadow caster, not just the grid helper.
        batchType.GetField("Source", flags)!.SetValue(batch, a);
        ((IDictionary)batchType.GetField("Spaces", flags)!.GetValue(batch)!).Add(a, space);
        AccessTools.Field(compat, "active").SetValue(null, batch);
        Array.Clear(b.edificeGrid.InnerArray); Array.Clear(cb.Cells);
        try { full.Invoke(null, new object[] { origin, 9, a, ca.Cells, false }); }
        finally { AccessTools.Field(compat, "active").SetValue(null, null); }
        batchType.GetMethod("Merge", flags)!.Invoke(batch, new object[] { typeof(FogFixture).GetField(nameof(FogFixture.Cells))! });
        Check(cb.Cells[At(3, 6)], "Harmony ref arguments redirect SS and publish to real destination");
        var sa = new SurveillanceShift.MapComponent_FogOfWar(a);
        var sb = new SurveillanceShift.MapComponent_FogOfWar(b);
        sa.Calculate = component => SurveillanceShift.ShadowCaster.ComputeFieldOfView(origin, 9, a, component.Cells);
        var reverse = new MapCellSpace(b, new[] { new MapCellPlacement(b, IntVec3.Zero, _ => true),
            new MapCellPlacement(a, new IntVec3(-12, 0, 0), _ => true) });
        object NewBatch(bool reversed)
        {
            var result = Activator.CreateInstance(batchType, true)!;
            var members = (IDictionary)batchType.GetField("Components", flags)!.GetValue(result)!;
            foreach (var component in reversed ? new[] { sb, sa } : new[] { sa, sb }) members.Add(component.map, component);
            var spaces = (IDictionary)batchType.GetField("Spaces", flags)!.GetValue(result)!;
            spaces.Add(a, space); spaces.Add(b, reverse);
            return result;
        }
        void Execute(bool reversed) => AccessTools.Method(compat, "ExecuteBatch").Invoke(null, new object[] { NewBatch(reversed), sa, false, null, null, 1 });
        Execute(false);
        Check(sb.Cells[At(3, 6)] && sa.Publications == 1 && sb.Publications == 1,
            $"Batch defers publication until both maps finish (visible={sb.Cells[At(3, 6)]}, publications={sa.Publications}/{sb.Publications}, failed={AccessTools.Field(compat, "failed").GetValue(null)})");
        Execute(true);
        Check(sb.Cells[At(3, 6)] && sa.Publications == 2 && sb.Publications == 2, "Reversed map update order produces same visibility");
        sa.Calculate = null;
        Execute(false);
        Check(!sb.Cells[At(3, 6)], "Next batch clears departed observer's cross-map visibility");
        sa.Calculate = component => SurveillanceShift.ShadowCaster.ComputeFieldOfView(origin, 9, a, component.Cells);
        bool once = true;
        sb.Calculate = _ => { if (once) { once = false; throw new InvalidOperationException("Injected SS calculation failure"); } };
        Execute(false);
        Check((bool)AccessTools.Field(compat, "failed").GetValue(null)!, "Batch failure disables adapter once");
        Check(!sb.Cells[At(3, 6)] && sa.Cells[a.cellIndices.CellToIndex(origin)], "Failure repairs native local visibility without staged cross-map output");
        harmony.UnpatchAll(harmony.Id);
        Console.WriteLine($"{checks} SS vision checks passed (unmodified reference algorithms, production grid/merger/binder; host services are fixtures).");
    }
    private sealed class FogFixture : MapComponent
    {
        public bool[] Cells;
        public FogFixture(Map map) : base(map) { Cells = new bool[map.cellIndices.NumGridCells]; }
    }
}
