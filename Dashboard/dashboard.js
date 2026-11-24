"use strict";

const $ = id => document.getElementById(id);
const number = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 3 });
const priceNumber = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 2 });
const smallPriceNumber = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 3 });
const date = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
const signalTime = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const killRarities = [["Normal", "normal"], ["Magic", "magic"], ["Rare", "rare"], ["Unique", "unique"]];
const stateLabels = {
  active: "In progress", paused: "Out of map", recorded: "Recorded",
  interrupted: "Interrupted", legacy: "Legacy record"
};
const averageItemsKey = "map-drop-statistics.average-items";
const averageOrderKey = "map-drop-statistics.average-order";
const defaultAverageItems = ["Divine Orb", "Valdo's Puzzle Box"];
const profitModeKey = "map-drop-statistics.profit-mode";
const state = { maps: [], filtered: [], statisticsMaps: [], selected: null, detail: null, tab: "loot", modMode: "basic", metric: "profit", visible: 25,
  editing: null, session: null, sessions: null, sessionView: "active", sessionExpanded: false, sessionAmount: "count",
  ignored: new Set(), lootSort: null, pickupScope: "all", averageItems: loadAverageItems(), averageOrder: loadAverageOrder(),
  profitMode: loadProfitMode(), threadCostsByMap: new WeakMap(), costMapId: null, widget: null };
const sessionPreviewSize = 9;
const sessionHighlights = new Set(["Divine Orb", "Valdo's Puzzle Box", "Foulborn Exalted Orb", "Voidborn Reliquary Key", "Exalted Orb", "Chaos Orb"]);
const uniqueSortOrders = {
  most: ["count", "descending"], fewest: ["count", "ascending"],
  "price-high": ["unitDivine", "descending"], "price-low": ["unitDivine", "ascending"],
  tier: ["tier", "ascending"], "tier-reverse": ["tier", "descending"],
  type: ["type", "ascending"], "type-reverse": ["type", "descending"],
  name: ["name", "ascending"], "name-reverse": ["name", "descending"]
};
const uniqueTierLabels = ["T0", "T1", "T2", "T3", "T4", "T5", "Unknown"];
let latestRequest = 0;
let hoverIndex = -1;
let chartPoints = [];
let chartAnimation = 0;
let dropAverageKey = "";
let latestAverageRequest = 0;
let averageDrag = null;
let averageDropTarget = null;
let pendingAverageRender = null;
let uniqueSelection = null;

function element(tag, label, className) {
  const node = document.createElement(tag);
  if (label != null) node.textContent = label;
  if (className) node.className = className;
  return node;
}

function formatDivineNumber(value) {
  const magnitude = Math.abs(value);
  if (magnitude > 0 && magnitude < 0.001) return "<0.001";
  return (magnitude < 0.01 ? smallPriceNumber : priceNumber).format(value);
}

const divines = value => value == null ? "—" : `${formatDivineNumber(value)} div`;
const duration = seconds => seconds == null ? "—" : seconds >= 3600
  ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`
  : `${Math.floor(seconds / 60)}m ${Math.floor(seconds % 60)}s`;
const pickupValue = (map, scope = state.pickupScope) => scope === "personal" ? map.personalPickupDivine :
  scope === "party" ? map.partyPickupDivine : map.profitDivine;
const defaultMapPrice = () => state.sessions?.mapCosts?.mapPriceDivine ?? 1;
const recordedDefaultMapPrice = map => map.pricing?.defaultMapDivine ?? 1;
const mapPrice = map => map.mapPriceDivine ?? recordedDefaultMapPrice(map);
const mapProfit = (map, mode = state.profitMode, scope = state.pickupScope) => {
  const cost = mode === "net" ? mapExpense(map) : 0;
  const value = pickupValue(map, scope);
  return cost == null || value == null ? null : value - cost;
};
const unpricedPickups = map => state.pickupScope === "personal" ? map.unpricedPersonal :
  state.pickupScope === "party" ? map.unpricedParty : map.unpricedQuantity;
const hourly = map => {
  const profit = mapProfit(map);
  return map.seconds > 0 && profit != null ? profit * 3600 / map.seconds : null;
};
const hasTracking = map => map.state !== "legacy";
const includedInStatistics = map => !map.excludedFromStatistics;
const statusLabel = map => map.failed ? "Failed" : stateLabels[map.state] ?? "Recorded";
const dateLabel = value => value && new Date(value).getFullYear() > 1 ? date.format(new Date(value)) : "Unknown date";
const set = (id, value) => { if ($(id).textContent !== value) $(id).textContent = value; };
const itemKey = name => name.toLowerCase();
const isIgnored = name => state.ignored.has(itemKey(name));
const pickupQuantity = item => state.pickupScope === "personal" ? item.collected :
  state.pickupScope === "party" ? item.estimated : item.collected + item.estimated;

function loadProfitMode() {
  try { return localStorage.getItem(profitModeKey) === "net" ? "net" : "gross"; }
  catch { return "gross"; }
}

function threadExpenses(map) {
  const cached = state.threadCostsByMap.get(map);
  if (cached) return cached;
  const quotes = map.pricing?.threadUnitDivine ?? {};
  const usage = map.threadUsage;
  const lines = (usage?.items ?? []).map(({ name, count }) => ({ name, units: count, unit: quotes[name] ?? null }));
  const typedCount = lines.reduce((sum, line) => sum + line.units, 0);
  const countMismatch = usage != null && typedCount !== usage.total;
  const knownTotal = lines.reduce((sum, line) => sum + line.units * (line.unit ?? 0), 0);
  const total = countMismatch || lines.some(line => line.unit == null) ? null : knownTotal;
  const expenses = { lines, knownTotal, total, countMismatch, typedCount };
  state.threadCostsByMap.set(map, expenses);
  return expenses;
}

function mapExpense(map) {
  const threads = threadExpenses(map).total;
  return threads == null ? null : mapPrice(map) + threads;
}

function setMetricDivines(id, value) {
  const metric = $(id);
  if (value == null) {
    set(id, "—");
    metric.style.removeProperty("--price-width");
    return;
  }

  const amount = formatDivineNumber(value);
  if (metric.textContent === `${amount} div`) return;
  const digits = amount.replace(/[^0-9]/g, "").length;
  metric.style.setProperty("--price-width", 0.64 * digits + 0.45 * (amount.length - digits) + 1.4);
  metric.replaceChildren(`${amount} `, element("span", "div", "unit"));
}

function renderPickupBreakdown(id, personal, party) {
  $(id).hidden = personal == null || party == null;
  set(`${id}-you`, divines(personal));
  set(`${id}-party`, divines(party));
  for (const button of $(id).querySelectorAll("[data-pickup-scope]")) {
    button.setAttribute("aria-pressed", String(button.dataset.pickupScope === state.pickupScope));
  }
}

function itemPricing(run, item, minimumDivine) {
  const edit = run.edits[item.id];
  if (isIgnored(item.name)) return { unit: null, reason: "Ignored globally", excluded: true };
  if (edit?.excluded) return { unit: null, reason: "Excluded", excluded: true };
  if (item.category === "Unique" && !state.sessions?.includeUniquePrices && edit?.unitDivine == null)
    return { unit: null, reason: "Disabled" };
  const unit = edit?.unitDivine ?? item.marketUnitDivine;
  if (unit == null) return { unit: null, reason: "No price" };
  if (minimumDivine != null && unit < minimumDivine) return { unit: null, reason: "Price cutoff", excluded: true };
  return { unit };
}

async function request(path, body) {
  const response = await fetch(path, body === undefined ? {} : {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(String(response.status));
  return response.status === 204 ? null : response.json();
}

async function refresh(force = false) {
  const requestId = ++latestRequest;
  if ($("cost-settings-dialog").open) refreshPricingStatus();
  try {
    const [maps, sessions, widget] = await Promise.all([request("/api/maps"), request("/api/sessions"),
      request("/api/widget").catch(() => null)]);
    if (requestId !== latestRequest) return;
    if (!["active", "all", "favorites"].includes(state.sessionView) &&
        !sessions.sessions.some(session => session.id === state.sessionView)) state.sessionView = "active";
    const selection = state.sessionView === "active" ? sessions.activeId : state.sessionView;
    const session = await request(`/api/session?session=${encodeURIComponent(selection)}&pickup=${state.pickupScope}`).catch(() => null);
    if (requestId !== latestRequest) return;
    const previous = state.maps.find(map => map.id === state.selected);
    const cutoffChanged = sessions.minimumPrice?.value !== state.sessions?.minimumPrice?.value ||
      sessions.minimumPrice?.currency !== state.sessions?.minimumPrice?.currency;
    const pricesChanged = sessions.includeUniquePrices !== state.sessions?.includeUniquePrices || cutoffChanged ||
      JSON.stringify(sessions.ignoredItems) !== JSON.stringify(state.sessions?.ignoredItems);
    const changed = JSON.stringify(maps) !== JSON.stringify(state.maps) || sessions.activeId !== state.sessions?.activeId ||
      pricesChanged;
    state.maps = maps;
    state.sessions = sessions;
    state.widget = widget;
    state.ignored = new Set(sessions.ignoredItems.map(itemKey));
    renderSessionControls();
    renderWidgetStatus();
    document.dispatchEvent(new Event("map-history-updated"));
    if (pricesChanged) renderIgnoreList(cutoffChanged);
    set("connection", "Connected locally");
    $("connection").classList.remove("error");
    if (changed || force) {
      updateLeagueFilter();
      renderOverview();
    } else if (!dropAverageKey) {
      refreshDropAverages();
    }
    if (force || JSON.stringify(session) !== JSON.stringify(state.session) || !session) {
      state.session = session;
      renderSession();
      updateAverageItemSuggestions();
    }
    const current = maps.find(map => map.id === state.selected);
    if (current && (force || pricesChanged || state.detail?.id !== current.id || current.updatedUtc !== state.detail?.updatedUtc ||
      current.profitDivine !== previous?.profitDivine || current.unpricedQuantity !== previous?.unpricedQuantity ||
      current.personalPickupDivine !== previous?.personalPickupDivine || current.partyPickupDivine !== previous?.partyPickupDivine ||
      current.unpricedPersonal !== previous?.unpricedPersonal || current.unpricedParty !== previous?.unpricedParty ||
      current.leftDivine !== previous?.leftDivine || current.leftUnpricedQuantity !== previous?.leftUnpricedQuantity ||
      current.chaosDivineRate !== previous?.chaosDivineRate || current.mapPriceDivine !== previous?.mapPriceDivine ||
      current.favorite !== previous?.favorite || current.failed !== previous?.failed ||
      JSON.stringify(current.pricing) !== JSON.stringify(previous?.pricing)))
      await loadDetail(current.id, requestId);
  } catch {
    if (requestId !== latestRequest) return;
    set("connection", "Disconnected from plugin");
    $("connection").classList.add("error");
  }
}

function selectedHistorySession() {
  const id = state.sessionView === "active" ? state.sessions?.activeId : state.sessionView;
  return state.sessions?.sessions.find(session => session.id === id);
}

function renderSessionControls() {
  const select = $("session-view");
  const current = state.sessions.sessions.find(session => session.id === state.sessions.activeId);
  const archive = state.sessions.sessions.filter(session => session.id !== state.sessions.activeId).slice().reverse();
  const currentLabel = current ? `Current · ${current.name ?? dateLabel(current.startedUtc)}` : "Current session";
  const options = [[currentLabel, "active"], ...archive.map(session =>
    [session.name ?? `Session · ${dateLabel(session.startedUtc)}`, session.id]), ["All history", "all"], ["Favorites", "favorites"]];
  if (select.options.length !== options.length || options.some(([label, value], index) =>
    select.options[index].value !== value || select.options[index].textContent !== label))
    select.replaceChildren(...options.map(([label, value]) => new Option(label, value)));
  if (!options.some(option => option[1] === state.sessionView)) state.sessionView = "active";
  select.value = state.sessionView;
  $("open-rename-session").hidden = !selectedHistorySession();
  $("open-delete-session").hidden = $("open-rename-session").hidden;
  $("resume-session").hidden = !archive.some(session => session.id === state.sessionView);
  $("unique-prices").checked = state.sessions.includeUniquePrices;
  const threshold = state.sessions.failUniqueThreshold;
  set("fail-note", Number.isInteger(threshold) ? `Threshold · ≤ ${number.format(threshold)} uniques` : "Selected period");
}

function renderWidgetStatus() {
  $("open-widget-settings").disabled = !state.widget;
  const sessions = state.sessions?.sessions ?? [];
  const id = state.sessionView === "active" ? state.sessions?.activeId : state.sessionView;
  const live = state.widget?.data;
  const included = sessions.filter(session => id === "all" || session.id === id);
  const hideout = included.reduce((total, session) => total +
    (live?.sessionId === session.id ? live.hideoutSeconds : session.hideoutSeconds ?? 0), 0);
  set("session-hideout-time", included.length ? `Hideout time · ${duration(hideout)}` : "");
  if ($("widget-settings-dialog").open) renderWidgetPreview();
}

function openWidgetSettings() {
  const settings = state.widget?.settings;
  if (!settings) return;
  $("widget-enabled").checked = settings.enabled;
  $("widget-outside").checked = settings.showOutsideMaps;
  $("widget-panels").checked = settings.showOverPanels;
  $("widget-compact").checked = settings.compact;
  $("widget-x").value = settings.x;
  $("widget-y").value = settings.y;
  $("widget-opacity").value = settings.opacity;
  $("widget-items").value = (settings.customItems ?? []).join("\n");
  const hidden = new Set(settings.hiddenRows ?? []);
  $("widget-rows").replaceChildren(...state.widget.options.map(option => {
    const label = element("label", null, "check");
    const input = element("input");
    input.type = "checkbox";
    input.value = option.id;
    input.checked = !hidden.has(option.id);
    label.append(input, option.label);
    return label;
  }));
  set("widget-settings-status", "");
  renderWidgetPreview();
  $("widget-settings-dialog").showModal();
}

function renderWidgetPreview() {
  for (const key of ["x", "y", "opacity"])
    set(`widget-${key}-label`, `${$(`widget-${key}`).value}%`);
  const placement = $("widget-position-marker");
  placement.style.left = `${$("widget-x").valueAsNumber * 0.78}%`;
  placement.style.top = `${$("widget-y").valueAsNumber * 0.74}%`;
  const preview = $("widget-preview");
  preview.classList.toggle("compact", $("widget-compact").checked);
  preview.style.setProperty("--widget-opacity", $("widget-opacity").valueAsNumber / 100);
  preview.replaceChildren();
  const data = state.widget?.data;
  if (!data?.inGame) {
    preview.append(element("p", "Waiting for the game to load.", "muted"));
    return;
  }
  const visible = new Set([...$("widget-rows").querySelectorAll("input:checked")].map(input => input.value));
  const list = element("dl");
  for (const row of data.rows) {
    if (!visible.has(row.id)) continue;
    const line = element("div", null, row.inline ? "widget-row widget-inline-row" : "widget-row");
    line.style.setProperty("--widget-accent", `#${row.accentRgb.toString(16).padStart(6, "0")}`);
    line.append(element("dt", row.label), element("dd", row.current, row.current === "—" ? "widget-empty-value" : ""));
    if (!row.inline) {
      const column = element("dd", row.session ?? "", "widget-session-value");
      if (row.progress?.found > 0) {
        const bar = element("progress", null, "widget-encounter-progress");
        bar.max = row.progress.found;
        bar.value = row.progress.completed;
        bar.setAttribute("aria-label", `${row.label} completed`);
        column.append(bar);
      }
      line.append(column);
    }
    list.append(line);
  }
  preview.append(element("strong", data.area), list);
}

