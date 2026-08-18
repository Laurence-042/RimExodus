using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图软休眠核心（用户定夺 2026-08）。
    ///
    /// 【模型】休眠图的 Map 对象**保留在 Find.Maps 中不卸载**，照常随档全量序列化
    /// （存档零额外机制——建筑/物品/地形完好，"不丢东西"由模型本身保证）。
    /// 休眠只意味着三件事：
    /// 1. 不 tick（MapPreTick/MapPostTick/MapUpdate 被 <see cref="Patches_Dormancy"/> 跳过，
    ///    且图上 Thing 从全局 TickList 摘除——原版 Thing tick 表按 TickerType 全局分桶、
    ///    不按地图分组，只跳过 Map 方法停不掉 Thing）；
    /// 2. 不作为邻接地图显示（<see cref="SeamlessTileGraph"/> 的邻接/已加载口径过滤休眠图，
    ///    渲染回到零邻居清色帧、跨图菜单/传送点关闭、撤离链 tier 判定视对端为"未加载"）；
    /// 3. 无地图访问入口（殖民者栏因无玩家 pawn 自动不显示；世界地图"查看地图"gizmo 保留为
    ///    显式唤醒入口——点击经 Game.CurrentMap setter 的 patch 同步唤醒后进图）。
    ///
    /// 【唤醒】= 恢复 tick 注册 + 恢复邻接显示 + 恢复入口。同步轻量（遍历 spawnedThings 重注册
    /// tick，无生成、无分帧），可在任意调用栈安全执行（RegisterAllTickabilityFor 与原版
    /// "tick 中途 spawn"场景同源安全）。
    ///
    /// 【不序列化】休眠状态是纯运行时性能状态：读档后所有图自动恢复活跃（tick 注册由
    /// FinalizeLoading 的重 spawn 完成），<see cref="SeamlessDormancyGovernor"/> 首轮扫描
    /// 重新收敛（≥休眠跳数的图转入休眠）。因此不存在"读档时 CurrentMap 处于休眠"的窗口问题。
    ///
    /// 【与删除的二分】删除 = <see cref="SeamlessTileManager.RemoveTileMap"/>（销毁 Map +
    /// WorldObject，下次进入走生成链重建）；休眠 = 本类（一切保留，只停模拟与显示）。
    /// </summary>
    public static class SeamlessDormancyManager
    {
        /// <summary>休眠中的地图集合（运行时状态，刻意不序列化——见类注释）。</summary>
        private static readonly HashSet<Map> dormantMaps = new HashSet<Map>();

        /// <summary>map 是否处于休眠（null/Disposed 安全返回 false）。</summary>
        public static bool IsDormant(Map map)
        {
            return map != null && !map.Disposed && dormantMaps.Contains(map);
        }

        /// <summary>让地图进入休眠（幂等）。reason = 触发原因（无条件写入日志，休眠状态变化是低频事件）。</summary>
        public static void Sleep(Map map, string reason)
        {
            if (map == null || map.Disposed || dormantMaps.Contains(map)) return;

            dormantMaps.Add(map);

            // 摘除全局 Thing tick（原版 API，MapDeiniter.Deinit 同款——按 x.Map == map 从
            // tickListNormal/Rare/Long 与注册缓冲中全摘）。governor 的调用时点在 GameComponentTick
            // （TickManager.DoSingleTick 的 MapPostTick 循环之后），不会落在 thing tick 中途。
            Find.TickManager.RemoveAllFromMap(map);

            // 结束该图的声音 sustainer（Deinit 同款，防休眠后幽灵音——图不 tick 但音频树还挂着；
            // 天气域共享的 ambientSustainer 若有活跃成员在听，其下一 tick 的 AmbientSoundsTick 自愈重启）。
            map.weatherManager.EndAllSustainers();
            Find.SoundRoot.sustainerManager.EndAllInMap(map);

            // 天气域无需处理：共享实例由各活跃成员图走自己字段推进（见 SeamlessWeatherClusterManager
            // BindMap 注释的机理勘误），休眠不冻结、绑定不变。

            // 两端传送点坐标缓存刷新：本图休眠后，对端（及本图）指向彼此的 spot 的 hasArrival
            // 应失效（link 查询被休眠口径过滤）。显式刷新使缓存立即收敛而非等下次惰性重算。
            RefreshSpotArrivalsBothSides(map);

            // 殖民者栏重算：顶部地图分组框不显示休眠图（Patch_ColonistBar_CheckRecacheEntries 过滤）。
            Find.ColonistBar?.MarkColonistsDirty();

            Log.Message($"[RimExodus] Dormancy SLEEP: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) — {reason}");
        }

        /// <summary>唤醒休眠地图（幂等；对未休眠图无操作）。同步轻量，可在任意调用栈执行。</summary>
        public static void Wake(Map map, string reason)
        {
            if (map == null || map.Disposed || !dormantMaps.Contains(map)) return;

            dormantMaps.Remove(map);

            // 重注册全局 Thing tick：遍历 spawnedThings 逐个注册（读档路径 FinalizeLoading 的
            // 重 spawn 走的就是同一 API，语义等价"这张图像刚读档一样恢复"）。
            var spawned = map.spawnedThings;
            for (var i = 0; i < spawned.Count; i++)
            {
                Find.TickManager.RegisterAllTickabilityFor(spawned[i]);
            }

            SeamlessWeatherClusterManager.RebindAll(); // 幂等重算；正常情况下休眠唤醒不改变绑定（见勘误）。
            RefreshSpotArrivalsBothSides(map);
            Find.ColonistBar?.MarkColonistsDirty();

            Log.Message($"[RimExodus] Dormancy WAKE: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) — {reason}");
        }

        /// <summary>
        /// 按 worldTile 唤醒休眠图（边界预加载带/生成守卫用）：WorldObject 在且带休眠 Map 则唤醒。
        /// 返回 false = 该 tile 无图或图本就活跃（调用方据此走"入队生成"路径）。
        /// </summary>
        public static bool TryWakeByWorldTile(int worldTile, string reason)
        {
            if (worldTile < 0 || Find.World == null) return false;
            var parent = Find.World.worldObjects.MapParentAt(new RimWorld.Planet.PlanetTile(worldTile)) as MapParent_SeamlessTile;
            var map = parent?.Map;
            if (map == null || map.Disposed) return false;
            if (!IsDormant(map)) return false;
            Wake(map, reason);
            return true;
        }

        /// <summary>
        /// 把地图从休眠集合移除而不做任何恢复（图即将被销毁时用，<see cref="SeamlessTileManager.RemoveTileMap"/>
        /// 开头调用）——防止集合持有已 Dispose 的 Map 引用阻碍 GC。非销毁场景请用 <see cref="Wake"/>。
        /// </summary>
        internal static void Forget(Map map)
        {
            if (map != null) dormantMaps.Remove(map);
        }

        /// <summary>
        /// 刷新本图与所有直接邻居的传送点坐标缓存（休眠/唤醒的两端收尾）。
        /// 读原始邻居表（不经休眠过滤）——对端休眠时刷它无害且便宜，唤醒路径省一次惰性重算。
        /// </summary>
        private static void RefreshSpotArrivalsBothSides(Map map)
        {
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(map);

            List<NeighborLink> links;
            if (map.Parent is MapParent_SeamlessTile tileParent)
            {
                links = tileParent.neighbors;
            }
            else
            {
                links = map.GetComponent<SeamlessTileManager>()?.neighbors;
            }
            if (links == null) return;

            foreach (var link in links)
            {
                var neighborMap = link?.neighbor?.Map;
                if (neighborMap == null || neighborMap.Disposed) continue;
                SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(neighborMap);
            }
        }
    }
}
