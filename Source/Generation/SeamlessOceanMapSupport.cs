using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// RimExodus 内置海洋地图补全（默认开，可在设置关闭并重启后让专门海洋 mod 接管）。
    ///
    /// 原版 Ocean 是不可进入的背景 biome，因此没有 terrainsByFertility、天气权重，也没有可供
    /// 普通 Pawn 使用的陆地出生点。RimExodus 允许海洋地块生成地图后，缺失地形会回退 Sand，
    /// 通用 Base_Player genStep 还会尝试寻找陆地出生点/散布物/陆生动物并刷失败日志。
    ///
    /// Def 已在 Mod 构造器之前加载，而 GetSettings 在构造器中可用：Initialize 按本次启动时的
    /// 设置值只补空表，不覆盖其他 mod 已提供的 Ocean 地形/天气。EnabledForSession 锁存启动值，
    /// 设置窗口中途切换不会造成“Def 已补但生成分支已关闭”的半生效状态；重启后整体切换。
    /// </summary>
    internal static class SeamlessOceanMapSupport
    {
        internal static bool EnabledForSession { get; private set; } = true;

        private static BiomeDef oceanBiome;

        internal static void Initialize(bool enabled)
        {
            EnabledForSession = enabled;
            if (!enabled) return;

            // Mod 构造器执行时 ContentParser 已建立 DefDatabase，但 DefOf 静态字段仍可能尚未注入。
            // 延迟到当前加载长事件收尾：此时 DefOf/全部 mod Def 均稳定，又早于任何玩家地图生成。
            LongEventHandler.ExecuteWhenFinished(ApplyDefs);
        }

        private static void ApplyDefs()
        {
            oceanBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("Ocean");
            var deepOcean = DefDatabase<TerrainDef>.GetNamedSilentFail("WaterOceanDeep");
            if (oceanBiome == null || deepOcean == null)
            {
                Log.Warning($"[RimExodus] Built-in ocean map support could not initialize " +
                    $"(Ocean biome={oceanBiome != null}, WaterOceanDeep terrain={deepOcean != null}); " +
                    "Ocean defaults were not changed.");
                return;
            }

            // 只补原版空表：专门海洋 mod 已定义地形时不覆盖其选择。
            var terrainAdded = false;
            if (oceanBiome.terrainsByFertility.Count == 0)
            {
                oceanBiome.terrainsByFertility.Add(new TerrainThreshold
                {
                    terrain = deepOcean,
                    min = -999f,
                    max = 999f
                });
                terrainAdded = true;
            }

            // 基础海洋天气：晴、雾、雨、干雷雨、雷雨、雾雨。不加温度相关雪天，也不覆盖 mod 表。
            var weatherAdded = false;
            if (oceanBiome.baseWeatherCommonalities.Count == 0)
            {
                AddWeather(oceanBiome, "Clear", 18f);
                AddWeather(oceanBiome, "Fog", 1f);
                AddWeather(oceanBiome, "Rain", 2f);
                AddWeather(oceanBiome, "DryThunderstorm", 1f);
                AddWeather(oceanBiome, "RainyThunderstorm", 1f);
                AddWeather(oceanBiome, "FoggyRain", 1f);
                weatherAdded = true;
            }

            RimExodusLog.Message(RimExodusLogModule.Generation,
                $"Ocean map support initialized: terrainAdded={terrainAdded} terrainEntries={oceanBiome.terrainsByFertility.Count} " +
                $"weatherAdded={weatherAdded} weatherEntries={oceanBiome.baseWeatherCommonalities.Count}.");
        }

        /// <summary>本次会话是否由 RimExodus 接管这张合法表面 Ocean 地图的缺省生成行为。</summary>
        internal static bool Handles(Map map)
        {
            return EnabledForSession
                && map != null
                && SeamlessTileRegistry.GetMapWorldTile(map) >= 0
                // 不依赖 BiomeDefOf 初始化时序；正常路径引用相等，defName 是纯防御 fallback。
                && (map.TileInfo.PrimaryBiome == oceanBiome || map.TileInfo.PrimaryBiome?.defName == "Ocean");
        }

        private static void AddWeather(BiomeDef biome, string defName, float commonality)
        {
            var weather = DefDatabase<WeatherDef>.GetNamedSilentFail(defName);
            if (weather == null)
            {
                Log.Warning($"[RimExodus] Ocean map support could not find WeatherDef {defName}; skipping it.");
                return;
            }

            biome.baseWeatherCommonalities.Add(new WeatherCommonalityRecord
            {
                weather = weather,
                commonality = commonality
            });
        }
    }
}