function renderIgnoreList(syncCutoff = false) {
  const names = state.sessions?.ignoredItems ?? [];
  if (syncCutoff) {
    $("ignore-price-threshold").value = state.sessions?.minimumPrice?.value ?? "";
    $("minimum-price-currency").value = state.sessions?.minimumPrice?.currency ?? "chaos";
  }
  set("open-ignore-list", names.length ? `Global ignore list (${number.format(names.length)})` : "Global ignore list");
  $("ignore-empty").hidden = names.length > 0;
  const list = $("ignored-items");
  list.replaceChildren(...names.map(name => {
    const row = element("li");
    const remove = element("button", "Remove", "button");
    remove.type = "button";
    remove.setAttribute("aria-label", `Remove ${name} from the global ignore list`);
    remove.addEventListener("click", () => saveIgnoredItem(name, false, remove, "ignore-status"));
    row.append(element("span", name), remove);
    return row;
  }));
  if (state.editing)
    set("ignore-from-edit", isIgnored(state.editing.name) ? "Restore in all maps" : "Ignore in all maps");
}

async function saveIgnoredItem(name, ignored, trigger, status) {
  trigger.disabled = true;
  set(status, "");
  try {
    await request("/api/settings/ignored-items", { name, ignored });
    await refresh(true);
    return true;
  } catch {
    set(status, "Could not update the global ignore list.");
    return false;
  } finally {
    trigger.disabled = false;
  }
}

function itemIcon(item, fallback) {
  const initials = fallback ?? item.name.split(/\s+/).filter(Boolean).slice(0, 2).map(word => word[0]).join("").toUpperCase();
  const icon = element("span", initials, "item-icon");
  icon.setAttribute("aria-hidden", "true");
  if (item.icon) {
    const image = element("img");
    image.alt = "";
    image.draggable = false;
    image.loading = "lazy";
    image.decoding = "async";
    image.referrerPolicy = "no-referrer";
    image.addEventListener("load", () => icon.classList.add("loaded"), { once: true });
    image.addEventListener("error", () => image.remove(), { once: true });
    image.src = item.icon;
    icon.append(image);
  }
  return icon;
}

function renderSession() {
  const session = state.session;
  const items = session?.items ?? [];
  const showsMore = items.length > sessionPreviewSize;
  const expanded = showsMore && state.sessionExpanded;
  const list = $("session-loot");
  const button = $("session-more");
  const amountMode = $("session-amount-mode");
  const byValue = state.sessionAmount === "divines";
  const showDrops = state.pickupScope === "all";
  const favorites = state.sessionView === "favorites";
  set("session-title", state.pickupScope === "personal" ? "Your pickups" : state.pickupScope === "party" ? "Party pickups (est.)" :
    state.sessionView === "all" ? "Drops across all sessions" : favorites ? "Favorite map drops" : "Session drops");
  list.hidden = !items.length;
  amountMode.hidden = !items.length;
  for (const choice of amountMode.querySelectorAll("[data-session-amount]"))
    choice.setAttribute("aria-pressed", String(choice.dataset.sessionAmount === state.sessionAmount));
  list.classList.toggle("expanded", expanded);
  $("session-empty").hidden = !!items.length;
  button.hidden = !showsMore;
  button.setAttribute("aria-expanded", String(expanded));
  set("session-more", expanded ? "Show less" : `Show all (${number.format(items.length)})`);
  set("session-maps", session ? `Maps · ${number.format(session.maps)}` : "");
  set("session-note", session?.lastMap ? `Last map · ${session.lastMap}` : "");
  const emptyDrops = favorites ? "No drops recorded for favorite maps." : "No drops recorded for this session.";
  set("session-empty", session ? (showDrops ? emptyDrops : "No pickups from this source were recorded.") : "Could not load session loot.");

  const entries = items.slice().sort((a, b) => compareLootValues(a.valueDivine, b.valueDivine, "descending") ||
    b.count - a.count || a.name.localeCompare(b.name));
  const scroll = expanded ? list.scrollTop : 0;
  list.replaceChildren(...(expanded ? entries : entries.slice(0, sessionPreviewSize)).map(item => {
    const row = element("li", null, "session-item");
    row.classList.toggle("highlight", sessionHighlights.has(item.name));
    const value = element("span", null, "item-amount");
    const amount = element("strong", byValue ? divines(item.valueDivine) : number.format(item.count));
    if (byValue && item.pricedCount < item.count)
      amount.title = `Priced ${number.format(item.pricedCount)} of ${number.format(item.count)} items`;
    value.append(amount);
    const lastAmount = byValue
      ? (item.lastMapValueDivine == null ? null : divines(item.lastMapValueDivine))
      : (item.lastMapCount > 0 ? number.format(item.lastMapCount) : null);
    if (lastAmount != null) {
      const last = element("span", `+${lastAmount}`, "last-amount");
      last.title = `${showDrops ? "Dropped" : "Picked up"} on the last map · ${session.lastMap}`;
      last.setAttribute("aria-label", `On the last map ${lastAmount}`);
      value.append(last);
    }
    row.append(itemIcon(item), element("span", item.name, "item-label"), value);
    return row;
  }));
  list.scrollTop = scroll;
}

function loadAverageItems() {
  try {
    const saved = JSON.parse(localStorage.getItem(averageItemsKey));
    if (Array.isArray(saved)) {
      const names = saved.filter(name => typeof name === "string" && name.trim() && name.length <= 240);
      return [...new Map(names.map(name => [name.trim().toLowerCase(), name.trim()])).values()];
    }
  } catch {
    // Keep the default counters when browser storage is unavailable.
  }
  return [...defaultAverageItems];
}

function loadAverageOrder() {
  try {
    const saved = JSON.parse(localStorage.getItem(averageOrderKey));
    if (!Array.isArray(saved)) return [];
    const order = [...new Set(saved.filter(key => typeof key === "string"))];
    const t0 = order.indexOf("stat:t0");
    if (t0 >= 0 && !order.includes("stat:belts")) order.splice(t0 + 1, 0, "stat:belts");
    return order;
  } catch {
    return [];
  }
}

function saveAverageItems(names) {
  state.averageItems = names;
  try {
    localStorage.setItem(averageItemsKey, JSON.stringify(names));
  } catch {
    // The counters can still be edited for this page.
  }
  refreshDropAverages();
}

function updateAverageItemSuggestions() {
  const names = new Set([...defaultAverageItems, "Voidborn Reliquary Key",
    ...(state.session?.items ?? []).map(item => item.name)]);
  $("drop-average-suggestions").replaceChildren(...[...names].sort((a, b) => a.localeCompare(b))
    .map(name => new Option(name, name)));
}

function dropAverageMetrics(result) {
  const metrics = [
    ["stat:currency", "Currency", result?.currencyDropped, result?.maps],
    ["stat:uniques", "Uniques", result?.uniquesDropped, result?.maps],
    ["stat:kills", "Monster kills", result?.monsterKills, result?.mapsWithKills],
    ["stat:t0", "T0 Uniques", result?.t0UniquesDropped, result?.mapsWithT0],
    ["stat:belts", "Unique Belts", result?.uniqueBeltsDropped, result?.mapsWithBeltCounts],
    ...state.averageItems.map(name => [`item:${itemKey(name)}`, name, result?.drops[name], result?.maps, true])
  ];
  const ranks = new Map(state.averageOrder.map((key, index) => [key, index]));
  return metrics.sort(([a], [b]) => (ranks.get(a) ?? ranks.size) - (ranks.get(b) ?? ranks.size));
}

