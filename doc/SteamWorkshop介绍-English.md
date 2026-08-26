[h1]RimExodus — Seamless World[/h1]

Say goodbye to the disjointed "exit map as caravan → world map → reload" travel loop. RimExodus stitches the local maps of adjacent world tiles together, visually and functionally: your colonists can walk straight out of the base gate into the wilderness and keep going across invisible world-tile borders — no caravan, no loading screen.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/demo.gif[/img]

[color=#7ee787][b]⚠ This mod is still in development and makes deep changes to the map system — expect bugs in odd places. The save format is essentially settled, so you can play without worry. Due to the depth of the changes, it can't be removed from an existing save; an unload utility may be added later.[/b][/color]

[h1]What It Does[/h1]

Every tile map is carved into a hexagon matching its world tile shape (pentagons too), and adjacent tiles align through a "seam band": terrain, rock, and roofs blend across the seam; rivers and roads connect through. Approach a seam and the neighbor tile's live terrain is already rendered beyond your border — your colonists can simply walk across. Simply put: open-world RimWorldin' time!

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/map-grid.png[/img]
Hexagonal tile carving and seam alignment

[list]
[*][b]Seamless cross-map walking[/b]: order a colonist across a map seam and they switch to the adjacent map with barely a hitch.

[*][b]Cross-map combat[/b]: line of sight, target acquisition, and projectile handoff work across seams — bullets fly over the seam and hit the right target on the right map. Chase fleeing enemies across maps, but they can chase you back. Can be disabled to restore vanilla combat semantics.

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/shoot.png[/img]
Shooting across a seam

[*][b]Incremental frame-sliced generation[/b]: neighbor tile maps generate as colonists are ordered toward the border, usually without pausing the game or a loading screen (saving is blocked while generating, but that only takes seconds).

[*][b]Rolling map dormancy[/b]: far maps go dormant (no ticking; contents fully preserved, reawakened at any time), and even farther maps are deleted and regenerated on revisit. Note: regeneration is fresh — items left behind, buildings, and terrain changes are lost. The home map is never dormant or deleted. Both distances are adjustable.

[*][b]Native content integration[/b]: world-map POIs — faction settlements, ancient mechanitor compounds, ambushes, opportunity sites — generate as seamless tiles through the vanilla pipeline as you approach, staying compatible with other mods' structures. "Set up camp" maps are seamless too.

[*][b]Settlement traders[/b]: friendly settlements designate a "trader" from their garrison (question mark overhead); right-click to talk and trade — buy from the settlement's stock, sell what you carry, pay with silver on your body. Full caravan trading semantics, minus the caravan. The dialog also gathers every interaction the settlement offers, including ones from other mods (e.g. Oberonia Aurea's diplomacy).

[img]https://raw.githubusercontent.com/Laurence-042/RimExodus/main/doc/img/trade.png[/img]
Trading with a settlement trader

[*][b]Three-state world map icons[/b]: tile icons show active and dormant states in real time; select a tile to manually dormify or delete it.
[/list]

[h1]Mod Compatibility[/h1]

[b]Adapted:[/b]
[list]
[*][b]Geological Landforms[/b] — fully adapted (landforms generate correctly on tile maps, previews included).

[*][b]MapPreview[/b] — previews show terrain generation faithful to the real map.

[*][b]Vehicle Framework[/b] — vehicles cross seams: drive up to a transfer spot to cross, and cross-map orders work. Large multi-tile vehicles get full-vehicle arrival validation (honestly refused if the far side has no room). Vehicles count as pawns for rolling dormancy.

[*][b]Vehicle Map Framework[/b] — a vehicle carrying colonists on its map can cross between maps normally.

[*][b]Perspective Shift[/b] — first-person WASD movement crosses maps fine, and cross-map shooting works too.
[/list]

[b]Expected to be incompatible:[/b]
[list]
[*][b]RimSkyBlock (Edge of War sky islands)[/b] — their sky-island conversion breaks this mod's map setup, leaving sky-island maps non-seamless. Support is not planned, given the gameplay tone clash.

[*][b]Mods deeply modifying map generation, map borders, or world tiles[/b] — not systematically tested. If you hit conflicts, turn off "incremental generation" so tile maps use the vanilla synchronous pipeline instead (other mods' patches then apply, at the cost of a brief freeze).
[/list]

[h1]Known Limitations[/h1]

[list]
[*]If weather is force-modified and the source map is deleted before the source is dealt with (e.g. its game condition hasn't ended), the weather can never return to normal — the restoration path was deleted along with the source.

[*]Commands requiring a designation first (mining, chopping, etc.) don't appear in cross-map right-click menus (everything works once you've walked over the seam).

[*]Odyssey space-layer maps and pocket maps (underground vaults, pits, VMF vehicle interiors, etc.) stay outside the seamless system and keep vanilla behavior.

[*]Explosion AoE and mortar-style projectiles don't cross seams (they resolve on the map they land on).

[*]Settlement trading only supports items on your body (prisoners/slaves not yet); the trader is designated once at generation and not re-picked.

[*]Saving is blocked while an adjacent map generates (progress shows in the top-left corner).

[*]Neighbor map rendering is approximate (no shadows/water ripples); neighbors in a different weather cluster follow the current map's sky.

[*]Map connections around the planet's 12 pentagon tiles look slightly off, but movement is unaffected.
[/list]

[h1]Performance[/h1]

Hot paths early-out when no cross-map interaction is happening — near-zero overhead otherwise. The main costs are neighbor map rendering and a TPS dip while adjacent maps generate. Too many maps is itself a RimWorld performance problem; this mod adds no standing cost and mitigates it via dormancy and auto-deletion, but it can't conjure TPS out of thin air. See the GitHub README for a full breakdown.

[h1]Settings[/h1]

Every entry has a tooltip — just hover over an entry to see its explanation, so they aren't repeated here.

[h1]License & Credits[/h1]

Open-sourced under the MIT License; source code on GitHub.

Special thanks to [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3426502333]Vehicle Map Framework[/url] — it proved cross-map modding was possible and started this project's research. The architectures diverged too far to reuse its code (VMF centers on pocket maps and vehicle crossings; RimExodus needed peer tiles in one continuous world), so RimExodus was built from scratch — but without it, RimExodus wouldn't exist.

[h1]Links & Feedback[/h1]

Source code and full documentation: [url=https://github.com/Laurence-042/RimExodus]GitHub[/url]

Found a problem? Please report it in the comments or on GitHub Issues — attaching your log and reproduction steps helps a lot.
