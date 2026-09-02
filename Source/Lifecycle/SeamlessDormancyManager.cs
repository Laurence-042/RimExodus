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
    /// 【与删除的二分】删除 = <see cref="SeamlessTileManager.RemoveRollingMap"/>（地块图销毁 Map +
    /// WorldObject，下次进入走生成链重建；原生家族延迟执行原版删除偏好）；休眠 = 本类
    /// （一切保留，只停模拟与显示）。
    /// </summary>
    public static class SeamlessDormancyManager
    {
        /// <summary>休眠中的地图集合（运行时状态，刻意不序列化——见类注释）。</summary>
        private static readonly HashSet<Map> dormantMaps = new HashSet<Map>();

        /// <summary>
        /// 手动休眠锁（2026-08，玩家世界图 gizmo）：锁定的休眠图不被 governor 的保活/距离回落
        /// 分支唤醒——只有玩家主动进图（CurrentMap setter）与 pawn 被命令接近其接缝
        /// （BorderPreloader 的 TryWakeByWorldTile）两类入口能唤醒（用户定夺）。
        /// 任意来源的 <see cref="Wake"/> 都会解除锁定。
        /// 休眠状态本身不序列化，但**手动锁随档保留**（2026-08 用户要求）：登记/解除同步桥接到
        /// <see cref="SeamlessDormancyGovernor"/> 的持久化 tile 集合，读档后 governor 首轮 Sweep
        /// 对锁内图以 manual:true 重新入睡。
        /// governor 的删除分支不受锁影响（距离 ≥ deleteHops 照常滚动删除）。
        /// </summary>
        private static readonly HashSet<Map> manualDormantMaps = new HashSet<Map>();

        /// <summary>map 是否处于休眠（null/Disposed 安全返回 false）。</summary>
        public static bool IsDormant(Map map)
        {
            return map != null && !map.Disposed && dormantMaps.Contains(map);
        }

        /// <summary>map 是否被玩家手动休眠（= 锁定不被自动唤醒）。对非休眠图恒 false。</summary>
        public static bool IsManuallyDormant(Map map)
        {
            return map != null && !map.Disposed && manualDormantMaps.Contains(map);
        }

        /// <summary>让地图进入休眠（幂等）。reason = 触发原因（无条件写入日志，休眠状态变化是低频事件）。
        /// manual = true（玩家 gizmo）时登记手动锁，见 <see cref="manualDormantMaps"/>。</summary>
        public static void Sleep(Map map, string reason, bool manual = false)
        {
            if (map == null || map.Disposed || dormantMaps.Contains(map)) return;

            // 降频 → 休眠升档：tick 注册即将全摘，降频集合一并清（保持三档互斥）。
            SeamlessTickThrottle.Unthrottle(map, "sleeping (dormancy supersedes throttle)");

            dormantMaps.Add(map);
            if (manual)
            {
                manualDormantMaps.Add(map);
                SyncManualTile(map, register: true);
            }

            // 摘除全局 Thing tick（原版 API，MapDeiniter.Deinit 同款——按 x.Map == map 从
            // tickListNormal/Rare/Long 与注册缓冲中全摘）。governor 的调用时点在 GameComponentTick
            // （TickManager.DoSingleTick 的 MapPostTick 循环之后），不会落在 thing tick 中途。
            Find.TickManager.RemoveAllFromMap(map);

            // 结束该图的声音 sustainer（Deinit 同款，防休眠后幽灵音——图不 tick 但音频树还挂着；
            // 天气域"决策集中 + 执行各图"模型下这是本图自己的实例，不影响其他成员图）。
            map.weatherManager.EndAllSustainers();
            Find.SoundRoot.sustainerManager.EndAllInMap(map);

            // 休眠 = 停摆（2026-09-02 天气域注册制事件②）：若本图是域激活图 → 还活着成员接任
            // （交接换天进度，域继续演化）；全停摆不动，首个唤醒者经 OnMapResumed 接任。
            SeamlessWeatherClusterManager.NotifySimulationSuspended(map, "dormancy sleep");

            // 两端传送点坐标缓存刷新：本图休眠后，对端（及本图）指向彼此的 spot 的 hasArrival
            // 应失效（link 查询被休眠口径过滤）。显式刷新使缓存立即收敛而非等下次惰性重算。
            RefreshSpotArrivalsBothSides(map);

            // 殖民者栏重算：顶部地图分组框不显示休眠图（Patch_ColonistBar_CheckRecacheEntries 过滤）。
            Find.ColonistBar?.MarkColonistsDirty();

            Log.Message($"[RimExodus] Dormancy SLEEP: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) — {reason}");
        }

        /// <summary>唤醒休眠地图（幂等；对未休眠图无操作）。同步轻量，可在任意调用栈执行。
        /// 唤醒 = 恢复全速（降频一并解除；活跃圈内无玩家 pawn 的图由 governor 下轮 Sweep 重新降频）。</summary>
        public static void Wake(Map map, string reason)
        {
            if (map == null || map.Disposed || !dormantMaps.Contains(map)) return;

            SeamlessTickThrottle.Unthrottle(map, "waking (dormant → full speed)");
            dormantMaps.Remove(map);
            if (manualDormantMaps.Remove(map)) // 任意来源的唤醒都解除手动锁（含 CurrentMap/预加载带）。
                SyncManualTile(map, register: false);

            // 重注册全局 Thing tick：遍历 spawnedThings 逐个注册（读档路径 FinalizeLoading 的
            // 重 spawn 走的就是同一 API，语义等价"这张图像刚读档一样恢复"）。
            // Map 守卫（2026-08 持有链审计）：innerList 可能残留"已活在别图"的陈旧条目（影子注入期 DeSpawn
            // 的 Remove 静默失败残留），重注册会造成 TickList 双注册（RegisterThing 无去重）= 活人双 tick。
            // 影子系统的 60t 轮询清扫（SeamlessShadowCaravan.ScrubAllMaps）为主，此处按 Map 过滤兜底。
            var spawned = map.spawnedThings;
            for (var i = 0; i < spawned.Count; i++)
            {
                if (spawned[i].Map != map) continue;
                Find.TickManager.RegisterAllTickabilityFor(spawned[i]);
            }

            // 天气状态校准（2026-09 形态 B）：休眠期本图 curWeatherAge 落后于激活图（换天广播会归零
            // 自愈，两次换天间的漂移在此补齐——lerp 进度/过渡视觉与域一致）。
            SeamlessWeatherClusterManager.OnMapResumed(map);
            RefreshSpotArrivalsBothSides(map);
            Find.ColonistBar?.MarkColonistsDirty();

            Log.Message($"[RimExodus] Dormancy WAKE: map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}) — {reason}");
        }

        /// <summary>
        /// 按 worldTile 唤醒休眠图（边界预加载带/生成守卫用）：WorldObject 在且带休眠 Map 则唤醒。
        /// 返回 false = 该 tile 无图或图本就活跃（调用方据此走"入队生成"路径）。
        /// 类型通用化（2026-08，勿回退为 as MapParent_SeamlessTile）：认任意 MapParent——玩家
        /// 家园图的原生 parent 不是 SeamlessTile，按 SeamlessTile 转型会让本查询对它失明（false），
        /// 边界带命中即入队生成，撞上生成守卫的同款类型盲就会造出重复家园图（"没有特殊地图"铁律：
        /// 守卫不得按 parent 类型区分处理）。
        /// </summary>
        public static bool TryWakeByWorldTile(int worldTile, string reason)
        {
            if (worldTile < 0 || Find.World == null) return false;
            var parent = Find.World.worldObjects.MapParentAt(new RimWorld.Planet.PlanetTile(worldTile));
            var map = parent?.Map;
            if (map == null || map.Disposed) return false;
            if (!IsDormant(map))
            {
                // 降频图同理（2026-08 分级休眠）：玩家 pawn 被命令接近其接缝 = 即将交互，
                // 提前恢复全速（governor 下轮 Sweep 会因玩家 pawn 临近自然维持活跃）。
                SeamlessTickThrottle.Unthrottle(map, "pawn approaching seam (border preload band)");
                return false;
            }
            Wake(map, reason);
            return true;
        }

        /// <summary>
        /// 把地图从休眠集合移除而不做任何恢复（图即将被销毁时用，<see cref="SeamlessTileManager.RemoveTileMap"/>
        /// 开头调用）——防止集合持有已 Dispose 的 Map 引用阻碍 GC。非销毁场景请用 <see cref="Wake"/>。
        /// </summary>
        internal static void Forget(Map map)
        {
            if (map != null)
            {
                SeamlessTickThrottle.Forget(map);
                dormantMaps.Remove(map);
                if (manualDormantMaps.Remove(map))
                    SyncManualTile(map, register: false); // 图销毁：残留 tile id 一并清出持久化集合。
                // 删图 = 该图全体 pawn 所在地批量变动 → 统一追踪底座（2026-08-29 收拢架构）：影子消费为
                // 删前全图清扫（一切删图路径的两个叶子都在 DeinitAndRemoveMap 之前到达这里），使 Deinit 的
                // DecrementMapIndex 索引补偿与持有链遍历永跑干净列表——不 patch MapDeiniter。
                SeamlessPawnLocationTracker.NotifyMapRemoving(map);
            }
        }

        /// <summary>
        /// 手动锁 ↔ governor 持久化集合的桥接（登记/解除）。静态集合并存为运行时真值，
        /// 桥接 null 安全（无 Game/无 governor 实例时只动静态集合——旧档/异常态不炸）。
        /// </summary>
        private static void SyncManualTile(Map map, bool register)
        {
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (tile < 0) return;
            var governor = Current.Game?.GetComponent<SeamlessDormancyGovernor>();
            if (governor == null) return;
            if (register) governor.RecordManualDormant(tile);
            else governor.ClearManualDormant(tile);
        }

        /// <summary>
        /// 刷新本图与所有直接邻居的传送点坐标缓存（休眠/唤醒的两端收尾）。
        /// 读原始邻居表（不经休眠过滤）——对端休眠时刷它无害且便宜，唤醒路径省一次惰性重算。
        /// </summary>
        private static void RefreshSpotArrivalsBothSides(Map map)
        {
            SeamlessEnterSpotPlacer.RefreshEnterSpotArrivals(map);

            var links = SeamlessMapData.Neighbors(map);
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
