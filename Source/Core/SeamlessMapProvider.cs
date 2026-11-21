using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>Describes maps on a world layer before RimExodus creates or connects them.</summary>
    public abstract class SeamlessMapProvider
    {
        /// <summary>The parent is null for an empty world tile.</summary>
        public abstract bool Accepts(PlanetTile tile, MapParent parent);

        /// <summary>The default parent to create on an empty tile. Null prevents generation there.</summary>
        public abstract WorldObjectDef GetMapParentDef(PlanetTile tile);

        public virtual bool EnableNewMaps => true;

        public virtual bool CanGenerate(PlanetTile tile, MapParent parent) => true;

        /// <summary>Return a convex polygon in local X/Z coordinates, or null for the native polygon.</summary>
        public virtual IReadOnlyList<Vector2> GetFootprint(PlanetTile tile, int mapSize) => null;

        /// <summary>Describe the edge from this vertex to the next. An invalid destination closes the seam.</summary>
        public virtual SeamlessMapEdge GetEdge(PlanetTile tile, int edge, PlanetTile nativeNeighbor)
            => new SeamlessMapEdge(nativeNeighbor);
    }

    public readonly struct SeamlessMapEdge
    {
        private readonly PlanetTile destination;
        private readonly bool initialized;

        public SeamlessMapEdge(PlanetTile destination, IntVec3? offset = null, bool allowVanillaExit = true)
        {
            this.destination = destination;
            initialized = true;
            Offset = offset;
            AllowVanillaExit = allowVanillaExit;
        }

        public PlanetTile Destination => initialized ? destination : PlanetTile.Invalid;

        /// <summary>Optional translation from destination coordinates into source coordinates.</summary>
        public IntVec3? Offset { get; }

        public bool AllowVanillaExit { get; }
    }

    /// <summary>Register after Defs are loaded and before generating or loading a world.</summary>
    public static class SeamlessMapProviders
    {
        private static readonly Dictionary<PlanetLayerDef, SeamlessMapProvider> Providers = new();

        public static bool Register(PlanetLayerDef layer, SeamlessMapProvider provider)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (layer == PlanetLayerDefOf.Surface || Providers.ContainsKey(layer))
            {
                Log.Warning($"[RimExodus] A seamless map provider already owns layer {layer.defName}.");
                return false;
            }
            Providers.Add(layer, provider);
            return true;
        }

        internal static SeamlessMapProvider For(PlanetTile tile)
            => tile.Valid && Providers.TryGetValue(tile.LayerDef, out var provider) ? provider : null;

        internal static bool Accepts(PlanetTile tile, MapParent parent = null)
        {
            if (!tile.Valid || parent is PocketMapParent) return false;
            if (tile.LayerDef == PlanetLayerDefOf.Surface && tile.Layer.IsRootSurface) return true;
            return Invoke(tile, provider => provider.Accepts(tile, parent), false);
        }

        internal static T Invoke<T>(PlanetTile tile, Func<SeamlessMapProvider, T> query, T fallback)
        {
            var provider = For(tile);
            if (provider == null) return fallback;
            try { return query(provider); }
            catch (Exception ex)
            {
                Log.WarningOnce($"[RimExodus] Seamless provider for {tile.LayerDef.defName} failed: {ex}",
                    Gen.HashCombineInt(tile.LayerDef.shortHash, ex.GetType().GetHashCode()));
                return fallback;
            }
        }
    }
}
