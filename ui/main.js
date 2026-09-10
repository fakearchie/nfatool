const invoke = window.__TAURI__.core.invoke;
const listen = window.__TAURI__.event.listen;

const el = (id) => document.getElementById(id);
const tiles = el("tiles");
const pickerHeadline = el("pickerHeadline");
const toastWrap = el("toastWrap");
const backBtn = el("backBtn");
const footlinks = el("footlinks");
const moreBtn = el("moreBtn");
const emptyNote = el("emptyNote");
const filterBar = el("filterBar");
const searchInput = el("searchInput");
const colorFilterEl = el("colorFilter");
const colorMenu = el("colorMenu");

let accounts = [];
let intel = {};
let meta = {};
let searchQuery = "";
let colorFilter = "";
let filterOpen = false;
let lastIntelAt = 0;
let lastIntelIds = "";
let confirmHandler = null;
let currentView = "picker";
const ROW_CAP = 8; // Steam shows at most 8 cached users
let showAllAccounts = false;
let settings = {
  always_invisible: true,
  cancel_downloads_on_login: false,
  streamer_mode: false,
  launch_steam_minimized: false,
  mute_notifications_on_login: false,
  fetch_missing_avatars: true,
  hide_from_capture: false,
  auto_remove_rejected: false,
  check_updates_on_start: true,
  steam_api_key: "",
  cs2_launch_options: "",
  cs2_config_source: "",
  suppress_workshop_downloads: false,
  disable_remote_play: false,
  launch_cs2_on_login: false,
};

const VIEWS = {
  picker: el("pickerView"),
  signing: el("signingView"),
  account: el("accountView"),
  add: el("addView"),
  settings: el("settingsView"),
  lock: el("lockView"),
};

function toast(message, kind = "ok") {
  while (toastWrap.firstElementChild) toastWrap.firstElementChild.remove();
  const node = document.createElement("div");
  node.className = "toast " + kind;
  node.textContent = message;
  toastWrap.appendChild(node);
  setTimeout(() => {
    node.style.opacity = "0";
    node.style.transition = "opacity 0.2s ease";
    setTimeout(() => node.remove(), 220);
  }, 3500);
}

function formatError(e) {
  return typeof e === "string" ? e : String(e);
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])
  );
}
const escapeAttr = escapeHtml;

function paintBackdrop() {
  const host = el("capsules");
  if (!host) return;
  let seed = 0x9e3779b9;
  const rand = () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const palette = [
    "#24313f", "#39404d", "#4b3f2e", "#2b3d33", "#3b2c3d",
    "#26343f", "#433c31", "#2e3441", "#4a3532", "#2c3f47",
    "#373f4f", "#1b2028", "#544733", "#26393a", "#3c3349",
    "#1f2731", "#4d4436", "#2a3140",
  ];
  let html = "";
  for (let i = 0; i < 220; i++) {
    const c = palette[Math.floor(rand() * palette.length)];
    const a = 0.45 + rand() * 0.55;
    html += `<i style="background:${c};opacity:${a.toFixed(2)}"></i>`;
  }
  host.innerHTML = html;
}

function showView(name) {
  currentView = name;
  for (const [key, node] of Object.entries(VIEWS)) {
    node.classList.toggle("hidden", key !== name);
  }
  const bare = name === "signing" || name === "lock";
  backBtn.classList.toggle("hidden", name === "picker" || bare);
  footlinks.classList.toggle("hidden", bare);
}

const isPlaceholderName = (name, steamid) => !name || name === steamid;

function displayAccount(acc, index) {
  if (settings.streamer_mode) {
    return { ...acc, display_name: `Account ${index + 1}`, account_name: "••••" };
  }
  const fromSteam = intel[acc.steamid]?.persona_name;
  if (fromSteam && isPlaceholderName(acc.display_name, acc.steamid)) {
    return { ...acc, display_name: fromSteam };
  }
  return acc;
}

const DEFAULT_AVATAR_SVG =
  '<svg class="default-avatar" viewBox="0 0 64 64" preserveAspectRatio="xMidYMid slice" aria-hidden="true">' +
  '<rect width="64" height="64" fill="#2a2d35"/>' +
  '<circle cx="32" cy="24.5" r="11" fill="#5a616d"/>' +
  '<path d="M11 62c0-11.6 9.4-19 21-19s21 7.4 21 19z" fill="#5a616d"/></svg>';

function iconInner(view) {
  if (view.avatar) {
    return `<img src="${escapeAttr(view.avatar)}" alt="" />`;
  }
  if (view.avatar_url) {
    return `<img class="remote-avatar" src="${escapeAttr(view.avatar_url)}" alt="" />`;
  }
  return DEFAULT_AVATAR_SVG;
}

document.addEventListener(
  "error",
  (e) => {
    const img = e.target;
    if (img instanceof HTMLImageElement && img.classList.contains("remote-avatar")) {
      img.outerHTML = DEFAULT_AVATAR_SVG;
    }
  },
  true
);

const ADD_SVG =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="M12 4v16M4 12h16"/></svg>';
function statusClass(info) {
  if (!info) return "";
  if (info.game) return "is-ingame";
  switch (info.persona_state) {
    case 1:
      return "is-online";
    case 2: // busy
    case 3: // away
    case 4: // snooze
      return "is-away";
    default:
      return "";
  }
}

const COLORS = ["red", "amber", "green", "blue", "purple", "gray"];
const COOLDOWNS = [
  { label: "20h", seconds: 20 * 3600 },
  { label: "7d", seconds: 7 * 86400 },
  { label: "31d", seconds: 31 * 86400 },
  { label: "181d", seconds: 181 * 86400 },
];

const metaFor = (steamid) => meta[steamid] || {};
const nowUnix = () => Math.floor(Date.now() / 1000);

function onCooldown(m) {
  return Boolean(m.cooldown_until && m.cooldown_until > nowUnix());
}

