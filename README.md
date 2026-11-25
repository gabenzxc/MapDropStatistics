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

## Local map history (Path of Exile 1)

- With ExileApi and Map Drop Statistics enabled, use **Open Map History** in the plugin settings or open `http://127.0.0.1:17832/`. The **Dashboard** settings control the listener, port, and party pickup estimation.
- Atlas maps are tracked automatically using the regular and unique map areas from the 3.30 client tables `Maps` and `UniqueMaps`, including older map series. Entering an untracked side area pauses the run, and returning to the same instance resumes it.
- In **History → Tracked areas**, enter additional in-game area names, one per line. Names match without regard to case. Towns and hideouts remain excluded. Additional areas count towards session statistics and comparisons and have a default entry cost of zero. Removing a name keeps previously recorded runs.
- The dashboard shows drops and pickups together with a pickup progress bar, plus area modifiers, deaths, and the market value of picked-up loot in Divine Orbs. Divine/hour uses time spent inside the selected maps. **Net** deducts the recorded map and Thread costs.
- Map details show currency and unique totals, Normal/Magic/Rare gear bases, and gold collected by the local character. They also show detected hostile monster kills and a rarity breakdown. Kill totals are available for maps recorded with kill tracking. The **Basic** modifier view shows readable effects and bonuses, and **All** shows every captured stat.
- The map list shows time spent in each recorded map and can sort by total kills. New records save the monster kill totals by rarity and track player and party deaths. The kill counter requires an observed living monster and a later confirmed death. Known effects and encounter proxies are excluded.
- Clear **Include in statistics** in a map's details to remove that run from charts, totals, the widget, and session comparisons. The run remains in History. Use **Status → Excluded from statistics** to find it and include it again.
- Choose **Delete map** in the map details and confirm to remove the record from History, Favorites, and statistics. A live map can also be removed while you are still in it.
- Map details show picked-up value and the estimated value left on the ground. The remainder uses observed drops minus personal and estimated party pickups. Displayed prices use up to two decimal places, or three for small amounts. The calculations use the stored prices.
- Open **Left on ground** to see the uncollected quantities and recorded prices from finished runs in the selected session. Search by item name, filter by category, or sort by quantity and value. This view includes prices below the global price cutoff.
- Click a loot column heading to sort it. **Default order** restores the original order. The Currency and Uniques cards show quantities and Divine Orb valuations for both drops and pickups.
- Use **Uniques** to browse drops from the current map or the completed maps in the selected period. Filter by item type and rarity tier. Click a column heading to sort by quantity, tier, type, name, or unit price. Prices use recorded quotes and manual values, weighted by the number of priced copies in the selection. The totals follow the pickup filter and include older recorded drops.
- The bundled `uniqueDropTiers.json` contains item names, categories, and the latest unambiguous tier recorded in the wiki's 3.25–3.30 columns and notes. Ambiguous tiers and unlisted variants display **Unknown**. The reference is a snapshot from 16 November 2026. [PoE Wiki, revision 1706499](https://www.poewiki.net/index.php?title=Guide:Analysis_of_unique_item_tiers&oldid=1706499) · [CC BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/)
- The **Altars** tab records observed Eldritch altars and the chosen option when a click is followed by altar activation. Earlier records can still show duplication modifiers captured on the map. The **Encounter diagnostics** panel records area transitions and related client signals. A detected Delirium mirror, Delirium reward progress, or a confirmed map modifier adds a Delirium tag. Observing Varashta in the map or recording a wish selection adds a Mirage tag. The Viridian Wildwood entrance adds a Wildwood tag as soon as it is observed.
- Select a session to see its maps, hourly rate and drops. **New session** archives the current session. **Resume** makes the selected session active again. A map belongs to the session active when it was entered. Sessions survive Loader restarts. Item icons use Ninja Pricer metadata and the Path of Exile CDN.
- After leaving a map, **Record last map** includes the paused run in completed-map statistics immediately. Returning to the same instance continues the same record and updates its statistics when you leave again.
- Select a current or archived session and click **Rename** to change its label in History and Compare sessions.
- Click the star next to a map's name in its details to add or remove it. Select **Favorites** in the session menu to browse marked maps across sessions and view their loot and statistics. Favorite marks are saved with each map.
- Open **Compare sessions** to place two sessions side by side. **Session drops** compares total items or priced value. **Drops per map** compares averages for counters, map time, quantity, rarity, pack size, and recorded drop bonuses. A map with modifier data contributes zero for bonuses it lacks. The charts align both series by map number on one scale. **Unique drops** can filter to items found in just one session.
- **Unique drops by tier** compares total dropped copies per tier and can export the tier totals for both sessions as CSV. Each map's **Uniques** tab also shows its tier counts. Drops without enough information to identify a tier appear under **Unknown**.
- **Collection** totals selected currency pickups and T0 pickups across all runs included in statistics. **By tier** shows unique drops and personal pickups from T0 through T5, plus Unknown. **Map records** shows the best finished runs for personal Divine Orb pickups, monster kills, and unique drops.
- **Hideout time** continues while the game is in the background and is saved with the active session.
- The widget shows completed Legion monoliths, Abyss pits, and Breaches out of the encounters detected on the current map. Breach progress counts closed Unstable Breaches at either terminal state, plus successful Hive defenses. Mirage and Delirium show activation status when detected. Each row can be hidden in **Widget settings**.
- Own pickups are matched against inventory changes. Nearby party pickups are estimates from ground items disappearing.
- A finished Atlas map is automatically marked **Failed** when its unique drop count is at or below **Tracking → Fail unique threshold**. The default is 200, and the setting also applies to saved Atlas maps. An observed party wipe can mark any tracked area as failed. Use the **Failed** selector in the run details to override its automatic status.
- Pickup value shows personal and estimated party totals for the map and the selected session. The map detail shows deaths when the client has observed a personal or party death.
- Click **You** or **Party est.** to see pickups from that source in the session and the selected map. **All drops** restores the full view.
- Fresh market prices require the separate [Ninja Pricer (Get-Chaos-Value)](https://github.com/exApiTools/Get-Chaos-Value) plugin. Install and enable it in Loader, then use **Reload Prices** in Ninja Pricer to populate its `NinjaData/<league>` cache. Check its status in the Map Drop Statistics settings or in **Cost settings** in the browser. Drop tracking and saved prices remain available when the pricer is unavailable.
- Pickups without a matching market quote show as unpriced in the dashboard. Use **Edit** in the loot list to set a unit price or exclude the entry from the selected map. Clear the manual price to use the recorded market quote again.
- **Global ignore list** excludes matching item names from both valuations in every session. Add the exact name or choose **Ignore in all maps** in the item's valuation dialog. Ignored items remain visible in the drop counts.
- **Minimum price per item** supports Chaos Orbs and Divine Orbs. Quotes at the minimum are included. Chaos minima use the map's recorded or backfilled league exchange rate. Clear the field and save to disable the minimum.
- New maps share one saved market snapshot per league and three-hour UTC window. The plugin checks for a new window while a character is in game, including in the hideout, and saves the first available local prices. The snapshot fixes the loot quotes, Chaos/Divine rate, and Thread prices for maps started in that window. Unavailable quotes remain unpriced.
- New snapshots are stored under `Config/MapDropStatistics/MarketPrices/<league>/YYYY-MM-DD-HH-3h.json.gz`. Older 12-hour snapshots keep their original filenames and remain available to the maps recorded with them. **Cost settings** shows the full archive path for this installation.
- The default purchase price is saved when the map starts. Use the map's **Costs** tab to edit its purchase price or restore its saved default. Older maps retain their recorded loot prices. For old Thread costs with available quotes, the **Costs** tab shows a one-time estimate and its snapshot date.
- The optional unique pricing switch uses conservative base prices from Ninja Pricer. Per-item manual prices and exclusions apply to both the pickup and ground estimates.
- Records and edits are saved in `config/MapDropStatistics/MapHistory` without automatic pruning. **Max saved map stats** controls the separate local summaries used by the in-game viewer and supports up to 5,000 files. The archive imports existing saved map summaries on startup.
