using System.Collections.Generic;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 武器射程映射曲线（默认关闭）：跨图交战常态化后，把远程武器射程按玩家配置的分段线性
    /// 曲线整体拉伸，并把原版精度锚点距离（3/12/25/40）同曲线重映射。
    ///
    /// 原版 AI/UI/射程校验、RimExodus 跨图层与 CE 兼容层全部实时读取
    /// <see cref="VerbProperties.range"/>（经 EffectiveRange），全链路无缓存——本类是唯一的
    /// def 变异入口：启动长事件收尾快照原始射程，之后总是"从原件重建式"应用（幂等，杜绝
    /// 增量二改），关闭即还原，开关/改值即时生效、无需重启。枚举范围 = 所有 ThingDef 的
    /// <see cref="ThingDef.Verbs"/> 中 <see cref="VerbProperties.LaunchesProjectile"/> 为真者
    /// （武器/炮塔 gun def/种族喷火等；炮塔 verb 来自 turretGunDef 这一普通注册的武器 ThingDef，
    /// AllDefs 遍历天然覆盖，无需单独分支）；<see cref="VerbProperties.rangeStat"/> 型 verb 跳过
    /// （AdjustedRange 不读 range 字段，变异无效）。
    ///
    /// 精度锚点不在 def 上（3/12/25/40 硬编码于 VerbProperties.GetHitChanceFactor 与
    /// ShotReport.HitFactorFromShooter），由 Patches_RangeCurveAccuracy 的两个 Prefix 做距离
    /// 逆映射；CE 不消费原版锚点（自建散布模型，距离惩罚按武器射程归一，改 range 后自动适配）。
    /// 两族精度 StatDef 描述（武器 Accuracy* 与射手 ShootingAccuracyFactor_*，文案含锚点距离，
    /// 如"武器在40格距离或更远时的精度乘数"）随曲线改写/还原——只替换描述里独立的旧锚点数字，
    /// 本地化措辞原样保留。
    /// </summary>
    internal static class SeamlessRangeCurve
    {
        /// <summary>曲线定义域控制点：前四个 = 原版精度锚点（Touch/Short/Medium/Long），末点为上界。</summary>
        internal static readonly float[] Anchors = { 3f, 12f, 25f, 40f, 60f };

        /// <summary>
        /// 默认目标曲线（设置项的出厂值）：刻意压制短射程武器的增长、放大长射程武器的射程差异
        /// ——环境尺度（掩体/房间/移动速度）不随无缝世界放大，只有远距离交战包络变大，故曲线
        /// 底部接近原样、顶端加速放大（各段倍率 1.67/1.83/1.80/2.13/2.33）。原版经典武器
        /// 映射：霰弹 16→29、左轮 26→48（恰过中距锚点，保持"恰好摸到中距离"的原版定位）、
        /// 突击步枪 31→61、栓动 37→77、狙击 45→99（跨缝压制成型）。
        /// </summary>
        internal static readonly float[] DefaultTargets = { 5f, 22f, 45f, 85f, 140f };

        /// <summary>两个精度 Prefix 的早退门：开关开启且快照已建立。</summary>
        internal static bool Active => enabled && originalRanges != null;

        /// <summary>已快照的远程 verb 数（设置状态行之用）。</summary>
        internal static int SnapshotCount => originalRanges?.Count ?? 0;

        private static bool enabled;
        /// <summary>最近一次成功应用的曲线（与 def 现状一致；无效曲线/关闭时为 null，逆映射回退恒等）。</summary>
        private static float[] applied;
        private static Dictionary<VerbProperties, float> originalRanges;

        /// <summary>
        /// 描述文案含锚点距离的 StatDef（每两行一组对应 Anchors[i/2]）：武器精度四档 +
        /// 射手精度因子四档。在 Initialize（DefOf 已注入）时解析，不在静态字段初始化器里
        /// 引用 StatDefOf——类静态构造时机早于 DefOf 注入。
        /// </summary>
        private static StatDef[] anchorStatDefs;
        private static string[] originalStatDescriptions;

        /// <summary>
        /// 启动初始化（RimExodusMod 构造器经 LongEventHandler.ExecuteWhenFinished 排入）：
        /// 此时全部 mod Def 已稳定且早于任何玩家地图生成；CE 经 XML PatchOperation 在 def 加载期
        /// 已把 verb 换装为 VerbPropertiesCE（继承同一 range 字段），其运行时注入不改 range，
        /// 先后无冲突。无条件快照（供中途开/关/改值还原），再按当前设置应用。
        /// </summary>
        internal static void Initialize()
        {
            originalRanges = new Dictionary<VerbProperties, float>();
            var rangeStatSkipped = 0;
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                var verbs = def.Verbs;
                for (var i = 0; i < verbs.Count; i++)
                {
                    var props = verbs[i];
                    if (!props.LaunchesProjectile) continue;
                    if (props.rangeStat != null)
                    {
                        rangeStatSkipped++;
                        continue;
                    }
                    originalRanges[props] = props.range;
                }
            }
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Weapon range curve: snapshotted {originalRanges.Count} projectile verb(s) " +
                $"(rangeStat-driven verbs skipped: {rangeStatSkipped}).");
            // 精度 StatDef 描述快照（改写前原文），供应用时替换与关闭时还原。
            anchorStatDefs = new[]
            {
                StatDefOf.AccuracyTouch, StatDefOf.ShootingAccuracyFactor_Touch,
                StatDefOf.AccuracyShort, StatDefOf.ShootingAccuracyFactor_Short,
                StatDefOf.AccuracyMedium, StatDefOf.ShootingAccuracyFactor_Medium,
                StatDefOf.AccuracyLong, StatDefOf.ShootingAccuracyFactor_Long,
            };
            originalStatDescriptions = new string[anchorStatDefs.Length];
            for (var i = 0; i < anchorStatDefs.Length; i++)
            {
                originalStatDescriptions[i] = anchorStatDefs[i]?.description;
            }
            SetEnabled(RimExodusMod.Settings.weaponRangeCurveEnabled);
        }

        /// <summary>开关切换（设置 UI 即时调用）：开启时按当前设置应用，关闭时整体还原原件。</summary>
        internal static void SetEnabled(bool value)
        {
            enabled = value;
            if (originalRanges == null) return;
            if (value) ApplyFromSettings();
            else RestoreAll();
        }

        /// <summary>设置值变动（曲线编辑器即时调用）：仅在开启时重应用；非法曲线不动 def、返回 false。</summary>
        internal static bool ApplyFromSettings()
        {
            if (originalRanges == null) return false;
            var ys = ReadSettingsCurve();
            if (!IsValid(ys))
            {
                RimExodusLog.Message(RimExodusLogModule.Combat,
                    "Weapon range curve: values rejected (must be positive and strictly increasing); " +
                    "definitions keep the last valid curve.");
                return false;
            }
            applied = ys;
            foreach (var entry in originalRanges)
            {
                entry.Key.range = MapRange(entry.Value, ys);
            }
            RewriteStatDescriptions(ys);
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Weapon range curve applied to {originalRanges.Count} projectile verb(s): " +
                $"{Describe(ys)}.");
            return true;
        }

        /// <summary>设置值变动（曲线编辑器即时调用）：仅开关开启时重应用；关闭时只存值不生效。</summary>
        internal static void SettingsChanged()
        {
            if (!enabled) return;
            ApplyFromSettings();
        }

        /// <summary>整体还原启动快照（关闭开关时调用）。</summary>
        internal static void RestoreAll()
        {
            applied = null;
            if (originalRanges == null) return;
            foreach (var entry in originalRanges)
            {
                entry.Key.range = entry.Value;
            }
            if (anchorStatDefs != null)
            {
                for (var i = 0; i < anchorStatDefs.Length; i++)
                {
                    if (anchorStatDefs[i] != null)
                    {
                        anchorStatDefs[i].description = originalStatDescriptions[i];
                    }
                }
            }
            RimExodusLog.Message(RimExodusLogModule.Combat,
                $"Weapon range curve disabled: restored original range on {originalRanges.Count} projectile verb(s).");
        }

        /// <summary>当前设置值是否构成合法曲线（全为正且严格递增，保证逆映射存在）。</summary>
        internal static bool CurrentValuesValid()
        {
            return IsValid(ReadSettingsCurve());
        }

        /// <summary>按当前设置值试算映射（曲线编辑器的试算行；非法曲线返回 false）。</summary>
        internal static bool TryMapFromSettings(float x, out float mapped)
        {
            var ys = ReadSettingsCurve();
            if (!IsValid(ys))
            {
                mapped = x;
                return false;
            }
            mapped = MapRange(x, ys);
            return true;
        }

        /// <summary>
        /// 精度距离逆映射（两个 Prefix 的唯一数学入口）：新尺度距离 d → 原尺度距离。
        /// 与 <see cref="MapRange"/> 关于"当前已应用曲线"互逆；applied 为 null（未应用/非法）时恒等。
        /// </summary>
        internal static float InverseRange(float d)
        {
            var ys = applied;
            if (ys == null) return d;
            // 下侧恒等取严格小于：d 恰为首锚点（新 Touch 距离）时落入首段 t=0 → 原尺度 3，
            // 与"锚点映到锚点"一致（用户规格：距离恰为新锚点时精确取该档精度，无半档偏移）。
            if (d < ys[0]) return d;
            for (var i = 0; i < Anchors.Length - 1; i++)
            {
                if (d <= ys[i + 1])
                {
                    return Mathf.Lerp(Anchors[i], Anchors[i + 1], InverseLerp(ys[i], ys[i + 1], d));
                }
            }
            // 末点之外是比例缩放区（f(x)=x·ys[4]/Anchors[4]），其逆同取比例。
            return d * (Anchors[Anchors.Length - 1] / ys[ys.Length - 1]);
        }

        /// <summary>
        /// 正向映射：&lt; 首锚点恒等（近战/超近与 minRange 语义不动）；锚点间分段线性；
        /// 超过末点按末点比例连续缩放（函数连续无跳变，迫击炮等超远程武器同口径放大）。
        /// </summary>
        internal static float MapRange(float x, float[] ys)
        {
            // 恒等取严格小于：射程恰为首锚点 3 时落入首段 t=0 → 映射到 ys[0]，与锚点语义一致。
            if (x < Anchors[0]) return x;
            for (var i = 0; i < Anchors.Length - 1; i++)
            {
                if (x <= Anchors[i + 1])
                {
                    return Mathf.Lerp(ys[i], ys[i + 1], InverseLerp(Anchors[i], Anchors[i + 1], x));
                }
            }
            return x * (ys[ys.Length - 1] / Anchors[Anchors.Length - 1]);
        }

        private static bool IsValid(float[] ys)
        {
            if (ys == null || ys.Length != Anchors.Length) return false;
            if (ys[0] <= 0f) return false;
            for (var i = 1; i < ys.Length; i++)
            {
                if (ys[i] <= ys[i - 1]) return false;
            }
            return true;
        }

        /// <summary>
        /// 改写两族精度 StatDef 描述中的锚点距离文案（"在40格或更远"→"在60格或更远"）。
        /// 从快照原文重建（幂等）；描述里没有旧锚点数字的（第三方改写/未含距离）自然不动。
        /// </summary>
        private static void RewriteStatDescriptions(float[] ys)
        {
            if (anchorStatDefs == null) return;
            for (var i = 0; i < anchorStatDefs.Length; i++)
            {
                var stat = anchorStatDefs[i];
                if (stat == null) continue;
                stat.description = ReplaceDistanceToken(originalStatDescriptions[i], Anchors[i / 2], ys[i / 2]);
            }
        }

        /// <summary>把文案中独立的旧锚点距离数字替换为新值：前后都不是数字才算命中（避免改到
        /// 其他数值或"140"里的"40"），本地化措辞原样保留。from == to 时原样返回。</summary>
        private static string ReplaceDistanceToken(string text, float from, float to)
        {
            if (string.IsNullOrEmpty(text) || Mathf.Approximately(from, to)) return text;
            return Regex.Replace(text, $@"(?<!\d){from:0}(?!\d)", to.ToString("0.#"));
        }

        private static float[] ReadSettingsCurve()
        {
            var s = RimExodusMod.Settings;
            return new[] { s.weaponRangeCurveTouch, s.weaponRangeCurveShort, s.weaponRangeCurveMedium,
                s.weaponRangeCurveLong, s.weaponRangeCurveFar };
        }

        private static float InverseLerp(float a, float b, float value)
        {
            return b - a <= Mathf.Epsilon ? 0f : (value - a) / (b - a);
        }

        private static string Describe(float[] ys)
        {
            return $"{Anchors[0]}→{ys[0]:0.#}, {Anchors[1]}→{ys[1]:0.#}, {Anchors[2]}→{ys[2]:0.#}, " +
                   $"{Anchors[3]}→{ys[3]:0.#}, {Anchors[4]}→{ys[4]:0.#}";
        }
    }
}