function remainingText(until) {
  let left = Math.max(0, until - nowUnix());
  const days = Math.floor(left / 86400);
  left -= days * 86400;
  const hours = Math.floor(left / 3600);
  left -= hours * 3600;
  const minutes = Math.floor(left / 60);
  if (days) return `${days}d ${hours}h`;
  if (hours) return `${hours}h ${minutes}m`;
  if (minutes) return `${minutes}m`;
  return "under a minute";
}

// Dates use toLocaleString deliberately; NUMBERS must not - on a German locale
// it renders 18432 as "18.432", which reads as eighteen-point-four.
function agoText(unix) {
  const secs = Math.max(0, nowUnix() - unix);
  if (secs < 90) return "just now";
  const minutes = Math.floor(secs / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(unix * 1000).toLocaleDateString();
}

async function loadMeta() {
  try {
    meta = await invoke("get_metadata");
  } catch (e) {
    console.warn("metadata unavailable:", e);
    meta = {};
  }
}

function swatchesHtml(selected, extraClass = "") {
  return ["", ...COLORS]
    .map((c) => {
      const on = c === selected ? " is-on" : "";
      const label = c ? `Tag ${c}` : "No tag";
      return `<button type="button" class="swatch${on} ${extraClass}" data-color="${c}" title="${label}" aria-label="${label}"></button>`;
    })
    .join("");
}

function banTitle(info) {
  const parts = [];
  if (info.vac_bans > 0) parts.push(`${info.vac_bans} VAC ban${info.vac_bans > 1 ? "s" : ""}`);
  else if (info.vac_banned) parts.push("VAC banned");
  if (info.game_bans > 0) parts.push(`${info.game_bans} game ban${info.game_bans > 1 ? "s" : ""}`);
  if (info.community_banned) parts.push("Community banned");
  if (info.trade_ban && info.trade_ban !== "none") parts.push(`Trade: ${info.trade_ban}`);
  return parts.join(" · ") || "Banned";
}

function render() {
  const hasAccounts = accounts.length > 0;
  pickerHeadline.textContent = hasAccounts ? "Who's playing?" : "Add your first account";

  // Most recently tried first, so the account you just signed into leads the row.
  // last_used is stamped on the attempt, not on Steam accepting it, which is what
  // you want here: the one you just reached for is the one you reach for again.
  // Steam's own MostRecent flag only breaks ties between accounts never tried here,
  // and the original index keeps the rest in a stable order.
  const ordered = accounts
    .map((acc, index) => ({ acc, index }))
    .sort((a, b) => {
      const usedA = metaFor(a.acc.steamid).last_used || 0;
      const usedB = metaFor(b.acc.steamid).last_used || 0;
      if (usedA !== usedB) return usedB - usedA;
      const recentA = Number(a.acc.most_recent);
      const recentB = Number(b.acc.most_recent);
      if (recentA !== recentB) return recentB - recentA;
      return a.index - b.index;
    });

  const query = searchQuery.trim().toLowerCase();
  const matching = ordered.filter(({ acc, index }) => {
    if (colorFilter && metaFor(acc.steamid).color !== colorFilter) return false;
    if (!query) return true;
    const view = displayAccount(acc, index);
    return (
      view.display_name.toLowerCase().includes(query) ||
      view.account_name.toLowerCase().includes(query)
    );
  });

  const filtering = Boolean(query || colorFilter);
  const overflowing = matching.length > ROW_CAP && !filtering;
  const shown = overflowing && !showAllAccounts ? matching.slice(0, ROW_CAP) : matching;

  emptyNote.classList.toggle("hidden", !(filtering && matching.length === 0));

  tiles.parentElement.classList.toggle(
    "expanded",
    (overflowing && showAllAccounts) || shown.length > ROW_CAP
  );
  moreBtn.classList.toggle("hidden", !overflowing);
  moreBtn.classList.toggle("is-on", showAllAccounts);
  if (overflowing) {
    const tip = showAllAccounts
      ? `Show ${ROW_CAP} most recent`
      : `Show all ${matching.length} accounts`;
    moreBtn.dataset.tip = tip;
    moreBtn.setAttribute("aria-label", tip);
  }

  const accountTiles = shown
    .map(({ acc, index }) => {
      const view = displayAccount(acc, index);
      const info = intel[acc.steamid];
      const m = metaFor(acc.steamid);
      const state = [
        info?.banned ? "is-banned" : "",
        onCooldown(m) ? "is-cooldown" : "",
        statusClass(info),
      ]
        .filter(Boolean)
        .join(" ");
      const color = m.color ? ` data-color="${escapeAttr(m.color)}"` : "";
      return `
        <div class="user existing ${state}"${color} role="button" tabindex="0" data-account="${escapeAttr(acc.steamid)}" aria-label="${escapeAttr(view.display_name)}, open account">
          <div class="icon">${iconInner(view)}</div>
          <div class="name persona">${escapeHtml(view.display_name)}</div>
          <div class="name account">${escapeHtml(view.account_name)}</div>
        </div>`;
    })
    .join("");

  const addTile = filtering
    ? ""
    : `<div class="user new" role="button" tabindex="0" id="addTile" aria-label="Add Account">
       <div class="icon">${ADD_SVG}</div>
       <div class="name persona">Add Account</div>
       <div class="name account"></div>
     </div>`;
  tiles.innerHTML = accountTiles + addTile;
  renderSelection();
  syncCs2Source();
}

async function refreshIntel(force = false) {
  const steamids = accounts.map((a) => a.steamid);
  if (!steamids.length) {
    intel = {};
    lastIntelIds = "";
    return;
  }
  const roster = steamids.join(",");
  const rosterChanged = roster !== lastIntelIds;
  if (!force && !rosterChanged && Date.now() - lastIntelAt < 30000) return;
  lastIntelAt = Date.now();
  lastIntelIds = roster;
  try {
    const result = await invoke("fetch_account_intel", { steamids });
    if (accounts.map((a) => a.steamid).join(",") !== roster) return;
    intel = result;
    render();
  } catch (e) {
    console.warn("account intel unavailable:", e);
  }
}

async function refresh() {
  try {
    accounts = await invoke("list_accounts");
    await loadMeta();
    render();
    refreshIntel();
  } catch (e) {
    accounts = [];
    render();
    toast(formatError(e), "err");
  }
}

const accountAvatar = el("accountAvatar");
const accountPersona = el("accountPersona");
const accountLogin = el("accountLogin");
const accountFacts = el("accountFacts");
const cooldownChips = el("cooldownChips");
const colorPick = el("colorPick");
const accountSignIn = el("accountSignIn");
let openAccountId = null;

function statusText(info) {
  if (!info) return "Unknown";
  if (info.game) return `In game: ${info.game}`;
  switch (info.persona_state) {
    case 0:
      return "Offline";
    case 1:
      return "Online";
    case 2:
      return "Busy";
    case 3:
      return "Away";
    case 4:
      return "Snooze";
    default:
      return "Unknown";
  }
}

function factRow(label, value, kind, copy, hint) {
  const attr = copy ? ` data-copy="${escapeAttr(copy)}"` : "";
  const tip = hint || (copy ? "Click to copy" : "");
  const title = tip ? ` title="${escapeAttr(tip)}"` : "";
  return `<div class="fact${kind ? ` ${kind}` : ""}"><dt>${escapeHtml(label)}</dt><dd${attr}${title}>${escapeHtml(value)}</dd></div>`;
}

function actionFact(label, value, action, kind, hint) {
  return `<div class="fact${kind ? ` ${kind}` : ""}"><dt>${escapeHtml(label)}</dt><dd data-act="${escapeAttr(action)}"${hint ? ` title="${escapeAttr(hint)}"` : ""}>${escapeHtml(value)}</dd></div>`;
}

function codeFact(acc) {
  if (!acc.has_token) {
    return factRow("Login code", "Not saved", "bad");
  }
  if (acc.token_expires) {
    const left = acc.token_expires - nowUnix();
    if (left <= 0) return factRow("Login code", "Expired", "bad", `token:${acc.steamid}`);
    return factRow("Login code", `Expires in ${remainingText(acc.token_expires)}`, "", `token:${acc.steamid}`);
  }
  return factRow("Login code", "Saved", "", `token:${acc.steamid}`);
}

function openAccount(steamid) {
  const index = accounts.findIndex((a) => a.steamid === steamid);
  const acc = accounts[index];
  if (!acc) return;
  const view = displayAccount(acc, index);
  const info = intel[steamid];
  const m = metaFor(steamid);
  const cooling = onCooldown(m);

  openAccountId = steamid;
  accountAvatar.innerHTML = iconInner(view);
  accountAvatar.className = `account-avatar${info?.banned ? " is-banned" : ""}`;
  accountPersona.textContent = view.display_name;
  accountPersona.className = `account-persona ${statusClass(info)}`;
  accountLogin.textContent = view.account_name;
  accountLogin.classList.toggle("hidden", view.account_name === view.display_name);

  const rows = [factRow("Status", statusText(info))];
  if (m.last_used) {
    rows.push(factRow("Last used", agoText(m.last_used)));
  }
  if (info && info.level != null) {
    rows.push(factRow("Steam level", String(info.level)));
  }
  rows.push(codeFact(acc));
  rows.push(
    info
      ? factRow("Bans", info.banned ? banTitle(info) : "None", info.banned ? "bad" : "good")
      : factRow("Bans", "Add a Steam Web API key in Settings")
  );
  rows.push(
    settings.streamer_mode
      ? factRow("Steam ID", "••••••••••")
      : factRow("Steam ID", acc.steamid, "", acc.steamid)
  );
  accountFacts.innerHTML = rows.join("");
  accountFacts.classList.toggle(
    "is-scrollable",
    accountFacts.scrollHeight > accountFacts.clientHeight
  );

  cooldownChips.innerHTML =
    COOLDOWNS.map(
      (c) =>
        `<button type="button" class="chip" data-cooldown="${c.seconds}">${c.label}</button>`
    ).join("") +
    (cooling
      ? `<button type="button" class="chip is-on" data-cooldown="0" title="Clear cooldown">${escapeHtml(
          remainingText(m.cooldown_until)
        )} left ✕</button>`
      : "");

  colorPick.innerHTML = swatchesHtml(m.color || "");

  accountSignIn.textContent = cooling ? "Sign in anyway" : "Sign in";

  showView("account");
}

function renderColorFilter() {
  colorFilterEl.innerHTML = swatchesHtml(colorFilter, "filter-swatch");
}

function setFilterOpen(open, { focus = true } = {}) {
  filterOpen = open;
  filterBar.classList.toggle("hidden", !open);
  if (open) {
    renderColorFilter();
    if (focus) searchInput.focus();
  } else {
    searchQuery = "";
    colorFilter = "";
    searchInput.value = "";
    render();
  }
}

function onSearchInput() {
  searchQuery = searchInput.value;
  render();
}

async function applyColor(steamid, color) {
  try {
    await invoke("set_account_color", { steamid, color });
    meta = { ...meta, [steamid]: { ...metaFor(steamid), color } };
    render();
    if (currentView === "account" && openAccountId === steamid) {
      colorPick.innerHTML = swatchesHtml(color);
    }
  } catch (e) {
    toast(formatError(e), "err");
  }
}

let colorMenuFor = null;

function openColorMenu(steamid, x, y) {
  colorMenuFor = steamid;
  colorMenu.className = "ctxmenu ctxmenu-stack";
  colorMenu.innerHTML =
    `<div class="swatches">${swatchesHtml(metaFor(steamid).color || "")}</div>` +
    `<div class="ctxmenu-sep"></div>` +
    `<button type="button" class="ctxitem" data-cmd="copy-code">Copy login code</button>` +
    `<button type="button" class="ctxitem" data-cmd="copy-name">Copy username</button>` +
    `<button type="button" class="ctxitem" data-cmd="profile">Open Steam profile</button>` +
    `<div class="ctxmenu-sep"></div>` +
    `<button type="button" class="ctxitem" data-cmd="remove">Remove account</button>`;
  colorMenu.classList.remove("hidden");
  colorMenu.style.left = "0px";
  colorMenu.style.top = "0px";
  const box = colorMenu.getBoundingClientRect();
  const left = Math.min(Math.max(4, x), window.innerWidth - box.width - 4);
  const top = Math.min(Math.max(4, y), window.innerHeight - box.height - 4);
  colorMenu.style.left = `${left}px`;
  colorMenu.style.top = `${top}px`;
}

function closeColorMenu() {
  colorMenu.classList.add("hidden");
  colorMenuFor = null;
}

async function runCopy(promise) {
  try {
    const msg = await promise;
    if (msg) toast(msg, "ok");
  } catch (e) {
    toast(formatError(e), "err");
  }
}

async function copyFactValue(dd) {
  const spec = dd.dataset.copy;
  const original = dd.textContent;
  try {
    if (spec.startsWith("token:")) {
      await invoke("copy_token", { steamid: spec.slice(6) });
    } else {
      await invoke("copy_text", { text: spec });
    }
    dd.textContent = "Copied";
    dd.classList.add("copied");
    setTimeout(() => {
      dd.textContent = original;
      dd.classList.remove("copied");
    }, 1100);
  } catch (e) {
    toast(formatError(e), "err");
  }
}

async function applyCooldown(steamid, seconds) {
  const until = seconds > 0 ? nowUnix() + seconds : null;
  try {
    await invoke("set_account_cooldown", { steamid, until });
    meta = { ...meta, [steamid]: { ...metaFor(steamid), cooldown_until: until } };
    render();
    if (currentView === "account" && openAccountId === steamid) openAccount(steamid);
  } catch (e) {
    toast(formatError(e), "err");
  }
}

const selBar = el("selBar");
const selCount = el("selCount");
const selColor = el("selColor");
const selected = new Set();

function renderSelection() {
  for (const id of [...selected]) {
    if (!accounts.some((a) => a.steamid === id)) selected.delete(id);
  }
  const n = selected.size;
  selBar.classList.toggle("hidden", n === 0);
  if (n > 0) {
    selCount.textContent = `${n} selected`;
    selColor.innerHTML = swatchesHtml("");
  }
  for (const tile of tiles.querySelectorAll("[data-account]")) {
    tile.classList.toggle("is-selected", selected.has(tile.dataset.account));
  }
}

function toggleSelected(steamid) {
  if (selected.has(steamid)) selected.delete(steamid);
  else selected.add(steamid);
  renderSelection();
}

function clearSelection() {
  if (selected.size === 0) return false;
  selected.clear();
  renderSelection();
  return true;
}

async function tagSelected(color) {
  const ids = [...selected];
  try {
    for (const steamid of ids) {
      await invoke("set_account_color", { steamid, color });
      meta = { ...meta, [steamid]: { ...metaFor(steamid), color } };
    }
    render();
  } catch (e) {
    toast(formatError(e), "err");
  }
}

function askRemoveSelected() {
  const ids = [...selected];
  const n = ids.length;
  openConfirm(
    "Remove accounts",
    `Remove ${n} account${n > 1 ? "s" : ""} from this app? Steam itself is not touched.`,
    `Remove ${n}`,
    async () => {
      const failed = [];
      for (const steamid of ids) {
        try {
          await invoke("remove_account", { steamid });
        } catch (e) {
          failed.push(formatError(e));
        }
      }
      selected.clear();
      await refresh();
      if (failed.length) toast(`${failed.length} could not be removed.`, "err");
    }
  );
}

const signingAvatar = el("signingAvatar");
const signingName = el("signingName");
const signingText = el("signingText");

let awaitingSignIn = null;
let signInSeq = 0;

async function signIn(steamid) {
  const index = accounts.findIndex((a) => a.steamid === steamid);
  const acc = accounts[index];
  const view = acc ? displayAccount(acc, index) : null;

  if (view) {
    signingAvatar.innerHTML = iconInner(view);
    signingName.textContent = view.display_name;
  } else {
    signingAvatar.textContent = "";
    signingName.textContent = "";
  }
  signingText.textContent = "Signing in";
  el("signingSkip").classList.add("hidden");
  showView("signing");

  const settled = invoke("sign_in", { steamid });
  const minimumDwell = new Promise((r) => setTimeout(r, 700));

  try {
    await Promise.all([settled, minimumDwell]);
  } catch (e) {
    await minimumDwell;
    await refresh();
    showView("picker");
    return toast(formatError(e), "err");
  }

  await refresh();

  // The watcher runs on its own thread and answers with an event, so the window
  // stays responsive while Steam starts.
  const attempt = ++signInSeq;
  awaitingSignIn = { steamid, attempt, name: view ? view.display_name : "this account" };
  signingText.textContent = "Waiting for Steam";
  el("signingSkip").classList.remove("hidden");
  invoke("watch_sign_in", { steamid, attempt }).catch(() => finishSignIn(steamid, "ok", attempt));
}

function nameFor(steamid) {
  const index = accounts.findIndex((a) => a.steamid === steamid);
  return index < 0 ? "this account" : displayAccount(accounts[index], index).display_name;
}

function finishSignIn(steamid, verdict, attempt) {
  // Match on the attempt, not the account. Skipping a wait leaves its watcher running
  // for the rest of its 90 seconds, and signing into the same account again gave two
  // watchers the window could not tell apart: the abandoned one's "unknown" cancelled
  // the live attempt, and the real answer was dropped.
  // Attempt 0 comes from the tray, where nothing on screen is waiting. There is no
  // signing view to leave, but a refused code still deserves the same offer.
  if (attempt === 0) {
    if (verdict === "rejected") offerRemoval(steamid, nameFor(steamid));
    else if (verdict === "other") toast("Steam signed in as a different account.", "err");
    return;
  }
  if (!awaitingSignIn || awaitingSignIn.attempt !== attempt) return;
  const { name } = awaitingSignIn;
  awaitingSignIn = null;
  if (currentView !== "signing") return;
  showView("picker");
  if (verdict === "rejected") offerRemoval(steamid, name);
  else if (verdict === "other") toast("Steam signed in as a different account.", "err");
}

el("signingSkip").addEventListener("click", () => {
  awaitingSignIn = null;
  showView("picker");
});

listen("sign-in-result", (e) => {
  const [steamid, verdict, attempt] = e.payload || [];
  finishSignIn(steamid, verdict, attempt);
});

async function removeRejected(steamid) {
  try {
    await invoke("remove_account", { steamid });
    await refresh();
    return true;
  } catch (e) {
    toast(formatError(e), "err");
    return false;
  }
}

function offerRemoval(steamid, name) {
  // Only ever reached when Steam itself said the code was refused, so acting on it
  // without asking is the setting doing what it says rather than a guess.
  if (settings.auto_remove_rejected) {
    removeRejected(steamid).then((gone) => {
      if (gone) toast(`Steam refused the code for ${name}, so it was removed.`, "err");
    });
    return;
  }

  openConfirm(
    "Steam rejected this login code",
    `Steam refused the saved code for ${name} and is asking for a manual sign in. ` +
      `Codes stop working after a password change, or after signing out of all devices. ` +
      `Remove ${name}?`,
    "Remove",
    () => removeRejected(steamid)
  );
}

const importInput = el("importInput");
const importStatus = el("importStatus");
const doImportBtn = el("doImportBtn");
const addForm = document.querySelector(".add-form");
const stageEl = document.querySelector(".stage");

function codeCount() {
  return importInput.value.split("\n").filter((line) => line.trim()).length;
}

function syncImportForm() {
  importInput.style.height = "auto";
  const wanted = Math.max(40, importInput.scrollHeight);
  importInput.style.height = wanted + "px";

  const room =
    stageEl.getBoundingClientRect().bottom - addForm.getBoundingClientRect().bottom - 8;
  if (room < 0) {
    const rendered = importInput.getBoundingClientRect().height;
    importInput.style.height = Math.max(40, rendered + room) + "px";
  }

  const n = codeCount();
  doImportBtn.disabled = n === 0;
  doImportBtn.textContent = n > 1 ? `Import ${n} codes` : "Import";
}

function setImportStatus(text, kind) {
  importStatus.textContent = text;
  importStatus.className = kind === "err" ? "form-msg err" : "form-msg";
  syncImportForm();
}

function openAdd() {
  importInput.value = "";
  showView("add");
  setImportStatus("", "ok");
}

async function pasteIntoImport() {
  try {
    const text = await invoke("read_clipboard");
    importInput.value = text.trim();
    setImportStatus("", "ok");
  } catch (e) {
    setImportStatus(formatError(e), "err");
  }
}

async function runImport(promise) {
  setImportStatus("Importing…", "ok");
  try {
    const msg = await promise;
    await refresh();
    showView("picker");
    toast(msg, "ok");
  } catch (e) {
    const text = formatError(e);
    setImportStatus(text === "__cancelled__" ? "" : text, "err");
  }
}

async function importManual() {
  const payload = importInput.value.trim();
  if (!payload) {
    setImportStatus("No codes entered.", "err");
    return;
  }

  const n = codeCount();
  setImportStatus("Importing…", "ok");

  try {
    const msg = await invoke("import_account", { payload });
    await refresh();
    showView("picker");
    toast(settings.streamer_mode ? `Imported ${n} account${n > 1 ? "s" : ""}.` : msg, "ok");
  } catch (e) {
    setImportStatus(formatError(e), "err");
  }
}

function askRemove(steamid) {
  const idx = accounts.findIndex((a) => a.steamid === steamid);
  const acc = accounts[idx];
  const name = settings.streamer_mode
    ? `Account ${idx + 1}`
    : acc
      ? acc.display_name
      : "this account";
  openConfirm("Remove account", `Remove ${name}?`, "Remove", async () => {
    try {
      await invoke("remove_account", { steamid });
      await refresh();
      if (currentView === "account") {
        openAccountId = null;
        showView("picker");
      }
    } catch (e) {
      toast(formatError(e), "err");
    }
  });
}

function askClearSteam() {
  openConfirm(
    "Sign Steam out",
    "Clear Steam's cached login tokens on this PC? Your saved accounts stay here and can sign in again.",
    "Sign out",
    async () => {
      try {
        const msg = await invoke("clear_steam");
        await refresh();
        toast(msg, "ok");
      } catch (e) {
        toast(formatError(e), "err");
      }
    }
  );
}

const confirmModal = el("confirmModal");
const confirmInput = el("confirmInput");

function openPrompt(title, text, label, placeholder, handler, type = "text") {
  openConfirm(title, text, label, handler, "primary");
  confirmInput.value = "";
  confirmInput.type = type;
  confirmInput.placeholder = placeholder;
  confirmInput.classList.remove("hidden");
  setTimeout(() => confirmInput.focus(), 60);
}

function openConfirm(title, text, label, handler, tone = "danger") {
  confirmInput.classList.add("hidden");
  el("confirmTitle").textContent = title;
  el("confirmText").textContent = text;
  const yes = el("confirmYes");
  yes.textContent = label;
  yes.className = `lbtn lbtn-${tone}`;
  confirmHandler = handler;
  confirmModal.classList.remove("hidden");
  setTimeout(
    () => confirmModal.querySelector('[data-action="close-confirm"]')?.focus(),
    50
  );
}
function closeConfirm() {
  confirmModal.classList.add("hidden");
  confirmHandler = null;
}

const settingsView = VIEWS.settings;

function syncCs2Source() {
  const current = settings.cs2_config_source || "";
  const options = [`<option value="">Don't copy</option>`];
  for (const [index, acc] of accounts.entries()) {
    const view = displayAccount(acc, index);
    options.push(
      `<option value="${escapeAttr(acc.steamid)}"${acc.steamid === current ? " selected" : ""}>${escapeHtml(view.display_name)}</option>`
    );
  }
  if (current && !accounts.some((a) => a.steamid === current)) {
    options.push(`<option value="${escapeAttr(current)}" selected>(account no longer saved)</option>`);
  }
  cs2SourceSelect.innerHTML = options.join("");
}

function syncSettingsForm() {
  for (const input of settingsView.querySelectorAll("[data-setting]")) {
    input.checked = Boolean(settings[input.dataset.setting]);
  }
  syncCs2Source();
  const launch = settings.cs2_launch_options || "";
  if (cs2LaunchInput.value !== launch && document.activeElement !== cs2LaunchInput) {
    cs2LaunchInput.value = launch;
  }
  const stored = settings.steam_api_key || "";
  if (apiKeyInput.value.trim() !== stored) {
    apiKeyInput.value = stored;
    setKeyState("", "");
  }
}

function openSettings() {
  syncSettingsForm();
  showView("settings");
}

async function loadSettings() {
  try {
    settings = await invoke("get_settings");
    syncSettingsForm();
    render();
  } catch (e) {
    toast(formatError(e), "err");
  }
}

async function persistSettings() {
  try {
    await invoke("save_settings", { settings });
    render();
  } catch (e) {
    toast(formatError(e), "err");
    syncSettingsForm();
  }
}

const apiKeyInput = el("apiKeyInput");
const apiKeyState = el("apiKeyState");
const cs2LaunchInput = el("cs2LaunchInput");
const cs2SourceSelect = el("cs2SourceSelect");
let apiKeyTimer = null;
let cs2LaunchTimer = null;

function setKeyState(text, kind) {
  apiKeyState.textContent = text;
  apiKeyState.className = "key-state" + (kind ? " " + kind : "");
}

async function saveApiKey(key) {
  settings = { ...settings, steam_api_key: key };
  await persistSettings();
}

function onApiKeyInput() {
  const key = apiKeyInput.value.trim();
  clearTimeout(apiKeyTimer);

  if (!key) {
    setKeyState("", "");
    apiKeyTimer = setTimeout(() => saveApiKey(""), 400);
    return;
  }
  if (key.length !== 32) {
    setKeyState("Keys are 32 characters", "err");
    return;
  }

  setKeyState("Checking…", "");
  apiKeyTimer = setTimeout(async () => {
    await saveApiKey(key);
    try {
      const ok = await invoke("validate_api_key", { key });
      if (apiKeyInput.value.trim() !== key) return;
      setKeyState(ok ? "Key accepted" : "Steam rejected this key", ok ? "ok" : "err");
      if (ok) refreshIntel(true);
    } catch (e) {
      if (apiKeyInput.value.trim() !== key) return;
      setKeyState(formatError(e), "err");
    }
  }, 600);
}

const versionText = el("versionText");
const updateBtn = el("updateBtn");
const logBox = el("logBox");

function offerUpdate(info) {
  openConfirm(
    `Version ${info.latest} is available`,
    info.notes || "No release notes.",
    "Install now",
    async () => {
      toast("Downloading the update. The app will close to install it.", "ok");
      try {
        const installed = await invoke("install_update");
        // The installer normally replaces the app and this never runs. Reaching here
        // means the updater found nothing to install, which is worth saying rather
        // than leaving the toast above as the last word.
        if (!installed) toast("There was no update to install after all.", "err");
      } catch (e) {
        // Never leave the user stuck on a failed install: the download page still works.
        toast(formatError(e), "err");
        invoke("open_url", { url: info.url }).catch(() => {});
      }
    },
    "primary"
  );
}

async function checkForUpdate() {
  updateBtn.disabled = true;
  updateBtn.textContent = "Checking…";
  try {
    const info = await invoke("check_for_update");
    if (!info.available) {
      toast(`You're on the latest version (${info.current}).`, "ok");
      return;
    }
    offerUpdate(info);
  } catch (e) {
    toast(formatError(e), "err");
  } finally {
    updateBtn.disabled = false;
    updateBtn.textContent = "Check for updates";
  }
}

// Quiet on purpose: this runs without being asked, so it speaks up only when there
// is something to install. A failed check is not the user's problem to hear about.
async function checkForUpdateQuietly() {
  if (!settings.check_updates_on_start) return;
  try {
    const info = await invoke("check_for_update");
    if (info.available) offerUpdate(info);
  } catch (e) {
    console.warn("update check failed:", e);
  }
}

async function toggleLog() {
  if (!logBox.classList.contains("hidden")) {
    logBox.classList.add("hidden");
    logBtn.textContent = "Show recent problems";
    return;
  }
  try {
    const lines = await invoke("get_log");
    logBox.textContent = lines.length
      ? lines.join("\n")
      : "Nothing went wrong this session.";
    logBox.classList.remove("hidden");
    logBtn.textContent = "Hide";
  } catch (e) {
    toast(formatError(e), "err");
  }
}

function onSettingToggle(e) {
  const input = e.target.closest("[data-setting]");
  if (!input) return;
  settings = { ...settings, [input.dataset.setting]: input.checked };
  persistSettings();
}

el("pasteBtn").addEventListener("click", pasteIntoImport);
doImportBtn.addEventListener("click", importManual);
el("importFileBtn").addEventListener("click", () => runImport(invoke("import_from_file")));
el("importSteamBtn").addEventListener("click", () =>
  runImport(invoke("import_from_steam_cache"))
);
importInput.addEventListener("input", syncImportForm);
el("accountSignIn").addEventListener("click", () => {
  if (openAccountId) signIn(openAccountId);
});
el("accountRemove").addEventListener("click", () => {
  if (openAccountId) askRemove(openAccountId);
});
el("refreshBtn").addEventListener("click", () => refresh().then(() => refreshIntel(true)));
el("searchBtn").addEventListener("click", () => {
  if (currentView !== "picker") showView("picker");
  setFilterOpen(!filterOpen);
});
searchInput.addEventListener("input", onSearchInput);

colorFilterEl.addEventListener("click", (e) => {
  const swatch = e.target.closest(".swatch");
  if (!swatch) return;
  colorFilter = swatch.dataset.color === colorFilter ? "" : swatch.dataset.color;
  renderColorFilter();
  render();
});

colorPick.addEventListener("click", (e) => {
  const swatch = e.target.closest(".swatch");
  if (swatch && openAccountId) applyColor(openAccountId, swatch.dataset.color);
});

colorMenu.addEventListener("click", (e) => {
  if (!colorMenuFor) return;
  const steamid = colorMenuFor;

  const swatch = e.target.closest(".swatch");
  if (swatch) {
    applyColor(steamid, swatch.dataset.color);
    return closeColorMenu();
  }

  const item = e.target.closest("[data-cmd]");
  if (!item) return;
  closeColorMenu();
  const index = accounts.findIndex((a) => a.steamid === steamid);
  const acc = accounts[index];
  switch (item.dataset.cmd) {
    case "copy-code":
      return runCopy(invoke("copy_token", { steamid }));
    case "copy-name":
      if (settings.streamer_mode) return toast("Hidden by streamer mode.", "err");
      return acc && runCopy(invoke("copy_text", { text: acc.account_name }).then(() => "Username copied."));
    case "profile":
      return invoke("open_profile", { steamid }).catch((e) => toast(formatError(e), "err"));
    case "remove":
      return askRemove(steamid);
  }
});

accountFacts.addEventListener("click", (e) => {
  const copyable = e.target.closest("dd[data-copy]");
  if (copyable) return copyFactValue(copyable);
});

cooldownChips.addEventListener("click", (e) => {
  const chip = e.target.closest("[data-cooldown]");
  if (chip && openAccountId) applyCooldown(openAccountId, Number(chip.dataset.cooldown));
});

selColor.addEventListener("click", (e) => {
  const swatch = e.target.closest(".swatch");
  if (swatch) tagSelected(swatch.dataset.color);
});
el("selCopy").addEventListener("click", () =>
  runCopy(invoke("export_tokens", { steamids: [...selected] }))
);
el("selExport").addEventListener("click", () =>
  runCopy(invoke("export_tokens_to_file", { steamids: [...selected] }).catch((e) => {
    if (formatError(e) === "__cancelled__") return "";
    throw e;
  }))
);
el("selRemove").addEventListener("click", askRemoveSelected);
el("selClear").addEventListener("click", clearSelection);

tiles.addEventListener("contextmenu", (e) => {
  const tile = e.target.closest("[data-account]");
  if (!tile) return;
  e.preventDefault();
  openColorMenu(tile.dataset.account, e.clientX, e.clientY);
});
moreBtn.addEventListener("click", () => {
  showAllAccounts = !showAllAccounts;
  render();
});
el("pruneBtn").addEventListener("click", () => {
  openConfirm(
    "Remove expired codes",
    "Remove saved accounts whose login code has expired? Codes that can't be read are kept.",
    "Remove expired",
    async () => {
      try {
        const msg = await invoke("prune_expired");
        await refresh();
        toast(msg, "ok");
      } catch (e) {
        toast(formatError(e), "err");
      }
    }
  );
});
el("dangerBtn").addEventListener("click", askClearSteam);
el("settingsBtn").addEventListener("click", openSettings);
backBtn.addEventListener("click", () => showView("picker"));
settingsView.addEventListener("change", onSettingToggle);
apiKeyInput.addEventListener("input", onApiKeyInput);

cs2LaunchInput.addEventListener("input", () => {
  clearTimeout(cs2LaunchTimer);
  cs2LaunchTimer = setTimeout(() => {
    settings = { ...settings, cs2_launch_options: cs2LaunchInput.value };
    persistSettings();
  }, 500);
});

const logBtn = el("logBtn");
updateBtn.addEventListener("click", checkForUpdate);
logBtn.addEventListener("click", toggleLog);

cs2SourceSelect.addEventListener("change", () => {
  settings = { ...settings, cs2_config_source: cs2SourceSelect.value };
  persistSettings();
});

el("confirmYes").addEventListener("click", () => {
  const needsValue = !confirmInput.classList.contains("hidden");
  const value = confirmInput.value.trim();
  if (needsValue && !value) return confirmInput.focus();
  const fn = confirmHandler;
  closeConfirm();
  if (fn) fn(value);
});

confirmInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    e.preventDefault();
    el("confirmYes").click();
  }
});

