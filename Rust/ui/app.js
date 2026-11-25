(() => {
  "use strict";
  const app = document.getElementById("morphocyte-atlas");
  const $ = selector => app.querySelector(selector);
  const all = selector => [...app.querySelectorAll(selector)];
  const geo = window.d3;
  const topo = window.topojson;
  const atlas = JSON.parse($("#atlas-geography").textContent);
  const borderView = JSON.parse($("#atlas-border-view").textContent);
  const themeCatalog = JSON.parse(document.getElementById("atlas-themes").textContent);
  const flagCatalog = JSON.parse(document.getElementById("atlas-flags").textContent);
  const regionCodes = JSON.parse($("#atlas-region-codes").textContent);
  const regionNames = new Intl.DisplayNames(["ru"], { type: "region", fallback: "none" });
  const features = atlas.objects.features;
  const countries = topo.feature(atlas, features).features;
  const land = topo.merge(atlas, features.geometries);
  const borders = topo.mesh(atlas, features, (left, right) => left !== right
    && !([left.properties.id, right.properties.id].includes("UKR")
      && [left.properties.id, right.properties.id].includes("RUS")));
  for (const country of countries) {
    if (country.properties.id === "UKR") country.geometry = borderView.ukraine;
    if (country.properties.id === "RUS")
      country.geometry.coordinates.push(...borderView.russiaAddition.coordinates);
  }
  const countryIndex = new Map(countries.map(country => [country.properties.id, country]));
  const countriesByCode = new Map();
  for (const [iso, code] of Object.entries(regionCodes)) {
    if (!countriesByCode.has(code)) countriesByCode.set(code, countryIndex.get(iso));
  }
  const countryCenters = new Map();
  const connectionNames = new Intl.Collator("ru");
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  const stackedLayout = matchMedia("(max-width: 665px)");
  // A fixed reference point for the schematic. The device is not geolocated.
  const origin = [37.613577, 55.75411];
  const state = { page: "home", active: false, tun: false, auto: false, popup: false, filter: null, pending: -1, country: null };
  const targets = { DIRECT: "Напрямую", VPN: "Через VPN", REJECT: "Блокировать" };
  let servers = [];
  const serverRows = [new Map(), new Map()];
  let availableGeometries = [];
  let mapColors;
  let snapshot = null;
  let current = -1;
  let busy = false;
  let operation = null;
  let pageAnimations = [];
  const tabs = all("nav [data-page]");
  const nav = tabs[0].parentElement;
  const tabIndicator = $("[data-nav-indicator]");
  let polling = false;
  let mutation = 0;
  let pendingTheme = null;
  let savingTheme = false;
  let preferencesEdited = false;
  let drawerReturn;
  let signature = "";
  let ruleSignature = "";
  let connections = [];
  let logKey = null;
  let logSort = "";
  let logReverse = false;
  let logUpdated = "";
  let ping = new Map();
  let pingErrors = new Set();
  let pingSkipped = new Map();
  let pingMethod = "vpn";
  const pingMethods = { vpn: "HTTPS через VPN", tcp: "TCP", icmp: "ICMP" };
  let pingOrder = 0;
  let dismissedUpdate = "";
  let transparent = 21;
  try { transparent = Number(localStorage.getItem("atlas-transparency") ?? 21); } catch {}
  if (!Number.isFinite(transparent)) transparent = 21;
  const selected = () => servers[current] ?? {};
  const announce = text => { $("[data-announcement]").textContent = text; };
  const report = text => {
    $("[data-atlas-error]").textContent = text || "";
    $("[data-atlas-error]").hidden = !text;
  };
  const make = (tag, text, className) => {
    const element = document.createElement(tag);
    if (text != null) element.textContent = text;
    if (className) element.className = className;
    return element;
  };
  const native = message => window.ipc?.postMessage(JSON.stringify(message));
  const requests = new Map();
  let requestId = 0;
  function rpc(op, args = {}) {
    if (!window.ipc) return Promise.reject(new Error("Открой приложение Морфоцит OS."));
    return new Promise((resolve, reject) => {
      const id = ++requestId;
      requests.set(id, { resolve, reject });
      native({ id, op, ...args });
    });
  }
  window.atlasReply = reply => {
    const pending = requests.get(reply.id);
    if (!pending) {
      if (!reply.ok) report(reply.error);
      return;
    }
    requests.delete(reply.id);
    if (reply.ok) pending.resolve(reply.value);
    else pending.reject(new Error(reply.error || "Операция не выполнена."));
  };

  function blend(a, b, ratio) {
    const left = Number.parseInt(a.slice(1), 16);
    const right = Number.parseInt(b.slice(1), 16);
    const components = [16, 8, 0].map(shift => {
      const value = Math.round(((left >> shift) & 255) * (1 - ratio) + ((right >> shift) & 255) * ratio);
      return value.toString(16).padStart(2, "0");
    });
    return "#" + components.join("");
  }
  function renderMetric(key, value) {
    const field = $(`[data-stat="${key}"]`);
    const text = value || "—";
    if (field.dataset.measurement === text) return;
    field.dataset.measurement = text;
    const match = /^(\S+)\s+(\S+)$/.exec(text.trim());
    if (match) {
      field.replaceChildren(make("span", match[1], "a-metric-part"), " ",
        make("span", match[2], "a-metric-part a-metric-unit"));
    } else field.textContent = text;
  }
  function themeName(name) {
    const themes = Object.keys(themeCatalog);
    return themes.find(theme => theme.toLowerCase() === name?.trim().toLowerCase()) ?? themes[0];
  }
  function applyTheme(name) {
    const theme = themeCatalog[name];
    const { canvas, title, panel, surface, input, popup, text, accent, secondary, border, borderStrong, hover, selected, danger, log, orbit, orbitFill, start, middle, end, light } = theme;
    const muted = blend(theme.muted, text, .26);
    const foreground = light ? "#ffffff" : theme.onAccent;
    const glow = blend(middle, accent, light ? .17 : .19);
    app.dataset.theme = light ? "light" : "dark";
    app.dataset.palette = name;
    document.documentElement.style.colorScheme = light ? "light" : "dark";
    document.documentElement.style.setProperty("--atlas-background", canvas);
    for (const [key, color] of Object.entries({
      shell: canvas, nav: title, sheet: surface, input, popup, ink: text, muted, line: border, hover, selected, accent,
      signal: accent, panel, stage: middle, "stage-edge": end, "stage-glow": glow, "stage-ink": text, "stage-muted": muted,
      "on-solid": foreground, danger, "log-surface": log,
      "diagram-border": borderStrong, "diagram-input": input, "diagram-selected": selected,
      "diagram-hover": hover, "diagram-accent": accent, "diagram-sheet": surface
    })) app.style.setProperty("--" + key, color);
    const logo = $("svg.atlas-logo");
    for (const [key, color] of Object.entries(theme.logo)) logo.style.setProperty("--logo-" + key, color);
    native({ shell: "theme-icon", resource: theme.iconResource });
    mapColors = {
      halo1: orbit + "55", halo2: secondary + "20", haloEnd: secondary + "00",
      stars: muted + "46", orbit: orbit + "6a", outerRim: orbit + "42",
      water1: blend(start, secondary, light ? .21 : .25),
      water2: blend(middle, orbitFill, .53), water3: blend(end, orbitFill, .18),
      land1: blend(start, accent, light ? .48 : .40),
      land2: blend(middle, orbit, .39), land3: blend(end, secondary, .23),
      shore: muted + "ab", available: accent + "80",
      border: text + (light ? "64" : "59"), grid: secondary + "32", selected: accent + "76",
      accent: light ? blend(accent, secondary, .22) : blend(accent, "#ffffff", .20),
      dot: accent + "db", marker: text, labelShadow: light ? "#f8fbf9" : "#10131f",
      rim1: secondary + "be", rim2: orbit + "90", rim3: orbit + "60",
      beamGlow: accent + "25", beamHalo: accent + "81"
    };
    $("[data-theme-choice]").value = name;
    scheduleDraw();
  }

  /* ATLAS_GLOBE */

  function inspectCountry(country) {
    state.country = country;
    $("[data-inspected]").hidden = !country;
    $("[data-inspected-name]").textContent = country?.name ?? "";
    $("[data-inspected-detail]").textContent = country?.detail ?? "";
    scheduleDraw();
  }
  function openPicker(filter = null, index = current) {
    go("home");
    drawerReturn = filter ? globe : $("[data-open-servers]");
    state.popup = true;
    state.filter = filter;
    state.pending = index;
    $("[data-drawer]").hidden = false;
    $("[data-drawer]").dataset.filtered = String(Boolean(filter));
    $("#atlas-picker-title").textContent = filter?.name || "Серверы";
    $("[data-server-search]").value = "";
    $("[data-all-countries]").hidden = !filter;
    $("[data-open-servers]").setAttribute("aria-expanded", "true");
    app.dataset.picker = "true";
    inspectCountry(null);
    refreshPreview();
    renderServerLists();
  }
  function closePicker(focus = false) {
    if (!state.popup) return;
    state.popup = false;
    state.filter = null;
    previewRoute = null;
    $("[data-drawer]").hidden = true;
    app.dataset.picker = "false";
    $("[data-open-servers]").setAttribute("aria-expanded", "false");
    if (focus) drawerReturn?.focus({ preventScroll: true });
    scheduleDraw();
  }
  function refreshPreview() {
    previewRoute = state.popup && state.pending !== current && servers[state.pending]?.position ? makeRoute(servers[state.pending]) : null;
    scheduleDraw();
  }
  function positionTabIndicator() {
    const tab = tabs.find(button => button.dataset.page === state.page);
    if (!tab) return;
    tabIndicator.style.transform = `translate(${tab.offsetLeft}px, ${tab.offsetTop}px)`;
    tabIndicator.style.width = tab.offsetWidth + "px";
    tabIndicator.style.height = tab.offsetHeight + "px";
    tabIndicator.hidden = false;
  }
  function go(page) {
    if (state.page === page) return;
    const previous = state.page;
    const outgoing = $("[data-screen=\"" + previous + "\"]");
    const incoming = $("[data-screen=\"" + page + "\"]");
    if (!incoming) return;
    const fromHome = previous === "home";
    const toHome = page === "home";
    const continuingFlight = Boolean(globeFlightFrame);
    cancelAnimationFrame(globeFlightFrame);
    globeFlightFrame = 0;
    for (const animation of pageAnimations) animation.cancel();
    pageAnimations = [];
    state.page = page;
    if (previous === "routes") setProcessPicker(false);
    if (fromHome) { stopCamera(); closePicker(); }
    for (const screen of all("[data-screen]")) {
      screen.hidden = screen !== incoming;
      screen.inert = screen !== incoming;
    }
    for (const tab of tabs) {
      if (tab.dataset.page === page) tab.setAttribute("aria-current", "page");
      else tab.removeAttribute("aria-current");
    }
    positionTabIndicator();
    if (!reducedMotion.matches && !document.hidden) {
      outgoing.hidden = false;
      const direction = Math.sign(tabs.findIndex(tab => tab.dataset.page === page) -
        tabs.findIndex(tab => tab.dataset.page === previous));
      const crossesHome = fromHome || toHome;
      const timing = { duration: crossesHome ? 460 : 240, easing: crossesHome ? "ease-in-out" : "cubic-bezier(.22,.75,.2,1)", fill: "both" };
      if (crossesHome) {
        if (!continuingFlight) globeFlight = toHome ? 1 : 0;
        const home = toHome ? incoming : outgoing;
        const work = toHome ? outgoing : incoming;
        const overlays = home.querySelectorAll(".a-card, .a-map-tools, .a-country-info, .a-footer");
        const fade = toHome ? [{ opacity: 0 }, { opacity: 1 }] : [{ opacity: 1 }, { opacity: 0 }];
        for (const overlay of overlays) {
          const effect = overlay.animate(fade, timing);
          effect.onfinish = () => effect.cancel();
          pageAnimations.push(effect);
        }
        const workFrames = [{ transform: "none", opacity: 1 }, { transform: "translateX(8%)", opacity: 0 }];
        const workEffect = work.animate(toHome ? workFrames : workFrames.reverse(), timing);
        pageAnimations.push(workEffect);
        workEffect.onfinish = () => {
          if (state.page === page) outgoing.hidden = true;
          workEffect.cancel();
        };
        flyGlobe(toHome ? 0 : 1, timing.duration);
      } else {
        const leave = outgoing.animate([
          { transform: "none", opacity: 1 }, { transform: `translateX(${-10 * direction}%)`, opacity: 0 }
        ], timing);
        const enter = incoming.animate([
          { transform: `translateX(${8 * direction}%)`, opacity: 0 }, { transform: "none", opacity: 1 }
        ], timing);
        pageAnimations = [leave, enter];
        leave.onfinish = () => {
          if (state.page === page) outgoing.hidden = true;
          leave.cancel();
        };
        enter.onfinish = () => enter.cancel();
      }
    } else globeFlight = 0;
    if (toHome) scheduleDraw();
    if (page === "log") renderJournal();
  }

  function countryLabel(code, fallback = "") {
    if (code === "TW") return "Тайвань · Материковый Китай";
    return /^[A-Z]{2}$/.test(code ?? "") ? regionNames.of(code) || fallback : fallback;
  }

  function paintFlag(badge, code, label) {
    const flag = flagCatalog[code?.toLowerCase()];
    if (!flag) {
      badge.classList.remove("a-flag");
      badge.textContent = code === "AUTO" ? "★" : code || "—";
      badge.title = label || "";
      return;
    }
    const image = document.createElement("img");
    image.src = "data:image/svg+xml;base64," + flag;
    image.alt = "";
    badge.classList.add("a-flag");
    badge.title = label;
    badge.replaceChildren(image);
  }

  function installServers(items) {
    const next = JSON.stringify(items);
    if (next === signature) return false;
    signature = next;
    for (const rows of serverRows) rows.clear();
    const names = new Set(items.map(server => server.name));
    ping = new Map([...ping].filter(([name]) => names.has(name)));
    servers = items.map(item => {
      const code = item.countryCode;
      const feature = countriesByCode.get(code);
      const iso = feature?.properties.id;
      const country = countryLabel(code, feature?.properties.name);
      if (feature && !countryCenters.has(iso)) countryCenters.set(iso, geo.geoCentroid(feature));
      const search = [item.name, item.title, country, item.details].join(" ").toLocaleLowerCase("ru-RU");
      return { ...item, code, iso, country, search, position: countryCenters.get(iso) ?? null };
    });
    const available = new Set(servers.map(server => server.iso));
    availableGeometries = countries.filter(country => available.has(country.properties.id));
    return true;
  }
  function serverRow(server, index, compact) {
    const item = make("li");
    const button = make("button", null, "a-server cursor-interaction");
    button.type = "button";
    button.dataset[compact ? "server" : "fullServer"] = String(index);
    const badge = make("span", null, "a-code");
    badge.setAttribute("aria-hidden", "true");
    paintFlag(badge, server.code, server.country);
    const title = make("span");
    title.append(make("strong", server.title), make("small", server.details || server.country || "Локация не указана"));
    const latency = make("span", null, "a-ping");
    button.append(badge, title, latency);
    item.append(button);
    return { item, button, latency };
  }
  function updateServerRow({ button, latency }, server, selected) {
    if (button.getAttribute("aria-pressed") !== String(selected)) button.setAttribute("aria-pressed", String(selected));
    const result = ping.get(server.name);
    const failed = pingErrors.has(server.name);
    const measured = Number.isFinite(result);
    const skipped = pingSkipped.get(server.name);
    const method = snapshot?.serverPingMethod ?? "vpn";
    const suffix = method === "vpn" ? "" : " · " + pingMethods[method];
    const label = measured ? result + " мс" + suffix
      : skipped ? "Пропущен" : failed ? "Ошибка замера" : result === null ? "Нет ответа" + suffix : "—";
    if (latency.textContent !== label) latency.textContent = label;
    latency.classList.toggle("a-ping-measured", measured);
    const hint = skipped || (failed ? "Локальная проверка завершилась ошибкой"
      : result !== undefined ? "Способ проверки: " + pingMethods[method] : "");
    if (button.title !== hint) button.title = hint;
  }
  function renderServerLists() {
    for (const compact of [false, true]) {
      const key = compact ? "" : "full-";
      const query = $("[data-" + key + "server-search]").value.toLocaleLowerCase("ru-RU").trim();
      const list = $("[data-" + key + "server-list]");
      const rows = serverRows[Number(compact)];
      const visible = servers.map((server, index) => ({ server, index })).filter(({ server }) =>
        (!compact || !state.filter || state.filter.iso === server.iso) && server.search.includes(query));
      if (pingOrder) visible.sort((left, right) => {
        const a = ping.get(left.server.name), b = ping.get(right.server.name);
        const aMeasured = Number.isFinite(a), bMeasured = Number.isFinite(b);
        if (aMeasured !== bMeasured) return aMeasured ? -1 : 1;
        return aMeasured && a !== b ? (a - b) * pingOrder : left.index - right.index;
      });
      const entries = visible.map(({ server, index }) => {
        if (!rows.has(index)) rows.set(index, serverRow(server, index, compact));
        const row = rows.get(index);
        updateServerRow(row, server, index === (compact ? state.pending : current));
        return row.item;
      });
      if (entries.length !== list.children.length || entries.some((entry, index) => list.children[index] !== entry)) {
        const scroll = list.parentElement;
        const offset = scroll.scrollTop;
        const focused = document.activeElement;
        const focusedIndex = list.contains(focused) ? focused.dataset[compact ? "server" : "fullServer"] : null;
        list.replaceChildren(...entries);
        scroll.scrollTop = offset;
        if (focusedIndex != null) list.querySelector(`[data-${key}server="${focusedIndex}"]`)?.focus({ preventScroll: true });
      }
      $("[data-" + key + "server-empty]").hidden = !!entries.length;
    }
    renderPickerControl();
    for (const button of all("[data-ping-order]")) {
      button.dataset.direction = pingOrder === 1 ? "up" : pingOrder === -1 ? "down" : "none";
      button.setAttribute("aria-label", pingOrder === 1 ? "Пинг по возрастанию. Сортировать по убыванию."
        : pingOrder === -1 ? "Пинг по убыванию. Сортировать по возрастанию." : "Сортировать по возрастанию пинга.");
    }
  }

  function optionList(actions, selectedKey) {
    const select = make("select", null, "a-select");
    for (const [key, value] of Object.entries(actions)) select.add(new Option(value, key, false, key === selectedKey));
    return select;
  }
  const processPicker = $("[data-process-picker]");
  const processToggle = $("[data-process-toggle]");
  const processSearch = $("[data-process-search]");
  const processList = $("[data-process-list]");
  const processMessage = $("[data-process-message]");
  const processRefresh = $("[data-process-refresh]");
  const processAdd = $("[data-process-add]");
  const processRoute = $("[data-process-route]");
  let runningApps = [];
  let processTicket = 0;
  let processStatus = "";
  let processAnchor = null;
  const selectedProcesses = new Set();

  function syncProcessSelection() {
    for (const box of processList.querySelectorAll("input:not(:disabled)")) box.checked = selectedProcesses.has(box.value);
    const count = selectedProcesses.size;
    processAdd.disabled = count === 0;
    processAdd.textContent = count ? `Добавить выбранные (${count})` : "Добавить выбранные";
  }

  function renderProcesses() {
    const existing = new Set((snapshot?.rules ?? []).filter(rule => rule.kind === "PROCESS-NAME")
      .map(rule => rule.value.toLowerCase()));
    for (const name of selectedProcesses) if (existing.has(name.toLowerCase())) selectedProcesses.delete(name);
    const query = processSearch.value.toLowerCase().trim();
    const rows = runningApps.filter(app => `${app.name} ${app.title}`.toLowerCase().includes(query)).map(app => {
      const item = make("li");
      const label = make("label", null, "a-process-row");
      const input = make("input");
      const details = make("span");
      input.type = "checkbox";
      input.value = app.name;
      input.disabled = existing.has(app.name.toLowerCase());
      input.checked = input.disabled || selectedProcesses.has(app.name);
      details.append(make("strong", app.name), make("small", input.disabled ? "Уже в маршрутах" : app.title));
      label.append(input, details);
      item.append(label);
      return item;
    });
    processList.replaceChildren(...rows);
    const message = processStatus || (!rows.length ? (query ? "Приложения не найдены." : "Нет приложений с открытыми окнами.") : "");
    processMessage.textContent = message;
    processMessage.hidden = !message;
    syncProcessSelection();
  }

  async function loadProcesses() {
    const ticket = ++processTicket;
    processRefresh.disabled = true;
    processStatus = "Загружаю список приложений…";
    renderProcesses();
    try {
      const apps = await rpc("running_apps");
      if (ticket !== processTicket) return;
      runningApps = apps;
      const names = new Set(apps.map(app => app.name));
      for (const name of selectedProcesses) if (!names.has(name)) selectedProcesses.delete(name);
      processStatus = "";
    } catch (error) {
      if (ticket !== processTicket) return;
      processStatus = error.message;
    } finally {
      if (ticket === processTicket) {
        processRefresh.disabled = false;
        renderProcesses();
      }
    }
  }

  function setProcessPicker(open, focus = false) {
    if (processPicker.inert === !open) return;
    processPicker.inert = !open;
    processPicker.dataset.open = String(open);
    $("[data-rules-list]").inert = open;
    processToggle.setAttribute("aria-expanded", String(open));
    if (open) {
      processPicker.parentElement.scrollTop = 0;
      processRoute.value = $("[data-add-rule]").elements.route.value;
      void loadProcesses();
      processSearch.focus({ preventScroll: true });
    } else {
      processTicket++;
      runningApps = [];
      selectedProcesses.clear();
      processAnchor = null;
      processSearch.value = "";
      if (focus) processToggle.focus();
    }
  }

  function renderRules(rules) {
    const next = JSON.stringify(rules);
    if (ruleSignature === next) return;
    ruleSignature = next;
    const groups = Map.groupBy ? Map.groupBy(rules.entries(), ([, rule]) => rule.folder || "") : null;
    const entries = groups ? [...groups.entries()] : [["", [...rules.entries()]]];
    const list = $("[data-rules-list]");
    const rows = [];
    for (const [folder, items] of entries) {
      if (folder) rows.push(make("li", folder, "a-folder-title"));
      for (const [index, rule] of items) {
        const row = make("li", null, "a-rule");
        row.dataset.rule = String(index);
        const enabled = make("input");
        enabled.type = "checkbox";
        enabled.checked = rule.enabled;
        enabled.setAttribute("aria-label", "Правило " + rule.value);
        const description = make("div");
        description.append(make("strong", rule.value), make("small", rule.kindLabel || rule.kind));
        const select = optionList(targets, rule.route === "DIRECT" ? "DIRECT" : rule.route.startsWith("REJECT") ? "REJECT" : "VPN");
        select.setAttribute("aria-label", "Маршрут для " + rule.value);
        const remove = make("button", "×", "a-icon cursor-interaction");
        remove.type = "button";
        remove.dataset.removeRule = String(index);
        remove.setAttribute("aria-label", "Удалить " + rule.value);
        row.append(enabled, description, select, remove);
        rows.push(row);
      }
    }
    list.replaceChildren(...rows);
    $("[data-rules-empty]").hidden = !!rows.length;
    if (!processPicker.inert) renderProcesses();
  }
  function renderJournal() {
    const query = $("[data-log-search]").value.toLocaleLowerCase("ru-RU").trim();
    const rows = connections.filter(row => [row.process, row.destination, row.network, row.rule, row.route].join(" ").toLocaleLowerCase("ru-RU").includes(query));
    const fields = { process: "process", address: "destination", protocol: "network", rule: "rule", route: "route" };
    const sortField = fields[logSort];
    if (sortField) rows.sort((a, b) => connectionNames.compare(a[sortField] ?? "", b[sortField] ?? "") * (logReverse ? -1 : 1));
    for (const button of all("[data-log-sort]")) button.setAttribute("aria-pressed", String(logSort === button.dataset.logSort));
    $("[data-connections]").replaceChildren(...rows.map((connection, index) => {
      const row = make("tr");
      row.dataset.selected = String(connection === logKey);
      const process = make("button", connection.process || "—", "a-connection-name cursor-interaction");
      process.dataset.connection = String(index);
      row.insertCell().append(process);
      for (const value of [connection.destination, connection.network, connection.rule]) row.insertCell().textContent = value || "—";
      const route = optionList({ current: connection.route || "—", ...targets }, "current");
      route.dataset.connection = String(index);
      route.setAttribute("aria-label", "Маршрут для " + connection.destination);
      row.insertCell().append(route);
      return row;
    }));
    filteredConnections = rows;
    $("[data-no-connections]").hidden = !!rows.length;
    $("[data-log-more]").disabled = !rows.includes(logKey);
    if (!rows.includes(logKey)) $("[data-log-details-panel]").hidden = true;
    $("[data-log-status]").textContent = "Соединения: " + rows.length + (logUpdated ? " · " + logUpdated : "");
    renderEvents();
  }
  let renderedLogs = "";
  function renderEvents() {
    const lines = snapshot?.logs ?? [];
    const key = JSON.stringify(lines);
    if (key === renderedLogs) return;
    renderedLogs = key;
    const list = $("[data-events]");
    const atEnd = list.scrollHeight - list.clientHeight - list.scrollTop < 20;
    const offset = list.scrollTop;
    list.replaceChildren(...lines.map(line => {
      const event = make("li", null, "a-event");
      const parts = line.match(/^\[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2})\]\s*([\s\S]*)$/);
      if (parts) {
        const time = make("time", parts[2]);
        time.dateTime = parts[1] + "T" + parts[2];
        time.title = parts[1];
        event.append(time, make("span", parts[3], "a-log-message"));
      } else event.append(make("span", line, "a-log-message a-log-untimed"));
      return event;
    }));
    list.scrollTop = atEnd ? list.scrollHeight : offset;
  }
  let filteredConnections = [];
  function setBusy(element, waiting) {
    if (waiting) element.setAttribute("aria-busy", "true");
    else element.removeAttribute("aria-busy");
  }
  function renderPickerControl() {
    const candidate = servers[state.pending];
    const picker = $("[data-connect-selected]");
    picker.disabled = Boolean(operation) || !candidate || (state.active && state.pending === current);
    setBusy(picker, operation?.control === "picker");
    picker.textContent = operation?.control === "picker" ? operation.button :
      state.active ? (state.pending === current ? "Подключено" : "Переключить") : "Подключить";
    $("[data-picker-selection]").textContent = candidate?.title ?? "Выбери сервер";
  }
  function renderOperations() {
    const updating = Boolean(operation);
    app.dataset.operating = String(updating);
    const tracked = snapshot?.tracked;
    $("[data-status]").textContent = operation?.message || snapshot?.status || "Загрузка";
    const power = $("[data-power]");
    power.disabled = updating || (!snapshot?.ready && !tracked);
    power.querySelector("span").textContent = operation?.control === "power" ? operation.button :
      tracked ? "Остановить" : "Подключить";
    power.title = snapshot?.requiresElevation && !tracked ? "Подключить с разрешения администратора Windows" : "";
    setBusy(power, operation?.control === "power");
    const restart = $("[data-restart]");
    restart.disabled = updating || !tracked;
    restart.hidden = !tracked;
    restart.textContent = operation?.control === "restart" ? operation.button : "Перезапустить ядро";
    setBusy(restart, operation?.control === "restart");
    const tunnel = $("[data-tunnel]");
    tunnel.disabled = updating || !snapshot;
    tunnel.setAttribute("aria-checked", String(state.tun));
    setBusy(tunnel, operation?.control === "tunnel");
    $("[data-tunnel-caption]").textContent = operation?.control === "tunnel" ? operation.button : "Режим TUN";
    renderPickerControl();
    for (const refresh of all("[data-refresh]")) {
      refresh.disabled = updating;
      setBusy(refresh, operation?.control === "refresh");
    }
  }
  function install(value) {
    if (!value) return;
    const old = snapshot;
    snapshot = value;
    if (value.nativePreview && !old) {
      processToggle.hidden = false;
      for (const selector of ["[data-classic]", "[data-core-controls]", "[data-update-preview]"]) {
        for (const element of all(selector)) element.hidden = true;
      }
      preferences.elements.namedItem("reconnect")?.closest("label")?.setAttribute("hidden", "");
      for (const answer of all("[data-native-answer]")) answer.textContent = answer.dataset.nativeAnswer;
    }
    const pendingServer = servers[state.pending]?.name;
    const changed = installServers(value.servers);
    const pingsChanged = JSON.stringify(old?.serverPings) !== JSON.stringify(value.serverPings) ||
      JSON.stringify(old?.serverPingErrors) !== JSON.stringify(value.serverPingErrors) ||
      JSON.stringify(old?.serverPingSkipped) !== JSON.stringify(value.serverPingSkipped) ||
      old?.serverPingMethod !== value.serverPingMethod;
    if (pingsChanged) {
      ping = new Map(Object.entries(value.serverPings ?? {}));
      pingErrors = new Set(value.serverPingErrors ?? []);
      pingSkipped = new Map(Object.entries(value.serverPingSkipped ?? {}));
    }
    const next = servers.findIndex(item => item.name === value.selected);
    const selectionChanged = next !== current;
    const wasActive = state.active;
    current = next;
    state.active = value.online;
    state.auto = selected().code === "AUTO";
    state.tun = value.mode === "FullTunnel";
    app.dataset.connected = String(value.online);
    const theme = themeName(value.theme);
    if (!pendingTheme && !savingTheme && app.dataset.palette !== theme) applyTheme(theme);
    if (changed || selectionChanged) {
      route = makeRoute(selected());
      if (state.popup) {
        if (changed) state.pending = servers.findIndex(item => item.name === pendingServer);
        refreshPreview();
      } else state.pending = current;
      if (old && selectionChanged && state.page === "home" && !introPending) centerRoute(true);
    }
    $("[data-state]").textContent = !value.online ? "Отключён" :
      value.healthChecked && !value.healthOk ? "Нет ответа" : "Подключён";
    if (changed || selectionChanged || !old) paintFlag($("[data-code]"), selected().code, selected().country);
    $("[data-city]").textContent = selected().title || "Выбери профиль";
    $("[data-detail]").textContent = selected().details || value.core || "Подписка или YAML";
    $("[data-connection-issue]").textContent = value.issue || "";
    $("[data-connection-issue]").hidden = !value.issue;
    renderOperations();
    renderMetric("download", value.online ? value.receive : "—");
    renderMetric("upload", value.online ? value.send : "—");
    renderMetric("ping", value.latency);
    $("[data-stat='ping']").title = pingMethods[value.latencyMethod]
      ? `Последний успешный замер · ${pingMethods[value.latencyMethod]}` : "";
    $("[data-max-speed]").textContent = value.maxDownload || "—";
    const speed = $("[data-speed]");
    speed.disabled = !value.speedAvailable && !value.speedRunning;
    speed.title = speed.disabled ? "Подключи VPN, чтобы измерить скорость"
      : "Тестовая загрузка через выбранный сервер · до 64 МБ и 24 секунд";
    speed.textContent = value.speedRunning ? "Остановить" : "Проверить";
    const progress = $("[data-speed-progress]");
    const percent = value.speedProgress?.match(/^\d{1,3}\s*%$/)?.[0] || (value.speedRunning ? "0 %" : "");
    progress.hidden = !percent;
    progress.textContent = percent;
    const speedIssue = $("[data-speed-issue]");
    speedIssue.hidden = !value.speedMessage;
    speedIssue.textContent = value.speedMessage || "";
    const offer = $("[data-update-offer]");
    offer.hidden = !value.atlasUpdate || value.atlasUpdate === dismissedUpdate;
    $("[data-update-offer-text]").textContent = value.atlasUpdate ? "Доступна версия " + value.atlasUpdate : "";
    $("[data-update-open]").hidden = !value.atlasUpdate;
    if (updateDialog.open && updateDialog.dataset.release) renderUpdateProgress(value);
    $("[data-apply]").disabled = !value.dirty;
    const sub = value.subscription;
    $("[data-subscription-name]").textContent = sub?.title || "Моя подписка";
    $("[data-subscription-updated]").textContent = sub?.checkedAt ? new Date(sub.checkedAt).toLocaleString("ru-RU") : "Данные подписки недоступны";
    $("[data-usage-label]").textContent = sub?.trafficLabel || "";
    $(".a-usage").value = sub?.usagePercent || 0;
    $("[data-subscription-expiry]").textContent = sub?.expiryLabel || "";
    $("[data-subscription-announcement]").textContent = sub?.announcement || "";
    $("[data-home-subscription]").textContent = sub?.trafficLabel || sub?.title || "Подписка не добавлена";
    renderPreferences(value);
    if (changed || selectionChanged || wasActive !== state.active || pingsChanged) renderServerLists();
    for (const picker of all("[data-ping-method]")) {
      picker.hidden = !value.nativePreview;
      picker.disabled = !!value.pingRunning;
      picker.value = pingMethod;
    }
    const pingRetry = Number(value.pingRetrySeconds) || 0;
    for (const button of all("[data-ping-all]")) {
      button.disabled = !servers.length || (!value.pingRunning && (value.speedRunning || pingRetry > 0));
      button.querySelector("span").textContent = value.pingRunning
        ? (button.closest("[data-drawer]") ? "Стоп" : "Остановить пинг") : "Пинг";
      button.setAttribute("aria-label", value.pingRunning ? "Остановить проверку пинга" : "Проверить пинг серверов");
    }
    const pingWarning = pingRetry ? `Windows не хватает сетевых ресурсов (10055). Повторный замер доступен через ${pingRetry} с.`
      : value.pingIssue || "";
    for (const warning of all("[data-ping-issue]")) {
      if (warning.textContent !== pingWarning) warning.textContent = pingWarning;
      warning.hidden = !pingWarning;
    }
    renderRules(value.rules);
    if (state.page === "log") renderEvents();
    if (changed || selectionChanged || wasActive !== state.active) scheduleDraw();
  }
  function ruleArgs(index) {
    const { kind, value } = snapshot.rules[index];
    return { index, kind, value };
  }
  async function action(run, feedback = null) {
    if (busy) { announce("Подожди завершения операции"); return false; }
    busy = true;
    mutation++;
    try {
      report("");
      if (feedback) {
        operation = feedback;
        renderOperations();
        announce(feedback.message);
      }
      await run();
      return true;
    } catch (error) {
      report(error.message);
      try { install(await rpc("state")); } catch {}
      return false;
    } finally {
      busy = false;
      if (feedback) {
        operation = null;
        renderOperations();
      }
      void saveTheme();
    }
  }
  async function pollState() {
    if (busy || polling) return;
    const revision = mutation;
    polling = true;
    try {
      const value = await rpc("state");
      if (!busy && mutation === revision) install(value);
    } catch (error) {
      if (!snapshot) report(error.message);
    } finally {
      polling = false;
    }
  }
  async function saveTheme() {
    if (busy || savingTheme || !pendingTheme) return;
    savingTheme = true;
    const name = pendingTheme;
    pendingTheme = null;
    const saved = await action(async () => install(await rpc("theme", { name })));
    savingTheme = false;
    if (!saved && !pendingTheme && snapshot) applyTheme(themeName(snapshot.theme));
    if (pendingTheme) void saveTheme();
  }
  function chooseTheme(name) {
    if (!Object.hasOwn(themeCatalog, name)) return;
    pendingTheme = name;
    applyTheme(name);
    void saveTheme();
  }
  const command = (op, args, feedback) => action(async () => install(await rpc(op, args)), feedback);
  const loadConnections = async () => {
    connections = await rpc("connections");
    logUpdated = new Date().toLocaleTimeString("ru-RU");
    renderJournal();
  };
  function togglePower() {
    if (snapshot?.requiresElevation && !snapshot?.tracked) {
      report("");
      native("elevate");
      return;
    }
    return command("power", {}, snapshot?.tracked
      ? { control: "power", message: "Останавливаю VPN…", button: "Остановка…" }
      : { control: "power", message: "Подключаю VPN…", button: "Запуск…" });
  }

  const preferences = $("[data-preferences]");
  const controls = preferences.elements;
  const scale = controls.namedItem("scale");
  const launchAtSignIn = controls.namedItem("launchAtSignIn");
  const connectAtSignIn = controls.namedItem("connectAtSignIn");
  const connectOnOpen = controls.namedItem("connectOnOpen");
  const reconnect = controls.namedItem("reconnect");
  const updates = controls.namedItem("automaticUpdates");
  const preferencesSave = $("[data-preferences-save]");
  function renderPreferences(value) {
    const data = value.preferences;
    if (!data) return;
    for (const button of all("[data-core-change]")) button.disabled = value.tracked;
    $("[data-core-reason]").hidden = value.nativePreview || !value.tracked;
    $("[data-profile-path]").textContent = data.profilePath;
    $("[data-core-path]").textContent = data.corePath;
    $("[data-update-status]").textContent = data.updateMessage;
    if (value.nativePreview) {
      const check = $("[data-operation=check_updates]");
      check.disabled = value.atlasChecking || value.atlasInstallRunning;
      check.textContent = value.atlasChecking ? "Проверяю…" : "Проверить обновления";
    }
    $("[data-atlas-version]").textContent = data.atlasVersion || "";
    app.style.setProperty("--atlas-scale", data.interfaceScale);
    if (preferencesEdited) return;
    const option = String(data.interfaceScale);
    if (![...scale.options].some(item => item.value === option))
      scale.add(new Option(Math.round(data.interfaceScale * 100) + "%", option));
    scale.value = option;
    launchAtSignIn.checked = data.launchAtSignIn;
    connectAtSignIn.checked = data.autoConnectOnStartup;
    connectAtSignIn.disabled = !launchAtSignIn.checked;
    connectOnOpen.checked = data.autoConnectOnOpen;
    reconnect.checked = data.autoReconnect;
    updates.checked = data.autoCheckUpdates;
    preferencesSave.disabled = true;
  }

  const themePicker = $("[data-theme-choice]");
  const sourceDialog = $("[data-source-dialog]");
  const updateDialog = $("[data-update-dialog]");
  const updateInstall = $("[data-update-install]");
  function renderUpdateProgress(value) {
    const progress = $("[data-update-progress]");
    const installing = Boolean(value?.atlasInstallRunning);
    progress.hidden = !value?.atlasInstall;
    progress.querySelector("progress").hidden = !installing;
    progress.querySelector("progress").value = value?.atlasInstall?.fraction || 0;
    $("[data-update-install-status]").textContent = value?.atlasInstall?.message || "";
    updateInstall.disabled = installing;
    for (const button of updateDialog.querySelectorAll("[data-update-dismiss]")) button.disabled = installing;
  }
  function dismissUpdate() {
    if (updateInstall.disabled) return;
    if (updateDialog.dataset.release) {
      dismissedUpdate = updateDialog.dataset.release;
      $("[data-update-offer]").hidden = true;
    }
    updateDialog.close();
  }
  updateDialog.addEventListener("cancel", event => { event.preventDefault(); dismissUpdate(); });
  window.atlasUpdateFailed = () => {
    renderUpdateProgress({ atlasInstall: { message: "Не удалось запустить установщик. Повтори попытку позже." } });
    void pollState();
  };
  function showUpdate(preview) {
    const installed = snapshot?.preferences?.atlasVersion?.split(".");
    const version = preview && installed?.length === 3
      ? [installed[0], installed[1], Number(installed[2]) + 1].join(".")
      : snapshot?.atlasUpdate;
    if (!version) return;
    updateDialog.dataset.release = preview ? "" : version;
    $("[data-update-kicker]").textContent = preview ? "Пример уведомления" : "Обновление";
    $("[data-update-title]").textContent = "Доступна версия " + version;
    $("[data-update-description]").textContent = "Морфоцит скачает обновление и перезапустится. Подписка, правила и настройки сохранятся.";
    renderUpdateProgress(preview ? null : snapshot);
    if (!updateDialog.open) updateDialog.showModal();
  }
  async function installUpdate() {
    const version = updateDialog.dataset.release;
    if (!version) { dismissUpdate(); return; }
    if (busy || updateInstall.disabled) return;
    renderUpdateProgress({ atlasInstallRunning: true, atlasInstall: { message: "Подключаюсь к GitHub…" } });
    if (!await command("update_install", { version })) renderUpdateProgress(snapshot);
  }
  async function checkForUpdate(button) {
    if (busy) return;
    button.disabled = true;
    button.textContent = "Проверяю…";
    try {
      await command("check_updates");
      if (snapshot?.atlasUpdate) showUpdate(false);
    } finally {
      if (!snapshot?.atlasChecking) {
        button.disabled = false;
        button.textContent = "Проверить обновления";
      }
    }
  }
  const sourceForm = $("[data-source-form]");
  const sourceInput = sourceForm.elements.namedItem("url");
  const sourceSave = $("[data-source-save]");
  const sourceStatus = $("[data-source-status]");
  let sourceReturn = null;
  let sourceLoad = null;
  let savingSource = false;
  function setSourceStatus(text) {
    sourceStatus.hidden = !text;
    sourceStatus.textContent = text;
  }
  async function openSource(button) {
    sourceReturn = button;
    sourceForm.reset();
    const ticket = sourceLoad = {};
    sourceInput.readOnly = true;
    sourceSave.disabled = true;
    setSourceStatus("Читаю сохранённую ссылку…");
    sourceDialog.showModal();
    try {
      const saved = await rpc("subscription_url");
      if (sourceLoad !== ticket || !sourceDialog.open) return;
      sourceInput.value = saved.url ?? "";
      setSourceStatus("");
    } catch (error) {
      if (sourceLoad === ticket && sourceDialog.open) setSourceStatus(error.message);
    } finally {
      if (sourceLoad === ticket && sourceDialog.open) {
        sourceLoad = null;
        sourceInput.readOnly = false;
        sourceSave.disabled = false;
        sourceInput.focus();
        sourceInput.setSelectionRange(sourceInput.value.length, sourceInput.value.length);
      }
    }
  }
  sourceDialog.addEventListener("cancel", event => { if (savingSource) event.preventDefault(); });
  sourceDialog.addEventListener("close", () => {
    if (sourceDialog.open) return;
    sourceLoad = null;
    sourceForm.reset();
    sourceReturn?.focus();
  });
  sourceForm.addEventListener("submit", async event => {
    event.preventDefault();
    if (sourceLoad || savingSource) return;
    if (busy) { setSourceStatus("Дождись завершения предыдущей операции."); return; }
    const url = sourceInput.value.trim();
    if (!url) { setSourceStatus("Вставь ссылку от провайдера."); sourceInput.focus(); return; }
    savingSource = sourceSave.disabled = true;
    sourceInput.readOnly = true;
    for (const button of all("[data-source-cancel]")) button.disabled = true;
    setSourceStatus("Загружаю и проверяю серверы…");
    try {
      await action(async () => {
        try {
          const result = await rpc("subscription_save", { url });
          install(result.state);
          if (result.applied) {
            sourceDialog.close();
            announce(result.message);
          } else setSourceStatus(result.message);
        } catch (error) { setSourceStatus(error.message); }
      });
    } finally {
      savingSource = sourceSave.disabled = false;
      sourceInput.readOnly = false;
      for (const button of all("[data-source-cancel]")) button.disabled = false;
    }
  });
  for (const name of Object.keys(themeCatalog)) themePicker.add(new Option(name));
  function setTransparency(value) {
    transparent = Math.max(0, Math.min(100, value));
    $("[data-glass-picker]").value = transparent;
    $("[data-glass-value]").textContent = transparent + "%";
    app.style.setProperty("--glass-density", (100 - transparent) + "%");
    app.style.setProperty("--connection-blur", (14 * (100 - transparent) / 100) + "px");
  }
  setTransparency(transparent);
  applyTheme(themeName(window.morphocyteStartup?.theme));
  prepareIntro();
  positionTabIndicator();
  const tabObserver = new ResizeObserver(positionTabIndicator);
  tabObserver.observe(nav);
  for (const tab of tabs) tabObserver.observe(tab);
  renderServerLists();
  renderOperations();
  window.atlasShown = playIntro;

  app.addEventListener("click", event => {
    const button = event.target.closest("button");
    if (!button) return;
    const has = key => button.hasAttribute("data-" + key);
    if (has("window-minimize")) native("minimize");
    else if (has("window-maximize")) native("maximize");
    else if (has("window-close")) native("close");
    else if (has("open-servers")) openPicker();
    else if (button.dataset.page) {
      go(button.dataset.page);
      if (state.page === "log") action(loadConnections);
    } else if (has("close-drawer")) closePicker(true);
    else if (has("all-countries")) openPicker(null, state.pending);
    else if (has("clear-country")) { inspectCountry(null); globe.focus(); }
    else if (has("center")) centerRoute(true);
    else if (button.dataset.zoom) zoomBy(button.dataset.zoom === "in" ? 1.2 : 1 / 1.2);
    else if (has("connect-selected")) action(async () => {
      const server = servers[state.pending];
      if (!server) return;
      install(await rpc("select", { name: server.name }));
      if (!state.active && snapshot?.requiresElevation) native("elevate");
      else if (!state.active) install(await rpc("power"));
      closePicker(true);
    }, { control: "picker", message: "Подключаю выбранный сервер…", button: "Подключаю…" });
    else if (has("server")) { state.pending = Number(button.dataset.server); refreshPreview(); renderServerLists(); }
    else if (has("full-server")) command("select", { name: servers[Number(button.dataset.fullServer)].name },
      { message: state.active ? "Переключаю сервер…" : "Выбираю сервер…" });
    else if (has("ping-all")) {
      if (snapshot?.pingRunning) command("ping_cancel");
      else command("ping_start", { method: pingMethod });
    }
    else if (has("ping-order")) { pingOrder = pingOrder === 1 ? -1 : 1; renderServerLists(); }
    else if (has("power")) togglePower();
    else if (has("speed")) command(snapshot?.speedRunning ? "speed_cancel" : "speed_start");
    else if (has("restart")) command("restart", {}, { control: "restart", message: "Перезапускаю ядро…", button: "Перезапуск…" });
    else if (has("tunnel")) {
      const label = state.tun ? "Выключаю TUN…" : "Включаю TUN…";
      command("mode", { mode: state.tun ? "Rules" : "FullTunnel" }, { control: "tunnel", message: label, button: label });
    }
    else if (has("refresh")) command("refresh", {}, { control: "refresh", message: "Обновляю подписку…" });
    else if (has("source")) openSource(button);
    else if (has("source-cancel")) sourceDialog.close();
    else if (has("update-preview")) showUpdate(true);
    else if (has("update-offer") || has("update-open")) showUpdate(false);
    else if (has("update-dismiss")) dismissUpdate();
    else if (has("update-install")) void installUpdate();
    else if (has("exit")) native("exit");
    else if (button.dataset.operation === "check_updates") checkForUpdate(button);
    else if (button.dataset.operation) command(button.dataset.operation);
    else if (has("classic")) command("classic");
    else if (has("subscription-toggle")) {
      const details = $("#atlas-subscription-details");
      details.hidden = !details.hidden;
      button.setAttribute("aria-expanded", String(!details.hidden));
      button.querySelector("span").textContent = details.hidden ? "Подробнее" : "Свернуть";
    } else if (has("remove-rule")) command("rule_remove", ruleArgs(Number(button.dataset.removeRule)));
    else if (has("apply")) command("apply");
    else if (has("log-refresh")) action(loadConnections);
    else if (has("check-connection")) action(async () => {
      const result = await rpc("check");
      install(result.state);
      $("[data-check-result]").textContent = result.result;
    });
    else if (has("log-sort")) {
      const key = button.dataset.logSort;
      logReverse = key === logSort && !logReverse;
      logSort = key;
      renderJournal();
    } else if (has("connection")) {
      logKey = filteredConnections[Number(button.dataset.connection)];
      renderJournal();
    } else if (has("log-more") && logKey) {
      const details = $("[data-log-details-panel]");
      details.hidden = !details.hidden;
      details.textContent = [logKey.process, logKey.destination, logKey.network, logKey.rule, logKey.route].filter(Boolean).join(" · ");
      button.setAttribute("aria-expanded", String(!details.hidden));
    } else if (has("toggle-text-log")) {
      const opening = $("[data-text-log]").hidden;
      $("[data-text-log]").hidden = !opening;
      if (opening) $("[data-events]").scrollTop = $("[data-events]").scrollHeight;
      button.setAttribute("aria-checked", String(opening));
    }
  });
  window.atlasTray = (command, resource) => {
    if (command === "power") {
      if (!snapshot || (!snapshot.ready && !snapshot.tracked)) {
        native("restore");
        go("settings");
      } else togglePower();
    } else if (command === "settings") go("settings");
    else if (command === "journal") { go("log"); action(loadConnections); }
    else if (command === "theme") {
      const name = Object.keys(themeCatalog).find(name => themeCatalog[name].iconResource === resource);
      if (name) chooseTheme(name);
    }
  };
  $("[data-window-header]").addEventListener("pointerdown", event => {
    if (event.button === 0 && !event.target.closest("button, a, input, select")) native("drag");
  });
  app.addEventListener("keydown", event => { if (event.key === "Escape" && state.popup && !sourceDialog.open) closePicker(true); });
  processPicker.addEventListener("keydown", event => {
    if (event.key === "Escape") { event.preventDefault(); setProcessPicker(false, true); }
  });
  processToggle.addEventListener("click", () => setProcessPicker(processPicker.inert, true));
  processRefresh.addEventListener("click", loadProcesses);
  processSearch.addEventListener("input", renderProcesses);
  processList.addEventListener("click", event => {
    const box = event.target;
    if (!box.matches("input:not(:disabled)")) return;
    const boxes = [...processList.querySelectorAll("input:not(:disabled)")];
    const end = boxes.indexOf(box);
    const start = event.shiftKey ? boxes.findIndex(item => item.value === processAnchor) : -1;
    const range = start < 0 ? [box] : boxes.slice(Math.min(start, end), Math.max(start, end) + 1);
    for (const item of range) {
      if (box.checked) selectedProcesses.add(item.value);
      else selectedProcesses.delete(item.value);
    }
    processAnchor = box.value;
    syncProcessSelection();
  });
  processAdd.addEventListener("click", async () => {
    if (busy || !selectedProcesses.size) return;
    const names = [...selectedProcesses];
    processAdd.disabled = true;
    const applied = await command("rule_add_processes", { names, route: processRoute.value });
    if (applied) {
      setProcessPicker(false, true);
      announce("Правила для приложений добавлены в черновик.");
    } else syncProcessSelection();
  });
  $("[data-server-search]").addEventListener("input", renderServerLists);
  $("[data-full-server-search]").addEventListener("input", renderServerLists);
  $("[data-log-search]").addEventListener("input", renderJournal);
  $("[data-add-rule]").addEventListener("submit", event => {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = new FormData(form);
    action(async () => {
      install(await rpc("rule_add", { kind: fields.get("type"), value: fields.get("target"), route: fields.get("route") }));
      form.elements.target.value = "";
    });
  });
  $("[data-rules-list]").addEventListener("change", event => {
    const index = Number(event.target.closest("[data-rule]").dataset.rule);
    command(event.target.type === "checkbox" ? "rule_toggle" : "rule_route", { ...ruleArgs(index), route: event.target.value });
  });
  $("[data-connections]").addEventListener("change", event => {
    const connection = filteredConnections[Number(event.target.dataset.connection)];
    if (connection && event.target.value !== "current") command("route_connection", { address: connection.destination, route: event.target.value });
  });
  themePicker.addEventListener("change", () => chooseTheme(themePicker.value));
  for (const picker of all("[data-ping-method]")) {
    picker.addEventListener("change", () => {
      pingMethod = picker.value;
      for (const other of all("[data-ping-method]")) other.value = pingMethod;
    });
  }
  preferences.addEventListener("change", event => {
    if (!event.target.name) return;
    preferencesEdited = true;
    connectAtSignIn.disabled = !launchAtSignIn.checked;
    preferencesSave.disabled = false;
  });
  preferences.addEventListener("submit", async event => {
    event.preventDefault();
    if (busy || !snapshot) return;
    preferencesSave.disabled = true;
    const saved = await action(async () => {
      const result = await rpc("preferences", {
        scale: Number(scale.value), automaticUpdates: updates.checked,
        launchAtSignIn: launchAtSignIn.checked, connectAtSignIn: connectAtSignIn.checked,
        connectOnOpen: connectOnOpen.checked, reconnect: reconnect.checked
      });
      preferencesEdited = false;
      install(result);
    });
    if (!saved) preferencesSave.disabled = false;
  });
  $("[data-glass-picker]").addEventListener("input", event => {
    setTransparency(Number(event.target.value));
    try { localStorage.setItem("atlas-transparency", String(transparent)); } catch {}
  });
  new ResizeObserver(scheduleDraw).observe(globe);
  requestAnimationFrame(function waitForFirstDraw() {
    requestAnimationFrame(globePainted ? () => native("ui-ready") : waitForFirstDraw);
  });
  void pollState();
  setInterval(pollState, 2500);
  setInterval(() => { if (snapshot?.speedRunning) void pollState(); }, 400);
})();
