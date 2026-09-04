[h1]RimExodus — Seamless World[/h1]

Say goodbye to the disjointed "exit map → world map → reload" travel loop. RimExodus stitches adjacent world tiles' maps together, visually and functionally: colonists walk out of the base gate into the wilderness and on across invisible world-tile borders — no caravan, no loading screen.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/demo.gif[/img]

[b]⚠ In development; deep map-system changes — bugs may still pop up in odd places. The save format is now basically stable, and problem reports are welcome. Before uninstalling, run "Restore vanilla compatibility" in settings and save; the first mod-free load shows harmless one-time red errors, gone after one more save. But map-generation-related limits remain — see the "About uninstalling" post.[/b]

[h1]What It Does[/h1]

Every tile map is carved into a hexagon matching its world tile shape (pentagons too); adjacent tiles align through a "seam band": terrain, rock, and roofs blend across the seam, rivers and roads connect through. At a seam the neighbor's live terrain is already rendered beyond your border — colonists just walk across. Simply put: open-world RimWorld time!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/map-grid.png[/img]
Hexagonal tile carving and seam alignment

[list]
[*][b]Seamless cross-map walking[/b]: order a colonist toward a seam to generate the adjacent map, order them across the seam and they switch to the adjacent map with barely a hitch, or order them to stand on the seam to form a vanilla caravan.

[*][b]Cross-map combat[/b]: line of sight, targeting, and projectile handoff work across seams — bullets hit the right target on the right map. Chase enemies across maps; they can chase you back. Disable in settings to restore vanilla combat.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/shoot.png[/img]
Shooting across a seam

[*][b]Incremental frame-sliced generation[/b]: neighbor tile maps generate as colonists are ordered toward the border, usually without pausing the game or a loading screen (saving blocked while generating — takes seconds).

[*][b]Rolling map dormancy[/b]: far maps go dormant (no ticking; contents preserved, reawakened anytime); farther maps are deleted and regenerated on revisit. If a map has a home area above the threshold size, buildings and stockpiles inside it are archived and rebuilt when the map regenerates. Note: regeneration is a fresh map — archiving only guarantees that buildings and stockpiles within the home area are restored. The home map is never auto-dormant or deleted. Dormancy/deletion distances and the archiving threshold are all adjustable in settings.

[*][b]Native content integration[/b]: world-map POIs — faction settlements, ancient mechanitor compounds, ambushes, opportunity sites — generate as seamless tiles via the vanilla pipeline as you approach, naturally compatible with other mods' structures; camps too.

[*][b]Settlement traders[/b]: friendly settlements designate a "trader" from their garrison (question mark overhead); right-click to talk and trade — buy from settlement stock, sell your carried items, pay with carried silver. Full caravan trading semantics, minus the caravan. The dialog gathers every settlement interaction, including ones from other mods (e.g. Oberonia Aurea's diplomacy).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/trade.png[/img]
Trading with a settlement trader

[*][b]Three-state world map icons[/b]: tile icons show active/dormant state live; select a tile to manually dorm or delete it.
[/list]

[h1]Mod Compatibility[/h1]

In theory, anything that doesn't modify base maps (the per-tile maps on the planet view) works.

For details, see the "Compatibility Details" post in the discussions.

[h1]Known Limitations[/h1]

For details, see the "Known Limitations" post in the discussions.

[h1]Performance[/h1]

Near-zero overhead when no cross-map action or generation is happening — and while too many maps is itself a RimWorld problem, this mod improves on it. Beyond dormancy and far-map deletion, you can configure the **tick rate of pawnless non-home maps**, plus a **seam fast zone (N cells around each seam, ignored by the rate)** to keep cross-map combat intact.

With both set to 0, an extreme stress test of 14 active grassland/rainforest maps still measured 360 TPS on a 5-year-old laptop (a first-gen OMEN 16).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/performance.png[/img]

For details, see the "Performance Notes" post in the discussions.

[h1]Settings[/h1]

Every entry has a tooltip — hover to see it, not repeated here.

[h1]License & Credits[/h1]

Open source under the MIT License; code on GitHub.

Special thanks to [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3426502333]Vehicle Map Framework[/url] — it proved that simultaneously rendering multiple interactive maps is possible, and this project's research started there. No code was shared with it; only ideas were referenced.

[h1]Links[/h1]

Source and full documentation: [url=https://github.com/Laurence-042/RimExodus]GitHub[/url]

Found a problem? Report it in the comments or on GitHub Issues — a log and repro steps help.