confirmModal.addEventListener("click", (e) => {
  if (e.target === confirmModal) closeConfirm();
});

const tauriWin = window.__TAURI__ && window.__TAURI__.window;
const appWindow = tauriWin
  ? tauriWin.getCurrentWindow
    ? tauriWin.getCurrentWindow()
    : tauriWin.getCurrent && tauriWin.getCurrent()
  : null;
if (appWindow) {
  el("winMin").addEventListener("click", () => appWindow.minimize());
  el("winMax").addEventListener("click", () => appWindow.toggleMaximize());
  el("winClose").addEventListener("click", () => appWindow.close());
}

document.addEventListener("click", (e) => {
  if (colorMenuFor && !e.target.closest("#colorMenu")) closeColorMenu();

  const closer = e.target.closest('[data-action="close-confirm"]');
  if (closer) return closeConfirm();

  if (e.target.closest("#addTile")) return openAdd();
  const tile = e.target.closest("[data-account]");
  if (tile) {
    if (e.ctrlKey || e.metaKey) return toggleSelected(tile.dataset.account);
    clearSelection();
    return openAccount(tile.dataset.account);
  }
});

const isTypingTarget = (node) =>
  node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement;

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    if (!confirmModal.classList.contains("hidden")) return closeConfirm();
    if (colorMenuFor) return closeColorMenu();
    if (currentView === "picker" && clearSelection()) return;
    if (currentView === "picker" && filterOpen) return setFilterOpen(false);
    if (currentView !== "picker" && currentView !== "signing") showView("picker");
    return;
  }

  if (
    currentView === "picker" &&
    !filterOpen &&
    !isTypingTarget(e.target) &&
    e.key.length === 1 &&
    !e.ctrlKey &&
    !e.altKey &&
    !e.metaKey &&
    e.key !== " "
  ) {
    setFilterOpen(true);
    searchInput.value = e.key;
    onSearchInput();
    e.preventDefault();
    return;
  }
  if (e.key === "Enter" || e.key === " ") {
    const tile = e.target.closest(".user[role='button']");
    if (tile && e.target === tile) {
      e.preventDefault();
      if (tile.id === "addTile") return openAdd();
      if (!tile.dataset.account) return;
      if (e.ctrlKey || e.metaKey) return toggleSelected(tile.dataset.account);
      clearSelection();
      return openAccount(tile.dataset.account);
    }
  }
  if (e.key === "Enter" && e.target === searchInput) {
    const first = tiles.querySelector("[data-account]");
    if (first) {
      e.preventDefault();
      openAccount(first.dataset.account);
    }
    return;
  }
  if (e.key === "Enter" && currentView === "add") {
    if (e.ctrlKey || e.target === importInput) {
      e.preventDefault();
      importManual();
    }
  }
});

