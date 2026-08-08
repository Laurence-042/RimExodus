using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Verse.GenUI.ThingsUnderMouse 的 Map 感知复制版。
    /// 原版方法隐式操作 Find.CurrentMap，无法用于宿主边界外的口袋地图，
    /// 因此需要一份接受显式 Map 参数的等价实现，供选中反查与 FloatMenu 前端 patch 共用。
    /// </summary>
    public static class SeamlessGenUI
    {
        private static readonly List<Thing> tmpCollected = new List<Thing>();

        public static List<Thing> ThingsUnderMouse(Vector3 clickPos, float thingSelectRadiusFactor, TargetingParameters parms, Map map)
        {
            var result = new List<Thing>();
            if (map == null)
            {
                return result;
            }

            var clickCell = IntVec3.FromVector3(clickPos);
            tmpCollected.Clear();
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    CollectAt(clickCell + new IntVec3(dx, 0, dz), map, tmpCollected);
                }
            }

            var radiusSq = thingSelectRadiusFactor * thingSelectRadiusFactor;
            foreach (var thing in tmpCollected)
            {
                if (!parms.CanTarget(new TargetInfo(thing)))
                {
                    continue;
                }

                if ((thing.TrueCenter() - clickPos).MagnitudeHorizontalSquared() > radiusSq)
                {
                    continue;
                }

                result.Add(thing);
            }

            result.Sort((a, b) => (a.TrueCenter() - clickPos).MagnitudeHorizontalSquared()
                .CompareTo((b.TrueCenter() - clickPos).MagnitudeHorizontalSquared()));
            return result;
        }

        private static void CollectAt(IntVec3 cell, Map map, List<Thing> into)
        {
            if (!cell.InBounds(map))
            {
                return;
            }

            foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
            {
                if (!into.Contains(thing))
                {
                    into.Add(thing);
                }
            }
        }
    }
}
