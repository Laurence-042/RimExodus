using System.Reflection;
using HarmonyLib;

namespace RimExodus
{
    /// <summary>
    /// MapPreview mod 软依赖判据（反射，未装 MapPreview 时恒 false）。
    ///
    /// 官方组合用法（MapPreview 自身 Patch_Verse_Map.CheckSkipComponent 同款）：
    /// <c>MapPreviewAPI.IsGeneratingPreview</c>（全局有预览请求在进行）
    /// &amp;&amp; <c>MapPreviewGenerator.IsGeneratingOnCurrentThread</c>（当前线程是预览生成线程）。
    /// 双条件缺一不可：只判全局标志时，后台预览进行期间主线程的真实地图生成会被误跳；
    /// 只判线程标志时语义不完整。两个属性均为 static bool。
    ///
    /// 为什么需要它：MapPreview 在后台线程对**组件被裁剪的预览图**跑 genStep 白名单
    /// （FillComponents 被 transpiler 过滤，Reachability 等不在 IncludedMapComponentsFull
    /// 白名单的组件为 null）——任何触发 <c>PathGrid.NotifyCellDirtied</c> 的调用
    /// （<c>map.pathing.RecalculateAllPerceivedPathCosts</c>）在预览图上 NRE 于
    /// <c>map.reachability.ClearCache()</c>（2026-08 实测每张预览两条红字，被原版单步
    /// catch 吞掉，预览功能无损）。预览图不消费 pathGrid，跳过刷新语义正确。
    /// </summary>
    internal static class SeamlessMapPreviewCompat
    {
        private static readonly PropertyInfo _isGeneratingPreview = AccessTools.Property(
            AccessTools.TypeByName("MapPreview.MapPreviewAPI"), "IsGeneratingPreview");
        private static readonly PropertyInfo _isGeneratingOnCurrentThread = AccessTools.Property(
            AccessTools.TypeByName("MapPreview.MapPreviewGenerator"), "IsGeneratingOnCurrentThread");

        /// <summary>当前线程是否正在生成 MapPreview 预览（未装 MapPreview 恒 false）。</summary>
        public static bool IsGeneratingPreviewOnCurrentThread =>
            _isGeneratingPreview?.GetValue(null) is true
            && _isGeneratingOnCurrentThread?.GetValue(null) is true;
    }
}
