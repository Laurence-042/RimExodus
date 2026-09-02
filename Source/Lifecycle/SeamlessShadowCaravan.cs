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
    /// ②**零副作用模型（2026-08-26 方案 B）**：影子**不进 Find.WorldObjects**（不 Add）——不 tick
    ///   （WorldObject.DoTick 只遍历世界对象表，成员需求照常由所在地图 tick）、不序列化、殖民者栏
    ///   组框/框选/闲站告警/远行队合并/世界图绘制与选中（全部遍历 WorldObjects.Caravans）天然不可见。
    ///   代价：Spawned 恒 false → SettlementVisitedNow 恒 null → 原版 TradeCommand 不产生（TraderDialog
    ///   的"无交易命令则回退自建交易项"覆盖）。
    /// ③**GetCaravan 语义还原**：holdingOwner 指影子后 <see cref="Pawn.GetCaravan"/> 对图上殖民者恒
    ///   非空，原版 ~40 处消费面（心情/娱乐/殖民者栏/Dialog_Trade…）会误判"在远行队"——Postfix 对
    ///   影子返回 null。远行队身份判定不受影响：<see cref="Caravan.IsOwner"/> 走 pawns.Contains
    ///   （holdingOwner 比对）而非 GetCaravan。
    ///
    /// 维护：统一 pawn 所在地追踪底座（<see cref="SeamlessPawnLocationTracker"/>，2026-08-29 收拢架构）
    /// 的消费者——事件触发（跨缝传送/组队/进图，即时）+ 60 ticks 轮询兜底（进/离图/死亡自动跟随），
    /// 读档后首轮自动重建——**影子不序列化**（pawn 是图上 pawn，随档
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

        /// <summary>影子 → 据点图（GetRootMap patch 的持有链解析用；建影子/同步时写入、拆除时清除）。
        /// 2026-08 教训：影子成员 holdingOwner 指影子后，其 inventory 物品的 MapHeld 走
        /// ThingOwnerUtility.GetRootMap 沿持有链（inventory→pawn→Caravan→世界）解析，链上无 Map
        /// → "Got temperature for null map" 每 TickRare 刷屏（CompRottable.AmbientTemperature）。</summary>
        private static readonly Dictionary<Caravan, Map> shadowMaps = new();

        /// <summary>成员注入前的原容器（地图 spawnedThings），拆除/同步移除时归还。按 pawn 键控（名单跨刷新增减，顺序不稳定）。</summary>
        private static readonly Dictionary<Pawn, ThingOwner> originalOwners = new();

        private static readonly List<Pawn> tmpDesired = new();
        private static int nextRefreshTick;

        /// <summary>
        /// ThingOwner 内部列表反射（v1 实测教训）：innerList 声明在泛型类 ThingOwner&lt;T&gt; 上
        /// （非基类 ThingOwner），反射目标必须带泛型参数，取基类恒 null。
        /// spawnedThings 变体：Map.spawnedThings 声明类型是基类 ThingOwner 但实例是
        /// ThingOwner&lt;Thing&gt;（Map 构造），清扫用（见 <see cref="ScrubMap"/>）。
        /// </summary>
        private static readonly FieldInfo innerListField = AccessTools.Field(typeof(ThingOwner<Pawn>), "innerList");
        private static readonly FieldInfo spawnedInnerListField = AccessTools.Field(typeof(ThingOwner<Thing>), "innerList");

        private static List<Pawn> PawnInnerList(ThingOwner<Pawn> owner)
        {
            return (List<Pawn>)innerListField?.GetValue(owner);
        }

        // =====================================================================================
        // 注入副作用清扫（2026-08 持有链审计根治件，两名玩家报告"pawn 同时在两个地点"的定案根因）
        // =====================================================================================
        // 成因：成员注入把 holdingOwner 改指影子容器后，该 pawn 在据点图上任何 DeSpawn（跨缝走出 /
        // 原版 SplitOff 抱起 / 死亡）中 map.spawnedThings.Remove 都因 ThingOwner.Contains 的指针判定
        // （Contains = item.holdingOwner == this）失配而**静默失败**，innerList 残留一条正常 API 删不掉的
        // 陈旧条目（零 Error 日志、读档自愈故难复现）。三个 innerList 直读消费者：
        // ① 休眠 Wake 按 innerList 重注册 tick——TickList.RegisterThing 无去重，陈旧条目把活在别图的
        //    pawn 再注册一次（需求/衰老 2 倍速；Sleep 的 RemoveAllFromMap 按 Map 过滤摘不掉，即永久）；
        // ② 删图时 MapDeiniter 对后续图逐条 DecrementMapIndex 补偿 Find.Maps 索引位移——陈旧条目给
        //    活人多减一次 → pawn.Map 指向错图（"被计数在另一个 tile"：点殖民者栏头像相机跳错图、下令
        //    失灵；玩家自救"重组远行队走到该 tile 进图"= 完整登记重写，与之精确吻合）；
        // ③ 被删图自身的 NotifyEverythingWhichUsesMapReference 持有链遍历命中陈旧条目 → 活人被
        //    Notify_MyMapRemoved 置 Discarded(-3) + holdingOwner 置 null（鬼影/雕像）。
        //
        // 修复机制（与地图休眠共享 SeamlessPawnLocationTracker 底座，2026-08-29 用户定夺收拢）：
        // 触发 = 事件（跨缝传送/组队/进图 → 名单即时同步）+ 60t 轮询（本轮清扫兜底残余源）；
        // 删图自洽 = 底座 NotifyMapRemoving（一切删图路径经 Forget）→ OnMapRemoving 删前清扫，
        // 使 DecrementMapIndex 循环与持有链遍历永跑干净列表——不 patch MapDeiniter 消费者。
        //
        // 判据：t == null，或（t.Map != map 且 t.holdingOwner != map.spawnedThings）。活成员 t.Map == map
        // 天然保留——**不可把活成员移出 innerList**：原版删图的 DecrementMapIndex 索引补偿遍历的就是
        // 各图 spawnedThings.innerList，移走 = 漏补偿 = 反向制造 mapIndex 损坏（"注入时对称移出/释放时
        // 加回"方案因此被否决，勿复犯）。holdingOwner 仍指本图容器的病态条目只跳过（移除留悬垂指针）。
        // 清除时打 Message——兼野外诊断器：报告玩家看到该行即坐实本机制在其存档活动过。
        // =====================================================================================

        private static bool scrubReflectionFailed;
        private static readonly List<string> TmpScrubLabels = new List<string>();

        /// <summary>
        /// 删图前清扫（底座 NotifyMapRemoving 转发；一切删图路径经 Forget 在 DeinitAndRemoveMap 之前到达）。
        /// 刻意清扫全部图而非仅 <paramref name="map"/>：删图的 DecrementMapIndex 索引补偿遍历的是**其它图**
        /// 的 spawnedThings——陈旧条目危害恰恰在被删图之外（载荷保留供未来按图差异化的消费者）。
        /// </summary>
        internal static void OnMapRemoving(Map map)
        {
            ScrubAllMaps();
        }

        /// <summary>遍历全部地图清扫（60t 轮询 + 删图前各一次；成本 = 一趟全图 innerList 指针比较，可忽略）。</summary>
        private static void ScrubAllMaps()
        {
            var maps = Find.Maps;
            if (maps == null) return;
            for (var i = 0; i < maps.Count; i++)
            {
                ScrubMap(maps[i]);
            }
        }

        /// <summary>清除 map.spawnedThings.innerList 中不属于本图的陈旧条目（判据见上方架构注释）。</summary>
        private static void ScrubMap(Map map)
        {
            if (map == null || map.Disposed) return;
            if (!(spawnedInnerListField?.GetValue(map.spawnedThings) is List<Thing> list))
            {
                if (!scrubReflectionFailed)
                {
                    scrubReflectionFailed = true;
                    Log.Error("[RimExodus] ShadowCaravan scrub: ThingOwner<Thing>.innerList reflection failed; stale-entry cleanup disabled.");
                }
                return;
            }

            var removed = 0;
            TmpScrubLabels.Clear();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t == null)
                {
                    list.RemoveAt(i);
                    removed++;
                    continue;
                }
                if (t.Map == map) continue; // 活在本图（含影子成员）——DecrementMapIndex 索引补偿依赖其在列。
                if (t.holdingOwner == map.spawnedThings) continue; // 病态条目（指针仍指本图）：跳过，勿留悬垂指针。
                list.RemoveAt(i);
                removed++;
                if (TmpScrubLabels.Count < 5) TmpScrubLabels.Add(t.LabelShort ?? t.def?.defName ?? t.GetType().Name);
            }

            if (removed > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < TmpScrubLabels.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(TmpScrubLabels[i]);
                }
                if (removed > TmpScrubLabels.Count) sb.Append(" ...");
                Log.Message($"[RimExodus] Scrubbed {removed} stale spawnedThings entr{(removed == 1 ? "y" : "ies")} "
                    + $"on map {map.uniqueID} (wt={SeamlessTileRegistry.GetMapWorldTile(map)}): {sb}");
            }
            TmpScrubLabels.Clear();
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

        /// <summary>影子的据点图（GetRootMap patch 用）：图仍在且未 Disposed 才有效。</summary>
        public static bool TryGetShadowMap(Caravan caravan, out Map map)
        {
            map = null;
            return caravan != null && shadowMaps.TryGetValue(caravan, out map) && map != null && !map.Disposed;
        }

        /// <summary>
        /// 维护（统一 pawn 所在地追踪底座的消费者入口，2026-08-29 收拢架构）：governor GameComponentTick
        /// 位置刷新段调用——事件触发（SeamlessPawnLocationTracker.NotifyChanged，下一 tick 即时）或
        /// 60 ticks 轮询兜底二者其一即跑。读档后首轮自动重建（影子不序列化，首轮扫描自然补齐）。
        /// 事件触发只做名单同步（便宜）；轮询到期额外清扫注入副作用（见 <see cref="ScrubAllMaps"/>）。
        /// </summary>
        internal static void Maintain(bool eventTriggered)
        {
            if (Find.TickManager == null || Find.Maps == null) return;
            var pollDue = Find.TickManager.TicksGame >= nextRefreshTick;
            if (!eventTriggered && !pollDue) return;
            if (pollDue) nextRefreshTick = Find.TickManager.TicksGame + RefreshIntervalTicks;
            try
            {
                Refresh(new List<Map>(Find.Maps));
                // 注入副作用的周期兜底（2026-08 持有链审计）：残余源（原版 SplitOff 抱起 / 死亡 Destroy
                // 内的 DeSpawn）不 patch 无法拦截，由轮询清扫把 spawnedThings 陈旧条目存活期压到 ≤60 ticks。
                // 事件触发的刷新刻意不清扫——删图自洽已由底座 NotifyMapRemoving 全覆盖（一切删图路径经
                // Forget），事件路径零清扫成本。
                if (pollDue) ScrubAllMaps();
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
                    // 刻意不 Find.WorldObjects.Add（2026-08-26 用户定夺方案 B）：不进世界对象表 =
                    // Spawned 恒 false——殖民者栏组框（CheckRecacheEntries 遍历 WorldObjects.Caravans）、
                    // 框选（经殖民者栏条目）、闲站告警、远行队合并、世界图绘制/选中**全部自然不可见**，
                    // 无需逐消费面打地鼠。交互链不依赖 Spawned：成员注入/IsOwner/FindBestDiplomat 走
                    // holdingOwner；面板选项执行 = pather.StartPath(同 tile)→AtDestinationPosition→
                    // PatherArrived 全链无 Spawned 检查。代价：SettlementVisitedNow 恒 null → 原版
                    // TradeCommand 不产生（由 TraderDialog 的"无交易命令则回退自建交易项"覆盖）。
                    shadows[kv.Key] = shadow;
                    if (RimExodusLog.Enabled(RimExodusLogModule.Settlement)) Log.Message($"[RimExodus] ShadowCaravan: created at tile {kv.Key} for '{kv.Value.Parent.Label}' (unspawned).");
                }
                shadowMaps[shadow] = kv.Value; // 期望集本轮确认的据点图（防旧图删除后残留）
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
                    // 归还持有（2026-08 奴役实测教训）：快照不可信（加入与归还之间容器可能已变——
                    // 奴役/受伤倒地等路径会重挂容器），pawn 仍 spawned 时唯一正确容器就是所在图；
                    // 未 spawn（死亡抬尸/被装容器）才还快照，快照缺失置 null 由其新持有者接管。
                    p.holdingOwner = p.Spawned ? p.Map.spawnedThings : (TakeOriginalOwner(p) ?? null);
                }
                if (RimExodusLog.Enabled(RimExodusLogModule.Settlement))
                    Log.Message($"[RimExodus] ShadowCaravan: removed {p?.LabelShort ?? "null"} from shadow (tile={shadow.Tile}).");
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
                if (RimExodusLog.Enabled(RimExodusLogModule.Settlement))
                    Log.Message($"[RimExodus] ShadowCaravan: injected {p.LabelShort} into shadow (tile={shadow.Tile}).");
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
                        p.holdingOwner = p.Spawned ? p.Map.spawnedThings : (TakeOriginalOwner(p) ?? null);
                    }
                    innerList.Clear();
                }
                if (RimExodusLog.Enabled(RimExodusLogModule.Settlement)) Log.Message($"[RimExodus] ShadowCaravan: dismantled at tile {tile} ({reason}).");
            }
            catch (System.Exception e)
            {
                Log.Warning($"[RimExodus] Shadow caravan dismantle failed at tile {tile}: {e.Message}");
            }
            finally
            {
                shadows.Remove(tile);
                if (shadow != null) shadowMaps.Remove(shadow);
            }
        }

        /// <summary>全量拆除（存档前 SaveGame Prefix 调）：影子绝不随档序列化——其成员是图上 pawn，
        /// Caravan.ExposeData 的 Scribe_Deep(pawns) 会把它们双存成远行队成员。读档后由位置刷新段首轮重建。</summary>
        public static void EnsureReleasedForSave()
        {
            var tiles = new List<int>(shadows.Keys);
            foreach (var tile in tiles) DismantleAt(tile, "save");
        }

        /// <summary>把 pawn 从其所属影子中释放（若有）：移出影子名单并归还 holdingOwner。
        /// 真远行队 <see cref="Caravan.AddPawn"/> 前必须调用（2026-08 组队冲突修复）——
        /// 影子成员同步是 60 ticks 一轮，赶不上组队瞬间；holdingOwner 指影子时 AddPawn 的
        /// DeSpawnOrDeselect 只清地图容器、TryAdd 因 "already in another container" 拒绝。
        /// 释放后 pawn 仍 Spawned → 归还所在图容器，AddPawn 走完整原版路径。</summary>
        public static bool ReleaseIfShadowMember(Pawn p)
        {
            if (p == null || p.holdingOwner == null) return false;
            foreach (var kv in shadows)
            {
                var shadow = kv.Value;
                if (shadow == null || shadow.pawns != p.holdingOwner) continue;
                PawnInnerList(shadow.pawns)?.Remove(p);
                p.holdingOwner = p.Spawned ? p.Map.spawnedThings : (TakeOriginalOwner(p) ?? null);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 释放悬指【非现役】影子容器的持有（2026-09-02 "交易后再登穿梭机复现"案的止血+探测器）。
        /// <see cref="ReleaseIfShadowMember"/> 匹配失败但 holdingOwner 仍指一个 Caravan 容器 =
        /// 旧影子实例泄漏（该 Caravan 已不在 shadows 注册表）——此时 DeSpawn 的
        /// map.spawnedThings.Remove 静默失败、后续容器 TryAdd 被拒，登机照旧"消失"。本方法把
        /// holdingOwner 归还本图容器使 DeSpawn/容器转移恢复原版，并打 Warning 坐实泄漏
        /// （报告玩家看到该行即证明存在未知的旧实例泄漏源，配合 SyncMembership 的注入/移除日志定位）。
        /// 真远行队（Find.WorldObjects.Caravans 在册）不碰——组队链的 DeSpawn 发生在
        /// holdingOwner = 地图容器时，DeSpawn 时持有【真】Caravan 容器的 pawn 只可能是
        /// 不 Spawned 的原版错误路径（原版自会报 "Tried to despawn ... not spawned"）。
        /// </summary>
        internal static bool ReleaseIfOrphanShadowHolder(Pawn p)
        {
            if (p == null || p.holdingOwner == null) return false;
            if (p.holdingOwner.Owner is not Caravan orphan) return false;
            if (Find.WorldObjects == null || Find.WorldObjects.Caravans.Contains(orphan)) return false;
            PawnInnerList(orphan.pawns)?.Remove(p);
            Log.Warning($"[RimExodus] ShadowCaravan: {p.LabelShort} was held by an ORPHANED caravan container " +
                        $"(tile={orphan.Tile}, id={orphan.ID}, not in world list, not an active shadow) — released on DeSpawn. " +
                        $"This indicates a stale shadow instance leak; please report this log line.");
            p.holdingOwner = p.Spawned ? p.Map.spawnedThings : null;
            return true;
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
    /// 影子远行队的行为面 patch（2026-08-26 方案 B 收敛：影子不进 Find.WorldObjects——不 tick、
    /// 不序列化、殖民者栏/框选/告警/合并/世界图绘制与选中全部天然不可见，原 TickInterval 抑制与
    /// 三个世界图隐藏 patch 随之删除）。五件：
    /// ①<see cref="Pawn.GetCaravan"/> 对影子返回 null——还原原版 ~40 处消费面的地图语义
    ///   （holdingOwner 指影子不应改变地图行为；Caravan.IsOwner 走 pawns.Contains 不受影响）；
    /// ②<see cref="Caravan.GetGizmos"/> 清空——防御性兜底（影子不可选中后本不应被调到）。
    /// ③<see cref="ThingOwnerUtility.GetRootMap"/> Prefix——影子成员 inventory 物品的 MapHeld 还原：
    /// holdingOwner 指影子后持有链（inventory→pawn→Caravan→世界）不含 Map → null，CompRottable 的
    /// AmbientTemperature 每 TickRare 刷 "Got temperature for null map"（2026-08 奴役场景实测暴露，
    /// 实为一切影子成员带腐烂食物即触发）；遇影子时返回其据点图，掉落/腐烂/心情等全消费面一并还原。
    /// ④<see cref="Caravan.AddPawn"/> Prefix 释放影子成员（2026-08 组队冲突）——见 patch 处注释。
    /// ⑤<see cref="Pawn.DeSpawn"/> Prefix 释放影子成员（2026-09 穿梭机登机消失案）——一切
    ///   "离图进容器"路径的统一闸门，④与之幂等互备；见 patch 处注释。
    /// </summary>
    internal static class Patches_ShadowCaravan
    {
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

        // 真组队释放（2026-08 组队冲突修复）：玩家在据点图上组建真远行队（ExitMapAndCreateCaravan→
        // MakeCaravan→AddPawn）时，成员 holdingOwner 还指着影子容器（60 ticks 同步赶不上组队瞬间）→
        // TryAdd "already in another container" 拒绝、pawn 进不了新远行队。Prefix 先释放：
        // pawn 仍 Spawned 则归还地图容器，AddPawn 原生的 DeSpawnOrDeselect+TryAdd 走完整原版路径。
        // 用 __0 位置参数绑定（vanilla 签名 AddPawn(Pawn, bool)，参数名不在公开源内勿按名绑）。
        [HarmonyPatch(typeof(Caravan), nameof(Caravan.AddPawn))]
        static class Patch_ShadowCaravan_ReleaseForRealCaravan
        {
            static void Prefix(Pawn __0)
            {
                SeamlessShadowCaravan.ReleaseIfShadowMember(__0);
            }
        }

        // DeSpawn 统一释放闸（2026-09 穿梭机登机消失案，AddPawn patch 的同族推广）：原版不变量 =
        // pawn DeSpawn 前 holdingOwner 必须是本图 spawnedThings 或 null（原版 Thing.SplitOff /
        // Pawn.Kill 的"DeSpawn 后 holdingOwner?.Remove(this)"先例）；影子注入期 holdingOwner 指影子
        // 容器 → DeSpawn 的 map.spawnedThings.Remove 因 Contains 指针判定静默失败、holdingOwner
        // 悬指影子 → 后续任何容器 TryAdd 全被 "already in another container" 拒绝：
        // JobDriver_EnterTransporter（穿梭机登机，TryAdd 返回值被原版忽略 = pawn 离图又进不了舱，
        // 实测"登机即消失"，且一旦存读档即永久丢失）、JobDriver_EnterCryptosleepCasket（TryAcceptThing
        // 同款）、JobDriver_EnterPortal（DeSpawn→Spawn 新图，Spawn 侧 TryAdd 同要求持有者为空）。
        // Prefix 时 pawn 仍 Spawned → 释放归还本图容器，DeSpawn 本体 Remove 正常成功并清空
        // holdingOwner，后续一切容器转移恢复原版；跨缝走出/SplitOff 抱起/死亡路径的注入期 DeSpawn
        // 陈旧条目由此事前不发生（第六件三下游），60t 清扫降级纯兜底。与 AddPawn patch 幂等互备
        // （组队链 AddPawn 内部的 DeSpawnOrDeselect 亦经此闸）。纯容器簿记，thing tick 中途安全。
        // 2026-09-02 复现案加固：ReleaseIfShadowMember 按现役影子匹配失败时，再试孤儿容器释放
        //（"交易后再登穿梭机复现"案最终定案 = 测试误用 release 旧二进制、非真泄漏——兜底保留为
        // 防御：若未来真出现旧影子实例泄漏，登机/传送仍被救回并打 Warning 坐实）。
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
        static class Patch_ShadowCaravan_ReleaseOnDespawn
        {
            static void Prefix(Pawn __instance)
            {
                if (SeamlessShadowCaravan.ReleaseIfShadowMember(__instance))
                {
                    if (RimExodusLog.Enabled(RimExodusLogModule.Settlement))
                        Log.Message($"[RimExodus] ShadowCaravan: released {__instance.LabelShort} from active shadow on DeSpawn.");
                    return;
                }
                SeamlessShadowCaravan.ReleaseIfOrphanShadowHolder(__instance);
            }
        }

        // 持有链 Map 解析收口（静态方法，链遍历成本与原方法同级）：沿链遇影子 Caravan → 返回其据点图。
        // 影子不在链上（绝大多数调用）首步即放行原方法，零额外语义面。
        [HarmonyPatch(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.GetRootMap))]
        static class Patch_ShadowCaravan_RootMap
        {
            static bool Prefix(IThingHolder holder, ref Map __result)
            {
                for (var h = holder; h != null; h = h.ParentHolder)
                {
                    if (h is Caravan c && SeamlessShadowCaravan.TryGetShadowMap(c, out var map))
                    {
                        __result = map;
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
