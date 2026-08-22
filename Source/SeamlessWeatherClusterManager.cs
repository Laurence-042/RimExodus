using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 群系连通域天气共享（全局天气状态注册机制，用户定夺 2026-08——**无家园特殊论**：
    /// 家园图不特殊，任何图都可作为域宿主；旧"家园图专属天气共享"已废弃）。
    ///
    /// 【机制】天气共享域 = 世界图上"同群系（biome）且邻接连通"的 tile 分量中**有图**的成员集合。
    /// 例：赤道雨林 A—C 相邻同群系 → 同域；往北走到温带森林 D → 新域；再往东走到与 A—C
    /// 不连通（链被温带切断）的另一片雨林 F → 又是新域。每域共享一个天气源（宿主图的
    /// skyManager/weatherDecider/weatherManager 实例）；**宿主 = 域内最小 tileId 的图**
    /// （确定性选择——域结构不序列化，天气状态随宿主图序列化，读档后 FinalizeInit 重算重绑）。
    ///
    /// 【绑定方式】manager 字段替换（map.skyManager = host.skyManager 等）。三件套均非 MapComponent
    /// （WeatherManager/WeatherDecider/SkyManager 是普通类，不在 components 列表），tick/update 由
    /// 每张图的 MapPostTick/MapUpdate **走自己字段**调用——域内所有图的字段指向同一实例，任一活跃
    /// 成员都在推进它（软休眠图被跳过 MapPostTick 不参与，实例由成员字段引用保持存活）。
    /// 成员被替换掉的旧实例无人引用（GC 回收，不 tick、无冗余）。共享实例被多图重复 tick 的
    /// N 倍速问题由 <see cref="Patches_WeatherCluster"/> 的幂等守卫修复。
    ///
    /// 【触发】新图生成 onComplete（<see cref="BindMap"/>）；读档 FinalizeInit（<see cref="RebindAll"/>）；
    /// 任何图移除后（<see cref="SeamlessTileManager.RemoveTileMap"/> / RemoveRollingMap / Manager 的
    /// MapRemoved → <see cref="RebindAll"/>——宿主销毁时成员自动改绑新宿主。gravship 起飞销毁家园图的场景
    /// 由此覆盖，无需任何"家园善后"）。
    ///
    /// 【注册】Game.FillComponents 反射自动实例化所有 GameComponent 子类，无需 XML def。
    /// </summary>
    public class SeamlessWeatherClusterManager : GameComponent
    {
        public SeamlessWeatherClusterManager(Game game) { }

        public override void FinalizeInit()
        {
            // 读档后重算全部绑定：序列化会为共享 manager 产生多份实例（各图各存一份），
            // 重绑恢复共享关系（状态以宿主图那份为准）。
            RebindAll();
        }

        /// <summary>
        /// 绑定一张图到它的群系连通域（新图生成 onComplete 调用；幂等——已是宿主或已绑定则无操作）。
        /// 域内无其他图时本图即新域第一张（用自己的默认 manager，无需绑定）。
        ///
        /// 【机理勘误 2026-08】共享实例的推进**不依赖宿主图**：WeatherManager/WeatherDecider/
        /// SkyManager 均非 MapComponent，tick/update 由每张图的 MapPostTick/MapUpdate **走自己字段**
        /// 调用——域内所有图的字段指向同一实例，任一活跃成员都在推进它，实例由成员字段引用保持存活。
        /// 因此宿主休眠/换宿主**既不冻结也不跳变**（旧论断"宿主图 tick 是唯一驱动"是错误心智模型，
        /// 勿回退为"宿主必须活跃"的过滤——无行为差异且制造宿主特殊性错觉）。宿主的唯一实义：
        /// 存档时该图存这套实例的状态副本（各成员都存同一份，读档后 RebindAll 择一恢复共享）+
        /// 新成员 BindMap 读谁的字段（域内字段全指同一实例，读谁都一样）。
        /// </summary>
        public static void BindMap(Map map)
        {
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (tile < 0 || Find.WorldGrid == null) return;

            Map host = null;
            var hostTile = int.MaxValue;
            foreach (var m in Find.Maps)
            {
                var t = SeamlessTileRegistry.GetMapWorldTile(m);
                if (t < 0 || t >= hostTile || m == map) continue;
                if (!SameBiomeComponent(tile, t)) continue;
                hostTile = t;
                host = m;
            }
            if (host == null) return; // 新域第一张图：默认 manager 即域源

            map.skyManager = host.skyManager;
            map.weatherDecider = host.weatherDecider;
            map.weatherManager = host.weatherManager;
        }

        /// <summary>重算全部绑定（读档后 / 任何图移除后）。一次连通分量标记避免逐图 BFS 的平方复杂度。</summary>
        public static void RebindAll()
        {
            if (Find.WorldGrid == null) return;

            // tile → 分量 id（同群系连通）。只为有图的 tile 沿分量展开（一次 BFS 标记整分量）。
            var componentOf = new Dictionary<int, int>();
            var nextId = 0;
            var mapsByTile = new Dictionary<int, Map>();
            foreach (var m in Find.Maps)
            {
                var t = SeamlessTileRegistry.GetMapWorldTile(m);
                if (t >= 0) mapsByTile[t] = m;
            }
            foreach (var root in mapsByTile.Keys)
            {
                if (componentOf.ContainsKey(root)) continue;
                MarkBiomeComponent(root, nextId++, componentOf);
            }

            // 每分量内最小 tileId 的图为宿主，成员绑定到它。勘误 2026-08：见 BindMap 注释——
            // 共享实例由各活跃成员图走自己字段推进（与宿主活跃与否无关），此处不过滤休眠图；
            // 换宿主不换实例（域内成员字段本就全指同一实例），无冻结、无跳变。
            var hostOf = new Dictionary<int, Map>();
            foreach (var kv in mapsByTile)
            {
                var comp = componentOf[kv.Key];
                if (!hostOf.TryGetValue(comp, out var host) || kv.Key < SeamlessTileRegistry.GetMapWorldTile(host))
                {
                    hostOf[comp] = kv.Value;
                }
            }
            foreach (var kv in mapsByTile)
            {
                var host = hostOf[componentOf[kv.Key]];
                if (host == kv.Value) continue; // 宿主自己：默认 manager 即域源
                kv.Value.skyManager = host.skyManager;
                kv.Value.weatherDecider = host.weatherDecider;
                kv.Value.weatherManager = host.weatherManager;
            }
        }

        /// <summary>tileB 是否在 tileA 的"同群系邻接连通分量"内（世界图 BFS，纯世界数据，不依赖地图存在）。</summary>
        private static bool SameBiomeComponent(int tileA, int tileB)
        {
            if (tileA == tileB) return true;
            var biome = Find.WorldGrid[new PlanetTile(tileA)].PrimaryBiome;
            var visited = new HashSet<int> { tileA };
            var queue = new Queue<int>();
            queue.Enqueue(tileA);
            var neighbors = new List<PlanetTile>();
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                Find.WorldGrid.GetTileNeighbors(cur, neighbors);
                foreach (var n in neighbors)
                {
                    var nt = n.tileId;
                    if (!visited.Add(nt)) continue;
                    if (Find.WorldGrid[new PlanetTile(nt)].PrimaryBiome != biome) continue; // 跨群系切断传递
                    if (nt == tileB) return true;
                    queue.Enqueue(nt);
                }
            }
            return false;
        }

        /// <summary>从 root 出发 BFS 标记同群系连通分量（result[tile]=id；跨群系切断）。</summary>
        private static void MarkBiomeComponent(int root, int id, Dictionary<int, int> result)
        {
            var biome = Find.WorldGrid[new PlanetTile(root)].PrimaryBiome;
            var queue = new Queue<int>();
            queue.Enqueue(root);
            result[root] = id;
            var neighbors = new List<PlanetTile>();
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                Find.WorldGrid.GetTileNeighbors(cur, neighbors);
                foreach (var n in neighbors)
                {
                    var nt = n.tileId;
                    if (result.ContainsKey(nt)) continue;
                    if (Find.WorldGrid[new PlanetTile(nt)].PrimaryBiome != biome) continue;
                    result[nt] = id;
                    queue.Enqueue(nt);
                }
            }
        }
    }
}
