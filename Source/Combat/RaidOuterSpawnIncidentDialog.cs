using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 袭击外缘生成的事件类型配置（2026-09，默认全员跨图）：列出所有走外缘进场链的事件
    ///（raid 族 + 发狂动物/食尸鬼/奇美拉/EntitySwarm 族），逐项勾选"仅本图生成"。
    /// 默认全部跨图（mod 扩展的事件同样默认跨图——未知策略按 parms 旗标降级为普通突击迁移，
    /// 专属策略行为可能丢失），本列表即玩家的逐事件退出通道。改动即时生效，随存档设置持久化。
    /// 覆写 MakeLords 的策略（机械体围攻/PsychicRitualSiege 等）结构性不经迁移链，勾选与否
    /// 对它们无效果。
    /// </summary>
    public class RaidOuterSpawnIncidentDialog : Window
    {
        public override Vector2 InitialSize => new Vector2(560f, 640f);

        private Vector2 scroll;
        private float contentHeight;
        private readonly List<IncidentDef> incidents;

        public RaidOuterSpawnIncidentDialog()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            // 候选 = worker 属于外缘进场链的事件（与 Patches_RaidOuterSpawn 的入口 patch 同族判定）。
            incidents = DefDatabase<IncidentDef>.AllDefs
                .Where(d => IsOuterSpawnCandidate(d.workerClass))
                .OrderBy(d => d.label)
                .ToList();
        }

        internal static bool IsOuterSpawnCandidate(System.Type workerType)
        {
            return workerType != null
                && (typeof(IncidentWorker_Raid).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_AggressiveAnimals).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_GhoulAttack).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_ChimeraAssault).IsAssignableFrom(workerType)
                    || typeof(IncidentWorker_EntitySwarm).IsAssignableFrom(workerType));
        }

        public override void DoWindowContents(Rect inRect)
        {
            var settings = RimExodusMod.Settings;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f),
                "RimExodus_RaidOuterSpawnIncidentsTitle".Translate());
            Text.Font = GameFont.Small;
            var hintRect = new Rect(0f, 38f, inRect.width, 58f);
            Widgets.Label(hintRect, "RimExodus_RaidOuterSpawnIncidentsHint".Translate());
            TooltipHandler.TipRegion(hintRect, "RimExodus_RaidOuterSpawnIncidentsHintTip".Translate());

            var listRect = new Rect(0f, 102f, inRect.width, inRect.height - 102f);
            var viewRect = new Rect(0f, 0f, listRect.width - 16f, contentHeight);
            Widgets.BeginScrollView(listRect, ref scroll, viewRect);
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(viewRect);
            foreach (var def in incidents)
            {
                // 结构性本图（灵能仪式围攻等——玩法锚定被袭击图，跨图即抹除内容）：标注并锁定勾选。
                var structural = SeamlessRaidOuterSpawn.IsStructurallyLocalWorker(def.workerClass);
                var local = structural || settings.raidOuterSpawnLocalIncidents.Contains(def.defName);
                var lineRect = listing.GetRect(30f);
                var label = $"{def.label} ({def.defName})";
                if (structural) label += " — " + "RimExodus_RaidOuterSpawnAlwaysLocal".Translate().Resolve();
                Widgets.CheckboxLabeled(lineRect, label, ref local, structural);
                TooltipHandler.TipRegion(lineRect,
                    structural
                        ? "RimExodus_RaidOuterSpawnAlwaysLocalTip".Translate()
                        : "RimExodus_RaidOuterSpawnLocalToggleTip".Translate());
                if (!structural && local != settings.raidOuterSpawnLocalIncidents.Contains(def.defName))
                {
                    if (local) settings.raidOuterSpawnLocalIncidents.Add(def.defName);
                    else settings.raidOuterSpawnLocalIncidents.Remove(def.defName);
                }
            }
            listing.End();
            contentHeight = listing.CurHeight + 8f;
            Widgets.EndScrollView();
        }
    }
}
