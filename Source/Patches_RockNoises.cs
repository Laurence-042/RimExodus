using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 修复共因 bug：口袋地图的 <see cref="Map.Tile"/> 固定为 0（阶段4a 调研确认这是口袋地图惯例，
    /// VMF 也设 Tile=0，不能改为真实 worldTile 否则破坏口袋地图语义、触发世界系统把它当真实地块处理）。
    ///
    /// 后果：<see cref="RockNoises.Init"/> 用 <c>map.Tile</c> 调
    /// <see cref="World.NaturalRockTypesIn(PlanetTile)"/>（以 tile.GetHashCode() 为 seed 选岩石类型集合），
    /// 所有口袋地图都读到 tile 0 的岩石类型集合——无论邻居地块真实 biome 如何，岩石类型永远相同。
    ///
    /// 修复：Prefix 拦截 <see cref="RockNoises.Init"/>，对 RimExodus 口袋地图（<see cref="MapParent_SeamlessTile"/>）
    /// 改用 <c>MapParent_SeamlessTile.worldTile</c> 查岩石类型。原方法的 Perlin seed（Rand.Range）不动
    /// （属连续地形阶段的岩石连续化范围，本轮只修"岩石类型读对 tile"）。
    /// </summary>
    [HarmonyPatch(typeof(RockNoises), nameof(RockNoises.Init))]
    static class Patch_RockNoises_Init
    {
        static bool Prefix(Map map)
        {
            // 非 RimExodus 口袋地图放行原方法。
            if (!(map.Parent is MapParent_SeamlessTile seamlessParent)) return true;
            if (seamlessParent.worldTile < 0) return true;

            var realTile = new PlanetTile(seamlessParent.worldTile);

            // 复制 RockNoises.Init 原逻辑，仅把 NaturalRockTypesIn 的入参从 map.Tile 换成 realTile。
            // 其余（Perlin 构造、Rand seed、NoiseDebugUI）保持原样——Rand 仍由 genStep 的 Rand.Seed 驱动。
            RockNoises.rockNoises = new List<RockNoises.RockNoise>();
            foreach (var rockDef in Find.World.NaturalRockTypesIn(realTile))
            {
                var rockNoise = new RockNoises.RockNoise
                {
                    rockDef = rockDef,
                    noise = new Perlin(0.004999999888241291f, 2.0, 0.5, 6, Rand.Range(0, int.MaxValue), QualityMode.Medium)
                };
                RockNoises.rockNoises.Add(rockNoise);
                NoiseDebugUI.StoreNoiseRender(rockNoise.noise, rockNoise.rockDef?.ToString() + " score", map.Size.ToIntVec2);
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] RockNoises.Init patched for pocket map {map.uniqueID} " +
                    $"(worldTile={seamlessParent.worldTile}, rockTypes={RockNoises.rockNoises.Count}).");

            return false; // 跳过原方法。
        }
    }
}
