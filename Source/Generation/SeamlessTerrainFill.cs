using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 多边形地形铺设工具（阶段3：多边形裁切）。
    /// 每个地块独立按自己的六边形铺 void：六边形内（含边）非 void，六边形外 void。
    /// 不看邻居——两个地图各自独立铺 void，重叠区的 void 在对方地图上恰好是非 void。
    /// 传送点铺在自己六边形的边经过的格子上（含边判定 → 非 void）。
    /// 供 GenStep_SeamlessTile（order=389，Roads 之前、Settlement 之前）调用，该 genStep 通过 XML patch
    /// 注入到所有玩家可进入的 MapGeneratorDef（Base_Player / Base_Faction / Encounter）。
    /// 地块图与原生 parent 图（家园/原生家族）走完全相同的 genStep 链。
    /// </summary>
    public static class SeamlessTerrainFill
    {
        /// <summary>
        /// 备份基础快照（void 裁切前，平行三层：terrain / building / roof）并按接缝带几何铺 void。
        ///
        /// **两入口共用同一逻辑体**（归一点）：原生 parent 图（家园/原生家族）与地块图的 void 铺设
        /// 都走本方法。快照存储载体由 <see cref="SeamlessMapData.SetBaseSnapshots"/> 统一屏蔽
        /// （地块图存 MapParent_SeamlessTile 字段，原生图存 SeamlessTileManager 组件），
        /// 其余完全一致——避免两份重复的"备份+铺void"逻辑漂移。
        ///
        /// 三层同点位备份（均非序列化——跨读档的接缝参考由序列化的 SeamStripData 承担）。
        /// void 铺设会清掉接缝带外格的岩体与屋顶，SeamStripData 对外条带格必须引用**原生**
        /// 三层（同源）。**清理全部归 ApplyPolygonTerrain（依次清 roof → rock → terrain，
        /// 用户定夺 2026-08）**：原生数据先落快照再被清理，时序天然安全。历史教训：曾在
        /// order 200 patch 提前清岩，外条带原生岩体在备份前丢失 → 新图照抄区岩壁整齐切断。
        /// </summary>
        public static void BackupSnapshotAndApplyVoid(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            SeamlessMapData.SetBaseSnapshots(map,
                (TerrainDef[])map.terrainGrid.topGrid.Clone(),
                BackupBuildingSnapshot(map),
                BackupRoofSnapshot(map));

            ApplyPolygonTerrain(map, worldTile);
        }

        /// <summary>
        /// 备份全图建筑层快照（格 → 岩石体 BuildingDef，自然岩石 + 矿石，null=无）。遍历 listerThings
        /// 而非逐格 GetEdifice（岩石数量级几千，远小于全图 62500 格）。
        /// </summary>
        private static ThingDef[] BackupBuildingSnapshot(Map map)
        {
            var defs = new ThingDef[map.Size.x * map.Size.z];
            var indices = map.cellIndices;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is not Building b || !thing.Spawned) continue;
                // 判据 isNaturalRock（自然岩石与矿石同 true）；naturalTerrain 只盖非矿石天然岩
                // （TerrainDefGenerator_Stone 赋值规则），矿石须走 isNaturalRock（2026-08 修复）。
                if (b.def.building == null || !b.def.building.isNaturalRock) continue;
                var idx = indices.CellToIndex(thing.Position);
                if (idx >= 0 && idx < defs.Length) defs[idx] = b.def;
            }
            return defs;
        }

        /// <summary>
        /// 备份全图屋顶层快照（格 → RoofDef，null=无）。RoofGrid 内部数组私有，逐格 RoofAt
        /// （生成期一次性全图遍历，可接受）。
        /// </summary>
        private static RoofDef[] BackupRoofSnapshot(Map map)
        {
            var defs = new RoofDef[map.Size.x * map.Size.z];
            var indices = map.cellIndices;
            var roofGrid = map.roofGrid;
            foreach (var c in map.AllCells)
            {
                defs[indices.CellToIndex(c)] = roofGrid.RoofAt(c);
            }
            return defs;
        }

        /// <summary>
        /// 按 worldTile 的接缝带几何铺 void：接缝带外（格中心在多边形外且 ∉ 接缝带 B）铺 void，
        /// 其余（核心区 + 接缝带三圈，含带外圈）保持原生地形。
        /// void 格铺 RimExodus_Void 并清除其上的实体与 Pawn。
        ///
        /// 【新定义（doc/接缝带定义.md）】带外圈（离散边外侧一圈）从 void 变为实地形——
        /// 传送圈 = 离散边圈 ∪ 带外圈（外侧 2 圈），3 圈带全实地形使传送落点 ±1 格偏差
        /// （连续边中点对齐的取整残差）落在对侧带内/带外圈，不会传送到 void。
        /// </summary>
        public static void ApplyPolygonTerrain(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            if (voidDef == null)
            {
                Log.Error("[RimExodus] RimExodus_Void terrain not found, cannot apply polygon fill.");
                return;
            }

            var size = map.Size;
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, size.x);

            // 逐格判定：void = 接缝带外（格中心在多边形外且 ∉ B）。
            // 防孤岛等价性：凸多边形下角在内的格必属 {中心在内} ∪ 离散边圈（⊆ B），
            // 旧"中心或 4 角任一在内"角检测的非 void 集被 {中心在内} ∪ B 完全覆盖且多出带外圈（有意）。
            var voidCells = new List<IntVec3>();

            for (var x = 0; x < size.x; x++)
            {
                for (var z = 0; z < size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (SeamlessPolygonGeometry.IsVoidCell(band, worldTile, size.x, cell)) voidCells.Add(cell);
                }
            }

            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            if (verbose)
                Log.Message($"[RimExodus] ApplyPolygonTerrain worldTile={worldTile} map={map.uniqueID} size={size.x} voidCells={voidCells.Count} nonVoid={size.x*size.z - voidCells.Count}");
            var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
            long tClear = 0, tTerrain = 0, tEvac = 0, tClassify = 0, tRoof = 0;
            if (sw != null) { tClassify = sw.ElapsedMilliseconds; }

            // 依次清 roof → rock（实体）→ terrain（用户定夺 2026-08；快照已在备份后，时序安全）。
            // 先清屋顶（RocksFromGrid 设的 RoofRockThick/Thin）。
            foreach (var cell in voidCells)
            {
                if (map.roofGrid.RoofAt(cell) != null)
                {
                    map.roofGrid.SetRoof(cell, null);
                }
            }
            if (sw != null) { tRoof = sw.ElapsedMilliseconds - tClassify; }

            // 再清除虚空格上的实体（岩石 Building/植物/物品等），保留 Pawn（Pawn 单独处理）。
            ClearThingsOnCells(map, voidCells);
            if (sw != null) { tClear = sw.ElapsedMilliseconds - tRoof - tClassify; }

            // 铺虚空地形：直接写 topGrid（公开字段 TerrainGrid.cs:13）跳过 SetTerrain 的重计算副作用，
            // 只保留渲染必需的 mesh 脏标记。void 地形 dontRender=true、passability=Impassable、不发光、
            // 不是水、layerable=false。SetTerrain 的 DoTerrainChangedEffects 把 mesh 标记（必需）和
            // pathGrid/waterBodyTracker 重算（非必需且耗时）混在一起，21000 次 × 副作用 = 5.7 秒。
            // 这里只做 mesh 标记 + 下方的 pathGrid 全量刷新。
            // regenAdjacentCells=false：void 格大面积连续，邻格也是 void 或边格，不需逐格扩散 dirty 标记，
            // FinalizeInit 的 RegenerateEverythingNow 会全量重建 mesh。
            var terrainGrid = map.terrainGrid;
            var cellIndices = map.cellIndices;
            var topGrid = terrainGrid.topGrid; // public TerrainDef[]（TerrainGrid.cs:13）
            var mapDrawer = map.mapDrawer;
            foreach (var cell in voidCells)
            {
                var idx = cellIndices.CellToIndex(cell);
                topGrid[idx] = voidDef;
                mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
            }
            if (sw != null) { tTerrain = sw.ElapsedMilliseconds - tClear - tClassify; }

            // MapPreview 预览线程限定（2026-08）：预览纹理的 PreviewTextureGenStep 用
            // Elevation>=0.7 代理判据画石色（不读岩石实体），本步清岩不写 elevation →
            // 曾有岩的 void 格在预览里仍显岩石。预览是该阈值的唯一消费者，真实地图渲染
            // 不读 MapGenerator.Elevation，故只在预览线程抹平，主生成链零改动。
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread && MapGenerator.Elevation != null)
            {
                var elevation = MapGenerator.Elevation;
                foreach (var cell in voidCells)
                    elevation[cell] = 0f;
            }

            // pathGrid 必须在此即时全量刷新（勿删，2026-08 历史教训）：Walkable/Standable 读的是
            // PathGrid 缓存数组（GenGrid.Walkable → pathGrid.WalkableFast），不读 terrainGrid——
            // 直写 topGrid 后若等 FinalizeInit 才重算，本 genStep(389) 与 FinalizeInit 之间的所有
            // 步骤（Settlement 400 / Plants 900 / Animals 1200 / Fog 1500 / 威胁步骤 1600）全部
            // 拿着"void 可走"的旧缓存选址，动物/pawn 照落 void（2026-08 实测动物站 void 即此机理）。
            // 全图重算与 FinalizeInit(Map.cs:804) 同款（生成期无脏位模式，真实重算），一次性成本；
            // 当年规避的 5.7 秒是 DoTerrainChangedEffects 全副作用路径（waterBodyTracker/逐格植物销毁），
            // 与这一条重算调用无关。
            // MapPreview 预览图例外（2026-08）：预览图的 Map 组件被 MapPreview 裁剪（FillComponents
            // transpiler 过滤，Reachability 不在白名单 → null），刷新在 NotifyCellDirtied 第一行
            // NRE；预览不消费 pathGrid，跳过（见 SeamlessMapPreviewCompat）。
            if (!SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread)
                map.pathing.RecalculateAllPerceivedPathCosts();

            // 纯防御：清除生成在虚空格上的 Pawn。order=389 时图上正常无 pawn（Settlement 400+/ScenParts 875+
            // 都在本步之后），保留兜底以防 mod 化 genStep 等异常来源；pathGrid 已在上一步刷新，判定可靠。
            EvacuatePawnsOnCells(map, voidCells);
            if (sw != null)
            {
                tEvac = sw.ElapsedMilliseconds - tTerrain - tClear - tClassify;
                sw.Stop();
                Log.Message($"[RimExodus] ApplyPolygonTerrain timings: classify={tClassify}ms roof={tRoof}ms clear={tClear}ms terrain={tTerrain}ms evac={tEvac}ms");
            }
        }

        /// <summary>
        /// 清除指定格集合上的所有实体（岩石 Building/植物/物品/草丛等），保留 Pawn（Pawn 单独处理）。
        ///
        /// **当前时序下的实际工作量**：void 在 order=389（Roads 之前、Settlement 之前）铺。此时
        /// RocksFromGrid(200) 的岩石已 spawn 在将来 void 格上，是本方法的主要清理对象；
        /// Plants(900)/Animals(1200) 在本步之后、于最终地形上生成（void fertility=0 / Standable=false
        /// 天然跳过 void 格），不再需要事后清理。清理开销在生成时一次性发生。
        ///
        /// **批量注销（2026-08 O(n²) 修复，"暂存清空"法，勿回退为逐 thing 直接 Destroy）**：
        /// per-thing Destroy→DeSpawn 的列表注销是 O(n²)——ThingOwner.Remove（spawnedThings）=
        /// Contains 头扫 + LastIndexOf 尾扫 + RemoveAt 搬移各 O(n)；ListerThings.Remove =
        /// per-def 列表线性删 + 每个匹配 ThingRequestGroup 的组列表各删一遍。实测 52µs/thing
        /// @ spawnedTotal=5.4k（岩少图）、~140µs @ ~3 万（岩多图 clear=2.4s）——单件成本随列表
        /// 总长增长，是二次方特征；岩石生成是 List.Add O(1)，删除是线性查找+搬移，即"生成快删除慢"。
        /// 修复：销毁前把两个热点容器的**活列表**（全公开访问器：InnerListForReading / ThingsOfDef /
        /// ThingsInGroup 返回 live 引用，零反射）复制暂存并 Clear——销毁循环里 DeSpawn 的 Remove
        /// 全部命中"空表不含"O(1) 早退，其余 DeSpawn 副作用（fog/roof/glow 通知、pathGrid 单格
        /// 重算、comps 等）原样执行，语义不变；结束后幸存者按原顺序装回（O(n) 总量）。
        /// 仅生成期安全：清空窗口内无并发 Spawn（DoLeavingsFor 在 MapInitializing 早退、岩石无
        /// 附件级联），stateHashByGroup 不补偿（生成期无消费缓存，FinalizeInit 后缓存全新建）。
        /// </summary>
        private static void ClearThingsOnCells(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return;

            var cellSet = new HashSet<IntVec3>(cells);
            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;

            var toDestroy = new List<Thing>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing is Pawn) continue;
                if (!thing.Spawned) continue;
                if (cellSet.Contains(thing.Position))
                {
                    toDestroy.Add(thing);
                }
            }
            var tEnumerate = sw?.ElapsedMilliseconds ?? 0;
            // spawnedThings 总量（销毁前）——历史 O(n²) 的 n（保留作回归观测）。
            var spawnedTotal = map.spawnedThings.Count;

            if (toDestroy.Count > 0)
            {
                var spawnedOwner = map.spawnedThings as ThingOwner<Thing>;
                if (spawnedOwner != null)
                {
                    // ==== 暂存清空热点容器 ====
                    // 结构铁律（2026-08 事故教训，勿改）：暂存/清空/销毁**全部**在 try 内、finally 恢复——
                    // 事故：组枚举碰 ThingsInGroup(Undefined) 抛 InvalidOperationException，抛点在 try 外的
                    // 暂存段 → spawnedThings/def 列表已 Clear 但恢复代码永不执行 → 注册表以清空态泄漏，
                    // 后续 genStep 在损坏注册表上运行（BaseGen RemoveAt 越界、Rand 栈不配平），且异常
                    // 中断 ApplyPolygonTerrain 后半段 → void 未铺、边界带（读 void 实况）为空。
                    var removed = new HashSet<Thing>(toDestroy);
                    List<Thing> spawnedLive = null;
                    List<Thing> spawnedSaved = null;
                    var defLive = new List<List<Thing>>();
                    var defSaved = new List<List<Thing>>();
                    var groupLive = new List<List<Thing>>();
                    var groupSaved = new List<List<Thing>>();
                    try
                    {
                        spawnedLive = spawnedOwner.InnerListForReading;
                        spawnedSaved = new List<Thing>(spawnedLive);
                        spawnedLive.Clear();

                        var defsToClear = new HashSet<ThingDef>();
                        foreach (var t in toDestroy) defsToClear.Add(t.def);
                        foreach (var def in defsToClear)
                        {
                            var live = map.listerThings.ThingsOfDef(def);
                            if (!ContainsAnyTarget(live, removed)) continue;
                            defLive.Add(live);
                            defSaved.Add(new List<Thing>(live));
                            live.Clear();
                        }

                        foreach (var group in ThingListGroupHelper.AllGroups)
                        {
                            // Undefined 组原版从不触碰（Includes 恒 false），ThingsInGroup 对其抛
                            // "Invalid ThingRequest"（AllGroups 含全部枚举值，Undefined 排第一）。
                            if (group == ThingRequestGroup.Undefined) continue;
                            List<Thing> live;
                            try
                            {
                                live = map.listerThings.ThingsInGroup(group);
                            }
                            catch (System.InvalidOperationException)
                            {
                                continue; // 未知无效组防御：跳过即可，该组不含任何东西
                            }
                            if (!ContainsAnyTarget(live, removed)) continue;
                            groupLive.Add(live);
                            groupSaved.Add(new List<Thing>(live));
                            live.Clear();
                        }

                        DestroyAll(toDestroy);
                    }
                    finally
                    {
                        // 幸存者按原顺序装回。双重过滤：removed（本批销毁/DeSpawn 的）与 Destroyed
                        // （防御级联销毁；注意 DeSpawn 分支非 Destroyed，必须靠 removed 过滤）。
                        // 任何一步暂存失败/销毁异常，已装填的 saved 部分也会在此恢复。
                        if (spawnedLive != null)
                            foreach (var t in spawnedSaved)
                                if (!removed.Contains(t) && !t.Destroyed) spawnedLive.Add(t);
                        RestoreList(defLive, defSaved, removed);
                        RestoreList(groupLive, groupSaved, removed);
                    }
                }
                else
                {
                    // 理论不可达（Map 构造用 ThingOwner<Thing>）：回落旧路径，宁可慢不可错。
                    Log.Warning("[RimExodus] ClearThingsOnCells: spawnedThings is not ThingOwner<Thing>, fallback to per-thing Destroy.");
                    DestroyAll(toDestroy);
                }
            }

            if (sw != null)
            {
                sw.Stop();
                var tDestroy = sw.ElapsedMilliseconds - tEnumerate;
                Log.Message($"[RimExodus] ClearThingsOnCells: things={toDestroy.Count} spawnedTotal={spawnedTotal} " +
                            $"enumerate={tEnumerate}ms destroy={tDestroy}ms " +
                            $"({(toDestroy.Count > 0 ? (double)tDestroy * 1000 / toDestroy.Count : 0):F0}µs/thing)");
            }
        }

        /// <summary>列表是否包含任意待销毁目标（null/空表/无交集返回 false，跳过暂存）。</summary>
        private static bool ContainsAnyTarget(List<Thing> list, HashSet<Thing> removed)
        {
            if (list == null || list.Count == 0) return false;
            foreach (var t in list)
                if (removed.Contains(t))
                    return true;
            return false;
        }

        private static void RestoreList(List<List<Thing>> liveLists, List<List<Thing>> savedLists, HashSet<Thing> removed)
        {
            for (var i = 0; i < liveLists.Count; i++)
                foreach (var t in savedLists[i])
                    if (!removed.Contains(t) && !t.Destroyed)
                        liveLists[i].Add(t);
        }

        /// <summary>逐 thing 销毁（Vanish）；holdingOwner 在 Destroy 前清空以对齐旧路径时序
        /// （旧路径 DeSpawn 的 ThingOwner.Remove 会清它；暂存清空后 Remove 早退走不到）。</summary>
        private static void DestroyAll(List<Thing> toDestroy)
        {
            foreach (var thing in toDestroy)
            {
                // 用 Destroy（Vanish）彻底移除；对 destroyable=false 的（如 SteamGeyser）改用 DeSpawn。
                thing.holdingOwner = null;
                if (thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
                else if (thing.Spawned)
                {
                    thing.DeSpawn();
                }
            }
        }

        /// <summary>
        /// 把生成在虚空格上的 Pawn 移到最近的可通行格（避免它们卡在不可通行地形上）。
        /// order=389 下图上正常无 pawn，纯防御路径（见调用处注释）。前提：调用前 pathGrid 已刷新——
        /// 历史教训勿回退：旧序（1400）直写 topGrid 后未刷新 pathGrid，Walkable 读旧缓存且径向
        /// 搜索首候选即 pawn 自身格，判"可走"→ 撤离原地空转，动物留在 void 上。
        /// </summary>
        private static void EvacuatePawnsOnCells(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return;

            var cellSet = new HashSet<IntVec3>(cells);
            var pawnsToMove = new List<Pawn>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (cellSet.Contains(pawn.Position))
                {
                    pawnsToMove.Add(pawn);
                }
            }

            foreach (var pawn in pawnsToMove)
            {
                var dest = FindNearestWalkable(map, pawn.Position, 10);
                if (dest.IsValid)
                {
                    // 直写 Position 安全：Thing.Position setter 自动在 thingGrid/coverGrid 重注册（Pawn 无覆写）。
                    pawn.Position = dest;
                }
                else
                {
                    if (!pawn.RaceProps.Humanlike)
                    {
                        pawn.Destroy(DestroyMode.Vanish);
                    }
                    else
                    {
                        Log.Warning($"[RimExodus] Humanlike pawn {pawn.LabelShort} has no walkable cell within 10 of {pawn.Position} after void fill; leaving in place.");
                    }
                }
            }
        }

        private static IntVec3 FindNearestWalkable(Map map, IntVec3 center, int maxRadius)
        {
            var cellCount = GenRadial.NumCellsInRadius(maxRadius);
            for (var i = 0; i < cellCount; i++)
            {
                var c = center + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && c.Walkable(map))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
