using System;
using System.Collections.Generic;
using System.Linq;
using RimExodus.Maps;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal static class SeamlessFrameworkArchive
    {
        internal static MapPrefabDocument Capture(Map map, ZoneMapRecord record)
        {
            var doc = MapPrefabs.Capture(map, new CellSelection(record.zoneCells), "outpost-" + record.worldTile,
                t => t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item,
                null, false, true);
            // Natural terrain belongs to the next generation, never to the shared prefab.
            doc.terrain.RemoveAll(t => !t.def.IsFloor && !t.def.IsSubstructure);
            return doc;
        }

        internal static MapPrefabDocument Read(ZoneMapRecord record)
        {
            if (!string.IsNullOrEmpty(record.frameworkPrefabXml))
                return MapPrefabDocument.FromXml(record.frameworkPrefabXml);
            // Old archives retain their sparse fidelity. Convert at restoration, with Scribe inactive.
            var bounds = CellRect.FromCellList(record.zoneCells);
            var doc = new MapPrefabDocument { name = "legacy-outpost-" + record.worldTile,
                sourceOrigin = bounds.Min, size = bounds.Size,
                mask = record.zoneCells.Select(c => c - bounds.Min).ToList() };
            foreach (var b in record.buildings)
                AddThing(doc, b.def, b.stuff, b.cell, new Rot4(b.rotInt), b.hp, 1, b.qualityInt,
                    ZoneMapRecord.ResolveFaction(b.factionLoadID));
            foreach (var i in record.items)
                AddThing(doc, i.def, i.stuff, i.cell, Rot4.North, i.hp, i.count, i.qualityInt, null);
            foreach (var f in record.floors)
            {
                if (f.def == null) continue;
                var c = f.cell - bounds.Min;
                if (f.def.isFoundation) doc.foundations.Add(new PrefabFoundationEntry { pos = c, def = f.def });
                else doc.terrain.Add(new PrefabTerrainData { def = f.def,
                    rects = new List<CellRect> { new CellRect(c.x, c.z, 1, 1) } });
            }
            foreach (var r in record.roofs)
                if (r.def != null) doc.roofs.Add(new PrefabRoofEntry { pos = r.cell - bounds.Min, def = r.def });
            return doc;
        }

        private static void AddThing(MapPrefabDocument doc, ThingDef def, ThingDef stuff, IntVec3 cell,
            Rot4 rot, int hp, int count, int quality, Faction faction)
        {
            if (def == null) return;
            var thing = ThingMaker.MakeThing(def, stuff);
            thing.Rotation = rot;
            thing.stackCount = Mathf.Clamp(count, 1, def.stackLimit);
            if (def.useHitPoints && hp > 0) thing.HitPoints = Mathf.Clamp(hp, 1, thing.MaxHitPoints);
            if (quality >= 0) thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)quality, null);
            if (def.CanHaveFaction) thing.SetFactionDirect(faction);
            doc.thingEntries.Add(new MapPrefabThingEntry { def = def, stuff = stuff, pos = cell - doc.sourceOrigin,
                rotation = rot, hitPoints = thing.HitPoints, stackCount = thing.stackCount,
                stateXml = MapPrefabThingState.Capture(thing) });
        }

        internal static PrefabPlacementResult Rebuild(Map map, ZoneMapRecord record, HashSet<IntVec3> keptCells,
            Func<IntVec3, IntVec3> findFree)
        {
            var doc = Read(record);
            var buildingCells = new HashSet<IntVec3>();
            foreach (var entry in doc.thingEntries)
                if (entry.def.category == ThingCategory.Building)
                    foreach (var cell in GenAdj.OccupiedRect(entry.pos + doc.sourceOrigin, entry.rotation, entry.def.Size))
                        buildingCells.Add(cell);
            // RE clears natural obstacles and preserves foreign buildings before REF touches any layer.
            foreach (var entry in doc.thingEntries)
            {
                if (entry.def.category != ThingCategory.Building) continue;
                var footprint = GenAdj.OccupiedRect(entry.pos + doc.sourceOrigin, entry.rotation, entry.def.Size);
                if (footprint.Any(keptCells.Contains)) continue;
                foreach (var c in footprint)
                {
                    if (!c.InBounds(map)) throw new InvalidOperationException("Outpost footprint lies outside its map.");
                    foreach (var thing in map.thingGrid.ThingsListAt(c).ToArray())
                    {
                        if (thing.Destroyed) continue;
                        if (thing.def.category == ThingCategory.Building)
                        {
                            if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                            if (!thing.Destroyed) foreach (var occupied in thing.OccupiedRect()) keptCells.Add(occupied);
                        }
                        else if (thing.def.category == ThingCategory.Plant) thing.Destroy(DestroyMode.Vanish);
                        else if (thing.def.category == ThingCategory.Item)
                        {
                            var free = IntVec3.Invalid;
                            // Search Chebyshev rings outside every restored building footprint.
                            for (int radius = 1; radius <= 14 && !free.IsValid; radius++)
                                for (int x = -radius; x <= radius && !free.IsValid; x++)
                                    for (int z = -radius; z <= radius; z++)
                                    {
                                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                                        var candidate = thing.Position + new IntVec3(x, 0, z);
                                        if (candidate.InBounds(map) && !buildingCells.Contains(candidate) && candidate.Standable(map))
                                        { free = candidate; break; }
                                    }
                            if (!free.IsValid) throw new InvalidOperationException("No free cell for generated item; retain outpost record for retry.");
                            thing.DeSpawn();
                            GenSpawn.Spawn(thing, free, map, WipeMode.Vanish);
                        }
                    }
                }
            }
            var prefab = doc.BuildPrefabDef();
            var position = doc.sourceOrigin - PrefabUtility.GetRoot(prefab, IntVec3.Zero, Rot4.North);
            var result = MapPrefabs.Rebuild(doc, map, position, Rot4.North, new PrefabPlacement
            {
                Clone = true, PreserveFactions = true,
                ProtectCell = (m, c) => keptCells.Contains(c),
                PlaceThing = (thing, m, c, rot) =>
                {
                    if (thing.def.category == ThingCategory.Item && !c.Standable(m)) c = findFree(c);
                    if (!c.IsValid) throw new InvalidOperationException("No free cell for archived item.");
                    return GenSpawn.Spawn(thing, c, m, rot, WipeMode.Vanish, respawningAfterLoad: true);
                }
            });
            if (!result.Success) throw new InvalidOperationException("REF outpost restore failed: " + string.Join("; ", result.Errors));
            return result;
        }
    }
}
