using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 手动休眠/删除命令的标记子类：据点贸易商对话（<see cref="SeamlessSettlementTalk"/>）按类型
    /// 过滤本类实例，地图生命周期操作不进"远行者"面板（不靠 label/icon 匹配——2026-08 实测
    /// Dev 后缀/禁用原因内联曾令精确匹配漏网的教训）。
    /// </summary>
    internal class Command_ManualDormancy : Command_Action
    {
    }

    /// <summary>
    /// 手动休眠/删除 gizmo 的唯一构造处（2026-09 扩展到原生家族 POI 图）。两个消费入口：
    /// - 地块图（含营地）：<see cref="MapParent_SeamlessTile.GetGizmos"/> 覆写调本 helper（行为与
    ///   2026-08 首版逐键一致）；
    /// - 原生家族 POI（Settlement[含玩家自家与 ArchotechSettlement]、Site[埋伏/远古复杂等一切
    ///   SitePart 图]、CaravansBattlefield、DestroyedSettlement——即
    ///   <see cref="SeamlessMapGovernance.IsNativeFamily"/> 全集）：
    ///   <see cref="Patch_MapParent_GetGizmos_ManualDormancy"/> Postfix。
    ///
    /// 底层零新机制：Sleep/Wake 手动锁（manualDormantTiles 按 tile id 持久化，LoadedGame 重睡对
    /// 原生 parent 图同样生效）、<see cref="SeamlessTileManager.RemoveRollingMap"/>（对原生 parent
    /// 自动分派 RemoveNativeFamilyMap：Forget→影子远行队拆→DeinitAndRemoveMap→天气域重绑）均
    /// 类型无关，本类只补 gizmo 挂点。
    /// </summary>
    internal static class SeamlessManualDormancyGizmos
    {
        private static bool IsLiveParentMap(MapParent parent, Map map)
        {
            return parent != null && !parent.Destroyed
                && map != null && !map.Disposed && parent.Map == map;
        }

        private static void RemoveAfterPlayerPawnWarning(MapParent parent, Map map)
        {
            if (!IsLiveParentMap(parent, map)) return;
            if (!SeamlessMapGovernance.HasPlayerPawn(map))
            {
                map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(parent);
                return;
            }

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimExodus_DeleteTileMapPlayerPawnConfirm".Translate(parent.Label), delegate
                {
                    // 弹窗停留期间地图可能已被移除或换绑；执行前按最新状态复核对象。
                    if (!IsLiveParentMap(parent, map)) return;
                    map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(parent);
                }, destructive: true));
        }

        public static IEnumerable<Gizmo> ForParent(MapParent parent)
        {
            // 封存态（2026-09 前哨保留）：无图有记录的 WO——只提供"删除此封存"（销毁 WO 连记录
            // = 放弃该前哨，与淘汰/卸载恢复同语义）。恢复入口 = 走近触发生成链（守卫识别复用），
            // 不在此提供（预加载链才是唯一正确入口，见 GenerateTileMap 守卫注释）。
            if (parent is MapParent_SeamlessTile archivedTile && archivedTile.preserveRecord != null && !archivedTile.Destroyed)
            {
                var deleteArchive = new Command_ManualDormancy
                {
                    defaultLabel = "RimExodus_DeleteArchivedMap".Translate(),
                    defaultDesc = "RimExodus_DeleteArchivedMapDesc".Translate(),
                    icon = TileWorldIcons.DeleteCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            "RimExodus_DeleteArchivedMapConfirm".Translate(parent.Label), delegate
                            {
                                TileWorldIcons.ReleaseTileMesh(archivedTile.Tile);
                                archivedTile.Destroy();
                            }, destructive: true));
                    },
                };
                yield return deleteArchive;
                yield break;
            }

            var map = parent.Map;
            if (map == null || map.Disposed) yield break;
            if (!SeamlessMapGovernance.IsGoverned(map)) yield break;
            // 家园特权：不休眠不删除（玩家自家定居点 IsPlayerHome 同此口径，与地块图家园一致；
            // 原版"放弃定居点"路径仍可用）。
            if (SeamlessMapGovernance.IsProtectedHome(map)) yield break;
            if (IncrementalMapGenerator.IsGenerating(map)) yield break; // 分帧生成中不可干预。

            if (SeamlessDormancyManager.IsDormant(map))
            {
                bool tileMap = parent is MapParent_SeamlessTile;
                // 前哨保留（2026-09 单一入口收拢）：地块图居住区达阈时，"删除此图"实际执行封存——
                // 确认文案换封存语义（彻底放弃 = 封存后在世界图 WO 上选"丢弃已封存"）；action 不变，
                // RemoveRollingMap 入口内部对达阈图分流到 ArchiveTileMap（勿在此自行前置封存调用——
                // 删除流程只有一个入口）。
                bool willArchive = tileMap
                    && SeamlessMapModificationTracker.Evaluate(map, out _) == PreserveDecision.Auto;
                var delete = new Command_ManualDormancy
                {
                    defaultLabel = "RimExodus_DeleteTileMap".Translate(),
                    defaultDesc = (tileMap ? "RimExodus_DeleteTileMapDesc" : "RimExodus_DeletePoiMapDesc").Translate(),
                    icon = TileWorldIcons.DeleteCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        // 地块图 = 销毁 Map+WorldObject（"从未出现过"，下次进入走生成链重建；
                        // 达阈前哨经入口内部分流转为封存）；原生家族 = 延迟执行原版删除偏好
                        //（Settlement 删图留对象再访重生成驻军，废墟/战场连对象删）。确认防误触。
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            (willArchive
                                ? "RimExodus_ArchiveTileMapConfirm"
                                : tileMap ? "RimExodus_DeleteTileMapConfirm" : "RimExodus_DeletePoiMapConfirm"
                            ).Translate(parent.Label), delegate
                            {
                                // 第一层保留原有删除/封存确认；答复时按地图最新状态决定是否追加
                                // “玩家 pawn 在场”风险确认（口径复用治理层，勿另写遍历）。
                                RemoveAfterPlayerPawnWarning(parent, map);
                            }, destructive: true));
                    },
                };
                // 用户定夺 2026-09：原生家族手动删除按原版规则拦下——殖民者/运输舱/重力锚等阻挡项
                // 在场时灰显给原因（防误删丢殖民者；与滚动删除同语义，RemoveNativeFamilyMap 内部
                // 还会再跑原版判定兜底）。预检纯只读无副作用。地块图删除保持无条件（既有行为）。
                if (!tileMap && !parent.ShouldRemoveMapNow(out _))
                {
                    delete.Disable("RimExodus_DeletePoiMapBlocked".Translate());
                }
                yield return delete;
            }
            else
            {
                bool isCurrentMap = map == Find.CurrentMap;
                var sleep = new Command_ManualDormancy
                {
                    defaultLabel = "RimExodus_SleepTileMap".Translate(),
                    defaultDesc = "RimExodus_SleepTileMapDesc".Translate(),
                    icon = TileWorldIcons.SleepCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        if (!SeamlessMapGovernance.HasPlayerPawn(map))
                        {
                            SeamlessDormancyManager.Sleep(map, "player gizmo (world map)", manual: true);
                            return;
                        }

                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            "RimExodus_SleepTileMapPlayerPawnConfirm".Translate(parent.Label), delegate
                            {
                                if (!IsLiveParentMap(parent, map) || map == Find.CurrentMap
                                    || SeamlessDormancyManager.IsDormant(map)) return;
                                SeamlessDormancyManager.Sleep(map, "player gizmo (world map)", manual: true);
                            }, destructive: true));
                    },
                };
                if (isCurrentMap)
                {
                    // CurrentMap 是 governor 无条件保活项——睡着会立刻被视作异常，玩家须先切走再睡。
                    sleep.Disable("RimExodus_SleepTileMapDisabledCurrentMap".Translate());
                }
                yield return sleep;
            }
        }
    }

    /// <summary>
    /// 原生家族 POI 图的手动休眠/删除 gizmo 挂点（2026-09）。
    ///
    /// <see cref="MapParent.GetGizmos"/> 是迭代器方法：Postfix 在枚举器创建时执行，必须惰性包一层
    /// （转发原序列再追加），base 体执行时机与原版一致。派发面已核实：覆写类 Settlement/Site/Camp/
    /// DestroyedSettlement 恰调一次 base（base 调用是非虚直调，patch 的方法体仍会执行），
    /// CaravansBattlefield 不覆写直达基类——每个对象恰触发一次。空间层图/口袋图被
    /// IsGoverned→IsNativeFamily 的表面层守卫天然排除。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.GetGizmos))]
    static class Patch_MapParent_GetGizmos_ManualDormancy
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, MapParent __instance)
        {
            foreach (var gizmo in __result) yield return gizmo;
            // 地块图由 MapParent_SeamlessTile.GetGizmos 覆写提供（其内部调 base 也会进本 Postfix），
            // 排除防重复。
            if (__instance is MapParent_SeamlessTile) yield break;
            foreach (var gizmo in SeamlessManualDormancyGizmos.ForParent(__instance)) yield return gizmo;
        }
    }
}
