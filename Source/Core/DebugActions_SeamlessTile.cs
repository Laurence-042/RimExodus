using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 无缝地块的调试命令（Dev 菜单）。
    /// 用于在游戏内手动生成/卸载无缝地块口袋地图，验证渲染与接缝。
    ///
    /// 阶段3：邻居方向基于世界地块真实顶点角度（动态），不再用固定 0-5 编号。
    /// 命令改为"生成当前地块的指定世界邻居地块"。
    /// </summary>
    public static class DebugActions_SeamlessTile
    {
        private const string Category = "RimExodus";

        private const int DefaultMapSize = 250;

        /// <summary>获取当前地图上的 SeamlessTileManager。</summary>
        private static SeamlessTileManager CurrentManager
        {
            get
            {
                var map = Find.CurrentMap;
                if (map == null) return null;
                return map.GetComponent<SeamlessTileManager>();
            }
        }

        /// <summary>
        /// 生成当前地块的全部邻居地块（2026-08-27 入口统一化）：走预加载队列
        /// <see cref="SeamlessTilePreloader.QueuePreload"/>——与玩家命令 pawn 触发的边界预加载
        /// 完全同一条链（队列 → ConsumeQueued → TryPreloadNeighbor → 私有 GenerateTileMap），
        /// 忙时自动重排队下一 tick，六邻图逐个串行生成完。**勿改为同步直调 GenerateTileMap**：
        /// 分帧增量生成全程持有 mapBeingGenerated，同步循环里第一个调用启动后其余全撞忙守卫被拒
        /// （曾致"只生成了一个图"，原 GenerateForNeighbor 即此病灶，已删）。
        /// </summary>
        [DebugAction(Category, "Generate All Seamless Neighbors", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GenerateAllNeighbors()
        {
            var currentMap = Find.CurrentMap;
            var currentWorldTile = SeamlessTileRegistry.GetMapWorldTile(currentMap);
            if (currentWorldTile < 0)
            {
                Log.Warning("[RimExodus] Current map has no valid world tile.");
                return;
            }

            var worldNeighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(currentWorldTile, worldNeighbors);
            var queued = 0;
            foreach (var neighborTile in worldNeighbors)
            {
                if (neighborTile.tileId == currentWorldTile) continue;
                SeamlessTilePreloader.QueuePreload(currentMap, neighborTile.tileId);
                queued++;
            }
            Log.Message($"[RimExodus] Queued generation for {queued} seamless neighbor tiles (consumed one per tick via preloader).");
        }

        [DebugAction(Category, "Remove All Tile Maps", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAll()
        {
            var manager = CurrentManager;
            if (manager == null) return;

            // 本工具的显式范围 = 仅地块图（快速清场用）；原生家族图的删除走 governor/
            // Force Delete（RemoveRollingMap 统一入口）——此处类型过滤是工具范围选择，非特权判定。
            var currentMap = Find.CurrentMap;
            var neighbors = SeamlessTileGraph.GetAllNeighbors(currentMap);
            var toRemove = new List<MapParent_SeamlessTile>();
            foreach (var info in neighbors)
            {
                if (info.map.Parent is MapParent_SeamlessTile neighborParent)
                {
                    toRemove.Add(neighborParent);
                }
            }

            foreach (var parent in toRemove)
            {
                // 单一删除入口（2026-09 收拢）：达阈前哨经入口内部分流转为封存、小于阈且提示开则
                // 入队询问——清场后要彻底放弃再手动"丢弃已封存"。
                manager.RemoveRollingMap(parent);
            }

            Log.Message($"[RimExodus] Remove-all dispatched via single entry: {toRemove.Count} map(s) (outcomes in entry logs).");
        }

        /// <summary>手动休眠当前图（软休眠验证：不 tick、不显示为邻接地图、无访问入口，内容完好）。</summary>
        [DebugAction(Category, "Sleep Current Map (Dormancy)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SleepCurrentMap()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            if (!SeamlessDormancyManager.IsDormant(map))
            {
                SeamlessDormancyManager.Sleep(map, "dev command (Sleep Current Map)");
            }
            else
            {
                Log.Message($"[RimExodus] Map {map.uniqueID} is already dormant.");
            }
        }

        /// <summary>手动唤醒当前图（若休眠中）。注意：切图到休眠图会经 CurrentMap setter 自动唤醒。</summary>
        [DebugAction(Category, "Wake Current Map (Dormancy)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WakeCurrentMap()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            if (SeamlessDormancyManager.IsDormant(map))
            {
                SeamlessDormancyManager.Wake(map, "dev command (Wake Current Map)");
            }
            else
            {
                Log.Message($"[RimExodus] Map {map.uniqueID} is not dormant.");
            }
        }

        /// <summary>强制删除当前受管辖图（唯一删除入口 RemoveRollingMap：地块图达阈前哨转封存、其余销毁 Map+WorldObject；原生家族延迟原版偏好；家园/未管辖图拒绝）。</summary>
        [DebugAction(Category, "Force Delete Current Tile Map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceDeleteCurrentTileMap()
        {
            var map = Find.CurrentMap;
            if (!SeamlessMapGovernance.CanRollingDelete(map))
            {
                Log.Message("[RimExodus] Current map is not rolling-deletable (player home or unmanaged map).");
                return;
            }
            var tile = SeamlessTileRegistry.GetMapWorldTile(map);
            map.GetComponent<SeamlessTileManager>()?.RemoveRollingMap(map.Parent);
            // 结局（封存/延后询问/删除）由单一入口的 ARCHIVE/DEFERRED/DELETE 日志输出，此处不重复断言。
            Log.Message($"[RimExodus] Force delete dispatched via single entry wt={tile}.");
        }

        /// <summary>休眠状态报告：活跃/休眠图数、地图总数（127 上限余量）、governor 设置。</summary>
        [DebugAction(Category, "Dormancy Report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DormancyReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RimExodus Dormancy Report]");
            sb.AppendLine($"  maps total: {Find.Maps.Count} (Game.AddMap limit 127)");
            var dormant = 0;
            var governed = 0;
            foreach (var m in Find.Maps)
            {
                if (SeamlessMapGovernance.IsGoverned(m)) governed++;
                if (SeamlessDormancyManager.IsDormant(m)) dormant++;
            }
            sb.AppendLine($"  governed maps (tile ∪ native family): {governed}   dormant: {dormant}");
            var s = RimExodusMod.Settings;
            sb.AppendLine($"  governor: enabled={s?.dormancyEnabled ?? true} sleepHops={s?.dormancySleepHops ?? 2} deleteHops={s?.dormancyDeleteHops ?? 3}");
            Log.Message(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// 调试工具（Dev 地图工具）：点击地图格，输出该格的**结构化接缝诊断报告**
        /// （<see cref="SeamlessSeamOverride.DescribeCellReport"/>）：本侧 + 各对侧对应位置两栏，
        /// 每侧 = 位置（坐标+圈层）/ 生成时 snapshot（389 三层原生备份）/ 当前实际（三层现值）；
        /// 本侧另附混合情况追踪（权重 / 两侧各层 3×3 卷积 / 各层混合结果），对侧附实时/基础参考来源。
        /// 用于精确定位"某格 snapshot 是水/沙，但被邻居土卷积成了泥"、孤儿岩墙等接缝混合问题。
        /// </summary>
        [DebugAction(Category, "Inspect Snapshot At Position", false, false, false, false, false, 0, false,
            actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectSnapshotAtPosition()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            var cell = UI.MouseCell();
            if (!cell.InBounds(map)) return;

            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[RimExodus-SnapshotInspect] map={map.uniqueID} cell=({cell.x},{cell.z}) wt={worldTile}");

            if (worldTile < 0)
            {
                sb.AppendLine("  (no valid worldTile, no polygon/neighbor info)");
            }
            else
            {
                sb.Append(SeamlessSeamOverride.DescribeCellReport(map, worldTile, cell));
            }

            Log.Message(sb.ToString().TrimEnd());
        }

        // ------- 2026-09 诊断：ancient vent 邻图背景不可见调查（定案后去留随正式修复定夺） -------
        // 背景：AncientSmokeVent 是 MapMeshOnly 静态 mesh thing，印刷链（SectionLayer_Things.Regenerate
        // → Thing.Print → Graphic.Print → Printer_Plane.PrintPlane）与邻图收集链（SeamlessTileRenderer
        // 的 SectionLayer_ThingsGeneral 精确类型收集）逐环节静态核验闭合、无任何 per-def 排除，但实测
        // 该 thing 在邻图背景整只缺失（同区域岩石/建筑正常）。两个动作各一次实测即可定案：
        //   Probe —— 回答"四边形到底在不在邻图 mesh 里"（读 mesh.vertices 权威几何），并复算
        //            Regenerate 的逐条印刷条件（雾快照/RealtimeOnly/雪沙埋藏）供直接对照；
        //   Regen —— 全量重烘邻图 sections，区分"mesh 陈旧/烘焙期缺失"与"收集/提交侧丢失"。

        /// <summary>探针目标 def（Odyssey 三种 ancient vent；RealtimeOnly 的 HeatVent 预期不在 mesh，作对照）。</summary>
        private static readonly string[] MeshProbeDefNames = { "AncientSmokeVent", "AncientToxVent", "AncientHeatVent" };

        /// <summary>MapDrawer.sections 私有字段访问（诊断动作自持，勿扩散到生产代码）。</summary>
        private static AccessTools.FieldRef<MapDrawer, Section[,]> meshProbeSectionsRef;

        private static Section[,] MeshProbeSections(Map map)
        {
            meshProbeSectionsRef ??= AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");
            return meshProbeSectionsRef(map.mapDrawer);
        }

        /// <summary>
        /// 探针：对每个收集中的邻图上的目标 def，输出印刷条件复算 + 所在 section 状态 +
        /// "四边形是否真在邻图 ThingsGeneral mesh 里"（扫描全部 section 的已上传 mesh.vertices，
        /// 找 TrueCenter ±3.6 格内的顶点，≥4 即一个 quad）。输出为 Dev 动作结果，常开不门控。
        /// </summary>
        [DebugAction(Category, "Probe: Neighbor Things Mesh", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ProbeNeighborThingsMesh()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            if (SeamlessTileRegistry.GetMapWorldTile(map) < 0)
            {
                Log.Warning("[RimExodus][mesh-probe] Current map has no valid world tile.");
                return;
            }

            var neighbors = new List<SeamlessTileGraph.NeighborInfo>();
            SeamlessTileGraph.PopulateNeighbors(map, neighbors);
            if (neighbors.Count == 0)
            {
                Log.Message("[RimExodus][mesh-probe] no collected neighbors (dormant/unloaded?).");
                return;
            }

            var hostViewRect = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
            var foundAny = false;
            foreach (var nb in neighbors)
            {
                var sections = MeshProbeSections(nb.map);
                if (sections == null)
                {
                    Log.Message($"[RimExodus][mesh-probe] neighbor tile={nb.worldTile}: mapDrawer.sections == null");
                    continue;
                }
                var neighborView = hostViewRect.MovedBy(-nb.offset).ClipInsideMap(nb.map);
                Log.Message($"[RimExodus][mesh-probe] === neighbor tile={nb.worldTile} map={nb.map.uniqueID} offset={nb.offset} neighborView={neighborView} ===");

                foreach (var t in nb.map.listerThings.AllThings)
                {
                    if (t.Destroyed || !IsMeshProbeTarget(t)) continue;
                    foundAny = true;
                    ProbeOneThing(nb.map, sections, neighborView, t);
                }
            }

            if (!foundAny)
            {
                var sb = new System.Text.StringBuilder("[RimExodus][mesh-probe] no probe def found on collected neighbors. Maps containing probe defs:");
                foreach (var m in Find.Maps)
                {
                    var count = 0;
                    foreach (var t in m.listerThings.AllThings)
                    {
                        if (!t.Destroyed && IsMeshProbeTarget(t)) count++;
                    }
                    if (count > 0)
                    {
                        sb.Append($" [map={m.uniqueID} wt={SeamlessTileRegistry.GetMapWorldTile(m)} x{count}]");
                    }
                }
                Log.Message(sb.Append(" (不在收集集合 = 该图休眠/未加载/非直接邻居)").ToString());
            }
        }

        private static bool IsMeshProbeTarget(Thing t)
        {
            var defName = t.def.defName;
            foreach (var probe in MeshProbeDefNames)
            {
                if (defName == probe) return true;
            }
            return false;
        }

        private static void ProbeOneThing(Map nbMap, Section[,] sections, CellRect neighborView, Thing t)
        {
            var center = t.TrueCenter();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[RimExodus][mesh-probe] {t.def.defName} id={t.thingIDNumber} map={nbMap.uniqueID} pos={t.Position} trueCenter=({center.x:F2},{center.z:F2})");
            // Regenerate 六条件逐项复算（对照 SectionLayer_Things.Regenerate 的过滤序）。
            var snow = nbMap.snowGrid.GetDepth(t.Position);
            var sand = t.Position.GetSandDepth(nbMap);
            sb.AppendLine($"    cond[fog] fogged={nbMap.fogGrid.IsFogged(t.Position)} seeThroughFog={t.def.seeThroughFog}");
            sb.AppendLine($"    cond[drawerType]={t.def.drawerType} cond[snowSand]={Mathf.Max(snow, sand):F2}/{t.def.hideAtSnowOrSandDepth} cond[plant]={(t.def.plant == null ? "n/a(building)" : "plant")} dontPrint={t.def.dontPrint}");

            var scX = Mathf.FloorToInt(t.Position.x / 17f);
            var scZ = Mathf.FloorToInt(t.Position.z / 17f);
            var sec = sections[scX, scZ];
            var thingsLayer = sec?.GetLayer(typeof(SectionLayer_ThingsGeneral));
            sb.AppendLine($"    section=({scX},{scZ}) bounds={sec?.Bounds.ToStringSafe()} dirtyFlags={sec?.dirtyFlags} overlapsNeighborView={(sec != null && sec.Bounds.Overlaps(neighborView))} layer={(thingsLayer == null ? "NULL" : thingsLayer.GetType().Name)} layerDirty={thingsLayer?.Dirty ?? false}");
            if (thingsLayer != null)
            {
                var shown = 0;
                foreach (var sm in thingsLayer.subMeshes)
                {
                    if (shown++ >= 8)
                    {
                        sb.AppendLine("      ...(more submeshes omitted)");
                        break;
                    }
                    sb.AppendLine($"      subMesh mat={sm.material?.name} queue={sm.material?.renderQueue} finalized={sm.finalized} disabled={sm.disabled} verts={(sm.mesh == null ? -1 : sm.mesh.vertexCount)}");
                }
            }

            // 四边形存在性判定：扫描全部 section 的 ThingsGeneral 已上传几何（mesh.vertices 为权威，
            // 不依赖 FinalizeMesh 后 verts 列表是否留存）。
            var hitVerts = 0;
            for (var x = 0; x < sections.GetLength(0); x++)
            {
                for (var z = 0; z < sections.GetLength(1); z++)
                {
                    var layer = sections[x, z]?.GetLayer(typeof(SectionLayer_ThingsGeneral));
                    if (layer == null) continue;
                    foreach (var sm in layer.subMeshes)
                    {
                        if (sm.mesh == null) continue;
                        var verts = sm.mesh.vertices;
                        var hits = 0;
                        foreach (var v in verts)
                        {
                            if (Mathf.Abs(v.x - center.x) <= 3.6f && Mathf.Abs(v.z - center.z) <= 3.6f) hits++;
                        }
                        if (hits > 0)
                        {
                            hitVerts += hits;
                            sb.AppendLine($"      QUAD-HIT section=({x},{z}) mat={sm.material?.name} queue={sm.material?.renderQueue} finalized={sm.finalized} disabled={sm.disabled} meshVerts={verts.Length} nearVerts={hits}");
                        }
                    }
                }
            }
            sb.AppendLine(hitVerts >= 4
                ? "    => quad IS in mesh（提交侧问题：下一步给 CollectLayer 加提交日志二分）"
                : "    => quad NOT in mesh（烘焙侧问题：对照上方 cond 行与 subMesh 清单定位被哪条过滤）");
            Log.Message(sb.ToString().TrimEnd());
        }

        /// <summary>
        /// 强制全量重烘全部收集邻图的 sections（手动全覆盖锤：RegenerateAllLayers + 清 dirtyFlags，
        /// 2026-09 起常规渲染路径只做视区内命中层的 Section.TryUpdate，此动作保留为诊断用全量重烘）。
        /// 配合观察：按完后邻图背景 vent 是否出现——
        /// 出现 = mesh 曾陈旧/烘焙期缺失；仍不出现 = 收集/提交侧丢失（与 Probe 输出互相印证）。
        /// 注意单次成本 = 邻图 mesh 全量重建（历史实测单图数百 ms 量级），仅诊断用。
        /// </summary>
        [DebugAction(Category, "Force Regen Neighbor Sections", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceRegenNeighborSections()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            var neighbors = new List<SeamlessTileGraph.NeighborInfo>();
            SeamlessTileGraph.PopulateNeighbors(map, neighbors);
            if (neighbors.Count == 0)
            {
                Log.Message("[RimExodus][mesh-probe] no collected neighbors to regenerate.");
                return;
            }

            foreach (var nb in neighbors)
            {
                var sections = MeshProbeSections(nb.map);
                if (sections == null)
                {
                    Log.Message($"[RimExodus][mesh-probe] tile={nb.worldTile}: sections == null, skipped");
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var regen = 0;
                for (var x = 0; x < sections.GetLength(0); x++)
                {
                    for (var z = 0; z < sections.GetLength(1); z++)
                    {
                        var sec = sections[x, z];
                        if (sec == null) continue;
                        sec.RegenerateAllLayers();
                        sec.dirtyFlags = 0uL;
                        regen++;
                    }
                }
                Log.Message($"[RimExodus][mesh-probe] force regen tile={nb.worldTile}: {regen} sections in {sw.ElapsedMilliseconds}ms（观察下一帧邻图背景）");
            }
        }
    }
}
