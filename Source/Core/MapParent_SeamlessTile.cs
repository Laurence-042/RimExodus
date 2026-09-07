using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝世界地块的 MapParent（阶段4前置：基础地图而非口袋地图）。
    /// 继承原生 <see cref="MapParent"/>（非 PocketMapParent），作为独立基础地图存在，
    /// 无 sourceMap 父子关系，所有地块对等。
    ///
    /// 基础地图的 <c>map.Tile</c> = 真实 PlanetTile，原生 Coast/River/Delta 等 TileMutator
    /// 在口袋地图上无法生效的问题自然消失（mutator.Init 读 map.Tile 拿到真实邻居数据）。
    ///
    /// 地块间邻接关系由 <see cref="neighbors"/> 直接邻居表维护，不依赖 sourceMap/IsPocketMap。
    /// 邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// </summary>
    public class MapParent_SeamlessTile : MapParent
    {
        /// <summary>该地块在世界地图上的 tile 索引。</summary>
        public int worldTile = -1;

        /// <summary>
        /// 直接邻居表。每槽存邻居引用 + 该邻居相对本地块的偏移 + 对应世界邻居 tile。
        /// 偏移 = 邻居本地坐标 → 本地块坐标的平移（绘制邻居时用）。
        /// worldTile 作为主键查询（稳定，无角度歧义）。
        /// </summary>
        public List<NeighborLink> neighbors = new List<NeighborLink>();

        /// <summary>该地块是否已被自动聚焦过（首个自有 pawn 入境时聚焦，仅一次）。</summary>
        public bool autoFocused;

        /// <summary>
        /// 该地块在全局平面坐标系里的原点（阶段4 接缝覆写预留）。
        ///
        /// 原生 parent 图（玩家家园/原生家族）作为生成链源点时 tileOrigin 固定为 (0,0)。
        /// 地块 tile 的 tileOrigin = 源 tile 的 tileOrigin + ComputeNeighborOffset（生成时确定）。
        /// 多跳场景（A→B→C）沿邻居链自然累加。
        ///
        /// 当前暂无消费者（连续 Perlin 方案已回退）。接缝覆写 genStep 将用它确定接缝位置。
        /// </summary>
        public Vector2 tileOrigin = Vector2.zero;

        /// <summary>
        /// 基础地形快照（阶段4 接缝覆写）：void 裁切前的完整矩形地形备份。
        /// 在 GenStep_SeamlessTile（order=389）开头备份——此时全部基础地形写入步骤（Terrain 210 /
        /// Coast 220 / CoastalEdgeFill 230）已跑完，Roads(390)/Plants(900)/Animals(1200) 尚未
        /// （它们不写 topGrid）；400+ 的 BaseGen 地板写入被选址 patch 拦在接缝带之外（残余缺口见
        /// SeamlessTileGenerator.xml 观察项）。
        /// 接缝参考在当前格已被裁成 void 时读取此数组。地块图不序列化，读档后需要时由
        /// <see cref="SeamlessSnapshotRegenerator"/> 在正式生成开始前按需重建。
        /// </summary>
        public TerrainDef[] baseTerrainSnapshot;

        /// <summary>
        /// 原生建筑快照（岩石体 BuildingDef，null=无；与 <see cref="baseTerrainSnapshot"/> 同点位
        /// 备份、非序列化）。与地形快照平行的第二层：void 铺设会清除接缝带外格上的岩体，
        /// void 侧参考必须引用**原生**两层（同源），否则世界连续的岩壁会被误判
        /// 无岩体（2026-08 实测：(98,233) 连续岩壁到该格突然消失）。存 BuildingDef 而非 bool：
        /// 完全一致区 spawn 用对端岩石 def，跨缝岩色也连续。
        /// </summary>
        public ThingDef[] baseBuildingSnapshot;

        /// <summary>
        /// 原生屋顶快照（RoofDef，null=无；与 <see cref="baseTerrainSnapshot"/> 同点位备份、非序列化）。
        /// 第三平行层：void 铺设会清掉接缝带外格的屋顶，外条带参考须用原生屋顶——否则对侧
        /// 照抄区"有岩石没有 roof"（岩壁裸顶，不挡雨/视觉突兀）。
        /// </summary>
        public RoofDef[] baseRoofSnapshot;

        /// <summary>
        /// 前哨封存记录（2026-09 前哨保留）。非 null = 本 WO 处于封存态（Map 已拆、记录在档）：
        /// - 到达删除距离且居住区达阈时由 governor 捕获（<see cref="ZoneMapRecord.Capture"/>）并拆图，
        ///   本 WO 刻意保留（邻居链不断、世界图第四态图标）；封存态无 Map，不参与接缝参考；
        /// - 再次生成时由 <see cref="SeamlessTileManager.GenerateTileMap"/> 守卫识别并复用本 WO，
        ///   <see cref="GenStep_ZoneRestore"/>(395) 在生成链内重放，onComplete 消费置 null。
        /// 淘汰/手动删除封存 = 销毁本 WO（记录随亡）。
        /// </summary>
        public ZoneMapRecord preserveRecord;

        public override string Label => "Seamless Tile Map";

        /// <summary>
        /// 世界图状态标记（2026-08 真实形状填充版）：useDynamicDrawer=true（def），动态层每帧调
        /// 本方法 → 按本 tile 真实多边形顶点（WorldGrid.GetTileVertices，六/五边形各异）画向内收缩的
        /// 半透明填充，颜色按运行时状态（休眠灰/活跃有人橙/活跃无人蓝）。取代旧的固定六边形 PNG
        /// 图标（Material override + TileWorldIcons 贴图）——固定形状永远契合不了各 tile 实际角度。
        /// 点击命中不受影响：GenWorldUI 的动态层点击判定只看 useDynamicDrawer + DistanceToMouse，
        /// 且 tile 匹配兜底（WorldObjectsUnderMouse 第三段）本身就可选中本对象。
        /// </summary>
        public override void Draw()
        {
            int state = TileWorldIcons.GetStateIndex(this);
            if (state < 0) return;
            Mesh mesh = TileWorldIcons.GetStateMesh(Tile, state);
            if (mesh == null) return;
            Graphics.DrawMesh(mesh, Vector3.zero, Quaternion.identity, TileWorldIcons.OverlayMat, WorldCameraManager.WorldLayer);
            // 前哨保留点标（2026-09）：会被保留的 tile（达阈活图 ∪ 已封存）中心小白点，
            // 与三/四态填充正交——玩家一眼看出哪些 tile 承载着会被保留的玩家工程。
            // 数据源 = tracker.PreservedTiles（每轮 Sweep 末发布，每帧仅一次 HashSet 查询）。
            if (SeamlessMapModificationTracker.PreservedTiles.Contains(Tile.tileId))
            {
                TileWorldIcons.DrawPreservedDot(Tile);
            }
        }

        /// <summary>
        /// 玩家在世界地图上的主动生命周期 gizmo（2026-08；2026-09 逻辑唯一出处移至
        /// <see cref="SeamlessManualDormancyGizmos"/>，本覆写只保留地块图挂点——原生家族 POI 图由
        /// <c>Patch_MapParent_GetGizmos_ManualDormancy</c> Postfix 提供同一套 gizmo）：
        /// - 活跃非家园图 → "休眠此图"（登记手动休眠锁，governor 不再按距离唤醒，只有玩家
        ///   主动进图 / pawn 被命令接近其接缝时唤醒）；
        /// - 休眠图 → "删除此图"（确认后走 <see cref="SeamlessTileManager.RemoveRollingMap"/>，
        ///   与 Dev Force Delete 同路径——地块图销毁 Map+WorldObject"从未出现过"）。
        /// 家园图（IsProtectedHome）永不休眠不删除，无 gizmo。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var gizmo in base.GetGizmos()) // 保留"查看地图"（休眠图的显式唤醒入口）。
            {
                yield return gizmo;
            }

            foreach (var gizmo in SeamlessManualDormancyGizmos.ForParent(this))
            {
                yield return gizmo;
            }
        }

        /// <summary>
        /// 阶段4前置：基础地图的 WorldObject 会进入世界视图静态绘制层（useDynamicDrawer=false）。
        /// override Print 为空操作，让地块在世界地图上不显示图标。
        /// 地块通过地图内叠加渲染呈现，不需世界视图图标。
        /// （2026-08 起 def 改 useDynamicDrawer=true 走动态层，Print 仍为空操作——
        /// 动态层绘制由 <see cref="Draw"/> 的真实形状填充负责。）
        /// </summary>
        public override void Print(LayerSubMesh subMesh)
        {
            // 不在世界视图静态层画图标（动态层由 Draw/Material 负责）。
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_Values.Look(ref autoFocused, "autoFocused", false);
            Scribe_Values.Look(ref tileOrigin, "tileOrigin", Vector2.zero);
            Scribe_Deep.Look(ref preserveRecord, "preserveRecord");

            // 邻居表序列化：用 IExposable 的 NeighborLink 列表。
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                PruneInvalidNeighbors();
            }
            Scribe_Collections.Look(ref neighbors, "neighbors", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                neighbors ??= new List<NeighborLink>();
                PruneInvalidNeighbors();
            }
        }

        /// <summary>清理邻居表中的空引用（null NeighborLink 或 neighbor 字段为 null）。</summary>
        public void PruneInvalidNeighbors()
        {
            neighbors?.RemoveAll(n => n == null || n.neighbor == null);
        }

        /// <summary>获取指向指定世界 tile 的邻居连接（null 表示无此邻居）。</summary>
        public NeighborLink GetNeighborByWorldTile(int targetWorldTile)
        {
            foreach (var link in neighbors)
            {
                if (link != null && link.worldTile == targetWorldTile && link.neighbor != null)
                {
                    return link;
                }
            }
            return null;
        }

        /// <summary>设置指向 worldTile 的邻居连接（覆盖或新增）。neighbor 可为原生 parent MapParent（家园 Settlement 等）或 MapParent_SeamlessTile。</summary>
        public void SetNeighbor(int worldTile, MapParent neighbor, IntVec3 offset)
        {
            neighbors.RemoveAll(n => n != null && n.worldTile == worldTile);
            if (neighbor == null)
            {
                return;
            }
            neighbors.Add(new NeighborLink
            {
                worldTile = worldTile,
                neighbor = neighbor,
                offset = offset
            });
        }
    }

    /// <summary>
    /// 世界图地块状态标记的材质/网格缓存与 gizmo 图标（2026-08 真实形状顶点色渐变版，
    /// StaticConstructorOnStartup）。状态 = 活跃有人（橙）/ 活跃无人（蓝）/ 休眠（灰）——玩家在
    /// 世界地图上一眼区分哪些 tile 有已生成地图、地图里有没有人（色盲友好蓝橙对，用户定夺）。
    /// 绘制方式 = 按 tile 真实多边形顶点（六/五边形各异）重建单格 mesh（中心顶点 + 环形顶点三角扇），
    /// **颜色与透明度写进顶点色**：中心 <see cref="CenterAlpha"/> 较实（仍透出地形）→ 边缘顶点
    /// alpha 0 线性渐隐（用户定夺 2026-08：中心近实边缘全透，既醒目又不遮地块/不与原版
    /// MouseTile/SelectedTile 描边打架）。材质 = 原版 <see cref="WorldMaterials.VertexColorTransparent"/>
    /// 同款顶点色 shader（WorldDrawLayer_RaycastableGrid 先例），全状态共享一个材质实例，
    /// 状态切换只重写 mesh 顶点色（TileFillMesh.bakedState 守卫，非每帧）。
    /// 教训（勿回退）：首版 SolidColorMaterials + WorldOverlayTransparent 无贴图且 renderQueue
    /// 未设 → 完全不显示；二版 CurrentMapTile 贴图染色 → 休眠灰 0.45 平铺对比度不足看不出来。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TileWorldIcons
    {
        /// <summary>渐变中心的 alpha（边缘为 0）：接近不透明但仍透出地形（用户定夺）。</summary>
        public const float CenterAlpha = 0.65f;

        // 状态索引（GetStateIndex 的返回值，亦为 StateColors 下标）。
        public const int StateActive = 0;
        public const int StateUnmanned = 1;
        public const int StateDormant = 2;
        public const int StateArchived = 3; // 前哨封存态（2026-09）：无图但有 preserveRecord

        private static readonly Color[] StateColors =
        {
            new Color(1f, 0.55f, 0.1f),   // 活跃有人：橙
            new Color(0.2f, 0.5f, 1f),    // 活跃无人：蓝
            new Color(0.5f, 0.5f, 0.5f),  // 休眠：灰
            new Color(0.1f, 0.7f, 0.6f),  // 封存：青绿（与蓝橙灰三态可区分）
        };

        private static Material _overlayMat;

        /// <summary>缓存条目：mesh 几何一次构建；bakedState 记录当前顶点色对应的状态，变化才重写。</summary>
        private sealed class TileFillMesh
        {
            public Mesh mesh;
            public int bakedState = -1;
        }

        private static readonly Dictionary<PlanetTile, TileFillMesh> TileMeshes = new Dictionary<PlanetTile, TileFillMesh>();

        private static readonly List<Vector3> TmpVerts = new List<Vector3>();
        private static readonly List<int> TmpIndices = new List<int>();
        private static readonly Color32[] TmpColors = new Color32[16];

        /// <summary>gizmo"休眠此图"图标（mod 自带，原版无现成 Suspend 命令图标）。</summary>
        public static Texture2D SleepCommandIcon { get; private set; }

        /// <summary>gizmo"删除此图"图标（mod 自带）。</summary>
        public static Texture2D DeleteCommandIcon { get; private set; }

        /// <summary>全状态共享的顶点色世界覆盖材质（颜色在顶点色里，材质无需分状态）。</summary>
        public static Material OverlayMat => _overlayMat;

        // 初始化放声明类自身的静态构造（特性也挂在声明类上）——Verse 的"缺特性"分析器只认
        // 字段声明类的特性，挂嵌套类不消警告（2026-08 修警告时发现）。
        static TileWorldIcons()
        {
            _overlayMat = new Material(WorldMaterials.VertexColorTransparent);
            SleepCommandIcon = ContentFinder<Texture2D>.Get("UI/Commands/RimExodus_SleepMap", reportFailure: false);
            DeleteCommandIcon = ContentFinder<Texture2D>.Get("UI/Commands/RimExodus_DeleteMap", reportFailure: false);
            BuildPreservedDotMesh();
        }

        /// <summary>前哨保留点标 mesh（全 tile 共享：本地 XY 平面单位圆盘，颜色烘在顶点色，白 0.92）。</summary>
        private static Mesh _preservedDotMesh;

        /// <summary>
        /// 建"已保留"点标 mesh：16 段三角扇圆盘（与 tile 填充同构，XY 平面、+Z 朝外）。
        /// 共享单实例 + 每 tile TRS 矩阵定位（Patch_MapEdgeClipDrawer 的"unit quad + matrix"范式），
        /// 无 per-tile mesh 生命周期负担。
        /// </summary>
        private static void BuildPreservedDotMesh()
        {
            const int segments = 16;
            var verts = new List<Vector3>(segments + 1) { Vector3.zero };
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
            }
            var indices = new List<int>(segments * 3);
            for (int j = 0; j < segments; j++)
            {
                // 与 BuildTileMesh 同绕序（b, a, 0）。
                int va = 1 + j;
                int vb = 1 + (j + 1) % segments;
                indices.Add(vb);
                indices.Add(va);
                indices.Add(0);
            }
            var colors = new Color32[verts.Count];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color32(255, 255, 255, 235);
            }
            _preservedDotMesh = new Mesh { name = "RimExodus_PreservedDot" };
            _preservedDotMesh.SetVertices(verts);
            _preservedDotMesh.SetTriangles(indices, 0);
            _preservedDotMesh.colors32 = colors;
            _preservedDotMesh.RecalculateNormals();
        }

        /// <summary>
        /// 画"已保留"中心点标（2026-09 前哨保留）：tile 中心 + 法向抬 0.03（盖过填充的 0.02），
        /// 圆盘半径 ≈ AverageTileSize × 0.18。共享 OverlayMat（顶点色材质）。
        /// </summary>
        public static void DrawPreservedDot(PlanetTile tile)
        {
            if (!tile.Valid || _preservedDotMesh == null || _overlayMat == null) return;
            Vector3 center = Find.WorldGrid.GetTileCenter(tile);
            Vector3 normal = center.normalized;
            var matrix = Matrix4x4.TRS(center + normal * 0.03f, Quaternion.LookRotation(normal),
                Vector3.one * (Find.WorldGrid.AverageTileSize * 0.18f));
            Graphics.DrawMesh(_preservedDotMesh, matrix, _overlayMat, WorldCameraManager.WorldLayer);
        }

        /// <summary>
        /// 按地图运行时状态取状态索引。无图/已 Dispose：封存态（有 <see cref="MapParent_SeamlessTile.preserveRecord"/>
        /// 且未销毁，2026-09 前哨保留）→ <see cref="StateArchived"/>；其余（历史孤儿）返回 -1 = 不画。
        /// </summary>
        public static int GetStateIndex(MapParent parent)
        {
            var map = parent?.Map;
            if (map == null || map.Disposed)
            {
                return parent is MapParent_SeamlessTile tileParent
                    && tileParent.preserveRecord != null && !tileParent.Destroyed
                    ? StateArchived
                    : -1;
            }

            if (SeamlessDormancyManager.IsDormant(map)) return StateDormant;
            // "有人"判定唯一出处 = SeamlessMapGovernance.HasPlayerPawn（与 governor 距离源同口径，
            // 勿在此自写 pawn 遍历——首版用 AllPawnsSpawnedCount 把野生动物也算有人）。
            if (SeamlessMapGovernance.HasPlayerPawn(map)) return StateActive;
            return StateUnmanned;
        }

        /// <summary>
        /// 取/建 tile 的渐变填充 mesh 并确保顶点色对应当前状态（进程缓存几何，状态变化才重写颜色）。
        /// 图删除时经 <see cref="ReleaseTileMesh"/> 释放。世界图 tile 数有穷且受管辖图 ≤ deleteHops 球，
        /// 缓存有界。
        /// </summary>
        public static Mesh GetStateMesh(PlanetTile tile, int state)
        {
            if (!tile.Valid || state < 0 || state >= StateColors.Length) return null;
            if (!TileMeshes.TryGetValue(tile, out var entry) || entry?.mesh == null)
            {
                entry = BuildTileMesh(tile);
                if (entry == null) return null;
                TileMeshes[tile] = entry;
            }
            if (entry.bakedState != state)
            {
                BakeStateColors(entry.mesh, state);
                entry.bakedState = state;
            }
            return entry.mesh;
        }

        private static TileFillMesh BuildTileMesh(PlanetTile tile)
        {
            Find.WorldGrid.GetTileVertices(tile, TmpVerts);
            if (TmpVerts.Count < 3 || TmpVerts.Count + 1 > TmpColors.Length) return null;

            // 顶点布局：[0] = tile 中心，[1..n] = 环形顶点。均沿法向抬升 0.02 防 z-fighting
            // （原版 WorldDrawLayer_SingleTile/DebugTile 同款）。
            Vector3 center = Find.WorldGrid.GetTileCenter(tile);
            var verts = new List<Vector3>(TmpVerts.Count + 1)
            {
                center + center.normalized * 0.02f
            };
            for (int i = 0; i < TmpVerts.Count; i++)
            {
                Vector3 v = TmpVerts[i];
                verts.Add(v + v.normalized * 0.02f);
            }

            // 三角扇（中心为公共顶点）：跨三角形对 alpha 线性插值 = 中心→边缘径向渐变。
            TmpIndices.Clear();
            for (int j = 0; j < TmpVerts.Count; j++)
            {
                int a = 1 + j;
                int b = 1 + (j + 1) % TmpVerts.Count;
                TmpIndices.Add(b);
                TmpIndices.Add(a);
                TmpIndices.Add(0);
            }

            var mesh = new Mesh { name = "RimExodus_TileFill" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(new List<int>(TmpIndices), 0);
            mesh.RecalculateNormals();
            return new TileFillMesh { mesh = mesh };
        }

        private static void BakeStateColors(Mesh mesh, int state)
        {
            Color stateColor = StateColors[state];
            int vertCount = mesh.vertexCount;
            // [0] 中心最实，[1..n] 环形边缘全透（渐变由 GPU 对顶点色插值得到）。
            for (int i = 0; i < vertCount; i++)
            {
                float a = i == 0 ? CenterAlpha : 0f;
                TmpColors[i] = new Color32(
                    (byte)Mathf.RoundToInt(stateColor.r * 255f),
                    (byte)Mathf.RoundToInt(stateColor.g * 255f),
                    (byte)Mathf.RoundToInt(stateColor.b * 255f),
                    (byte)Mathf.RoundToInt(a * 255f));
            }
            var colors = new Color32[vertCount];
            System.Array.Copy(TmpColors, colors, vertCount);
            mesh.colors32 = colors;
        }

        /// <summary>释放 tile 的缓存 mesh（图删除/销毁 WorldObject 时调用，防 Mesh 累积泄漏）。</summary>
        public static void ReleaseTileMesh(PlanetTile tile)
        {
            if (TileMeshes.TryGetValue(tile, out var entry))
            {
                if (entry?.mesh != null) UnityEngine.Object.Destroy(entry.mesh);
                TileMeshes.Remove(tile);
            }
        }

    }

    /// <summary>
    /// 一条直接邻居连接：邻居地块引用 + 该邻居相对本地块的偏移 + 对应世界邻居 tile。
    /// 偏移语义：邻居本地坐标 + offset = 本地块坐标系的坐标（绘制邻居内容时平移用）。
    /// neighbor 类型为 MapParent 基类，可容纳原生 parent 地图（如家园 Settlement）和无缝地块 MapParent_SeamlessTile。
    /// worldTile 是该邻居在世界地图上的 tile id，作为邻居表主键（稳定，无角度歧义）。
    /// </summary>
    public class NeighborLink : IExposable
    {
        /// <summary>该邻居在世界地图上的 tile id（主键）。</summary>
        public int worldTile;

        /// <summary>邻居地块的 MapParent 引用（原生 parent 图或无缝地块）。</summary>
        public MapParent neighbor;

        /// <summary>邻居相对本地块的偏移（邻居本地 → 本地块）。</summary>
        public IntVec3 offset;

        public NeighborLink() { }

        public NeighborLink(int worldTile, MapParent neighbor, IntVec3 offset)
        {
            this.worldTile = worldTile;
            this.neighbor = neighbor;
            this.offset = offset;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_References.Look(ref neighbor, "neighbor");
            Scribe_Values.Look(ref offset, "offset");
        }
    }
}
