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
        /// 跨图场景下注入可用的 Goto 选项。
        /// 原版 provider 对跨图 cell 做 CanReach 失败，生成禁用选项（action=null）。
        /// 这里移除被禁用的 Goto 相关选项，注入一个 action 直接下发跨图 Goto Job 的选项（由 Patches_Job 桥接）。
        /// </summary>
        private static void InjectCrossMapGotoOption(FloatMenuContext context, List<FloatMenuOption> result)
        {
            var pawn = context.FirstSelectedPawn;
            if (pawn == null || pawn.Downed) return;
            if (!context.ClickedCell.IsValid) return;

            var gotoLabel = "GoHere".Translate();

            // 移除被禁用的 Goto 相关选项（action 为 null 且 label 含 Goto 关键词或拒绝理由）。
            for (var i = result.Count - 1; i >= 0; i--)
            {
                var opt = result[i];
                if (opt.action == null && (opt.Label.Contains(gotoLabel) || opt.Label.Contains("CannotGo")))
                {
                    result.RemoveAt(i);
                }
            }

            // 若已存在可用的 Goto 选项（action != null 且 label 匹配），不重复注入。
            foreach (var opt in result)
            {
                if (opt.action != null && opt.Label.Contains(gotoLabel))
                {
                    return;
                }
            }

            // 注入跨图 Goto 选项。action 下发 Goto Job（targetA = 目标 cell），
            // Patches_Job.TryInterceptJob 检测到 pending 跨图目标后会桥接到传送点。
            var gotoOption = new FloatMenuOption(gotoLabel, () =>
            {
                var job = JobMaker.MakeJob(JobDefOf.Goto, context.ClickedCell);
                job.playerForced = true;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }, MenuOptionPriority.High);
            result.Add(gotoOption);
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