function renderDropAverages(result, note) {
  if (averageDrag) {
    pendingAverageRender = { result, note };
    return;
  }
  set("drop-averages-note", note);
  const visible = dropAverageMetrics(result).filter(([key, , , maps]) => key !== "stat:kills" || maps > 0);
  $("drop-average-items").replaceChildren(...visible.map(([key, name, count, maps, custom]) => {
    const stat = element("div", null, "drop-average-card");
    stat.dataset.averageKey = key;
    stat.tabIndex = 0;
    stat.setAttribute("aria-keyshortcuts", "ArrowLeft ArrowRight ArrowUp ArrowDown");
    const average = element("span", maps ? formatDivineNumber(count / maps) : "—", "drop-average-value");
    if (maps) average.title = `${number.format(count)} across ${number.format(maps)} maps`;
    const controls = element("dd");
    controls.append(average);
    if (custom) {
      const remove = element("button", "×", "plain drop-average-remove");
      remove.type = "button";
      remove.setAttribute("aria-label", `Remove ${name}`);
      remove.addEventListener("click", () => {
        saveAverageItems(state.averageItems.filter(item => item !== name));
        $("drop-average-name").focus();
      });
      controls.append(remove);
    }
    const label = element("dt");
    const icon = result?.icons?.[custom ? `item:${name}` : key];
    label.append(itemIcon({ name, icon }, key === "stat:kills" ? "⚔︎" : undefined), element("span", name, "drop-average-label"));
    stat.append(label, controls);
    return stat;
  }));
}

function moveAverageCard(card, target) {
  if (card === target) return;
  const list = $("drop-average-items");
  const follows = card.compareDocumentPosition(target) & Node.DOCUMENT_POSITION_FOLLOWING;
  list.insertBefore(card, follows ? target.nextElementSibling : target);
  state.averageOrder = [...list.children].map(entry => entry.dataset.averageKey);
  try { localStorage.setItem(averageOrderKey, JSON.stringify(state.averageOrder)); }
  catch { /* The order still applies on this page. */ }
}

function markAverageDropTarget(card) {
  if (averageDropTarget === card) return;
  averageDropTarget?.classList.remove("drop-average-target");
  averageDropTarget = card;
  averageDropTarget?.classList.add("drop-average-target");
}

function finishAverageDrag(event) {
  if (averageDrag?.pointerId !== event.pointerId) return;
  const { card, moving } = averageDrag;
  averageDrag = null;
  if (moving && event.type === "pointerup" && averageDropTarget) moveAverageCard(card, averageDropTarget);
  card.classList.remove("drop-average-dragging");
  card.style.removeProperty("transform");
  markAverageDropTarget(null);
  if (pendingAverageRender) {
    const { result, note } = pendingAverageRender;
    pendingAverageRender = null;
    renderDropAverages(result, note);
  }
}

async function refreshDropAverages() {
  const maps = state.statisticsMaps.filter(map => hasTracking(map) && map.state !== "active");
  const itemNames = state.averageItems;
  const key = JSON.stringify([itemNames, maps.map(map => [map.id, map.updatedUtc])]);
  if (key === dropAverageKey) return;
  dropAverageKey = key;
  const requestId = ++latestAverageRequest;
  if (!maps.length) {
    renderDropAverages(null, "No recorded maps in this selection.");
    return;
  }

  renderDropAverages(null, "Calculating…");
  try {
    const result = await request("/api/drop-averages", { itemNames, mapIds: maps.map(map => map.id) });
    if (requestId === latestAverageRequest)
      renderDropAverages(result, `${number.format(result.maps)} ${result.maps === 1 ? "map" : "maps"}`);
  } catch {
    if (requestId === latestAverageRequest) {
      dropAverageKey = "";
      renderDropAverages(null, "Could not load drop averages.");
    }
  }
}

function updateLeagueFilter() {
  const select = $("league");
  const leagues = [...new Set(state.maps.map(map => map.league).filter(Boolean))].sort();
  if (Array.from(select.options).slice(1).map(option => option.value).join("|") === leagues.join("|")) return;
  const selected = select.value;
  select.replaceChildren(new Option("All leagues", ""), ...leagues.map(league => new Option(league, league)));
  if (leagues.includes(selected)) select.value = selected;
}

function renderOverview() {
  const search = itemKey($("search").value.trim());
  const league = $("league").value;
  const status = $("status").value;
  const encounter = $("map-encounter").value;
  const limit = Number($("limit").value);
  const scope = state.sessionView === "active" ? state.sessions?.activeId : state.sessionView;
  const matches = state.maps.filter(map => {
    const inScope = scope === "favorites" ? map.favorite : !scope || scope === "all" || map.sessionId === scope;
    if (!inScope) return false;
    if (league && map.league !== league) return false;
    if (search && !itemKey(map.name).includes(search)) return false;
    if (encounter && !map.zones?.includes(encounter)) return false;
    if (status === "excluded") return map.excludedFromStatistics;
    if (status === "failed") return map.failed;
    if (status === "active") return !map.failed && ["active", "paused"].includes(map.state);
    return !status || (!map.failed && map.state === status);
  });
  const included = matches.filter(includedInStatistics);
  const lastMap = limit ? included[limit - 1] : null;
  state.filtered = lastMap ? matches.slice(0, matches.indexOf(lastMap) + 1) : matches;
  state.statisticsMaps = limit ? included.slice(0, limit) : included;
  if (!state.filtered.some(map => map.id === state.selected)) {
    state.selected = state.filtered[0]?.id ?? null;
    state.detail = null;
    $("loot-search").value = "";
    renderDetail();
  } else if (state.tab === "uniques" && state.detail) {
    renderDetail();
  }

  const tracked = state.statisticsMaps.filter(hasTracking);
  const net = state.profitMode === "net";
  const costs = tracked.map(mapExpense);
  const missingCosts = costs.filter(cost => cost == null).length;
  const valued = net ? tracked.filter((_, index) => costs[index] != null) : tracked;
  const timed = valued.filter(map => map.seconds > 0);
  const seconds = timed.reduce((sum, map) => sum + map.seconds, 0);
  const total = valued.reduce((sum, map) => sum + mapProfit(map), 0);
  const personalPickup = tracked.reduce((sum, map) => sum + map.personalPickupDivine, 0);
  const partyPickup = tracked.reduce((sum, map) => sum + map.partyPickupDivine, 0);
  const timedTotal = timed.reduce((sum, map) => sum + mapProfit(map), 0);
  const unpriced = valued.reduce((sum, map) => sum + unpricedPickups(map), 0);
  const left = tracked.reduce((sum, map) => sum + map.leftDivine, 0);
  const leftUnpriced = tracked.reduce((sum, map) => sum + map.leftUnpricedQuantity, 0);
  const withKills = state.statisticsMaps.filter(map => map.monsterKills != null);
  $("kill-metric").hidden = !withKills.length;
  $("sort-history-kills").hidden = !withKills.length;
  if (!withKills.length && $("map-sort").value === "kills") $("map-sort").value = "recent";
  const killTotals = Object.fromEntries(killRarities.map(([, key]) =>
    [key, withKills.reduce((total, map) => total + (map.killBreakdown?.[key] ?? 0), 0)]));
  set("profit-label", net ? "Net profit" : "Pickup value");
  set("hour-label", net ? "Net per hour" : "Value per hour");
  for (const button of $("profit-mode").querySelectorAll("[data-profit-mode]"))
    button.setAttribute("aria-pressed", String(button.dataset.profitMode === state.profitMode));
  const costTotal = divines(tracked.reduce((sum, map) => sum + mapPrice(map) + threadExpenses(map).knownTotal, 0));
  set("cost-summary", tracked.length ? [
    `${missingCosts ? "Known costs" : "Map costs"} · ${costTotal}`,
    missingCosts ? `${number.format(missingCosts)} ${missingCosts === 1 ? "map" : "maps"} with incomplete costs` : ""
  ].filter(Boolean).join(" · ") : "");
  setMetricDivines("total", valued.length ? total : null);
  renderPickupBreakdown("total-pickup-split", tracked.length ? personalPickup : null, tracked.length ? partyPickup : null);
  setMetricDivines("hour", seconds ? timedTotal * 3600 / seconds : null);
  setMetricDivines("average", valued.length ? total / valued.length : null);
  setMetricDivines("left-total", tracked.length ? left : null);
  set("left-note", leftUnpriced ? `Unpriced · ${number.format(leftUnpriced)} items` : "Priced drops");
  set("fails", state.statisticsMaps.length ? number.format(state.statisticsMaps.filter(map => map.failed).length) : "—");
  set("kills-total", withKills.length ? number.format(withKills.reduce((sum, map) => sum + map.monsterKills, 0)) : "—");
  $("kills-note").replaceChildren(withKills.length ? createKillBreakdown(killTotals) : "No maps with kill tracking");
  $("kills-note").title = withKills.length ? `Maps with kill tracking · ${number.format(withKills.length)}` : "";
  set("total-note", unpriced ? `Unpriced · ${number.format(unpriced)} items` : net ? "After map costs" : "Priced pickups");
  set("time-note", seconds ? duration(seconds) + " in maps" : "Time spent in maps");
  set("map-note", `${number.format(valued.length)} ${valued.length === 1 ? "map" : "maps"}`);
  set("maps-title", scope === "favorites" ? "Favorite maps" : "Maps");
  set("map-count", number.format(state.filtered.length));
  refreshDropAverages();
  renderMapList();
  drawChart();
}

function renderMapList() {
  const selectedFocus = document.activeElement?.dataset.mapId;
  const list = $("maps");
  const sort = $("map-sort").value;
  const order = {
    profit: map => hasTracking(map) ? mapProfit(map) : null,
    divines: map => map.divineOrbsDropped,
    uniques: map => map.uniquesDropped,
    kills: map => map.monsterKills
  }[sort];
  const maps = order ? state.filtered.slice().sort((a, b) => compareLootValues(order(a), order(b), "descending")) : state.filtered;
  const countLabel = { divines: "Divine Orb", uniques: "unique", kills: "kill" }[sort];
  list.replaceChildren();
  if (!maps.length) {
    const noFavorites = state.sessionView === "favorites" && !state.maps.some(map => map.favorite);
    list.append(element("p", noFavorites ? "Open a map and click its star to add it to Favorites." : "No matching maps.", "empty"));
  }
  for (const map of maps.slice(0, state.visible)) {
    const row = element("button", null, "map-row");
    row.type = "button";
    row.dataset.mapId = map.id;
    row.classList.toggle("map-row-failed", map.failed);
    row.setAttribute("aria-current", String(map.id === state.selected));
    const information = element("span");
    const name = element("strong", map.name);
    if (map.favorite) {
      const star = element("span", "★", "map-favorite-marker");
      star.setAttribute("role", "img");
      star.setAttribute("aria-label", "Favorite");
      name.append(" ", star);
    }
    if (map.failed) name.append(" ", element("span", "Failed", "map-failed"));
    if (map.excludedFromStatistics) name.append(" ", element("span", "Excluded", "map-excluded"));
    const date = dateLabel(map.startedUtc);
    information.append(name, element("small", map.failed ? date : `${statusLabel(map)} · ${date}`));
    if (map.seconds != null)
      information.append(element("small", `Duration ${duration(map.seconds)}`, "map-duration"));
    const tags = element("span", null, "zone-tags");
    renderZoneTags(tags, map.zones);
    if (!tags.hidden) information.append(tags);
    const profit = divines(hasTracking(map) ? mapProfit(map) : null);
    const count = countLabel ? order(map) : null;
    const amount = !countLabel ? profit : count == null ? "—" : `${number.format(count)} ${countLabel}${count === 1 ? "" : "s"}`;
    const value = element("span", amount, "amount");
    const hourlyValue = hasTracking(map) ? hourly(map) : null;
    value.append(element("small", countLabel ? profit : hourlyValue != null ? `${divines(hourlyValue)} / h` : duration(map.seconds)));
    row.append(information, value);
    row.addEventListener("click", () => selectMap(map.id));
    list.append(row);
  }
  if (selectedFocus) [...list.children].find(row => row.dataset.mapId === selectedFocus)?.focus({ preventScroll: true });
  $("more").hidden = state.filtered.length <= state.visible;
}

