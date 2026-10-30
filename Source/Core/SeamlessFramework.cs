using RimExodus.Maps;
using Verse;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>Startup selection and consumer policy; shared services never depend on RE types.</summary>
    internal static class SeamlessFramework
    {
        internal const string Owner = "RimExodus.SeamlessWorld";
        internal static bool Enabled { get; private set; }
        internal static MapRuntime Runtime => MapRuntime.Current;
        internal static bool GenerationBusy => IncrementalMapGenerator.IsAnyGenerating
            || (Enabled && Runtime?.Generation.IsBusy == true);

        internal static void Initialize(bool enabled)
        {
            Enabled = enabled;
            if (enabled) MapCombatModule.Install();
            Log.Message("[RimExodus] Map backend: " + (enabled ? "REF" : "legacy") + " (startup setting).");
        }

        internal static void ConfigureGame()
        {
            SeamlessSpatialQueries.Configure();
            if (!Enabled) return;
            SeamlessDormancyManager.ResetForGame();
            SeamlessTickThrottle.ResetForGame();
            Runtime.SetStopPolicy(Owner, map => SeamlessDormancyManager.IsDormant(map)
                || IncrementalMapGenerator.IsGenerating(map));
            foreach (var map in Find.Maps) RegisterMap(map);
            Current.Game.GetComponent<SeamlessFrameworkState>().SynchronizeCombatSetting();
            Runtime.Combat.DiagnosticLogging = RimExodusMod.Settings?.logCombatExtended ?? false;
        }

        internal static void RegisterMap(Map map)
        {
            if (!Enabled || SeamlessTileRegistry.GetMapWorldTile(map) < 0) return;
            Runtime.Combat.Enable(map, Current.Game.GetComponent<SeamlessFrameworkState>().Combat);
        }

        internal static void Sleep(Map map)
        {
            Runtime.Combat.Invalidate(map);
            Runtime.TickRates.Set(map, Owner, 0);
        }

        internal static void Wake(Map map)
        {
            Runtime.TickRates.Release(map, Owner);
        }
    }

    internal sealed class SeamlessFrameworkState : GameComponent
    {
        internal readonly MapCombatPolicy Combat;
        internal void SynchronizeCombatSetting()
        {
            Combat.Enabled = RimExodusMod.Settings?.crossMapCombatEnabled ?? true;
        }
        public SeamlessFrameworkState(Game game)
        {
            Combat = new MapCombatPolicy(SeamlessFramework.Owner)
            {
                Available = map => (RimExodusMod.Settings?.crossMapCombatEnabled ?? true)
                    && SeamlessTileRegistry.GetMapWorldTile(map) >= 0 && !SeamlessDormancyManager.IsDormant(map)
                    && !IncrementalMapGenerator.IsGenerating(map),
                Offset = (host, target) => SeamlessViewProjection.TryProject(target, IntVec3.Zero, host, out var offset)
                    ? (IntVec3?)offset : null,
                CellOwner = (host, cell) => SeamlessTileRegistry.TryGetOwnerNeighbor(host, cell, out var owner, out var local)
                    ? (MapCombatCell?)new MapCombatCell(owner, local) : null,
                HasNeighbors = SeamlessCombatCoords.HasActiveSeamNeighborsLegacy,
                CanApproach = SeamlessCrossMapOrders.CanBridgeTo,
                SuppressTargetSearch = pawn => pawn?.GetLord()?.LordJob is LordJob_SeamlessRaidMarch march && march.Marching,
                Neighbors = (map, result) =>
                {
                    var neighbors = new System.Collections.Generic.List<SeamlessTileGraph.NeighborInfo>();
                    SeamlessTileGraph.PopulateNeighbors(map, neighbors);
                    foreach (var n in neighbors) result.Add(new MapCombatNeighbor { map = n.map, offset = n.offset });
                },
                IgnoreProjectile = SeamlessVGECompat.IsWorldArtilleryLeavingMap,
                DiagnosticLogging = () => RimExodusMod.Settings?.logCombatExtended ?? false
            };
        }
    }
}
