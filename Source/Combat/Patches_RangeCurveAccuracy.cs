using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 精度锚点距离重映射（<see cref="SeamlessRangeCurve"/> 开启时生效）。
    ///
    /// 原版 3/12/25/40 锚点硬编码在两处：<see cref="VerbProperties.GetHitChanceFactor"/>
    /// （武器精度插值）与 <see cref="ShotReport.HitFactorFromShooter"/>（射手精度插值 + 指数衰减），
    /// 不在 def 上。唯一改法是在方法入口把实际距离逆映射回原尺度再走原版插值链——数学上等价于
    /// 把锚点搬到 f(3)/f(12)/f(25)/f(40)，且射手精度的 Mathf.Pow(命中率, 距离) 指数同步用原尺度
    /// 距离（武器精度剖面整体拉伸：新射程 51.5 处的命中率 = 原射程 32.5 处的命中率）。
    ///
    /// 公式本体永远跟随原版，无克隆漂移；RimExodus 跨图 ShotReport 修正
    /// （Patches_CombatTargeting 的 Postfix）调用的也是这两个原版方法，自动同口径。
    /// CE 不消费原版锚点（自建散布模型），无需也不得为其添加映射。关闭时开销 = 一次静态 bool 读。
    /// </summary>
    public static class Patches_RangeCurveAccuracy
    {
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.GetHitChanceFactor))]
    public static class Patch_VerbProperties_GetHitChanceFactor
    {
        public static void Prefix(ref float dist)
        {
            if (SeamlessRangeCurve.Active)
            {
                dist = SeamlessRangeCurve.InverseRange(dist);
            }
        }
    }

    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitFactorFromShooter))]
    public static class Patch_ShotReport_HitFactorFromShooter
    {
        public static void Prefix(ref float distance)
        {
            if (SeamlessRangeCurve.Active)
            {
                distance = SeamlessRangeCurve.InverseRange(distance);
            }
        }
    }
}
