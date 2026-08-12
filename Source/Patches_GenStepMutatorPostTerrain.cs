using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Postfix GenStep_MutatorPostTerrain.Generate：锚点家园地图（Settlement）的 void 铺设。
    ///
    /// **背景**：锚点家园地图走原生 <c>Base_Player</c> MapGeneratorDef，genStep 链里没有 RimExodus 的
    /// void 铺设步骤（<c>RimExodus_SeamlessTile</c> genStep 只注册在 <c>RimExodus_SeamlessTileGenerator</c>，
    /// 仅服务邻接地块 <see cref="MapParent_SeamlessTile"/>）。此前锚点 void 靠 MapGenerated 后延迟 1 tick 的
    /// <c>TrySetupOnStart → RefreshMapVoid → ApplyPolygonTerrain</c> 后补，此时地图已完全生成（岩石/植物/玩家
    /// 建造都在），<c>ClearThingsOnCells</c> 真实删除实体 → 删岩壁触发落石、删建筑切断玩家建造。
    ///
    /// 本 Postfix 把锚点 void 铺设前移到 genStep 阶段，与邻接地块（<c>RimExodus_SeamlessTile</c> order=211）
    /// 完全对称：在 <c>GenStep_MutatorPostTerrain</c>（order=220，Terrain(210) + 所有 post-terrain mutator 之后、
    /// Plants(900)/RockChunks(970)/Animals(1200) 之前）正常返回后立即铺 void。后续 spawn 类 genStep 读 void
    /// Impassable 自然不在 void 格 spawn，<c>ClearThingsOnCells</c> 变成空操作（岩石已被
    /// <see cref="Patch_GenStep_RocksFromGrid"/> Postfix 提前清掉）。
    ///
    /// **为何选择 MutatorPostTerrain 而非 Terrain**：<c>SeamlessTileGenerator.xml</c> 有历史教训注释——
    /// Postfix <c>GenStep_Terrain.Generate</c> 会因原方法内部 WaterBodyTracker NRE 被跳过。
    /// <c>GenStep_MutatorPostTerrain.Generate</c> 方法体极简（仅遍历 mutators 调 GeneratePostTerrain），
    /// 无内部 NRE 风险，Postfix 稳定执行。
    ///
    /// **为何不 patch RimExodus_SeamlessTile genStep 本身**：那个 genStep 注册在 RimExodus_SeamlessTileGenerator，
    /// 锚点走 Base_Player 根本不跑它。要让它对锚点生效得改 Base_Player XML（污染全局 + 场景覆盖）。
    /// Harmony Postfix 一个所有地图都跑的原版 genStep（MutatorPostTerrain 在 MapCommonBase 里，所有 MapGeneratorDef 继承）更干净。
    /// </summary>
    [HarmonyPatch(typeof(GenStep_MutatorPostTerrain), nameof(GenStep_MutatorPostTerrain.Generate))]
    static class Patch_GenStep_MutatorPostTerrain
    {
        static void Postfix(Map map)
        {
            // 仅锚点家园地图（Settlement，非 MapParent_SeamlessTile）。
            // 邻接地块走自己的 RimExodus_SeamlessTile genStep（order=211），不在此处理。
            if (!SeamlessTileGraph.IsAnchorMap(map)) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            // 备份基础地形（void 裁切前的完整矩形 topGrid）+ 按六边形挖虚空。
            // 与 GenStep_SeamlessTile.Generate（邻接地块路径）共用同一逻辑体（归一），
            // 差异仅在入口（本 Postfix vs genStep），避免两份重复逻辑漂移。
            SeamlessTerrainFill.BackupSnapshotAndApplyVoid(map, worldTile);
        }
    }
}
