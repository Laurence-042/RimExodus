using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 精简快照重生成器（2026-08-31）：生成邻接地图前，若**源图**内存里没有基础三层快照
    /// （旧存档读档 / <c>serializeBaseSnapshots</c> 关闭后读档的准确特征），在一张临时 Map 上
    /// 同步重跑 order &lt; <see cref="SnapshotStepOrderCutoff"/> 的 genStep 子集（恰为基础快照
    /// 在 389 备份时点的地形/岩体/屋顶状态），得到精简快照后回填邻图。这样邻图当前已为
    /// void 的映射格仍能提供裁切前的自然地形，同时带内玩家修改继续直接取当前实况。
    ///
    /// 【口径与先例】
    /// - genStep 组装 / 种子派生 / RockNoises / GL shim / per-step Rand+ProgramState 包裹全部
    ///   复刻 <see cref="IncrementalMapGenerator.Start"/> / <c>RunOneGenStep</c>——种子
    ///   = HashCombine(世界种子, tile) 且 per-step seedPart 计数只依赖同 SeedPart 的**前序**步，
    ///   子集只删 order ≥ 389 的后续步，地形结果与原图生成一致（确定性复现）。
    /// - 临时 Map 在同步段内临时加入 <c>Find.Maps</c>（<c>Thing.SpawnSetup</c> 的存在性检查是
    ///   无条件的 <c>Find.Maps.IndexOf(map) &lt; 0</c>，RocksFromGrid 的 GenSpawn 在非登记图上
    ///   必失败）：加/移都在同一次 tick 回调内完成，不会被 tick/渲染/存档看见；移除 =
    ///   纯 <c>Find.Maps.Remove</c>（勿走 DeinitAndRemoveMap：MapDrawer.Dispose 在 sections
    ///   未生成时 NRE，且会把 tempMap 的 parent 误当被移除图的 parent 通知）；也不跑
    ///   AddStartingAreas/StartInitialWeather/FinalizeInit。
    /// - 互斥：全程持有 <see cref="MapGenerator.mapBeingGenerated"/>（与原生 GenerateMap 同款
    ///   单飞语义）；入口若已有分帧增量/预览在飞则本次跳过（不阻塞邻图生成本身，混合退回
    ///   既有静默跳过，下次生成再补）。
    /// - 只回填完整基础快照，不保存派生条带子集；接缝提供器按格决定读当前实况还是基础快照。
    /// </summary>
    internal static class SeamlessSnapshotRegenerator
    {
        /// <summary>genStep 子集上界（不含）：389 = 基础快照备份时点，子集 = 备份时点的地形/岩体/屋顶。</summary>
        private const float SnapshotStepOrderCutoff = 389f;

        private static readonly FieldInfo gravshipField = AccessTools.Field(typeof(MapGenerator), "gravship");

        /// <summary>
        /// 生成邻接地图前调用：参考图缺内存基础快照时同步精简重生成（幂等；已有快照零成本早退）。
        /// 任何失败只出警告，绝不抛出——该图非 void 当前实况仍可参考，缺失的 void 侧格跳过。
        /// </summary>
        internal static void EnsureSourceSnapshot(Map sourceMap, int worldTile)
        {
            try
            {
                if (sourceMap == null || worldTile < 0) return;
                if (!(RimExodusMod.Settings?.regenerateMissingSnapshots ?? true)) return;
                // 三层必须成套可用；旧档/异常数据只有 terrain 时也要补，不能把缺失的岩体/屋顶层
                // 误当成“明确为空”。
                if (SeamlessMapData.GetBaseTerrainSnapshot(sourceMap) != null
                    && SeamlessMapData.GetBaseBuildingSnapshot(sourceMap) != null
                    && SeamlessMapData.GetBaseRoofSnapshot(sourceMap) != null)
                    return;
                // 互斥避让：精简生成消费 MapGenerator 进程级 static（data/RockNoises/mapBeingGenerated），
                // 与分帧增量 / 原生生成 / MapPreview 预览线程互踩。跳过本次（不重试不阻塞）——
                // 邻图生成本身照常，混合该方向退回既有静默跳过，下次触发生成时再补。
                if (MapGenerator.mapBeingGenerated != null || IncrementalMapGenerator.IsAnyGenerating
                    || SeamlessMapPreviewCompat.IsPreviewInFlight)
                {
                    return;
                }
                Regenerate(sourceMap, worldTile);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Snapshot regeneration failed for map {sourceMap?.uniqueID} (wt={worldTile}); current non-void cells remain usable: {ex}");
            }
        }

        /// <summary>
        /// 在目标图正式占用 MapGenerator 静态状态前，为它周围所有已加载地图补齐基础快照。
        /// 接缝单格通常只命中一个邻图、顶点处命中两个，但整张目标图可能沿不同边参考多张图，
        /// 因此不能只补触发生成的源图。无 Map 的封存/占位 WorldObject 不参与参考。
        /// </summary>
        internal static void EnsureNeighborSnapshots(PlanetTile targetTile)
        {
            if (!targetTile.Valid || targetTile.LayerDef != PlanetLayerDefOf.Surface) return;
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return;

            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(targetTile, neighbors);
            foreach (var neighbor in neighbors)
            {
                Map live = null;
                foreach (var candidate in Find.Maps)
                {
                    if (candidate == null || candidate.Disposed) continue;
                    if (SeamlessTileRegistry.GetMapWorldTile(candidate) == neighbor.tileId)
                    {
                        live = candidate;
                        break;
                    }
                }
                if (live != null) EnsureSourceSnapshot(live, neighbor.tileId);
            }
        }

        private static void Regenerate(Map sourceMap, int worldTile)
        {
            var startRealtime = UnityEngine.Time.realtimeSinceStartup;
            var parent = sourceMap.Parent;
            if (parent == null || parent.Tile.Valid == false) return;
            var planetTile = parent.Tile;
            // 源图生成时用的 MapGeneratorDef（Map.generatorDef 在生成时记录）——地形/岩体步骤
            // biome 驱动、跨 def 一致；def 差异步骤（Settlement 等）均在 389 之后被 cutoff 排除。
            // 缺失（异常态）回落 Base_Player（地块图原生链）。
            var def = sourceMap.generatorDef
                ?? DefDatabase<MapGeneratorDef>.GetNamedSilentFail("Base_Player");
            if (def == null)
            {
                Log.Warning("[RimExodus] Snapshot regeneration skipped: no MapGeneratorDef.");
                return;
            }

            int seed = Gen.HashCombineInt(Find.World.info.Seed, planetTile.GetHashCode());
            Map tempMap = null;
            // 进度提示（单帧冻结内 OnGUI 不重绘，本标签在入队帧已点亮、此处换成具体文案）
            MapGenerationProgressUI.BeginSyncOp("RimExodus_SnapshotRegenProgress".Translate());
            Rand.PushState();
            try
            {
                Rand.Seed = seed;
                // ===== 临时 Map（复刻 IncrementalMapGenerator.Start 的准备段，但不 AddMap）=====
                tempMap = new Map();
                tempMap.uniqueID = Find.UniqueIDsManager.GetNextMapID();
                tempMap.generationTick = GenTicks.TicksGame;
                tempMap.events = new MapEvents(tempMap);
                MapGenerator.mapBeingGenerated = tempMap; // 单飞持有（与原生 GenerateMap 同语义）
                tempMap.info.Size = new IntVec3(sourceMap.Size.x, 1, sourceMap.Size.z);
                tempMap.info.parent = parent; // TileInfo 读真实 WorldGrid（biome/mutators 等）
                tempMap.generatorDef = def;
                tempMap.ConstructComponents();
                foreach (var mutator in tempMap.TileInfo.Mutators)
                {
                    mutator.Worker?.Init(tempMap);
                }
                // 加入 Find.Maps（同步段内加、同步段内移，不跨帧不跨回调，不会被 tick/渲染/存档看见）：
                // Thing.SpawnSetup 的地图存在性检查是无条件的 Find.Maps.IndexOf(map) < 0（无 ProgramState
                // 逃生口），RocksFromGrid 的 GenSpawn 在非登记图上必红字 + 后续 NRE。移除走
                // DeinitAndRemoveMap（含 mapIndices 补偿，CleanupFailedGeneration 同款先例）。
                Current.Game.AddMap(tempMap);

                // GL shim（复刻 Start 的三步）：原生成带 landform 时漏 shim 快照就错。
                SeamlessLandformsCompat.TryPrepare(tempMap);
                SeamlessLandformsCompat.InitBiomeGrid(tempMap);
                var landformExtraSteps = SeamlessLandformsCompat.GetExtraGenSteps(tempMap);

                // ===== 组装 genStep 子集（复刻 Start L204-222 的组装，再按 order < 389 过滤）=====
                var enumerable = def.genSteps
                    .Where(IsValidBiomeGenStep).Select(d => new GenStepWithParams(d, default));
                foreach (var mutator in tempMap.TileInfo.Mutators)
                    if (mutator.extraGenSteps.Any())
                        enumerable = enumerable.Concat(mutator.extraGenSteps.Select(d => new GenStepWithParams(d, default)));
                if (tempMap.Biome.extraGenSteps.Any())
                    enumerable = enumerable.Concat(tempMap.Biome.extraGenSteps.Where(IsValidBiomeGenStep)
                        .Select(d => new GenStepWithParams(d, default)));
                if (tempMap.Biome.preventGenSteps.Any())
                    enumerable = enumerable.Where(s => !tempMap.Biome.preventGenSteps.Contains(s.def));
                foreach (var mut in tempMap.TileInfo.Mutators)
                    if (mut.preventGenSteps.Any())
                        enumerable = enumerable.Where(s => !mut.preventGenSteps.Contains(s.def));
                if (landformExtraSteps != null)
                    enumerable = enumerable.Concat(landformExtraSteps);
                // 原版先在完整链上执行 GenStepDef.preventsGenSteps，再运行排序结果。不能先按 <389
                // 截断：order 较高的声明者也可能排除一个较早步骤，若忽略会让重建快照偏离原图。
                var orderedSteps = enumerable.Distinct()
                    .OrderBy(x => x.def.order).ThenBy(x => x.def.index).ToList();
                orderedSteps.RemoveAll(step => orderedSteps.Any(other =>
                    other.def.preventsGenSteps != null && other.def.preventsGenSteps.Contains(step.def)));
                orderedSteps.RemoveAll(step => step.def.order >= SnapshotStepOrderCutoff);

                Rand.PushState();
                try
                {
                    Rand.Seed = seed;
                    RockNoises.Init(tempMap);
                }
                finally
                {
                    Rand.PopState();
                }

                // ===== 同步执行子集（复刻 RunOneGenStep：per-step Rand 种子 + ProgramState 包裹）=====
                for (var i = 0; i < orderedSteps.Count; i++)
                {
                    SeamlessLandformsCompat.EnsureContextAlive(tempMap);
                    var step = orderedSteps[i];
                    var savedState = Current.ProgramState;
                    Current.ProgramState = ProgramState.MapInitializing;
                    Rand.PushState();
                    try
                    {
                        Rand.Seed = Gen.HashCombineInt(seed, GetSeedPartFor(orderedSteps, i));
                        step.def.genStep.Generate(tempMap, step.parms);
                        if (tempMap.pathing.IncrementalDirtyingDisabled)
                        {
                            Log.Error($"[RimExodus] Snapshot regen genstep {step.def} ended with path incremental dirtying disabled.");
                            tempMap.pathing.ReEnableIncrementalDirtying();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                        Current.ProgramState = savedState;
                    }
                }

                // ===== 提取三层 → 回填源图 =====
                SeamlessMapData.SetBaseSnapshots(sourceMap,
                    (TerrainDef[])tempMap.terrainGrid.topGrid.Clone(),
                    SeamlessTerrainFill.BackupBuildingSnapshot(tempMap),
                    SeamlessTerrainFill.BackupRoofSnapshot(tempMap));

                if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                {
                    Log.Message($"[RimExodus] Snapshot regenerated for map={sourceMap.uniqueID}(wt={worldTile}): " +
                                $"genSteps={orderedSteps.Count} elapsed={(UnityEngine.Time.realtimeSinceStartup - startRealtime) * 1000f:F0}ms.");
                }
            }
            finally
            {
                // 移除 + 销毁（对齐 MapPreview 的 DisposeMap 卫生口径，其源码在
                // references/MapPreview/.../MapPreviewGenerator.cs——注意它并非"非登记图上 spawn"的
                // 先例：其 Patch_Verse_GenSpawn 把预览图上一切 Spawn 整体拦掉，岩石色从 Elevation
                // 网格算出；我们要真实岩体数据必须真 spawn，故走临时 AddMap，销毁卫生照抄它）：
                // 全局注册表摘除（tick 表/sustainer——岩石 TickerType.Never 理论不注册，但 GL 等
                // mod 的 <389 步骤可能 spawn 带 tick 的东西，不摘则全局表持死图引用）→ 共享资源归还
                // （GlowGrid 光照池是静态池，实例 Dispose 才归还）→ Find.Maps.Remove。逐项容忍失败。
                try
                {
                    if (tempMap != null) DisposeTempMap(tempMap);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] Snapshot regen temp map disposal error (map may leak): {ex}");
                }
                MapGenerator.mapBeingGenerated = null;
                gravshipField?.SetValue(null, null);
                RockNoises.Reset();
                SeamlessLandformsCompat.Cleanup();
                IncrementalMapGenerator.ClearWorkingDataStatic();
                Rand.PopState();
                MapGenerationProgressUI.EndSyncOp(); // 无条件（勿放进 DisposeTempMap 的局部 try/catch）
            }
        }

        /// <summary>
        /// 临时图销毁（MapPreview 的 MapPreviewGenerator.DisposeMap 同款卫生，逐项 null 守卫 +
        /// try/catch）：全局注册表摘除 → 组件 Dispose → Find.Maps.Remove。刻意不走
        /// DeinitAndRemoveMap（MapDrawer.Dispose 在 sections 未生成时 NRE，且会把 tempMap 借用的
        /// 源图 parent 误当被移除图的 parent 通知——2026-08-31 实测教训）。
        /// </summary>
        private static void DisposeTempMap(Map map)
        {
            try { Find.SoundRoot?.sustainerManager?.EndAllInMap(map); } catch { }
            try { Find.TickManager?.RemoveAllFromMap(map); } catch { }
            try { map.regionAndRoomUpdater.Enabled = false; } catch { }
            try { map.fogGrid?.Dispose(); } catch { }
            try { map.snowGrid?.Dispose(); } catch { }
            try { map.pathFinder?.Dispose(); } catch { }
            try { map.lordManager?.Dispose(); } catch { }
            try { map.glowGrid?.Dispose(); } catch { }
            try { map.sandGrid?.Dispose(); } catch { }
            try { map.avoidGrid?.Dispose(); } catch { }
            try { map.listerBuildings?.Dispose(); } catch { }
            try { map.listerThings?.Clear(); } catch { }
                try { map.regionGrid?.Dispose(); } catch { }
                try { map.pathing?.Dispose(); } catch { }
                // mapDrawer：sections 是私有字段判不了 null，但临时图从未跑 mesh 重建/渲染，
                // sections 恒 null（MapDrawer.Dispose 对其 NRE = 上轮报错根因），直接跳过不 Dispose。
                Find.Maps.Remove(map);
            }

        /// <summary>复刻 IncrementalMapGenerator.GetSeedPartFor（内联）：SeedPart + 前面同 SeedPart 步的数量。</summary>
        private static int GetSeedPartFor(List<GenStepWithParams> steps, int index)
        {
            var seedPart = steps[index].def.genStep.SeedPart;
            int count = 0;
            for (int i = 0; i < index; i++)
            {
                if (steps[i].def.genStep.SeedPart == seedPart) count++;
            }
            return seedPart + count;
        }

        /// <summary>复刻 MapGenerator.IsValidBiome（过滤 Scenario 的 ScenPart_DisableMapGen 禁用步骤）。</summary>
        private static bool IsValidBiomeGenStep(GenStepDef def)
        {
            return !Find.Scenario.AllParts.Any(p =>
                typeof(ScenPart_DisableMapGen).IsAssignableFrom(p.def.scenPartClass) && p.def.genStep == def);
        }
    }

    /// <summary>
    /// 原版同步生成的统一预备入口。必须在 GenerateMap 方法体翻转 ProgramState、占用
    /// mapBeingGenerated 之前运行；覆盖营地、POI、任务图和第三方直接调用。分帧生成不经过该方法，
    /// 由 IncrementalMapGenerator.Start 显式调用同一准备函数。
    /// </summary>
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class Patch_MapGenerator_GenerateMap_PrepareSeamReferences
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(MapParent parent, bool isPocketMap)
        {
            // 同时看显式参数与 parent 类型：第三方若漏传 isPocketMap，口袋图仍不得进入表面参考链。
            if (isPocketMap || parent == null || parent is PocketMapParent) return;
            try
            {
                SeamlessSnapshotRegenerator.EnsureNeighborSnapshots(parent.Tile);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimExodus] Preparing neighbor terrain snapshots failed; map generation continues with available references: {ex}");
            }
        }
    }
}
