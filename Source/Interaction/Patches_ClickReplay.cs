using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 跨图点击重放（2026-08 架构重构，VMF 同构，用户定夺勿回退为 GetOptions 整方法接管）：
    /// 点击落在本图渲染的邻图区域时，FloatMenuContext 构造期注入（ref 参数改写：点击坐标 →
    /// 邻图局部、map → 真邻图），52 个 provider 原生在真邻图上评估产出选项；命令由公共函数层
    /// （Patches_CrossMapCommon，Phase B）跨图化。本图点击完全原生，所有 patch 同图早退。
    ///
    /// patch 面（对齐 VMF Patches_FloatMenu.cs）：
    /// ① FloatMenuContext ctor Prefix/Finalizer——重放注入 + 命令目标登记（Transpiler 在
    ///    Patches_ClickReplay_GenUIRedirect 中把 ctor 体内 GenUI.ThingsUnderMouse 换镜像垫片）；
    /// ② GetOptions 两处 GenGrid.InBounds 调用点重定向（原始点击位/ClickedCell 的重放语义）；
    /// ③ ShouldGenerateFloatMenuForPawn 放宽"必须同图"（活跃邻图 pawn 允许）；
    /// ④ FloatMenuMap.StillValid 的 revalidateClickTarget.PositionHeld 取值点转宿主系。
    /// </summary>
    public static class Patches_ClickReplay
    {
    }

    /// <summary>
    /// 重放注入点：点击解析到活跃邻图 → 改写构造参数（坐标邻图局部 + map 真邻图）并开启重放槽。
    /// 槽生命周期 = 整个 GetOptions 调用（ctor Prefix 开 → GetOptions Finalizer 关），
    /// 供 ctor 体内的 GenUI 镜像垫片与 GetOptions 体内 InBounds 重定向消费。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuContext), MethodType.Constructor, new[] { typeof(List<Pawn>), typeof(Vector3), typeof(Map) })]
    public static class Patch_FloatMenuContext_Ctor_Replay
    {
        public static void Prefix(List<Pawn> selectedPawns, ref Vector3 clickPosition, ref Map map)
        {
            if (map == null || map.Disposed || Find.CurrentMap != map) return; // 只在 CurrentMap 语境下重放
            if (!SeamlessMapUtility.TryResolveMapPosition(clickPosition, map, out var targetMap, out var targetCell))
                return; // 本图命中：全原生
            if (!SeamlessCombatCoords.TryGetCombatLink(map, targetMap, out var link))
                return; // 非活跃邻居（休眠/未连接）：原生（本图坐标评估，选项自会诚实禁用）

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Click replay: {IntVec3.FromVector3(clickPosition)} on map {map.uniqueID} "
                    + $"-> {targetCell} on map {targetMap.uniqueID} (offset {link.offset}).");

            SeamlessReplayContext.Begin(map, targetMap, link.offset, targetCell);
            clickPosition = targetCell.ToVector3Shifted();
            map = targetMap;
        }

        /// <summary>
        /// 上下文构造完成：为每个"站在本图、命令目标在邻图"的选中 pawn 登记命令目标
        /// （VMF TargetInfo 侧表同构）——下游 PawnGotoAction/StartPath 包装/选位 patch 据此
        /// 识别邻图框架坐标，不依赖数值反推。同时登记选中保持器（首个 pawn 切图会
        /// ClearSelection，提前登记让转移后统一 re-Select）。
        /// </summary>
        public static void Finalizer(Exception __exception, List<Pawn> selectedPawns)
        {
            if (__exception != null) return;
            if (!SeamlessReplayContext.Active)
            {
                // 本图点击的菜单：清除残留命令登记（上次跨图菜单的遗留会误导后续
                // PawnGotoAction/StartPath 包装把本图命令误判为跨图）。
                foreach (var pawn in selectedPawns)
                {
                    SeamlessCommandTargets.Remove(pawn);
                }
                return;
            }
            foreach (var pawn in selectedPawns)
            {
                if (pawn?.Map == null || pawn.Map == SeamlessReplayContext.Target) continue;
                SeamlessCommandTargets.Set(pawn, SeamlessReplayContext.Target, SeamlessReplayContext.TargetCell);
                SeamlessSelectionTracker.Register(pawn);
            }
        }
    }

    /// <summary>
    /// FloatMenuContext ctor 体内的 GenUI.ThingsUnderMouse → SeamlessGenUI.ThingsUnderMouseReplay
    /// （同签名垫片：重放激活在真邻图收集，否则转调原版）。原版全库唯一的其他消费者不受影响。
    /// 锚点 = 对 GenUI.ThingsUnderMouse(4 参，source 有默认值) 的 call/callvirt（ctor 内 2 处：
    /// ForThing 收集 + ForPawns 收集），失配启动即红字。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuContext), MethodType.Constructor, new[] { typeof(List<Pawn>), typeof(Vector3), typeof(Map) })]
    public static class Patch_FloatMenuContext_Ctor_GenUIRedirect
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return RedirectThingsUnderMouse(instructions);
        }

        internal static IEnumerable<CodeInstruction> RedirectThingsUnderMouse(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(GenUI), nameof(GenUI.ThingsUnderMouse),
                new[] { typeof(Vector3), typeof(float), typeof(TargetingParameters), typeof(ITargetingSource) });
            var mirror = AccessTools.Method(typeof(SeamlessGenUI), nameof(SeamlessGenUI.ThingsUnderMouseReplay));
            var replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(original))
                {
                    // 原调用点可能是 callvirt（实例 getter）——替换目标为静态方法，opcode 必须归一为 call。
                    ci.opcode = OpCodes.Call;
                    ci.operand = mirror;
                    replaced++;
                }
                yield return ci;
            }
            if (replaced == 0)
                Log.Error("[RimExodus] FloatMenuContext ctor GenUI.ThingsUnderMouse anchor not found; "
                    + "replayed menus will collect things from the wrong map. Game version changed?");
        }
    }

    /// <summary>
    /// GetOptions 体内两处边界检查的重放语义 + 重放槽收口（Finalizer 必然执行，含异常路径）。
    /// L31 原始点击位：本图界内 **或** 可解析到邻图（重放入口）。
    /// L40 ClickedCell：重放激活时按目标图界内判（此时 cell 已是邻图局部坐标）。
    ///
    /// VMF 共存（2026-08，勿回退本优先级/链式回落）：VMF 对同一方法也有 transpiler
    /// （CodeMatcher.MatchStartForward(Calls(InBounds(Vector3))) → .Set——**锚点找不到时
    /// MatchStartForward 静默失败、位置无效，.Set 才抛 "Cannot set values at invalid
    /// position"** = VMF 整类 patch 应用失败、红字）。VF 硬依赖 VMF 必然在场，而我们的
    /// 替换式 transpiler 若先跑会把 InBounds 调用换掉、毁掉它的锚点（2026-08 实测启动
    /// 红字根因）。修复 = 本 transpiler 降为 Low 优先级（VMF 的 Normal 先在原始 IL 上
    /// 成功应用），我们在其输出上做**链式接管**：Vector3 调用点无论此时是原版 InBounds
    /// 还是 VMF 的 InBounds（签名同 (Vector3, Map)→bool），都换成 ReplayInBoundsClick——
    /// 它先走 VMF 判定（在场时，保留其车图语义：CurrentMap 是车图时以 TryGetVehicleMap
    /// 取代原生判定），无缝解析作为其 false 后的扩展，VMF 缺席 = 纯原生+无缝。IntVec3
    /// 调用点 VMF 只在后面 Insert 不替换（原调用仍在），我们的替换不影响其插入体。
    /// transpiler 每次重打包都从原始方法体重跑全链，无顺序不稳定。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    public static class Patch_FloatMenuMakerMap_GetOptions_Replay
    {
        /// <summary>VMF 的 GetOptions InBounds 替换方法（软反射，VMF 缺席 = null）。</summary>
        private static System.Reflection.MethodInfo _vmfInBoundsVec;

        public static void Finalizer()
        {
            SeamlessReplayContext.End();
        }

        [HarmonyPriority(Priority.Low)]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var inBoundsVec = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds),
                new[] { typeof(Vector3), typeof(Map) });
            var inBoundsCell = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds),
                new[] { typeof(IntVec3), typeof(Map) });
            var vmfPatchType = AccessTools.TypeByName("VehicleMapFramework.VMF_HarmonyPatches.Patch_FloatMenuMakerMap_GetOptions");
            _vmfInBoundsVec = vmfPatchType == null ? null
                : AccessTools.Method(vmfPatchType, "InBounds", new[] { typeof(Vector3), typeof(Map) });
            var vecReplay = AccessTools.Method(typeof(Patch_FloatMenuMakerMap_GetOptions_Replay), nameof(ReplayInBoundsClick));
            var cellReplay = AccessTools.Method(typeof(Patch_FloatMenuMakerMap_GetOptions_Replay), nameof(ReplayInBoundsCell));
            var replacedVec = 0;
            var replacedCell = 0;
            foreach (var ci in instructions)
            {
                // Vector3 调用点：VMF 先跑时已被换成它的 InBounds（签名相同）——两种形态都接管，
                // 由 ReplayInBoundsClick 内部链式回落保住 VMF 的车图语义（见类注释）。
                if (ci.Calls(inBoundsVec) || (_vmfInBoundsVec != null && ci.Calls(_vmfInBoundsVec)))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = vecReplay;
                    replacedVec++;
                }
                else if (ci.Calls(inBoundsCell))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = cellReplay;
                    replacedCell++;
                }
                yield return ci;
            }
            if (replacedVec == 0 || replacedCell == 0)
                Log.Error($"[RimExodus] GetOptions InBounds anchors not found (vec={replacedVec} cell={replacedCell}); "
                    + "cross-map clicks will yield empty menus. Game version changed?");
        }

        /// <summary>
        /// L31 语义：原始点击位在本图界内，或可解析到邻图（重放入口）。
        /// VMF 在场时先走其判定（保留车图语义），无缝解析作为其 false 后的扩展。
        /// </summary>
        public static bool ReplayInBoundsClick(Vector3 pos, Map map)
        {
            if (_vmfInBoundsVec != null)
            {
                try
                {
                    if ((bool)_vmfInBoundsVec.Invoke(null, new object[] { pos, map })) return true;
                }
                catch
                {
                    // VMF 反射调用异常（签名漂移等）：按 false 处理，落回无缝解析——菜单功能保守降级不炸。
                }
            }
            else if (pos.InBounds(map)) return true;
            return SeamlessMapUtility.TryResolveMapPosition(pos, map, out _, out _);
        }

        /// <summary>L40 语义：重放激活时 ClickedCell 按目标图界内判；否则原生。</summary>
        public static bool ReplayInBoundsCell(IntVec3 cell, Map map)
        {
            if (SeamlessReplayContext.Active) return cell.InBounds(SeamlessReplayContext.Target);
            return cell.InBounds(map);
        }
    }

    /// <summary>
    /// 放宽"pawn 必须在 CurrentMap 上才能生成菜单"：活跃邻图上的 pawn 也允许（混合选中
    /// 场景：本图与邻图 pawn 同时被选、点击任一侧）。其余判定逐条复刻原版（方法体小且稳定，
    /// 版本升级时比对反编译源）。非邻居真误用保持原版语义（false）。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn))]
    public static class Patch_FloatMenuMakerMap_ShouldGenerateCrossMap
    {
        public static bool Prefix(Pawn pawn, ref AcceptanceReport __result)
        {
            if (pawn?.Map == null || pawn.Map == Find.CurrentMap) return true; // 原生
            if (!SeamlessCombatCoords.TryGetCombatLink(Find.CurrentMap, pawn.Map, out _))
                return true; // 非活跃邻居：原生语义（false）

            if (pawn.Downed)
            {
                __result = "IsIncapped".Translate(pawn.LabelCap, pawn);
                return false;
            }
            if (ModsConfig.BiotechActive && pawn.Deathresting)
            {
                __result = "IsDeathresting".Translate(pawn.Named("PAWN"));
                return false;
            }
            var lord = pawn.GetLord();
            if (lord != null)
            {
                var result = lord.AllowsFloatMenu(pawn);
                if (!result.Accepted)
                {
                    __result = result;
                    return false;
                }
            }
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// FloatMenuMap.StillValid 的重验证取值点：revalidateClickTarget 在邻图上时 PositionHeld
    /// 是邻图本地坐标，直接喂回 GetOptions 会按本图同数字坐标误解析——转宿主系（+offset）
    /// 后重放链一致（VMF 同款）。锚点 = StillValid 体内唯一的 Thing.get_PositionHeld 调用。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMap), "StillValid")]
    public static class Patch_FloatMenuMap_StillValid_Replay
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.PositionHeld));
            var shim = AccessTools.Method(typeof(SeamlessReplayContext),
                nameof(SeamlessReplayContext.PositionHeldForRevalidate));
            var replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(original))
                {
                    // 原调用点可能是 callvirt（实例 getter）——替换目标为静态方法，opcode 必须归一为 call。
                    ci.opcode = OpCodes.Call;
                    ci.operand = shim;
                    replaced++;
                }
                yield return ci;
            }
            if (replaced != 1)
                Log.Error($"[RimExodus] FloatMenuMap.StillValid PositionHeld anchor count = {replaced} (expected 1); "
                    + "open-menu revalidation may mismatch for neighbor-map targets.");
        }
    }
}