listen("accounts-changed", () => refresh());
listen("settings-changed", () => loadSettings());
listen("metadata-changed", () => loadMeta().then(render));
listen("steam-starting", async (e) => {
  await refresh();
  const index = accounts.findIndex((a) => a.steamid === e.payload);
  if (index < 0) return;
  // Not "Signed in": the files are written and Steam is starting, but Steam has not
  // said yet whether it accepts the code. A watcher reports that separately.
  toast(`Starting Steam as ${displayAccount(accounts[index], index).display_name}`, "ok");
});
listen("status", (e) => {
  refresh().then(() => toast(e.payload, "ok"));
});
listen("status-error", (e) => toast(e.payload, "err"));


/* ---------- Password lock ---------- */
const lockInput = el("lockInput");
const lockError = el("lockError");
const lockTitle = el("lockTitle");
let usingRecovery = false;
let vault = { enabled: false, unlocked: false };

function showLock() {
  usingRecovery = false;
  lockTitle.textContent = "Locked";
  lockInput.type = "password";
  lockInput.placeholder = "Password";
  lockInput.value = "";
  lockError.textContent = "";
  el("lockRecovery").classList.remove("hidden");
  showView("lock");
  setTimeout(() => lockInput.focus(), 60);
}

async function attemptUnlock() {
  const secret = lockInput.value.trim();
  if (!secret) return lockInput.focus();
  el("lockUnlock").disabled = true;
  lockError.textContent = "";
  try {
    await invoke("vault_unlock", { secret, isRecovery: usingRecovery });
    lockInput.value = "";
    await startApp();
  } catch (e) {
    lockError.textContent = formatError(e);
    lockInput.select();
  } finally {
    el("lockUnlock").disabled = false;
  }
}

