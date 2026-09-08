using UnityEngine;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Single coordinate contract between seamless maps. A source-local point is either already in the
    /// destination map or translated from one active direct neighbor. Callers must explicitly choose the
    /// destination coordinate system and apply any view-specific policy themselves.
    /// </summary>
    public static class SeamlessViewProjection
    {
        /// <summary>
        /// Resolves the translation from source-local coordinates into a destination map's coordinates.
        /// Only the same map or an active direct seamless neighbor is accepted.
        /// </summary>
        private static bool TryGetOffset(Map sourceMap, Map destinationMap, out IntVec3 offset)
        {
            offset = IntVec3.Zero;
            if (sourceMap == null || destinationMap == null || sourceMap.Disposed || destinationMap.Disposed)
            {
                return false;
            }

            if (sourceMap == destinationMap)
            {
                return true;
            }

            var sourceTile = SeamlessTileRegistry.GetMapWorldTile(sourceMap);
            if (sourceTile < 0
                || !SeamlessTileGraph.TryGetNeighborLinkByWorldTile(destinationMap, sourceTile, out var link)
                || link.map != sourceMap) return false;
            offset = link.offset;
            return true;
        }

        public static bool TryProject(Map sourceMap, Vector3 sourceLocal, Map destinationMap, out Vector3 projected)
        {
            projected = sourceLocal;
            if (!TryGetOffset(sourceMap, destinationMap, out var offset)) return false;
            projected += new Vector3(offset.x, 0f, offset.z);
            return true;
        }

        public static bool TryProject(Map sourceMap, IntVec3 sourceLocal, Map destinationMap, out IntVec3 projected)
        {
            if (TryGetOffset(sourceMap, destinationMap, out var offset))
            {
                projected = sourceLocal + offset;
                return true;
            }

            projected = IntVec3.Invalid;
            return false;
        }
    }
}
