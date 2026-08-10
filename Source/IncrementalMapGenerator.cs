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
                // 若实测有 genStep 依赖 MapInitializing，再按需 patch。
                Rand.PushState();
                int seed;
                try
                {
                    ClearWorkingDataStatic();
                    seed = Gen.HashCombineInt(Find.World.info.Seed, mapParent.ID);
                    Rand.Seed = seed;
                }
                catch
                {
                    Rand.PopState();
                    throw;
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
                    Rand.Seed = seed;
                    RockNoises.Init(newMap);
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

        /// <summary>每帧推进 genStep。</summary>
        private void TickGeneration()
        {
            if (generatingMap == null || genSteps == null) return;

            try
            {
                if (currentStepIndex < genSteps.Count)
                {
                    RunOneGenStep();
                    currentStepIndex++;
                    if (currentStepIndex < genSteps.Count) return; // 还有 genStep，下一帧继续。
                }

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

        /// <summary>跑一个 genStep（复刻 GenerateContentsIntoMap:319-344）。</summary>
        private void RunOneGenStep()
        {
            var step = genSteps[currentStepIndex];
            if (RimExodusMod.Settings?.verboseLogging ?? false)
                Log.Message($"[RimExodus] GenStep [{currentStepIndex}/{genSteps.Count}] {step.def.defName}");

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
            }
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
                ClearWorkingDataStatic();
                MapGenerator.mapBeingGenerated = null;
                gravshipField.SetValue(null, null);
                RockNoises.Reset();
                Rand.PopState(); // 弹出准备阶段 PushState 的外层栈帧。
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
                MapGenerator.mapBeingGenerated = null;
                gravshipField.SetValue(null, null);
                RockNoises.Reset();
                if (map != null && Find.Maps.Contains(map))
                {
                    Current.Game.DeinitAndRemoveMap(map, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus] CleanupFailedGeneration error: {ex}");
            }
        }
    }
}
