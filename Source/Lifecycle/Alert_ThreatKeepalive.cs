using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 威胁保活持续告警（2026-09，用户需求"自定义低优先持续告警，类似需要治疗/缺少床铺"）：
    /// 存在因活跃敌人而保持全速（不降频）的地图时，在右下告警区提示是哪些地图（tile 经纬度）。
    ///
    /// 数据源 = governor 的追踪名单快照（<see cref="SeamlessDormancyGovernor.ThreatKeepaliveSnapshot"/>
    /// ——Sweep 每轮发布、独立轮询段注销；本告警按原版节奏每 ~24 帧重算一次，读快照零遍历）。
    /// 原版 <see cref="Alert"/> 子类由 AlertsReadout 的 AllLeafSubclasses 扫描自动发现，零注册代码。
    /// **刻意不做点击跳转**（返回 <see cref="AlertReport.Active"/>）：告警点击跳 culprit 相机位，
    /// 对"跳到别的地图"意味着切 CurrentMap——太激进，纯提示即够（用户例句只有提示语义）。
    /// </summary>
    public class Alert_ThreatKeepalive : Alert
    {
        public Alert_ThreatKeepalive()
        {
            // 枚举最低档（Medium/High/Critical 三档）= "低优先持续告警"（需要治疗/无聊同族，用户定夺）。
            defaultPriority = AlertPriority.Medium;
        }

        private static List<Map> Snapshot()
        {
            return Current.Game?.GetComponent<SeamlessDormancyGovernor>()?.ThreatKeepaliveSnapshot();
        }

        public override AlertReport GetReport()
        {
            var maps = Snapshot();
            return maps != null && maps.Count > 0 ? AlertReport.Active : AlertReport.Inactive;
        }

        public override string GetLabel()
        {
            return "RimExodus_ThreatKeepaliveLabel".Translate(Snapshot()?.Count ?? 0);
        }

        public override TaggedString GetExplanation()
        {
            var sb = new StringBuilder();
            var maps = Snapshot();
            if (maps != null)
            {
                // 稳定排序防每帧重算时条目跳动。
                maps.Sort((a, b) => a.uniqueID.CompareTo(b.uniqueID));
                foreach (var m in maps)
                {
                    var latLong = "?";
                    var tile = SeamlessTileRegistry.GetMapWorldTile(m);
                    if (tile >= 0 && Find.WorldGrid != null)
                    {
                        var v = Find.WorldGrid.LongLatOf(new PlanetTile(tile));
                        latLong = $"{v.x:0.0}, {v.y:0.0}";
                    }

                    sb.AppendLine("RimExodus_ThreatKeepaliveEntry".Translate(latLong, m.Parent?.Label ?? "?"));
                }
            }

            return "RimExodus_ThreatKeepaliveExplanation".Translate(sb.ToString().TrimEnd());
        }
    }
}
