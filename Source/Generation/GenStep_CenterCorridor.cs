using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 中心走廊 genStep（order=100，MutatorPostElevationFertility(20) 之后、RocksFromGrid(200) 之前）：
    /// 邻接生成图 B 在 Caves 浮点网格上挖"B 中心 ↔ 各已生成邻居方向接缝"的星形走廊，
    /// 保证 RocksFromGrid 在走廊格上不 spawn 岩石（elevation&gt;0.7 &amp;&amp; caves&lt;=0 才生成——
    /// 原版洞窟 MapGenCavesUtility 的同一条无后效通道），从而 B 中心到各接缝不被岩石阻挡
    /// （深水等非岩地形不算阻挡，用户定义；走廊穿山段保留岩顶 = 原版山内隧道语义）。
    ///
    /// **为什么必须在 200 之前（勿后移）**：岩石 spawn 后只剩 building Destroy 路线——逐格销毁
    /// 触发屋顶支撑重算/脏区级联，长走廊代价高且带后效；提前写 Caves 网格则岩石根本不生成。
    /// 此时 Terrain(210) 未铺，端点在**已生成邻居 N 侧**选（接缝带按 region 分集合随机代表、
    /// 可达目标[触发图=goto 触发位置/其余=中心]者镜像到 B），B 侧无需地形判定——v2 算法详见
    /// <see cref="SeamlessCenterCorridor"/> 类注释。
    ///
    /// **只挖 B 侧**（单向原则，与 392 混合同族）：不修改任何已生成邻居。
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
