using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图软休眠的运行时拦截（用户定夺 2026-08，与 <see cref="Patches_IncrementalMapGen"/> 同款模式）。
    ///
    /// - Map.MapPreTick / MapPostTick / MapUpdate：休眠图早退（三方法均 public 非虚，原版
    ///   TickManager.DoSingleTick / Game.UpdatePlay 直接遍历 Find.Maps 调用，无其他拦截点）。
    ///   MapPostTick 内含 MapComponentUtility.MapComponentTick、MapUpdate 内含 MapComponentUpdate，
    ///   跳过即停组件模拟/更新；MapUpdate 的渲染部分本就只对 CurrentMap 生效（Map.cs 门控）。
    /// - Game.CurrentMap setter：一切"进入地图"路径（世界地图"查看地图"gizmo、殖民者栏组框点击、
    ///   CameraJumper、跨图选中）都汇聚于此 setter——目标为休眠图则先同步唤醒再放行，
    ///   单点覆盖全部唤醒入口（"唤醒就是下次进入休眠地图时"，用户定夺）。
    /// - Storyteller.AllIncidentTargets：剔除休眠图，防 storyteller 把袭击/事件打到冻结图上
    ///   （pawn 同步 spawn 后不 tick 站着不动，直到玩家回来——语义不可接受）。
    ///   注意返回的是原版静态缓存列表，调用方即用即弃，Postfix 原地 RemoveAll 安全。
    /// </summary>
    public static class Patches_Dormancy
    {
        [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
        static class Patch_Map_MapPreTick_Dormancy
        {
            static bool Prefix(Map __instance)
            {
                return !SeamlessDormancyManager.IsDormant(__instance); // 休眠图跳过 tick。
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
        static class Patch_Map_MapPostTick_Dormancy
        {
            static bool Prefix(Map __instance)
            {
                return !SeamlessDormancyManager.IsDormant(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        static class Patch_Map_MapUpdate_Dormancy
        {
            static bool Prefix(Map __instance)
            {
                return !SeamlessDormancyManager.IsDormant(__instance); // 休眠图跳过每帧更新（含组件 Update）。
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
        static class Patch_Game_CurrentMap_Setter
        {
            static void Prefix(Map value)
            {
                // 进入休眠图 = 玩家的显式唤醒意图：先同步 Wake（轻量，无生成）再切图。
                if (value != null && SeamlessDormancyManager.IsDormant(value))
                {
                    SeamlessDormancyManager.Wake(value, "entering map (CurrentMap switch)");
                }
            }
        }

        [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets), MethodType.Getter)]
        static class Patch_Storyteller_AllIncidentTargets
        {
            static void Postfix(List<IIncidentTarget> __result)
            {
                __result.RemoveAll(t => t is Map m && SeamlessDormancyManager.IsDormant(m));
            }
        }

        /// <summary>
        /// 顶部殖民者栏的地图分组框 = 休眠图的"地图访问入口"（用户定夺 2026-08：休眠图不得显示）。
        /// 原版 CheckRecacheEntries 遍历 Find.Maps **每图一组**——即使组内无殖民者 pawn 也会
        /// Add(new Entry(null, map, group)) 画空分组框，点击框切图（HandleGroupFrameClicks →
        /// CurrentMap setter，会被唤醒 patch 接管）。此处 Postfix 从 cachedEntries 移除休眠图
        /// 的全部 entry（含殖民者尸体 entry——休眠图不可访问，点也点不进去），并把 group 重编为
        /// 连续编号。重编必须用构造器重建 entry：Entry 的 reorderAction/extraDraggedItemOnGUI
        /// 闭包捕获构造时的 group，直改字段会使拖动跨组排序写错组。
        /// Sleep/Wake 收尾调 MarkColonistsDirty 触发重算，状态切换立即生效。
        /// </summary>
        [HarmonyPatch(typeof(ColonistBar), "CheckRecacheEntries")]
        static class Patch_ColonistBar_CheckRecacheEntries
        {
            static void Postfix(ColonistBar __instance)
            {
                var entriesField = AccessTools.Field(typeof(ColonistBar), "cachedEntries");
                if (!(entriesField?.GetValue(__instance) is List<ColonistBar.Entry> entries)) return;

                var removed = entries.RemoveAll(e => e.map != null && SeamlessDormancyManager.IsDormant(e.map));
                if (removed <= 0) return;

                var lastOriginal = int.MinValue;
                var newGroup = -1;
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.group != lastOriginal)
                    {
                        lastOriginal = e.group;
                        newGroup++;
                    }
                    entries[i] = new ColonistBar.Entry(e.pawn, e.map, newGroup);
                }
            }
        }
    }
}
