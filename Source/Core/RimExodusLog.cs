using Verse;

namespace RimExodus
{
    /// <summary>
    /// 诊断日志的功能模块划分（2026-09 分模块日志开关，取代单一 verboseLogging 总开关——
    /// 用户需求：允许追踪单个模块的开发问题 / 让报告者只开相关模块提供详细日志，避免全局 verbose spam）。
    ///
    /// 模块与开关字段（RimExodusSettings.log*）一一对应；归属按日志盘点（2026-09，268 个日志点）划分：
    /// - Generation：生成与地形（分帧 genStep/河路/接缝混合/快照/中心走廊/海岸补铺）
    /// - Transfer：传送与边界（跨缝传送/桥接/许可/边界预加载/点击重放/相机聚焦/跨图下令/出口格）
    /// - CaravanExit：离场链诊断（[caravan-exit]/[diag] 全部——用户点名独立开关）
    /// - Weather：天气域（域重算/接入/广播/门控/激活/校准埋点——用户点名独立开关）
    /// - Dormancy：休眠与性能（governor/降频 verbose 子集；SLEEP/WAKE/DELETE/Throttle 心跳刻意常开不收）
    /// - Combat：跨图战斗（索敌/射击/弹道交接）
    /// - Compat：兼容层（VF/PS/GL 的 verbose 子集；init/绑定确认/自检类刻意常开不收）
    /// - Settlement：据点与贸易（贸易商指定/影子远行队/贸易对话——原无条件 TraderDialog 14 处收编于此）
    /// - Core：核心与图管理（TileManager/BorderLookup/卸载恢复/Dev 调试动作）
    ///
    /// 纪律（勿回退）：全部 Warn/Error 不受开关控制（信号非 spam）；文档定过的常开心跳
    /// （Dormancy SLEEP/WAKE/DELETE、Throttle ON/OFF、GL 注册自检、Uninstall restore 一次性行）
    /// 不收编；旧 verboseLogging 字段保留仅为旧档兼容（读入后忽略，无 UI、无消费点）。
    /// </summary>
    public enum RimExodusLogModule
    {
        Generation,
        Transfer,
        CaravanExit,
        Weather,
        Dormancy,
        Combat,
        CombatExtended,
        Compat,
        Settlement,
        Core
    }

    /// <summary>
    /// 分模块日志入口（2026-09）。两类用法：
    /// ①门控替换：原 `RimExodusMod.Settings?.verboseLogging ?? false` 一律改为
    ///   `RimExodusLog.Enabled(RimExodusLogModule.X)`——**日志正文原样保留**（[caravan-exit] 等
    ///   既有标签用户已熟悉，不改写）；
    /// ②新埋点：`RimExodusLog.Message(RimExodusLogModule.X, "...")` 输出 `[RimExodus:X] ...` 前缀。
    /// 热路径成本 = 一次静态调用 + 字段读（Settings 为 null 时恒 false）。
    /// </summary>
    public static class RimExodusLog
    {
        public static bool Enabled(RimExodusLogModule module)
        {
            var s = RimExodusMod.Settings;
            if (s == null) return false;
            switch (module)
            {
                case RimExodusLogModule.Generation: return s.logGeneration;
                case RimExodusLogModule.Transfer: return s.logTransfer;
                case RimExodusLogModule.CaravanExit: return s.logCaravanExit;
                case RimExodusLogModule.Weather: return s.logWeather;
                case RimExodusLogModule.Dormancy: return s.logDormancy;
                case RimExodusLogModule.Combat: return s.logCombat;
                case RimExodusLogModule.CombatExtended: return s.logCombatExtended;
                case RimExodusLogModule.Compat: return s.logCompat;
                case RimExodusLogModule.Settlement: return s.logSettlement;
                case RimExodusLogModule.Core: return s.logCore;
                default: return false;
            }
        }

        /// <summary>门控通过才输出，前缀 [RimExodus:{module}]（新埋点用；迁移的旧日志走原 Log.Message 不经此）。</summary>
        public static void Message(RimExodusLogModule module, string text)
        {
            if (Enabled(module)) Log.Message("[RimExodus:" + module + "] " + text);
        }
    }
}
