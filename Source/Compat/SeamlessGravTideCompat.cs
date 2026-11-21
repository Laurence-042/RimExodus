using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimExodus.Maps;
using Verse;

namespace RimExodus
{
    internal sealed class SeamlessGravTideCompat : SeamlessMapProvider
    {
        private static readonly Func<MapDrawCommand, bool?> FloodTintSelector = _ => drawingFloodTint ? true : (bool?)null;
        [ThreadStatic] private static bool drawingFloodTint;
        private static bool floodTintBound;

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

        internal static void Register(Harmony harmony)
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
                    BindFloodTintClipping(harmony);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GravTide compatibility unavailable: {ex}");
            }
        }

        internal static Func<MapDrawCommand, bool?> GetOverlayClipSelector(PlanetTile tile)
            => floodTintBound && SeamlessMapProviders.For(tile) is SeamlessGravTideCompat ? FloodTintSelector : null;

        private static void BindFloodTintClipping(Harmony harmony)
        {
            try
            {
                var layer = AccessTools.TypeByName("GravTide.SectionLayer_Flooded");
                var draw = layer == null ? null : AccessTools.DeclaredMethod(layer, nameof(MapDrawLayer.DrawLayer), Type.EmptyTypes);
                if (draw == null || draw.IsStatic || draw.ReturnType != typeof(void) || !typeof(MapDrawLayer).IsAssignableFrom(layer))
                    throw new MissingMethodException("GravTide.SectionLayer_Flooded", "DrawLayer()");
                harmony.Patch(draw,
                    prefix: new HarmonyMethod(typeof(SeamlessGravTideCompat), nameof(BeginFloodTintDraw)),
                    finalizer: new HarmonyMethod(typeof(SeamlessGravTideCompat), nameof(EndFloodTintDraw)));
                floodTintBound = true;
                Log.Message("[RimExodus] GravTide compatibility bound: SectionLayer_Flooded.DrawLayer overlay clipping.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] GravTide flood tint clipping unavailable: {ex}");
            }
        }

        // This material is shared with other layers. Mark only submissions made inside the flood draw.
        private static void BeginFloodTintDraw(Map ___map, out bool __state)
        {
            __state = drawingFloodTint;
            drawingFloodTint = floodTintBound && MapRenderPlan.CurrentNativeView is MapRenderPlan view
                && view.Source == ___map;
        }

        private static void EndFloodTintDraw(bool __state) => drawingFloodTint = __state;

        public override bool Accepts(PlanetTile tile, MapParent parent) => available(tile);

        public override bool CanGenerate(PlanetTile tile, MapParent parent) => refusal(tile) == null;

        public override WorldObjectDef GetMapParentDef(PlanetTile tile) => seabedSite;
    }
}
