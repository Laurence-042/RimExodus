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
        /// 在 GenStep_SeamlessTile（order=391）开头备份——此时全部基础地形写入步骤（Terrain 210 /
        /// Coast 220 / CoastalEdgeFill 230 / Roads 390）已跑完，Plants(900)/Animals(1200) 尚未
        /// （它们不写 topGrid）；400+ 的 BaseGen 地板写入被选址 patch 拦在接缝带之外（残余缺口见
        /// SeamlessTileGenerator.xml 观察项）。
        /// 供接缝条带快照捕获（SeamStripData.CaptureAndStore 读外条带格的原生值）。
        /// 非序列化：生成期临时数据，读档后为 null（原生图快照存组件、亦同）——
        /// 跨读档的接缝参考由 <see cref="seamStrip"/>（序列化）承担。
        /// </summary>
        public TerrainDef[] baseTerrainSnapshot;

        /// <summary>
        /// 原生建筑快照（岩石体 BuildingDef，null=无；与 <see cref="baseTerrainSnapshot"/> 同点位
        /// 备份、非序列化）。与地形快照平行的第二层：void 铺设会清除接缝带外格上的岩体，
        /// 接缝条带快照对外条带格必须引用**原生**两层（同源），否则世界连续的岩壁会被误判
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
        /// 接缝条带快照（接缝带 B ∪ 过渡带 T 最终值 + 接缝带外条带原生值）。
        /// 挂在 WorldObject 上（非 Map）：随存档序列化，**地图卸载后数据存活**——
        /// 地图滚动加载卸载的生命周期预埋（未来卸 Map、留 WorldObject 时，
        /// SeamlessTileGraph.TryGetNeighborSeamStrip 的 WorldObject 回落路径自动接管）。
        /// Dev 完全卸载（RemoveTileMap）销毁本对象即数据消失（"完全卸载 = 从未出现过"）。
        /// </summary>
        public SeamStripData seamStrip;

        public override string Label => "Seamless Tile Map";

        /// <summary>
        /// 世界图三态图标（2026-08）：useDynamicDrawer=true（def），<see cref="WorldObject.Draw"/>
        /// 每帧取 <see cref="WorldObject.Material"/> → 按运行时状态（休眠/活跃有人/活跃无人）换图标。
        /// 静态层 Material 按 def 缓存不可用；Material 由 <see cref="TileWorldIcons"/> 预构建缓存。
        /// </summary>
        public override UnityEngine.Material Material => TileWorldIcons.PickFor(this);

        /// <summary>
        /// 玩家在世界地图上的主动生命周期 gizmo（2026-08）：
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

            var map = Map;
            if (map == null || map.Disposed) yield break;
            if (!SeamlessMapGovernance.IsGoverned(map)) yield break;
            if (SeamlessMapGovernance.IsProtectedHome(map)) yield break; // 家园特权：不休眠不删除。
            if (IncrementalMapGenerator.IsGenerating(map)) yield break; // 分帧生成中不可干预。

            if (SeamlessDormancyManager.IsDormant(map))
            {
                var delete = new Command_Action
                {
                    defaultLabel = "RimExodus_DeleteTileMap".Translate(),
                    defaultDesc = "RimExodus_DeleteTileMapDesc".Translate(),
                    icon = TileWorldIcons.DeleteCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        // 销毁 Map+WorldObject（"从未出现过"，下次进入走生成链重建）。确认防误触。
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            "RimExodus_DeleteTileMapConfirm".Translate(Label), delegate
                        {
                            var manager = map.GetComponent<SeamlessTileManager>();
                            manager?.RemoveRollingMap(this);
                        }, destructive: true));
                    },
                };
                yield return delete;
            }
            else
            {
                bool isCurrentMap = map == Find.CurrentMap;
                var sleep = new Command_Action
                {
                    defaultLabel = "RimExodus_SleepTileMap".Translate(),
                    defaultDesc = "RimExodus_SleepTileMapDesc".Translate(),
                    icon = TileWorldIcons.SleepCommandIcon,
                    alsoClickIfOtherInGroupClicked = false,
                    action = delegate
                    {
                        SeamlessDormancyManager.Sleep(map, "player gizmo (world map)", manual: true);
                    },
                };
                if (isCurrentMap)
                {
                    // CurrentMap 是 governor 无条件保活项——睡着会立刻被视作异常，玩家须先切走再睡。
                    sleep.Disable("RimExodus_SleepTileMapDisabledCurrentMap".Translate());
                }
                yield return sleep;
            }
        }

        /// <summary>
        /// 阶段4前置：基础地图的 WorldObject 会进入世界视图静态绘制层（useDynamicDrawer=false）。
        /// override Print 为空操作，让地块在世界地图上不显示图标。
        /// 地块通过地图内叠加渲染呈现，不需世界视图图标。
        /// （2026-08 起 def 改 useDynamicDrawer=true 走 <see cref="WorldObject.Draw"/> 动态层，
        /// Print 仍为空操作——动态层不经过静态层 Print。）
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
            Scribe_Deep.Look(ref seamStrip, "seamStrip");

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
    /// 地块图世界图标的三态 Material 与 gizmo 图标缓存（2026-08，StaticConstructorOnStartup）。
    /// 三态 = 休眠（灰） / 活跃有人（绿+P） / 活跃无人（蓝紫）——玩家在世界地图上一眼区分
    /// 哪些 tile 有已生成地图、地图里有没有人（用户定夺）。
    /// Material 用与 <see cref="WorldObjectDef.Material"/> 同款 shader/altit 制构建各缓存一次，
    /// 每帧 PickFor 只做引用返回，勿在绘制路径新建 Material。
    /// </summary>
    public static class TileWorldIcons
    {
        private static Material _activeMat;
        private static Material _unmannedMat;
        private static Material _dormantMat;

        /// <summary>gizmo"休眠此图"图标（mod 自带，原版无现成 Suspend 命令图标）。</summary>
        public static Texture2D SleepCommandIcon { get; private set; }

        /// <summary>gizmo"删除此图"图标（mod 自带）。</summary>
        public static Texture2D DeleteCommandIcon { get; private set; }

        /// <summary>按地图运行时状态选三态 Material（无图/资源缺失返回 null = 不画）。</summary>
        public static Material PickFor(MapParent parent)
        {
            var map = parent?.Map;
            if (map == null || map.Disposed) return null;

            if (SeamlessDormancyManager.IsDormant(map)) return _dormantMat;
            // "有人"判定唯一出处 = SeamlessMapGovernance.HasPlayerPawn（与 governor 距离源同口径，
            // 勿在此自写 pawn 遍历——首版用 AllPawnsSpawnedCount 把野生动物也算有人）。
            if (SeamlessMapGovernance.HasPlayerPawn(map)) return _activeMat;
            return _unmannedMat;
        }

        private static Material BuildMat(string texturePath)
        {
            // 与 WorldObjectDef.Material 同款 shader/altit 制（renderQueue 3550）。
            return MaterialPool.MatFrom(texturePath, ShaderDatabase.WorldOverlayTransparentLit, 3550);
        }

        [Verse.StaticConstructorOnStartup]
        private static class StaticInit
        {
            static StaticInit()
            {
                _activeMat = BuildMat("World/WorldObjects/RimExodus_Tile_Active");
                _unmannedMat = BuildMat("World/WorldObjects/RimExodus_Tile_Unmanned");
                _dormantMat = BuildMat("World/WorldObjects/RimExodus_Tile_Dormant");
                SleepCommandIcon = ContentFinder<Texture2D>.Get("UI/Commands/RimExodus_SleepMap", reportFailure: false);
                DeleteCommandIcon = ContentFinder<Texture2D>.Get("UI/Commands/RimExodus_DeleteMap", reportFailure: false);
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
