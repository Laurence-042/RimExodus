using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Opens the arrival plan around native raid workers, redirects only their spatial generation
    /// calls to the final host, and captures pawns for independent incident workers and dormancy.
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
                SeamlessRaidDirectSpawn.BeginExecutionTrace(parms, __instance);
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms, bool __result)
            {
                // Run once at the existing incident boundary, including strategies overriding MakeLords.
                SeamlessRaidOuterSpawn.KeepDormantRaidLocal(parms, SeamlessRaidOuterSpawn.WindowPawns);
                if (__result) SeamlessRaidOuterSpawn.LogUnusedPlan(parms);
                SeamlessRaidOuterSpawn.End();
                SeamlessRaidDirectSpawn.EndExecutionTrace(parms);
            }

            static Exception Finalizer(IncidentParms parms, Exception __exception)
            {
                if (__exception != null)
                {
                    SeamlessRaidDirectSpawn.LogIncidentFailure(parms, __exception);
                    SeamlessRaidOuterSpawn.End();
                    SeamlessRaidDirectSpawn.EndExecutionTrace(parms);
                }
                return __exception;
            }

        }

        /// <summary>
        /// Supported strategies that inherit the vanilla implementation create their Lord at this
        /// narrow boundary. Temporarily expose the generation host to that method, then immediately
        /// restore the attacked map. Strategies overriding MakeLords are not intercepted here.
        /// </summary>
        [HarmonyPatch(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.MakeLords))]
        static class Patch_RaidStrategyWorker_MakeLords_DirectSpawn
        {
            static void Prefix(IncidentParms parms, out SeamlessRaidDirectSpawn.LordCreationState __state)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "MakeLords.base.begin");
                __state = SeamlessRaidDirectSpawn.BeginLordCreation(parms);
            }

            static void Postfix(IncidentParms parms, List<Pawn> pawns,
                SeamlessRaidDirectSpawn.LordCreationState __state)
            {
                SeamlessRaidDirectSpawn.FinishLordCreation(parms, pawns, __state, succeeded: true);
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "MakeLords.base.end");
            }

            static Exception Finalizer(IncidentParms parms, List<Pawn> pawns,
                SeamlessRaidDirectSpawn.LordCreationState __state, Exception __exception)
            {
                if (__exception != null)
                {
                    SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "MakeLords.base.exception");
                    SeamlessRaidDirectSpawn.FinishLordCreation(parms, pawns, __state, succeeded: false);
                }
                return __exception;
            }
        }

        /// <summary>
        /// Keeps the attacked map in IncidentParms while redirecting only the spatial native calls.
        /// The stack shape is unchanged: the virtual worker receiver becomes the first static argument.
        /// </summary>
        [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
        static class Patch_IncidentWorker_Raid_TryGenerateRaidInfo_DirectSpawn
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "TryGenerateRaidInfo.begin");
            }

            static void Postfix(IncidentParms parms, List<Pawn> pawns, bool __result)
            {
                SeamlessRaidDirectSpawn.CaptureGeneratedPawns(parms, pawns, __result);
            }

            static Exception Finalizer(IncidentParms parms, Exception __exception)
            {
                if (__exception != null)
                    SeamlessRaidDirectSpawn.MarkExecutionException(parms, "TryGenerateRaidInfo");
                return __exception;
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var resolve = AccessTools.Method(typeof(PawnsArrivalModeWorker),
                    nameof(PawnsArrivalModeWorker.TryResolveRaidSpawnCenter));
                var resolveDirect = AccessTools.Method(typeof(SeamlessRaidDirectSpawn),
                    nameof(SeamlessRaidDirectSpawn.TryResolveSpawnCenter));
                var spawn = AccessTools.Method(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.SpawnThreats));
                var spawnDirect = AccessTools.Method(typeof(SeamlessRaidDirectSpawn),
                    nameof(SeamlessRaidDirectSpawn.SpawnThreats));
                var arrive = AccessTools.Method(typeof(PawnsArrivalModeWorker), nameof(PawnsArrivalModeWorker.Arrive));
                var arriveDirect = AccessTools.Method(typeof(SeamlessRaidDirectSpawn),
                    nameof(SeamlessRaidDirectSpawn.Arrive));

                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(resolve))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = resolveDirect;
                    }
                    else if (instruction.Calls(spawn))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = spawnDirect;
                    }
                    else if (instruction.Calls(arrive))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = arriveDirect;
                    }
                    yield return instruction;
                }
            }
        }

        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterLabel")]
        static class Patch_IncidentWorker_RaidEnemy_GetLetterLabel_Trace
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterLabel.begin");
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterLabel.end");
            }

            static Exception Finalizer(IncidentParms parms, Exception __exception)
            {
                if (__exception != null)
                    SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterLabel.exception");
                return __exception;
            }
        }

        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterText")]
        static class Patch_IncidentWorker_RaidEnemy_GetLetterText_Trace
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterText.begin");
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterText.end");
            }

            static Exception Finalizer(IncidentParms parms, Exception __exception)
            {
                if (__exception != null)
                    SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "GetLetterText.exception");
                return __exception;
            }
        }

        [HarmonyPatch(typeof(PawnRelationUtility), nameof(PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter))]
        static class Patch_PawnRelationUtility_NotifyPawnsSeenByPlayerLetter_Trace
        {
            static void Prefix()
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(null, "PawnRelations.begin");
            }

            static void Postfix()
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(null, "PawnRelations.end");
            }

            static Exception Finalizer(Exception __exception)
            {
                if (__exception != null)
                    SeamlessRaidDirectSpawn.MarkExecutionPhase(null, "PawnRelations.exception");
                return __exception;
            }
        }

        [HarmonyPatch(typeof(IncidentWorker), "SendStandardLetter", new Type[]
        {
            typeof(TaggedString), typeof(TaggedString), typeof(LetterDef), typeof(IncidentParms),
            typeof(LookTargets), typeof(NamedArgument[])
        })]
        static class Patch_IncidentWorker_SendStandardLetter_Trace
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "SendStandardLetter.begin");
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "SendStandardLetter.end");
            }

            static Exception Finalizer(IncidentParms parms, Exception __exception)
            {
                if (__exception != null)
                    SeamlessRaidDirectSpawn.MarkExecutionPhase(parms, "SendStandardLetter.exception");
                return __exception;
            }
        }

        /// <summary>发狂动物：跨图生成 + 无 lord 迁移（行军走近战桥接推进）。</summary>
        [HarmonyPatch(typeof(IncidentWorker_AggressiveAnimals), "TryExecuteWorker")]
        static class Patch_IncidentWorker_AggressiveAnimals_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms,
                out SeamlessRaidDirectSpawn.WindowState __state)
            {
                if (parms.pawnKind == null && parms.target is Map attackedMap
                    && AggressiveAnimalIncidentUtility.TryFindAggressiveAnimalKind(parms.points, attackedMap,
                        out var animalKind))
                    parms.pawnKind = animalKind;
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: SeamlessCombatCoords.Enabled, __instance.def?.defName,
                    __instance.GetType());
                __state = SeamlessRaidDirectSpawn.BeginWindowIncident(parms, CellFinder.EdgeRoadChance_Animal);
            }

            static void Postfix(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state)
            {
                SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                SeamlessRaidOuterSpawn.End();
            }

            static Exception Finalizer(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state,
                Exception __exception)
            {
                if (__exception != null)
                {
                    SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                    SeamlessRaidOuterSpawn.End();
                }
                return __exception;
            }
        }

        /// <summary>食尸鬼保留原版袭击队伍及其撤退规则。</summary>
        [HarmonyPatch(typeof(IncidentWorker_GhoulAttack), "TryExecuteWorker")]
        static class Patch_IncidentWorker_GhoulAttack_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms,
                out SeamlessRaidDirectSpawn.WindowState __state)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
                __state = SeamlessRaidDirectSpawn.BeginWindowIncident(parms, CellFinder.EdgeRoadChance_Hostile);
            }

            static void Postfix(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state)
            {
                SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                SeamlessRaidOuterSpawn.End();
            }

            static Exception Finalizer(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state,
                Exception __exception)
            {
                if (__exception != null)
                {
                    SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                    SeamlessRaidOuterSpawn.End();
                }
                return __exception;
            }
        }

        /// <summary>奇美拉沿用原版队伍、潜伏阶段与切换计时。</summary>
        [HarmonyPatch(typeof(IncidentWorker_ChimeraAssault), "TryExecuteWorker")]
        static class Patch_IncidentWorker_ChimeraAssault_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms,
                out SeamlessRaidDirectSpawn.WindowState __state)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
                __state = SeamlessRaidDirectSpawn.BeginWindowIncident(parms, CellFinder.EdgeRoadChance_Hostile);
            }

            static void Postfix(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state)
            {
                SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                SeamlessRaidOuterSpawn.End();
            }

            static Exception Finalizer(IncidentParms parms, SeamlessRaidDirectSpawn.WindowState __state,
                Exception __exception)
            {
                if (__exception != null)
                {
                    SeamlessRaidDirectSpawn.FinishWindowIncident(parms, __state);
                    SeamlessRaidOuterSpawn.End();
                }
                return __exception;
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

            static Exception Finalizer(Exception __exception)
            {
                if (__exception != null) SeamlessRaidOuterSpawn.End();
                return __exception;
            }
        }

        /// <summary>Standalone Royalty mech-cluster incident uses the same far-side plan.</summary>
        [HarmonyPatch(typeof(IncidentWorker_MechCluster), "TryExecuteWorker")]
        static class Patch_IncidentWorker_MechCluster_DirectSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true,
                    __instance.def?.defName, __instance.GetType());
            }

            static void Postfix(IncidentParms parms, bool __result)
            {
                if (__result) SeamlessRaidOuterSpawn.LogUnusedPlan(parms);
                SeamlessRaidOuterSpawn.End();
            }

            static Exception Finalizer(Exception __exception)
            {
                if (__exception != null) SeamlessRaidOuterSpawn.End();
                return __exception;
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var find = AccessTools.Method(typeof(MechClusterUtility), nameof(MechClusterUtility.FindClusterPosition),
                    new[] { typeof(Map), typeof(MechClusterSketch), typeof(int), typeof(float) });
                var findDirect = AccessTools.Method(typeof(SeamlessRaidDirectSpawn),
                    nameof(SeamlessRaidDirectSpawn.FindStandaloneClusterPosition));
                var spawn = AccessTools.Method(typeof(MechClusterUtility), nameof(MechClusterUtility.SpawnCluster),
                    new[] { typeof(IntVec3), typeof(Map), typeof(MechClusterSketch), typeof(bool), typeof(bool), typeof(string) });
                var spawnDirect = AccessTools.Method(typeof(SeamlessRaidDirectSpawn),
                    nameof(SeamlessRaidDirectSpawn.SpawnStandaloneCluster));
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(find))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = findDirect;
                    }
                    else if (instruction.Calls(spawn))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = spawnDirect;
                    }
                    yield return instruction;
                }
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

        [HarmonyPatch(typeof(PawnsArrivalModeWorkerUtility),
            nameof(PawnsArrivalModeWorkerUtility.FindNewMapEdgeGroupCenter))]
        static class Patch_PawnsArrivalModeWorkerUtility_FindNewMapEdgeGroupCenter_DirectSpawn
        {
            static bool Prefix(Map map, List<Pair<List<Pawn>, IntVec3>> groups, bool arriveInPods,
                ref IntVec3 __result)
            {
                if (!SeamlessRaidDirectSpawn.TryFindGroupCenter(map, groups, arriveInPods, out var center))
                    return true;
                __result = center;
                return false;
            }
        }
    }
}
