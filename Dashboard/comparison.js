"use strict";

(() => {
  const sides = ["a", "b"];
  const view = $("comparison-view");
  const selected = sides.map(side => $(`compare-${side}`));
  const chart = $("compare-chart");
  const colors = sides.map(side => getComputedStyle(view).getPropertyValue(`--session-${side}`).trim());
  let summaries = [];
  const requests = {};
  let sessionAmount = "count";
  let sessionExpanded = false;
  let uniqueSort = null;
  let chartIndex = null;
  let chartScale = null;
  let opened = false;

  const isCompleted = map => includedInStatistics(map) && hasTracking(map) && map.state !== "active" && map.state !== "paused";
  const formatCount = value => value == null ? "—" : formatDivineNumber(value);
  const formatPercent = value => value == null ? "—" : `${formatDivineNumber(value)}%`;
  const formatTime = value => value == null ? "—" : `${value < 0 ? "−" : ""}${duration(Math.round(Math.abs(value)))}`;
  const dropBonusBadges = new Map([["More Currency", "CUR"], ["More Maps", "MAP"], ["More Threads", "THR"],
    ["More Divination Cards", "DIV"], ["More Item Rarity", "IIR"], ["Increased Atlas Drops", "ATL"], ["Increased Maps", "MAP"]]);

  function refreshComparison() {
    if (view.hidden) return;
    const activeId = state.sessions?.activeId;
    const sessions = (state.sessions?.sessions ?? []).slice().reverse();
    const bySession = new Map(sessions.map(session => [session.id, []]));
    for (const map of state.maps.slice().reverse()) {
      if (isCompleted(map)) bySession.get(map.sessionId)?.push(map);
    }

    const labels = sessions.map(session => `${session.id === activeId ? "Current · " : ""}${session.name ?? dateLabel(session.startedUtc)}`);
    for (const select of selected) {
      if (select.options.length !== sessions.length || sessions.some((session, index) =>
        select.options[index].value !== session.id || select.options[index].textContent !== labels[index])) {
        const value = select.value;
        select.replaceChildren(...sessions.map((session, index) => new Option(labels[index], session.id)));
        select.value = value;
      }
    }
    if (!selected[0].value) {
      const preferred = state.sessionView === "active" ? activeId : state.sessionView;
      selected[0].value = bySession.has(preferred) ? preferred : activeId ?? "";
    }
    if (!selected[1].value) {
      const others = sessions.filter(session => session.id !== selected[0].value);
      selected[1].value = (others.find(session => bySession.get(session.id).length) ?? others[0])?.id ?? "";
    }

    $("compare-swap").disabled = sessions.length < 2 || selected[0].value === selected[1].value;
    const valid = sessions.length >= 2 && selected.every(select => select.value) && selected[0].value !== selected[1].value;
    $("compare-empty").hidden = valid;
    $("compare-results").hidden = !valid;
    if (!valid) {
      set("compare-empty", !state.sessions ? "Loading sessions…" : sessions.length < 2
        ? "Save a second session to compare your maps." : "Choose two different sessions.");
      return;
    }

    const mode = $("compare-profit").value;
    const scope = $("compare-pickup").value;
    summaries = selected.map(select => summarize(select.value, bySession.get(select.value), mode, scope));
    renderTotals(mode);
    renderMapAverages();
    drawComparisonChart();
    loadComparisons(scope);
  }

  function summarize(id, maps, mode, scope) {
    let profit = 0, valued = 0, timedProfit = 0, timedSeconds = 0;
    let divines = 0, uniques = 0, time = 0, timedMaps = 0;
    let quantity = 0, rarity = 0, packSize = 0;
    const points = maps.map(map => {
      const value = mapProfit(map, mode, scope);
      if (value != null) {
        profit += value;
        valued++;
        if (map.seconds > 0) {
          timedProfit += value;
          timedSeconds += map.seconds;
        }
      }
      divines += map.divineOrbsDropped;
      uniques += map.uniquesDropped;
      quantity += map.quantity;
      rarity += map.rarity;
      packSize += map.packSize;
      if (map.seconds > 0) {
        time += map.seconds;
        timedMaps++;
      }
      return { profit: value, cumulative: value == null ? null : profit, divines: map.divineOrbsDropped, uniques: map.uniquesDropped };
    });
    return { id, maps, points, valued, timedMaps, profit: valued ? profit : null, hour: timedSeconds ? timedProfit * 3600 / timedSeconds : null,
      averageProfit: valued ? profit / valued : null, divines, averageDivines: maps.length ? divines / maps.length : null,
      uniques, averageUniques: maps.length ? uniques / maps.length : null,
      averageQuantity: maps.length ? quantity / maps.length : null, averageRarity: maps.length ? rarity / maps.length : null,
      averagePackSize: maps.length ? packSize / maps.length : null, averageTime: timedMaps ? time / timedMaps : null };
  }

  function appendValues(row, a, b, format, zeroDim = false) {
    const delta = a == null || b == null ? null : a - b;
    for (const [index, value] of [a, b].entries()) {
      const cell = row.insertCell();
      cell.className = "numeric";
      if (delta != null && Math.sign(delta) === (index === 0 ? 1 : -1)) cell.classList.add(`compare-${sides[index]}`, "compare-best");
      if (zeroDim && value === 0) cell.classList.add("compare-zero");
      cell.textContent = format(value);
    }
    const change = row.insertCell();
    change.className = "numeric";
    if (delta) change.classList.add(delta > 0 ? "compare-a" : "compare-b");
    else change.classList.add("compare-zero");
    change.textContent = `${delta > 0 ? "+" : ""}${format(delta)}`;
  }

  function renderTotals(mode) {
    const labels = [
      ["Recorded maps", summary => summary.maps.length, formatCount],
      [mode === "net" ? "Net profit" : "Gross pickup value", summary => summary.profit, divines],
      ["Value / hour", summary => summary.hour, divines],
      ["Value / map", summary => summary.averageProfit, divines],
      ["Divine Orbs dropped", summary => summary.divines, formatCount],
      ["Divine Orbs / map", summary => summary.averageDivines, formatCount],
      ["Uniques dropped", summary => summary.uniques, formatCount],
      ["Uniques / map", summary => summary.averageUniques, formatCount]
    ];
    const target = $("compare-totals");
    target.replaceChildren();
    for (const [label, read, format] of labels) {
      const row = target.insertRow();
      const heading = element("th", label);
      heading.scope = "row";
      row.append(heading);
      appendValues(row, read(summaries[0]), read(summaries[1]), format);
    }
    const coverage = summaries.map((summary, index) => summary.valued < summary.maps.length
      ? `${sides[index].toUpperCase()} · ${number.format(summary.valued)} of ${number.format(summary.maps.length)} maps with complete costs` : null).filter(Boolean);
    $("compare-valuation-note").hidden = mode !== "net" || !coverage.length;
    set("compare-valuation-note", coverage.join(" | "));
  }

  function drawComparisonChart() {
    if (view.hidden || $("compare-results").hidden) return;
    const metric = $("compare-metric").value;
    const format = metric === "profit" || metric === "cumulative" ? divines : formatCount;
    const points = summaries.map(summary => summary.points.map(point => point[metric]));
    const length = Math.max(...points.map(series => series.length));
    const values = points.flat().filter(Number.isFinite);
    const width = chart.clientWidth;
    const height = chart.clientHeight;
    const ratio = devicePixelRatio || 1;
    if (chart.width !== Math.round(width * ratio) || chart.height !== Math.round(height * ratio)) {
      chart.width = Math.round(width * ratio);
      chart.height = Math.round(height * ratio);
    }
    const context = chart.getContext("2d");
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    const tooltip = $("compare-tooltip");
    tooltip.hidden = true;
    $("compare-chart-empty").hidden = values.length > 0;
    chartScale = null;
    if (!values.length || width < 105 || height < 80) return;

    const min = values.reduce((result, value) => Math.min(result, value), 0);
    const max = values.reduce((result, value) => Math.max(result, value), 0);
    const span = max - min;
    const magnitude = span ? 10 ** Math.floor(Math.log10(span / 4)) : 1;
    const step = (span / (4 * magnitude) > 5 ? 10 : span / (4 * magnitude) > 2 ? 5 : span / (4 * magnitude) > 1 ? 2 : 1) * magnitude;
    const floor = Math.floor(min / step) * step;
    const ceiling = span ? Math.ceil(max / step) * step : step;
    const left = 58, right = width - 21, top = 16, bottom = height - 25;
    const x = index => length === 1 ? (left + right) / 2 : left + (right - left) * index / (length - 1);
    const y = value => bottom - (bottom - top) * (value - floor) / (ceiling - floor);
    chartScale = { left, right, length };
    context.font = "12px Segoe UI, sans-serif";
    context.textAlign = "right";
    context.fillStyle = "#adb7aa";
    for (let tick = Math.round(floor / step); tick <= Math.round(ceiling / step); tick++) {
      const ordinate = y(tick * step);
      context.lineWidth = 1;
      context.strokeStyle = tick === 0 ? "#5b695d" : "#313b34";
      context.beginPath();
      context.moveTo(left, ordinate);
      context.lineTo(right, ordinate);
      context.stroke();
      context.fillText(formatDivineNumber(tick * step), left - 8, ordinate + 4, left - 10);
    }
    context.textAlign = "center";
    const xMarks = Math.min(length, width < 410 ? 4 : 6);
    for (let i = 0; i < xMarks; i++) {
      const index = xMarks === 1 ? 0 : Math.round((length - 1) * i / (xMarks - 1));
      context.fillText(number.format(index + 1), x(index), height - 6);
    }
    for (const [side, series] of points.entries()) {
      context.strokeStyle = context.fillStyle = colors[side];
      context.lineWidth = 2;
      context.setLineDash(side === 1 ? [5, 4] : []);
      context.beginPath();
      for (const [index, value] of series.entries()) {
        if (!Number.isFinite(value)) continue;
        if (index > 0 && Number.isFinite(series[index - 1])) context.lineTo(x(index), y(value));
        else context.moveTo(x(index), y(value));
      }
      context.stroke();
      context.setLineDash([]);
      for (const [index, value] of series.entries()) {
        if (!Number.isFinite(value) || series.length > 24 && (Number.isFinite(series[index - 1]) || Number.isFinite(series[index + 1]))) continue;
        context.beginPath();
        context.arc(x(index), y(value), 2.3, 0, Math.PI * 2);
        context.fill();
      }
    }

    if (chartIndex == null || chartIndex >= length) return;
    context.lineWidth = 1;
    context.strokeStyle = "#899588";
    context.beginPath();
    context.moveTo(x(chartIndex), top);
    context.lineTo(x(chartIndex), bottom);
    context.stroke();
    tooltip.replaceChildren(element("strong", `Map ${number.format(chartIndex + 1)}`), ...points.map((series, index) =>
      element("div", `${sides[index].toUpperCase()} · ${format(series[chartIndex])}`, `compare-${sides[index]}`)));
    tooltip.hidden = false;
  }

  function loadComparisons(scope) {
    const mapsKey = summaries.map(({ id, maps }) => [id, maps.map(map => [map.id, map.updatedUtc])]).sort();
    const valuation = [state.sessions?.includeUniquePrices, state.sessions?.minimumPrice, state.sessions?.ignoredItems];
    // Match History's Session drops, including the live map when present.
    const sessionKey = state.maps.filter(map => includedInStatistics(map) && summaries.some(summary => summary.id === map.sessionId)).map(map =>
      [map.id, map.updatedUtc, map.profitDivine, map.unpricedQuantity, map.leftDivine, map.leftUnpricedQuantity]);
    set("compare-session-title", scope === "personal" ? "Your pickups" : scope === "party" ? "Party pickups (est.)" : "Session drops");
    loadPair("session", JSON.stringify([mapsKey.map(([id]) => id), sessionKey, valuation, scope]),
      ({ id }) => request(`/api/session?session=${encodeURIComponent(id)}&pickup=${scope}`), renderSessionComparison);
    loadPair("averages", JSON.stringify([mapsKey, state.averageItems, state.averageOrder]), ({ maps }) => maps.length
      ? request("/api/drop-averages", { mapIds: maps.map(map => map.id), itemNames: state.averageItems }) : null, renderAverageComparison);
    loadPair("uniques", JSON.stringify(mapsKey), ({ maps }) => maps.length
      ? request("/api/unique-drops", { mapIds: maps.map(map => map.id) }) : { items: [] }, renderUniqueComparison);
  }

  function loadPair(source, key, fetchSide, render) {
    const first = summaries[0].id;
    if (requests[source]?.key === key && requests[source].first === first && requests[source].rendered) return;
    if (requests[source]?.key !== key) {
      const pending = { key, first, byId: null, failed: false, rendered: false };
      requests[source] = pending;
      Promise.all(summaries.map(async summary => [summary.id, await fetchSide(summary)])).then(results => {
        pending.byId = new Map(results);
        pending.rendered = false;
        if (pending === requests[source]) render();
      }, () => {
        pending.failed = true;
        pending.rendered = false;
        if (pending === requests[source]) render();
      });
    } else requests[source].first = first;
    render();
  }

  function pairResults(source, name, status, target) {
    if (view.hidden || $("compare-results").hidden || !requests[source]) return null;
    const pending = requests[source];
    pending.rendered = true;
    if (!pending.byId) {
      target.replaceChildren();
      set(status, pending.failed ? `Could not load ${name}.` : `Loading ${name}…`);
      if (pending.failed) {
        const retry = element("button", "Retry", "button");
        retry.type = "button";
        retry.addEventListener("click", () => { delete requests[source]; refreshComparison(); });
        target.append(retry);
      }
      return null;
    }
    return summaries.map(summary => pending.byId.get(summary.id));
  }

  function shareCard(item, a, b, overall, format, overallLabel, overallCaption) {
    const row = element("li", null, "compare-share-row");
    const title = element("div", null, "compare-share-title");
    const total = element("strong", format(overall), "compare-share-total");
    total.prepend(element("small", overallCaption));
    total.title = overallLabel;
    total.setAttribute("aria-label", `${overallLabel} ${format(overall)}`);
    title.append(itemIcon(item, item.badge), element("span", item.name), total);
    row.append(title, shareBar(a, b, format));
    return row;
  }

  function shareBar(a, b, format) {
    const footer = element("div", null, "compare-share-values");
    const bar = element("div", null, "compare-share-bar");
    bar.setAttribute("aria-hidden", "true");
    const ratio = Number.isFinite(a) && Number.isFinite(b) && a >= 0 && b >= 0 && a + b > 0 ? a / (a + b) : null;
    const tenths = ratio == null ? null : Math.round(ratio * 1000);
    const percent = tenths == null ? [null, null] : [tenths / 10, (1000 - tenths) / 10];
    const labels = [a, b].map((value, side) => {
      const name = sides[side].toUpperCase();
      const share = formatPercent(percent[side]);
      const label = element("span", format ? `${name} ${format(value)}` : share, `compare-${sides[side]}`);
      if (!format) label.setAttribute("aria-label", `Session ${name} share ${share}`);
      if (format && percent[side] != null) {
        const caption = element("small", share, "compare-share-percent");
        caption.title = `Session ${name} share of A and B combined`;
        label.append(caption);
      }
      return label;
    });
    if (ratio != null) {
      bar.dataset.filled = "";
      bar.style.setProperty("--share-a", `${100 * ratio}%`);
    }
    footer.append(labels[0], bar, labels[1]);
    return footer;
  }

  function renderSessionComparison() {
    const target = $("compare-session-content");
    const status = "compare-session-status";
    const button = $("compare-session-more");
    const results = pairResults("session", "session drops", status, target);
    if (!results) { button.hidden = true; return; }
    const combined = new Map();
    for (const [side, result] of results.entries()) {
      for (const item of result.items) {
        const key = `${item.category}\0${itemKey(item.name)}`;
        const row = combined.get(key) ?? { name: item.name, icon: null, items: [null, null] };
        row.items[side] = item;
        row.icon ??= item.icon;
        combined.set(key, row);
      }
    }
    const sum = values => values.every(value => value == null) ? null : values.reduce((total, value) => total + (value ?? 0), 0);
    const query = itemKey($("compare-session-search").value.trim());
    const rows = [...combined.values()].filter(item => itemKey(item.name).includes(query)).map(item => {
      const quantities = item.items.map(data => data?.count ?? 0);
      const prices = item.items.map(data => data ? data.valueDivine : 0);
      const priced = item.items.some(data => data?.valueDivine != null) ? sum(prices) : null;
      return { ...item, quantities, prices, priced, count: sum(quantities) };
    }).sort((a, b) => compareLootValues(a.priced, b.priced, "descending") || b.count - a.count || a.name.localeCompare(b.name));

    const scroll = target.firstElementChild?.scrollTop ?? 0;
    const canExpand = rows.length > sessionPreviewSize;
    const expanded = canExpand && sessionExpanded;
    button.hidden = !canExpand;
    button.setAttribute("aria-expanded", String(expanded));
    set("compare-session-more", expanded ? "Show less" : `Show all (${number.format(rows.length)})`);
    set(status, rows.length ? "" : "No session drops match this selection.");
    const list = element("ul", null, expanded ? "compare-share-list expanded" : "compare-share-list");
    for (const item of expanded ? rows : rows.slice(0, sessionPreviewSize)) {
      const useValue = sessionAmount === "divines";
      const [a, b] = useValue ? item.prices : item.quantities;
      list.append(shareCard(item, a, b, useValue ? item.priced : item.count, useValue ? divines : formatCount,
        useValue ? "Combined priced value" : "Combined total", "Total"));
    }
    target.replaceChildren(...(rows.length ? [list] : []));
    if (expanded) list.scrollTop = scroll;
  }

  function renderAverageComparison() {
    const target = $("compare-averages-content");
    const status = "compare-averages-status";
    const results = pairResults("averages", "map averages", status, target);
    if (!results) return;
    set(status, `A · ${number.format(results[0]?.maps ?? 0)} maps | B · ${number.format(results[1]?.maps ?? 0)} maps`);
    const columns = results.map(result => dropAverageMetrics(result));
    const list = element("ul", null, "compare-share-list");
    for (const [index, [key, name, countA, mapsA, custom]] of columns[0].entries()) {
      const [, , countB, mapsB] = columns[1][index];
      if (key === "stat:kills" && !mapsA && !mapsB) continue;
      const a = mapsA ? countA / mapsA : null;
      const b = mapsB ? countB / mapsB : null;
      const maps = (mapsA ?? 0) + (mapsB ?? 0);
      const overall = maps ? ((countA ?? 0) + (countB ?? 0)) / maps : null;
      const iconKey = custom ? `item:${name}` : key;
      const icon = results[0]?.icons?.[iconKey] ?? results[1]?.icons?.[iconKey];
      list.append(shareCard({ name, icon }, a, b, overall, formatCount, "Combined average per map", "Avg"));
    }
    target.replaceChildren(list);
  }

  function renderMapAverages() {
    const list = element("ul", null, "compare-share-list");
    for (const [name, badge, key, format] of [["Map time", "MT", "averageTime", formatTime],
      ["Item quantity", "IIQ", "averageQuantity", formatPercent], ["Item rarity", "IIR", "averageRarity", formatPercent],
      ["Pack size", "PS", "averagePackSize", formatPercent]]) {
      const [a, b] = summaries.map(summary => summary[key]);
      const [weightA, weightB] = summaries.map(summary => key === "averageTime" ? summary.timedMaps : summary.maps.length);
      const total = weightA + weightB;
      const average = total ? ((a ?? 0) * weightA + (b ?? 0) * weightB) / total : null;
      list.append(shareCard({ name, badge }, a, b, average, format, "Combined average per map", "Avg"));
    }

    const bonuses = new Map();
    const coverage = summaries.map(({ maps }, side) => {
      const recorded = maps.filter(map => Array.isArray(map.dropBonuses));
      for (const map of recorded) {
        for (const { label, source, percent } of map.dropBonuses) {
          const name = source ? `${label} · ${source}` : label;
          const bonus = bonuses.get(name) ?? { name, label, sums: [0, 0], badge: dropBonusBadges.get(label) ?? "%" };
          bonus.sums[side] += percent;
          bonuses.set(name, bonus);
        }
      }
      return recorded.length;
    });
    const ranks = new Map([...dropBonusBadges.keys()].map((label, index) => [label, index]));
    const ordered = [...bonuses.values()].sort((a, b) =>
      (ranks.get(a.label) ?? ranks.size) - (ranks.get(b.label) ?? ranks.size) || a.name.localeCompare(b.name));
    for (const bonus of ordered) {
      const [a, b] = bonus.sums.map((sum, side) => coverage[side] ? sum / coverage[side] : null);
      const average = (bonus.sums[0] + bonus.sums[1]) / (coverage[0] + coverage[1]);
      list.append(shareCard(bonus, a, b, average, formatPercent, "Combined average per map with modifier data", "Avg"));
    }
    $("compare-map-averages").replaceChildren(list);
  }

  function renderUniqueComparison() {
    const target = $("compare-unique-content");
    const results = pairResults("uniques", "unique drops", "compare-unique-status", target);
    renderUniqueTierComparison(results);
    if (!results) return;
    target.replaceChildren();

    const combined = new Map();
    for (const [index, result] of results.entries()) {
      for (const item of result.items) {
        if (item.dropped <= 0) continue;
        const key = itemKey(item.name);
        const row = combined.get(key) ?? { name: item.name, tier: null, icon: null, a: 0, b: 0 };
        row[sides[index]] += item.dropped;
        row.tier ??= item.tier;
        row.icon ??= item.icon;
        combined.set(key, row);
      }
    }
    const query = itemKey($("compare-unique-search").value.trim());
    const presence = $("compare-unique-filter").value;
    const rows = [...combined.values()].filter(row => itemKey(row.name).includes(query) &&
      (presence === "all" || (presence === "both" ? row.a > 0 && row.b > 0 : row[presence] > 0 && row[presence === "a" ? "b" : "a"] === 0)));
    set("compare-unique-status", rows.length ? `${number.format(rows.length)} ${rows.length === 1 ? "item name" : "item names"}` : "No unique drops match this view.");
    if (!rows.length) return;

    const delta = row => row.a - row.b;
    rows.sort((a, b) => {
      const value = (row, key) => key === "delta" ? delta(row) : row[key];
      const order = uniqueSort ? compareLootValues(value(a, uniqueSort.key), value(b, uniqueSort.key), uniqueSort.direction) : Math.abs(delta(b)) - Math.abs(delta(a));
      return order || (b.a + b.b) - (a.a + a.b) || a.name.localeCompare(b.name);
    });
    const table = element("table", null, "compare-table");
    const heading = table.createTHead().insertRow();
    for (const [label, key, numeric] of [["Item", "name"], ["Tier", "tier"], ["A", "a", true], ["B", "b", true], ["A − B", "delta", true]]) {
      const th = sortableHeading(label, key, uniqueSort?.key === key ? uniqueSort.direction : null, numeric ? "descending" : "ascending", numeric, direction => {
        uniqueSort = { key, direction };
        renderUniqueComparison();
        target.querySelector(`[data-sort-key="${key}"]`)?.focus({ preventScroll: true });
      });
      if (sides.includes(key)) th.classList.add(`compare-${key}`);
      heading.append(th);
    }
    const share = element("th", "Share", "compare-unique-share");
    share.scope = "col";
    heading.append(share);
    const body = table.createTBody();
    for (const item of rows) {
      const row = body.insertRow();
      const name = element("div", null, "table-item");
      name.append(itemIcon(item), element("span", item.name, "item-name"));
      row.insertCell().append(name);
      row.insertCell().textContent = item.tier == null ? "—" : `T${item.tier}`;
      appendValues(row, item.a, item.b, formatCount, true);
      const distribution = row.insertCell();
      distribution.className = "compare-unique-share";
      distribution.append(shareBar(item.a, item.b));
    }
    const scroll = element("div", null, "compare-scroll");
    scroll.tabIndex = 0;
    scroll.setAttribute("aria-label", "Unique drops in both sessions");
    scroll.append(table);
    target.append(scroll);
  }

  function renderUniqueTierComparison(results) {
    const target = $("compare-tier-content");
    $("download-unique-tiers").hidden = !results;
    set("compare-tier-status", results ? "" : $("compare-unique-status").textContent);
    target.replaceChildren();
    if (!results) return;

    const [a, b] = results.map(uniqueTierTotals);
    const list = element("ul", null, "compare-share-list");
    for (const [index, label] of uniqueTierLabels.entries()) {
      const row = element("li", null, "compare-share-row");
      const tier = element("span", label, "unique-tier");
      if (label !== "Unknown") tier.dataset.tier = index;
      row.append(tier, shareBar(a[index], b[index], value => number.format(value)));
      list.append(row);
    }
    target.append(list);
  }

  $("download-unique-tiers").addEventListener("click", () => {
    const results = selected.map(select => requests.uniques?.byId?.get(select.value));
    if (results.some(result => !result)) return;

    const [a, b] = results.map(uniqueTierTotals);
    const header = ["Tier", ...selected.map((select, index) =>
      `Session ${sides[index].toUpperCase()} · ${select.selectedOptions[0].textContent}`), "Total"];
    const rows = uniqueTierLabels.map((tier, index) => [tier, a[index], b[index], a[index] + b[index]]);
    const csv = [header, ...rows].map(row => row.map(value => `"${String(value).replaceAll('"', '""')}"`).join(","))
      .join("\r\n");
    const url = URL.createObjectURL(new Blob(["\ufeff", csv], { type: "text/csv;charset=utf-8" }));
    const link = element("a");
    link.href = url;
    link.download = "unique-tiers-comparison.csv";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });

  function showComparison(show) {
    view.hidden = !show;
    $("history-view").hidden = show;
    $("show-history").setAttribute("aria-pressed", String(!show));
    $("show-comparison").setAttribute("aria-pressed", String(show));
    if (show && !opened) {
      $("compare-profit").value = state.profitMode;
      $("compare-pickup").value = state.pickupScope;
      opened = true;
    }
    if (show) refreshComparison();
  }

  $("show-history").addEventListener("click", () => showComparison(false));
  $("show-comparison").addEventListener("click", () => showComparison(true));
  document.addEventListener("map-history-updated", refreshComparison);
  for (const id of ["compare-a", "compare-b", "compare-profit", "compare-pickup"]) $(id).addEventListener("change", () => {
    chartIndex = null;
    refreshComparison();
  });
  $("compare-swap").addEventListener("click", () => {
    [selected[0].value, selected[1].value] = [selected[1].value, selected[0].value];
    refreshComparison();
  });
  $("compare-metric").addEventListener("change", drawComparisonChart);
  for (const button of document.querySelectorAll("[data-compare-amount]")) button.addEventListener("click", () => {
    sessionAmount = button.dataset.compareAmount;
    for (const option of button.parentElement.children) option.setAttribute("aria-pressed", String(option === button));
    renderSessionComparison();
  });
  $("compare-session-more").addEventListener("click", () => { sessionExpanded = !sessionExpanded; renderSessionComparison(); });
  $("compare-session-search").addEventListener("input", renderSessionComparison);
  $("compare-unique-search").addEventListener("input", renderUniqueComparison);
  $("compare-unique-filter").addEventListener("change", renderUniqueComparison);
  chart.addEventListener("pointermove", event => {
    if (!chartScale) return;
    const { left, right, length } = chartScale;
    const offset = event.clientX - chart.getBoundingClientRect().left;
    const index = Math.min(length - 1, Math.max(0, Math.round((offset - left) * (length - 1) / (right - left))));
    if (index === chartIndex) return;
    chartIndex = index;
    drawComparisonChart();
  });
  chart.addEventListener("pointerleave", () => { chartIndex = null; drawComparisonChart(); });
  chart.addEventListener("blur", () => { chartIndex = null; drawComparisonChart(); });
  chart.addEventListener("keydown", event => {
    if (!chartScale || !["ArrowLeft", "ArrowRight"].includes(event.key)) return;
    event.preventDefault();
    chartIndex = chartIndex == null ? 0 : Math.min(chartScale.length - 1, Math.max(0, chartIndex + (event.key === "ArrowLeft" ? -1 : 1)));
    drawComparisonChart();
  });
  new ResizeObserver(drawComparisonChart).observe($("compare-plot"));
})();