function syncVaultUi() {
  el("vaultState").textContent = vault.enabled ? "On" : "Off";
  el("vaultToggleBtn").textContent = vault.enabled ? "Remove password" : "Set a password";
  el("vaultChangeBtn").classList.toggle("hidden", !vault.enabled);
  el("lockBtn").classList.toggle("hidden", !vault.enabled);
}

function showRecoveryCode(code) {
  openConfirm(
    "Save your recovery code",
    `${code}

This is the only way back in if you forget the password. It is shown once ` +
      `and cannot be retrieved later. Write it down before closing this.`,
    "I have saved it",
    () => invoke("copy_text", { text: code }).then(() => toast("Recovery code copied.", "ok")),
    "primary"
  );
}

async function refreshVault() {
  try {
    vault = await invoke("vault_status");
  } catch {
    vault = { enabled: false, unlocked: false };
  }
  syncVaultUi();
}

el("lockUnlock").addEventListener("click", attemptUnlock);
lockInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") attemptUnlock();
});

el("lockRecovery").addEventListener("click", () => {
  usingRecovery = true;
  lockTitle.textContent = "Recovery code";
  lockInput.type = "text";
  lockInput.placeholder = "XXXXX-XXXXX-XXXXX";
  lockInput.value = "";
  lockError.textContent = "";
  el("lockRecovery").classList.add("hidden");
  lockInput.focus();
});

