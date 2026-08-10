using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 让原版 FloatMenu 生成链路在"选中的 Pawn 站在相邻地块上"或"点击目标落在相邻地块
    /// footprint 内"时也能正确生成菜单（携带正确的 Map + 局部坐标），不需要为跨地图
    /// 专门写一个绕过原版的移动选项。两端都在宿主地图时完全放行原版流程。
    ///
    /// FloatMenuMakerMap.GetOptions 全程硬编码 Find.CurrentMap，版本间 IL 差异较大，
    /// 难以用 Transpiler 精确定位，这里改为 Prefix 在跨地图场景下接管整个方法体
    /// （复制自 references/RimWorldDecompiled/RimWorld/FloatMenuMakerMap.cs 的同名方法）。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    public static class Patch_FloatMenuMakerMap_GetOptions
    {
        private static readonly MethodInfo GetProviderOptionsMethod =
            AccessTools.Method(typeof(FloatMenuMakerMap), "GetProviderOptions");

        // FloatMenuContext 的 cachedClickedThings/cachedClickedPawns 是 private 字段，构造时从
        // Find.CurrentMap 收集（GenUI.ThingsUnderMouse 硬编码 CurrentMap）。跨图场景下这两个集合
        // 装的是"A 上同坐标格的对象"，与玩家点的 B 无关，会让 52 个 Thing/Pawn provider 产出错误选项
        // 甚至触发 Reachability.CanReach 的跨图 Log.Error。用反射在跨图场景清空它们，从源头切断。
        private static readonly AccessTools.FieldRef<FloatMenuContext, List<Thing>> ClickedThingsField =
            AccessTools.FieldRefAccess<FloatMenuContext, List<Thing>>("cachedClickedThings");
        private static readonly AccessTools.FieldRef<FloatMenuContext, List<Pawn>> ClickedPawnsField =
            AccessTools.FieldRefAccess<FloatMenuContext, List<Pawn>>("cachedClickedPawns");

        public static bool Prefix(List<Pawn> selectedPawns, Vector3 clickPos, ref List<FloatMenuOption> __result, ref FloatMenuContext context)
        {
            var hostMap = Find.CurrentMap;
            context = null;
            __result = new List<FloatMenuOption>();

            var crossesToPocketMap = SeamlessMapUtility.TryResolveMapPosition(clickPos, hostMap, out var targetMap, out var targetLocalCell);
            var anyPawnElsewhere = selectedPawns.Any(p => p.Map != hostMap);
            if (!crossesToPocketMap && !anyPawnElsewhere)
            {
                // 两端都在宿主地图，原版流程完全不受影响。
                return true;
            }

            var effectiveMap = crossesToPocketMap ? targetMap : hostMap;
            var effectiveClickPos = crossesToPocketMap ? targetLocalCell.ToVector3Shifted() : clickPos;
            if (effectiveMap == null || !effectiveClickPos.InBounds(effectiveMap))
            {
                return false;
            }

            context = new FloatMenuContext(selectedPawns, effectiveClickPos, effectiveMap);
            if (!context.allSelectedPawns.Any())
            {
                return false;
            }
            if (!context.ClickedCell.IsValid || !context.ClickedCell.InBounds(effectiveMap))
            {
                return false;
            }

            // 跨图场景下清空 ClickedThings/ClickedPawns：它们在构造时从 Find.CurrentMap 收集，
            // 装的是"A 上同坐标格的对象"，与玩家真正点的地图无关。让所有 Thing/Pawn provider
            //（攻击/拾取/治疗/haul 等 52 个）的 GetOptionsFor 循环不执行，从源头切断错误选项
            // 和 Reachability.CanReach 的跨图 Log.Error。跨图右键任何位置统一收敛为"走到这里"。
            // （未来支持跨图射击/交互时，在此处按需注入专门的跨图选项。）
            ClickedThingsField(context)?.Clear();
            ClickedPawnsField(context)?.Clear();

            if (!context.IsMultiselect)
            {
                var acceptanceReport = ShouldGenerateFloatMenuForPawnCrossMap(context.FirstSelectedPawn, hostMap);
                if (!acceptanceReport.Accepted)
                {
                    if (!acceptanceReport.Reason.NullOrEmpty())
                    {
                        Messages.Message(acceptanceReport.Reason, context.FirstSelectedPawn, MessageTypeDefOf.RejectInput, historical: false);
                    }
                    return false;
                }
            }
            else
            {
                context.allSelectedPawns.RemoveAll(p => !ShouldGenerateFloatMenuForPawnCrossMap(p, hostMap).Accepted);
                if (!context.allSelectedPawns.Any())
                {
                    return false;
                }
            }

            if (!context.IsMultiselect)
            {
                FloatMenuMakerMap.makingFor = context.FirstSelectedPawn;
            }

            GetProviderOptionsMethod.Invoke(null, new object[] { context, __result });
            FloatMenuMakerMap.makingFor = null;

            // 记录本次点击真正对应的 Map + 局部坐标，供 Pawn_JobTracker.StartJob 拦截跨图 Job 时使用。
            // 逐个 Pawn 判断而不是只看点击方向：Pawn 已经站在相邻地块、点击宿主地图时同样需要桥接。
            var anyCrossMapPawn = false;
            foreach (var pawn in context.allSelectedPawns)
            {
                if (pawn.Map != context.map)
                {
                    SeamlessCrossMapOrders.RecordPendingTarget(pawn, context.map, context.ClickedCell);
                    // 登记到选中保持器：多 pawn 跨图是逐个的，首个 pawn 切图会 ClearSelection 清掉其余 pawn
                    // 的选中状态，这里提前登记让转移后能统一 re-Select。
                    SeamlessSelectionTracker.Register(pawn);
                    anyCrossMapPawn = true;
                }
            }

            // 跨图场景：原版 Goto provider 用 pawn.Map 对目标 cell 做 CanReach，必然失败（目标在另一张地图），
            // 生成的"Go here"选项被禁用（action=null）。这里检测并注入我们自己的跨图 Goto 选项，
            // 其 action 直接下发 Goto Job（targetA = 目标 cell），由 Patches_Job 的桥接逻辑处理跨图传送。
            if (anyCrossMapPawn)
            {
                InjectCrossMapGotoOption(context, __result);
            }

            return false;
        }

        /// <summary>
        /// 跨图场景下统一收敛为唯一选项（"走到这里"或灰色"无法到达"）。
        ///
        /// 上游 Prefix 已清空 ClickedThings/ClickedPawns（从源头切断了 52 个 Thing/Pawn provider 的产出），
        /// 但 cell 级 provider（DraftedMove 的 GoHere、WorkGivers 的 cell 分支、ExtinguishFires 等）仍会用
        /// <c>pawn.CanReach(B cell)</c> 产出可达/禁用选项。这些产出基于错误的可达性检查（用 pawn.Map=A 的
        /// reachability 查 B 坐标），不能采用。本方法清空全部原版产出，按桥接可达性注入唯一选项。
        ///
        /// 桥接可达性 = pawn 能否到达本图的桥接传送点（<see cref="SeamlessCrossMapOrders.CanBridgeTo"/>），
        /// 不感知对端坐标在本图是 void 还是山壁——只要本图有可达桥接 spot 就放行。
        /// </summary>
        private static void InjectCrossMapGotoOption(FloatMenuContext context, List<FloatMenuOption> result)
        {
            var pawn = context.FirstSelectedPawn;
            if (pawn == null || pawn.Downed) return;
            if (!context.ClickedCell.IsValid) return;

            // 清空全部原版产出（cell 级 provider 的错误可达性判定结果）。
            result.Clear();

            // 桥接可达性：pawn 能否从本图到达某个对端指向 context.map 的桥接传送点。
            var canBridge = SeamlessCrossMapOrders.CanBridgeTo(pawn, context.map);

            if (canBridge)
            {
                // 桥接可达：注入 autoTakeable 的跨图 GoHere，对齐原版可达 GoHere 语义，
                // 使 GetAutoTakeOption 直接执行（不弹菜单，自动寻路到桥接点）。
                // action 下发 Goto Job（targetA = 目标 cell），Patches_Job.TryInterceptJob 桥接到传送点。
                var gotoOption = new FloatMenuOption("GoHere".Translate(), () =>
                {
                    var job = JobMaker.MakeJob(JobDefOf.Goto, context.ClickedCell);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.GoHere);
                gotoOption.isGoto = true;
                gotoOption.autoTakeable = true;
                gotoOption.autoTakeablePriority = 10f;
                result.Add(gotoOption);
            }
            else
            {
                // 桥接不可达（pawn 被围死，无法到任何桥接点）：注入禁用"无法到达"，对齐原版不可达表现。
                result.Add(new FloatMenuOption("CannotGoNoPath".Translate(), null));
            }
        }

        /// <summary>
        /// FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn 的跨地图版本：
        /// 放宽"pawn.Map != hostMap"的判断，允许已经站在相邻地块上的 Pawn 正常弹出菜单。
        /// </summary>
        private static AcceptanceReport ShouldGenerateFloatMenuForPawnCrossMap(Pawn pawn, Map hostMap)
        {
            if (pawn.Map != hostMap && !SeamlessTileRegistry.AreSeamlessNeighbors(pawn.Map, hostMap))
            {
                return false;
            }
            if (pawn.Downed)
            {
                return "IsIncapped".Translate(pawn.LabelCap, pawn);
            }
            if (ModsConfig.BiotechActive && pawn.Deathresting)
            {
                return "IsDeathresting".Translate(pawn.Named("PAWN"));
            }
            var lord = pawn.GetLord();
            if (lord != null)
            {
                var result = lord.AllowsFloatMenu(pawn);
                if (!result.Accepted)
                {
                    return result;
                }
            }
            return true;
        }
    }
}
