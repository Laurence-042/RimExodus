[h1]RimExodus — Seamless World[/h1]

Say goodbye to the disjointed "exit map → world map → reload" travel loop. RimExodus stitches adjacent world tiles' maps together, visually and functionally: colonists walk out of the base gate into the wilderness and on across invisible world-tile borders — no caravan, no loading screen.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/demo.gif[/img]

[b]⚠ Before uninstalling, run "Restore vanilla compatibility" in settings and save; the first mod-free load shows harmless one-time red errors, gone after one more save. But map-generation-related limits remain — see the "About uninstalling" post.[/b]

[h1]What It Does[/h1]

Every tile map is carved into a hexagon matching its world tile shape (pentagons too); adjacent tiles align through a "seam band": terrain, rock, and roofs blend across the seam, rivers and roads connect through. At a seam the neighbor's live terrain is already rendered beyond your border — colonists just walk across. Simply put: open-world RimWorld time!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/map-grid.png[/img]
Hexagonal tile carving and seam alignment

[list]
[*][b]Seamless cross-map walking[/b]: order a colonist toward a seam to generate the adjacent map, order them across the seam and they switch to the adjacent map with barely a hitch, or order them to stand on the seam to form a vanilla caravan.

[*][b]Cross-map combat[/b]: line of sight, targeting, and projectile handoff work across seams — bullets hit the right target on the right map. Chase enemies across maps; they can chase you back. Disable in settings to restore vanilla combat.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/shoot.png[/img]
Shooting across a seam

[*][b]Raids that march in from neighboring maps[/b]: walking attackers can enter at an unseen outer seam of an adjacent map, then cross the world-tile border to advance on and attack you, instead of materializing between two maps that are already loaded.

[*][b]Weapon range curve remap (off by default)[/b]: optionally stretches every ranged weapon's range along a piecewise curve, moving vanilla accuracy-band distances with it — each weapon keeps roughly its old hit performance at its new maximum range. Why it exists: even if you never roam far, you can keep several adjacent maps around your home (with their simulation throttled way down) and, combined with walking enemies arriving from the outer edge, get a far deeper defensive buffer than vanilla — and on a battlefield that open, vanilla's abstract weapon ranges (a rifle tops out around forty cells) start to feel jarringly out of place. When enabled, long-range weapons gain engagement distances that fit their role; the default curve deliberately keeps short-range weapons modest while spreading long-range ones further apart.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/range-curve.png[/img]
The range curve editor

[*][b]Incremental frame-sliced generation[/b]: neighbor tile maps generate as colonists are ordered toward the border, usually without pausing the game or a loading screen (saving blocked while generating — takes seconds).

[*][b]Rolling map dormancy[/b]: far maps go dormant (no ticking; contents preserved, reawakened anytime); farther maps are deleted and regenerated on revisit. If a map has a home area above the threshold size, buildings and stockpiles inside it are archived and rebuilt when the map regenerates. Note: regeneration is a fresh map — archiving only guarantees that buildings and stockpiles within the home area are restored. The home map is never auto-dormant or deleted. Dormancy/deletion distances and the archiving threshold are all adjustable in settings.

[*][b]Native content integration[/b]: world-map POIs — faction settlements, ancient mechanitor compounds, ambushes, opportunity sites — generate as seamless tiles via the vanilla pipeline as you approach, naturally compatible with other mods' structures; camps too.

[*][b]Ocean exploration[/b]: vanilla has no enterable ocean maps, so this mod supplies deep-ocean terrain and basic weather built in — sail out through a coastal seam to explore and cross the open ocean. If another mod provides its own ocean maps, the built-in support can be disabled in settings (takes effect after restart, only affects newly generated maps).

[*][b]Settlement traders[/b]: friendly settlements designate a "trader" from their garrison (question mark overhead); right-click to talk and trade — buy from settlement stock, sell your carried items, pay with carried silver. Full caravan trading semantics, minus the caravan. The dialog gathers every settlement interaction, including ones from other mods (e.g. Oberonia Aurea's diplomacy).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/trade.png[/img]
Trading with a settlement trader

[*][b]Live tile status markers[/b]: tiles are tinted along their real shape — active (orange with pawns / blue without), dormant (grey), archived (teal). Select a tile to manually dorm or delete it.
[/list]

[h1]Mod Compatibility[/h1]

In theory, anything that doesn't modify base maps (the per-tile maps on the planet view) works.

[b]cross-map combat is adapted for Combat Extended[/b]: ordinary firearms, grenades, mortars, instant rays, guided projectiles, CIWS, suppression fire, and ability-launched projectiles can all engage targets on adjacent maps, while pawns and automatic/manual turrets acquire cross-map targets; range, line of sight, cover, smoke, lighting, dispersion, and ballistics still follow CE rules on the actual maps. Same-map combat goes entirely through CE's native logic, and CE world shelling keeps its original flow.

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

Special thanks to [url=https://github.com/Nanaloveyuki]Nanaloveyuki[/url] for their help and contributions to this project.

[h1]Links[/h1]

Source and full documentation: [url=https://github.com/Laurence-042/RimExodus]GitHub[/url]

Found a problem? Report it in the comments or on GitHub Issues — a log and repro steps help.
