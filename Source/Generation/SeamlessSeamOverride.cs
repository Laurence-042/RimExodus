using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace RimExodus
{
    /// <summary>
    /// 接缝覆写（3 圈接缝带 + 连续边中点对齐 + 实时地图/基础快照参考，权威定义见 doc/接缝带定义.md）。
    ///
    /// 【混合范围】枚举 C（新生成 tile）自己的接缝带 B ∪ 过渡带 T（显式几何集合，
    /// <see cref="SeamlessPolygonGeometry.BuildSeamBand"/> 缓存）。旧方案"从 A 全图 snapshot 枚举候选"
    /// 已废弃——A 方形角落格绕六边形顶点投影进 C 侧向楔形区的污染结构性消失（只枚举 C 的带格，
    /// 每格反向找邻居参考）。带外圈（新定义下为实地形）也参与混合，跨缝两侧地形成对一致。
    ///
    /// 【混合规则（用户定夺 2026-08，规则轴 = 本端圈层，对端只提供数据不参与规则判定）】
    /// - **B_C（接缝带三圈）→ 字面照抄对端对应格**：a = c − offset，有参考即抄地形与岩体
///   （岩体用对端 def——跨缝岩色连续；地形未变时岩体同步仍执行）。无任何地形例外（对端是
///   Marsh/深水本端就是——不能走 pawn 自然绕路）。错位时 a 落在对端哪个圈层无所谓，
    ///   参考域覆盖 B_A∪限定深度 T_A∪外条带，照抄天然免疫错位（历史：按对端圈层分规则的版本需要
    ///   B_A/T_A/外条带/核心区四条判定 + 错位补偿补丁，已废弃勿回退）。参考优先读**对端当前
    ///   实况**；映射格已被裁成 void 或不可读时回落 389 清理前的完整基础快照，从而既反映玩家
    ///   后续修改，又保留 void 之下的自然山体延续。一切参考只认 a = c − offset 渲染映射
    ///   （地图偏移带旋转，切比雪夫圈对应 ≠ 渲染映射对应）；
    /// - **void_C → 不在枚举范围**（直接用自己的 void）；
    /// - **T_C（过渡带）→ 卷积权重覆盖（源地图轴权重）**：过渡带数据驱动（a 在邻居参考域内
    ///   即参与，外条带全深到源方形边）。w = dSq/(dSq+dOut)——dOut = a 距源接缝带切比雪夫
    ///   深度（贴缝≈0），dSq = a 到源方形边切比雪夫距离：**接近源六边形权重高，到源方形边
    ///   权重渐近 0**——混合范围截止边界（源方形边）恰好是权重归零处，自然闭合无锐利边缘；
    ///   乘性 dither（端点 0/1 不动）打散等值线。历史教训（勿回退）：曾按"本端距接缝带固定
    ///   深度衰减 + 外条带限深 7 格"，数据边界处权重残值戛然而止，源方形边在 C 上投影成
    ///   一条直线。self 分布（C 当前 topGrid 3×3）+ 各参考分布（实时/基础快照 3×3）→ 众数覆写；
    ///   **岩体/屋顶跟随主导参考（w 最大）的 def**——与照抄区语义统一（地形走卷积混合，
    ///   离散层跟随参考；地形驱动 spawn 会在参考无岩体处生成本端岩体，2026-08 用户实测）。
    /// 顶点楔形区（B_C 多邻居命中）保留卷积合成（几何模糊地带，各 w=1）。
    ///
    /// 【参考源（所有实际加载邻图）】B 与限定深度 T 优先读当前 terrain/rock/roof；当前格为
    /// null/void 时回落完整基础快照，外条带直接读基础快照。无 Map 的封存图不参与参考。
    /// offset 现算（<see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/>，与邻居表登记
    /// 同公式恒等）——genStep 392 运行时邻居表尚未登记（RegisterNeighborBidirectional 在
    /// onComplete，晚于整个 genStep 链），运行时消费方（传送/渲染）才走邻居表。
    ///
    /// 【保护判据：特征级例外（道路/走廊/河流），无地形类型例外（2026-08-30 河流并入，用户定夺）】
    /// SeamOverride 只做三件事：按卷积权重覆盖、随机化边缘（dither）、特征保护。窄特征（1-2 格宽）
    /// 在 3×3 众数卷积里必然是少数派被抹掉，且由各自 patch 权威生成：①本格 IsRoad/bridge 跳过；
    /// ②<see cref="SeamlessRoadPaths"/> ±3 格切比雪夫缓冲兜底无 tag 路面；③中心走廊精确足迹
    /// （CorridorCellsComponent）；④**河流实际修改格**（<see cref="SeamlessRiverCells"/>，2026-08-31
    /// v3 抽象——河生成期不进 void、修改格免混合；替代 v2 的 riverGraph 走廊近似）。**地形类型无
    /// 例外**：水体、沼泽等一切非河特征地形照常参与混合与拷贝——参考位置由连续边中点对齐保证精确，
    /// 对端是水体本端就是水体（不能走 pawn 自然绕路）；历史的水体例外（本格 IsWater 跳过、卷积采样
    /// 跳水）是旧 offset ±2 格系统误差下"防水蔓延"的补丁，中点对齐后不成立（其间还因 HasTag 前缀
    /// 匹配误伤 Marsh 造成接缝断裂）——河的保护是**实际修改格级**，非水体类型级，海岸/沼泽不受影响。
    ///
    /// 【三层归一框架（用户定夺 2026-08）】terrain/building/roof 三层共用同一混合框架：
    /// 照抄区 = 每层读对端参考值（<see cref="seamLayers"/>.ReadReference）→ 与本端不同（ReadLocal）则写
    /// （Write，委托内含必要守卫）；卷积区 = terrain（唯一连续层）走加权众数，离散层跟随主导
    /// 参考。层差异全部收在 <see cref="seamLayers"/> 的三个委托里，新增层只需追加一项。
    ///
    /// 【单向】只改 C，不改 A（先生成者原生，后生成者服从——A 生成时 C 还不存在是自然序）。
    ///
    /// 【为什么卷积】terrainDef 离散，不能直接加权平均。卷积把每格 terrainDef 变成
    /// "周围 3×3 邻域的 terrainDef 分布"，分布可以加权平均，再取众数 → 平滑过渡。
    /// </summary>
    public static class SeamlessSeamOverride
    {
        /// <summary>邻居参考源（生成期预收集，静态复用避免分配）。规则轴=本端圈层，对端带几何不参与。</summary>
        private struct NeighborRef
        {
            public int worldTile;
            public IntVec3 offset;
            public Map map;
            public SeamlessPolygonGeometry.SeamBandInfo band;
            public TerrainDef[] baseTerrain;
            public ThingDef[] baseBuilding;
            public RoofDef[] baseRoof;
        }

        /// <summary>单格的邻居参考（对齐格 a + 权重 + 所属邻居；三层参考按层实时读取）。</summary>
        private struct CellRef
        {
            public float w;
            public NeighborRef owner;
            public IntVec3 aCell;
        }

        /// <summary>
        /// 混合层操作集（**三层归一框架**，用户定夺 2026-08）：terrain/building/roof 三层共用同一
        /// 混合框架（照抄 = 读对端参考值 → 与本端不同则写；卷积区见主循环），层差异全部收进委托：
        /// <see cref="ReadLocal"/>（读本端）、<see cref="ReadReference"/>（读对端实时/基础参考）、<see cref="Write"/>
        /// （写本端，委托内部含必要守卫——如岩石 spawn 的 existing==null 检查、屋顶判等）。
        /// 新增层（如植物）只需在此追加一项。
        /// </summary>
        private sealed class SeamLayer
        {
            public readonly string Name;
            /// <summary>读本端当前值。</summary>
            public readonly Func<Map, IntVec3, Def> ReadLocal;
            /// <summary>读对端当前实况或基础快照参考值。</summary>
            public readonly Func<NeighborRef, IntVec3, (bool has, Def value)> ReadReference;
            /// <summary>写本端。</summary>
            public readonly Action<Map, IntVec3, Def> Write;

            public SeamLayer(string name, Func<Map, IntVec3, Def> readLocal,
                Func<NeighborRef, IntVec3, (bool has, Def value)> readReference, Action<Map, IntVec3, Def> write)
            {
                Name = name;
                ReadLocal = readLocal;
                ReadReference = readReference;
                Write = write;
            }
        }

        /// <summary>三层实例：terrain（连续层，卷积区走加权众数）/ building（岩石体 def）/ roof（RoofDef）。</summary>
        private static readonly SeamLayer[] seamLayers =
        {
            new("terrain",
                (m, c) => m.terrainGrid.topGrid[m.cellIndices.CellToIndex(c)],
                (r, a) => ReadReference(r, a, ReferenceLayer.Terrain),
                (m, c, v) =>
                {
                    m.terrainGrid.topGrid[m.cellIndices.CellToIndex(c)] = (TerrainDef)v;
                    m.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
                }),
            new("building",
                (m, c) => RockDefAt(m, c),
                (r, a) => ReadReference(r, a, ReferenceLayer.Building),
                (m, c, v) => SyncRockBuildingTo(m, c, (ThingDef)v, out _)),
            new("roof",
                (m, c) => m.roofGrid.RoofAt(c),
                (r, a) => ReadReference(r, a, ReferenceLayer.Roof),
                (m, c, v) => SyncRoofTo(m, c, (RoofDef)v)),
        };

        // MapPreview 会在后台线程运行 392；这些复用容器必须线程隔离，不能与主线程正式生成互踩。
        [ThreadStatic] private static List<NeighborRef> neighborRefs;
        [ThreadStatic] private static HashSet<IntVec3> bandCells;
        [ThreadStatic] private static List<CellRef> cellRefs;
        [ThreadStatic] private static List<(Dictionary<TerrainDef, float> dist, float w)> blendParts;
        private static readonly HashSet<IntVec3> EmptyCells = new();

        private enum ReferenceLayer { Terrain, Building, Roof }
        private enum ReferenceSource { Missing, Current, BaseSnapshot }
        private static TerrainDef cachedVoidDef;
        private static TerrainDef VoidDef => cachedVoidDef ??= DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");

        private static void EnsureScratchBuffers()
        {
            neighborRefs ??= new List<NeighborRef>();
            bandCells ??= new HashSet<IntVec3>();
            cellRefs ??= new List<CellRef>();
            blendParts ??= new List<(Dictionary<TerrainDef, float> dist, float w)>();
        }

        /// <summary>
        /// 对 map 的所有已生成邻居做单向接缝覆写（只改 map 自身，不改邻居）。
        /// 在 GenStep_SeamOverride.Generate 里调用（order=392，接缝带 void 裁切 389 之后、
        /// Settlement 400 与 Fog 1500 之前）。
        /// </summary>
        public static void ApplyOneWay(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;
            EnsureScratchBuffers();

            var mapSize = map.Size.x;

            // 权重噪声：低频空间 Perlin，dither 掉衰减区 GetMode 离散跳变（把规则等距过渡线
            // 打散成弯曲斑块）。基于 worldTile 的稳定 seed → 同一地块多次生成噪声一致。
            // 只作用于 w<1 的衰减区（完全一致区保持字面一致）。
            var noiseAmp = RimExodusMod.Settings?.seamOverrideNoiseAmplitude ?? 0.15f;
            Perlin weightNoise = null;
            if (noiseAmp > 0f)
            {
                weightNoise = new Perlin(0.04f, 2.0, 0.5, 4, worldTile * 31 + 7919, QualityMode.Medium);
            }

            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);
            if (band.Band.Count == 0) return;

            var voidDef = VoidDef;
            var cellIndices = map.cellIndices;
            var topGrid = map.terrainGrid.topGrid;
            var mapDrawer = map.mapDrawer;
            var verbose = RimExodusLog.Enabled(RimExodusLogModule.Generation);
            var written = 0;

            // C 的道路保护集：GenStep_Roads 路径快照 ±3 格缓冲（覆盖 Bezier 平滑相对 A* 折线的偏离）。
            // 窄路（1-2 格宽）在 3×3 卷积里永远是少数派，不保护会被周围地形卷没。
            var roadGuard = BuildRoadGuard(map);

            CollectNeighborRefs(map, worldTile);
            if (neighborRefs.Count == 0) return;

            // 主循环：枚举 C 的接缝带 B ∪ 过渡带 T。**规则轴 = 本端圈层**（对端只提供数据，
            // 不参与规则判定——错位天然免疫）：
            // - B_C（三圈）→ 字面照抄对端对应格的地形与岩体（a = c − offset，有参考即抄）；
            // - T_C（过渡带）→ 卷积权重覆盖（w 按本端过渡深度衰减 + dither）；
            // - void_C → 不在枚举范围（直接用自己的 void）。
            // 顶点楔形区（B_C 多邻居命中）保留卷积合成（几何模糊地带，各 w=1）。
            bandCells.Clear();
            bandCells.UnionWith(band.Band);
            bandCells.UnionWith(band.TransitionBand);

            foreach (var cCell in bandCells)
            {
                var cIdx = cellIndices.CellToIndex(cCell);
                var localTerrain = topGrid[cIdx];
                if (localTerrain == null || (voidDef != null && localTerrain == voidDef)) continue;

                // 道路保护①（地形判据，精确）：本格已是路/桥地形 → 不混合。窄路（1-2 格宽）
                // 在 3×3 卷积里永远是少数派，混合必然失真；路面可铺到距 A* 中线 2-3 格处
                // （材质曲线 fromRoad≤1.4 + 抗锯齿）+ Bezier 偏离折线最多 3-4 格，仅靠路径
                // 缓冲判据会漏（用户实测：snapshot=BrokenAsphalt 的格被卷成 Sand）。
                if (localTerrain.IsRoad || localTerrain.bridge) continue;
                // 道路保护②（路径缓冲判据，兜底）：A* 路径 ±3 格内的格不混合——覆盖
                // Gravel 等无 Road tag 的路面（曲线同样到 fromRoad 1.4）。
                if (roadGuard.Contains(cCell)) continue;

                var inBand = band.Band.Contains(cCell);

                // 照抄区 = B ∪ {T depth=1}（2026-08 用户逻辑，距缝切比雪夫 0/1/2 三带完全参考对端）：
                // T depth=1（本图带内侧一圈）与对侧 OuterStrip depth=1（对侧 void 第一圈）是同一条
                // 空间带——先生成侧在该 void 带上按 own snapshot 放的 void rock，要求后生成侧在
                    // 镜像位置"直接复刻"（含岩体，对端外条带基础快照=清 void 前原生岩 = 山体延续），
                // 本图 T depth=1 因此不走卷积、与 B 同款字面照抄。depth≥2 的 T 仍走卷积
                //（对端无对应义务，渐进混合归位）。
                var isCopyZone = inBand
                    || (band.TransitionDepth.TryGetValue(cCell, out var tDepth) && tDepth == 1);

                if (!CollectCellRefs(cCell, isCopyZone, weightNoise, noiseAmp)) continue;

                if (isCopyZone && cellRefs.Count == 1)
                {
                    // 照抄区（B 或 T·1）：三层统一框架——每层读对端参考值，与本端不同则写。
                    // 无任何地形例外（对端是 Marsh/深水本端就是——不能走 pawn 自然绕路）；
                    // 岩体用对端 def（跨缝岩色连续，"岩石地形无岩体"中间带状态正确继承）；
                    // 屋顶照抄对端岩顶（Thick/Thin，不裸顶）。地形未变时岩体/屋顶同步仍执行。
                    // 参考一律 = 对端生成时 snapshot 的渲染映射格值（B∪T 段=定格实况 /
                    // 外条带段=389 清 void 前原生——T·1 复刻对端 void 之下的自然山体延续，
                    // 不是清除后的实际；曾改存 null"实际状态"误杀真实山体，2026-08 回退）。
                    var ref0 = cellRefs[0];
                    var anyWritten = false;
                    foreach (var layer in seamLayers)
                    {
                        var read = layer.ReadReference(ref0.owner, ref0.aCell);
                        if (!read.has) continue;
                        var target = read.value;
                        if (ReferenceEquals(target, layer.ReadLocal(map, cCell))) continue;
                        layer.Write(map, cCell, target);
                        anyWritten = true;
                    }
                    if (anyWritten) written++;
                }
                else
                {
                    // 过渡带（或顶点楔形多参考）：terrain 是唯一连续层，走卷积加权众数；
                    // 离散层（building/roof）无法加权，跟随主导参考（w 最大）的 def——
                    // 仅在 terrain 被参考覆写时同步（地形没变 = 本端已与参考一致或参考权重弱，
                    // 不动离散层）。历史教训（2026-08 用户实测）：地形驱动 spawn 会在参考
                    // 无岩体处生成本端岩体——离散层必须跟随参考而非地形。
                    CollectBlendParts(topGrid, mapSize, voidDef, cCell);
                    if (blendParts.Count == 0) continue;
                    var blended = BlendDistributions(blendParts);
                    var chosenTerrain = GetMode(blended);
                    if (chosenTerrain == null || ReferenceEquals(chosenTerrain, seamLayers[0].ReadLocal(map, cCell))) continue;

                    seamLayers[0].Write(map, cCell, chosenTerrain);
                    var dominant = DominantRef(cellRefs);
                    // 离散层跟随主导参考的渲染映射格值（带内=当前实况 / void 外条带=基础快照）。
                    for (var i = 1; i < seamLayers.Length; i++)
                    {
                        var read = seamLayers[i].ReadReference(dominant.owner, dominant.aCell);
                        if (read.has) seamLayers[i].Write(map, cCell, read.value);
                    }
                    written++;
                }
            }

            // 逐格诊断不打日志（B∪T 数千格会刷屏）——用 Dev 探针 InspectSnapshotAtPosition 单格重放。
            if (verbose)
                Log.Message($"[RimExodus] SeamOverride map={map.uniqueID}(wt={worldTile}) refs={neighborRefs.Count} written={written} cells.");
        }

        /// <summary>
        /// 预收集参考源到 <see cref="neighborRefs"/>：C 的每个世界邻居中实际有 Map 的邻居，
        /// 附 offset（现算，与邻居表登记同公式恒等）、邻居带几何和完整基础快照。
        /// </summary>
        private static void CollectNeighborRefs(Map map, int worldTile)
        {
            neighborRefs.Clear();
            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(worldTile, worldNeighbors);
            foreach (var nt in worldNeighbors)
            {
                var neighborTile = nt.tileId;
                if (neighborTile == worldTile) continue;
                Map neighborMap = null;
                foreach (var candidate in Find.Maps)
                {
                    if (candidate == null || candidate.Disposed) continue;
                    if (SeamlessTileRegistry.GetMapWorldTile(candidate) == neighborTile)
                    {
                        neighborMap = candidate;
                        break;
                    }
                }
                if (neighborMap == null) continue;

                var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTile, map, neighborMap);
                if (offset == IntVec3.Zero) continue;

                neighborRefs.Add(new NeighborRef
                {
                    worldTile = neighborTile,
                    offset = offset,
                    map = neighborMap,
                    band = SeamlessPolygonGeometry.BuildSeamBand(neighborTile, neighborMap.Size.x),
                    baseTerrain = SeamlessMapData.GetBaseTerrainSnapshot(neighborMap),
                    baseBuilding = SeamlessMapData.GetBaseBuildingSnapshot(neighborMap),
                    baseRoof = SeamlessMapData.GetBaseRoofSnapshot(neighborMap)
                });
            }
        }

        /// <summary>
        /// 收集 cCell 的各邻居参考到 <see cref="cellRefs"/>（复用 <see cref="neighborRefs"/>），
        /// 返回是否有参考。**规则轴 = 本端圈层**（用户定夺 2026-08）：
        /// - c ∈ 照抄区（B_C 或 T·1）：命中 = a 在邻居参考域内（有数据即命中），
        ///   w = 1（仅多命中合成时用到）；
        /// - c ∈ T_C depth≥2（卷积区）：w = dSq/(dSq+dOut)（源地图轴：接近源六边形高 → 源方形边 0）
        ///   ×乘性 dither。
        /// </summary>
        private static bool CollectCellRefs(IntVec3 cCell, bool inCopyZone, Perlin weightNoise, float noiseAmp)
        {
            cellRefs.Clear();
            foreach (var nref in neighborRefs)
            {
                // 契约 neighborLocal + offset = myLocal → 邻居格 a = c − offset（同一空间位置：
                // 中点对齐保证镜像格即重叠格。2026-08 曾按用户提议内移对端边法向 1 格，游戏内
                // 实测整缝系统性偏 1 格——本侧带抄"对端同一位置再往里 1 格"的值，图案向本侧
                // 平移；已回退，勿再加参考偏移）。
                var aCell = new IntVec3(cCell.x - nref.offset.x, 0, cCell.z - nref.offset.z);
                // terrain 决定此格是否有参考；building/roof 的 null 是明确的“无”，不能当缺数据。
                if (!seamLayers[0].ReadReference(nref, aCell).has) continue;

                float wRaw;
                if (inCopyZone)
                {
                    wRaw = 1f;
                }
                else
                {
                    // T_C 卷积区权重（**源地图轴**，用户定夺 2026-08 勿回退为本端深度衰减）：
                    // a 接近源六边形（接缝带）→ 高；a 逼近源方形边 → 渐近 0。
                    // dOut = a 距源接缝带的切比雪夫深度（B/T=0），dSq = a 到源方形边的切比雪夫距离。
                    // w = dSq/(dSq+dOut)：贴缝（dOut≈0）→ 1；源方形边（dSq=0）→ 0——混合范围的
                    // 截止边界（A 方形边）恰好是权重归零处，自然闭合无锐利边缘（历史教训：
                    // 按本端固定深度衰减 + 外条带限深 7 格，数据边界处权重残值 ~0.14 戛然而止，
                    // 源方形边在 C 上投影成一条直线）。乘性 dither（端点 0/1 不动）打散等值线。
                    var nSize = nref.map.Size.x;
                    var dSq = Mathf.Min(Mathf.Min(aCell.x, aCell.z),
                        Mathf.Min(nSize - 1 - aCell.x, nSize - 1 - aCell.z));
                    var dOut = ReferenceDepth(nref, aCell);
                    var wBase = (dSq + dOut) > 1e-3f ? (float)dSq / (dSq + dOut) : 0f;
                    wRaw = weightNoise != null
                        ? Mathf.Clamp01(wBase * (1f + (float)weightNoise.GetValue(cCell) * noiseAmp))
                        : wBase;
                    if (wRaw <= 0f) continue;
                }

                cellRefs.Add(new CellRef { w = wRaw, owner = nref, aCell = aCell });
            }
            return cellRefs.Count > 0;
        }

        /// <summary>权重最大的参考（卷积区离散层跟随它）。</summary>
        private static CellRef DominantRef(List<CellRef> refs)
        {
            var best = refs[0];
            for (var i = 1; i < refs.Count; i++)
            {
                if (refs[i].w > best.w) best = refs[i];
            }
            return best;
        }

        /// <summary>
        /// 读取邻图在映射格上的权威参考。接缝带/浅层过渡带优先用当前实况；当前格已是 void
        /// 或不可读时回落 389 基础快照。void 侧外条带始终读基础快照。返回 has=true,value=null
        /// 表示“明确无岩/无顶”，与 has=false（没有参考数据）严格区分。
        /// </summary>
        private static (bool has, Def value) ReadReference(NeighborRef nref, IntVec3 cell, ReferenceLayer layer)
        {
            if (nref.map == null || !cell.InBounds(nref.map)) return (false, null);

            var inLiveBand = nref.band.Band.Contains(cell)
                || (nref.band.TransitionDepth.TryGetValue(cell, out var td)
                    && td <= SeamlessPolygonGeometry.SeamStripInnerDepth);
            var inOuter = nref.band.OuterStripDepth.ContainsKey(cell);
            if (!inLiveBand && !inOuter) return (false, null);

            if (inLiveBand)
            {
                var terrain = nref.map.terrainGrid.topGrid[nref.map.cellIndices.CellToIndex(cell)];
                if (terrain != null && terrain != VoidDef)
                {
                    return layer switch
                    {
                        ReferenceLayer.Terrain => (true, terrain),
                        ReferenceLayer.Building => (true, RockDefAt(nref.map, cell)),
                        ReferenceLayer.Roof => (true, nref.map.roofGrid.RoofAt(cell)),
                        _ => (false, null)
                    };
                }
            }

            var idx = nref.map.cellIndices.CellToIndex(cell);
            if (nref.baseTerrain == null || idx < 0 || idx >= nref.baseTerrain.Length
                || nref.baseTerrain[idx] == null)
                return (false, null);
            return layer switch
            {
                ReferenceLayer.Terrain => (true, nref.baseTerrain[idx]),
                ReferenceLayer.Building => nref.baseBuilding != null && idx < nref.baseBuilding.Length
                    ? (true, nref.baseBuilding[idx]) : (false, null),
                ReferenceLayer.Roof => nref.baseRoof != null && idx < nref.baseRoof.Length
                    ? (true, nref.baseRoof[idx]) : (false, null),
                _ => (false, null)
            };
        }

        private static ReferenceSource GetReferenceSource(NeighborRef nref, IntVec3 cell)
        {
            if (nref.map == null || !cell.InBounds(nref.map)) return ReferenceSource.Missing;
            var inLiveBand = nref.band.Band.Contains(cell)
                || (nref.band.TransitionDepth.TryGetValue(cell, out var td)
                    && td <= SeamlessPolygonGeometry.SeamStripInnerDepth);
            if (inLiveBand)
            {
                var terrain = nref.map.terrainGrid.topGrid[nref.map.cellIndices.CellToIndex(cell)];
                if (terrain != null && terrain != VoidDef) return ReferenceSource.Current;
            }
            return ReadReference(nref, cell, ReferenceLayer.Terrain).has
                ? ReferenceSource.BaseSnapshot : ReferenceSource.Missing;
        }

        private static int ReferenceDepth(NeighborRef nref, IntVec3 cell)
        {
            if (nref.band.Band.Contains(cell)) return 0;
            if (nref.band.TransitionDepth.TryGetValue(cell, out var inner)) return inner;
            return nref.band.OuterStripDepth.TryGetValue(cell, out var outer) ? outer : 0;
        }

        internal static ThingDef RockDefAt(Map map, IntVec3 cell)
        {
            var edifice = cell.GetEdifice(map);
            return edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock
                ? edifice.def
                : null;
        }

        internal static bool TryReadRockReference(Map neighborMap, int neighborWorldTile, IntVec3 cell,
            out ThingDef rockDef)
        {
            rockDef = null;
            if (neighborMap == null || neighborMap.Disposed) return false;
            var nref = new NeighborRef
            {
                worldTile = neighborWorldTile,
                map = neighborMap,
                band = SeamlessPolygonGeometry.BuildSeamBand(neighborWorldTile, neighborMap.Size.x),
                baseTerrain = SeamlessMapData.GetBaseTerrainSnapshot(neighborMap),
                baseBuilding = SeamlessMapData.GetBaseBuildingSnapshot(neighborMap),
                baseRoof = SeamlessMapData.GetBaseRoofSnapshot(neighborMap)
            };
            var read = ReadReference(nref, cell, ReferenceLayer.Building);
            rockDef = read.value as ThingDef;
            return read.has;
        }

        /// <summary>
        /// 组装卷积合成输入到 <see cref="blendParts"/>：self 分布（C 当前 topGrid，selfW>0 时）+
        /// 各邻居参考分布（实时地图/基础快照 3×3 采样）。
        /// </summary>
        private static void CollectBlendParts(TerrainDef[] topGrid, int mapSize, TerrainDef voidDef, IntVec3 cCell)
        {
            blendParts.Clear();
            var totalW = 0f;
            foreach (var r in cellRefs) totalW += r.w;
            var selfW = Mathf.Clamp01(1f - totalW);
            if (selfW > 0f)
            {
                var selfDist = Convolve3x3(topGrid, mapSize, voidDef, cCell);
                if (selfDist != null) blendParts.Add((selfDist, selfW));
            }
            foreach (var r in cellRefs)
            {
                var refDist = Convolve3x3FromReference(r.owner, r.aCell);
                if (refDist != null) blendParts.Add((refDist, r.w));
            }
        }

        /// <summary>
        /// 构建 C 的特征保护集（道路/中心走廊/河流）：
        /// ① <see cref="SeamlessRoadPaths"/> 快照的 A* 路径节点，每节点 ±<see cref="RoadGuardRadius"/> 格
        /// 切比雪夫膨胀——有 Road tag / bridge 的路面由混合循环的地形判据精确保护，本缓冲兜底
        /// Gravel 等无 tag 路面；
        /// ② 中心走廊足迹（2026-08-30）；
        /// ③ **河流实际修改格**（2026-08-31 v3 抽象：<see cref="SeamlessRiverCells"/>——生成期
        /// RiverTerrainAt/RiverBankTerrainAt 返回非 null 的格集，即河真实铺过的水+岸格）。
        /// 跨地块结构（河/路）不可被混合侵犯——照抄与卷积分流都跳过（用户定夺：地面类型/岩壁
        /// 可混合调整，跨地块结构不可）。实际修改格比 v2 的"riverGraph 中线±halfWidth"走廊精确
        /// （走廊已删——v3 中线延伸到方形边后几何近似失真）。
        /// </summary>
        private static HashSet<IntVec3> BuildRoadGuard(Map map)
        {
            var guard = new HashSet<IntVec3>();
            var comp = map.GetComponent<SeamlessRoadPaths>();
            // 中心走廊足迹（2026-08-30）：走廊格免疫混合（照抄 + 卷积）——N 侧 void 下的基础快照
            // 是清 void 前的原生连绵山体，照抄区会把岩体 spawn 回 B 的接缝带压掉走廊末端。
            // 精确格集（WriteCaveDisc 记录的整条走廊宽度足迹），无需道路那样的 ±3 缓冲
            // （那是 Bezier 偏离 + 无 tag 路面半宽的兜底，走廊足迹无此近似）。
            guard.UnionWith(map.GetComponent<SeamlessCenterCorridor.CorridorCellsComponent>()?.cells ?? EmptyCells);
            guard.UnionWith(map.GetComponent<SeamlessRiverCells>()?.cells ?? EmptyCells);
            if (comp == null) return guard;

            foreach (var path in comp.paths)
            {
                foreach (var node in path)
                {
                    // 切比雪夫半径 RoadGuardRadius 方形窗口（R>1 无原版数组可用，保留显式双层循环，
                    // 口径指回 SeamlessGridMath——与 3×3 窗口的 GenAdj.AdjacentCellsAndInside 同族）。
                    for (var dx = -RoadGuardRadius; dx <= RoadGuardRadius; dx++)
                    {
                        for (var dz = -RoadGuardRadius; dz <= RoadGuardRadius; dz++)
                        {
                            var c = new IntVec3(node.x + dx, 0, node.z + dz);
                            if (c.InBounds(map)) guard.Add(c);
                        }
                    }
                }
            }
            return guard;
        }

        /// <summary>
        /// 道路路径缓冲保护半径（格，切比雪夫）。3 = Bezier 偏离折线（最多 3-4 格）+
        /// 无 tag 路面半宽（Gravel 曲线到 fromRoad 1.4）。有 Road tag / bridge 的路面
        /// 由地形判据（IsRoad/bridge）精确保护，本缓冲只兜底无 tag 路面。
        /// </summary>
        private const int RoadGuardRadius = 3;

        /// <summary>
        /// 3×3 卷积（数组源，self 用）：统计 center 周围 3×3 邻域（含自身）每种 terrainDef 的占比。
        /// 越界格 clamp 到边界格（假设边界外与边界格同地形，消除矩形边界处窗口截断）。
        /// void 格跳过（C 当前 topGrid 可能含 void）。**无地形例外**——水体照常参与分布
        /// （对端是水体本端就是水体，岸线由卷积窗口自然平滑过渡）。
        /// 返回归一化占比（和=1），total=0 返回 null。
        /// </summary>
        private static Dictionary<TerrainDef, float> Convolve3x3(TerrainDef[] snapshot, int mapSize, TerrainDef voidDef, IntVec3 center)
        {
            var counts = new Dictionary<TerrainDef, int>();
            var total = 0;

            // 3×3 窗口遍历 = GenAdj.AdjacentCellsAndInside（SeamlessGridMath 统一口径，勿手搓 dx/dz）。
            var window = GenAdj.AdjacentCellsAndInside;
            for (var i = 0; i < window.Length; i++)
            {
                var nx = center.x + window[i].x;
                if (nx < 0) nx = 0; else if (nx >= mapSize) nx = mapSize - 1;
                var nz = center.z + window[i].z;
                if (nz < 0) nz = 0; else if (nz >= mapSize) nz = mapSize - 1;
                var idx = nz * mapSize + nx;
                var t = snapshot[idx];
                if (t == null) continue;
                if (voidDef != null && t == voidDef) continue;
                counts.TryGetValue(t, out var c);
                counts[t] = c + 1;
                total++;
            }

            if (total == 0) return null;
            var result = new Dictionary<TerrainDef, float>(counts.Count);
            foreach (var kv in counts)
                result[kv.Key] = (float)kv.Value / total;
            return result;
        }

        /// <summary>
        /// 3×3 卷积（邻居参考用）：采样实时地图/基础快照提供器中 center 周围 3×3 邻域。
        /// 窗口越出参考域的格跳过（不 clamp——参考域外无数据，与数组源的矩形边界
        /// 语义不同）。**无地形例外**（同 <see cref="Convolve3x3"/>，水体照常参与）。
        /// total=0 返回 null。
        /// </summary>
        private static Dictionary<TerrainDef, float> Convolve3x3FromReference(NeighborRef nref, IntVec3 center)
        {
            Dictionary<TerrainDef, int> counts = null;
            var total = 0;

            // 3×3 窗口遍历 = GenAdj.AdjacentCellsAndInside（SeamlessGridMath 统一口径）。
            var window = GenAdj.AdjacentCellsAndInside;
            for (var i = 0; i < window.Length; i++)
            {
                var read = ReadReference(nref,
                    new IntVec3(center.x + window[i].x, 0, center.z + window[i].z), ReferenceLayer.Terrain);
                if (!read.has || read.value is not TerrainDef t) continue;
                counts ??= new Dictionary<TerrainDef, int>();
                counts.TryGetValue(t, out var c);
                counts[t] = c + 1;
                total++;
            }

            if (total == 0) return null;
            var result = new Dictionary<TerrainDef, float>(counts.Count);
            foreach (var kv in counts)
                result[kv.Key] = (float)kv.Value / total;
            return result;
        }

        /// <summary>N 路分布加权混合：result[t] = Σ dist_i[t] × w_i（加权和可能 >1，GetMode 只比较相对大小）。</summary>
        private static Dictionary<TerrainDef, float> BlendDistributions(List<(Dictionary<TerrainDef, float> dist, float w)> parts)
        {
            var result = new Dictionary<TerrainDef, float>();
            foreach (var (dist, w) in parts)
            {
                foreach (var kv in dist)
                {
                    result.TryGetValue(kv.Key, out var v);
                    result[kv.Key] = v + kv.Value * w;
                }
            }
            return result;
        }

        /// <summary>
        /// 取分布中占比最大的 terrainDef（众数）。平局时按 defName 稳定决胜（消除 Dictionary 遍历序的随机性，
        /// 避免相邻 cell 因遍历序不同而在占比相同时翻转，产生斑驳）。
        /// </summary>
        private static TerrainDef GetMode(Dictionary<TerrainDef, float> dist)
        {
            TerrainDef best = null;
            var bestVal = -1f;
            foreach (var kv in dist)
            {
                if (kv.Value > bestVal + 1e-6f ||
                    (Mathf.Abs(kv.Value - bestVal) <= 1e-6f && best != null && string.Compare(kv.Key.defName, best.defName, System.StringComparison.Ordinal) < 0))
                {
                    bestVal = kv.Value;
                    best = kv.Key;
                }
            }
            return best;
        }

        /// <summary>
        /// 单格接缝结构化诊断报告（本侧 + 各对侧对应位置），供 InspectSnapshotAtPosition（Dev ToolMap）。
        /// 每侧三段：**位置**（坐标 + 圈层，以"接缝带中圈 = 离散边圈"为基准的相对圈数）/
        /// **生成时 snapshot**（389 三层原生备份，非序列化——读档后不可用时如实注明）/
        /// **当前实际**（会进 snapshot 的三层现值：topGrid 地面 / 岩石 edifice / roof）。
        /// 本侧另附**混合情况追踪**（权重 / 本侧 snapshot 各层 3×3 卷积 / 各对侧参考各层 3×3 卷积 /
        /// 各层混合结果）；对侧附本侧实际读取的来源（当前实况/基础快照/缺失）。
        /// 权重/卷积/混合复用 ApplyOneWay 同一实现（CollectCellRefs/DominantRef/GetMode）——
        /// 报告与真实逻辑共享同一份代码，不会因复刻而漂移。
        ///
        /// 失真声明：生成期混合的 self 卷积采样自当时 topGrid（= 原生值）；此处重放优先用 389 备份
        /// snapshot 采样，快照不可用（读档后）回落当前 topGrid 并注明——回答的是"以该数据源再跑一次
        /// 会怎样"。
        /// </summary>
        public static string DescribeCellReport(Map map, int worldTile, IntVec3 cell)
        {
            EnsureScratchBuffers();
            var sb = new System.Text.StringBuilder();
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, map.Size.x);
            var voidDef = VoidDef;
            var mapSize = map.Size.x;
            var idx = map.cellIndices.CellToIndex(cell);
            var localTerrain = map.terrainGrid.topGrid[idx];
            var localRock = RockDefAt(map, cell);
            var localRoof = map.roofGrid.RoofAt(cell);

            // ―― 本侧 ――
            var baseT = SeamlessMapData.GetBaseTerrainSnapshot(map);
            var baseB = SeamlessMapData.GetBaseBuildingSnapshot(map);
            var baseR = SeamlessMapData.GetBaseRoofSnapshot(map);

            sb.AppendLine("―― 本侧 ――");
            sb.AppendLine($"  位置: ({cell.x},{cell.z})  圈层: {RingLabel(band, cell)}");
            sb.AppendLine($"  生成时 snapshot:  {LayerTriple(SnapAt(baseT, idx), SnapAt(baseB, idx), SnapAt(baseR, idx))}{(baseT != null ? "" : "  (非序列化，读档后不可用)")}");
            sb.AppendLine($"  当前实际:         {LayerTriple(localTerrain, localRock, localRoof)}");

            // 参考收集（混合追踪与对侧栏共用；被跳过的格也展示对侧对应位置）。
            var inMixRange = band.Band.Contains(cell) || band.TransitionBand.Contains(cell);
            var tDepth = band.TransitionDepth.TryGetValue(cell, out var td) ? td : 0;
            var isCopyZone = band.Band.Contains(cell) || tDepth == 1;
            var noiseAmp = RimExodusMod.Settings?.seamOverrideNoiseAmplitude ?? 0.15f;
            Perlin weightNoise = null;
            if (noiseAmp > 0f)
                weightNoise = new Perlin(0.04f, 2.0, 0.5, 4, worldTile * 31 + 7919, QualityMode.Medium);
            CollectNeighborRefs(map, worldTile);
            var hasRefs = neighborRefs.Count > 0 && CollectCellRefs(cell, isCopyZone, weightNoise, noiseAmp);

            // ―― 混合情况追踪（本侧）――
            sb.AppendLine("  混合情况追踪:");
            var roadComp = map.GetComponent<SeamlessRoadPaths>();
            var roadGuard = BuildRoadGuard(map);
            if (roadComp == null || roadComp.paths.Count == 0)
                sb.AppendLine("    (注意: SeamlessRoadPaths 为空——本图无路，或读档后快照丢失，道路保护②判据可能失真)");
            string skipReason = null;
            if (!inMixRange)
                skipReason = localTerrain == voidDef ? "本格为 void，不在混合枚举范围" : "核心区，不在混合枚举范围";
            else if (localTerrain == null)
                skipReason = "本格地形为 null";
            else if (localTerrain.IsRoad || localTerrain.bridge)
                skipReason = "道路保护①(IsRoad/bridge 地形判据)";
            else if (roadGuard.Contains(cell))
                skipReason = "道路保护②(A* 路径 ±3 格缓冲)";
            else if (!hasRefs)
                skipReason = "无已加载邻图参考（或对齐格不在参考区域内）";

            if (skipReason != null)
            {
                sb.AppendLine($"    被跳过: {skipReason} → 不混合，保持本侧当前值");
            }
            else
            {
                var totalW = 0f;
                foreach (var r in cellRefs) totalW += r.w;
                var selfW = Mathf.Clamp01(1f - totalW);
                sb.AppendLine($"    权重: self={selfW:F3}  {string.Join("  ", cellRefs.ConvertAll(r => $"wt={r.owner.worldTile} w={r.w:F3}"))}{(isCopyZone ? "（照抄区 B∪T·1，参考 w=1）" : "（卷积区 T·≥2，源地图轴权重 × dither）")}");

                // 本侧 snapshot 各层 3×3 卷积（快照缺失回落当前实际并注明）。
                var selfTerrDist = Convolve3x3(baseT ?? map.terrainGrid.topGrid, mapSize, baseT != null ? null : voidDef, cell);
                sb.AppendLine($"    本侧 snapshot 3×3:  地面: {FormatDist(selfTerrDist)}  岩体: {ConvolveLayer3x3(SelfReader(map, baseB, static (m, c) => RockDefAt(m, c)), cell)}  屋顶: {ConvolveLayer3x3(SelfReader(map, baseR, static (m, c) => c.GetRoof(m)), cell)}{(baseT != null ? "" : "  (snapshot 不可用，self 回落当前实际)")}");

                // 各对侧实时/基础快照组合参考的 3×3 卷积。
                foreach (var r in cellRefs)
                {
                    sb.AppendLine($"    对侧 wt={r.owner.worldTile} 参考 3×3:  地面: {FormatDist(Convolve3x3FromReference(r.owner, r.aCell))}  岩体: {ConvolveLayer3x3(ReferenceReader(r.owner, ReferenceLayer.Building), r.aCell)}  屋顶: {ConvolveLayer3x3(ReferenceReader(r.owner, ReferenceLayer.Roof), r.aCell)}  来源: {ReferenceSourceSummary(r.owner, r.aCell)}");
                }

                // 各层混合结果（照抄区单参考 = 逐层 ReadReference；卷积区 = terrain 加权众数 + 离散层跟随主导参考）。
                TerrainDef terrPart; ThingDef rockPart; RoofDef roofPart; string modeLabel;
                if (isCopyZone && cellRefs.Count == 1)
                {
                    var ref0 = cellRefs[0];
                    var readT = seamLayers[0].ReadReference(ref0.owner, ref0.aCell);
                    var readB = seamLayers[1].ReadReference(ref0.owner, ref0.aCell);
                    var readR = seamLayers[2].ReadReference(ref0.owner, ref0.aCell);
                    terrPart = readT.has ? (TerrainDef)readT.value : localTerrain;
                    rockPart = readB.has ? (ThingDef)readB.value : localRock;
                    roofPart = readR.has ? (RoofDef)readR.value : localRoof;
                    modeLabel = "照抄区单参考";
                }
                else
                {
                    blendParts.Clear();
                    if (selfW > 0f && selfTerrDist != null) blendParts.Add((selfTerrDist, selfW));
                    foreach (var r in cellRefs)
                    {
                        var refDist = Convolve3x3FromReference(r.owner, r.aCell);
                        if (refDist != null) blendParts.Add((refDist, r.w));
                    }
                    var dominant = DominantRef(cellRefs);
                    terrPart = blendParts.Count > 0 ? GetMode(BlendDistributions(blendParts)) : null;
                    var readB = seamLayers[1].ReadReference(dominant.owner, dominant.aCell);
                    var readR = seamLayers[2].ReadReference(dominant.owner, dominant.aCell);
                    rockPart = readB.has ? (ThingDef)readB.value : localRock;
                    roofPart = readR.has ? (RoofDef)readR.value : localRoof;
                    modeLabel = $"卷积区/多参考，离散层随主导 wt={dominant.owner.worldTile}";
                }
                sb.AppendLine($"    各层混合结果({modeLabel}):  {LayerTriple(terrPart, rockPart, roofPart)}{VerdictSuffix(terrPart, localTerrain, "地面")}{VerdictSuffix(rockPart, localRock, "岩体")}{VerdictSuffix(roofPart, localRoof, "屋顶")}");
            }

            // ―― 对侧对应位置（每个参考邻居一栏）――
            foreach (var r in cellRefs)
            {
                var nBand = r.owner.band;
                sb.AppendLine($"―― 对侧 wt={r.owner.worldTile}  对应格 ({r.aCell.x},{r.aCell.z})  offset={r.owner.offset} ――");
                sb.AppendLine($"  位置: ({r.aCell.x},{r.aCell.z})  圈层: {RingLabel(nBand, r.aCell)}");

                var nMap = r.owner.map;
                if (nMap != null && r.aCell.InBounds(nMap))
                {
                    var nIdx = nMap.cellIndices.CellToIndex(r.aCell);
                    var nBaseT = SeamlessMapData.GetBaseTerrainSnapshot(nMap);
                    var nBaseB = SeamlessMapData.GetBaseBuildingSnapshot(nMap);
                    var nBaseR = SeamlessMapData.GetBaseRoofSnapshot(nMap);
                    sb.AppendLine($"  生成时 snapshot:  {LayerTriple(SnapAt(nBaseT, nIdx), SnapAt(nBaseB, nIdx), SnapAt(nBaseR, nIdx))}{(nBaseT != null ? "" : "  (非序列化，读档后不可用)")}");
                    sb.AppendLine($"  当前实际:         {LayerTriple(nMap.terrainGrid.topGrid[nIdx], RockDefAt(nMap, r.aCell), nMap.roofGrid.RoofAt(r.aCell))}");
                }
                else
                {
                    sb.AppendLine("  图未加载或对应格越界 → 生成时 snapshot / 当前实际不可读");
                }

                var refT = seamLayers[0].ReadReference(r.owner, r.aCell);
                var refB = seamLayers[1].ReadReference(r.owner, r.aCell);
                var refR = seamLayers[2].ReadReference(r.owner, r.aCell);
                sb.AppendLine($"  本侧实际参考源[{ReferenceSourceLabel(GetReferenceSource(r.owner, r.aCell))}]:  {LayerTriple(refT.value, refB.value, refR.value)}{(!refT.has ? "  (无参考数据)" : "")}");
            }

            // 已加载邻图但本格缺参考时也必须显式报告；不能因 CollectCellRefs 跳过而让“缺失”
            // 从探针里消失，否则会再次把明确空值与数据缺口混淆。
            foreach (var nref in neighborRefs)
            {
                var alreadyShown = false;
                for (var i = 0; i < cellRefs.Count; i++)
                {
                    if (cellRefs[i].owner.worldTile == nref.worldTile) { alreadyShown = true; break; }
                }
                if (alreadyShown) continue;

                var aCell = new IntVec3(cell.x - nref.offset.x, 0, cell.z - nref.offset.z);
                if (!aCell.InBounds(nref.map)) continue;
                var inReferenceDomain = nref.band.Band.Contains(aCell)
                    || nref.band.TransitionDepth.TryGetValue(aCell, out _)
                    || nref.band.OuterStripDepth.ContainsKey(aCell);
                if (!inReferenceDomain) continue;

                sb.AppendLine($"―― 对侧 wt={nref.worldTile}  对应格 ({aCell.x},{aCell.z})  offset={nref.offset} ――");
                sb.AppendLine($"  位置: ({aCell.x},{aCell.z})  圈层: {RingLabel(nref.band, aCell)}");
                var nIdx = nref.map.cellIndices.CellToIndex(aCell);
                sb.AppendLine($"  生成时 snapshot:  {LayerTriple(SnapAt(nref.baseTerrain, nIdx), SnapAt(nref.baseBuilding, nIdx), SnapAt(nref.baseRoof, nIdx))}{(nref.baseTerrain != null ? "" : "  (不可用)")}");
                sb.AppendLine($"  当前实际:         {LayerTriple(nref.map.terrainGrid.topGrid[nIdx], RockDefAt(nref.map, aCell), nref.map.roofGrid.RoofAt(aCell))}");
                sb.AppendLine($"  本侧实际参考源[缺失]:  地面=(null)  岩体=无  屋顶=无  (无参考数据；3×3 来源: {ReferenceSourceSummary(nref, aCell)})");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 圈层标签（以"接缝带中圈 = 离散边圈"为基准的相对圈数）：带内圈 = 以内第1圈、过渡带 T·d =
        /// 以内第d+1圈；带外圈 = 以外第1圈、外条带·d = 以外第d+1圈（void）；其余 = 核心区。
        /// 括号内附原始圈名（与 doc/接缝带定义.md 术语对齐）。
        /// </summary>
        private static string RingLabel(SeamlessPolygonGeometry.SeamBandInfo band, IntVec3 c)
        {
            if (band.DiscreteEdge.Contains(c)) return "接缝带中圈（离散边圈）";
            if (band.OuterRing.Contains(c)) return "接缝带中圈以外第1圈（带外圈）";
            if (band.InnerRing.Contains(c)) return "接缝带中圈以内第1圈（带内圈）";
            if (band.TransitionDepth.TryGetValue(c, out var td)) return $"接缝带中圈以内第{td + 1}圈（过渡带T·{td}）";
            if (band.OuterStripDepth.TryGetValue(c, out var od)) return $"接缝带中圈以外第{od + 1}圈（外条带·{od}，void）";
            return "核心区（接缝带中圈以内远处）";
        }

        /// <summary>三层一行式：地面=/岩体=/屋顶=（null 安全）。</summary>
        private static string LayerTriple(Def t, Def b, Def r) =>
            $"地面={t?.defName ?? "(null)"}  岩体={b?.defName ?? "无"}  屋顶={r?.defName ?? "无"}";

        /// <summary>数组快照安全取值。</summary>
        private static T SnapAt<T>(T[] arr, int i) where T : Def =>
            arr != null && i < arr.Length ? arr[i] : null;

        /// <summary>混合结果 vs 当前的差异标注：[层:同] / [层:X→Y] / [层:X→清除]。</summary>
        private static string VerdictSuffix(Def target, Def local, string label)
        {
            if (ReferenceEquals(target, local)) return $"  [{label}:同]";
            if (target == null) return $"  [{label}:{local?.defName ?? "无"}→清除]";
            return $"  [{label}:{local?.defName ?? "无"}→{target.defName}]";
        }

        /// <summary>
        /// 3×3 窗口逐层分布（通用读子）：reader 返回 (has, v)——has=false（越界/不在条带）跳过该格，
        /// v=null 计入"无"。输出"defName=占比"降序（与 <see cref="FormatDist"/> 同款）。
        /// </summary>
        private static string ConvolveLayer3x3(Func<int, int, (bool has, Def v)> read, IntVec3 c)
        {
            var counts = new Dictionary<string, int>();
            var total = 0;
            var window = GenAdj.AdjacentCellsAndInside;
            for (var i = 0; i < window.Length; i++)
            {
                var (has, v) = read(c.x + window[i].x, c.z + window[i].z);
                if (!has) continue;
                var key = v?.defName ?? "无";
                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
                total++;
            }
            if (total == 0) return "(窗口无数据)";
            var entries = new List<KeyValuePair<string, int>>(counts);
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            return string.Join("  ", entries.ConvertAll(kv => $"{kv.Key}={(float)kv.Value / total:F3}"));
        }

        /// <summary>本侧读子：快照数组可用时读 389 原生快照（数组直索），否则回落当前实际（live 读子）。</summary>
        private static Func<int, int, (bool has, Def v)> SelfReader(Map map, Def[] snapshot, Func<Map, IntVec3, Def> live)
        {
            var mapSize = map.Size.x;
            return (x, z) =>
            {
                if (x < 0 || z < 0 || x >= mapSize || z >= mapSize) return (false, null);
                if (snapshot != null) return (true, snapshot[z * mapSize + x]);
                return (true, live(map, new IntVec3(x, 0, z)));
            };
        }

        private static Func<int, int, (bool has, Def v)> ReferenceReader(NeighborRef nref, ReferenceLayer layer)
        {
            return (x, z) => ReadReference(nref, new IntVec3(x, 0, z), layer);
        }

        private static string ReferenceSourceSummary(NeighborRef nref, IntVec3 center)
        {
            var current = 0; var snapshot = 0; var missing = 0;
            var window = GenAdj.AdjacentCellsAndInside;
            for (var i = 0; i < window.Length; i++)
            {
                switch (GetReferenceSource(nref, center + window[i]))
                {
                    case ReferenceSource.Current: current++; break;
                    case ReferenceSource.BaseSnapshot: snapshot++; break;
                    default: missing++; break;
                }
            }
            return $"当前={current} 基础快照={snapshot} 缺失={missing}";
        }

        private static string ReferenceSourceLabel(ReferenceSource source) => source switch
        {
            ReferenceSource.Current => "当前实况",
            ReferenceSource.BaseSnapshot => "基础快照",
            _ => "缺失"
        };

        /// <summary>分布格式化：按占比降序拼接 defName=0.xxx（null = 窗口无数据）。</summary>
        private static string FormatDist(Dictionary<TerrainDef, float> dist)
        {
            if (dist == null) return "(窗口无数据)";
            var entries = new List<KeyValuePair<TerrainDef, float>>(dist);
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            return string.Join("  ", entries.ConvertAll(kv => $"{kv.Key.defName}={kv.Value:F3}"));
        }

        /// <summary>
        /// 同步 cell 屋顶到目标 def（照抄区/卷积区均直接跟随参考——岩壁带原生岩顶 Thick/Thin，
        /// 不裸顶；null → 清顶。C 是新生成图无玩家屋顶，直接同步安全）。
        /// </summary>
        private static void SyncRoofTo(Map map, IntVec3 cell, RoofDef roofDef)
        {
            if (map.roofGrid.RoofAt(cell) != roofDef)
            {
                map.roofGrid.SetRoof(cell, roofDef);
            }
        }

        /// <summary>
        /// 同步 cell 上的岩石体 Building（自然岩石 + 矿石）到**目标 def**（照抄区=单参考 def；卷积区=主导参考 def）：
        /// rockDef != null 且无岩体 → spawn 该 def（跨缝岩色/矿脉连续）；
        /// rockDef == null 且有岩体 → Destroy(Vanish)；
        /// rockDef != null 且有岩体但 def 不同 → 替换（Destroy(Vanish)+spawn）——矿 lump 边界两侧
        /// def 必不同，不替换则跨缝"铁矿紧邻花岗岩"断裂残留（2026-08 修复；自然岩岩色跨缝替换同受益）。
        /// out action: 1=spawn, -1=destroy, 2=replace, 0=无操作。
        /// </summary>
        private static void SyncRockBuildingTo(Map map, IntVec3 cell, ThingDef rockDef, out int action)
        {
            action = 0;
            var existing = cell.GetEdifice(map);
            // 判据 isNaturalRock（自然岩石与矿石同 true）；naturalTerrain 只盖非矿石天然岩
            // （TerrainDefGenerator_Stone 赋值规则），矿石须走 isNaturalRock（2026-08 修复）。
            var hasRockBuilding = existing != null && existing.def.building != null && existing.def.building.isNaturalRock;

            if (rockDef != null && !hasRockBuilding)
            {
                // 需要岩体但无岩体 Building → spawn。
                // 只在无 edifice 的格 spawn（避免覆盖已存在的非岩体 edifice）。
                if (existing == null)
                {
                    GenSpawn.Spawn(rockDef, cell, map);
                    action = 1;
                }
            }
            else if (rockDef == null && hasRockBuilding)
            {
                // 不需要岩体但有岩体 Building → 清除（Vanish 无掉落）。
                existing.Destroy(DestroyMode.Vanish);
                action = -1;
            }
            else if (rockDef != null && existing.def != rockDef)
            {
                // 两侧都有岩体但 def 不同 → 替换（Vanish 无掉落、不吐矿；392 时点 C 图 edifice 只有
                // RocksFromGrid 产物，无覆盖后续建筑的风险）。
                existing.Destroy(DestroyMode.Vanish);
                GenSpawn.Spawn(rockDef, cell, map);
                action = 2;
            }
        }

    }
}
