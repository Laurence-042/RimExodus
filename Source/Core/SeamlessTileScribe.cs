using System.Collections.Generic;
using System.Globalization;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    internal static class SeamlessTileScribe
    {
        internal static PlanetTile Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return PlanetTile.Invalid;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return new PlanetTile(index, -1);
            return PlanetTile.FromString(value);
        }

        internal static bool HasWorldLayer(PlanetTile tile)
        {
            if (!tile.Valid || Find.World?.grid == null) return false;
            try { return tile.Layer != null; }
            catch (KeyNotFoundException) { return false; }
        }

        internal static void Look(ref PlanetTile tile, string label)
        {
            string value = Scribe.mode == LoadSaveMode.Saving && tile.Valid ? tile.ToString() : null;
            Scribe_Values.Look(ref value, label);
            if (Scribe.mode == LoadSaveMode.LoadingVars) tile = Parse(value);
        }

        internal static void Look(ref List<PlanetTile> tiles, string label)
        {
            var values = Scribe.mode == LoadSaveMode.Saving ? tiles.ConvertAll(tile => tile.ToString()) : null;
            Scribe_Collections.Look(ref values, label, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) tiles = values?.ConvertAll(Parse) ?? new List<PlanetTile>();
        }
    }
}
