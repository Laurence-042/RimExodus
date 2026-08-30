using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 中心走廊 genStep（order=100，MutatorPostElevationFertility(20) 之后、RocksFromGrid(200) 之前）：
    /// 邻接生成图 B 在 Caves 浮点网格上挖一条"本图中心 → 生成源方向共享边"的走廊，
    /// 保证 RocksFromGrid 在走廊格上不 spawn 岩石（elevation&gt;0.7 &amp;&amp; caves&lt;=0 才生成——
    /// 原版洞窟 MapGenCavesUtility 的同一条无后效通道），从而 B 中心到接缝必然不被岩石阻挡
    /// （深水等非岩地形不算阻挡，用户定义；走廊穿山段保留岩顶 = 原版山内隧道语义）。
    ///
    /// **为什么必须在 200 之前（勿后移）**：岩石 spawn 后只剩 building Destroy 路线——逐格销毁
    /// 触发屋顶支撑重算/脏区级联，长走廊代价高且带后效；提前写 Caves 网格则岩石根本不生成。
    /// 此时 Terrain(210) 未铺，端点筛选只做 A 侧真实可达性校验（B 侧地形未知、且需求本就
    /// 只排除岩石不排除水）。offset/边定位/传送圈几何全部纯几何现算，任意时机可用。
    ///
    /// **只挖 B 侧**（单向原则，与 392 混合同族）：不修改源图 A——A 能触发 B 的邻接生成，
    /// 其接缝侧已可通行（用户定夺）。
    ///
    /// 不进 MapPreview 预览白名单：预览纹理用 elevation≥0.7 代理画石色、不消费 Caves 网格。
    /// 实际逻辑委托 <see cref="SeamlessCenterCorridor"/>。
    /// </summary>
    public class GenStep_CenterCorridor : GenStep
    {
        public override int SeedPart => 82648347;

        public override void Generate(Map map, GenStepParams parms)
        {
            var worldTile = SeamlessTileRegistry.GetMapWorldTile(map);
            if (worldTile < 0) return;

            SeamlessCenterCorridor.Apply(map, worldTile);
        }
    }
}
