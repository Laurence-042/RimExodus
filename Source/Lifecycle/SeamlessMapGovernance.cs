using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 地图治理判定归一层（2026-08 用户定夺）。一切「哪些图受 RimExodus 滚动管辖 / 哪些图有
    /// 家园特权 / 哪些图可滚动删除」的判定只从这里问——上层（governor / Harmony patch / Dev 菜单 /
    /// 生成流程）不得自写 parent 类型特判。
    ///
    /// 历史教训（本类存在的理由）：口袋地图时代的"锚点"概念（IsAnchorMap，已删除——名称歧义，
    /// 勿复用）令特权判定散落 8 处且口径漂移——家园图"睡而不删"的不对称（governor 保活无家园
    /// 豁免、删除有类型门）与 MapGenerated/governor 的管辖门不一致皆源于此。底层单点判定 &
    /// 顶层只消费，比顶层各 patch 一份稳定、一致、可维护。
    ///
    /// 概念（"一切地图对等"铁律下的三分）：
    /// - 地块图：parent 为 <see cref="MapParent_SeamlessTile"/>（RimExodus 自己的 parent 类型）。
    /// - 原生家族：原版"全员离开即删图"语义的原生 parent（Settlement/Site/Camp/CaravansBattlefield/
    ///   DestroyedSettlement）——图与地块图同享滚动生命周期（人走不删、距离删），被动删除由
    ///   <see cref="Patches_NativeMapFamily"/> 在 CheckRemoveMapNow 收口拦截，删除时机移交
    ///   governor（<see cref="SeamlessTileManager.RemoveRollingMap"/>）。
    /// - 家园特权：原版 <see cref="Map.IsPlayerHome"/>（开局家园/定居/逆重飞船降落/建了引力引擎
    ///   的营地）——不休眠不删除，全项目唯一行为消费点 = governor 保活分支。
    /// </summary>
    internal static class SeamlessMapGovernance
    {
        /// <summary>
        /// 原版"全员离开即删图"的原生 parent 类（ShouldRemoveMapNow 的全部表面层覆写类；第 6 个
        /// 覆写类 SpaceMapParent 属空间层，被下方的表面层守卫排除——其 tileId 不在表面 WorldGrid）。
        /// 类判定（is）而非 def 判定：mod 子类继承原生删除语义，自然纳入。含 Camp：RimExodus def
        /// 缺失时 Patches_CampTileMap 放行原版 Camp，仍需滚动接管。地块图（MapParent_SeamlessTile）
        /// 恒 false——它不是原生家族，生命周期本就归 governor。
        /// </summary>
        internal static bool IsNativeFamily(MapParent parent)
        {
            if (parent == null || parent is MapParent_SeamlessTile || parent.Destroyed) return false;
            // 表面层守卫：轨道层 Site 等（有效 tileId 但 layer ≠ Surface）不打表面 WorldGrid——
            // 纳入管辖会用空间 tileId 查表面邻居表（距离/几何全错），且其删除语义应保持原版。
            var tile = parent.Tile;
            if (!tile.Valid || tile.LayerDef != PlanetLayerDefOf.Surface) return false;
            return parent is Settlement || parent is Site || parent is Camp
                || parent is CaravansBattlefield || parent is DestroyedSettlement;
        }

        /// <summary>
        /// 受 RimExodus 滚动管辖：地块图 ∪ 原生家族。governor 扫描范围 = 本判定。
        /// 玩家家园 Settlement 也在家族内（入辖以获得保活唤醒），睡/删豁免由家园特权承担。
        /// </summary>
        internal static bool IsGoverned(Map map)
        {
            if (map == null || map.Disposed) return false;
            var parent = map.Parent;
            return parent is MapParent_SeamlessTile || IsNativeFamily(parent);
        }

        /// <summary>
        /// 家园特权（不休眠、不删除，2026-08 用户定夺）：原版 <see cref="Map.IsPlayerHome"/>——
        /// 开局家园/定居/gravship 降落产生的原生 Settlement 均为 true；任何图上建了引力引擎
        /// （含地块图营地）经 GravshipUtility 兜底亦为 true。玩家搬离后的旧家园 Settlement 在
        /// 原版语义下仍 IsPlayerHome（原版也从不删它），保持一致。
        /// </summary>
        internal static bool IsProtectedHome(Map map)
        {
            return map != null && !map.Disposed && map.IsPlayerHome;
        }

        /// <summary>
        /// 可滚动删除：受管辖且非家园。governor 删除分支与 Dev Force Delete 共用；
        /// 家园在 governor 侧由保活分支先行豁免，本判定是显式第二道单点。
        /// </summary>
        internal static bool CanRollingDelete(Map map)
        {
            return IsGoverned(map) && !IsProtectedHome(map);
        }

        /// <summary>
        /// 图上是否有玩家阵营 spawned pawn（2026-08 收口："有人"判定的唯一出处）。
        /// 消费方 = governor 距离源（Sweep 的 sources 构建）与世界图三态图标（TileWorldIcons）——
        /// 上层勿再自写 pawn 遍历（曾散落两份致口径漂移：图标首版用 AllPawnsSpawnedCount 把
        /// 野生动物/访客也算"有人"，地块图几乎恒显有人态）。口径 = Faction == OfPlayer，
        /// 与保活条件"玩家 pawn 在场"同义。
        /// </summary>
        internal static bool HasPlayerPawn(Map map)
        {
            if (map == null || map.Disposed) return false;
            var player = Faction.OfPlayerSilentFail;
            if (player == null) return false;
            var pawns = map.mapPawns?.AllPawnsSpawned;
            if (pawns == null) return false;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i].Faction == player) return true;
            }
            return false;
        }

        /// <summary>
        /// 图上是否存在活跃敌人（2026-09 威胁保活，"活跃敌人"判定的唯一出处）。消费方 = governor
        /// Sweep 的威胁发现与追踪轮询；口径与用户定夺：
        /// - **零排除分支**（用户定夺 2026-09）：驻军、炮塔一律不排除——语义始终是"活跃敌人"，
        ///   一致性优先；已知后果 = 敌对据点/Site 图因常驻驻军恒保活（活跃圈内不降频），记录为
        ///   定夺行为而非缺陷。倒地/逃跑/休眠/雾中目标仍不算"活跃"（原版谓词内置，属"活跃"语义
        ///   本身而非额外排除）。
        /// - **虫激怒窄判**（用户定夺 2026-09）：与原版地图威胁/撤退判定同语义——守巢/激进守巢
        ///   的虫不算（原版 <c>IsActiveThreatTo</c> 对 LordJob_DefendAndExpandHive 且非
        ///   AssaultColony duty 的豁免），只有真正发起进攻的虫才算。
        /// 主判定直接复用原版 <see cref="GenHostility.AnyHostileActiveThreatToPlayer(Map)"/>
        /// （走 attackTargetsCache 事件增量缓存，非全图 pawn 遍历；官方先例 = 原版撤离门
        /// CaravanExitMapUtility / CompShuttle 的"图上有威胁"判定）。蹒跚怪单独补充：动物版猎群
        /// faction==null 不进敌对缓存桶（GenHostility 的 shambler 专用分支判其非敌对），走
        /// <see cref="MapPawns.SpawnedShamblers"/> 专用列表；无 Anomaly 时该列表恒空，天然安全。
        /// </summary>
        internal static bool HasActiveThreat(Map map)
        {
            if (map == null || map.Disposed) return false;
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return true;

            var shamblers = map.mapPawns?.SpawnedShamblers;
            if (shamblers == null) return false;
            for (int i = 0; i < shamblers.Count; i++)
            {
                var s = shamblers[i];
                if (s != null && !s.Dead && !s.Downed) return true;
            }
            return false;
        }
    }
}
