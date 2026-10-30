using System;
using System.Reflection;
using HarmonyLib;
using RimExodus.Maps;
using Verse;

namespace RimExodus
{
    /// <summary>SS's array-indexing algorithms read a bounded obstacle snapshot. This detached Map is
    /// only an adapter for those read-only entry points: never register, initialize, tick, save or dispose
    /// it through game lifecycle code. Buildings retain their real Map and Position throughout.</summary>
    internal sealed class SeamlessSurveillanceGrid
    {
        private static readonly FieldInfo Roofs = AccessTools.Field(typeof(RoofGrid), "roofGrid");
        private static readonly Building Unavailable = new Building
        {
            def = new ThingDef { defName = "RimExodus_UnavailableVisionCell", fillPercent = 1f }
        };
        internal readonly MapCellGrid Addresses;
        internal readonly Map Data;
        internal readonly bool[] Visible;
        internal SeamlessSurveillanceGrid(MapCellSpace space, CellRect bounds)
        {
            Addresses = space.CaptureGrid(bounds);
            Data = new Map();
            Data.info.Size = new IntVec3(bounds.Width, 1, bounds.Height);
            Data.cellIndices = new CellIndices(Data);
            Data.edificeGrid = new EdificeGrid(Data);
            Data.roofGrid = new RoofGrid(Data);
            var roofs = (RoofDef[])Roofs.GetValue(Data.roofGrid);
            var edifices = Data.edificeGrid.InnerArray;
            Visible = new bool[Addresses.Count];
            for (int i = 0; i < Addresses.Count; i++)
            {
                var address = Addresses[i];
                if (!address.IsValid) { edifices[i] = Unavailable; continue; }
                int index = address.Map.cellIndices.CellToIndex(address.Cell);
                edifices[i] = address.Map.edificeGrid.InnerArray[index];
                roofs[i] = address.Map.roofGrid.RoofAt(index);
            }
        }
        internal bool HasCoverage(IntVec3 origin, IntVec3 target)
        {
            foreach (var cell in GenSight.PointsOnLineOfSight(origin, target))
                if (!Addresses.TryResolve(cell, out _)) return false;
            return Addresses.TryResolve(target, out _);
        }
        internal void Publish(Map source, bool[] sourceVisible, IntVec3 origin, bool seeOverMountains,
            Action<MapCellAddress> crossMap)
        {
            for (int i = 0; i < Visible.Length; i++)
            {
                if (!Visible[i]) continue;
                var address = Addresses[i];
                if (!address.IsValid) continue;
                // SS high towers deliberately ignore walls. Missing maps must still stop vision.
                if (seeOverMountains && !HasCoverage(origin, Addresses.ReferenceCell(i))) continue;
                if (address.Map == source) sourceVisible[source.cellIndices.CellToIndex(address.Cell)] = true;
                else crossMap(address);
            }
        }
    }
}
