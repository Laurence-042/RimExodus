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

        // ===== GenerateMap postfix 复放实参（2026-09）=====
        // 与原版 MapGenerator.GenerateMap 形参表逐位对应的实参快照（isPocketMap/stepDebugger 恒
        // false——增量路径只生成表面地块图）。FinishGeneration 成功后交给
        // GenerateMapPostfixReplay 复放第三方 postfix（VEF ObjectSpawns 等，机制详见其类注释）。
        private object[] generateMapArgs;

        // ===== 全程计时（verbose 诊断，2026-08：genStep 后收尾长尾用时统计）=====
        private float startRealtime;   // Start 同步段起点（Time.realtimeSinceStartup），总算 wall 时长。
        private int startTickGame;     // Start 时刻 GenTicks.TicksGame，总算生成消耗的 tick 数。
        private double totalGenStepMs; // genStep CPU 耗时累加（仅 verbose 时累加，供 FinishGeneration 总摘要）。

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
            public double cpuMs; // verbose 计时：跨多次 RunSubStepChunk 调用累计的 CPU 耗时（不含帧间等待）。
            public int nextProgressLog; // 进度日志的下一个阈值（batchSize 不再整除 10000，改累进）。
        }

        // ===== 反射缓存（MapGenerator private static 访问）=====
        private static readonly FieldInfo tmpGenStepsField = AccessTools.Field(typeof(MapGenerator), "tmpGenSteps");
        private static readonly MethodInfo getSeedPartMethod = AccessTools.Method(typeof(MapGenerator), "GetSeedPart");
        private static readonly FieldInfo gravshipField = AccessTools.Field(typeof(MapGenerator), "gravship");
        private static readonly MethodInfo clearWorkingDataMethod = AccessTools.Method(typeof(MapGenerator), "ClearWorkingData");

        /// <summary>当前是否有分帧生成在进行（供外部判断是否清理防重入锁）。</summary>
        public static bool IsAnyGenerating => current != null;

        /// <summary>
        /// 当前分帧生成进度描述（"{当前步}/{总步} {步骤defName}"；无生成时 null）。供左上角进度 UI
        /// （<see cref="MapGenerationProgressUI"/>）消费。收尾帧（FinalizeInit 等）显示为 Finalizing。
        /// 注意：仅覆盖分帧增量路径——Settlement 原生生成是同步单帧（游戏冻结中 UI 无法刷新），不在此列。
        /// </summary>
        public static string GenerationProgressDescription
        {
            get
            {
                var c = current;
                if (c?.genSteps == null || c.genSteps.Count == 0) return null;
                var name = c.currentStepIndex < c.genSteps.Count ? c.genSteps[c.currentStepIndex].def.defName : "Finalizing";
                return UnityEngine.Mathf.Min(c.currentStepIndex + 1, c.genSteps.Count) + "/" + c.genSteps.Count + " " + name;
            }
        }

        internal static void ClearWorkingDataStatic()
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
            // 自愈守卫：读档/换档不清理静态 current（MapComponent 的 static 游离于序列化之外，
            // 无载入钩子）——驱动前校验目标图仍在 Find.Maps，悬挂的旧对象（已死图的组件）直接丢弃。
            // 不校验则换档后 IsAnyGenerating 永真（后续生成全拒 + 进度 UI 常驻）且驱动已死图组件。
            if (current != null && (current.generatingMap == null || !Find.Maps.Contains(current.generatingMap)))
            {
                Log.Warning("[RimExodus] IncrementalMapGenerator: stale current (map gone, e.g. after load), discarding.");
                current = null;
            }
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

            var prepStartRealtime = UnityEngine.Time.realtimeSinceStartup;
            try
            {
                // ===== 准备阶段（复刻 MapGenerator.GenerateMap :82-185 的前半段）=====
                // 注意：不翻转 ProgramState（原版设 MapInitializing，但分帧期间主线程继续 tick，
                // 翻转会导致 20+ 处守卫逻辑错乱）。genStep 通常不读 ProgramState，保持 Playing 更安全。
                // 不保持外层 Rand.PushState 栈帧——Root.Update 每帧调 EnsureStateStackEmpty 会清空栈，
                // 跨帧保持的 PushState 会被清掉导致 FinishGeneration 的 PopState 弹空栈。
                // 每个 genStep 用 Rand.PushState/Seed/PopState 独立配对（RunOneGenStep），不依赖外层栈。
                // seed 必须用 mapParent.Tile.GetHashCode()——与原版 MapGenerator.GenerateMap（MapGenerator.cs:90）
                // 和 MapPreview（SeedRerollData.GetOriginalMapSeed = World.info.Seed ⊕ tile.GetHashCode()）一致。
                // 早期错误地用了 mapParent.ID（WorldObject 自增 ID），导致 B 的 elevation Perlin 种子与 MapPreview
                // 预览不同 → 预览地形和实际生成完全对不上（包括非混合带部分）。A 走原生 MapGenerator 不受影响。
                int seed = Gen.HashCombineInt(Find.World.info.Seed, mapParent.Tile.GetHashCode());
                ClearWorkingDataStatic();
                // 复刻原版 GenerateMap 开头的跨图 static 重置（MapGenerator.cs:83-85，ClearWorkingData
                // 本身不动这两个字段）：PlayerStartSpot/rootsToUnfog 是进程级共享，本类分帧跨多张图
                // 生成，不重置则上一张图的坐标残留——FindPlayerStartSpot(850) 的"已设跳过"守卫直接
                // 跳过本图选址，GenStep_Fog(1500) 用他图坐标揭雾（坐标落 MakeFog 建筑时零揭雾→整图
                // 全雾，2026-08 偶发全雾成因）；rootsToUnfog 残留则在本图按他图坐标乱揭。原生
                // GenerateMap 交错插入时其开头也会重置，本行保证增量路径独立于交错也干净开局。
                MapGenerator.PlayerStartSpot = IntVec3.Invalid;
                MapGenerator.rootsToUnfog.Clear();
                // 外层 Rand 状态保护：Start 的同步段（ConstructComponents + 组装 genStep + RockNoises.Init）
                // 不跨帧，用 PushState/Seed/PopState 包裹，复刻原版 GenerateMap 顶部的 seed 设置。
                // 关键：Rand.Seed = seed 把 iterations 清零，使 FillComponents 入口 iterations 干净（MapPreview
                // 在 FillComponents_Prefix 检测 iterations，期望 1 = GasGrid 构造器的 1 次 Rand；若读不到干净
                // 值会告警 "vanilla map components modified RNG by N"）。Start 末尾 PopState 恢复主线程 RNG。
                // 不跨帧，EnsureStateStackEmpty 不会清栈。
                Rand.PushState();
                Rand.Seed = seed;
                Map newMap = null;

                try
                {
                newMap = new Map();
                newMap.uniqueID = Find.UniqueIDsManager.GetNextMapID();
                newMap.generationTick = GenTicks.TicksGame;
                newMap.events = new MapEvents(newMap);

                MapGenerator.mapBeingGenerated = newMap;
                newMap.info.Size = mapSize;
                    newMap.info.parent = mapParent;
                    newMap.generatorDef = mapGeneratorDef; // 关键：OutdoorTemp/Biome 等依赖此字段，原版 :126。
                    newMap.info.disableSunShadows = mapGeneratorDef.disableShadows;
                    // mapParent.Tile 是真实 PlanetTile，newMap.TileInfo 自动读 Find.WorldGrid[parent.Tile]
                    // （含真实 biome/hillness/mutators/rivers），原生 Coast/River/Delta 等 TileMutator 自然生效。
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

                    // GL 兼容（2026-08）：复刻 GL 的 GenerateContentsIntoMap Prefix 语义——Prepare 建立
                    // 静态生成上下文 + BiomeGrid 初始化 + genStep 注入（BiomeVariants/CustomGenSteps）。
                    // 时点对齐原生路径（GL Prefix 在 GenerateContentsIntoMap 入口、组装 genStep 之前）；
                    // Prepare 种子确定性派生（世界⊕tile），不消费全局 Rand，增量 RNG 流零扰动。
                    SeamlessLandformsCompat.TryPrepare(newMap);
                    SeamlessLandformsCompat.InitBiomeGrid(newMap);
                    var landformExtraSteps = SeamlessLandformsCompat.GetExtraGenSteps(newMap);

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
                    if (landformExtraSteps != null)
                        enumerable = enumerable.Concat(landformExtraSteps);
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
                    // 原版 GenerateMap 形参序：(mapSize, parent, mapGenerator, extraGenStepDefs,
                    // extraInitBeforeContentGen, isPocketMap, stepDebugger)。复放器按位置/名字编组。
                    comp.generateMapArgs = new object[]
                    {
                        mapSize, mapParent, mapGeneratorDef, extraGenStepDefs, extraInitBeforeContentGen, false, false
                    };
                    comp.startRealtime = prepStartRealtime;
                    comp.startTickGame = GenTicks.TicksGame;
                    current = comp;

                    newMap.areaManager.AddStartingAreas();
                    newMap.weatherDecider.StartInitialWeather();

                    if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                        Log.Message($"[RimExodus] IncrementalMapGenerator started: map={newMap.uniqueID}, genSteps={orderedSteps.Count}, seed={seed}, " +
                                    $"prep={(UnityEngine.Time.realtimeSinceStartup - prepStartRealtime) * 1000f:F0}ms.");

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimExodus] IncrementalMapGenerator prepare failed: {ex}");
                    CleanupFailedGeneration(newMap);
                    return false;
                }
                finally
                {
                    // 恢复主线程 RNG（Start 同步段用干净 seed 跑完，分帧 genStep 有自己的 PushState/Seed/PopState）。
                    Rand.PopState();
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
                        // Plants 全部完成：分帧步不经过 RunOneGenStep（无 per-step 计时），
                        // 此处补齐——CPU 累计进总摘要并出一条与其他 genStep 同格式的日志。
                        if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                        {
                            totalGenStepMs += subStepState.cpuMs;
                            Log.Message($"[RimExodus] GenStep [{currentStepIndex}/{genSteps.Count}] " +
                                        $"{genSteps[currentStepIndex].def.defName} {subStepState.cpuMs:F0}ms (split-frame)");
                        }
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
                nextProgressLog = 10000,
            };
            if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
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
            // 每批 cell 数（每批用独立 Rand seed）。默认 64（2026-08 从 2000 降下来）：实测
            // CheckSpawnWildPlantAt 平均 ~85µs/格（肥沃格候选植物 + 簇距离计算贵），2000 格/批 ≈
            // 170ms/帧——8ms 预算被超 20 倍，"分帧"名存实亡（每 tick 2 批 = 340ms 巨型 tick，
            // 游戏 ~1.6tps 爬行）。64 格 ≈ 5.4ms 平均/批，预算真正生效；代价是总 wall 拉长
            // （CPU 总量不变，5.3s CPU / 8ms 每帧 ≈ 660+ 帧），分帧设计本意即平滑优先于速度。
            // 2026-08 起可由设置 generationBatchSize 调节（16-512，clamp 兜底）：调大 = 生成更快
            // 但每帧更卡。batchIndex 进度日志按累进阈值，不依赖 batchSize 整除 10000。
            var batchSize = System.Math.Max(System.Math.Min(
                RimExodusMod.Settings?.generationBatchSize ?? 64, 512), 16);
            // verbose 计时：本方法每次调用执行一段（同步无等待），elapsed 累计进 state.cpuMs，
            // 完成时由 TickGeneration 计入 totalGenStepMs——分帧步的 CPU 不含帧间等待。
            var sw = RimExodusLog.Enabled(RimExodusLogModule.Generation)
                ? System.Diagnostics.Stopwatch.StartNew() : null;

            try
            {
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

                    // 注意括号：?? 优先级低于 &&，历史上写作 `a ?? false && c` 时解析为
                    // `a ?? (false && c)`——verbose 开启后每批都打日志（节流失效），2026-08 修正。
                    // 阈值用累进（batchSize=64 不整除 10000，取模判定会永不触发）。
                    if ((RimExodusLog.Enabled(RimExodusLogModule.Generation)) && state.cellIndex >= state.nextProgressLog)
                    {
                        Log.Message($"[RimExodus] Plants genStep: {state.cellIndex}/{state.totalCells} cells processed.");
                        state.nextProgressLog += 10000;
                    }
                }

                if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
                    Log.Message($"[RimExodus] Plants genStep: done ({state.totalCells} cells).");
                return true;
            }
            finally
            {
                if (sw != null)
                {
                    sw.Stop();
                    state.cpuMs += sw.Elapsed.TotalMilliseconds;
                }
            }
        }

        /// <summary>跑一个 genStep（复刻 GenerateContentsIntoMap:319-344）。</summary>
        private void RunOneGenStep()
        {
            // GL 兼容：分帧跨数百帧持有进程级静态上下文，每步执行前校验存活（被 GL 的 MapPreview
            // 后台线程或交错的原生生成清掉则重 Prepare，幂等）——GL worker 靠该上下文决定是否写地形。
            SeamlessLandformsCompat.EnsureContextAlive(generatingMap);

            var step = genSteps[currentStepIndex];
            var sw = RimExodusLog.Enabled(RimExodusLogModule.Generation)
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
            {
                totalGenStepMs += sw.Elapsed.TotalMilliseconds;
                Log.Message($"[RimExodus] GenStep [{currentStepIndex}/{genSteps.Count}] {step.def.defName} {sw.ElapsedMilliseconds}ms");
            }
        }

        /// <summary>完成阶段：FinalizeInit + 清理 + onComplete（复刻 GenerateMap:188-223）。</summary>
        private void FinishGeneration()
        {
            finalizing = true; // 让 IsGenerating 返回 false（map 可被 tick/渲染了）。

            // 收尾段计时（verbose，2026-08）：genStep 链跑完后的单帧长尾拆解。FinalizeInit 内部的
            // 三个全图级重活（pathGrid 重算 / region 重建 / mesh 全量重建）由 Patches_MapGenTiming
            // 单独出日志；onComplete 内的 RimExodus 邻居登记链由 SeamlessTileManager 回调内部计时。
            var timer = SectionTimer.StartIf(RimExodusLog.Enabled(RimExodusLogModule.Generation));
            long tScenario, tFinalizeInit, tMapComponents, tParentPost, tPostInit, tOnComplete;
            // 复放前置状态：FinishGeneration 无异常跑完才算"原方法成功返回"（原生语义：原方法抛
            // 异常则 postfix 不跑）；completedMap 在 finally 置空 generatingMap 前捕获。
            var completed = false;
            Map completedMap = null;

            try
            {
                // GL 兼容：对齐原生路径的 GL Postfix 时点（GenerateContentsIntoMap 返回处）——
                // genStep 链完成后即清静态上下文，再进入收尾段。
                SeamlessLandformsCompat.Cleanup();
                Find.Scenario.PostMapGenerate(generatingMap);
                tScenario = timer?.Section() ?? 0;

                generatingMap.FinalizeInit();
                tFinalizeInit = timer?.Section() ?? 0;

                MapComponentUtility.MapGenerated(generatingMap);
                tMapComponents = timer?.Section() ?? 0;

                generatingMap.Parent?.PostMapGenerate();
                tParentPost = timer?.Section() ?? 0;

                MapGeneratorPostInitFor(generatingMap);
                tPostInit = timer?.Section() ?? 0;

                onComplete?.Invoke(generatingMap);
                tOnComplete = timer?.Section() ?? 0;

                if (timer != null)
                {
                    Log.Message($"[RimExodus] FinishGeneration timings: map={generatingMap.uniqueID} " +
                                $"scenario={tScenario}ms finalizeInit={tFinalizeInit}ms mapComponents={tMapComponents}ms " +
                                $"parentPostMapGenerate={tParentPost}ms postMapInitialized={tPostInit}ms onComplete={tOnComplete}ms " +
                                $"total={tScenario + tFinalizeInit + tMapComponents + tParentPost + tPostInit + tOnComplete}ms.");
                    // wall 与 genStepCpu 的差值 = 分帧时间预算（8ms/帧）+ tick/渲染摊薄的成本。
                    Log.Message($"[RimExodus] Map generation total: map={generatingMap.uniqueID} " +
                                $"wall={(UnityEngine.Time.realtimeSinceStartup - startRealtime) * 1000f:F0}ms " +
                                $"ticks={GenTicks.TicksGame - startTickGame} genSteps={genSteps.Count} genStepCpu={totalGenStepMs:F0}ms.");
                }

                completed = true;
                completedMap = generatingMap;
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

            // GenerateMap postfix 复放（2026-09）：finally 之后调用对齐原生时点（Harmony postfix 在
            // 原方法 try/finally 完成后执行，此时 MapGenerator static 已是清理后的值）。只在增量
            // 路径触发——同步逃生/POI 原生/营地路径调原方法本体，patch 原生生效，复放会双跑。
            // onComplete（邻居登记/传送点/ZoneRestore）已先行 → VEF 的 no-thing 判据能看到传送点。
            if (completed && completedMap != null)
                GenerateMapPostfixReplay.Replay(completedMap, generateMapArgs);
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
            // 统一清 static current（2026-08 修复缺口）：凡走失败清理即本轮生成终结，无条件清。
            // 此前只有 TickGeneration catch 额外清 current，Start 内层 catch
            // （AddStartingAreas/StartInitialWeather 失败，此时 current 已在 :243 赋值）不清 →
            // IsAnyGenerating 永真、后续生成全拒 + 进度 UI 常驻。全局单生成器，无"他图占用"问题。
            try { current = null; } catch { }
            // GL 兼容：失败清理同样收掉 GL 静态上下文（与 FinishGeneration 正常路径语义一致）。
            SeamlessLandformsCompat.Cleanup();
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
