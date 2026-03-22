# MapDropStatistics

Plugin for ExileApi that tracks drops and session averages for map runs.

Works with `https://github.com/exApiTools/ExileApi-Compiled`.

## Settings

`Max Entities` setting in ExileAPi.Core should be on 40k

## What It Does

- Tracks loot for the current map area.
- Separates map time and non-map time.
- Counts uniques, T0 uniques, currency, fragments, base item rarities, Divine Orbs, and Valdo's Puzzle Boxes.
- Supports custom tracked currency items from settings.
- Saves per-area snapshots and a last-session snapshot.
- Shows an in-game overlay and a saved-stat viewer in settings.
