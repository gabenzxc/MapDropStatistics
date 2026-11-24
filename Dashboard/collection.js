"use strict";

(() => {
  const view = $("collection-view");
  let fetching = false;
  let fingerprint = "";

  function renderRecords(records) {
    const metrics = [["Most Divine Orbs picked", records.divineOrbs], ["Most monster kills", records.monsterKills],
      ["Most uniques dropped", records.uniqueDrops]];
    $("collection-record-items").replaceChildren(...metrics.map(([label, record]) => {
      const card = element("li", null, "collection-record-item");
      const value = element("strong", record ? number.format(record.count) : "—", "collection-currency-count");
      value.classList.toggle("collection-zero", !record);
      card.append(element("span", label), value);
      if (record) {
        const date = element("time", dateLabel(record.startedUtc));
        date.dateTime = record.startedUtc;
        const context = element("span", null, "muted");
        context.append(`${record.map} · `, date);
        card.append(context);
      }
      return card;
    }));
  }

  function renderCurrency(items) {
    $("collection-currency-items").replaceChildren(...items.map(item => {
      const card = element("li", null, "collection-currency-item");
      const quantity = element("strong", number.format(item.pickedUp), "collection-currency-count");
      quantity.classList.toggle("collection-zero", item.pickedUp === 0);
      card.append(itemIcon(item), element("span", item.name, "collection-currency-name"), quantity);
      return card;
    }));
  }

  function renderT0(items) {
    const body = $("collection-t0-items");
    const scroll = body.closest(".collection-table-scroll");
    const offset = scroll.scrollTop;
    body.replaceChildren();
    for (const item of items) {
      const row = body.insertRow();
      const heading = element("th");
      heading.scope = "row";
      const name = element("div", null, "table-item");
      name.append(itemIcon(item), element("span", item.name, "item-name"));
      heading.append(name);
      row.append(heading);
      appendCount(row, item.pickedUp, "collection-picked");
    }
    scroll.scrollTop = offset;
  }

  function renderTiers(items) {
    const body = $("collection-tier-items");
    body.replaceChildren();
    for (const item of items) {
      const row = body.insertRow();
      const heading = element("th");
      heading.scope = "row";
      const badge = element("span", item.name, "unique-tier");
      if (item.name !== "Unknown") badge.dataset.tier = item.name.slice(1);
      heading.append(badge);
      row.append(heading);
      appendCount(row, item.dropped);
      appendCount(row, item.pickedUp, "collection-picked");
    }
  }

  function appendCount(row, count, className) {
    const cell = row.insertCell();
    cell.className = "numeric";
    const value = element("span", number.format(count), className);
    value.classList.toggle("collection-zero", count === 0);
    cell.append(value);
  }

  async function refreshCollection() {
    if (view.hidden || fetching) return;
    fetching = true;
    if (!fingerprint) set("collection-status", "Loading collection…");
    try {
      const collection = await request("/api/collection");
      const key = JSON.stringify(collection);
      if (key !== fingerprint) {
        renderRecords(collection.records);
        renderCurrency(collection.currency);
        renderT0(collection.t0Uniques);
        renderTiers(collection.tiers);
        fingerprint = key;
      }
      set("collection-status", `${number.format(collection.runs)} ${collection.runs === 1 ? "run" : "runs"} · All history`);
    } catch {
      set("collection-status", "Could not load the collection.");
    } finally {
      fetching = false;
    }
  }

  document.addEventListener("dashboard-view-changed", refreshCollection);
  document.addEventListener("map-history-updated", refreshCollection);
})();