const zoneLabels = { wildwood: "Wildwood", delirium: "Delirium", mirage: "Mirage", beyond: "Beyond" };

function createKillBreakdown(kills) {
  const breakdown = element("span", null, "kill-breakdown");
  breakdown.append("(");
  for (const [index, [label, key]] of killRarities.entries()) {
    if (index) breakdown.append(" / ");
    const formatted = number.format(kills[key]);
    const count = element("span", formatted, `kill-${key}`);
    count.title = `${label} ${formatted}`;
    count.setAttribute("aria-label", count.title);
    breakdown.append(count);
  }
  breakdown.append(")");
  return breakdown;
}

function renderZoneTags(parent, zones = []) {
  const labels = Object.entries(zoneLabels).filter(([zone]) => zones?.includes(zone));
  parent.hidden = !labels.length;
  parent.replaceChildren(...labels.map(([zone, label]) => element("span", label, `zone-tag ${zone}`)));
}

async function selectMap(id) {
  if (state.selected === id && state.detail?.id === id) return;
  state.selected = id;
  state.detail = null;
  $("loot-search").value = "";
  renderMapList();
  renderDetail();
  drawChart();
  await loadDetail(id);
}

async function loadDetail(id, requestId = latestRequest) {
  try {
    const run = await request(`/api/maps/${id}`);
    if (id !== state.selected || requestId !== latestRequest) return;
    state.detail = run;
    renderDetail();
  } catch {
    if (id === state.selected && requestId === latestRequest)
      set("detail-empty", "Could not open this map record.");
  }
}

function renderDetail() {
  const run = state.detail;
  const map = state.maps.find(entry => entry.id === run?.id);
  $("selected").hidden = !map;
  $("detail-empty").hidden = !!map;
  set("detail-name", map?.name ?? "Map details");
  const star = $("toggle-map-favorite");
  star.hidden = !map;
  star.setAttribute("aria-pressed", String(!!map?.favorite));
  star.title = map?.favorite ? "Remove from Favorites" : "Add to Favorites";
  set("toggle-map-favorite", map?.favorite ? "★" : "☆");
  set("detail-meta", map ? [dateLabel(map.startedUtc), map.league, statusLabel(map),
    map.excludedFromStatistics ? "Excluded from statistics" : ""].filter(Boolean).join(" · ") : "");
  renderZoneTags($("detail-zones"), map?.zones);
  $("detail-profit").hidden = !map || !hasTracking(map);
  const showDrops = state.pickupScope === "all";
  set("detail-profit-label", state.profitMode === "net" ? "Net profit" : "Pickup value");
  set("detail-value", map && hasTracking(map) ? divines(mapProfit(map)) : "");
  renderPickupBreakdown("detail-pickup-split", map?.personalPickupDivine, map?.partyPickupDivine);
  $("detail-ground").hidden = !showDrops;
  set("detail-left", map && hasTracking(map) ? divines(map.leftDivine) : "");
  set("detail-empty", state.selected ? "Loading map…" : "Select a map to view its details.");
  if (!map) return;

  const minimum = state.sessions?.minimumPrice;
  const rateUnavailable = minimum?.currency === "chaos" && !(map.chaosDivineRate > 0);
  const minimumDivine = rateUnavailable ? null : minimum?.currency === "chaos" ? minimum.value * map.chaosDivineRate : minimum?.value;
  const kills = map.killBreakdown;
  const dropBonuses = (map.dropBonuses ?? []).filter(bonus => /^(?:More|Less) /.test(bonus.label) &&
    (bonus.stat || !run.modifiers?.length));
  const mapStats = [
    ["Time", duration(map.seconds)], ["Quantity", `${map.quantity}%`], ["Rarity", `${map.rarity}%`],
    ["Pack size", `${map.packSize}%`],
    ...dropBonuses.map(({ label, percent }) => [label, `${number.format(percent)}%`]),
    ...(map.t0UniquesDropped > 0 ? [["T0 Uniques", number.format(map.t0UniquesDropped), map.lastT0Unique]] : []),
    ...(map.deaths > 0 ? [["Deaths", number.format(map.deaths),
      `You ${number.format(map.deaths - map.partyDeaths)} · Party ${number.format(map.partyDeaths)}`]] : []),
    ...(kills == null ? [] : [["Monster kills", number.format(map.monsterKills), "Observed monster deaths in this area"]])
  ];
  $("map-stats").replaceChildren(...mapStats.map(([label, value, tooltip]) => {
    const stat = element("span", label);
    stat.append(element("strong", value));
    if (label === "Monster kills" && kills != null)
      stat.append(createKillBreakdown(kills));
    if (tooltip) stat.title = tooltip;
    return stat;
  }));
  renderRunTotals(run, map, minimumDivine);
  $("failure").value = run.failureOverride == null ? "auto" : run.failureOverride ? "yes" : "no";
  $("map-include-statistics").checked = includedInStatistics(map);
  set("save-status", "");

  const parent = $("details-body");
  parent.replaceChildren();
  $("loot-tools").hidden = state.tab !== "loot";
  $("mod-tools").hidden = state.tab !== "mods";
  $("unique-tools").hidden = state.tab !== "uniques";
  if (state.tab === "mods") {
    renderThreadModifiers(parent, run.modifiers, map.threadUsage);
    renderModifiers(parent, run, map, dropBonuses);
    renderEncounterDiagnostics(parent, run.encounterSignals);
    return;
  }
  if (state.tab === "altars") {
    renderAltars(parent, run);
    return;
  }
  if (state.tab === "uniques") {
    renderUniques();
    return;
  }
  if (state.tab === "costs") {
    renderMapCosts(parent, map);
    return;
  }

  const tracked = hasTracking(map);
  const query = itemKey($("loot-search").value.trim());
  const entries = run.loot.filter(item => (showDrops ? item.dropped > 0 || pickupQuantity(item) > 0 : tracked && pickupQuantity(item) > 0) &&
    (!query || itemKey(item.name).includes(query)));
  const notes = [];
  if (!tracked) notes.push("This legacy record contains drops only.");
  if (rateUnavailable) notes.push("Chaos rate unavailable for the price cutoff.");
  if (state.pickupScope !== "personal" && map.estimated) notes.push(`Estimated party pickups · ${number.format(map.estimated)} items`);
  if (unpricedPickups(map)) notes.push(`Unpriced · ${number.format(unpricedPickups(map))} items`);
  if (showDrops && map.leftUnpricedQuantity) notes.push(`Unpriced on the ground · ${number.format(map.leftUnpricedQuantity)} items`);
  set("table-note", notes.join("  |  "));
  $("reset-loot-sort").hidden = state.lootSort == null;
  if (!entries.length) {
    const message = query ? "No matching items." : showDrops ? "No drops recorded on this map yet." : "No pickups from this source were recorded.";
    parent.append(element("p", message, "empty"));
    return;
  }

  const columns = tracked
    ? [["Item", "name"], [showDrops ? "Picked up / dropped" : "Picked up", "picked"], ["Picked up (div)", "pickupValue"],
      ...(showDrops ? [["Left on ground (div)", "groundValue"]] : []), [""]]
    : [["Item", "name"], ["Dropped", "dropped"]];
  const currentSort = state.lootSort;
  const sort = currentSort && columns.some(([, key]) => key === currentSort.key) ? currentSort : null;
  const table = element("table");
  const head = table.createTHead().insertRow();
  columns.forEach(([label, key], i) => {
    const numeric = i > 1 || !tracked && i === 1;
    const direction = sort && sort.key === key ? sort.direction : null;
    const cell = sortableHeading(label, key, direction, key === "name" ? "ascending" : "descending", numeric, next => {
      state.lootSort = { key, direction: next };
      renderDetail();
      parent.querySelector(`[data-sort-key="${key}"]`)?.focus({ preventScroll: true });
    });
    head.append(cell);
  });
  const body = table.createTBody();
  const sortedEntries = entries.map(item => {
    const picked = pickupQuantity(item);
    const left = showDrops ? Math.max(item.dropped - picked, 0) : 0;
    const pricing = itemPricing(run, item, minimumDivine);
    const unit = pricing.unit;
    return {
      item, name: item.name, dropped: item.dropped, picked, left, pricing,
      pickupValue: picked > 0 && unit != null ? picked * unit : null,
      groundValue: left > 0 && unit != null ? left * unit : null
    };
  });
  const defaultOrder = (a, b) =>
    Number(b.picked > 0) - Number(a.picked > 0) ||
    compareLootValues(a.pickupValue, b.pickupValue, "descending") ||
    compareLootValues(a.groundValue, b.groundValue, "descending") ||
    (showDrops ? b.dropped - a.dropped : b.picked - a.picked) || a.name.localeCompare(b.name);
  sortedEntries.sort((a, b) => {
    if (!sort) return tracked ? defaultOrder(a, b) : b.dropped - a.dropped || a.name.localeCompare(b.name);
    return compareLootValues(a[sort.key], b[sort.key], sort.direction) ||
      (showDrops && sort.key === "picked" ? compareLootValues(a.dropped, b.dropped, sort.direction) : 0) ||
      a.name.localeCompare(b.name);
  });

  for (const { item, picked, left, pricing, pickupValue, groundValue } of sortedEntries) {
    const row = body.insertRow();
    const edit = run.edits[item.id];
    if (pricing.excluded) row.className = "omitted";
    const name = row.insertCell();
    const label = element("span");
    label.append(element("span", item.name, "item-name"), element("small", item.category));
    if (pricing.excluded) label.append(element("small", pricing.reason));
    const content = element("span", null, "table-item");
    content.append(itemIcon(item), label);
    name.append(content);
    const count = row.insertCell();
    if (!tracked) {
      count.className = "numeric";
      count.textContent = number.format(item.dropped);
      continue;
    }
    count.className = "pickup-cell";
    const ratio = element("span", null, "pickup-count");
    ratio.append(element("strong", number.format(picked)));
    if (showDrops) ratio.append(element("span", ` of ${number.format(item.dropped)}`));
    count.append(ratio);
    if (showDrops && item.dropped > 0) {
      const progress = element("progress", null, "pickup-progress");
      progress.max = item.dropped;
      progress.value = Math.min(picked, item.dropped);
      progress.setAttribute("aria-label", `${item.name}: picked up ${number.format(picked)} of ${number.format(item.dropped)}`);
      count.append(progress);
    }
    if (item.estimated && state.pickupScope !== "personal") {
      const sources = Object.entries(item.collectors ?? {}).map(([collector, units]) => `${collector} · ${units}`).join(", ");
      if (showDrops) {
        const estimate = element("small", `~${item.estimated} party`);
        estimate.title = sources;
        count.append(estimate);
      } else {
        count.title = sources;
      }
    }
    const values = [[picked, "pickup-value", pickupValue]];
    if (showDrops) values.push([left, "left-value", groundValue]);
    for (const [units, field, total] of values) {
      const value = row.insertCell();
      value.className = `numeric ${field}`;
      value.textContent = units === 0 ? "—" : total == null ? pricing.reason : divines(total);
      if (field === "left-value" && left > 0) value.append(element("small", `${number.format(left)} items`));
      if (units > 0 && edit?.unitDivine != null && total != null) value.append(element("small", "Manual price"));
    }
    const action = row.insertCell();
    action.className = "numeric";
    const button = element("button", "Edit", "button");
    button.type = "button";
    button.setAttribute("aria-label", `Edit valuation: ${item.name}`);
    button.addEventListener("click", () => openEdit(run, item));
    action.append(button);
  }
  parent.append(table);
}

