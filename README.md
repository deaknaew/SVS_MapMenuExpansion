# SVS_MapMenuExpansion

A BepInEx 6 (IL2CPP) plugin for SamabakeScramble that adds the mod maps to the Map screen.

Map packs loaded by SVS_MapLoader add maps to the game, but the game's Map screen only has its seven fixed picture tiles, so the new maps could not be picked from it. This plugin adds a **Mod Maps** button to that screen. It opens a scrollable map of every map the game knows, with a picture, a name and link lines between connected maps, and clicking a tile moves you there like the normal tiles do.

## Features

- **Mod Maps / Original Map buttons**, styled like the option menu buttons. The Map screen itself is untouched; the normal tiles are still there when you switch back.
- **Tile pictures** taken from each map's own 2D background. Maps with no 2D background (the 3D ones) show an empty tile.
- **Link lines** between maps the game lists as neighbours, drawn like the game's own routes (cream lines with rounded corners). Lines between a parent and its child map are bright, other links are thinner and tinted cyan. Hover a tile to light up all of its links.
- **Compact layout**: connected maps sit close together in a tree-like grid, the game's own maps are included so mod maps link to them (for example Asagaya Station next to its neighbour map).
- **Scrolling** with the mouse wheel, by dragging the map, or with a scroll bar copied from the option menu.
- **You are here** marker (the green runner icon) on your current map, and the map screen opens on the grid, scrolled to your map, when you are standing in a mod map.
- Maps that share a name get the linked map's name added, for example `Your Room (Riverside)` and `Your Room (School gates)`.
- **Private spaces are left out** (Your Room, toilets, machine room, the bushes, the tent): they are not travel destinations.
- **Cleaner links**: a denser layout, lines that share a gap are ordered so they do not cut through each other, and long lines snap straight onto their target tile.

## Requirements

- SamabakeScramble with BepInEx 6 (IL2CPP)
- SVS_MapLoader (or any plugin that adds maps to the game's map list)

## Installation

Copy `BepInEx/plugins/SVS_MapMenuExpansion.dll` from the release zip to `<GameRoot>/BepInEx/plugins/`.

## Settings

Saved in `BepInEx/config/SVS_MapMenuExpansion.cfg`.

| Setting | Default | Effect |
|---------|---------|--------|
| First Custom Map ID | 100 | Maps with an ID at or above this value are treated as mod maps and get a tile |
| Include Original Maps | on | Also list the game's own maps so mod maps link to them |
| Draw Map Links | on | Draw the link lines |
| Thumbnail Time Index | 1 | Which of a map's four 2D backgrounds (00-03, one per time of day) is used for the picture |

## Files it creates

In `UserData/plugins/MapMenuExpansion/`:

| File | Purpose |
|------|---------|
| `thumbs/<map id>.png` | Small pictures made from the maps' backgrounds the first time they are needed. Delete the folder to make them again, for example after a map pack changes its images |
| `layout.txt` | The map layout, searched once and reused. It is rebuilt automatically when the list of maps or links changes |

The map list is read when the game starts, so restart the game after adding or removing a map pack.

## Building

```powershell
dotnet build src\SVS_MapMenuExpansion.csproj -c Release "-p:GameRoot=<path to the game folder>"
```

The DLL is written to `BepinEx/plugins/`.
