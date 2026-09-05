using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Vanilla Expanded Framework（VEF，packageId = OskarPotocki.VanillaFactionsExpanded.Core——
    /// 历史遗留名，实际就是 VEF 本体）的 ObjectSpawns 地图物体刷出位置过滤（2026-09）。
    ///
    /// 【问题】VEF 的 <c>DoMapSpawns</c>（挂 MapGenerator.GenerateMap 的 Harmony Postfix）按
    /// <c>map.AllCells.InRandomOrder()</c> 逐格过 <c>CanSpawnAt(IntVec3, Map, ObjectSpawnsDef)</c>，
    /// 判据只有"格上无非 Filth thing + 地形过滤"，无 standability 检查、不知道六边形裁切。
    /// RimExodus 图方形边缘的 void 环（约 40-45% 格数）被 genStep 389 清空且 RimExodus_Void 无
    /// Water tag → 它是全图最大"合格空格池"（植被/岩石群系里核心区几乎全被 thing 占据）→ VVE
    /// Tier 3 载具残骸等物体系统性落进 void：不可达不可修，且该世界区域从邻图视角属于邻图自己
    /// 的方形，过缝后不再被渲染（玩家报告"spawn at the very edge / disappear on crossing"）。
    ///
    /// 【修复】Postfix 挂 VEF 的格级 CanSpawnAt，对 RimExodus 表面地块图把 void 格（六边形外）
    /// 与接缝带 B 三圈判为不可刷（用户定夺 2026-09：与禁建带同语义——传送圈保持干净、VF 载具
    /// 落点矩形不受挤压）。几何用 <see cref="SeamlessPolygonGeometry"/>（与 389 铺 void 同口径，
    /// 生成完成后恒定）。单点收口覆盖全部触发路径：初始家园图 / POI 原生 / 同步逃生 / 营地
    /// （VEF 自有 Postfix 原生生效）+ 增量分帧（经 GenerateMapPostfixReplay 复放 VEF 的 Postfix，
    /// 其内部再调本 patch 过的 CanSpawnAt）。一切 ObjectSpawnsDef 用户（VVE 残骸、Dark Ages
    /// 巢穴、Mythic Ages 等）一并修正。
    ///
    /// 【纪律】手动绑定不在离线验证器覆盖内——回归时盯启动日志确认行
    /// "VEF compat: bound ObjectSpawns cell filter"。软检测未装短路；绑定/漂移全程 try/catch
    /// 降级 Warning，绝不杀死 mod（VF/PS/GL compat 同族）。
    /// </summary>
    internal static class SeamlessVEFCompat
    {
        /// <summary>VEF 实际 packageId（历史名 VanillaFactionsExpanded.Core）。</summary>
        private const string VefPackageId = "oskarpotocki.vanillafactionsexpanded.core";

        /// <summary>per-map 屏蔽表缓存（void ∪ 接缝带 B 的格索引布尔表）。图生命周期内几何恒定。</summary>
        private static readonly ConditionalWeakTable<Map, bool[]> BlockedCells = new();

        /// <summary>几何故障一次性告警旗标（fail-open 后不再刷屏）。</summary>
        private static bool warnedFilterFailure;

        public static void Register(Harmony harmony)
        {
            try
            {
                var vefLoaded = false;
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    var id = mod.PackageIdPlayerFacing;
                    if (id != null && id.ToLower() == VefPackageId) { vefLoaded = true; break; }
                }
                if (!vefLoaded) return;

                var defType = AccessTools.TypeByName("VEF.Maps.ObjectSpawnsDef");
                var patchType = AccessTools.TypeByName("VEF.Maps.VanillaExpandedFramework_MapGenerator_GenerateMap_Patch");
                if (defType == null || patchType == null)
                {
                    Log.Warning($"[RimExodus] VEF compat: VEF is loaded but types not found "
                        + $"(ObjectSpawnsDef={defType != null}, patchClass={patchType != null}) — ObjectSpawns cell filter disabled.");
                    return;
                }

                // 两个 CanSpawnAt 重载（Map 级 / 格级），必须带类型数组消歧。
                var canSpawnAtCell = AccessTools.Method(patchType, "CanSpawnAt",
                    new[] { typeof(IntVec3), typeof(Map), defType });
                if (canSpawnAtCell == null)
                {
                    Log.Warning("[RimExodus] VEF compat: CanSpawnAt(IntVec3, Map, ObjectSpawnsDef) not found "
                        + "(VEF signature drift?) — ObjectSpawns cell filter disabled.");
                    return;
                }

                var postfix = new HarmonyMethod(AccessTools.Method(typeof(SeamlessVEFCompat), nameof(CanSpawnAtCellPostfix)));
                harmony.Patch(canSpawnAtCell, postfix: postfix);
                Log.Message("[RimExodus] VEF compat: bound ObjectSpawns cell filter (CanSpawnAt postfix).");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] VEF compat: binding failed (mod continues, VEF object spawns stay unfiltered): {ex}");
            }
        }

        /// <summary>
        /// 位置参数绑定（__0 = IntVec3，__1 = Map——同 VF Caravan.AddPawn patch 先例）。
        /// 只收紧不放松：<c>!__result</c> 早退；非 RimExodus 表面图（口袋/空间图 GetMapWorldTile&lt;0，
        /// 铁律：它们本就不在无缝语义内）与非地块图放行原判。
        /// </summary>
        private static void CanSpawnAtCellPostfix(ref bool __result, IntVec3 __0, Map __1)
        {
            if (!__result || __1 == null) return;
            try
            {
                var worldTile = SeamlessTileRegistry.GetMapWorldTile(__1);
                if (worldTile < 0) return;

                var blocked = GetOrBuildBlockedCells(__1, worldTile);
                if (blocked != null && blocked[__1.cellIndices.CellToIndex(__0)])
                    __result = false;
            }
            catch (Exception ex)
            {
                // fail-open：几何异常时放行原判（最坏 = 未过滤，回到旧行为），只告警一次。
                if (warnedFilterFailure) return;
                warnedFilterFailure = true;
                Log.Warning($"[RimExodus] VEF compat: cell filter geometry failed (fail-open from now on): {ex}");
            }
        }

        /// <summary>
        /// 惰性构建 per-map 屏蔽布尔表：接缝带 B 三圈 ∪ 全部 void 格（IsVoidCell 与 389 铺 void 同
        /// 口径的纯几何）。DoMapSpawns 逐格逐 def 高频调用，表建一次后每次查询 = 一次数组读。
        /// </summary>
        private static bool[] GetOrBuildBlockedCells(Map map, int worldTile)
        {
            if (BlockedCells.TryGetValue(map, out var cached)) return cached;

            var mapSize = map.Size.x;
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
            var blocked = new bool[mapSize * mapSize];
            var indices = map.cellIndices;
            foreach (var cell in map.AllCells)
            {
                if (band.Band.Contains(cell) || SeamlessPolygonGeometry.IsVoidCell(band, worldTile, mapSize, cell))
                    blocked[indices.CellToIndex(cell)] = true;
            }
            BlockedCells.Add(map, blocked);
            return blocked;
        }
    }
}