function sortableHeading(label, key, direction, firstDirection, numeric, onSort) {
  const cell = element("th", null, numeric ? "numeric" : null);
  cell.scope = "col";
  if (!key) return cell;
  if (direction) cell.setAttribute("aria-sort", direction);
  const button = element("button", label, "loot-sort");
  button.type = "button";
  button.dataset.sortKey = key;
  const indicator = element("span", direction === "ascending" ? "▲" : direction === "descending" ? "▼" : "↕");
  indicator.setAttribute("aria-hidden", "true");
  button.append(indicator);
  button.addEventListener("click", () => onSort(!direction ? firstDirection : direction === "ascending" ? "descending" : "ascending"));
  cell.append(button);
  return cell;
}

function compareLootValues(a, b, direction) {
  if (a == null) return b == null ? 0 : 1;
  if (b == null) return -1;
  const comparison = typeof a === "string" ? a.localeCompare(b) : a - b;
  return direction === "ascending" ? comparison : -comparison;
}

function renderMapCosts(parent, map) {
  const expenses = threadExpenses(map);
  const list = element("dl", null, "mod-bonuses map-cost-list");
  const addCost = (label, value, unitQuote) => {
    const row = element("div", null, "mod-bonus");
    const amount = element("dd", divines(value));
    if (unitQuote != null) amount.title = `${divines(unitQuote)} each`;
    row.append(element("dt", label), amount);
    list.append(row);
  };
  addCost(map.mapPriceDivine == null ? "Map price (default)" : "Map price", mapPrice(map));
  for (const { name, units, unit } of expenses.lines)
    addCost(`${name} × ${number.format(units)}`, unit == null ? null : units * unit, unit);
  addCost("Total map costs", mapExpense(map));
  const edit = element("button", "Edit map price", "button");
  edit.type = "button";
  edit.addEventListener("click", () => {
    state.costMapId = map.id;
    $("map-price").value = map.mapPriceDivine ?? "";
    $("map-price").placeholder = String(recordedDefaultMapPrice(map));
    set("map-price-status", "");
    $("map-price-dialog").showModal();
  });
  parent.append(list);
  if (map.pricing?.snapshotUtc) {
    const label = map.pricing.estimated ? "Thread cost estimate" : "Market price snapshot";
    parent.append(element("p", label + " · " + new Date(map.pricing.snapshotUtc).toLocaleString("en-GB"), "muted"));
  }
  if (expenses.countMismatch)
    parent.append(element("p", `Recorded Threads: ${number.format(map.threadUsage.total)}. Per-type counts add up to ${number.format(expenses.typedCount)}.`, "muted"));
  else if (expenses.total == null)
    parent.append(element("p", "Some Thread prices are missing from this map's recorded quotes.", "muted"));
  parent.append(edit);
}

function uniqueDropsFor(maps, includeExcluded) {
  const key = JSON.stringify([includeExcluded, maps.map(map => [map.id, map.updatedUtc])]);
  if (uniqueSelection?.key === key) return uniqueSelection;

  const selection = { key, result: null, failed: false };
  uniqueSelection = selection;
  if (!maps.length) {
    selection.result = { maps: 0, dropped: 0, items: [] };
    return selection;
  }

  request("/api/unique-drops", { mapIds: maps.map(map => map.id), includeExcluded }).then(result => {
    selection.result = result;
    if (uniqueSelection === selection) renderUniques();
  }, () => {
    selection.failed = true;
    if (uniqueSelection === selection) renderUniques();
  });
  return selection;
}

function uniqueTierTotals(result) {
  const unknown = uniqueTierLabels.length - 1;
  const counts = uniqueTierLabels.map(() => 0);
  let detailed = 0;
  for (const { tier, dropped } of result.items) {
    if (dropped <= 0) continue;
    const index = Number.isInteger(tier) && tier >= 0 && tier < unknown ? tier : unknown;
    counts[index] += dropped;
    detailed += dropped;
  }
  counts[unknown] += Math.max(0, (result.dropped ?? 0) - detailed);
  return counts;
}

function renderUniqueTierTotals(result) {
  const section = element("section", null, "unique-tier-summary");
  section.append(element("h3", "Drops by tier"));
  const list = element("dl", null, "mod-bonuses");
  const counts = uniqueTierTotals(result);
  for (const [index, tier] of uniqueTierLabels.entries()) {
    if (tier === "Unknown" && counts[index] === 0) continue;
    const stat = element("div", null, "mod-bonus");
    stat.append(element("dt", tier), element("dd", number.format(counts[index])));
    list.append(stat);
  }
  section.append(list);
  return section;
}

function renderUniques() {
  if (state.tab !== "uniques" || !state.detail) return;
  const parent = $("details-body");
  const period = $("unique-scope").value === "period";
  const maps = period ? state.statisticsMaps.filter(map => !["active", "paused"].includes(map.state)) :
    state.maps.filter(map => map.id === state.detail.id);
  const { result, failed } = uniqueDropsFor(maps, !period);
  parent.replaceChildren();
  if (!result) {
    parent.append(element("p", failed ? "Could not load unique items." : "Loading unique items…", "empty"));
    if (failed) {
      const retry = element("button", "Retry", "button");
      retry.type = "button";
      retry.addEventListener("click", () => { uniqueSelection = null; renderUniques(); });
      parent.append(retry);
    }
    return;
  }

  const typeSelect = $("unique-type");
  const type = typeSelect.value;
  const tier = $("unique-tier").value;
  const showDrops = state.pickupScope === "all";
  const items = result.items.map(item => ({ ...item, type: item.type ?? "Unknown",
    count: showDrops ? item.dropped : pickupQuantity(item) })).filter(item => item.count > 0);
  const types = [...new Set(items.map(item => item.type).concat(type ? [type] : []))].sort((a, b) => a.localeCompare(b));
  if (JSON.stringify(types) !== JSON.stringify([...typeSelect.options].slice(1).map(option => option.value))) {
    typeSelect.replaceChildren(new Option("All types", ""), ...types.map(name => new Option(name, name)));
    typeSelect.value = type;
  }

  const shown = items.filter(item => (!type || item.type === type) && (!tier || String(item.tier ?? "unknown") === tier));
  const count = shown.reduce((sum, item) => sum + item.count, 0);
  const total = showDrops && !type && !tier ? Math.max(result.dropped, count) : count;
  const amountLabel = showDrops ? "Dropped" : "Picked up";
  const totals = [[amountLabel, number.format(total)], ["Item names", number.format(new Set(shown.map(item => item.name)).size)]];
  if (period)
    totals.push(["Maps", number.format(result.maps)], ["Average per map", result.maps ? priceNumber.format(total / result.maps) : "—"]);

  const header = element("div", null, "unique-summary");
  const summary = element("dl", null, "mod-bonuses unique-totals");
  for (const [label, value] of totals) {
    const stat = element("div", null, "mod-bonus");
    stat.append(element("dt", label), element("dd", value));
    summary.append(stat);
  }
  const reference = element("a", "PoE Wiki tier estimates", "muted");
  reference.href = "https://www.poewiki.net/index.php?title=Guide:Analysis_of_unique_item_tiers&oldid=1706499";
  reference.target = "_blank";
  reference.rel = "noopener noreferrer";
  header.append(summary, reference);
  parent.append(header);
  parent.append(renderUniqueTierTotals(result));
  if (total > count)
    parent.append(element("p", `${number.format(total - count)} drops have no saved item details.`, "muted"));
  if (!shown.length) {
    parent.append(element("p", "No unique items match this selection.", "empty"));
    return;
  }

  const sortSelect = $("unique-sort");
  const [sortKey, sortDirection] = uniqueSortOrders[sortSelect.value];
  shown.sort((a, b) => compareLootValues(a[sortKey], b[sortKey], sortDirection) || b.count - a.count || a.name.localeCompare(b.name));

  const table = element("table", null, "unique-table");
  const headings = table.createTHead().insertRow();
  for (const [label, key, numeric] of [["Item", "name"], ["Type", "type"], ["Tier", "tier"],
    [amountLabel, "count", true], ["Unit price", "unitDivine", true]]) {
    const th = sortableHeading(label, key, sortKey === key ? sortDirection : null, numeric ? "descending" : "ascending", numeric, next => {
      sortSelect.value = Object.keys(uniqueSortOrders).find(order => uniqueSortOrders[order][0] === key && uniqueSortOrders[order][1] === next);
      renderUniques();
      parent.querySelector(`[data-sort-key="${key}"]`)?.focus({ preventScroll: true });
    });
    if (key === "unitDivine") th.title = "Recorded prices in Divine Orbs per item, weighted by the number of priced copies in the selection.";
    headings.append(th);
  }
  const body = table.createTBody();
  for (const item of shown) {
    const row = body.insertRow();
    const name = element("div", null, "table-item");
    name.append(itemIcon(item), element("span", item.name, "item-name"));
    row.insertCell().append(name);
    row.insertCell().textContent = item.type;
    const badge = element("span", item.tier == null ? "Unknown" : `T${item.tier}`, "unique-tier");
    badge.dataset.tier = item.tier ?? "";
    row.insertCell().append(badge);
    const quantity = row.insertCell();
    quantity.className = "numeric";
    quantity.textContent = number.format(item.count);
    const price = row.insertCell();
    price.className = "numeric unique-price";
    price.textContent = divines(item.unitDivine);
  }
  const scroll = element("div", null, "unique-table-scroll");
  scroll.tabIndex = 0;
  scroll.setAttribute("aria-label", "Unique items");
  scroll.append(table);
  parent.append(scroll);
}

function renderEncounterDiagnostics(parent, signals) {
  if (!signals?.length) return;
  const details = element("details", null, "encounter-diagnostics");
  details.append(element("summary", `Encounter diagnostics (${number.format(signals.length)})`));
  const list = element("ul", null, "encounter-signals");
  for (const signal of signals) {
    const time = signalTime.format(new Date(signal.seenUtc));
    list.append(element("li", `${time} · ${signal.area} [${signal.areaHash}] · ${signal.source} · ${signal.detail}`));
  }
  details.append(list);
  parent.append(details);
}

const normalBaseCategory = /^(Amulet|Belt|Body Armour|Boots|Bow|Claw|Dagger|Gloves|Helmet|(?:Abyss ?)?Jewel|Quiver|Ring|Rune Dagger|Sceptre|Shield|Staff|Wand|Warstaff|Thrusting One Hand Sword|(?:One|Two) Hand (?:Axe|Mace|Sword))$/i;
const isCurrency = item => item.category === "Currency";
const isUnique = item => item.category === "Unique";

