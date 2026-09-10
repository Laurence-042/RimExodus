using System;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 武器射程曲线编辑器（仓库首个自定义 Window）：5 行固定锚点（3/12/25/40/60 = 原版四个
    /// 精度区间 + 上界）右列数值编辑 + 曲线预览图 + 试算行。每次合法编辑即时调用
    /// <see cref="SeamlessRangeCurve.SettingsChanged"/>（开关关闭时仅保存设置）。
    ///
    /// 输入缓冲跨帧持久 + 未聚焦强制镜像当前值（照搬 RimExodusMod.NumericRow 的 2026-09-04
    /// 回归教训：静态 buffer 跨存档存活，读新档后残留文本会被误显并当作编辑基线写回）。
    /// 预览绘制用 Verse.Widgets.DrawLine（IMGUI 旋转纹理画线），采样步长 1 格。
    /// </summary>
    public class RangeCurveEditorDialog : Window
    {
        private static readonly string[] RowLabelKeys =
        {
            "RimExodus_RangeCurveRowTouch", "RimExodus_RangeCurveRowShort", "RimExodus_RangeCurveRowMedium",
            "RimExodus_RangeCurveRowLong", "RimExodus_RangeCurveRowFar",
        };

        private static readonly string[] ControlNames =
        {
            "RimExodus_FieldRangeCurveTouch", "RimExodus_FieldRangeCurveShort", "RimExodus_FieldRangeCurveMedium",
            "RimExodus_FieldRangeCurveLong", "RimExodus_FieldRangeCurveFar",
        };

        private readonly string[] buffers = new string[5];

        // 试算原射程（默认取 25-40 中点，演示插值语义：32.5 → 65（默认曲线下 45-85 中点））。
        private float probeValue = 32.5f;
        private string probeBuffer;

        public override Vector2 InitialSize => new Vector2(560f, 720f);

        public RangeCurveEditorDialog()
        {
            doCloseX = true;
            closeOnClickedOutside = false; // 文本输入中误点窗外不应丢编辑。
        }

        public override void DoWindowContents(Rect inRect)
        {
            var content = inRect.ContractedBy(14f);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(content.x, content.y, content.width, 30f),
                "RimExodus_RangeCurveDialogTitle".Translate());
            Text.Font = GameFont.Small;

            var listing = new Listing_Standard();
            listing.Begin(new Rect(content.x, content.y + 34f, content.width, content.height - 34f));

            listing.Label("RimExodus_RangeCurveIntro".Translate());

            // 表头：左列原射程（锚点固定），右列新射程（可编辑）。
            var header = listing.GetRect(22f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(LeftPart(header, 0.52f), "RimExodus_RangeCurveColumnOriginal".Translate());
            Widgets.Label(RightPart(header, 0.44f), "RimExodus_RangeCurveColumnNew".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            listing.Gap(2f);

            var s = RimExodusMod.Settings;
            CurveRow(listing, 0, s.weaponRangeCurveTouch, v => s.weaponRangeCurveTouch = v);
            CurveRow(listing, 1, s.weaponRangeCurveShort, v => s.weaponRangeCurveShort = v);
            CurveRow(listing, 2, s.weaponRangeCurveMedium, v => s.weaponRangeCurveMedium = v);
            CurveRow(listing, 3, s.weaponRangeCurveLong, v => s.weaponRangeCurveLong = v);
            CurveRow(listing, 4, s.weaponRangeCurveFar, v => s.weaponRangeCurveFar = v);

            var valid = SeamlessRangeCurve.CurrentValuesValid();

            // 校验提示行（高度恒定，无效时红字，避免布局跳动）。
            var validation = listing.GetRect(26f);
            if (!valid)
            {
                GUI.color = Color.red;
                Widgets.Label(validation, "RimExodus_RangeCurveInvalid".Translate());
                GUI.color = Color.white;
            }

            listing.Label("RimExodus_RangeCurvePreview".Translate());
            DrawCurvePreview(listing.GetRect(236f));

            DrawProbeRow(listing);

            if (listing.ButtonText("RimExodus_RangeCurveResetDefaults".Translate()))
            {
                s.weaponRangeCurveTouch = SeamlessRangeCurve.DefaultTargets[0];
                s.weaponRangeCurveShort = SeamlessRangeCurve.DefaultTargets[1];
                s.weaponRangeCurveMedium = SeamlessRangeCurve.DefaultTargets[2];
                s.weaponRangeCurveLong = SeamlessRangeCurve.DefaultTargets[3];
                s.weaponRangeCurveFar = SeamlessRangeCurve.DefaultTargets[4];
                for (var i = 0; i < buffers.Length; i++) buffers[i] = null;
                SeamlessRangeCurve.SettingsChanged();
            }

            // 状态行（高度恒定防布局跳动）：开关未开启 / 已应用规模；校验失败的红字已在上方提示。
            var status = listing.GetRect(24f);
            if (!s.weaponRangeCurveEnabled)
            {
                GUI.color = new Color(1f, 0.75f, 0.4f);
                Widgets.Label(status, "RimExodus_RangeCurveNotEnabledNote".Translate());
                GUI.color = Color.white;
            }
            else if (valid)
            {
                GUI.color = new Color(0.6f, 1f, 0.6f);
                Widgets.Label(status,
                    string.Format("RimExodus_RangeCurveAppliedNote".Translate(), SeamlessRangeCurve.SnapshotCount));
                GUI.color = Color.white;
            }

            listing.End();
        }

        /// <summary>单行曲线编辑：左侧行标签（tier 名 + 固定锚点），右侧 float 数字输入。</summary>
        private void CurveRow(Listing_Standard listing, int index, float current, Action<float> set)
        {
            var row = listing.GetRect(30f);
            var labelRect = LeftPart(row, 0.52f);
            Widgets.Label(labelRect, RowLabelKeys[index].Translate());
            TooltipHandler.TipRegionByKey(labelRect, "RimExodus_RangeCurveRowTip");
            var fieldRect = RightPart(row, 0.44f);
            GUI.SetNextControlName(ControlNames[index]);
            var tmp = current;
            Widgets.TextFieldNumeric(fieldRect, ref tmp, ref buffers[index], 0.1f, 1000f);
            // 未聚焦时强制镜像当前值：静态 buffer 跨存档存活，残留文本会被误显并当作编辑基线写回。
            if (GUI.GetNameOfFocusedControl() != ControlNames[index] && buffers[index] != tmp.ToString())
            {
                buffers[index] = null;
            }
            if (Math.Abs(tmp - current) > 1e-06)
            {
                set(tmp);
                SeamlessRangeCurve.SettingsChanged();
            }
            listing.Gap(2f);
        }

        /// <summary>试算行：输入一个原射程，实时显示映射结果（演示分段线性插值）。</summary>
        private void DrawProbeRow(Listing_Standard listing)
        {
            var row = listing.GetRect(30f);
            Widgets.Label(LeftPart(row, 0.34f), "RimExodus_RangeCurveProbeLabel".Translate());
            var fieldRect = new Rect(row.x + row.width * 0.36f, row.y + 1f, row.width * 0.16f, row.height - 2f);
            GUI.SetNextControlName("RimExodus_FieldRangeCurveProbe");
            Widgets.TextFieldNumeric(fieldRect, ref probeValue, ref probeBuffer, 0f, 1000f);
            var resultRect = new Rect(row.x + row.width * 0.54f, row.y, row.width * 0.46f, row.height);
            if (SeamlessRangeCurve.TryMapFromSettings(probeValue, out var mapped))
            {
                Widgets.Label(resultRect, string.Format("RimExodus_RangeCurveProbeResult".Translate(),
                    probeValue.ToString("0.#"), mapped.ToString("0.#")));
            }
            else
            {
                GUI.color = Color.red;
                Widgets.Label(resultRect, "RimExodus_RangeCurveInvalid".Translate());
                GUI.color = Color.white;
            }
            listing.Gap(6f);
        }

        /// <summary>曲线预览图：灰=恒等参考线，绿=当前曲线，白点=锚点，底部刻度=定义域锚点。</summary>
        private void DrawCurvePreview(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.13f, 0.13f, 0.15f));
            Widgets.DrawBox(rect, 1);
            if (!SeamlessRangeCurve.CurrentValuesValid())
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.red;
                Widgets.Label(rect, "RimExodus_RangeCurveInvalid".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            var s = RimExodusMod.Settings;
            var ys = new[] { s.weaponRangeCurveTouch, s.weaponRangeCurveShort, s.weaponRangeCurveMedium,
                s.weaponRangeCurveLong, s.weaponRangeCurveFar };

            var plot = new Rect(rect.x + 34f, rect.y + 8f, rect.width - 42f, rect.height - 32f);
            var axisMax = Mathf.Max(SeamlessRangeCurve.Anchors[4], ys[4]) * 1.08f;
            Vector2 ToPix(float x, float y)
            {
                return new Vector2(plot.x + x / axisMax * plot.width, plot.yMax - y / axisMax * plot.height);
            }

            // 恒等参考线（f(x)=x，两轴同上限 → 对角线）。
            Widgets.DrawLine(ToPix(0f, 0f), ToPix(axisMax, axisMax), new Color(0.45f, 0.45f, 0.45f, 0.7f), 1f);

            // 当前曲线：0→首锚点恒等段 + 分段线性 + 末点外比例直线，步长 1 格采样。
            Vector2 previous = ToPix(0f, 0f);
            for (var x = 1f; x <= axisMax; x += 1f)
            {
                var current = ToPix(x, SeamlessRangeCurve.MapRange(x, ys));
                Widgets.DrawLine(previous, current, new Color(0.35f, 0.9f, 0.45f), 2f);
                previous = current;
            }

            // 锚点刻度与标记点。
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperCenter;
            for (var i = 0; i < SeamlessRangeCurve.Anchors.Length; i++)
            {
                var anchor = SeamlessRangeCurve.Anchors[i];
                var bottom = ToPix(anchor, 0f);
                Widgets.DrawLine(new Vector2(bottom.x, plot.y), new Vector2(bottom.x, plot.yMax),
                    new Color(1f, 1f, 1f, 0.12f), 1f);
                Widgets.Label(new Rect(bottom.x - 20f, rect.yMax - 18f, 40f, 16f), anchor.ToString("0"));
                var point = ToPix(anchor, ys[i]);
                Widgets.DrawBoxSolid(new Rect(point.x - 2.5f, point.y - 2.5f, 5f, 5f), Color.white);
            }
            Text.Anchor = TextAnchor.UpperLeft;

            // 轴标签画在图内上沿，避开底部刻度数字。
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            Widgets.Label(new Rect(plot.x + 4f, plot.y + 2f, plot.width * 0.5f, 18f),
                "RimExodus_RangeCurveAxisNew".Translate());
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(plot.xMax - plot.width * 0.5f - 4f, plot.y + 2f, plot.width * 0.5f, 18f),
                "RimExodus_RangeCurveAxisOriginal".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private static Rect LeftPart(Rect row, float fraction)
        {
            return new Rect(row.x, row.y, row.width * fraction, row.height);
        }

        private static Rect RightPart(Rect row, float fraction)
        {
            return new Rect(row.x + row.width * (1f - fraction), row.y, row.width * fraction, row.height);
        }
    }
}
