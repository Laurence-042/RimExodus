using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 跨图索敌与射击的统一坐标层（阶段5）。
    ///
    /// 统一坐标以"宿主图"（射手/查询方所在图）为基准：unified = targetLocal + offset
    /// （邻居表契约原式；逆向 targetLocal = unified − offset，与
    /// CompSeamlessTileEnterSpot.ComputeAndCacheArrival 同式）。
    /// 中点对齐保证邻图接缝带（传送圈/离散边圈）的统一格落在本图方形内侧的 void 区
    /// （边中点距方形边约 13-17 格），因此 LOS/射程/弹道都能在本图坐标上计算；
    /// 逐格真实阻挡由格归属方在各自地图查询。
    ///
    /// 逐格归属统一口径 = <see cref="SeamlessTileRegistry.TryGetOwnerNeighbor"/>：
    /// true → 格属某邻图（同时返回其本地坐标，保证 in-bounds）；false → 格属本图或角部双 void
    /// （留在本图处理；void 无 edifice，LOS 判定天然透明）。LOS 分段与弹丸缝交接共用此判定，
    /// 凸多边形保证"线段两端皆本图格 ⟺ 中间无越界"。
    /// 休眠邻居取不到连接（<see cref="SeamlessTileGraph"/> 过滤）——休眠 = 不可射也不回击，
    /// 与 doc/地图滚动休眠.md 的"休眠=跟丢"语义一致。
    /// </summary>
    public static class SeamlessCombatCoords
    {
        /// <summary>
        /// 跨图战斗连接：host 与 target 互为活跃邻居 + 坐标换算 offset。
        /// </summary>
        public struct CombatLink
        {
            public Map host;
            public Map target;
            public int targetWorldTile;
            /// <summary>offset 契约：unified(host) = targetLocal + offset。</summary>
            public IntVec3 offset;
        }

        internal static bool Enabled => RimExodusMod.Settings?.crossMapCombatEnabled ?? true;

        /// <summary>取 host→target 的跨图战斗连接。同图/非邻居/休眠/开关关闭均返回 false。</summary>
        public static bool TryGetCombatLink(Map host, Map target, out CombatLink link)
        {
            link = default(CombatLink);
            if (!Enabled || host == null || target == null || host == target || host.Disposed || target.Disposed) return false;

            var targetTile = SeamlessTileRegistry.GetMapWorldTile(target);
            if (targetTile < 0) return false;
            if (!SeamlessTileGraph.TryGetNeighborLinkByWorldTile(host, targetTile, out var info)) return false;

            link = new CombatLink
            {
                host = host,
                target = info.map,
                targetWorldTile = info.worldTile,
                offset = info.offset
            };
            return true;
        }

        // —— 坐标换算 ——
        public static IntVec3 ToUnified(IntVec3 targetLocal, in CombatLink link) => targetLocal + link.offset;
        public static IntVec3 ToTargetLocal(IntVec3 unified, in CombatLink link) => unified - link.offset;

        /// <summary>
        /// 本图是否有活跃接缝邻居（弹丸交接的零分配快速门）。无邻居的图（孤岛图等）直接短路。
        /// 与 <see cref="SeamlessTileGraph.PopulateNeighbors"/> 同口径（休眠过滤），但遍历邻居链表本身、不建列表。
        /// </summary>
        internal static bool HasActiveSeamNeighbors(Map map)
        {
            if (map == null) return false;
            var links = SeamlessMapData.Neighbors(map);
            if (links == null) return false;

            foreach (var l in links)
            {
                var m = l?.neighbor?.Map;
                if (m == null || m.Disposed) continue;
                if (!SeamlessDormancyManager.IsDormant(m)) return true;
            }
            return false;
        }

        // —— 跨图施法上下文 ——
        // Verb_LaunchProjectile.TryCastShot 开头有"目标图 ≠ 施法者图 → return false"的原版硬拒绝。
        // 射击链到达 TryCastShot 前必经 TryStartCastOn → CanHitTarget →（我们接管的）
        // TryFindShootLineFromTo 验证——在那里登记"本 verb 的跨图目标图"，TryCastShot 的
        // transpiler 门（TargetMapForCastGate）据此在跨图有效时放行。burst 后续发不重验证，
        // 但 currentTarget 不变（目标图匹配即有效）；目标跨缝换图则匹配失败 → 原版语义（burst 中断）。
        private static readonly ConditionalWeakTable<Verb, Map> CrossMapCastTargets = new ConditionalWeakTable<Verb, Map>();

        internal static void MarkCrossMapCast(Verb verb, Map targetMap)
        {
            CrossMapCastTargets.Remove(verb);
            CrossMapCastTargets.Add(verb, targetMap);
        }

        /// <summary>
        /// TryCastShot transpiler 的替换调用（原 currentTarget.Thing.Map 取值点）：
        /// 跨图有效（上下文登记的目标图 == 实际目标图且仍互为活跃邻居）时返回施法者图，
        /// 使原版 != 比较通过；否则返回真实目标图（原语义）。
        /// </summary>
        internal static Map TargetMapForCastGate(Verb verb)
        {
            var tMap = verb.CurrentTarget.Thing?.Map;
            var cMap = VerbCaster(verb)?.Map;
            if (tMap != null && cMap != null && tMap != cMap
                && CrossMapCastTargets.TryGetValue(verb, out var marked) && marked == tMap
                && TryGetCombatLink(cMap, tMap, out _))
            {
                if (RimExodusMod.Settings?.verboseLogging ?? false)
                    Log.Message($"[RimExodus] Cross-map shot admitted: {VerbCaster(verb).LabelShort} -> {verb.CurrentTarget.Thing.LabelShort}.");
                return cMap;
            }
            return tMap;
        }

        private static readonly AccessTools.FieldRef<Verb, Thing> CasterRef = AccessTools.FieldRefAccess<Verb, Thing>("caster");

        /// <summary>Verb.caster 是 protected 字段（无公开 getter），patch 侧统一经此读取。</summary>
        internal static Thing VerbCaster(Verb verb) => verb == null ? null : CasterRef(verb);
    }
}
