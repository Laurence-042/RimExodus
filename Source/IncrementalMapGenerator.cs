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
    /// 【实验分支】分帧增量地图生成器。
    /// 把 MapGenerator.GenerateMap 的 genStep 链拆成 N 帧，主线程每帧跑 1+ genStep，
    /// 避免单帧长时间卡顿，同时不暂停 tick（玩家可继续操作，generating map 被 patch 跳过 tick/渲染）。
    ///
    /// 可行性依据（调研确认）：
    /// - genStep 间 Rand 状态独立：每个 genStep 开始时 Rand.Seed = baseSeed + GetSeedPart(index) 完全重置，
    ///   主帧 tick 改变 Rand 不影响下一个 genStep。
    /// - genStep 间共享数据通过 MapGenerator static（tmpGenSteps/data/mapBeingGenerated/RockNoises）保持，
    ///   分帧期间不 ClearWorkingData/RockNoises.Reset。
    /// - 原生 DebugDoNextGenStep（MapGenerator.cs:234-262）已证明跨帧逐 genStep 执行合法。
    /// - FinalizeInit 单帧执行（region rebuild 不能拆），保留最后一段卡顿。
    ///
    /// generating map 通过 patch Map.MapPreTick/MapPostTick/MapUpdate 早退，不被 tick/渲染。
    /// </summary>
    public class IncrementalMapGenerator : MapComponent
    {
        /// <summary>当前正在分帧生成的 generator（全局唯一，MapGenerator static 不支持并发生成）。</summary>
        private static IncrementalMapGenerator current;

        // ===== 分帧状态 =====
        private Map generatingMap;
        private List<GenStepWithParams> genSteps;
        private int currentStepIndex;
        private int baseSeed;
        private Action<Map> onComplete;
        private bool finalizing;

        // ===== 可分帧 genStep 状态（Plants 等重 genStep 跨帧执行）=====
        /// <summary>当前 genStep 是否正在进行中（跨帧），null=未在进行。</summary>
        private SubStepState subStepState;

        private class SubStepState
        {
            public int stepIndex;
            public int cellIndex; // 已处理的 cell 数（cellsInRandomOrder 索引）。
            public int totalCells;
            public float densityFactor;
            public float desiredPlants;
            public int randSeed; // 该 genStep 的 Rand.Seed（每帧恢复，保证可复现）。
        }

        // ===== 反射缓存（MapGenerator private static 访问）=====
        private static readonly FieldInfo tmpGenStepsField = AccessTools.Field(typeof(MapGenerator), "tmpGenSteps");
        private static readonly MethodInfo getSeedPartMethod = AccessTools.Method(typeof(MapGenerator), "GetSeedPart");
        private static readonly FieldInfo gravshipField = AccessTools.Field(typeof(MapGenerator), "gravship");
        private static readonly MethodInfo clearWorkingDataMethod = AccessTools.Method(typeof(MapGenerator), "ClearWorkingData");

        /// <summary>当前是否有分帧生成在进行（供外部判断是否清理防重入锁）。</summary>
        public static bool IsAnyGenerating => current != null;

        private static void ClearWorkingDataStatic()
        {
            clearWorkingDataMethod?.Invoke(null, null);
        }

        /// <summary>某个 map 是否正在分帧生成中（供 patch 判断是否早退）。</summary>
        public static bool IsGenerating(Map map)
        {
            return current != null && current.generatingMap == map && !current.finalizing;
        }

        public IncrementalMapGenerator(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            // 每帧推进 genStep（由任意 map 的 tick 触发，current 是全局唯一）。
            current?.TickGeneration();
        }

        /// <summary>
        /// 启动分帧生成。准备阶段（ConstructComponents→AddMap→组装 genSteps）同步完成，
        /// genStep 链分帧执行，FinalizeInit + onComplete 在最后帧执行。
        /// </summary>
        public static bool Start(MapParent mapParent, IntVec3 mapSize, MapGeneratorDef mapGeneratorDef,
            IEnumerable<GenStepWithParams> extraGenStepDefs, Action<Map> extraInitBeforeContentGen,
            Action<Map> onComplete)
        {
            if (current != null)
            {
                Log.Warning("[RimExodus] IncrementalMapGenerator: another generation in progress, reject.");
                return false;
            }
            if (MapGenerator.mapBeingGenerated != null)
            {
                Log.Warning("[RimExodus] IncrementalMapGenerator: MapGenerator.mapBeingGenerated != null, reject.");
                return false;
            }

            try
            {
                // ===== 准备阶段（复刻 MapGenerator.GenerateMap :82-185 的前半段）=====
                // 注意：不翻转 ProgramState（原版设 MapInitializing，但分帧期间主线程继续 tick，
                // 翻转会导致 20+ 处守卫逻辑错乱）。genStep 通常不读 ProgramState，保持 Playing 更安全。
                // 不保持外层 Rand.PushState 栈帧——Root.Update 每帧调 EnsureStateStackEmpty 会清空栈，
                // 跨帧保持的 PushState 会被清掉导致 FinishGeneration 的 PopState 弹空栈。
                // 每个 genStep 用 Rand.PushState/Seed/PopState 独立配对（RunOneGenStep），不依赖外层栈。
                int seed = Gen.HashCombineInt(Find.World.info.Seed, mapParent.ID);
                ClearWorkingDataStatic();
                // 准备阶段同步完成（不跨帧），PushState/Seed/PopState 配对安全：
                // Rand.Seed setter 要求 stateStack.Count > 0，PushState 保证；PopState 恢复原状态。
                Rand.PushState();
                try
                {
                    Rand.Seed = seed;
                }
                finally
                {
                    Rand.PopState();
                }

                var newMap = new Map();
                newMap.uniqueID = Find.UniqueIDsManager.GetNextMapID();
                newMap.generationTick = GenTicks.TicksGame;
                newMap.events = new MapEvents(newMap);

                MapGenerator.mapBeingGenerated = newMap;
                try
                {
                    newMap.info.Size = mapSize;
                    newMap.info.parent = mapParent;
                    newMap.generatorDef = mapGeneratorDef; // 关键：OutdoorTemp/Biome 等依赖此字段，原版 :126。
                    newMap.info.disableSunShadows = mapGeneratorDef.disableShadows;
                    if (mapGeneratorDef.pocketMapProperties != null)
                    {
                        newMap.info.isPocketMap = true;
                        newMap.pocketTileInfo = new Tile { PrimaryBiome = mapGeneratorDef.pocketMapProperties.biome };
                    }
                    newMap.ConstructComponents();
                    foreach (var mutator in newMap.TileInfo.Mutators)
                    {
                        mutator.Worker?.Init(newMap);
                    }

                    Current.Game.AddMap(newMap);

                    if (mapGeneratorDef.isUnderground)
                    {
                        foreach (var cell in newMap.AllCells)
                            newMap.roofGrid.SetRoof(cell, mapGeneratorDef.roofDef ?? RoofDefOf.RoofRockThick);
                    }

                    extraInitBeforeContentGen?.Invoke(newMap);

                    // 组装 genStep 列表（复刻 GenerateMap:155-182 + GenerateContentsIntoMap:291-312）。
                    var enumerable = mapGeneratorDef.genSteps
                        .Where(IsValidBiomeGenStep).Select(GetGenStepParmsFor);
                    foreach (var mutator in newMap.TileInfo.Mutators)
                        if (mutator.extraGenSteps.Any())
                            enumerable = enumerable.Concat(mutator.extraGenSteps.Select(GetGenStepParmsFor));
                    if (newMap.Biome.extraGenSteps.Any())
                        enumerable = enumerable.Concat(newMap.Biome.extraGenSteps.Where(IsValidBiomeGenStep).Select(GetGenStepParmsFor));
                    if (newMap.Biome.preventGenSteps.Any())
                        enumerable = enumerable.Where(s => !newMap.Biome.preventGenSteps.Contains(s.def));
                    foreach (var mut in newMap.TileInfo.Mutators)
                        if (mut.preventGenSteps.Any())
                            enumerable = enumerable.Where(s => !mut.preventGenSteps.Contains(s.def));
                    if (extraGenStepDefs != null)
                        enumerable = enumerable.Concat(extraGenStepDefs);
                    var orderedSteps = enumerable.Distinct()
                        .OrderBy(x => x.def.order).ThenBy(x => x.def.index).ToList();

                    // RockNoises.Init + 填充 MapGenerator.tmpGenSteps（供 GetSeedPart 反射用）。
                    Rand.PushState();
                    try
                    {
                        Rand.Seed = seed;
                        RockNoises.Init(newMap);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    var tmpGenSteps = (List<GenStepWithParams>)tmpGenStepsField.GetValue(null);
                    tmpGenSteps.Clear();
                    tmpGenSteps.AddRange(orderedSteps);
                    // gravship 标记（Odyssey）。
                    if (ModsConfig.OdysseyActive)
                    {
                        foreach (var gs in orderedSteps)
                        {
                            if (gs.def == GenStepDefOf.GravshipMarker)
                            {
                                gravshipField.SetValue(null, gs.parms.gravship);
                                break;
                            }
                        }
                    }

                    // ===== 注册分帧生成器 =====
                    // 用 newMap 的 MapComponent 承载（newMap 已 AddMap，会被 tick）。
                    var comp = newMap.GetComponent<IncrementalMapGenerator>();
                    comp.generatingMap = newMap;
                    comp.genSteps = orderedSteps;
                    comp.currentStepIndex = 0;
                    comp.baseSeed = seed;
                    comp.onComplete = onComplete;
                    current = comp;

                    newMap.areaManager.AddStartingAreas();
                    newMap.weatherDecider.StartInitialWeather();

                    if (RimExodusMod.Settings?.verboseLogging ?? false)
                        Log.Message($"[RimExodus] IncrementalMapGenerator started: map={newMap.uniqueID}, genSteps={orderedSteps.Count}, seed={seed}.");

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] IncrementalMapGenerator prepare failed: {ex}");
                    CleanupFailedGeneration(newMap);
                    return false;
                }
            }
            catch (Exception outerEx)
            {
                Log.Error($"[RimExodus] IncrementalMapGenerator outer failure: {outerEx}");
                return false;
            }
        }

        /// <summary>
        /// 每帧推进 genStep。用时间预算：每帧跑多个 genStep 直到累计时间接近 TimeBudgetMs。
        /// Plants 等重 genStep 支持跨帧执行（SubStepState），避免单帧卡顿。
        /// </summary>
        private const float TimeBudgetMs = 8f; // 每帧 genStep 时间预算（ms），留给 tick/渲染约 8ms。

        private void TickGeneration()
        {
            if (generatingMap == null || genSteps == null) return;

            try
            {
                var frameStart = UnityEngine.Time.realtimeSinceStartup;
                // 每帧循环跑 genStep，直到时间预算耗尽或全部完成。
                while (currentStepIndex < genSteps.Count)
                {
                    // 检查是否是可分帧的重 genStep（Plants），若是则跨帧执行。
                    if (IsSplittableGenStep(genSteps[currentStepIndex]) && subStepState == null)
                    {
                        StartSubStepState(currentStepIndex);
                    }

                    if (subStepState != null)
                    {
                        // 跨帧执行 Plants 的 cell 循环。
                        var done = RunSubStepChunk(frameStart);
                        if (!done) return; // 本帧预算耗尽，下一帧继续。
                        // Plants 全部完成。
                        subStepState = null;
                    }
                    else
                    {
                        RunOneGenStep();
                    }
                    currentStepIndex++;
                    // 检查时间预算。
                    var elapsedMs = (UnityEngine.Time.realtimeSinceStartup - frameStart) * 1000f;
                    if (elapsedMs >= TimeBudgetMs) break;
                }

                if (currentStepIndex < genSteps.Count) return; // 还有 genStep，下一帧继续。

                // 所有 genStep 完成，进入 finalize。
                FinishGeneration();
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] IncrementalMapGenerator TickGeneration failed at step {currentStepIndex}: {ex}");
                CleanupFailedGeneration(generatingMap);
                current = null;
            }
        }

        /// <summary>判断 genStep 是否可分帧（重 genStep 跨帧执行，避免单帧卡顿）。</summary>
        private static bool IsSplittableGenStep(GenStepWithParams step)
        {
            return step.def.defName == "Plants";
        }

        /// <summary>初始化可分帧 genStep 的状态。</summary>
        private void StartSubStepState(int stepIndex)
        {
            var spawner = generatingMap.wildPlantSpawner;
            subStepState = new SubStepState
            {
                stepIndex = stepIndex,
                cellIndex = 0,
                totalCells = generatingMap.cellIndices.NumGridCells,
                densityFactor = spawner.CurrentPlantDensityFactor,
                desiredPlants = spawner.CurrentWholeMapNumDesiredPlants,
                randSeed = Gen.HashCombineInt(baseSeed, GetSeedPartFor(stepIndex)),
            };
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Plants genStep: starting split-frame execution (totalCells={subStepState.totalCells}, density={subStepState.densityFactor}).");
        }

        /// <summary>
        /// 跨帧执行 Plants 的 cell 循环。每帧跑一批 cell 直到时间预算耗尽。
        /// 返回 true 表示全部完成，false 表示本帧未完成（下一帧继续）。
        /// 每 batch 用独立 Rand seed（stepSeed + batchIndex），保证跨帧 Rand 独立。
        /// </summary>
        private bool RunSubStepChunk(float frameStart)
        {
            var map = generatingMap;
            var spawner = map.wildPlantSpawner;
            var state = subStepState;
            const int batchSize = 2000; // 每批 cell 数（每批用独立 Rand seed）。

            while (state.cellIndex < state.totalCells)
            {
                // 时间预算检查。
                var elapsedMs = (UnityEngine.Time.realtimeSinceStartup - frameStart) * 1000f;
                if (elapsedMs >= TimeBudgetMs) return false;

                // 计算本批范围。
                var batchEnd = System.Math.Min(state.cellIndex + batchSize, state.totalCells);
                var batchIndex = state.cellIndex / batchSize;

                // 每批用独立 Rand seed（保证跨帧 Rand 独立，每批可复现）。
                Rand.PushState();
                try
                {
                    Rand.Seed = Gen.HashCombineInt(state.randSeed, batchIndex);
                    for (var i = state.cellIndex; i < batchEnd; i++)
                    {
                        var cell = map.cellsInRandomOrder.Get(i);
                        // ChanceToSkip=0.001f（99.9% 不跳过），这里直接处理所有格（跳过 Rand.Chance 优化）。
                        spawner.CheckSpawnWildPlantAt(cell, state.densityFactor, state.desiredPlants, setRandomGrowth: true);
                    }
                }
                finally
                {
                    Rand.PopState();
                }
                state.cellIndex = batchEnd;

                if (RimExodusMod.Settings?.verboseLogging ?? false && state.cellIndex % 10000 == 0)
                    Log.Message($"[RimExodus] Plants genStep: {state.cellIndex}/{state.totalCells} cells processed.");
            }

            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] Plants genStep: done ({state.totalCells} cells).");
            return true;
        }

        /// <summary>跑一个 genStep（复刻 GenerateContentsIntoMap:319-344）。</summary>
        private void RunOneGenStep()
        {
            var step = genSteps[currentStepIndex];
            var sw = RimExodusMod.Settings?.verboseLogging ?? false
                ? System.Diagnostics.Stopwatch.StartNew() : null;

            // 临时设 ProgramState=MapInitializing（原版 GenerateMap:82 行为）：
            // WaterBodyTracker.Notify_TerrainChanged(:175) 检查 ProgramState==Playing 才执行，
            // 生成期间 ProgramState=Playing 会导致它进入 TryCreateBodyOrAddToExisting → NRE（bodies 未初始化）。
            // genStep 结束后恢复 Playing（主线程 tick 在 genStep 之间发生，那时 ProgramState 已恢复）。
            var savedState = Current.ProgramState;
            Current.ProgramState = ProgramState.MapInitializing;
            Rand.PushState();
            try
            {
                var parms = step.parms;
                var gravship = gravshipField.GetValue(null);
                if (gravship != null) parms.gravship = (Gravship)gravship;

                Rand.Seed = Gen.HashCombineInt(baseSeed, GetSeedPartFor(currentStepIndex));
                step.def.genStep.Generate(generatingMap, parms);

                if (generatingMap.pathing.IncrementalDirtyingDisabled)
                {
                    Log.Error($"[RimExodus] Genstep [{currentStepIndex}] {step.def} ended with path incremental dirtying disabled.");
                    generatingMap.pathing.ReEnableIncrementalDirtying();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] Error in GenStep [{currentStepIndex}] {step.def}: {ex}");
            }
            finally
            {
                Rand.PopState();
                Current.ProgramState = savedState; // 恢复 Playing（主线程 tick 在 genStep 之间发生）。
            }
            sw?.Stop();
            if (sw != null)
                Log.Message($"[RimExodus] GenStep [{currentStepIndex}/{genSteps.Count}] {step.def.defName} {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>完成阶段：FinalizeInit + 清理 + onComplete（复刻 GenerateMap:188-223）。</summary>
        private void FinishGeneration()
        {
            finalizing = true; // 让 IsGenerating 返回 false（map 可被 tick/渲染了）。

            try
            {
                Find.Scenario.PostMapGenerate(generatingMap);
                generatingMap.FinalizeInit();
                MapComponentUtility.MapGenerated(generatingMap);
                generatingMap.Parent?.PostMapGenerate();
                MapGeneratorPostInitFor(generatingMap);

                onComplete?.Invoke(generatingMap);

                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] IncrementalMapGenerator finished: map={generatingMap.uniqueID}.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] IncrementalMapGenerator FinishGeneration failed: {ex}");
            }
            finally
            {
                // 清理 MapGenerator static（复刻 GenerateMap finally :215-222，但不恢复 ProgramState——未翻转）。
                // 不 PopState——准备阶段未保持外层 Rand 栈帧（EnsureStateStackEmpty 会清空跨帧的栈）。
                ClearWorkingDataStatic();
                MapGenerator.mapBeingGenerated = null;
                gravshipField.SetValue(null, null);
                RockNoises.Reset();
                current = null;
                generatingMap = null;
            }
        }

        // ===== 辅助 =====

        private int GetSeedPartFor(int index)
        {
            // 复刻 MapGenerator.GetSeedPart（private static），用反射或内联。
            // 内联更简单：SeedPart + 前面同 SeedPart 的数量。
            var genStepsList = genSteps;
            var seedPart = genStepsList[index].def.genStep.SeedPart;
            int count = 0;
            for (int i = 0; i < index; i++)
            {
                if (genStepsList[i].def.genStep.SeedPart == seedPart) count++;
            }
            return seedPart + count;
        }

        private static bool IsValidBiomeGenStep(GenStepDef def)
        {
            // 复刻 MapGenerator.IsValidBiome（:228-231）：过滤 Scenario 里被 ScenPart_DisableMapGen 禁用的 genStep。
            return !Find.Scenario.AllParts.Any(p =>
                typeof(ScenPart_DisableMapGen).IsAssignableFrom(p.def.scenPartClass) && p.def.genStep == def);
        }

        private static GenStepWithParams GetGenStepParmsFor(GenStepDef def)
        {
            return new GenStepWithParams(def, default);
        }

        private static void MapGeneratorPostInitFor(Map map)
        {
            // 复刻 MapGenerator.MapGeneratorPostInit：调每个 genStep 的 PostMapInitialized。
            var tmpGenSteps = (List<GenStepWithParams>)tmpGenStepsField.GetValue(null);
            foreach (var step in tmpGenSteps)
            {
                try
                {
                    step.def.genStep.PostMapInitialized(map, step.parms);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] Error in PostMapInitialized {step.def}: {ex}");
                }
            }
        }

        private static void CleanupFailedGeneration(Map map)
        {
            try
            {
                ClearWorkingDataStatic();
            }
            catch { }
            try { MapGenerator.mapBeingGenerated = null; } catch { }
            try { gravshipField.SetValue(null, null); } catch { }
            try { RockNoises.Reset(); } catch { }
            try
            {
                if (map != null && Find.Maps.Contains(map))
                {
                    // 半成品 map 的组件可能未完全初始化，DeinitAndRemoveMap 内部 dispose 可能 NRE。
                    // 先从 Find.Maps 移除，再尝试清理；逐项 try/catch 容忍部分失败。
                    Current.Game.DeinitAndRemoveMap(map, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] CleanupFailedGeneration DeinitAndRemoveMap error (map may leak): {ex}");
                // 兜底：至少从 Find.Maps 移除，避免主线程继续 tick 半成品 map。
                try { if (map != null) Find.Maps.Remove(map); } catch { }
            }
        }
    }
}
