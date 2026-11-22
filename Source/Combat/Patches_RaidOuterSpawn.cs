using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Opens the arrival plan around native raid workers and captures pawns for workers that
    /// generate their own groups. Dormant arrivals still use the same captured pawn list.
    /// </summary>
    static class Patches_RaidOuterSpawn
    {
        /// <summary>
        /// raid 族（敌对 + 友好援军）总入口。Prefix 时 parms.raidArrivalMode/faction 均未解析
        /// （TryGenerateRaidInfo 内才定）——计划不依赖它们：空投族不调 4 参边缘原语 = ambient
        /// 自然不被消费；faction 敌对性与 walkIn 在 MakeLords Postfix 处复验。
        /// </summary>
        [HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
        static class Patch_IncidentWorker_Raid_TryExecuteWorker_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms, bool __result)
            {
                // Run once at the existing incident boundary, including strategies overriding MakeLords.
                SeamlessRaidOuterSpawn.KeepDormantRaidLocal(parms, SeamlessRaidOuterSpawn.WindowPawns);
                if (__result) SeamlessRaidOuterSpawn.LogUnusedPlan(parms);
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>发狂动物：跨图生成 + 无 lord 迁移（行军走近战桥接推进）。</summary>
        [HarmonyPatch(typeof(IncidentWorker_AggressiveAnimals), "TryExecuteWorker")]
        static class Patch_IncidentWorker_AggressiveAnimals_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: SeamlessCombatCoords.Enabled, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms);
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>食尸鬼保留原版袭击队伍及其撤退规则。</summary>
        [HarmonyPatch(typeof(IncidentWorker_GhoulAttack), "TryExecuteWorker")]
        static class Patch_IncidentWorker_GhoulAttack_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms);
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>奇美拉沿用原版队伍、潜伏阶段与切换计时。</summary>
        [HarmonyPatch(typeof(IncidentWorker_ChimeraAssault), "TryExecuteWorker")]
        static class Patch_IncidentWorker_ChimeraAssault_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms);
                SeamlessRaidOuterSpawn.End();
            }
        }

        // Swarm travel owns a native path and exit on the attacked map.
        [HarmonyPatch(typeof(IncidentWorker_EntitySwarm), "TryExecuteWorker")]
        static class Patch_IncidentWorker_EntitySwarm_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: false, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.KeepDormantRaidLocal(parms, SeamlessRaidOuterSpawn.WindowPawns);
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>
        /// 跨图计划窗口内的 pawn 组捕获：三个不走 MakeLords 的 worker（发狂动物/食尸鬼/奇美拉）
        /// 的生成全部汇经本重载（PawnKindDef 便捷重载与 PawnGroupMaker 内部都最终调它）。
        /// 窗口外零成本（一次静态判空）。
        /// </summary>
        [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) })]
        static class Patch_PawnGenerator_GeneratePawn_RaidOuterSpawn
        {
            static void Postfix(Pawn __result)
            {
                SeamlessRaidOuterSpawn.NotifyWindowPawnGenerated(__result);
            }
        }

        /// <summary>
        /// raid 族的跨图执行点：MakeLords 是 TryExecuteWorker 里生成（Arrive）之后的独立步骤，
        /// 此处 pawns/parms 双全、lord 已建好——守卫全过才整体迁移（半途放弃 = 原版结果）。
        /// 非 raid 事件无此调用面，ambient 不在场时首行守卫即返回（一次静态判空）。
        /// </summary>
        [HarmonyPatch(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.MakeLords))]
        static class Patch_RaidStrategyWorker_MakeLords_RaidOuterSpawn
        {
            static void Postfix(IncidentParms parms, List<Pawn> pawns)
            {
                // 诊断：袭击跨图迁移整体墙钟（含迁移后 lord 建立触发的首批 think——spawn 卡顿主嫌疑）。
                var probeT0 = RimExodusPerfProbe.Now();
                try
                {
                    SeamlessRaidOuterSpawn.TryRelocateToHost(parms, pawns);
                }
                finally
                {
                    RimExodusPerfProbe.Record(RimExodusPerfProbe.RaidSpawnPostfix, probeT0, pawns?.Count ?? 0);
                }
            }
        }
    }
}
