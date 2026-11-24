using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    internal sealed class SeamlessGravTideCompat : SeamlessMapProvider
    {
        private readonly Func<PlanetTile, bool> available;
        private readonly Func<PlanetTile, string> refusal;
        private readonly WorldObjectDef seabedSite;
        public override bool EnableNewMaps { get; }

        private SeamlessGravTideCompat(Func<PlanetTile, bool> available, Func<PlanetTile, string> refusal,
            WorldObjectDef seabedSite, bool enableNewMaps)
        {
            this.available = available;
            this.refusal = refusal;
            this.seabedSite = seabedSite;
            EnableNewMaps = enableNewMaps;
        }

        internal static void Register()
        {
            if (!ModsConfig.IsActive("gravtide.mod")) return;
            try
            {
                var layer = DefDatabase<PlanetLayerDef>.GetNamedSilentFail("GravTide_SeabedLayer");
                var site = DefDatabase<WorldObjectDef>.GetNamedSilentFail("GravTide_SeabedSite");
                var type = AccessTools.TypeByName("GravTide.SeabedWorld");
                var method = type == null ? null : AccessTools.Method(type, "Available", new[] { typeof(PlanetTile) });
                var lease = AccessTools.TypeByName("GravTide.SeabedLease");
                var refused = lease == null ? null : AccessTools.Method(lease, "RefusalAt", new[] { typeof(PlanetTile) });
                if (layer == null || site == null || method == null || refused == null)
                    throw new InvalidOperationException("Seabed layer, world object or required generation checks were not found.");
                var available = (Func<PlanetTile, bool>)Delegate.CreateDelegate(typeof(Func<PlanetTile, bool>), method);
                var refusal = (Func<PlanetTile, string>)Delegate.CreateDelegate(typeof(Func<PlanetTile, string>), refused);
                if (SeamlessMapProviders.Register(layer, new SeamlessGravTideCompat(available, refusal, site,
                    RimExodusMod.Settings?.gravTideSeamlessEnabled == true)))
                {
                    Log.Message("[RimExodus] GravTide compatibility bound: seabed layer, availability and lease checks.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GravTide compatibility unavailable: {ex}");
            }
        }

        public override bool Accepts(PlanetTile tile, MapParent parent) => available(tile);

        public override bool CanGenerate(PlanetTile tile, MapParent parent) => refusal(tile) == null;

        public override WorldObjectDef GetMapParentDef(PlanetTile tile) => seabedSite;
    }
}
