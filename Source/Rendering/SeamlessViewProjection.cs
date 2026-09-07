using UnityEngine;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Single coordinate/view contract for content shown as part of the current seamless composite view.
    /// A source-local point is either already on the focused map or translated from one active direct
    /// neighbor. This deliberately does not depend on the cross-map combat setting: rendering and audible
    /// spatial feedback remain properties of the seamless view itself.
    /// </summary>
    public static class SeamlessViewProjection
    {
        public static bool TryProjectToCurrent(Map sourceMap, Vector3 sourceLocal, out Vector3 projected)
        {
            projected = sourceLocal;
            var viewMap = Find.CurrentMap;
            if (viewMap == null || sourceMap == null || sourceMap.Disposed || viewMap.Disposed
                || !WorldRendererUtility.DrawingMap)
            {
                return false;
            }

            if (sourceMap == viewMap)
            {
                return true;
            }

            var sourceTile = SeamlessTileRegistry.GetMapWorldTile(sourceMap);
            if (sourceTile < 0
                || !SeamlessTileGraph.TryGetNeighborLinkByWorldTile(viewMap, sourceTile, out var link)
                || link.map != sourceMap)
            {
                return false;
            }

            projected += new Vector3(link.offset.x, 0f, link.offset.z);
            return true;
        }

        public static bool TryProjectToCurrent(Map sourceMap, IntVec3 sourceLocal, out IntVec3 projected)
        {
            if (TryProjectToCurrent(sourceMap, sourceLocal.ToVector3Shifted(), out var projectedVector))
            {
                projected = projectedVector.ToIntVec3();
                return true;
            }

            projected = IntVec3.Invalid;
            return false;
        }

        public static bool TryProjectVisibleToCurrent(Map sourceMap, Vector3 sourceLocal, out Vector3 projected)
        {
            if (!TryProjectToCurrent(sourceMap, sourceLocal, out projected))
            {
                return false;
            }

            return Find.CameraDriver.CurrentViewRect.ExpandedBy(1).Contains(projected.ToIntVec3());
        }
    }
}
