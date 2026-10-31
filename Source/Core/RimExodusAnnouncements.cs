using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimExodus
{
    internal sealed class RimExodusAnnouncement
    {
        internal readonly string Id;
        internal readonly string TitleKey;
        internal readonly string BodyKey;

        internal RimExodusAnnouncement(string id, string titleKey, string bodyKey)
        {
            Id = id;
            TitleKey = titleKey;
            BodyKey = bodyKey;
        }
    }

    /// <summary>
    /// Append future announcements here with a new stable ID and Keyed title/body. Keep old entries
    /// so players can read them in settings. Receipts belong to ModSettings, not individual saves.
    /// </summary>
    internal static class RimExodusAnnouncements
    {
        internal const string RefTransitionId = "ref-transition-2026-10";

        internal static readonly IReadOnlyList<RimExodusAnnouncement> History = new[]
        {
            new RimExodusAnnouncement(RefTransitionId,
                "RimExodus_RefTransitionNoticeTitle", "RimExodus_RefTransitionNoticeBody")
        };

        private static bool showingUnread;
        private static bool checkedThisLaunch;

        internal static void ShowOnStartup()
        {
            if (checkedThisLaunch || RimExodusMod.Settings == null) return;
            var content = LoadedModManager.GetMod<RimExodusMod>()?.Content;
            if (content == null) return;
            var frameworkId = content.PackageIdPlayerFacing.EndsWith(".Dev", StringComparison.OrdinalIgnoreCase)
                ? "RimExodus.Framework.dev" : "RimExodus.Framework";
            if (!ModsConfig.IsActive(frameworkId)) return;
            checkedThisLaunch = true;
            ShowNextUnread();
        }

        internal static void ShowNextUnread()
        {
            if (showingUnread) return;
            var settings = RimExodusMod.Settings;
            if (settings == null) return;
            foreach (var announcement in History)
            {
                if (settings.acknowledgedAnnouncementIds.Contains(announcement.Id)) continue;
                showingUnread = true;
                System.Action acknowledge = () =>
                {
                    if (!settings.acknowledgedAnnouncementIds.Contains(announcement.Id))
                        settings.acknowledgedAnnouncementIds.Add(announcement.Id);
                    LoadedModManager.GetMod<RimExodusMod>()?.WriteSettings();
                    showingUnread = false;
                    ShowNextUnread();
                };
                Find.WindowStack.Add(new Dialog_MessageBox(
                    announcement.BodyKey.Translate(),
                    "RimExodus_AnnouncementsAcknowledge".Translate(), acknowledge,
                    title: announcement.TitleKey.Translate(), acceptAction: acknowledge));
                return;
            }
        }

        internal static void ShowAgain(RimExodusAnnouncement announcement)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                announcement.BodyKey.Translate(),
                "RimExodus_AnnouncementsClose".Translate(),
                title: announcement.TitleKey.Translate()));
        }

        internal static void RemindNextStartup(RimExodusAnnouncement announcement)
        {
            if (RimExodusMod.Settings.acknowledgedAnnouncementIds.Remove(announcement.Id))
                LoadedModManager.GetMod<RimExodusMod>()?.WriteSettings();
        }
    }

    internal sealed class AnnouncementHistoryDialog : Window
    {
        private Vector2 scroll;
        private float contentHeight = 400f;

        public override Vector2 InitialSize => new Vector2(700f, 620f);

        internal AnnouncementHistoryDialog()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f),
                "RimExodus_AnnouncementsHistoryTitle".Translate());
            Text.Font = GameFont.Small;

            var listRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f);
            var viewRect = new Rect(0f, 0f, listRect.width - 16f,
                Mathf.Max(contentHeight, listRect.height));
            Widgets.BeginScrollView(listRect, ref scroll, viewRect);
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(viewRect);
            foreach (var announcement in RimExodusAnnouncements.History)
            {
                Text.Font = GameFont.Medium;
                listing.Label(announcement.TitleKey.Translate());
                Text.Font = GameFont.Small;
                listing.Label(announcement.BodyKey.Translate());
                var buttons = listing.GetRect(32f);
                var half = (buttons.width - 8f) / 2f;
                if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, half, buttons.height),
                    "RimExodus_AnnouncementsShowAgain".Translate()))
                    RimExodusAnnouncements.ShowAgain(announcement);
                var reminder = new Rect(buttons.x + half + 8f, buttons.y, half, buttons.height);
                if (RimExodusMod.Settings.acknowledgedAnnouncementIds.Contains(announcement.Id))
                {
                    if (Widgets.ButtonText(reminder, "RimExodus_AnnouncementsRemindNextStartup".Translate()))
                        RimExodusAnnouncements.RemindNextStartup(announcement);
                }
                else Widgets.Label(reminder, "RimExodus_AnnouncementsRemindScheduled".Translate());
                listing.Gap(16f);
            }
            listing.End();
            contentHeight = listing.CurHeight + 8f;
            Widgets.EndScrollView();
        }
    }
}
