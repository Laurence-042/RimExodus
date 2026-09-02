using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 天气域"决策集中 + 执行各图"模型的四个 Harmony patch（2026-09 重构，取代旧
    /// <see cref="SeamlessWeatherClusterManager"/> 字段替换时代的幂等守卫 + SkyManager 宿主跟随 +
    /// ForcedWeather 域内并集三 patch——形态 B 下每图保留原生实例照常执行，三者皆无存在必要）。
    ///
    /// 【优先级不对称原则（2026-09 用户定夺）】
    /// - **决策方法吞（Priority.First）**：被动成员的 WeatherDeciderTick 本来就不存在决策过程
    ///   （决策只发生在激活图），短路时要抢先于一切第三方 prefix 一并吞——否则挂着决策 tick 上
    ///   做统计的第三方 mod 会看到"每 tick N 次决策事件"而真实决策只有 1 次（垃圾数据）。
    ///   观察天气变化者的正确钩子是 TransitionTo/curWeather——广播使它们对每张成员图原生成立。
    /// - **执行方法让（Priority.Last）**：TransitionTo/DisableRainFor 的广播/转发 prefix 放最后，
    ///   第三方取消型 prefix（return false）先于我们执行——取消时 Harmony 跳过其后全部 prefix
    ///   （含我们的），广播不发生，全域一致地取消，不出现"本图没换、邻图换了"的分叉。
    ///
    /// 【重入】TransitionTo 广播与 DisableRainFor 转发都会对成员再调同名方法（再次进入本 patch），
    /// 由 <see cref="SeamlessWeatherClusterManager"/> 的静态重入标志放行原体——漏防 = 递归广播/死循环，
    /// 这是整个模型的正确性单点。
    /// </summary>
    public static class Patches_WeatherDomain
    {
        /// <summary>
        /// 决策门控：被动成员的 decider tick 整方法短路（Priority.First 连第三方 prefix 一并吞，
        /// 理由见类头"决策方法吞"）。无域（单图分量/口袋图/空间层图/第三方图）与激活图放行原版。
        /// 被 Map.MapPostTick 每 tick 调用，门控为 O(1) 字典查询。
        /// </summary>
        [HarmonyPatch(typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick))]
        static class Patch_WeatherDecider_WeatherDeciderTick_DomainDecisionGate
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(WeatherDecider __instance)
            {
                return SeamlessWeatherClusterManager.ShouldRunDeciderTick(__instance);
            }
        }

        /// <summary>
        /// 换天广播：任一成员 TransitionTo → 同 tick 对域内其余成员各 TransitionTo 一次（含休眠图，
        /// 纯字段写 + overlay 重置，无 tick 依赖）。重入标志内直接放行原体。Priority.Last（"执行方法让"）。
        /// </summary>
        [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.TransitionTo))]
        static class Patch_WeatherManager_TransitionTo_DomainBroadcast
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(WeatherManager __instance, WeatherDef newWeather)
            {
                SeamlessWeatherClusterManager.BroadcastTransitionIfShared(__instance, newWeather);
            }
        }

        /// <summary>
        /// 禁雨转发：任一成员 DisableRainFor → 域内全部成员同窗口禁雨（保持各图 decider 的
        /// ticksWhenRainAllowedAgain 一致，自然轮换的降雨滤波不打架）。重入标志内直接放行。
        /// Priority.Last（"执行方法让"）。
        /// </summary>
        [HarmonyPatch(typeof(WeatherDecider), nameof(WeatherDecider.DisableRainFor))]
        static class Patch_WeatherDecider_DisableRainFor_DomainForward
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(WeatherDecider __instance, int ticks)
            {
                SeamlessWeatherClusterManager.ForwardDisableRainIfShared(__instance, ticks);
            }
        }

        /// <summary>
        /// 强制天气激活：条件注册当刻（仪式/灾害/任务/dev——GameCondition_ForceWeather 族的唯一
        /// 注册口）识别 ForcedWeather() != null 并把条件持有图提升为域激活图（其 decider 的
        /// ForcedWeather getter 原生读自己图的条件，取代旧的 getter 域内并集 patch）。
        /// ownerMap == null 的世界级 manager 放行不管——世界条件经 parent 链对全部图生效，
        /// 激活图 decider 原生读到。全程 try/catch：激活失败绝不伤害条件注册本身。
        /// </summary>
        [HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.RegisterCondition))]
        static class Patch_GameConditionManager_RegisterCondition_ActivateForcedWeather
        {
            static void Postfix(GameConditionManager __instance, GameCondition cond)
            {
                try
                {
                    if (cond == null) return;
                    var map = __instance.ownerMap; // 世界级/剥离态为 null → 原生语义。
                    if (map == null || map.Disposed) return;

                    WeatherDef forced = null;
                    try { forced = cond.ForcedWeather(); }
                    catch { return; } // 条件未完全初始化等异常态：不激活，原生 getter 会在读到时自行处理。
                    if (forced == null) return;

                    SeamlessWeatherClusterManager.ActivateForcedWeather(map);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] Weather activation patch error (non-fatal): {ex}");
                }
            }
        }
    }
}
