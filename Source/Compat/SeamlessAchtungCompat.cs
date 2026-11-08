using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimExodus.Maps;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Achtung 自定义站位在 Controller.MouseDown 中把 UI 点击格直接配给 CurrentMap 的
    /// Standable / Walkable / Fogged 网格。复合视图点击邻图且参考系格在宿主图恰为 void 时，
    /// 会把合法邻图地面误判为不可站立；普通 Pawn 因无额外选项可从 EveryoneHasGoto 兜底，
    /// Alien 的 Climb To / Hibernate 则会让 Achtung 提前弹菜单，队形移动根本没有开始。
    ///
    /// MouseDown 内同组的三个格查询统一先经 REF MapCellSpace.TryResolve，把参考系格解析为真实
    /// MapCellAddress，再读取对应 Map 网格。归属判定来自公共多边形 provider：属于当前图就留在
    /// 当前图，当前图 void 且邻图拥有则落到邻图；不依赖 Pawn 命令侧表，也不按 Mod/种族特判。
    /// 未解析时 fail-open 保留 Achtung 原查询。后续坐标归一继续由公共 RCellFinder 补丁完成。
    /// </summary>
    internal static class SeamlessAchtungCompat
    {
        internal static void Register(Harmony harmony)
        {
            var controllerType = AccessTools.TypeByName("AchtungMod.Controller");
            if (controllerType == null) return;

            try
            {
                var mouseDown = AccessTools.Method(controllerType, "MouseDown",
                    new[] { typeof(Vector3), typeof(int), typeof(bool) })
                    ?? throw new MissingMethodException(controllerType.FullName, "MouseDown(Vector3,int,bool)");
                var transpiler = AccessTools.Method(typeof(SeamlessAchtungCompat), nameof(MouseDownTranspiler))
                    ?? throw new MissingMethodException(typeof(SeamlessAchtungCompat).FullName, nameof(MouseDownTranspiler));
                harmony.Patch(mouseDown, transpiler: new HarmonyMethod(transpiler));

                var info = Harmony.GetPatchInfo(mouseDown);
                if (info == null || !info.Transpilers.Any(p => p.owner == harmony.Id && p.PatchMethod == transpiler))
                    throw new InvalidOperationException("Harmony did not register the requested transpiler.");
                Log.Message("[RimExodus] Achtung compat: bound Controller.MouseDown REF cell-space queries.");
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus] Achtung REF cell-space compatibility unavailable; "
                    + "local Achtung behavior retained: " + error);
            }
        }

        internal static IEnumerable<CodeInstruction> MouseDownTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var standable = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Standable),
                new[] { typeof(IntVec3), typeof(Map) });
            var walkable = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.Walkable),
                new[] { typeof(IntVec3), typeof(Map) });
            var fogged = AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged),
                new[] { typeof(IntVec3), typeof(Map) });
            var standableReplacement = AccessTools.Method(typeof(SeamlessAchtungCompat), nameof(StandableInCellSpace));
            var walkableReplacement = AccessTools.Method(typeof(SeamlessAchtungCompat), nameof(WalkableInCellSpace));
            var foggedReplacement = AccessTools.Method(typeof(SeamlessAchtungCompat), nameof(FoggedInCellSpace));
            var standableCount = 0;
            var walkableCount = 0;
            var foggedCount = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(standable))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = standableReplacement;
                    standableCount++;
                }
                else if (instruction.Calls(walkable))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = walkableReplacement;
                    walkableCount++;
                }
                else if (instruction.Calls(fogged))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = foggedReplacement;
                    foggedCount++;
                }
                yield return instruction;
            }

            if (standableCount != 1 || walkableCount != 1 || foggedCount != 1)
                throw new InvalidOperationException("Expected one Achtung MouseDown anchor for each cell query; found "
                    + $"Standable={standableCount}, Walkable={walkableCount}, Fogged={foggedCount}.");
        }

        internal static bool StandableInCellSpace(IntVec3 referenceCell, Map referenceMap)
        {
            Resolve(referenceMap, referenceCell, out var address);
            return address.IsValid
                ? address.Cell.Standable(address.Map)
                : referenceMap != null && referenceCell.Standable(referenceMap);
        }

        internal static bool WalkableInCellSpace(IntVec3 referenceCell, Map referenceMap)
        {
            Resolve(referenceMap, referenceCell, out var address);
            return address.IsValid
                ? address.Cell.Walkable(address.Map)
                : referenceMap != null && referenceCell.Walkable(referenceMap);
        }

        internal static bool FoggedInCellSpace(IntVec3 referenceCell, Map referenceMap)
        {
            Resolve(referenceMap, referenceCell, out var address);
            return address.IsValid
                ? address.Cell.Fogged(address.Map)
                : referenceMap != null && referenceCell.Fogged(referenceMap);
        }

        private static void Resolve(Map referenceMap, IntVec3 referenceCell, out MapCellAddress address)
        {
            address = default(MapCellAddress);
            if (referenceMap == null || referenceMap.Disposed
                || !SeamlessSpatialQueries.TryCapture(referenceMap, out var space)
                || !space.TryResolve(referenceCell, out address)) return;

            if (address.Map != referenceMap && RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Achtung cell-space resolve: {referenceCell} in map "
                    + $"{referenceMap.uniqueID} frame -> {address.Cell} on map {address.Map.uniqueID}.");
        }
    }
}
