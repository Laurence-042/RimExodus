using Verse;

// Only the host/gameplay services are fixtures. ShadowCaster.cs and ScopeView.cs are linked unchanged
// from the user's reference snapshot; none of their algorithms are copied into the product or this test.
namespace SurveillanceShift
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HotSwappableAttribute : Attribute { }
    public static class ModCompatibility
    {
        public static readonly HashSet<ThingDef> SeeThroughWallDefs = new();
        public static bool IsSeeThroughWall(Building building) => SeeThroughWallDefs.Contains(building.def);
        public static Pawn GetPerspectiveShiftAvatar() => null;
        public static void ClearPerspectiveShiftCameraLock() { }
    }
    public static class DefsOf
    {
        public static ThingDef SS_Mote_Binoculars, SS_Mote_Spyglass, SS_Binoculars, SS_Telescope;
    }
    public sealed class MapComponent_FogOfWar : MapComponent
    {
        private bool[] visibleCells;
        public Action<MapComponent_FogOfWar> Calculate;
        public int Publications;
        public bool[] Cells => visibleCells;
        public MapComponent_FogOfWar(Map map) : base(map) { visibleCells = new bool[map.cellIndices.NumGridCells]; }
        public void RecalculateVisibility(bool gravshipReveal = false, HashSet<IntVec3> excludeCells = null)
        { Array.Clear(visibleCells); Calculate?.Invoke(this); ApplyFog(); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ApplyFog() { Publications++; }
        public override void MapComponentTick() { RecalculateVisibility(); }
        public void RefreshScopeOverlay() { }
    }
}
