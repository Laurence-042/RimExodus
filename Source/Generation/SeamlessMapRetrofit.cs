using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 旧存档地图手动改造工具。设置页只暴露明确的三步：补传送点预览边界、移除传送点后搬迁、
    /// 最终不可逆 void 裁剪。前两步不写地形；第三步才捕获基础快照并把地图接入正常运行链。
    /// </summary>
    internal static class SeamlessMapRetrofit
    {
        internal static void OpenDialog()
        {
            if (!TryGetTarget(Find.CurrentMap, out _, out _, showFailure: true)) return;

            // Dialog_MessageBox 的布局顺序为 B（左）/C（中）/A（右），据此保持 1→2→3。
            var dialog = new Dialog_MessageBox(
                "RimExodus_MapRetrofitInstructions".Translate(),
                "RimExodus_MapRetrofitCrop".Translate(), RequestCrop,
                "RimExodus_MapRetrofitAddSpots".Translate(), AddTransferSpots,
                "RimExodus_MapRetrofitButton".Translate(), buttonADestructive: true);
            dialog.buttonCText = "RimExodus_MapRetrofitRemoveSpots".Translate();
            dialog.buttonCAction = RemoveTransferSpots;
            dialog.interactionDelay = 1f;
            Find.WindowStack.Add(dialog);
        }

        private static void AddTransferSpots()
        {
            if (!TryGetTarget(Find.CurrentMap, out var map, out var tile, showFailure: true)) return;
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, tile);
            RefreshExitBand(map);
            Messages.Message("RimExodus_MapRetrofitPreviewReady".Translate(),
                MessageTypeDefOf.PositiveEvent, false);
        }

        private static void RemoveTransferSpots()
        {
            if (!TryGetTarget(Find.CurrentMap, out var map, out _, showFailure: true)) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            if (def != null)
            {
                var spots = new List<Thing>(map.listerThings.ThingsOfDef(def));
                foreach (var spot in spots)
                    if (spot.Spawned) spot.DeSpawn();
            }
            RefreshExitBand(map);
            Messages.Message("RimExodus_MapRetrofitSpotsRemoved".Translate(),
                MessageTypeDefOf.NeutralEvent, false);
        }

        private static void RequestCrop()
        {
            if (IncrementalMapGenerator.IsAnyGenerating || SeamlessFramework.GenerationBusy)
            {
                Messages.Message("RimExodus_UninstallRestoreBlockedGenerating".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!TryGetTarget(Find.CurrentMap, out var map, out var tile, showFailure: true)) return;
            if (!HasTransferSpots(map))
            {
                Messages.Message("RimExodus_MapRetrofitPreviewRequired".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            var voidCells = BuildVoidCells(map, tile);
            var animals = new List<Pawn>();
            if (!CollectPawns(map, voidCells, animals, showBlocked: true)) return;

            var text = new StringBuilder("RimExodus_MapRetrofitConfirm".Translate().ToString());
            if (animals.Count > 0)
            {
                var entries = new StringBuilder();
                foreach (var animal in animals)
                {
                    if (entries.Length > 0) entries.AppendLine();
                    var key = animal.Faction == Faction.OfPlayer
                        ? "RimExodus_MapRetrofitColonyAnimalEntry"
                        : "RimExodus_MapRetrofitAnimalEntry";
                    entries.Append(key.Translate(animal.LabelShortCap, animal.KindLabel, animal.PositionHeld.ToString()));
                }
                text.AppendLine().AppendLine().Append(
                    "RimExodus_MapRetrofitAnimalWarning".Translate(animals.Count, entries.ToString()));
            }

            var confirm = Dialog_MessageBox.CreateConfirmation(
                text.ToString(), () => CropNow(map), destructive: true,
                title: "RimExodus_MapRetrofitCrop".Translate());
            confirm.interactionDelay = 3f;
            Find.WindowStack.Add(confirm);
        }

        private static void CropNow(Map expectedMap)
        {
            if (IncrementalMapGenerator.IsAnyGenerating || SeamlessFramework.GenerationBusy)
            {
                Messages.Message("RimExodus_UninstallRestoreBlockedGenerating".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (Find.CurrentMap != expectedMap
                || !TryGetTarget(expectedMap, out var map, out var tile, showFailure: true)) return;
            if (!HasTransferSpots(map))
            {
                Messages.Message("RimExodus_MapRetrofitPreviewRequired".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            var voidCells = BuildVoidCells(map, tile);
            var animals = new List<Pawn>();
            if (!CollectPawns(map, voidCells, animals, showBlocked: true)) return;

            // 动物已在确认框逐项列出；非动物 Pawn 一律在上方拒绝，绝不由裁剪流程挪动或清除。
            foreach (var animal in animals)
                if (!animal.Destroyed) animal.Destroy(DestroyMode.Vanish);

            ClearPlayerMapState(map, voidCells);
            var manager = map.GetComponent<SeamlessTileManager>();
            // 这可能是曾执行过卸载恢复、之后又重新启用 RimExodus 的地图。旧序列化快照必须
            // 作废，否则本次捕获的新数组会在保存时被旧 baseSnapshotData 遮住。
            if (map.Parent is not MapParent_SeamlessTile && manager != null)
                manager.baseSnapshotData = null;
            SeamlessTerrainFill.BackupSnapshotAndApplyVoid(map, tile);

            manager?.CompleteRetrofit(tile);
            SeamlessVoidRockLink.PlaceAfterSeamOverride(map, tile);
            map.GetComponent<SeamlessBorderLookup>()?.BuildBorderLookup(force: true);
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            RefreshExitBand(map);

            Messages.Message("RimExodus_MapRetrofitDone".Translate(),
                MessageTypeDefOf.PositiveEvent, false);
            Log.Message($"[RimExodus] Retrofitted map {map.uniqueID} at {tile}: " +
                        $"voidCells={voidCells.Count}, removedAnimals={animals.Count}.");
        }

        private static bool TryGetTarget(Map map, out Map target, out PlanetTile tile, bool showFailure)
        {
            target = null;
            tile = PlanetTile.Invalid;
            if (Current.ProgramState != ProgramState.Playing || map == null)
            {
                if (showFailure) Reject("RimExodus_MapRetrofitNoMap");
                return false;
            }

            tile = map.Tile;
            var provider = SeamlessMapProviders.For(tile);
            var supported = !map.Disposed && !map.IsPocketMap && map.Parent != null && tile.Valid
                            && map.Size.x == map.Size.z
                            && SeamlessMapProviders.Accepts(tile, map.Parent)
                            && (provider == null
                                || SeamlessMapProviders.Invoke(tile, p => p.EnableNewMaps, false))
                            && SeamlessPolygonGeometry.BuildPolygonVertices(tile, map.Size.x).Count >= 3;
            if (!supported)
            {
                if (showFailure) Reject("RimExodus_MapRetrofitUnsupported");
                return false;
            }
            if (HasVoid(map))
            {
                if (showFailure) Reject("RimExodus_MapRetrofitAlreadyCropped");
                return false;
            }

            target = map;
            return true;
        }

        private static bool HasVoid(Map map)
        {
            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null) return false;
            foreach (var terrain in map.terrainGrid.topGrid)
                if (terrain == voidDef) return true;
            return false;
        }

        private static bool HasTransferSpots(Map map)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");
            return def != null && map.listerThings.ThingsOfDef(def).Count > 0;
        }

        private static HashSet<IntVec3> BuildVoidCells(Map map, PlanetTile tile)
        {
            var cells = new HashSet<IntVec3>();
            var band = SeamlessPolygonGeometry.BuildSeamBand(tile, map.Size.x);
            foreach (var cell in map.AllCells)
                if (SeamlessPolygonGeometry.IsVoidCell(band, tile, map.Size.x, cell)) cells.Add(cell);
            return cells;
        }

        /// <summary>收集会被裁掉的动物；任何非动物 Pawn 都拒绝裁剪，要求玩家先搬入带内。</summary>
        private static bool CollectPawns(Map map, HashSet<IntVec3> voidCells, List<Pawn> animals, bool showBlocked)
        {
            animals.Clear();
            // AllPawns 包含运输舱、休眠舱等本图容器里的未生成 Pawn；只查 Spawned 会让裁剪
            // 容器时把里面的人静默带走。PositionHeld 给出其宿主建筑/容器所在格。
            var pawns = new List<Pawn>(map.mapPawns.AllPawns);
            foreach (var pawn in pawns)
            {
                if (pawn.MapHeld != map || !voidCells.Contains(pawn.PositionHeld)) continue;
                if (pawn.RaceProps?.Animal == true)
                {
                    animals.Add(pawn);
                    continue;
                }
                if (showBlocked)
                    Messages.Message("RimExodus_MapRetrofitPawnBlocked".Translate(pawn.LabelShortCap),
                        MessageTypeDefOf.RejectInput, false);
                return false;
            }
            return true;
        }

        /// <summary>清掉将落入 void 的区域、分区和格目标，避免裁剪后仍有工作语义指向虚空。</summary>
        private static void ClearPlayerMapState(Map map, HashSet<IntVec3> voidCells)
        {
            var zones = new List<Zone>(map.zoneManager.AllZones);
            foreach (var zone in zones)
            {
                var cells = new List<IntVec3>(zone.cells);
                foreach (var cell in cells)
                    if (voidCells.Contains(cell)) zone.RemoveCell(cell);
            }

            foreach (var area in map.areaManager.AllAreas)
            {
                var cells = new List<IntVec3>(area.ActiveCells);
                foreach (var cell in cells)
                    if (voidCells.Contains(cell)) area[cell] = false;
            }

            var designations = new List<Designation>(map.designationManager.AllDesignations);
            foreach (var designation in designations)
                if (voidCells.Contains(designation.target.Cell))
                    map.designationManager.RemoveDesignation(designation);
        }

        private static void RefreshExitBand(Map map)
        {
            SeamlessMapTopology.InvalidateExitCells(map);
            map.exitMapGrid?.Notify_LOSBlockerDespawned();
        }

        private static void Reject(string key)
            => Messages.Message(key.Translate(), MessageTypeDefOf.RejectInput, false);
    }
}