function renderRunTotals(run, map, minimumDivine) {
  const showDrops = state.pickupScope === "all";
  const container = $("run-totals");
  container.hidden = !showDrops && !hasTracking(map);
  container.classList.toggle("scoped", !showDrops);
  if (container.hidden) return;
  const sum = (test, field) => run.loot.reduce((count, item) => count + (test(item) ? field(item) : 0), 0);
  const dropped = test => sum(test, item => item.dropped);
  const oldBase = name => dropped(item => item.category === "Base" && item.name === name);
  const looksLikeGear = item => !/\bMap$|\b(?:Life|Mana|Hybrid|Utility) Flask$| Idol$/i.test(item.name);
  const totals = run.dropTotals ?? {
    currency: dropped(isCurrency), uniques: dropped(isUnique),
    normal: map.state === "legacy" ? oldBase("Normal") : dropped(item => normalBaseCategory.test(item.category)),
    magic: map.state === "legacy" ? oldBase("Magic") : dropped(item => item.category === "Magic" && looksLikeGear(item)),
    rare: map.state === "legacy" ? oldBase("Rare") : dropped(item => item.category === "Rare" && looksLikeGear(item))
  };
  const approximate = !run.dropTotals && map.state !== "legacy" ? "≈" : "";
  const valuation = (test, quantity, count) => {
    let total = 0;
    let pricedCount = 0;
    for (const item of run.loot) {
      if (!test(item)) continue;
      const units = quantity(item);
      const unit = itemPricing(run, item, minimumDivine).unit;
      if (units <= 0 || unit == null) continue;
      total += units * unit;
      pricedCount += units;
    }
    return {
      text: `${number.format(count)} / ${count === 0 || pricedCount > 0 ? formatDivineNumber(total) + "d" : "—"}`,
      title: pricedCount < count ? `${number.format(pricedCount)} of ${number.format(count)} items included in the valuation` : ""
    };
  };
  const categoryCard = (label, test, count, color) => {
    const picked = hasTracking(map) ? valuation(test, pickupQuantity, sum(test, pickupQuantity)) : null;
    if (!showDrops) return [label, picked.text, color, "", picked.title || "Picked up · Divine value"];
    const drop = valuation(test, item => item.dropped, count);
    return [label, drop.text, color, picked ? `Pick ${picked.text}` : "",
      drop.title || "Dropped · Divine value", picked?.title || "Picked up · Divine value"];
  };
  const cards = [
    categoryCard("Currency", isCurrency, totals.currency, ""),
    categoryCard("Uniques", isUnique, totals.uniques, "unique")
  ];
  if (showDrops) cards.push(
    ["Gold collected", run.goldCollected == null ? "—" : number.format(run.goldCollected), "gold", ""],
    ["Normal bases", approximate + number.format(totals.normal), "", ""],
    ["Magic bases", approximate + number.format(totals.magic), "magic", ""],
    ["Rare bases", approximate + number.format(totals.rare), "rare", ""]
  );
  container.replaceChildren(...cards.map(([label, value, color, note, title, noteTitle]) => {
    const card = element("div", null, `run-total ${color}`);
    const amount = element("dd", value);
    if (note) {
      const subtitle = element("small", note, "compact-pickup");
      if (noteTitle) subtitle.title = noteTitle;
      amount.append(subtitle);
    }
    if (title) amount.title = title;
    if (value === "—") amount.title = "Gold was not tracked for this map";
    if (value.startsWith("≈")) amount.title = "Estimated from saved drop records";
    card.append(element("dt", label), amount);
    return card;
  }));
}

const duplicateEffect = /duplicat|дублир|удво/i;
const altarNames = { eater: "Eater of Worlds", exarch: "Searing Exarch" };
const duplicateDrops = {
  Currency: "Currency", Gems: "Gems", Uniques: "Uniques", Scarabs: "Scarabs",
  Maps: "Maps", DivinationCards: "Divination cards", Sentinels: "Sentinels", Gold: "Gold"
};

function renderAltars(parent, run) {
  if (run.altars == null) {
    parent.append(element("p", "Altar choices were not tracked for this map.", "empty"));
  } else {
    const selections = run.altars.filter(altar => altar.selectedEffects?.length > 0);
    const duplicated = selections.filter(altar => altar.selectedEffects.some(effect => duplicateEffect.test(effect))).length;
    const metrics = element("dl", null, "altar-metrics");
    for (const [label, amount] of [["Found", run.altars.length], ["Selected", selections.length], ["With duplication", duplicated]]) {
      const stat = element("div", null, "mod-bonus");
      stat.append(element("dt", label), element("dd", number.format(amount)));
      metrics.append(stat);
    }
    parent.append(metrics);
    if (selections.length) {
      const list = element("ul", null, "altar-choices");
      for (const altar of selections) {
        const name = altarNames[altar.influence];
        const row = element("li", null, `altar-choice ${name ? altar.influence : ""}`);
        row.append(element("strong", name ?? "Altar"));
        for (const effect of altar.selectedEffects)
          row.append(element("p", displayModifierText(effect), duplicateEffect.test(effect) ? "duplicate" : ""));
        list.append(row);
      }
      parent.append(list);
    } else if (run.altars.length) {
      parent.append(element("p", "No confirmed altar choices yet.", "empty"));
    }
  }

  const effects = run.modifiers.filter(mod => mod.stat.startsWith("PctChanceToDuplicateDropped") && mod.value > 0);
  if (effects.length) {
    parent.append(element("p", "Duplication from map modifiers", "muted altar-stat-title"));
    const list = element("ul", null, "modifiers");
    for (const mod of effects) {
      const kind = mod.stat.slice("PctChanceToDuplicateDropped".length);
      list.append(element("li", displayModifierText(mod.text) ?? `${duplicateDrops[kind] ?? kind} · ${number.format(mod.value)}%`));
    }
    parent.append(list);
  }
}

const modOrder = [
  /^MapMonstersLifePct/i, /PhysicalDamageToAddAs|ExtraDamage/i, /MapMonsterDebuff/i, /CannotBeTaunted/i,
  /ActionSpeedCannotBeReduced/i, /MovementSpeedCannotBeReduced/i, /MapNumAllflame/i, /FractureOnDeath/i,
  /Shaper.*Touch|Touch.*Shaper/i, /Unique.*Shrine|Shrine.*Unique/i, /^MapZanaInfluence$/i,
  /^PctChanceToDuplicateDropped/i, /^MapDropScalingPct$/i
];
const mainModifier = /^(MapMonsters|MapMonster(?:ActionSpeed|MovementSpeed|Debuff)|MapPlayer(?:HasLevel|Cannot|Maximum|Block|Armour)|MapRareMonster(?:Fracture|NumAdditional)|MapNumAllflame|MapPacksHaveUber|MapZanaInfluence$|PctChanceToDuplicateDropped|MapDropScalingPct$|MapSpawnAfflictionMirror$|MapHasAfflictionEverywhere$|MapBeyondSpawnInitiator(?:PaleFaction|FleshFaction|DemonFaction)?$)/i;
const summaryModifier = /^(MapItemDropQuantityPct|MapItemDropRarityPct|MapPackSizePct)$/i;
const displayModifierText = text => text?.replace(/\[([^\[\]|]+)\|([^\[\]]+)\]/g, "$2").trim();
const threadModifiers = [
  ["Abyss", [
    ["MapFinalAbyssPitSpawnAbyssMiniboss", "Abyssal Consort from a pit"],
    ["MapNumExtraAbysses", "Additional Abysses", "total"],
    ["MapAbyssNumAdditionalPits", "Extra pits per Abyss", "total"],
    ["MapAbyssEliteMonstersPaired", "Rare Abyss monsters spawn in pairs", "chance"],
    ["MapAbyssPitsFireMortars", "Pit mortars spawn Bone Hoarders"],
    ["MapAbyssRaresXAdditionalAbyssMods", "Extra Abyssal modifiers per Rare", "count"]
  ]],
  ["Legion", [
    ["MapLegionLeagueExtraSpawns", "Additional Legion encounters", "total"],
    ["MapLegionLeagueForceGeneral", "Each Legion has a General"],
    ["MapLegionGeneralsSpawnBothGeneralsChancePct", "Both sides have Generals", "chance"],
    ["MapLegionAllChestsWarHoardsChancePct", "War Hoards replace chests", "chance"],
    ["MapLegionSmallCrystalsChancePct", "Purple crystals on the battlefield", "chance"],
    ["MapLegionUpgradeMonsterRankChancePct", "Lower ranks are upgraded", "chance"],
    ["MapLegionInvertedLegionChancePct", "High ranks spawn near the Monolith", "chance"],
    ["MapLegionMultipleBreakoutChancePct", "Multiple breakouts from stasis", "chance"]
  ]],
  ["Bestiary", [
    ["MapAdditionalRedBeasts", "Additional Red Beasts", "total"],
    ["MapBestiaryAreaContainsBlackMorrigan", "The Black Mórrigan"],
    ["MapXBestiaryPacksAreHarvestBeasts", "Red Beasts replaced by Sacred Fauna", "count"],
    ["MapBestiaryBeastsHaveXWispsOfAllTypes", "Beasts gain Wildwood Wisps of all types"],
    ["MapBestiaryEinharBloodExplodeAura", "Enemies near Einhar explode on death"],
    ["MapBestiaryYellowBeastsAreHulks", "Yellow Beasts become Nameless Beasts"],
    ["MapBestiaryBeastsXAdditionalModifiers", "Extra Bestiary modifiers per Red Beast", "count"]
  ]]
];

function renderThreadModifiers(parent, modifiers, usage) {
  if (!usage) return;
  const stats = new Map(modifiers.map(mod => [mod.stat, mod.value]));
  const counts = new Map(usage.items.map(item => [item.name, item.count]));

  const section = element("section", null, "thread-modifiers");
  const groups = element("div", null, "thread-groups");
  let hasAreaTotals = false;
  for (const [name, effects] of threadModifiers) {
    const count = counts.get(`${name} Thread`);
    if (!(count > 0)) continue;
    const list = element("ul", null, "thread-effects");
    for (const [stat, label, kind] of effects) {
      const value = stats.get(stat);
      if (!(value > 0)) continue;
      const entry = element("li");
      entry.append(element("span", label));
      if (kind) {
        const amount = kind === "chance" ? `${number.format(value)}% chance` :
          kind === "total" ? `${number.format(value)} total` : number.format(value);
        entry.append(element("strong", amount));
      }
      if (kind === "total") hasAreaTotals = true;
      list.append(entry);
    }
    if (!list.childElementCount) continue;

    const group = element("section", null, "thread-group");
    const heading = element("h4", name);
    heading.append(element("small", `${number.format(count)} ${count === 1 ? "Thread" : "Threads"}`));
    group.append(heading, list);
    groups.append(group);
  }
  if (!groups.childElementCount) return;

  const title = element("h3", "Thread modifiers");
  title.append(element("small", `${number.format(usage.total)} ${usage.total === 1 ? "Thread" : "Threads"} applied`));
  section.append(title, groups);
  if (hasAreaTotals)
    section.append(element("p", "Totals can include bonuses from the Atlas and other sources.", "muted"));
  parent.append(section);
}

