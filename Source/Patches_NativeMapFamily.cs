using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 原版「全员离开即删图」被动删除拦截（2026-08 用户定夺：删除时机从"人走即删"改为"距离删"）。
    ///
    /// 原版链路：MapParent.TickInterval（**世界 tick**，与地图休眠无关——休眠图同样被调）每 tick
    /// CheckRemoveMapNow → ShouldRemoveMapNow——Settlement / Site（含埋伏、遗迹等一切 site part）/
    /// Camp / CaravansBattlefield / DestroyedSettlement 在图上无 pawn/建筑阻挡时删图（部分连
    /// WorldObject 一起删）。本 patch 在唯一收口点 <see cref="MapParent.CheckRemoveMapNow"/>
    /// （public 非虚，覆盖全部子类覆写）Prefix 拦下原生家族的被动删除，删除时机移交
    /// <see cref="SeamlessDormancyGovernor"/>（距离 ≥ deleteHops 时
    /// <see cref="SeamlessTileManager.RemoveRollingMap"/> 延迟执行原版删除偏好）。
    ///
    /// 覆盖的非 tick 直调方：Dialog_FormCaravan"立即重组远行队"与 MoveColonyUtility 迁居——
    /// 两处离开后图保留，归滚动删除（与营地接管后的行为一致）。主动销毁（WorldObject.Destroy →
    /// PostRemove 删图，如任务结算）不经 CheckRemoveMapNow，不受影响。
    ///
    /// 口袋图（PocketMapParent 不覆写、基类 ShouldRemoveMapNow 默认 false）与空间层图
    /// （SpaceMapParent 不属 <see cref="SeamlessMapGovernance.IsNativeFamily"/>）不受影响。
    /// dormancyEnabled=false 时放行原版（与 governor 关闭时"不睡不删"的语义一致——回原版行为）。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
    static class Patch_MapParent_CheckRemoveMapNow_NativeFamily
    {
        static bool Prefix(MapParent __instance)
        {
            if (!__instance.HasMap) return true;
            if (!SeamlessMapGovernance.IsNativeFamily(__instance)) return true;
            if (!(RimExodusMod.Settings?.dormancyEnabled ?? true)) return true;
            // 删除时机归 governor（距离策略）；被拦截期间图照常休眠/唤醒（休眠 ≠ 删除）。
            return false;
        }
    }

    /// <summary>
    /// 据点败亡结算收口（2026-08 游戏内两轮实测定稿，用户定夺口径，勿删勿加码）。
    ///
    /// **Prefix——只有敌对据点才可能"被击败"**：原版 <see cref="SettlementDefeatUtility.IsDefeated"/>
    /// 判据 = "图上无对玩家的活跃威胁"（GenHostility.IsActiveThreatToPlayer），隐含假设"据点图只在
    /// 进攻敌对据点期间存在"。RimExodus 预加载生成的**中立**据点图驻军非敌对 → 判据恒真 → 生成后、
    /// 或玩家跨缝走进的**当 tick**即被误走进攻胜利结算（DestroyedSettlement 顶替 + Settlement WO
    /// 销毁 + 该派系敌人 +20 好感 + 可能 faction.defeated + CaravanAssaultSuccessful 传说从空殖民者集
    /// 随机取人 NRE）。原版进攻入口（运输舱/远行队 Attack）本就要求敌对——非敌对派系直接跳过结算
    /// 即恢复原版可达语义。
    ///
    /// **Postfix——真败亡后的无缝网重接**：原版败亡结算把 <c>map.info.parent</c> 偷换给新建的
    /// DestroyedSettlement **再** Destroy 老 Settlement——图存活易主（<see cref="MapParent.Map"/>
    /// 按 info.parent 实时查找，PostRemove 的"持图连删"不触发），但邻图反向邻居链仍指向已销毁的
    /// 老 WO（渲染断链/邻接查询 false/传送点缓存陈旧；且 governor 删废墟图时 CleanupNeighborLinks
    /// 按"反链指向废墟 WO"匹配、清不掉死 WO 条目）。以 <c>factionBase.Destroyed</c> 为门（原版体
    /// 早退/未败亡时不动），扫全部图的邻居表把指向老 WO 的 link 就地改指废墟 parent（同 tile 同
    /// offset），两端刷新传送点缓存。
    ///
    /// **历史教训（勿回退）**：两版被进场穿透的守卫——①挂 CheckDefeated 的"无玩家在场"单条件
    /// （玩家跨缝进图当 tick 即在场）；②挂 IsDefeated（public static）的"无玩家在场"（同样被进场
    /// 穿透，且越权改 public static 全局语义影响第三方调用方）。定稿口径 = **派系关系单条件**。
    /// </summary>
    [HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
    static class Patch_SettlementDefeat_CheckDefeated
    {
        static bool Prefix(Settlement factionBase)
        {
            var faction = factionBase?.Faction;
            // 只有敌对据点才可能"被击败"（原版进攻入口要求敌对）；非敌对（含 null/玩家派系）跳过结算。
            return faction != null && faction != Faction.OfPlayer && faction.HostileTo(Faction.OfPlayer);
        }

        static void Postfix(Settlement factionBase)
        {
            if (factionBase == null || !factionBase.Destroyed) return; // 原版体早退（未败亡）时未销毁。
            var ruinsParent = Find.WorldObjects.MapParentAt(factionBase.Tile); // 败亡结算原地新建的 DestroyedSettlement（同 tile）。
            var ruinsMap = ruinsParent?.Map;
            if (ruinsMap == null) return; // 废墟图已不存在的异常态——无需重接。

            foreach (var otherMap in Find.Maps)
            {
                if (otherMap == ruinsMap || otherMap.Disposed) continue;
                var links = SeamlessMapData.Neighbors(otherMap);
                if (links == null) continue;
                var touched = false;
                foreach (var link in links)
                {
                    if (link != null && link.neighbor == factionBase)
                    {
                        link.neighbor = ruinsParent; // 同 tile 同 offset，只换 WO 引用。
                        touched = true;
                    }
                }
                if (touched) SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(otherMap);
            }
            // 废墟图自己的出向链表存图组件（parent 偷换不影响），刷新其对端缓存收尾。
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(ruinsMap);
            Log.Message($"[RimExodus] Settlement defeated at tile {factionBase.Tile.tileId}: seamless neighbor links " +
                        $"rerouted to the DestroyedSettlement parent (map {ruinsMap.uniqueID}).");
        }
    }

    /// <summary>
    /// RimExodus 预加载生成的据点图跳过"被发现报复袭击"倒计时（2026-08，勿删）。
    ///
    /// TimedDetectionRaids 是 Settlement def **自带**组件（非进攻动态添加），
    /// <see cref="Settlement.PostMapGenerate"/> 对任何非玩家家据点图生成都启动 240000 tick（4 游戏
    /// 日）倒计时——原版语义是"玩家闯入/进攻据点被发现后的报复"，但我们的**中立**据点预加载生成
    /// 同样触发：约 1 天后玩家收到无端的"即将被袭击"威胁信，4 天后对无人的中立基地刷敌对袭击
    /// （中立派系会被 TryResolveRaidFaction 换成随机敌对派系执行）。
    /// 修复 = 生成标志（<see cref="SeamlessTileManager.GeneratingNativeSeamlessly"/>，预加载链
    /// try/finally 维护）期间 Postfix 直接 <c>ResetCountdown()</c> 撤销原版刚启动的倒计时；原版
    /// 进攻流程（运输舱进攻的报复倒计时是原版语义）不经标志期，原样保留。
    /// </summary>
    [HarmonyPatch(typeof(Settlement), nameof(Settlement.PostMapGenerate))]
    static class Patch_Settlement_PostMapGenerate_SkipDetectionRaids
    {
        static void Postfix(Settlement __instance)
        {
            if (!SeamlessTileManager.GeneratingNativeSeamlessly) return;
            if (__instance.TryGetComponent<TimedDetectionRaids>(out var comp) && comp.DetectionCountdownStarted)
            {
                comp.ResetCountdown();
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] TimedDetectionRaids countdown skipped for seamless-generated settlement at tile {__instance.Tile.tileId}.");
            }
        }
    }
}
