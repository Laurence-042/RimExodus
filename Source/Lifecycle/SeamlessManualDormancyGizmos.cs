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
        public static IEnumerable<Gizmo> ForParent(MapParent parent)
        {
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
                var delete = new Command_ManualDormancy
                {
                    defaultLabel = "RimExodus_DeleteTileMap".Translate(),
                    defaultDesc = (tileMap ? "RimExodus_DeleteTileMapDesc" : "RimExodus_DeletePoiMapDesc").Translate(),
                    icon = TileWorldIcons.DeleteCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        // 地块图 = 销毁 Map+WorldObject（"从未出现过"，下次进入走生成链重建）；
                        // 原生家族 = 延迟执行原版删除偏好（Settlement 删图留对象再访重生成驻军，
                        // 废墟/战场连对象删）。确认防误触。
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            (tileMap ? "RimExodus_DeleteTileMapConfirm" : "RimExodus_DeletePoiMapConfirm").Translate(parent.Label), delegate
                            {
                                map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(parent);
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
                        SeamlessDormancyManager.Sleep(map, "player gizmo (world map)", manual: true);
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