el("lockBtn").addEventListener("click", () => {
  invoke("vault_lock").then(() => {
    accounts = [];
    render();
    showLock();
  });
});

el("vaultToggleBtn").addEventListener("click", () => {
  if (vault.enabled) {
    openPrompt(
      "Remove password",
      "Enter the current password. Your login codes go back to being protected by Windows alone.",
      "Remove",
      "Password",
      async (password) => {
        try {
          await invoke("vault_disable", { password });
          await refreshVault();
          toast("Password removed.", "ok");
        } catch (e) {
          toast(formatError(e), "err");
        }
      },
      "password"
    );
    return;
  }
  openPrompt(
    "Set a password",
    "Asked for on every start, and used to encrypt your saved login codes. There is no " +
      "way to reset it, so keep the recovery code you get next.",
    "Set password",
    "New password",
    async (password) => {
      try {
        const code = await invoke("vault_enable", { password });
        await refreshVault();
        showRecoveryCode(code);
      } catch (e) {
        toast(formatError(e), "err");
      }
    },
    "password"
  );
});

el("vaultChangeBtn").addEventListener("click", () => {
  openPrompt("Change password", "Enter the current password.", "Continue", "Current password",
    (oldPassword) => {
      openPrompt("Change password", "Now the new one.", "Change", "New password",
        async (newPassword) => {
          try {
            const code = await invoke("vault_change", { old: oldPassword, new: newPassword });
            await refreshVault();
            showRecoveryCode(code);
          } catch (e) {
            toast(formatError(e), "err");
          }
        }, "password");
    }, "password");
});

async function startApp() {
  await refreshVault();
  await loadSettings();
  await refresh();
  showView("picker");
  // After the picker is up, never before: an update prompt is not what you want to
  // meet on a cold start, and the check must not delay the accounts appearing.
  checkForUpdateQuietly();
}

async function boot() {
  paintBackdrop();
  await refreshVault();
  if (vault.enabled && !vault.unlocked) {
    showLock();
    return;
  }
  await startApp();
}

boot();

invoke("app_version")
  .then((v) => {
    versionText.textContent = `v${v}`;
  })
  .catch(() => {
    versionText.textContent = "unknown";
  });
