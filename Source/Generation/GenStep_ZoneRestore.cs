using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 前哨保留·区域置换 genStep（order=1100，2026-09 回归定夺："生成建筑的 mutator/landmark
    /// genStep 之后、pawn 与动物生成之前"）。本 tile 无封存记录时零介入（一次转型 + 判空）。
    ///
    /// 【为什么在 1100（游戏 Data XML 已核实的窗口）】建筑生成源 = Settlement 400 /
    /// SettlementPower 401 / MutatorCriticalStructures（landmark，如 Odyssey 废弃殖民地）500 /
    /// ScatterGroupPrefabs 950 / ScatterGroup（junk 系）960——全部在身后，它们的产物（含绕过
    /// 选址根原语、由 BaseGen 从锚点铺开的 landmark 建筑群）一律落入 Phase A 清障范围；
    /// Animals 1200 在前——置换时**无 pawn 在场**，无需 pawn 推移。残差（观察项）：AncientTuneller
    /// 1650（刻意排 MutatorFinal 1600 后的 quest 建筑源）与 MutatorFinal 的地形覆写在我们之后，
    /// 若实测污染前哨区域再按特例处理；任意排 1100 之后的 mod 建筑步骤同类。
    ///
    /// 【架构史（勿回退）】首版 order 395 + 源头禁布三 patch + 395 后 +120t 延迟冲突清理 =
    /// "抢在生成源前面拦 + 事后补漏"的 patch 思路（实测漏：landmark 锚点在区域外、34-38 宽
    /// rect 压进区域；且 MutatorCriticalStructures 本就在 500 > 395）。order 后置到建筑源之后
    /// 让权威置换天然覆盖一切 gen 期生成源，禁布层与延迟清理层整体拆除。
    ///
    /// 【清障判据 = def.destroyable，勿改】区域内一切可拆除物（自然岩/远古与破损遗迹/
    /// mod 结构——它们挡在恢复路径上，"可拆"即游戏语义里的非关键物）一律 Destroy(Vanish)；
    /// Destroy 后复查存活（mod 用 Harmony patch 拒拆表达"别动我"）→ 升格为保留。不可拆除物
    /// （monolith/不可拆墙/传送点——RimExodus_SeamlessEnterSpot destroyable=false 天然在此侧）
    /// 保留且恢复条目让位（用户定夺 2026-09：玩家可接受营地被新内容破坏，不可接受本 mod 吞掉
    /// 其他 mod 的关键建筑）。
    ///
    /// 【地形只动地板层】记录只存地板/基架（土壤不录）；清除重生成地板时回落**本次生成自己的
    /// 389 baseSnapshot 原生地形**——"自然层相信重生成"的自洽实现。
    ///
    /// 【物品推移不销毁】items 可能是任务物/关键物；植物可清（原版建筑落成 Wipe 同语义）。
    /// ScatterGroup(950/960) 的 junk/loot 与 landmark 尸体已在场（Corpse 属 Item 类，推移覆盖）。
    ///
    /// 【收尾】居住区恢复（zone = 原 home area 字面快照，重放不恢复 = 下次离开不达封存判定的
    /// 二次丢失 bug）+ 直写铁律 RecalculateAllPerceivedPathCosts。Fog(1500) 在后：恢复的墙参与
    /// 接缝洪水（密封房间留雾 = 原版一致），原已探索态由 Fog patch 的区域揭雾补齐。
    /// </summary>
    public class GenStep_ZoneRestore : GenStep
    {
        public override int SeedPart => 630219481;

        public override void Generate(Map map, GenStepParams parms)
        {
            var parent = map.Parent as MapParent_SeamlessTile;
            var record = parent?.preserveRecord;
            if (record == null) return;
            if (SeamlessMapPreviewCompat.IsGeneratingPreviewOnCurrentThread) return;

            if (record.mapSize != map.Size.x)
            {
                Log.Warning($"[RimExodus] ZoneRestore: record mapSize {record.mapSize} != current {map.Size.x} " +
                            $"for tile {record.worldTile}, discarding record (mapSize 设置变更，坐标失义).");
                parent.preserveRecord = null;
                return;
            }

            // ===== Phase A 清障 =====
            var keptCells = new HashSet<IntVec3>();
            int cleared = 0, keptForeign = 0;

            var zoneBuildings = new List<Thing>();
            var seen = new HashSet<Thing>();
            foreach (var c in record.zoneCells)
            {
                var list = map.thingGrid.ThingsListAt(c);
                if (list == null) continue;
                foreach (var t in list)
                {
                    if (t == null || t.def.category != ThingCategory.Building) continue;
                    if (seen.Add(t)) zoneBuildings.Add(t);
                }
            }
            foreach (var t in zoneBuildings)
            {
                if (t.def.destroyable)
                {
                    t.Destroy(DestroyMode.Vanish);
                    if (t.Destroyed) { cleared++; continue; }
                    // Destroy 被 mod 拦下（事实上的不可拆）→ 升格保留（用户定夺的免疫清理场景）。
                }
                foreach (var fc in t.OccupiedRect()) keptCells.Add(fc);
                keptForeign++;
            }

            // 屋顶全清待 Phase B 重放（保留占用集上的格不动——防"拆了 monolith 脚下的仪式屋顶"类
            // 语义破坏）。窑洞语义：记录里 roof=null 的洞腔格在此清掉重生成的厚岩顶。
            foreach (var c in record.zoneCells)
            {
                if (keptCells.Contains(c)) continue;
                if (map.roofGrid.RoofAt(c) != null) map.roofGrid.SetRoof(c, null);
            }

            // 地板层：记录值直接写；记录无地板但重生成铺了地板（遗迹/landmark/mod 地板）→ 回落
            // 本次生成自己的 389 原生地形。保留占用集上的格不动。
            var floorByCell = new Dictionary<IntVec3, TerrainDef>();
            foreach (var f in record.floors) floorByCell[f.cell] = f.def;
            var baseTerrain = SeamlessMapData.GetBaseTerrainSnapshot(map);
            foreach (var c in record.zoneCells)
            {
                if (keptCells.Contains(c)) continue;
                var cur = map.terrainGrid.TopTerrainAt(c);
                if (floorByCell.TryGetValue(c, out var want))
                {
                    if (cur != want) map.terrainGrid.SetTerrain(c, want);
                }
                else if (cur != null && (cur.IsFloor || cur.IsSubstructure))
                {
                    var natural = baseTerrain != null ? baseTerrain[map.cellIndices.CellToIndex(c)] : null;
                    if (natural != null) map.terrainGrid.SetTerrain(c, natural);
                }
            }

            // ===== Phase B 恢复 =====
            int restored = 0, skipped = 0;

            // 建筑（先于屋顶/物品：物品推移/植物清除围绕建筑 footprint 进行）。
            foreach (var b in record.buildings)
            {
                if (b.def == null) { skipped++; continue; } // def 失落（mod 卸载）——原版存档缺落同语义
                var rot = new Rot4(b.rotInt);
                var rect = GenAdj.OccupiedRect(b.cell, rot, b.def.size);

                bool conflict = false;
                foreach (var fc in rect)
                {
                    if (keptCells.Contains(fc)) { conflict = true; break; }
                }
                if (conflict) { skipped++; continue; }

                foreach (var fc in rect)
                {
                    // footprint 上区域外的岩体（区域内 Phase A 已清）：可拆清、拒拆让位整条。
                    if (!record.ZoneContains(fc))
                    {
                        var ed = fc.GetEdifice(map);
                        if (ed != null && ed.def.destroyable)
                        {
                            ed.Destroy(DestroyMode.Vanish);
                            if (!ed.Destroyed) { conflict = true; break; }
                        }
                    }
                    if (conflict) break;

                    var list = map.thingGrid.ThingsListAt(fc);
                    if (list == null || list.Count == 0) continue;
                    Thing[] snapshot = list.ToArray();
                    foreach (var t in snapshot)
                    {
                        if (t == null || t.Destroyed) continue;
                        if (t.def.category == ThingCategory.Plant)
                        {
                            t.Destroy(DestroyMode.Vanish); // 植物可再生，与原版落成 Wipe 同语义
                        }
                        else if (t.def.category == ThingCategory.Item)
                        {
                            ShoveAside(map, t); // 物品推移不销毁（可能含任务物/关键物/尸体）
                        }
                        // Animals(1200) 在本 genStep 之后——置换时无 pawn 在场，无需 pawn 推移。
                    }
                }
                if (conflict) { skipped++; continue; }

                var thing = ThingMaker.MakeThing(b.def, b.stuff);
                if (thing == null) { skipped++; continue; }
                if (b.hp > 0 && thing.def.useHitPoints)
                {
                    thing.HitPoints = Mathf.Clamp(b.hp, 1, thing.MaxHitPoints);
                }
                if (b.qualityInt >= 0)
                {
                    thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)b.qualityInt, null);
                }
                var faction = ZoneMapRecord.ResolveFaction(b.factionLoadID);
                if (faction != null) thing.SetFaction(faction);
                GenSpawn.Spawn(thing, b.cell, map, rot, WipeMode.Vanish);
                restored++;
            }

            // 屋顶（保留占用集让位）。
            foreach (var r in record.roofs)
            {
                if (r.def == null || keptCells.Contains(r.cell)) continue;
                if (map.roofGrid.RoofAt(r.cell) != r.def) map.roofGrid.SetRoof(r.cell, r.def);
            }

            // 物品（落格冲突就近推移——含被让位格上的记录物品）。
            int itemsRestored = 0;
            foreach (var it in record.items)
            {
                if (it.def == null) continue;
                var thing = ThingMaker.MakeThing(it.def, it.stuff);
                if (thing == null) continue;
                if (thing.def.stackLimit > 1 && it.count > 1)
                {
                    thing.stackCount = Mathf.Min(it.count, thing.def.stackLimit);
                }
                if (it.hp > 0 && thing.def.useHitPoints)
                {
                    thing.HitPoints = Mathf.Clamp(it.hp, 1, thing.MaxHitPoints);
                }
                if (it.qualityInt >= 0)
                {
                    thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)it.qualityInt, null);
                }
                var cell = it.cell;
                if (!cell.Standable(map)) cell = FindFreeCellNear(map, it.cell);
                if (cell.IsValid) GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
                else thing.Destroy(DestroyMode.Vanish); // 理论不可达（全图找不到可站格）
                itemsRestored++;
            }

            // 居住区恢复（zone = 原 home area 字面快照）：不恢复 = 下次离开时 Evaluate=None 被直接
            // 删除（二次丢失 bug 的修复点）。索引器 setter 自动连带渲染/寻路/region 通知（Area.Set
            // → MarkDirty）；两路径 genSteps 前都已 AddStartingAreas（MapGenerator.cs:183 /
            // IncrementalMapGenerator.cs:263），此处 Home 必在。
            var home = map.areaManager?.Home;
            if (home != null)
            {
                foreach (var c in record.zoneCells)
                {
                    home[c] = true;
                }
            }

            // 直写铁律：批量 SetTerrain/spawn 后重算路径缓存（Walkable/Standable 读缓存数组）。
            map.pathing.RecalculateAllPerceivedPathCosts();

            // 让位计数玩家反馈（不可拆结构占据 → 记录条目跳过；原 +120t 延迟清理趟的 Message
            // 随其拆除移入本处——玩家可见"哪些前哨没能完整恢复"）。
            if (skipped > 0)
            {
                Messages.Message("RimExodus_PreserveConflictMsg".Translate(skipped),
                    MessageTypeDefOf.NeutralEvent, false);
            }

            if (RimExodusLog.Enabled(RimExodusLogModule.Generation))
            {
                Log.Message($"[RimExodus:Generation] ZoneRestore: tile={record.worldTile} zone={record.zoneCells.Count} cells " +
                            $"cleared={cleared} keptForeign={keptForeign} buildingsRestored={restored}/{record.buildings.Count} " +
                            $"skipped={skipped} roofs={record.roofs.Count} floors={record.floors.Count} items={itemsRestored} " +
                            $"homeAreaRestored={(home != null)}.");
            }
        }

        /// <summary>物品推移：DeSpawn 后就近重 Spawn（区域内外皆可——不销毁是硬约束）。</summary>
        private static void ShoveAside(Map map, Thing t)
        {
            var free = FindFreeCellNear(map, t.Position);
            t.DeSpawn();
            if (free.IsValid) GenSpawn.Spawn(t, free, map, WipeMode.Vanish);
            else t.Destroy(DestroyMode.Vanish); // 理论不可达兜底
        }

        /// <summary>径向螺旋找最近可站格（上限 14 格——1100 时点地图边缘全 void，找不到返回 Invalid）。</summary>
        private static IntVec3 FindFreeCellNear(Map map, IntVec3 center)
        {
            if (center.Standable(map)) return center;
            for (int r = 1; r <= 14; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue; // 只扫当前环
                        var c = new IntVec3(center.x + dx, 0, center.z + dz);
                        if (c.InBounds(map) && c.Standable(map)) return c;
                    }
                }
            }
            return IntVec3.Invalid;
        }
    }
}
