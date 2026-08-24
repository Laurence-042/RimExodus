using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Verse.GenUI.ThingsUnderMouse 的 Map 感知镜像（2026-08 重构升级为**忠实复刻**，VMF
    /// GenUIOnVehicle 同构）：原版方法隐式操作 Find.CurrentMap 与 UI.MouseMapPosition()，
    /// 跨图重放（点击落在邻图渲染区）需要它在"真邻图 + 邻图框架的鼠标等价点"上收集。
    ///
    /// mousePos 参数 = 目标图框架下的鼠标等价点（重放时 = 改写后的点击坐标；本图调用时
    /// = UI.MouseMapPosition()）——原版两处 UI.MouseMapPosition()（相邻物品半径过滤与距离
    /// 排序）与两个私有比较器全部以其为基准，语义与原版严格一致。
    /// </summary>
    public static class SeamlessGenUI
    {
        private static readonly List<Thing> CellThings = new List<Thing>();
        private static readonly List<Thing> WideThings = new List<Thing>();

        /// <summary>本图调用便捷重载：鼠标等价点 = 真实鼠标位（与原版语义一致）。</summary>
        public static List<Thing> ThingsUnderMouse(Vector3 clickPos, float pawnWideClickRadius,
            TargetingParameters clickParams, Map map)
        {
            return ThingsUnderMouse(clickPos, pawnWideClickRadius, clickParams, map, UI.MouseMapPosition(), null);
        }

        /// <summary>
        /// FloatMenuContext 构造函数体内的 GenUI.ThingsUnderMouse 重定向目标（同签名垫片，
        /// 由 Patches_ClickReplay 的 transpiler 注入）：重放激活 → 在真邻图上收集；
        /// 否则原样转调原版（本图路径零行为变化）。
        /// </summary>
        public static List<Thing> ThingsUnderMouseReplay(Vector3 clickPos, float pawnWideClickRadius,
            TargetingParameters clickParams, ITargetingSource source)
        {
            if (SeamlessReplayContext.Active)
            {
                return new List<Thing>(ThingsUnderMouse(clickPos, pawnWideClickRadius, clickParams,
                    SeamlessReplayContext.Target, clickPos, source));
            }
            return GenUI.ThingsUnderMouse(clickPos, pawnWideClickRadius, clickParams, source);
        }

        /// <summary>GenUI.ThingsUnderMouse 的忠实复刻（逐语句对齐 Verse/GenUI.cs），仅替换 Map 与鼠标点来源。</summary>
        public static List<Thing> ThingsUnderMouse(Vector3 clickPos, float pawnWideClickRadius,
            TargetingParameters clickParams, Map map, Vector3 mousePos, ITargetingSource source)
        {
            var result = new List<Thing>();
            if (map == null)
            {
                return result;
            }

            var clickCell = IntVec3.FromVector3(clickPos);
            IReadOnlyList<Pawn> allPawnsSpawned = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < allPawnsSpawned.Count; i++)
            {
                Pawn pawn = allPawnsSpawned[i];
                if ((pawn.DrawPos - clickPos).MagnitudeHorizontal() < 0.4f && clickParams.CanTarget(pawn, source))
                {
                    result.Add(pawn);
                    result.AddRange(ContainingSelectionUtility.SelectableContainedThings(pawn));
                }
            }
            result.Sort((a, b) => CompareByDistanceToMouse(a, b, mousePos));

            CellThings.Clear();
            foreach (Thing item in map.thingGrid.ThingsAt(clickCell))
            {
                if (!result.Contains(item) && clickParams.CanTarget(item, source))
                {
                    CellThings.Add(item);
                    CellThings.AddRange(ContainingSelectionUtility.SelectableContainedThings(item));
                }
            }
            IntVec3[] adjacentCells = GenAdj.AdjacentCells;
            for (int j = 0; j < adjacentCells.Length; j++)
            {
                IntVec3 c = adjacentCells[j] + clickCell;
                if (!c.InBounds(map) || c.GetItemCount(map) <= 1)
                {
                    continue;
                }
                foreach (Thing item2 in map.thingGrid.ThingsAt(c))
                {
                    if (item2.def.category == ThingCategory.Item
                        && (item2.TrueCenter() - mousePos).MagnitudeHorizontalSquared() <= 0.25f
                        && !result.Contains(item2) && clickParams.CanTarget(item2, source))
                    {
                        CellThings.Add(item2);
                    }
                }
            }
            List<Thing> list2 = map.listerThings.ThingsInGroup(ThingRequestGroup.WithCustomRectForSelector);
            for (int k = 0; k < list2.Count; k++)
            {
                Thing thing = list2[k];
                if (thing.CustomRectForSelector.HasValue && thing.CustomRectForSelector.Value.Contains(clickCell)
                    && !result.Contains(thing) && clickParams.CanTarget(thing, source))
                {
                    CellThings.Add(thing);
                }
            }
            CellThings.Sort((a, b) => CompareByDrawAltitudeOrDistToItem(a, b, mousePos));
            result.AddRange(CellThings);
            CellThings.Clear();

            IReadOnlyList<Pawn> allPawnsSpawned2 = map.mapPawns.AllPawnsSpawned;
            for (int l = 0; l < allPawnsSpawned2.Count; l++)
            {
                Pawn pawn2 = allPawnsSpawned2[l];
                if ((pawn2.DrawPos - clickPos).MagnitudeHorizontal() < pawnWideClickRadius && clickParams.CanTarget(pawn2, source))
                {
                    WideThings.Add(pawn2);
                }
            }
            WideThings.Sort((a, b) => CompareByDistanceToMouse(a, b, mousePos));
            for (int m = 0; m < WideThings.Count; m++)
            {
                if (!result.Contains(WideThings[m]))
                {
                    result.Add(WideThings[m]);
                    result.AddRange(ContainingSelectionUtility.SelectableContainedThings(WideThings[m]));
                }
            }
            WideThings.Clear();

            result.RemoveAll(delegate (Thing thing2) { return !clickParams.CanTarget(thing2, source); });
            result.RemoveAll(delegate (Thing thing3) { return thing3 is Pawn pawn3 && pawn3.IsHiddenFromPlayer(); });
            return result;
        }

        /// <summary>GenUI.CompareThingsByDistanceToMousePointer 的复刻（鼠标点参数化）。</summary>
        private static int CompareByDistanceToMouse(Thing a, Thing b, Vector3 mousePos)
        {
            float num = (a.DrawPosHeld.Value - mousePos).MagnitudeHorizontalSquared();
            float num2 = (b.DrawPosHeld.Value - mousePos).MagnitudeHorizontalSquared();
            if (num < num2)
            {
                return -1;
            }
            if (num == num2)
            {
                return b.Spawned.CompareTo(a.Spawned);
            }
            return 1;
        }

        /// <summary>GenUI.CompareThingsByDrawAltitudeOrDistToItem 的复刻（物品距离用鼠标点参数化）。</summary>
        private static int CompareByDrawAltitudeOrDistToItem(Thing A, Thing B, Vector3 mousePos)
        {
            if (A.def.category == ThingCategory.Item && B.def.category == ThingCategory.Item)
            {
                return (A.TrueCenter() - mousePos).MagnitudeHorizontalSquared()
                    .CompareTo((B.TrueCenter() - mousePos).MagnitudeHorizontalSquared());
            }
            Thing spawnedParentOrMe = A.SpawnedParentOrMe;
            Thing spawnedParentOrMe2 = B.SpawnedParentOrMe;
            if (spawnedParentOrMe.def.Altitude != spawnedParentOrMe2.def.Altitude)
            {
                return spawnedParentOrMe2.def.Altitude.CompareTo(spawnedParentOrMe.def.Altitude);
            }
            return B.Spawned.CompareTo(A.Spawned);
        }
    }
}