function renderModifiers(parent, run, map, dropBonuses) {
  const basic = state.modMode === "basic";
  const rank = stat => { const order = modOrder.findIndex(pattern => pattern.test(stat)); return order < 0 ? modOrder.length : order; };
  const summarized = new Set(dropBonuses.map(bonus => bonus.stat));
  const visible = basic ? run.modifiers.filter(mod => mod.text && !summaryModifier.test(mod.stat) &&
    !summarized.has(mod.stat) &&
    (mod.visible || mainModifier.test(mod.stat) || rank(mod.stat) < modOrder.length))
    .sort((a, b) => rank(a.stat) - rank(b.stat)) : run.modifiers;

  if (visible.length) {
    const list = element("ul", null, basic ? "modifiers basic" : "modifiers all");
    const seen = new Set();
    for (const mod of visible) {
      const text = displayModifierText(mod.text);
      if (basic && seen.has(text)) continue;
      seen.add(text);
      const danger = basic && /Monsters deal .*Damage|Reflect|reduced maximum Resistances/i.test(text);
      const row = element("li", text || `${mod.stat} · ${mod.value}`, danger ? "danger" : "");
      if (!basic && text) row.append(element("small", `${mod.stat} · ${mod.value}`));
      list.append(row);
    }
    parent.append(list);
  } else {
    parent.append(element("p", basic ? "This map has summary bonuses only." :
      "No modifiers were saved for this map.", "empty"));
  }
  if (!basic) return;

  const bonuses = [["IIQ", map.quantity], ["IIR", map.rarity], ["Pack size", map.packSize],
    ...dropBonuses.map(({ label, percent }) => [label, percent])];
  const list = element("dl", null, "mod-bonuses");
  for (const [label, value] of bonuses) {
    if (value == null) continue;
    const stat = element("div", null, "mod-bonus");
    stat.append(element("dt", label), element("dd", `${number.format(value)}%`));
    list.append(stat);
  }
  parent.append(list);
}

function openEdit(run, item) {
  state.editing = { mapId: run.id, itemId: item.id, name: item.name };
  const edit = run.edits[item.id];
  set("edit-name", item.name);
  set("edit-status", "");
  $("unit").value = edit?.unitDivine ?? "";
  $("unit").placeholder = item.marketUnitDivine == null ? "No market quote" : formatDivineNumber(item.marketUnitDivine);
  $("excluded").checked = edit?.excluded ?? false;
  set("ignore-from-edit", isIgnored(item.name) ? "Restore in all maps" : "Ignore in all maps");
  $("edit-dialog").showModal();
}

function drawChart(progress = 1) {
  const maps = state.statisticsMaps.filter(hasTracking).map(map => ({
    map, value: state.metric === "hour" ? hourly(map) : mapProfit(map)
  })).filter(point => point.value != null).reverse();
  const canvas = $("chart");
  canvas.setAttribute("aria-label", state.profitMode === "net" ? "Chart of net profit by map" : "Chart of pickup value by map");
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  const context = canvas.getContext("2d");
  context.scale(ratio, ratio);
  context.font = "12px Segoe UI, sans-serif";
  $("chart-empty").hidden = maps.length > 0;
  set("chart-empty", state.profitMode === "net" && state.statisticsMaps.some(hasTracking)
    ? "Net profit is unavailable for this selection."
    : "Record a map to see its pickup value here.");
  set("chart-start", maps.length ? dateLabel(maps[0].map.startedUtc) : "");
  set("chart-end", maps.length > 1 ? dateLabel(maps.at(-1).map.startedUtc) : "");
  chartPoints = [];
  if (!maps.length || width < 75) return;

  const left = 55;
  const top = 15;
  const right = width - 11;
  const bottom = height - 12;
  const max = maps.reduce((result, point) => Math.max(result, point.value), 0);
  const min = maps.reduce((result, point) => Math.min(result, point.value), 0);
  const scale = 10 ** Math.floor(Math.log10(Math.max(max, -min) || 1));
  const ceiling = max === 0 && min === 0 ? 1 : Math.ceil(max / scale) * scale;
  const floor = Math.floor(min / scale) * scale;
  const yAt = value => top + (bottom - top) * (ceiling - value) / (ceiling - floor);
  const zero = yAt(0);
  context.lineWidth = 1;
  context.strokeStyle = "#344035";
  context.fillStyle = "#aeb9aa";
  context.textAlign = "right";
  for (let i = 0; i <= 4; i++) {
    const y = top + (bottom - top) * i / 4;
    context.beginPath();
    context.moveTo(left, y);
    context.lineTo(right, y);
    context.stroke();
    context.fillText(formatDivineNumber(ceiling - (ceiling - floor) * i / 4), left - 8, y + 4, left - 10);
  }

  chartPoints = maps.map(({ map, value }, i) => ({
    map, value, x: maps.length > 1 ? left + (right - left) * i / (maps.length - 1) : (left + right) / 2,
    y: yAt(value * progress)
  }));
  if (chartPoints.length > 1) {
    const gradient = context.createLinearGradient(0, top, 0, bottom);
    gradient.addColorStop(0, "#abc29870");
    gradient.addColorStop(1, "#abc29808");
    context.beginPath();
    chartPoints.forEach(({ x, y }, i) => i ? context.lineTo(x, y) : context.moveTo(x, y));
    context.lineTo(chartPoints.at(-1).x, zero);
    context.lineTo(chartPoints[0].x, zero);
    context.closePath();
    context.fillStyle = gradient;
    context.fill();
    context.beginPath();
    chartPoints.forEach(({ x, y }, i) => i ? context.lineTo(x, y) : context.moveTo(x, y));
    context.strokeStyle = "#b6d3a2";
    context.lineWidth = 2;
    context.stroke();
  }
  for (const [i, point] of chartPoints.entries()) {
    const emphasized = point.map.id === state.selected || i === hoverIndex;
    if (!emphasized && chartPoints.length > 34) continue;
    context.beginPath();
    context.arc(point.x, point.y, emphasized ? 5 : 2.5, 0, Math.PI * 2);
    context.fillStyle = emphasized ? "#ebc684" : "#c8dabb";
    context.fill();
  }
  const point = chartPoints[hoverIndex];
  if (point) {
    const text = `${point.map.name} · ${divines(point.value)}`;
    const labelWidth = Math.min(context.measureText(text).width + 16, width - 12);
    const labelX = Math.min(Math.max(6, point.x - labelWidth / 2), width - labelWidth - 6);
    const labelY = point.y > 45 ? point.y - 33 : point.y + 9;
    context.fillStyle = "#29372a";
    context.fillRect(labelX, labelY, labelWidth, 25);
    context.fillStyle = "#f2efe4";
    context.textAlign = "left";
    context.fillText(text, labelX + 8, labelY + 17, labelWidth - 16);
  }
}

function animateChart() {
  cancelAnimationFrame(chartAnimation);
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return drawChart();
  const start = performance.now();
  function frame(now) {
    const progress = Math.min((now - start) / 320, 1);
    drawChart(1 - (1 - progress) ** 3);
    if (progress < 1) chartAnimation = requestAnimationFrame(frame);
  }
  chartAnimation = requestAnimationFrame(frame);
}

$("map-encounter").append(...Object.entries(zoneLabels).sort(([, a], [, b]) => a.localeCompare(b))
  .map(([zone, label]) => new Option(label, zone)));
for (const id of ["limit", "league", "status", "search", "map-encounter"]) {
  $(id).addEventListener(id === "search" ? "input" : "change", () => {
    hoverIndex = -1;
    state.visible = 25;
    latestRequest++;
    renderOverview();
    if (state.selected && !state.detail) loadDetail(state.selected);
  });
}
$("more").addEventListener("click", () => { state.visible += 25; renderMapList(); });
$("map-sort").addEventListener("change", () => { state.visible = 25; renderMapList(); });
for (const button of $("profit-mode").querySelectorAll("[data-profit-mode]")) {
  button.addEventListener("click", () => {
    if (state.profitMode === button.dataset.profitMode) return;
    state.profitMode = button.dataset.profitMode;
    try { localStorage.setItem(profitModeKey, state.profitMode); } catch {}
    hoverIndex = -1;
    renderOverview();
    if (state.detail) renderDetail();
  });
}
async function refreshPricingStatus() {
  const status = await request("/api/pricing-status").catch(() => null);
  set("pricing-source-status", status?.message ?? "Pricing status unavailable.");
  $("pricing-source-install").hidden = !status?.pluginMissing;
  $("price-archive").hidden = !status?.snapshotDirectory;
  set("price-archive-path", status?.snapshotDirectory ?? "");
}

$("open-cost-settings").addEventListener("click", () => {
  $("default-map-price").value = defaultMapPrice();
  set("cost-settings-status", "");
  $("cost-settings-dialog").showModal();
  refreshPricingStatus();
});
$("open-widget-settings").addEventListener("click", openWidgetSettings);
$("widget-settings-form").addEventListener("input", renderWidgetPreview);
$("widget-settings-form").addEventListener("submit", async event => {
  event.preventDefault();
  const settings = {
    enabled: $("widget-enabled").checked,
    showOutsideMaps: $("widget-outside").checked,
    showOverPanels: $("widget-panels").checked,
    compact: $("widget-compact").checked,
    x: $("widget-x").valueAsNumber,
    y: $("widget-y").valueAsNumber,
    opacity: $("widget-opacity").valueAsNumber,
    hiddenRows: [...$("widget-rows").querySelectorAll("input:not(:checked)")].map(input => input.value),
    customItems: $("widget-items").value.split(/\r?\n/).map(name => name.trim().replaceAll("’", "'")).filter(Boolean)
  };
  event.submitter.disabled = true;
  set("widget-settings-status", "");
  try {
    await request("/api/settings/widget", settings);
    $("widget-settings-dialog").close();
    await refresh(true);
  } catch {
    set("widget-settings-status", "Could not save widget settings.");
  } finally {
    event.submitter.disabled = false;
  }
});
for (const button of document.querySelectorAll("[data-close-dialog]"))
  button.addEventListener("click", () => $(button.dataset.closeDialog).close());
