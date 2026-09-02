using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// "设立营地"生成 <see cref="MapParent_SeamlessTile"/> 而非原版 Camp（2026-08，勿回退）。
    ///
    /// 根因：原版 <see cref="Camp"/>.ShouldRemoveMapNow 在地图无 pawn 阻挡时返回 true，而
    /// MapParent.TickInterval 每 tick CheckRemoveMapNow——全员跨缝走进邻图的瞬间营地被原版
    /// 自动解散（DeinitAndRemoveMap + Destroy + 原地留 AbandonedCamp 标记），完全绕过 governor
    /// 距离策略（实测"1 跳邻图上的营地 C 被删除"即此，日志无 Dormancy DELETE 行）。已查证
    /// Camp WorldObject 的唯一创建路径就是玩家"设立营地"（SettleInEmptyTileUtility.SetupCamp →
    /// GetOrGenerateMap），换 def 不影响任务/NPC 流程。
    ///
    /// 换成 RimExodus_SeamlessTileMap 后：生命周期归 governor（≥2 跳休眠 / ≥3 跳删除，与其他
    /// 地块图一致——"所有地图一视同仁"）；基类 MapParent.ShouldRemoveMapNow 默认 false，不再被
    /// 原版自动解散；"立即重组远行队"路径的 CheckRemoveMapNow 变 no-op。营地走 Base_Player
    /// genStep 链（含注入的 389/392/1490），与家园/地块同链。
    ///
    /// 语义变化（用户原则下接受，见 doc/地图滚动休眠.md）：营地不再自动解散、不再留 AbandonedCamp
    /// 标记；失去 Camp def 的 TimedDetectionRaids / Map_TempIncident（与其他地块图对等）。
    /// </summary>
    [HarmonyPatch(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap),
        new[] { typeof(PlanetTile), typeof(IntVec3), typeof(WorldObjectDef), typeof(IEnumerable<GenStepWithParams>), typeof(bool) })]
    static class Patch_GetOrGenerateMap_CampAsTileMap
    {
        static bool Prefix(PlanetTile tile, WorldObjectDef suggestedMapParentDef,
            IEnumerable<GenStepWithParams> extraGenStepDefs, bool stepDebugger, ref Map __result)
        {
            if (suggestedMapParentDef != WorldObjectDefOf.Camp) return true; // 只接管营地；任务 site 等其他 def 不碰。
            if (!tile.Valid) return true; // 无效 tile 交还原版（pocket 路径等）。

            // 防御放行：已有图（含休眠图——软休眠仍在 Find.Maps）或已有 parent 时原版走复用/报错路径。
            if (Current.Game.FindMap(tile) != null || Find.WorldObjects.MapParentAt(tile) != null) return true;
            // 增量生成进行中：同步 GenerateMap 与分帧生成共用 MapGenerator static（已知交错观察项），
            // 不冒险接管，退回原版 Camp 行为。
            if (MapGenerator.mapBeingGenerated != null) return true;

            var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("RimExodus_SeamlessTileMap");
            if (def == null) return true;

            var parent = (MapParent_SeamlessTile)WorldObjectMaker.MakeWorldObject(def);
            // worldTile 必须在生成前设置（勿改）：389/392/1490 genStep 以 GetMapWorldTile >= 0 为门，
            // 晚设则整条无缝链（void 裁切 / 接缝混合 / 传送点）被守卫跳过。
            parent.worldTile = tile.tileId;
            parent.Tile = tile;
            Find.WorldObjects.Add(parent);

            // 尺寸与家园/地块网格一致（弃 Camp def 的 overrideMapSize=200）。同步生成在营地的
            // LongEventHandler 里本就是原版行为；GenerateMap 自带 static 重置与 FinalizeInit。
            var map = MapGenerator.GenerateMap(Find.World.info.initialMapSize, parent, parent.MapGeneratorDef,
                parent.ExtraGenStepDefs.ConcatIfNotNull(extraGenStepDefs), null, isPocketMap: false, stepDebugger);

            // 生成后集成（对齐 GenerateTileMap 的 onComplete 子集；无源图，跳过源端邻居登记/补铺）。
            SeamlessWeatherClusterManager.BindMap(map);
            SeamlessEnterSpotPlacer.PlaceEnterSpotsAllNeighbors(map, tile.tileId); // 1490 已铺，幂等防御
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(map);
            SeamlessTileManager.AutoConnectWorldNeighbors(map, tile.tileId); // 紧邻已加载图时自动接线

            if (RimExodusLog.Enabled(RimExodusLogModule.Generation)) Log.Message($"[RimExodus] Camp at tile {tile.tileId} generated as seamless tile map {map.uniqueID} " +
                        "(vanilla Camp auto-dissolution bypassed; lifecycle now governed by dormancy governor).");
            __result = map;
            return false;
        }
    }
}
