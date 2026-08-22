using HarmonyLib;
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
}
