using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 袭击外缘生成的三个事件入口 patch（机制与资格三分支见 <see cref="SeamlessRaidOuterSpawn"/> 类注释）：
    ///
    /// ① <see cref="IncidentWorker_Raid.TryExecuteWorker"/>（raid 族总入口）：Prefix 建计划
    ///    （同图约束 ambient + 跨图生成挂起），Postfix 清。**只 patch 基类方法**——
    ///    IncidentWorker_RaidEnemy 虽覆写 TryExecuteWorker 但首行调 base.TryExecuteWorker
    ///    （非虚直调，命中基类补丁）；RaidFriendly 不覆写、虚分发直达基类——两者全覆盖。
    ///    HateChanters（邪教徒）继承 IncidentWorker 自带独立 TryExecuteWorker，不经此链 =
    ///    天然排除（用户定夺 v1 不做：自带"边缘分批布点 + 吟唱"机制且跨图难度剧增）。
    /// ② <see cref="IncidentWorker_AggressiveAnimals"/>（发狂动物，1.6 名，旧 ManhunterPack）：
    ///    仅同图约束（allowCrossMap=false）——动物非 NPC 战斗体、近战索敌不跨图，无法行军。
    /// ③ <see cref="IncidentWorker_GhoulAttack"/>（食尸鬼）：仅同图约束；跨图列为后续增量
    ///    （其 worker 内自建无超时 AssaultColony lord，迁移点不同族）。
    ///
    /// 跨图执行点 = <see cref="RaidStrategyWorker.MakeLords"/> Postfix：此时 pawn 已在 A 生成、
    /// lord 已建好且自洽（生成与建 lord 在 TryExecuteWorker 里是分离两步），Postfix 里整体迁移 +
    /// B 上重建——半途失败时 A 上已建好的 lord 与生成位原样保留 = 原版行为。MakeLords 被
    /// SiegeMechanoid/PsychicRitualSiege 覆写（Harmony 基类补丁不命中覆写）——它们非
    /// AssaultColony 纯突击策略，本就不在跨图范围，零损失。
    /// </summary>
    static class Patches_RaidOuterSpawn
    {
        /// <summary>
        /// raid 族（敌对 + 友好援军）总入口。Prefix 时 parms.raidArrivalMode 通常未解析
        /// （TryGenerateRaidInfo 内才选）——计划不依赖它：空投族不调 4 参边缘原语 = ambient
        /// 自然不被消费；跨图迁移在 MakeLords Postfix 处再验 walkIn。
        /// </summary>
        [HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
        static class Patch_IncidentWorker_Raid_TryExecuteWorker_RaidOuterSpawn
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: true);
            }

            static void Postfix()
            {
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>发狂动物：仅同图约束（无 lord、纯 mental state，跨图无法行军）。</summary>
        [HarmonyPatch(typeof(IncidentWorker_AggressiveAnimals), "TryExecuteWorker")]
        static class Patch_IncidentWorker_AggressiveAnimals_RaidOuterSpawn
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: false);
            }

            static void Postfix()
            {
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>食尸鬼：仅同图约束（跨图见类注释——后续增量）。</summary>
        [HarmonyPatch(typeof(IncidentWorker_GhoulAttack), "TryExecuteWorker")]
        static class Patch_IncidentWorker_GhoulAttack_RaidOuterSpawn
        {
            static void Prefix(IncidentParms parms)
            {
                SeamlessRaidOuterSpawn.BeginIncident(parms, allowCrossMap: false);
            }

            static void Postfix()
            {
                SeamlessRaidOuterSpawn.End();
            }
        }

        /// <summary>
        /// 跨图生成的执行点：MakeLords 是 TryExecuteWorker 里生成（Arrive）之后的独立步骤，
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
