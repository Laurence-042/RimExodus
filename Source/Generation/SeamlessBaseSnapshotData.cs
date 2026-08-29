using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 基础三层快照的**序列化载体**（2026-08，卸载前恢复原版兼容模式功能的支撑面）。
    ///
    /// 历史：三层快照（terrain/building/roof，void 裁切前原生数据，写入点
    /// <see cref="SeamlessTerrainFill.BackupSnapshotAndApplyVoid"/>）此前**非序列化**——读档即失，
    /// 因此卸载恢复只能在"生成后未读档的同一会话"内用内存快照，否则只剩就地复制回填的降级路径。
    /// 本类把快照以紧凑格式随 <see cref="SeamlessTileManager"/> 组件序列化（**仅原生 parent 图**——
    /// 地块图的 parent 本就是 RimExodus 类型、卸载恢复时必删，序列化纯浪费体积）：
    /// - terrain：全图每格 ushort（去重 defName 表索引；岩石等突变少、全表最简单且覆盖接缝混合消费面）；
    /// - building / roof：稀疏（非空格的 cellIndex + def 表索引；岩石/厚顶本就只占部分格）。
    ///
    /// 开关 <c>RimExodusSettings.serializeBaseSnapshots</c>（默认开）只门控**捕获**（保存时从内存数组
    /// 捕入本类）；已捕获的数据无条件照存（关闭开关不清数据——清除是设置 UI 二次确认流程的显式动作，
    /// 见 <see cref="Lifecycle.SeamlessUninstallRestore.PurgeAllSnapshots"/>）。读档无条件尝试还原
    /// 内存数组（<see cref="ToArrays"/>）——旧档无数据 = null，维持现状"读档后快照丢失"，混合参考
    /// 回落当前实况的既有路径不变；有数据则读档后内存快照可用（<c>DescribeCellReport</c> self 卷积
    /// 参考"读档后不可用回落当前"的降级顺带消失）。
    ///
    /// 体积量级（250×250）：terrain 62500×ushort + 两层稀疏各数千条，XML 原文几百 KB、存档 gzip 后
    /// 明显更小；在意体积的长玩玩家可关开关（关时有二次确认，见设置 UI）。
    /// </summary>
    public class SeamlessBaseSnapshotData : IExposable
    {
        // ===== terrain 层（全格稠密） =====
        private List<string> terrainDefs = new List<string>();
        private List<ushort> terrainIdx = new List<ushort>();

        // ===== building 层（稀疏：仅快照有岩石 def 的格） =====
        private List<string> buildingDefs = new List<string>();
        private List<int> buildingCells = new List<int>();
        private List<ushort> buildingIdx = new List<ushort>();

        // ===== roof 层（稀疏：仅快照有顶的格） =====
        private List<string> roofDefs = new List<string>();
        private List<int> roofCells = new List<int>();
        private List<ushort> roofIdx = new List<ushort>();

        /// <summary>是否为空数据（无 terrain 层 = 从未捕获）。</summary>
        public bool IsEmpty => terrainIdx == null || terrainIdx.Count == 0;

        /// <summary>
        /// 从内存三层数组捕获（保存时调用；任一层为 null 返回 null = 本次不序列化）。
        /// def 表去重：同图地形 def 种类个位数、岩石/屋顶两三种，表开销可忽略。
        /// </summary>
        public static SeamlessBaseSnapshotData Capture(TerrainDef[] terrain, ThingDef[] building, RoofDef[] roof)
        {
            if (terrain == null || building == null || roof == null) return null;
            if (terrain.Length != building.Length || terrain.Length != roof.Length) return null;

            var data = new SeamlessBaseSnapshotData();
            var terrainTable = new Dictionary<TerrainDef, int>();
            var buildingTable = new Dictionary<ThingDef, int>();
            var roofTable = new Dictionary<RoofDef, int>();

            // ushort 索引上限防御：def 表超过 65535 条（极端 mod 环境）放弃捕获。
            for (var i = 0; i < terrain.Length; i++)
            {
                var idx = IndexOf(terrainTable, data.terrainDefs, terrain[i]);
                if (idx < 0) return null;
                data.terrainIdx.Add((ushort)idx);

                var bDef = building[i];
                if (bDef != null)
                {
                    var bIdx = IndexOf(buildingTable, data.buildingDefs, bDef);
                    if (bIdx < 0) return null;
                    data.buildingCells.Add(i);
                    data.buildingIdx.Add((ushort)bIdx);
                }

                var rDef = roof[i];
                if (rDef != null)
                {
                    var rIdx = IndexOf(roofTable, data.roofDefs, rDef);
                    if (rIdx < 0) return null;
                    data.roofCells.Add(i);
                    data.roofIdx.Add((ushort)rIdx);
                }
            }
            return data;
        }

        private static int IndexOf<T>(Dictionary<T, int> table, List<string> names, T def) where T : Def, new()
        {
            if (def == null) return 0; // null 用表位 0（占位空串），读回 null。
            if (table.TryGetValue(def, out var idx)) return idx;
            if (names.Count >= ushort.MaxValue) return -1;
            table[def] = names.Count;
            names.Add(def.defName);
            return names.Count - 1;
        }

        private static T Resolve<T>(List<string> names, int idx) where T : Def, new()
        {
            if (idx <= 0 || idx >= names.Count) return null;
            return DefDatabase<T>.GetNamedSilentFail(names[idx]);
        }

        /// <summary>
        /// 读档还原内存三层数组（cellCount 应等于 map.Size.x * map.Size.z；不匹配返回 null 防错位）。
        /// def 失落（mod 环境 change）得 null——与"无快照"同降级，不炸。
        /// </summary>
        public bool ToArrays(int cellCount, out TerrainDef[] terrain, out ThingDef[] building, out RoofDef[] roof)
        {
            terrain = null; building = null; roof = null;
            if (terrainIdx == null || terrainIdx.Count != cellCount) return false;

            terrain = new TerrainDef[cellCount];
            building = new ThingDef[cellCount];
            roof = new RoofDef[cellCount];
            for (var i = 0; i < cellCount; i++)
            {
                terrain[i] = Resolve<TerrainDef>(terrainDefs, terrainIdx[i]);
            }
            if (buildingCells != null)
            {
                for (var i = 0; i < buildingCells.Count && i < buildingIdx.Count; i++)
                {
                    if (buildingCells[i] >= 0 && buildingCells[i] < cellCount)
                        building[buildingCells[i]] = Resolve<ThingDef>(buildingDefs, buildingIdx[i]);
                }
            }
            if (roofCells != null)
            {
                for (var i = 0; i < roofCells.Count && i < roofIdx.Count; i++)
                {
                    if (roofCells[i] >= 0 && roofCells[i] < cellCount)
                        roof[roofCells[i]] = Resolve<RoofDef>(roofDefs, roofIdx[i]);
                }
            }
            return true;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref terrainDefs, "terrainDefs", LookMode.Value);
            Scribe_Collections.Look(ref terrainIdx, "terrainIdx", LookMode.Value);
            Scribe_Collections.Look(ref buildingDefs, "buildingDefs", LookMode.Value);
            Scribe_Collections.Look(ref buildingCells, "buildingCells", LookMode.Value);
            Scribe_Collections.Look(ref buildingIdx, "buildingIdx", LookMode.Value);
            Scribe_Collections.Look(ref roofDefs, "roofDefs", LookMode.Value);
            Scribe_Collections.Look(ref roofCells, "roofCells", LookMode.Value);
            Scribe_Collections.Look(ref roofIdx, "roofIdx", LookMode.Value);
        }
    }
}
