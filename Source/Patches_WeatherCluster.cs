using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 天气域共享实例的重复推进修复（既有 bug，2026-08 随软休眠天气机理勘误一并发现）。
    ///
    /// 【bug】SeamlessWeatherClusterManager 用字段替换让同域各图共享同一套 WeatherManager /
    /// SkyManager 实例，但原版 Map.MapPostTick / Map.MapUpdate 是**走每张图自己的字段**调用
    /// WeatherManagerTick / SkyManagerUpdate 的——域内 N 张活跃图 = 同一实例每 tick/每帧被
    /// 推进 N 次：curWeatherAge 加速增长（换天气频率 N 倍）、天气事件与天空视觉过渡全部快进。
    /// （WeatherDeciderTick 无需守卫：内部仅条件比较，无计数推进，多次调用天然幂等。）
    ///
    /// 【修法】实例级幂等守卫——同 tick（WeatherManagerTick）/同帧（SkyManagerUpdate）内该实例
    /// 的第二次及以后调用直接跳过。不引入"域/宿主"概念、不误伤非共享的正常图（守卫按实例记录，
    /// 各实例独立），读档产生的新实例自动从头记录。键用 ConditionalWeakTable 弱引用，旧实例
    /// （图销毁/重绑丢弃）不阻碍 GC。
    ///
    /// 【宿主跟随（2026-08，邻图光照遮罩修复时发现）】SkyManagerUpdate 的视觉块门是
    /// if (map == Find.CurrentMap)，其中 map 是构造时的宿主图（readonly 字段）——共享后玩家聚焦
    /// 域内**非宿主**图时（宿主 = 域内最小 tileId 图，跑图时玩家大多数时间不在它上面），任何成员
    /// 的调用都过不了门：MatBases.LightOverlay.color（昼夜色）、FogOfWar 色、相机饱和度、太阳阴影
    /// 向量、_DayPercent 等全部冻结在宿主上次被聚焦时的值。守卫之前把宿主字段改写为当前聚焦图
    /// （仅当 CurrentMap.skyManager == 本实例，非共享/域外实例的宿主天然等于自己，零操作）。
    /// SkyManager 无 ExposeData，宿主字段纯运行时、无存档影响；curSky 计算随之改用当前图的
    /// game conditions / AffectsSky 物体（比恒用宿主的更正确）。与"宿主纯是工程叫法、换宿主
    /// 不换实例"的定夺一致。
    ///
    /// 【强制天气 = 域内并集（用户定夺 2026-08）】共享 WeatherDecider 的 map 字段钉死宿主图
    /// （与 SkyManager 不同，不做宿主跟随——跟随聚焦图会让强制天气随镜头切换翻转，违背"无跳变"），
    /// 而原版 ForcedWeather getter 只读 decider 自己 map 字段所指图的条件 → GameCondition_
    /// ForceWeather 族（Anomaly 异暗 UnnaturalDarkness/DeathPall/GrayPall/BloodRain，全部 per-map
    /// 注册）在域内两态错乱：条件在宿主图 → 泄漏整域（误）；在非宿主图 → 连自己都不生效（更误）。
    /// 修法 = <see cref="Patch_WeatherDecider_ForcedWeather_ClusterUnion"/>：共享实例的 getter 改为
    /// 聚合域内全部成员图的条件，任一图强制 → 全域生效（与"群系连通域共享天气源"前提一致）。
    /// 残余观察项：CurSkyGlow 是共享 SkyManager 单值，聚焦哪张图决定全域光照数值（太阳能/血族
    /// 日光/暗视），与强制天气无关，未修。
    /// </summary>
    public static class Patches_WeatherCluster
    {
        private class IntBox
        {
            public int v;
        }

        private static readonly AccessTools.FieldRef<SkyManager, Map> skyManagerHostMapRef =
            AccessTools.FieldRefAccess<SkyManager, Map>("map");

        private static readonly ConditionalWeakTable<WeatherManager, IntBox> LastWeatherTickOf =
            new ConditionalWeakTable<WeatherManager, IntBox>();

        private static readonly ConditionalWeakTable<SkyManager, IntBox> LastSkyUpdateOf =
            new ConditionalWeakTable<SkyManager, IntBox>();

        [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.WeatherManagerTick))]
        static class Patch_WeatherManager_WeatherManagerTick
        {
            static bool Prefix(WeatherManager __instance)
            {
                var box = LastWeatherTickOf.GetOrCreateValue(__instance);
                var now = Find.TickManager?.TicksGame ?? -1;
                if (box.v == now) return false; // 本 tick 已被同域其他图推进过（共享实例）。
                box.v = now;
                return true;
            }
        }

        [HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
        static class Patch_SkyManager_SkyManagerUpdate
        {
            static bool Prefix(SkyManager __instance)
            {
                var current = Find.CurrentMap;
                if (current != null && current.skyManager == __instance && skyManagerHostMapRef(__instance) != current)
                {
                    skyManagerHostMapRef(__instance) = current; // 宿主跟随当前聚焦图（视觉块门用，见类头注释）。
                }

                var box = LastSkyUpdateOf.GetOrCreateValue(__instance);
                var now = Time.frameCount;
                if (box.v == now) return false; // 本帧已被同域其他图推进过（共享实例）。
                box.v = now;
                return true;
            }
        }

        // 属性 getter patch 必须显式 MethodType.Getter（Harmony 按名解析默认找同名**方法**，
        // getter 实名是 get_ForcedWeather → 不标则 Undefined target method，离线验证器实测拦截）。
        [HarmonyPatch(typeof(WeatherDecider), nameof(WeatherDecider.ForcedWeather), MethodType.Getter)]
        static class Patch_WeatherDecider_ForcedWeather_ClusterUnion
        {
            private static readonly List<GameCondition> conditionsTmp = new List<GameCondition>();

            static bool Prefix(WeatherDecider __instance, ref WeatherDef __result)
            {
                // 域成员 = weatherDecider 字段指向本实例的图（含休眠图——软休眠 Map 保留、字段仍在）。
                // 先数持有图：0 张（剥离期/半拆除）放行原版（读自己 map 字段）；1 张 = 非共享，
                // 放行原版（零语义面变化，全部非集群 decider 走原生路径）；≥2 张 = 天气域共享实例，
                // 改写为域内并集。getter 被 WeatherDeciderTick 每 tick 调用（同域各活跃图各一次，
                // Tick 本身幂等无守卫），此处每次重算：≤37 次引用比较 + 小条件列表枚举，成本可忽略。
                int owners = 0;
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    if (ReferenceEquals(Find.Maps[i].weatherDecider, __instance) && ++owners >= 2) break;
                }
                if (owners < 2) return true;

                // 域内并集：按 Find.Maps 稳定顺序逐图枚举 GetAllGameConditionsAffectingMap
                // （含世界级条件，与原版单图路径同源），"最后一个非空胜出"对齐原版 getter 语义。
                // GameCondition_ForceWeather 族全部 per-map 注册，任一成员图强制 → 全域生效。
                __result = null;
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    var m = Find.Maps[i];
                    if (!ReferenceEquals(m.weatherDecider, __instance)) continue;
                    conditionsTmp.Clear();
                    m.gameConditionManager.GetAllGameConditionsAffectingMap(m, conditionsTmp);
                    for (int c = 0; c < conditionsTmp.Count; c++)
                    {
                        var forced = conditionsTmp[c].ForcedWeather();
                        if (forced != null) __result = forced;
                    }
                }
                return false;
            }
        }
    }
}
