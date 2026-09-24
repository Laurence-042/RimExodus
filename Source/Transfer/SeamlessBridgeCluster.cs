using System;
using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// NPC 战斗桥接的空间聚类选点共享（2026-09 perf probe 实证后的定向优化，用户定夺）：
    /// 双侧全图泛洪选点（<see cref="SeamlessCrossMapOrders.TryFindBestBridgeSpot"/>）单次 ~99ms，
    /// 大规模袭击（shambler 尸潮）每个体每 job 重下发都重算——实测 200 敌 600 ticks 内 400 次
    /// 泛洪 ≈ 40s，是袭击期卡顿的绝对主因（索敌扫描/CanBridgeTo 实测均可忽略）。
    ///
    /// 方案 = 按"位置空间桶"聚类共享：同一桶（半径 <see cref="ClusterRadiusCells"/>）内同 def 的
    /// 敌人在 <see cref="ShareWindowTicks"/> 窗口内复用同一选点结果——整群敌人按位置自然分成
    /// 几个聚类、各自独立选点（避免全员挤同一条过缝路），每聚类每窗口只跑一次泛洪。
    /// 桶粒度的近似远小于接战半径/警戒圈语义，行为上无感知差异；过缝后仍由各自 Grant 的
    /// FinalDest/NextJob 续程，选点共享只复用"从哪过缝"。
    ///
    /// 适用边界：仅 NPC 战斗桥接（StartPath 包装的 nextJob 非 playerForced 路径）；玩家下令
    /// （点击 nextJob==null / playerForced 包装）与 VF 载具不共享，保持联合最优全精度。
    /// 不序列化（窗口级运行时状态）；图删除经 <see cref="NotifyMapRemoved"/> 清扫（键持有 Map
    /// 引用，必须随图释放，对齐 SeamBandCache 纪律）。
    /// </summary>
    internal static class SeamlessBridgeCluster
    {
        /// <summary>空间桶边长（聚类半径，格）。</summary>
        internal const int ClusterRadiusCells = 24;

        /// <summary>选点结果共享窗口（ticks）：远短于战斗 job expiry（360-480）。</summary>
        internal const int ShareWindowTicks = 150;

        /// <summary>条目上限（超限先清过期、仍超则整表清空——桶粒度下正常量级远低于此）。</summary>
        private const int MaxEntries = 128;

        private readonly struct Entry
        {
            internal readonly Thing ExitSpot;
            internal readonly IntVec3 GotoCell;
            internal readonly int Tick;

            internal Entry(Thing exitSpot, IntVec3 gotoCell, int tick)
            {
                ExitSpot = exitSpot;
                GotoCell = gotoCell;
                Tick = tick;
            }
        }

        private static readonly Dictionary<(Map from, Map to, int bx, int bz, ThingDef def), Entry> shares = new();

        /// <summary>
        /// 查本桶的共享结论。返回 true = 窗口内已有结论：exitSpot 非 null = 共享选点（已校验
        /// TTL + spot 活性与指向）；exitSpot == null = 本桶本窗口已知无可达 spot（负面结论同样
        /// 共享——否则整群不可达的敌人会各自重跑全图泛洪白烧）。false = 无结论，调用方自行选点。
        /// </summary>
        internal static bool TryGetSharedSpot(Pawn pawn, Map toMap, int now, out Thing exitSpot, out IntVec3 gotoCell)
        {
            exitSpot = null;
            gotoCell = IntVec3.Invalid;
            var pos = pawn.Position;
            var key = (pawn.Map, toMap, pos.x / ClusterRadiusCells, pos.z / ClusterRadiusCells, pawn.def);
            if (!shares.TryGetValue(key, out var e)) return false;
            if (now - e.Tick > ShareWindowTicks) return false;
            if (e.ExitSpot == null) return true; // 负面结论
            if (!e.ExitSpot.Spawned || e.ExitSpot.Destroyed || e.ExitSpot.Map != pawn.Map) return false;
            // spot 的 arrival 缓存可能刷新指向他图，命中时必须仍指向 toMap。
            var comp = e.ExitSpot.TryGetComp<CompSeamlessTileEnterSpot>();
            if (comp == null || !comp.hasArrival || comp.targetWorldTile != SeamlessTileRegistry.GetMapWorldTile(toMap)) return false;
            exitSpot = e.ExitSpot;
            gotoCell = e.GotoCell;
            return true;
        }

        /// <summary>记录本桶选点结论（exitSpot 传 null = 记录负面结论；同桶后续敌人窗口内直接复用）。</summary>
        internal static void RecordSharedSpot(Pawn pawn, Map toMap, int now, Thing exitSpot, IntVec3 gotoCell)
        {
            if (shares.Count >= MaxEntries) Prune(now);
            var pos = pawn.Position;
            shares[(pawn.Map, toMap, pos.x / ClusterRadiusCells, pos.z / ClusterRadiusCells, pawn.def)] = new Entry(exitSpot, gotoCell, now);
        }

        /// <summary>图删除时清扫涉及该图的条目（MapRemoved 公共收口调用——键持 Map 引用防泄漏）。</summary>
        internal static void NotifyMapRemoved(Map map)
        {
            if (map == null) return;
            List<(Map, Map, int, int, ThingDef)> stale = null;
            foreach (var pair in shares)
                if (pair.Key.from == map || pair.Key.to == map)
                    (stale ??= new List<(Map, Map, int, int, ThingDef)>()).Add(pair.Key);
            if (stale != null)
                foreach (var key in stale)
                    shares.Remove(key);
        }

        private static void Prune(int now)
        {
            List<(Map, Map, int, int, ThingDef)> stale = null;
            foreach (var pair in shares)
                if (now - pair.Value.Tick > ShareWindowTicks || (pair.Value.ExitSpot != null && !pair.Value.ExitSpot.Spawned))
                    (stale ??= new List<(Map, Map, int, int, ThingDef)>()).Add(pair.Key);
            if (stale != null)
                foreach (var key in stale)
                    shares.Remove(key);
            if (shares.Count >= MaxEntries) shares.Clear(); // 极端量级（理论上不达）整表重来，成本可忽略
        }
    }
}
