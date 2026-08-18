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
    }
}
