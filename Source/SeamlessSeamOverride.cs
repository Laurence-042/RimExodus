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
    /// 接缝覆写（新架构：3 圈接缝带 + 连续边中点对齐 + 接缝条带快照参考，权威定义见 doc/接缝带定义.md）。
    ///
    /// 【混合范围】枚举 C（新生成 tile）自己的接缝带 B ∪ 过渡带 T（显式几何集合，
    /// <see cref="SeamlessPolygonGeometry.BuildSeamBand"/> 缓存）。旧方案"从 A 全图 snapshot 枚举候选"
    /// 已废弃——A 方形角落格绕六边形顶点投影进 C 侧向楔形区的污染结构性消失（只枚举 C 的带格，
    /// 每格反向找邻居参考）。带外圈（新定义下为实地形）也参与混合，跨缝两侧地形成对一致。
    ///
    /// 【混合规则（用户定夺 2026-08，规则轴 = 本端圈层，对端只提供数据不参与规则判定）】
    /// - **B_C（接缝带三圈）→ 字面照抄对端对应格**：a = c − offset，strip 有数据即抄地形与岩体
    ///   （岩体用对端 def——跨缝岩色连续；地形未变时岩体同步仍执行）。无任何地形例外（对端是
    ///   Marsh/深水本端就是——不能走 pawn 自然绕路）。错位时 a 落在对端哪个圈层无所谓，
    ///   strip 覆盖 B_A∪T_A∪外条带，照抄天然免疫错位（历史：按对端圈层分规则的版本需要
    ///   B_A/T_A/外条带/核心区四条判定 + 错位补偿补丁，已废弃勿回退）；
    /// - **void_C → 不在枚举范围**（直接用自己的 void）；
    /// - **T_C（过渡带）→ 卷积权重覆盖（源地图轴权重）**：过渡带数据驱动（a 在邻居 strip 内
    ///   即参与，外条带数据全深到源方形边）。w = dSq/(dSq+dOut)——dOut = a 距源接缝带切比雪夫
    ///   深度（贴缝≈0），dSq = a 到源方形边切比雪夫距离：**接近源六边形权重高，到源方形边
    ///   权重渐近 0**——混合范围截止边界（源方形边）恰好是权重归零处，自然闭合无锐利边缘；
    ///   乘性 dither（端点 0/1 不动）打散等值线。历史教训（勿回退）：曾按"本端距接缝带固定
    ///   深度衰减 + 外条带限深 7 格"，数据边界处权重残值戛然而止，源方形边在 C 上投影成
    ///   一条直线。self 分布（C 当前 topGrid 3×3）+ 各参考分布（strip 3×3）→ 众数覆写；
    ///   **岩体/屋顶跟随主导参考（w 最大）的 def**——与照抄区语义统一（地形走卷积混合，
    ///   离散层跟随参考；地形驱动 spawn 会在参考无岩体处生成本端岩体，2026-08 用户实测）。
    /// 顶点楔形区（B_C 多邻居命中）保留卷积合成（几何模糊地带，各 w=1）。
    ///
    /// 【参考源（所有已生成邻居）】经统一入口 <see cref="SeamlessTileGraph.TryGetNeighborSeamStrip"/>
    /// 取接缝条带快照（活图优先，地图卸载后 WorldObject 回落——滚动加载卸载预埋）。
    /// offset 现算（<see cref="SeamlessNeighborRegistry.ComputeNeighborOffset"/>，与邻居表登记
    /// 同公式恒等）——genStep 392 运行时邻居表尚未登记（RegisterNeighborBidirectional 在
    /// onComplete，晚于整个 genStep 链），运行时消费方（传送/渲染）才走邻居表。
    ///
    /// 【保护判据：只有道路，无地形例外（用户定夺 2026-08，勿回退）】SeamOverride 只做三件事：
    /// 按卷积权重覆盖、随机化边缘（dither）、道路修复（保护）。唯一例外是道路——路是 1-2 格
    /// 窄结构，3×3 众数卷积必然抹掉（窗口内少数派），且路由 A* 权威生成：①本格 IsRoad/bridge
    /// 跳过；②<see cref="SeamlessRoadPaths"/> ±3 格切比雪夫缓冲兜底无 tag 路面。水体、沼泽等
    /// 一切地形照常参与混合与拷贝——参考位置由连续边中点对齐保证精确，对端是水体本端就是
    /// 水体（不能走 pawn 自然绕路）；河/海走廊位置由 river patch 端点对齐权威保证。历史的
    /// 水体例外（本格 IsWater 跳过、卷积采样跳水）是旧 offset ±2 格系统误差下"防水蔓延"
    /// 的补丁，中点对齐后不成立（其间还因 HasTag 前缀匹配误伤 Marsh 造成接缝断裂）。
    ///
    /// 【三层归一框架（用户定夺 2026-08）】terrain/building/roof 三层共用同一混合框架：
    /// 照抄区 = 每层读对端参考值（<see cref="seamLayers"/>.ReadStrip）→ 与本端不同（ReadLocal）则写
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
            public SeamStripData strip;
        }

        /// <summary>单格的邻居参考（对齐格 a + 权重 + 所属邻居；三层的参考值经 <see cref="seamLayers"/> 的 ReadStrip 按层现读）。</summary>
        private struct CellRef
        {
            public float w;
            public NeighborRef owner;
            public IntVec3 aCell;
        }

        /// <summary>
        /// 混合层操作集（**三层归一框架**，用户定夺 2026-08）：terrain/building/roof 三层共用同一
        /// 混合框架（照抄 = 读对端参考值 → 与本端不同则写；卷积区见主循环），层差异全部收进委托：
        /// <see cref="ReadLocal"/>（读本端）、<see cref="ReadStrip"/>（读对端条带参考）、<see cref="Write"/>
        /// （写本端，委托内部含必要守卫——如岩石 spawn 的 existing==null 检查、屋顶判等）。
        /// 新增层（如植物）只需在此追加一项。
        /// </summary>
        private sealed class SeamLayer
        {
            public readonly string Name;
            /// <summary>读本端当前值。</summary>
            public readonly Func<Map, IntVec3, Def> ReadLocal;
            /// <summary>读对端条带参考值。</summary>
            public readonly Func<SeamStripData, IntVec3, Def> ReadStrip;
            /// <summary>写本端。</summary>
            public readonly Action<Map, IntVec3, Def> Write;

            public SeamLayer(string name, Func<Map, IntVec3, Def> readLocal,
                Func<SeamStripData, IntVec3, Def> readStrip, Action<Map, IntVec3, Def> write)
            {
                Name = name;
                ReadLocal = readLocal;
                ReadStrip = readStrip;
                Write = write;
            }
        }

        /// <summary>三层实例：terrain（连续层，卷积区走加权众数）/ building（岩石体 def）/ roof（RoofDef）。</summary>
        private static readonly SeamLayer[] seamLayers =
        {
            new("terrain",
                (m, c) => m.terrainGrid.topGrid[m.cellIndices.CellToIndex(c)],
                (s, a) => s.terrainLookup.TryGetValue(a, out var t) ? t : null,
                (m, c, v) =>
                {
                    m.terrainGrid.topGrid[m.cellIndices.CellToIndex(c)] = (TerrainDef)v;
                    m.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Terrain, regenAdjacentCells: false, regenAdjacentSections: false);
                }),
            new("building",
                (m, c) => SeamStripData.RockDefAt(m, c),
                (s, a) => s.buildingLookup.TryGetValue(a, out var b) ? b : null,
                (m, c, v) => SyncRockBuildingTo(m, c, (ThingDef)v, out _)),
            new("roof",
                (m, c) => m.roofGrid.RoofAt(c),
                (s, a) => s.roofLookup.TryGetValue(a, out var r) ? r : null,
                (m, c, v) => SyncRoofTo(m, c, (RoofDef)v)),
        };

        private static readonly List<NeighborRef> neighborRefs = new();
        private static readonly HashSet<IntVec3> bandCells = new();
        private static readonly List<CellRef> cellRefs = new();
        private static readonly List<(Dictionary<TerrainDef, float> dist, float w)> blendParts = new();

        /// <summary>
        /// 对 map 的所有已生成邻居做单向接缝覆写（只改 map 自身，不改邻居）。
        /// 在 GenStep_SeamOverride.Generate 里调用（order=392，接缝带 void 裁切 391 之后、
        /// Settlement 400 与 Fog 1500 之前）。
        /// </summary>
        public static void ApplyOneWay(Map map, int worldTile)
        {
            if (map == null || worldTile < 0) return;

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

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var cellIndices = map.cellIndices;
            var topGrid = map.terrainGrid.topGrid;
            var mapDrawer = map.mapDrawer;
            var verbose = RimExodusMod.Settings?.verboseLogging ?? false;
            var written = 0;

            // C 的道路保护集：GenStep_Roads 路径快照 ±3 格缓冲（覆盖 Bezier 平滑相对 A* 折线的偏离）。
            // 窄路（1-2 格宽）在 3×3 卷积里永远是少数派，不保护会被周围地形卷没。
            var roadGuard = BuildRoadGuard(map);

            CollectNeighborRefs(map, worldTile);
            if (neighborRefs.Count == 0) return;

            // 主循环：枚举 C 的接缝带 B ∪ 过渡带 T。**规则轴 = 本端圈层**（对端只提供数据，
            // 不参与规则判定——错位天然免疫）：
            // - B_C（三圈）→ 字面照抄对端对应格的地形与岩体（a = c − offset，strip 有数据即抄）；
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

                if (!CollectCellRefs(cCell, inBand, weightNoise, noiseAmp)) continue;

                if (inBand && cellRefs.Count == 1)
                {
                    // 接缝带照抄区：三层统一框架——每层读对端参考值，与本端不同则写。
                    // 无任何地形例外（对端是 Marsh/深水本端就是——不能走 pawn 自然绕路）；
                    // 岩体用对端 def（跨缝岩色连续，"岩石地形无岩体"中间带状态正确继承）；
                    // 屋顶照抄对端原生岩顶（Thick/Thin，不裸顶）。地形未变时岩体/屋顶同步仍执行。
                    var ref0 = cellRefs[0];
                    var anyWritten = false;
                    foreach (var layer in seamLayers)
                    {
                        var target = layer.ReadStrip(ref0.owner.strip, ref0.aCell);
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
                    for (var i = 1; i < seamLayers.Length; i++)
                    {
                        seamLayers[i].Write(map, cCell, seamLayers[i].ReadStrip(dominant.owner.strip, dominant.aCell));
                    }
                    written++;
                }
            }

            // 逐格诊断不打日志（B∪T 数千格会刷屏）——用 Dev 探针 InspectSnapshotAtPosition 单格重放。
            if (verbose)
                Log.Message($"[RimExodus] SeamOverride map={map.uniqueID}(wt={worldTile}) refs={neighborRefs.Count} written={written} cells.");
        }

        /// <summary>
        /// 预收集参考源到 <see cref="neighborRefs"/>：C 的每个世界邻居中"已生成"
        /// （<see cref="SeamlessTileGraph.TryGetNeighborSeamStrip"/> 命中接缝条带快照）的邻居，
        /// 附 offset（现算，与邻居表登记同公式恒等）与邻居带几何。
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
                if (!SeamlessTileGraph.TryGetNeighborSeamStrip(neighborTile, out var strip)) continue;
                if (strip == null || strip.terrainLookup == null || strip.terrainLookup.Count == 0) continue;

                var offset = SeamlessNeighborRegistry.ComputeNeighborOffset(worldTile, neighborTile, map);
                if (offset == IntVec3.Zero) continue;

                neighborRefs.Add(new NeighborRef
                {
                    worldTile = neighborTile,
                    offset = offset,
                    strip = strip
                });
            }
        }

        /// <summary>
        /// 收集 cCell 的各邻居参考到 <see cref="cellRefs"/>（复用 <see cref="neighborRefs"/>），
        /// 返回是否有参考。**规则轴 = 本端圈层**（用户定夺 2026-08）：
        /// - c ∈ B_C（照抄区）：命中 = a 在邻居 strip 内（有数据即命中，不看对端圈层），
        ///   w = 1（仅多命中合成时用到）——错位时 a 落在对端哪个圈层都无所谓，strip 有数据就抄；
        /// - c ∈ T_C（卷积区）：w = dSq/(dSq+dOut)（源地图轴：接近源六边形高 → 源方形边 0）
        ///   ×乘性 dither。
        /// </summary>
        private static bool CollectCellRefs(IntVec3 cCell, bool inBand, Perlin weightNoise, float noiseAmp)
        {
            cellRefs.Clear();
            foreach (var nref in neighborRefs)
            {
                // 契约 neighborLocal + offset = myLocal → 邻居格 a = c − offset。
                var aCell = new IntVec3(cCell.x - nref.offset.x, 0, cCell.z - nref.offset.z);
                // 命中判定用 terrain 层的 ReadStrip（terrain 无参考价值的格不入 strip）。
                if (seamLayers[0].ReadStrip(nref.strip, aCell) == null) continue;

                float wRaw;
                if (inBand)
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
                    var nSize = nref.strip.mapSize;
                    var dSq = Mathf.Min(Mathf.Min(aCell.x, aCell.z),
                        Mathf.Min(nSize - 1 - aCell.x, nSize - 1 - aCell.z));
                    var dOut = nref.strip.depthLookup.TryGetValue(aCell, out var d) ? d : 0;
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
        /// 组装卷积合成输入到 <see cref="blendParts"/>：self 分布（C 当前 topGrid，selfW>0 时）+
        /// 各邻居参考分布（快照稀疏字典 3×3 采样）。
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
                var refDist = Convolve3x3FromStrip(r.owner.strip.terrainLookup, r.aCell);
                if (refDist != null) blendParts.Add((refDist, r.w));
            }
        }

        /// <summary>
        /// 构建 C 的道路路径缓冲保护集：<see cref="SeamlessRoadPaths"/> 快照的 A* 路径节点，
        /// 每节点 ±<see cref="RoadGuardRadius"/> 格切比雪夫膨胀。有 Road tag / bridge 的路面
        /// 由混合循环的地形判据精确保护，本缓冲兜底 Gravel 等无 tag 路面。
        /// </summary>
        private static HashSet<IntVec3> BuildRoadGuard(Map map)
        {
            var guard = new HashSet<IntVec3>();
            var comp = map.GetComponent<SeamlessRoadPaths>();
            if (comp == null) return guard;

            foreach (var path in comp.paths)
            {
                foreach (var node in path)
                {
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

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var nx = center.x + dx;
                    if (nx < 0) nx = 0; else if (nx >= mapSize) nx = mapSize - 1;
                    var nz = center.z + dz;
                    if (nz < 0) nz = 0; else if (nz >= mapSize) nz = mapSize - 1;
                    var idx = nz * mapSize + nx;
                    var t = snapshot[idx];
                    if (t == null) continue;
                    if (voidDef != null && t == voidDef) continue;
                    counts.TryGetValue(t, out var c);
                    counts[t] = c + 1;
                    total++;
                }
            }

            if (total == 0) return null;
            var result = new Dictionary<TerrainDef, float>(counts.Count);
            foreach (var kv in counts)
                result[kv.Key] = (float)kv.Value / total;
            return result;
        }

        /// <summary>
        /// 3×3 卷积（稀疏字典源，邻居参考用）：采样接缝条带快照 lookup 中 center 周围 3×3 邻域。
        /// 条带是带状区域，窗口越出区域的格跳过（不 clamp——条带外无数据，与数组源的矩形边界
        /// 语义不同）。**无地形例外**（同 <see cref="Convolve3x3"/>，水体照常参与）。
        /// total=0 返回 null。
        /// </summary>
        private static Dictionary<TerrainDef, float> Convolve3x3FromStrip(Dictionary<IntVec3, TerrainDef> lookup, IntVec3 center)
        {
            Dictionary<TerrainDef, int> counts = null;
            var total = 0;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!lookup.TryGetValue(new IntVec3(center.x + dx, 0, center.z + dz), out var t) || t == null) continue;
                    counts ??= new Dictionary<TerrainDef, int>();
                    counts.TryGetValue(t, out var c);
                    counts[t] = c + 1;
                    total++;
                }
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
        /// 探针重放（只读诊断，不写任何 grid）：对单个 cell 重跑 <see cref="ApplyOneWay"/> 的混合判定，
        /// 返回多行诊断文本（无前导换行，调用方拼进自己的 StringBuilder）。供 InspectSnapshotAtPosition
        /// （Dev ToolMap）使用——复用 Convolve3x3/Convolve3x3FromStrip/BlendDistributions/GetMode/
        /// BuildRoadGuard/CollectNeighborRefs，重放与真实逻辑共享同一份代码，不会因复刻而漂移。
        ///
        /// 失真声明：self 侧 3×3 采样自当前 topGrid（生成后的状态），neighbor 侧采样自接缝条带快照
        /// （生成期定格，不可变）。故重放回答的是"以当前状态再跑一次会怎样"，而非生成那一刻的历史。
        /// </summary>
        public static string DescribeCellMixing(Map map, int worldTile, IntVec3 cell)
        {
            var sb = new System.Text.StringBuilder();
            var mapSize = map.Size.x;
            var band = SeamlessPolygonGeometry.BuildSeamBand(worldTile, mapSize);

            var voidDef = DefDatabase<TerrainDef>.GetNamedSilentFail("RimExodus_Void");
            var cIdx = map.cellIndices.CellToIndex(cell);
            var localTerrain = map.terrainGrid.topGrid[cIdx];

            sb.AppendLine("  == SeamOverride 重放（3 圈接缝带架构，只读） ==");
            sb.AppendLine("  (重放 = 以当前状态再跑一次：self 卷积采样用当前 topGrid——若与 self snapshot 不同，说明生成期已被混合覆写)");
            sb.AppendLine($"  本格地形: {localTerrain?.defName ?? "(null)"}");

            // 圈分类（权威定义见 doc/接缝带定义.md）。规则轴 = 本端圈层：
            // B_C（三圈）→ 照抄区；T_C → 卷积区。
            var inBand = band.Band.Contains(cell);
            if (band.OuterRing.Contains(cell)) sb.AppendLine("  圈层: 带外圈（B_C 照抄区；传送圈，格中心在多边形外）");
            else if (band.DiscreteEdge.Contains(cell)) sb.AppendLine("  圈层: 离散边圈（B_C 照抄区；传送圈，横跨连续边）");
            else if (band.InnerRing.Contains(cell)) sb.AppendLine("  圈层: 带内圈（B_C 照抄区；无传送点，格中心在多边形内）");
            else if (band.TransitionDepth.TryGetValue(cell, out var td)) sb.AppendLine($"  圈层: 过渡带 T_C 深度 {td}（卷积区，源地图轴权重）");
            else
            {
                sb.AppendLine("  圈层: 接缝带 ∪ 过渡带之外（void/核心区）→ 不参与混合");
                return sb.ToString().TrimEnd();
            }

            var roadComp = map.GetComponent<SeamlessRoadPaths>();
            var roadGuard = BuildRoadGuard(map);
            if (roadComp == null || roadComp.paths.Count == 0)
                sb.AppendLine("  (注意: SeamlessRoadPaths 为空——本图无路，或读档后快照丢失，道路保护②判据可能失真)");

            // 跳过保护（与 ApplyOneWay 主循环同序逐条判定）。只有道路保护——无地形例外。
            var skipReason =
                localTerrain == null || localTerrain == voidDef ? "localTerrain 为 null/void" :
                localTerrain.IsRoad || localTerrain.bridge ? "道路保护①(IsRoad/bridge 地形判据)" :
                roadGuard.Contains(cell) ? "道路保护②(A* 路径 ±3 格缓冲)" : null;
            if (skipReason != null)
            {
                sb.AppendLine($"  在混合范围内，但被跳过: {skipReason} → 不参与混合");
                return sb.ToString().TrimEnd();
            }

            // 邻居参考（与 ApplyOneWay 同源逻辑）。
            var noiseAmp = RimExodusMod.Settings?.seamOverrideNoiseAmplitude ?? 0.15f;
            Perlin weightNoise = null;
            if (noiseAmp > 0f)
                weightNoise = new Perlin(0.04f, 2.0, 0.5, 4, worldTile * 31 + 7919, QualityMode.Medium);

            CollectNeighborRefs(map, worldTile);
            if (neighborRefs.Count == 0)
            {
                sb.AppendLine("  → 无已生成邻居（无接缝条带快照）→ 不混合");
                return sb.ToString().TrimEnd();
            }

            // CollectCellRefs 走静态 cellRefs——探针调用后其内容对下一次 ApplyOneWay 无影响
            //（ApplyOneWay 每格先 Clear），此处直接读结果。
            if (!CollectCellRefs(cell, inBand, weightNoise, noiseAmp))
            {
                sb.AppendLine("  → 无邻居参考此格（对齐格不在任何邻居 strip 内）→ 保持原生");
                return sb.ToString().TrimEnd();
            }

            foreach (var r in cellRefs)
            {
                var terrain = seamLayers[0].ReadStrip(r.owner.strip, r.aCell)?.defName ?? "(null)";
                var building = seamLayers[1].ReadStrip(r.owner.strip, r.aCell)?.defName ?? "无";
                var roof = seamLayers[2].ReadStrip(r.owner.strip, r.aCell)?.defName ?? "无";
                sb.AppendLine($"  -- 邻居 wt={r.owner.worldTile}  aCell=({r.aCell.x},{r.aCell.z}) 参考地形={terrain} 岩体={building} 屋顶={roof} w={r.w:F3}");
            }

            if (inBand && cellRefs.Count == 1)
            {
                var ref0 = cellRefs[0];
                var parts = new List<string>();
                foreach (var layer in seamLayers)
                {
                    var target = layer.ReadStrip(ref0.owner.strip, ref0.aCell);
                    var local = layer.ReadLocal(map, cell);
                    parts.Add(target == null
                        ? $"{layer.Name}=无"
                        : $"{layer.Name}={target.defName}{(ReferenceEquals(target, local) ? "(同本端)" : $"({local?.defName ?? "无"}→)")}");
                }
                sb.AppendLine($"  → B_C 照抄区: 三层统一同步 {string.Join(" ", parts)}");
                return sb.ToString().TrimEnd();
            }

            // 卷积合成重放（T_C，或 B_C 顶点楔形多参考）。
            CollectBlendParts(map.terrainGrid.topGrid, mapSize, voidDef, cell);
            if (blendParts.Count == 0)
            {
                sb.AppendLine("  → 卷积分布全空(total=0) → 跳过");
                return sb.ToString().TrimEnd();
            }

            var totalW2 = 0f;
            foreach (var r in cellRefs) totalW2 += r.w;
            sb.AppendLine($"  合成{(inBand ? "(B_C 顶点多参考)" : "(T_C 卷积)")}: Σw={totalW2:F2} → selfW={Mathf.Clamp01(1f - totalW2):F2}，{blendParts.Count} 路分布：");
            foreach (var (dist, w) in blendParts)
                sb.AppendLine($"    ×w={w:F3}: {FormatDist(dist)}");

            var blended = BlendDistributions(blendParts);
            sb.AppendLine($"    blend: {FormatDist(blended)}");
            var chosen = GetMode(blended);
            if (chosen == null)
                sb.AppendLine("  → chosen=null → 不覆写");
            else if (chosen == localTerrain)
                sb.AppendLine($"  → chosen={chosen.defName} == local(当前) → 不覆写（当前值已与混合结果一致）");
            else
                sb.AppendLine($"  → 会覆写: {localTerrain.defName} → {chosen.defName}");

            return sb.ToString().TrimEnd();
        }

        /// <summary>分布格式化：按占比降序拼接 defName=0.xxx。</summary>
        private static string FormatDist(Dictionary<TerrainDef, float> dist)
        {
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
