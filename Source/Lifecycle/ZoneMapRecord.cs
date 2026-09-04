using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 封存建筑记录（居住区内一切 edifice：玩家建筑、自然岩壁、遗迹墙、mod 结构）。
    /// comp 级状态（床归属/温控设定等）明确不保真（用户定夺 2026-09——目标是"离开期演化"
    /// 而非"冻结复刻"，语义要点比 comp 保真重要；将来需要可对单建筑做 mini Scribe 增量）。
    /// </summary>
    public class ZoneBuildingRecord : IExposable
    {
        public IntVec3 cell;
        public ThingDef def;
        public ThingDef stuff;
        public int hp = 1;
        public int rotInt;
        public string factionLoadID;
        public int qualityInt = -1; // QualityCategory，-1 = 无 CompQuality

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref hp, "hp", 1);
            Scribe_Values.Look(ref rotInt, "rot", 0);
            Scribe_Values.Look(ref factionLoadID, "faction");
            Scribe_Values.Look(ref qualityInt, "quality", -1);
        }
    }

    /// <summary>封存屋顶记录（稀疏：仅非空格）。</summary>
    public class ZoneRoofRecord : IExposable
    {
        public IntVec3 cell;
        public RoofDef def;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
        }
    }

    /// <summary>
    /// 封存地板记录（稀疏：**仅地板与逆重基架，不录其下土壤**——用户定夺 2026-09：
    /// 自然层（土壤/岩体/河流）永远相信重生成，河流改道冲毁前哨 = 世界演化，可接受）。
    /// </summary>
    public class ZoneFloorRecord : IExposable
    {
        public IntVec3 cell;
        public TerrainDef def;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
        }
    }

    /// <summary>封存物品记录（地面物品——玩家物资是前哨价值的一半，不录 = "离开即被搬空"）。</summary>
    public class ZoneItemRecord : IExposable
    {
        public IntVec3 cell;
        public ThingDef def;
        public ThingDef stuff;
        public int count = 1;
        public int hp = -1; // -1 = 该 def 不用 hp
        public int qualityInt = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref count, "count", 1);
            Scribe_Values.Look(ref hp, "hp", -1);
            Scribe_Values.Look(ref qualityInt, "quality", -1);
        }
    }

    /// <summary>
    /// 前哨封存记录（2026-09 前哨保留，挂 <see cref="MapParent_SeamlessTile.preserveRecord"/>）。
    ///
    /// 【模型】区域（居住区字面快照）内**权威置换**、区域外重生成：
    /// - 区域 = 封存时的 Home area 格列表（字面，零膨胀——判据见
    ///   <see cref="SeamlessMapModificationTracker"/>）；
    /// - 全部层稀疏存储（buildings/roofs/floors/items 只录在档条目，缺省 = 无该层内容），
    ///   无区域大小上限（用户定夺 2026-09：玩家可能建超大基地再周游世界，无理由替玩家做价值判断）；
    /// - 重放 = <see cref="GenStep_ZoneRestore"/>（order 395）：区域内清一切可拆除物
    ///   （def.destroyable 判据，Destroy 后复查存活 = mod patch 拒拆的事实不可拆），
    ///   再恢复记录条目（footprint 与保留占用集相交则整条跳过）。
    ///
    /// 【不录的东西】pawn（封存走 DeinitAndRemoveMap，pawn 善后与删除一致 = 进世界池，
    /// 记录零 pawn → 无重复实体问题）；土壤（见 ZoneFloorRecord）；comp 级状态。
    ///
    /// 【运行时缓存】zoneLookup 非序列化、随 zoneCells 重建（"只存旗标不存数据"纪律的镜像面：
    /// 缓存永不比数据旧——重建入口在 ZoneContains 的判空，无事件驱动刷新点）。
    /// </summary>
    public class ZoneMapRecord : IExposable
    {
        public int formatVersion = 1;
        public int worldTile = -1;
        public int mapSize = -1;
        public int capturedAtTick;
        public int archiveOrdinal;
        public int homeCellsAtCapture;

        public List<IntVec3> zoneCells = new List<IntVec3>();
        public List<ZoneBuildingRecord> buildings = new List<ZoneBuildingRecord>();
        public List<ZoneRoofRecord> roofs = new List<ZoneRoofRecord>();
        public List<ZoneFloorRecord> floors = new List<ZoneFloorRecord>();
        public List<ZoneItemRecord> items = new List<ZoneItemRecord>();

        private HashSet<IntVec3> _zoneLookup;

        /// <summary>区域成员查询（O(1)；缓存随列表惰性重建，读档/捕获后首查生效）。</summary>
        public bool ZoneContains(IntVec3 c)
        {
            if (_zoneLookup == null) _zoneLookup = new HashSet<IntVec3>(zoneCells);
            return _zoneLookup.Contains(c);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref formatVersion, "formatVersion", 1);
            Scribe_Values.Look(ref worldTile, "worldTile", -1);
            Scribe_Values.Look(ref mapSize, "mapSize", -1);
            Scribe_Values.Look(ref capturedAtTick, "capturedAtTick", 0);
            Scribe_Values.Look(ref archiveOrdinal, "archiveOrdinal", 0);
            Scribe_Values.Look(ref homeCellsAtCapture, "homeCells", 0);
            Scribe_Collections.Look(ref zoneCells, "zoneCells", LookMode.Value);
            Scribe_Collections.Look(ref buildings, "buildings", LookMode.Deep);
            Scribe_Collections.Look(ref roofs, "roofs", LookMode.Deep);
            Scribe_Collections.Look(ref floors, "floors", LookMode.Deep);
            Scribe_Collections.Look(ref items, "items", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                zoneCells ??= new List<IntVec3>();
                buildings ??= new List<ZoneBuildingRecord>();
                roofs ??= new List<ZoneRoofRecord>();
                floors ??= new List<ZoneFloorRecord>();
                items ??= new List<ZoneItemRecord>();
            }
        }

        /// <summary>
        /// 封存捕获（图仍活着时调用——拆图前）。遍历居住区格，采四层稀疏数据：
        /// 建筑（thingGrid Building 类，跨格去重，记创建位/朝向/hp/stuff/派系 loadID/品质）、
        /// 屋顶（roofGrid 非空格）、地板（TopTerrainAt 为 Floor/基架 的格）、物品（Item 类含堆数）。
        /// 居住区为空返回 null（调用方回落普通删除）。
        /// </summary>
        public static ZoneMapRecord Capture(Map map, int worldTile, int archiveOrdinal, int homeCells)
        {
            var home = map.areaManager?.Home;
            if (home == null || home.TrueCount <= 0) return null;

            var rec = new ZoneMapRecord
            {
                worldTile = worldTile,
                mapSize = map.Size.x,
                capturedAtTick = Find.TickManager.TicksGame,
                archiveOrdinal = archiveOrdinal,
                homeCellsAtCapture = homeCells,
            };

            var seenThings = new HashSet<Thing>();
            foreach (var c in home.ActiveCells)
            {
                rec.zoneCells.Add(c);

                var list = map.thingGrid.ThingsListAt(c);
                if (list == null) continue;

                // 建筑层：该格全部 Building 类 thing（edifice + 罕见非 edifice 建筑），
                // 多格建筑跨格去重（Thing 对象身份），记录创建位 = t.Position。
                foreach (var t in list)
                {
                    if (t == null || t.def.category != ThingCategory.Building) continue;
                    if (!seenThings.Add(t)) continue;
                    rec.buildings.Add(new ZoneBuildingRecord
                    {
                        cell = t.Position,
                        def = t.def,
                        stuff = t.Stuff,
                        hp = t.HitPoints,
                        rotInt = t.Rotation.AsInt,
                        factionLoadID = t.Faction?.GetUniqueLoadID(),
                        qualityInt = (int?)t.TryGetComp<CompQuality>()?.Quality ?? -1,
                    });
                }

                var roof = map.roofGrid.RoofAt(c);
                if (roof != null)
                {
                    rec.roofs.Add(new ZoneRoofRecord { cell = c, def = roof });
                }

                // 地板层：只录 Floor/Substructure（IsFloor = HasTag("Floor")，IsSubstructure 为
                // 逆重基架）——土壤等自然层刻意不录（自然层相信重生成，用户定夺 2026-09）。
                var terrain = map.terrainGrid.TopTerrainAt(c);
                if (terrain != null && (terrain.IsFloor || terrain.IsSubstructure))
                {
                    rec.floors.Add(new ZoneFloorRecord { cell = c, def = terrain });
                }

                // 物品层：地面物品（含堆数/品质/hp—— apparel 武器品质走 CompQuality）。
                foreach (var t in list)
                {
                    if (t == null || t.def.category != ThingCategory.Item) continue;
                    rec.items.Add(new ZoneItemRecord
                    {
                        cell = t.Position,
                        def = t.def,
                        stuff = t.Stuff,
                        count = t.stackCount,
                        hp = t.def.useHitPoints ? t.HitPoints : -1,
                        qualityInt = (int?)t.TryGetComp<CompQuality>()?.Quality ?? -1,
                    });
                }
            }
            return rec;
        }

        /// <summary>
        /// 按 loadID 解析派系（记录存 GetUniqueLoadID 字符串，重放时线性匹配——不依赖 Scribe
        /// 引用机制，与原版"缺落 → null + 警告"同语义）。解析失败返回 null（如中立派系覆灭）。
        /// </summary>
        public static Faction ResolveFaction(string loadID)
        {
            if (loadID == null) return null;
            var factions = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < factions.Count; i++)
            {
                if (factions[i].GetUniqueLoadID() == loadID) return factions[i];
            }
            return null;
        }
    }
}