$("open-delete-map").addEventListener("click", () => {
  const map = state.maps.find(entry => entry.id === state.detail?.id);
  if (!map) return;
  const dialog = $("delete-map-dialog");
  dialog.dataset.recordId = map.id;
  set("delete-map-record", `${map.name} · ${dateLabel(map.startedUtc)}`);
  set("delete-map-status", "");
  dialog.showModal();
});
$("open-delete-session").addEventListener("click", () => {
  const session = selectedHistorySession();
  if (!session) return;
  const dialog = $("delete-session-dialog");
  dialog.dataset.recordId = session.id;
  const count = state.maps.filter(map => map.sessionId === session.id).length;
  set("delete-session-record", `${session.name ?? dateLabel(session.startedUtc)} · ${number.format(count)} ${count === 1 ? "map" : "maps"}`);
  set("delete-session-status", "");
  dialog.showModal();
});
for (const kind of ["map", "session"]) {
  const dialog = $(`delete-${kind}-dialog`);
  const confirm = $(`confirm-delete-${kind}`);
  const cancel = $(`cancel-delete-${kind}`);
  dialog.addEventListener("cancel", event => {
    if (confirm.disabled) event.preventDefault();
  });
  $(`delete-${kind}-form`).addEventListener("submit", async event => {
    event.preventDefault();
    if (confirm.disabled) return;
    confirm.disabled = cancel.disabled = true;
    set(`delete-${kind}-status`, "");
    try {
      const id = dialog.dataset.recordId;
      if (kind === "session") {
        await request("/api/sessions/delete", id);
        state.sessionView = "active";
        state.visible = 25;
      } else {
        await request(`/api/maps/${id}/delete`, true);
      }
      dialog.close();
      hoverIndex = -1;
      await refresh(true);
    } catch {
      set(`delete-${kind}-status`, `Could not delete this ${kind}.`);
    } finally {
      confirm.disabled = cancel.disabled = false;
    }
  });
}
$("cost-settings-form").addEventListener("submit", async event => {
  event.preventDefault();
  const costs = { mapPriceDivine: $("default-map-price").valueAsNumber };
  event.submitter.disabled = true;
  set("cost-settings-status", "");
  try {
    await request("/api/settings/map-costs", costs);
    $("cost-settings-dialog").close();
    await refresh(true);
  } catch {
    set("cost-settings-status", "Could not save map cost defaults.");
  } finally {
    event.submitter.disabled = false;
  }
});
$("map-price-form").addEventListener("submit", async event => {
  event.preventDefault();
  const input = $("map-price");
  const price = input.value === "" ? null : input.valueAsNumber;
  event.submitter.disabled = true;
  set("map-price-status", "");
  try {
    await request(`/api/maps/${state.costMapId}/map-price`, price);
    $("map-price-dialog").close();
    await refresh(true);
  } catch {
    set("map-price-status", "Could not save the map price.");
  } finally {
    event.submitter.disabled = false;
  }
});
$("drop-averages-form").addEventListener("submit", event => {
  event.preventDefault();
  const input = $("drop-average-name");
  const name = input.value.trim().replaceAll("’", "'");
  if (name && !state.averageItems.some(item => itemKey(item) === itemKey(name)))
    saveAverageItems([...state.averageItems, name]);
  input.value = "";
});
const averageCards = $("drop-average-items");
averageCards.addEventListener("pointerdown", event => {
  if (averageDrag || !event.isPrimary || event.button !== 0 || event.target.closest(".drop-average-remove")) return;
  const card = event.target.closest("[data-average-key]");
  if (!card) return;
  averageDrag = { card, pointerId: event.pointerId, x: event.clientX, y: event.clientY, moving: false };
  averageCards.setPointerCapture(event.pointerId);
});
averageCards.addEventListener("pointermove", event => {
  if (averageDrag?.pointerId !== event.pointerId) return;
  const { card, x, y, moving } = averageDrag;
  const dx = event.clientX - x;
  const dy = event.clientY - y;
  if (!moving && Math.hypot(dx, dy) < 5) return;
  averageDrag.moving = true;
  card.classList.add("drop-average-dragging");
  card.style.transform = `translate(${dx}px, ${dy}px)`;
  const target = document.elementFromPoint(event.clientX, event.clientY)?.closest("[data-average-key]");
  markAverageDropTarget(target && averageCards.contains(target) ? target : null);
});
for (const event of ["pointerup", "pointercancel", "lostpointercapture"])
  averageCards.addEventListener(event, finishAverageDrag);
averageCards.addEventListener("keydown", event => {
  const card = event.target;
  if (!card.hasAttribute("data-average-key")) return;
  const backwards = event.key === "ArrowLeft" || event.key === "ArrowUp";
  const forwards = event.key === "ArrowRight" || event.key === "ArrowDown";
  if (!backwards && !forwards) return;
  event.preventDefault();
  const target = backwards ? card.previousElementSibling : card.nextElementSibling;
  if (!target) return;
  moveAverageCard(card, target);
  card.focus({ preventScroll: true });
});
$("session-more").addEventListener("click", () => { state.sessionExpanded = !state.sessionExpanded; renderSession(); });
for (const button of document.querySelectorAll("[data-session-amount]")) {
  button.addEventListener("click", () => {
    if (state.sessionAmount === button.dataset.sessionAmount) return;
    state.sessionAmount = button.dataset.sessionAmount;
    $("session-loot").scrollTop = 0;
    renderSession();
  });
}
for (const button of document.querySelectorAll("[data-pickup-scope]")) {
  button.addEventListener("click", () => {
    if (state.pickupScope === button.dataset.pickupScope) return;
    state.pickupScope = button.dataset.pickupScope;
    refresh(true);
  });
}
$("session-view").addEventListener("change", event => {
  state.sessionView = event.target.value;
  state.visible = 25;
  set("session-control-status", "");
  refresh(true);
});

async function changeSessionSetting(action, errorMessage) {
  const controls = ["new-session", "open-rename-session", "open-delete-session", "resume-session", "session-view", "unique-prices"].map($);
  for (const control of controls) control.disabled = true;
  set("session-control-status", "");
  try {
    await action();
    await refresh(true);
  } catch {
    if (state.sessions) renderSessionControls();
    set("session-control-status", errorMessage);
  } finally {
    for (const control of controls) control.disabled = false;
  }
}
$("new-session").addEventListener("click", () => changeSessionSetting(async () => {
  await request("/api/sessions", {});
  state.sessionView = "active";
  state.visible = 25;
}, "Could not start a new session."));
$("resume-session").addEventListener("click", () => changeSessionSetting(async () => {
  await request("/api/sessions/active", state.sessionView);
  state.sessionView = "active";
  state.visible = 25;
}, "Could not resume this session."));
$("open-rename-session").addEventListener("click", () => {
  const session = selectedHistorySession();
  if (!session) return;
  const dialog = $("rename-session-dialog");
  const input = $("session-name");
  dialog.dataset.sessionId = session.id;
  input.value = session.name ?? "";
  input.placeholder = dateLabel(session.startedUtc);
  set("rename-session-status", "");
  dialog.showModal();
  input.select();
});
$("rename-session-form").addEventListener("submit", async event => {
  event.preventDefault();
  const dialog = $("rename-session-dialog");
  const sessionId = dialog.dataset.sessionId;
  const name = $("session-name").value.trim();
  if (!name) {
    set("rename-session-status", "Enter a session name.");
    return;
  }
  event.submitter.disabled = true;
  set("rename-session-status", "");
  try {
    await request("/api/sessions/rename", { sessionId, name });
    if (dialog.open && dialog.dataset.sessionId === sessionId) dialog.close();
    await refresh();
  } catch {
    if (dialog.dataset.sessionId === sessionId) set("rename-session-status", "Could not rename this session.");
  } finally {
    event.submitter.disabled = false;
  }
});
$("unique-prices").addEventListener("change", event => {
  const include = event.target.checked;
  changeSessionSetting(() => request("/api/settings/unique-prices", include), "Could not save the pricing setting.");
});
$("open-ignore-list").addEventListener("click", () => {
  set("ignore-status", "");
  renderIgnoreList(true);
  const items = [...new Set((state.detail?.loot ?? []).map(item => item.name))];
  $("ignore-suggestions").replaceChildren(...items.map(name => new Option(name, name)));
  $("ignore-dialog").showModal();
});
$("close-ignore-list").addEventListener("click", () => $("ignore-dialog").close());
$("ignore-threshold-form").addEventListener("submit", async event => {
  event.preventDefault();
  const field = $("ignore-price-threshold");
  event.submitter.disabled = true;
  set("ignore-status", "");
  try {
    await request("/api/settings/minimum-item-price", field.value === "" ? null : {
      value: field.valueAsNumber, currency: $("minimum-price-currency").value
    });
    await refresh(true);
  } catch {
    set("ignore-status", "Could not save the price cutoff.");
  } finally {
    event.submitter.disabled = false;
  }
});
$("ignore-form").addEventListener("submit", async event => {
  event.preventDefault();
  const input = $("ignore-name");
  const name = input.value.trim();
  if (name && await saveIgnoredItem(name, true, event.submitter, "ignore-status"))
    input.value = "";
});
$("ignore-from-edit").addEventListener("click", async event => {
  const { name } = state.editing;
  if (await saveIgnoredItem(name, !isIgnored(name), event.target, "edit-status"))
    $("edit-dialog").close();
});
$("loot-search").addEventListener("input", renderDetail);
for (const id of ["unique-scope", "unique-type", "unique-tier", "unique-sort"])
  $(id).addEventListener("change", renderUniques);
$("reset-loot-sort").addEventListener("click", () => {
  state.lootSort = null;
  renderDetail();
  $("loot-search").focus({ preventScroll: true });
});
for (const button of document.querySelectorAll("[data-tab], [data-metric], [data-mod-mode]")) {
  button.addEventListener("click", () => {
    for (const sibling of button.parentElement.children) sibling.setAttribute("aria-pressed", String(sibling === button));
    if (button.dataset.tab) { state.tab = button.dataset.tab; renderDetail(); }
    else if (button.dataset.modMode) { state.modMode = button.dataset.modMode; renderDetail(); }
    else { state.metric = button.dataset.metric; hoverIndex = -1; animateChart(); }
  });
}
$("failure").addEventListener("change", async event => {
  const mapId = state.selected;
  const value = event.target.value;
  event.target.disabled = true;
  try {
    await request(`/api/maps/${mapId}/failure`, value === "auto" ? null : value === "yes");
    await refresh(true);
  } catch {
    if (mapId === state.selected) {
      renderDetail();
      set("save-status", "Could not save the map status.");
    }
  } finally { event.target.disabled = false; }
});
$("map-include-statistics").addEventListener("change", async event => {
  const mapId = state.selected;
  const input = event.currentTarget;
  const excluded = !input.checked;
  input.disabled = true;
  try {
    await request(`/api/maps/${mapId}/statistics-exclusion`, excluded);
    hoverIndex = -1;
    await refresh(true);
  } catch {
    if (mapId === state.selected) {
      renderDetail();
      set("save-status", "Could not update map inclusion.");
    }
  } finally {
    input.disabled = false;
  }
});
$("toggle-map-favorite").addEventListener("click", async event => {
  const map = state.maps.find(entry => entry.id === state.detail?.id);
  if (!map) return;
  const button = event.currentTarget;
  button.disabled = true;
  set("save-status", "");
  try {
    await request(`/api/maps/${map.id}/favorite`, !map.favorite);
    await refresh(true);
  } catch {
    if (map.id === state.selected) set("save-status", "Could not save this favorite.");
  } finally {
    button.disabled = false;
  }
});
for (const id of ["close-edit", "cancel-edit"]) $(id).addEventListener("click", () => $("edit-dialog").close());
$("edit-form").addEventListener("submit", async event => {
  event.preventDefault();
  const { mapId, itemId } = state.editing;
  const unit = $("unit").value;
  const submit = event.submitter;
  submit.disabled = true;
  try {
    await request(`/api/maps/${mapId}/loot/${itemId}`, { excluded: $("excluded").checked, unitDivine: unit === "" ? null : Number(unit) });
    $("edit-dialog").close();
    await refresh(true);
  } catch { set("edit-status", "Could not save the valuation."); }
  finally { submit.disabled = false; }
});
$("chart").addEventListener("pointermove", event => {
  let closest = -1;
  let distance = 24;
  chartPoints.forEach((point, i) => {
    const current = Math.abs(point.x - event.offsetX);
    if (current < distance) { distance = current; closest = i; }
  });
  if (closest !== hoverIndex) { hoverIndex = closest; drawChart(); }
});
$("chart").addEventListener("pointerleave", () => { hoverIndex = -1; drawChart(); });
$("chart").addEventListener("click", () => {
  const point = chartPoints[hoverIndex];
  if (point) selectMap(point.map.id);
});
new ResizeObserver(() => drawChart()).observe($("plot"));
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
setInterval(() => { if (!document.hidden) refresh(); }, 4000);
refresh(true).then(animateChart);
