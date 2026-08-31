[h1]RimExodus — Seamless World[/h1]

Say goodbye to the disjointed "exit map → world map → reload" travel loop. RimExodus stitches adjacent world tiles' maps together, visually and functionally: colonists walk out of the base gate into the wilderness and on across invisible world-tile borders — no caravan, no loading screen.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/demo.gif[/img]

[b]⚠ In development; deep map-system changes — the save format is now basically stable, and problem reports are welcome. Before uninstalling, run "Restore vanilla compatibility" in settings and save; the first mod-free load shows harmless one-time red errors, gone after one more save. But map-generation-related limits remain — see the "About uninstalling" post.[/b]

[h1]What It Does[/h1]

Every tile map is carved into a hexagon matching its world tile shape (pentagons too); adjacent tiles align through a "seam band": terrain, rock, and roofs blend across the seam, rivers and roads connect through. At a seam the neighbor's live terrain is already rendered beyond your border — colonists just walk across. Simply put: open-world RimWorld time!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/map-grid.png[/img]
Hexagonal tile carving and seam alignment

[list]
[*][b]Seamless cross-map walking[/b]: order a colonist across a seam and they switch to the adjacent map with barely a hitch.

[*][b]Cross-map combat[/b]: line of sight, targeting, and projectile handoff work across seams — bullets hit the right target on the right map. Chase enemies across maps; they can chase you back. Disable in settings to restore vanilla combat.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/shoot.png[/img]
Shooting across a seam

[*][b]Incremental frame-sliced generation[/b]: neighbor tile maps generate as colonists are ordered toward the border, usually without pausing the game or a loading screen (saving blocked while generating — takes seconds).

[*][b]Rolling map dormancy[/b]: far maps go dormant (no ticking; contents preserved, reawakened anytime); farther maps are deleted and regenerated on revisit — freshly, so left-behind items, buildings, and terrain changes are lost. Home is never dormant or deleted. Both distances are adjustable.

[*][b]Native content integration[/b]: world-map POIs — faction settlements, ancient mechanitor compounds, ambushes, opportunity sites — generate as seamless tiles via the vanilla pipeline as you approach, naturally compatible with other mods' structures; camps too.

[*][b]Settlement traders[/b]: friendly settlements designate a "trader" from their garrison (question mark overhead); right-click to talk and trade — buy from settlement stock, sell your carried items, pay with carried silver. Full caravan trading semantics, minus the caravan. The dialog gathers every settlement interaction, including ones from other mods (e.g. Oberonia Aurea's diplomacy).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/trade.png[/img]
Trading with a settlement trader

[*][b]Three-state world map icons[/b]: tile icons show active/dormant state live; select a tile to manually dorm or delete it.
[/list]

[h1]Mod Compatibility[/h1]

In theory, anything that doesn't modify base maps (the per-tile maps on the planet view) works.

[b]Adapted:[/b]
[list]
[*][b]Geological Landforms[/b] — fully adapted (landforms generate correctly on tile maps, preview included).

[*][b]MapPreview[/b] — previews faithful to the real terrain generation.

[*][b]Vehicle Framework[/b] — vehicles cross seams: drive up to a transfer spot; cross-map orders work. Large multi-tile vehicles get full-vehicle arrival validation (honestly refused if the far side has no room), and count as pawns for dormancy.

[*][b]Vehicle Map Framework[/b] — a vehicle carrying colonists on its map crosses normally.

[*][b]Perspective Shift[/b] — first-person WASD movement crosses maps fine; cross-map shooting works too.
[/list]

[b]Expected to be incompatible:[/b]
[list]
[*][b]RimSkyBlock (Edge of War sky islands)[/b] — its sky-island conversion breaks this mod's map setup, leaving those maps non-seamless, and its movement logic is uninvestigated and may cause extra issues. Not planned, given the tone clash.

[*][b]CE[/b] — Character Editor is of course compatible. As for Combat Extended… cross-seam shooting isn't supported yet; single-map combat works. Vanilla comes first; cross-seam combat and related mod adaptation later.

[*][b]As above, So below 2[/b] — its map assumptions conflict outright: it must resize and re-split maps, and RimExodus can't keep tile borders smooth under that. The veteran multi-floor mod MultiFloors is adapted, though.

[*][b]Mods deeply modifying map generation, borders, or world tiles[/b] — untested. On conflicts, turn off "incremental generation" so tile maps use the vanilla synchronous pipeline (other mods' patches then apply, at the cost of a brief freeze).
[/list]

[h1]Known Limitations[/h1]

[list]
[*]If weather is force-modified and the source map is deleted before the source clears (e.g. its game condition hasn't ended), weather can never return to normal — the restoration path died with it.

[*]Designation-first commands (mining, chopping, etc.) don't appear in cross-map right-click menus (fine once you've crossed the seam).

[*]No cross-map hauling or cross-map work — this mod makes Odyssey more Odyssey, not one giant megabase.

[*]Odyssey space-layer maps and pocket maps (underground vaults, pits, VMF vehicle interiors, etc.) stay outside the seamless system, keeping vanilla behavior.

[*]Settlement trading only supports carried items (prisoners/slaves not yet); the trader is designated once at generation, never re-picked.

[*]Saving is blocked while an adjacent map generates (progress shows in the top-left corner).

[*]Neighbor rendering is approximate (no shadows/ripples); neighbors in another weather cluster follow the current map's sky.

[*]Connections around the planet's 12 pentagon tiles look slightly off, but movement is fine.

[*]Edge-spawned raids spawn near the seam, loaded neighbor or not. May improve later (spawning from the outskirts of loaded maps) — undecided, pending research into conflicting mods.
[/list]

[h1]Performance[/h1]

Near-zero overhead when no cross-map action or generation is happening — and while too many maps is itself a RimWorld problem, this mod improves on it. Beyond dormancy and far-map deletion, you can configure the **tick rate of pawnless non-home maps**, plus a **seam fast zone (N cells around each seam, ignored by the rate)** to keep cross-map combat intact.

With both set to 0, an extreme stress test of 14 active grassland/rainforest maps still measured 360 TPS on a 5-year-old laptop (a first-gen OMEN 16).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/performance.png[/img]

See the GitHub README for details.

[h1]Settings[/h1]

Every entry has a tooltip — hover to see it, not repeated here.

[h1]License & Credits[/h1]

Open source under the MIT License; code on GitHub.

Special thanks to [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3426502333]Vehicle Map Framework[/url] — it proved that simultaneously rendering multiple interactive maps is possible, and this project's research started there. No code was shared with it; only ideas were referenced.

[h1]Links[/h1]

Source and full documentation: [url=https://github.com/Laurence-042/RimExodus]GitHub[/url]

Found a problem? Report it in the comments or on GitHub Issues — a log and repro steps help.
