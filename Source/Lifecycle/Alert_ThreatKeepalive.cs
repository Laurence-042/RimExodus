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
    /// ——Sweep 每轮登记、独立轮询段注销；本告警按原版节奏重算，从每张追踪图的敌对目标缓存取一个聚焦目标）。
    /// 原版 <see cref="Alert"/> 子类由 AlertsReadout 的 AllLeafSubclasses 扫描自动发现，零注册代码。
    /// 每张受保护地图选一个活跃敌人作为 culprit，复用原版告警的点击聚焦与轮流跳转。
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
            var maps = Current.Game?.GetComponent<SeamlessDormancyGovernor>()?.ThreatKeepaliveSnapshot();
            // 点击循环与说明列表使用相同的稳定地图顺序。
            maps?.Sort((a, b) => a.uniqueID.CompareTo(b.uniqueID));
            return maps;
        }

        public override AlertReport GetReport()
        {
            var maps = Snapshot();
            if (maps == null || maps.Count == 0) return AlertReport.Inactive;

            var targets = new List<GlobalTargetInfo>(maps.Count);
            foreach (var map in maps)
            {
                if (!Find.Maps.Contains(map)) continue;
                if (SeamlessMapGovernance.TryGetActiveThreat(map, out var enemy)
                    && enemy.Spawned && enemy.Map == map)
                {
                    targets.Add(new GlobalTargetInfo(enemy));
                }
                else
                {
                    // 名单的独立复查尚未清掉失效敌人时，告警仍能跳到对应地图。
                    targets.Add(new GlobalTargetInfo(map.Center, map));
                }
            }
            return AlertReport.CulpritsAre(targets);
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
