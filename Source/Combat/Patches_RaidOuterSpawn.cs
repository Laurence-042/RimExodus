using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimExodus
{
    /// <summary>
    /// 袭击外缘生成的事件入口 patch（机制与资格三分支见 <see cref="SeamlessRaidOuterSpawn"/> 类注释）：
    ///
    /// ① <see cref="IncidentWorker_Raid.TryExecuteWorker"/>（raid 族总入口）：Prefix 建计划
    ///    （同图约束 ambient + 跨图生成挂起），Postfix 清。**只 patch 基类方法**——
    ///    IncidentWorker_RaidEnemy 虽覆写 TryExecuteWorker 但首行调 base.TryExecuteWorker
    ///    （非虚直调，命中基类补丁）；RaidFriendly 不覆写、虚分发直达基类——两者全覆盖。
    ///    跨图执行点 = RaidStrategyWorker.MakeLords Postfix（pawn 已生成、lord 自洽时整体迁移）。
    ///    HateChanters（邪教徒）继承 IncidentWorker 自带独立 TryExecuteWorker，不经此链 =
    ///    天然排除（用户定夺 v1 不做：自带"边缘分批布点 + 吟唱"机制且跨图难度剧增）。
    /// ② <see cref="IncidentWorker_AggressiveAnimals"/>（发狂动物，1.6 名，旧 ManhunterPack）：
    ///    跨图生成 + 无 lord 迁移（ManhunterPermanent mental state 跨图保留，行军由
    ///    Patch_JobGiver_Manhunter_CrossMapMelee 的桥接推进接管——2026-09 近战跨图索敌落地）。
    /// ③ <see cref="IncidentWorker_GhoulAttack"/>（食尸鬼）：跨图生成 + 迁移后重建其原生
    ///    "无超时 AssaultColony" lord（镜像 worker 内的 MakeNewLord 参数）。
    /// ④ <see cref="IncidentWorker_ChimeraAssault"/>（奇美拉袭击）：跨图生成 + 迁移后重建
    ///    LordJob_ChimeraAssault（stalk/attack 模式机从零开始，刚生成的组无状态可失）。
    /// ②③④ 不走 RaidStrategyWorker.MakeLords（worker 内直接 GenSpawn + LordMaker）——迁移
    /// 入口在各自 TryExecuteWorker Postfix，pawn 组由 <see cref="SeamlessRaidOuterSpawn.WindowPawns"/>
    /// （PawnGenerator.GeneratePawn Postfix，仅跨图计划窗口内捕获）提供。
    ///
    /// MakeLords 被 SiegeMechanoid/PsychicRitualSiege 覆写（Harmony 基类补丁不命中覆写）——
    /// 这两类与邪教徒围攻不在跨图范围（人类 Siege/StageThenAttack/工兵破墙已于 2026-09
    /// 纳入，见 SeamlessRaidOuterSpawn.TryRelocateToHost），零损失。
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

            static void Postfix()
            {
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>发狂动物：跨图生成 + 无 lord 迁移（行军走近战桥接推进）。</summary>
        [HarmonyPatch(typeof(IncidentWorker_AggressiveAnimals), "TryExecuteWorker")]
        static class Patch_IncidentWorker_AggressiveAnimals_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms, null);
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>食尸鬼：跨图生成 + 重建无超时 AssaultColony（镜像其 worker 原生 lord）。</summary>
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
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms,
                    first => new LordJob_AssaultColony(Faction.OfEntities, canKidnap: false,
                        canTimeoutOrFlee: false, sappers: false, useAvoidGridSmart: false, canSteal: false));
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>奇美拉袭击：跨图生成 + 重建 ChimeraAssault lord（镜像其 worker 原生 lord）。
        /// 重建保持原版 stalk 起步——潜伏等待机制原样保留（MTB 0.7 天 / 被打切换，均 lord 级、
        /// 跨图天然可用；首版曾直发 StalkToAttack memo 进攻击模式，用户实测反馈丢失等待机制后
        /// 回退）。攻击模式后其 ChimeraAttack duty 的三个 giver（AIFightEnemies/
        /// AIGotoNearestHostile/Manhunter）均已跨图化。
        /// </summary>
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
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms, first => new LordJob_ChimeraAssault());
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>
        /// EntitySwarm 族（蹒跚怪群/小型群/动物群及后续同族变体，2026-09）：非 raid 族链（worker
        /// 直接 GenSpawn + LordMaker.MakeNewLord），与发狂动物/食尸鬼/奇美拉同款窗口迁移。只 patch
        /// 基类 TryExecuteWorker（变体只覆写属性，虚分发/继承直达基类声明）。lord 重建 = 反射调
        /// worker 自己的 <c>GenerateLordJob(entry, dest)</c>（protected virtual，全族收口于此）——
        /// entry 取迁移落点、dest 在宿主图上重选（镜像原生 TryFindTravelDestFrom），对新变体零
        /// 硬编码。Shambler hediff 寿命随 pawn 跨图保留。
        /// </summary>
        [HarmonyPatch(typeof(IncidentWorker_EntitySwarm), "TryExecuteWorker")]
        static class Patch_IncidentWorker_EntitySwarm_RaidOuterSpawn
        {
            static void Prefix(IncidentWorker __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true, __instance.def?.defName,
                    __instance.GetType());
            }

            static void Postfix(IncidentWorker_EntitySwarm __instance, IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.TryRelocateWindowGroup(parms,
                    first => BuildSwarmLordJob(__instance, first));
                SeamlessRaidOuterSpawn.End();
            }

            private static LordJob BuildSwarmLordJob(IncidentWorker_EntitySwarm worker, Pawn firstPawn)
            {
                try
                {
                    var entry = firstPawn.Position;
                    if (!RCellFinder.TryFindTravelDestFrom(entry, firstPawn.Map, out var dest)) dest = entry;
                    var method = AccessTools.Method(worker.GetType(), "GenerateLordJob");
                    if (method?.Invoke(worker, new object[] { entry, dest }) is LordJob job) return job;
                }
                catch (System.Exception ex)
                {
                    Log.Error("[RimExodus] EntitySwarm cross-map lord rebuild failed, group left lordless: " + ex);
                }
                return null; // 反射失败 → 不建 lord（落地由转移收编/游荡宽限兜底），不让异常杀死迁移
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
                SeamlessRaidOuterSpawn.TryRelocateToHost(parms, pawns);
            }
        }
    }
}
