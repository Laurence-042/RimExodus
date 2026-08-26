using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 影子远行队 v2（2026-08，用户方案"据点地块常驻实例"）：据点图上的玩家 pawn **就是**远行队——
    /// 在有玩家自由殖民者的据点图（原生 Settlement parent）上自动维护一个**常驻**影子 <see cref="Caravan"/>，
    /// 成员 = 图上 FreeColonistsSpawned（按引用注入）。mod 无论何时（即时 / 延迟回调 / 跨帧闭包 /
    /// 自建窗口确认）读 caravan 都能查到 pawn——v1 的"同帧借用"对金鸢尾兰"外交交互"这类
    /// "点击只开 FloatMenu、真正逻辑在后续帧"的时序结构性失效（FindBestDiplomat 在 Release 后
    /// 拿空列表 → "无外交官"），常驻模型一次性消除全部时序耦合。
    ///
    /// 机制三件套：
    /// ①**成员注入**（v1 教训沿用）：<see cref="Caravan.AddPawn"/> 内部 DeSpawnOrDeselect 会把 pawn
    ///   从地图脱 spawn、<see cref="ThingOwner{T}.TryAdd"/> 检查 holdingOwner 拒绝图上 pawn——必须反射
    ///   直写 <see cref="ThingOwner{T}"/> 的 innerList（字段在泛型类上，反射目标必须带泛型参数）+
    ///   把 pawn.holdingOwner 临时改指影子容器（<see cref="ThingOwner.Contains(Thing)"/> 的判定就是
    ///   `item.holdingOwner == this`，<see cref="Caravan.IsOwner"/> 全走它——不转移则 FindBestDiplomat
    ///   等恒 null）。pawn 实体与地图注册全程不动。
    /// ②**副作用抑制**：影子唯一的行为面 = <see cref="Caravan.TickInterval"/>（needs/进食/
    ///   CheckAnyNonWorldPawns/pather……对图上 spawned pawn = 需求双计 + 真吃玩家库存），Prefix 按实例
    ///   身份整体跳过——影子纯数据视图，零 tick 行为。成员的需求照常由所在地图 tick（不缺不漏）。
    /// ③**GetCaravan 语义还原**：holdingOwner 指影子后 <see cref="Pawn.GetCaravan"/> 对图上殖民者恒
    ///   非空，原版 ~40 处消费面（心情/娱乐/殖民者栏/Dialog_Trade…）会误判"在远行队"——Postfix 对
    ///   影子返回 null。远行队身份判定不受影响：<see cref="Caravan.IsOwner"/> 走 pawns.Contains
    ///   （holdingOwner 比对）而非 GetCaravan。
    ///
    /// 维护：governor GameComponentTick 每 <see cref="RefreshIntervalTicks"/> 同步一次成员名单
    /// （进/离图/死亡自动跟随），读档后首轮自动重建——**影子不序列化**（pawn 是图上 pawn，随档
    /// 双存会变成真远行队成员；存档前 SaveGame Prefix 全量拆除）。
    ///
    /// 已知边界（观察项）：①世界图 tile 上据点与影子双图标、殖民者栏外的远行队 UI（Alert_CaravanIdle
    /// 等）可能对影子产生告警条目——已抑制 GetGizmos 防玩家下令移动影子；②mod 序列化影子引用
    /// （信件 lookTargets 等）读档后变 null；③"外交争锋"对影子执行 CreateFixedCaravan（转驻留
    /// 多日的 FixedCaravan）不拦截（用户定夺 2026-08：无法通用判定选项是否会转 fixed，出问题再
    /// 单独适配）；④刷新间隔内的名单过期由 IsConsciousOwner 的 Dead/Downed 检查兜底。
    /// </summary>
    public static class SeamlessShadowCaravan
    {
        private const int RefreshIntervalTicks = 60;

        /// <summary>tile → 影子。每 tile 至多一个（据点图唯一）。</summary>
        private static readonly Dictionary<int, Caravan> shadows = new();

        /// <summary>成员注入前的原容器（地图 spawnedThings），拆除/同步移除时归还。按 pawn 键控（名单跨刷新增减，顺序不稳定）。</summary>
        private static readonly Dictionary<Pawn, ThingOwner> originalOwners = new();

        private static readonly List<Pawn> tmpDesired = new();
        private static int nextRefreshTick;

        /// <summary>
        /// ThingOwner 内部列表反射（v1 实测教训）：innerList 声明在泛型类 ThingOwner&lt;T&gt; 上
        /// （非基类 ThingOwner），反射目标必须带泛型参数，取基类恒 null。
        /// </summary>
        private static readonly FieldInfo innerListField = AccessTools.Field(typeof(ThingOwner<Pawn>), "innerList");

        private static List<Pawn> PawnInnerList(ThingOwner<Pawn> owner)
        {
            return (List<Pawn>)innerListField?.GetValue(owner);
        }

        /// <summary>实例身份判定（patch 与维护共用）。影子数 ≤ 并发据点图数，线性查够用。</summary>
        public static bool IsShadow(Caravan caravan)
        {
            if (caravan == null) return false;
            foreach (var kv in shadows)
            {
                if (kv.Value == caravan) return true;
            }
            return false;
        }

        /// <summary>取某图对应 tile 的常驻影子（对话面板与交互入口用；无返回 false）。</summary>
        public static bool TryGetForMap(Map map, out Caravan shadow)
        {
            shadow = null;
            if (map == null) return false;
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            return tile >= 0 && shadows.TryGetValue(tile, out shadow) && shadow != null && !shadow.Destroyed;
        }

        /// <summary>
        /// 周期维护（governor GameComponentTick 每 tick 调，内部 60 ticks 间隔门控）：
        /// 为每个"有玩家自由殖民者的据点图"建/同步影子，拆掉失去条件的影子。读档重建也走这里
        /// （影子不序列化，首轮扫描自然补齐）。
        /// </summary>
        public static void TickMaintain()
        {
            if (Find.TickManager == null || Find.Maps == null) return;
            if (Find.TickManager.TicksGame < nextRefreshTick) return;
            nextRefreshTick = Find.TickManager.TicksGame + RefreshIntervalTicks;
            try
            {
                Refresh(new List<Map>(Find.Maps));
            }
            catch (System.Exception e)
            {
                Log.Error($"[RimExodus] Shadow caravan maintain failed: {e}");
            }
        }

        private static void Refresh(List<Map> maps)
        {
            // 期望集：tile → 据点图（原生 Settlement parent、表面层、有玩家自由殖民者）。
            var desired = new Dictionary<int, Map>();
            foreach (var map in maps)
            {
                if (map == null || map.Disposed) continue;
                if (map.Parent is not Settlement) continue;
                var tile = SeamlessTileRegistry.GetMapWorldTile(map);
                if (tile < 0) continue; // 口袋图/空间层（GetMapWorldTile 对其返回 -1）
                if (map.mapPawns.FreeColonistsSpawned.Count == 0) continue;
                desired[tile] = map;
            }

            // 拆除失去条件的影子（图没了 / 无玩家 pawn / parent 换类型）。
            var toRemove = new List<int>();
            foreach (var kv in shadows)
            {
                if (!desired.ContainsKey(kv.Key)) toRemove.Add(kv.Key);
            }
            foreach (var tile in toRemove) DismantleAt(tile, "condition lost (no map / no player pawns)");

            // 建/同步。
            foreach (var kv in desired)
            {
                if (!shadows.TryGetValue(kv.Key, out var shadow) || shadow == null || shadow.Destroyed)
                {
                    shadow = (Caravan)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Caravan);
                    shadow.SetFaction(Faction.OfPlayer);
                    shadow.SetUniqueId(Find.UniqueIDsManager.GetNextCaravanID());
                    shadow.Tile = kv.Key;
                    Find.WorldObjects.Add(shadow);
                    shadows[kv.Key] = shadow;
                    Log.Message($"[RimExodus] ShadowCaravan: created at tile {kv.Key} for '{kv.Value.Parent.Label}'.");
                }
                SyncMembership(shadow, kv.Value);
            }
        }

        /// <summary>同步成员名单 = 图上 FreeColonistsSpawned（按引用注入；增减自动跟随）。</summary>
        private static void SyncMembership(Caravan shadow, Map map)
        {
            var innerList = PawnInnerList(shadow.pawns);
            if (innerList == null)
            {
                // 反射缺失（ThingOwner 内部结构变更）——拆影子防半吊子状态。
                Log.Error("[RimExodus] ShadowCaravan: ThingOwner.innerList reflection failed; dismantling.");
                DismantleAt(shadow.Tile, "innerList reflection failed");
                return;
            }

            tmpDesired.Clear();
            tmpDesired.AddRange(map.mapPawns.FreeColonistsSpawned);

            // 先移除过期成员（离图/死亡/转囚犯），从尾往前删保索引稳定。
            for (int i = innerList.Count - 1; i >= 0; i--)
            {
                var p = innerList[i];
                if (p != null && !p.Destroyed && tmpDesired.Contains(p)) continue;
                if (p != null && p.holdingOwner == shadow.pawns)
                {
                    p.holdingOwner = TakeOriginalOwner(p) ?? null;
                }
                innerList.RemoveAt(i);
            }
            // 再补新成员（持有 holdingOwner 转移，见类注释①）。
            for (int i = 0; i < tmpDesired.Count; i++)
            {
                var p = tmpDesired[i];
                if (innerList.Contains(p)) continue;
                originalOwners[p] = p.holdingOwner;
                p.holdingOwner = shadow.pawns;
                innerList.Add(p);
            }
            tmpDesired.Clear();
        }

        /// <summary>拆除指定 tile 的影子（还 holdingOwner、清列表、摘出世界）。</summary>
        private static void DismantleAt(int tile, string reason)
        {
            if (!shadows.TryGetValue(tile, out var shadow) || shadow == null) return;
            try
            {
                var innerList = PawnInnerList(shadow.pawns);
                if (innerList != null)
                {
                    for (int i = 0; i < innerList.Count; i++)
                    {
                        var p = innerList[i];
                        if (p == null || p.holdingOwner != shadow.pawns) continue;
                        p.holdingOwner = TakeOriginalOwner(p) ?? null;
                    }
                    innerList.Clear();
                }
                if (shadow.Spawned) Find.WorldObjects.Remove(shadow);
                Log.Message($"[RimExodus] ShadowCaravan: dismantled at tile {tile} ({reason}).");
            }
            catch (System.Exception e)
            {
                Log.Warning($"[RimExodus] Shadow caravan dismantle failed at tile {tile}: {e.Message}");
            }
            finally
            {
                shadows.Remove(tile);
            }
        }

        /// <summary>全量拆除（存档前 SaveGame Prefix 调）：影子绝不随档序列化——其成员是图上 pawn，
        /// Caravan.ExposeData 的 Scribe_Deep(pawns) 会把它们双存成远行队成员。读档后由 TickMaintain 重建。</summary>
        public static void EnsureReleasedForSave()
        {
            var tiles = new List<int>(shadows.Keys);
            foreach (var tile in tiles) DismantleAt(tile, "save");
        }

        /// <summary>取回成员注入前的原容器并移除记录；无记录返回 null。</summary>
        private static ThingOwner TakeOriginalOwner(Pawn p)
        {
            if (originalOwners.TryGetValue(p, out var owner))
            {
                originalOwners.Remove(p);
                return owner;
            }
            return null;
        }
    }

    /// <summary>
    /// 影子远行队的三个行为面 patch（2026-08 v2 常驻模型）：
    /// ①<see cref="Caravan"/>.TickInterval 整体跳过——影子零 tick 行为（需求/进食/pather 全免疫），
    ///   纯数据视图；②<see cref="Pawn.GetCaravan"/> 对影子返回 null——还原原版 ~40 处消费面的地图语义
    ///   （holdingOwner 指影子不应改变地图行为；Caravan.IsOwner 走 pawns.Contains 不受影响）；
    /// ③<see cref="Caravan.GetGizmos"/> 清空——防玩家在世界图选中影子下令移动/合并（影子是投影，
    ///   被移动 = 投影与图脱钩）。
    /// </summary>
    internal static class Patches_ShadowCaravan
    {
        // TickInterval 是 protected override，按名绑定（字符串名 + 实例参数）。
        [HarmonyPatch(typeof(Caravan), "TickInterval")]
        static class Patch_ShadowCaravan_SkipTickInterval
        {
            static bool Prefix(Caravan __instance)
            {
                return !SeamlessShadowCaravan.IsShadow(__instance); // true = 照常原方法
            }
        }

        // GetCaravan 是 CaravanUtility 上的扩展方法（this Pawn），patch 挂定义类、实例参数为 Pawn。
        [HarmonyPatch(typeof(CaravanUtility), nameof(CaravanUtility.GetCaravan))]
        static class Patch_ShadowCaravan_GetCaravanNull
        {
            static void Postfix(Pawn __instance, ref Caravan __result)
            {
                if (SeamlessShadowCaravan.IsShadow(__result)) __result = null;
            }
        }

        [HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
        static class Patch_ShadowCaravan_NoWorldGizmos
        {
            static readonly Gizmo[] empty = new Gizmo[0];
            static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
            {
                if (SeamlessShadowCaravan.IsShadow(__instance)) __result = empty;
            }
        }
    }
}
