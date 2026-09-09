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
/// steamid -> public account data from Steam's Web API (level, bans, status).
let intel = {};
/// steamid -> { color, cooldown_until, last_used } — our own, user-assigned.
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
};

function toast(message, kind = "ok") {
  // The window is short; a stack would climb over the account row, so the
  // newest message replaces the previous one.
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

/* ---------- Backdrop: a tilted, blurred grid of capsules, drawn in CSS ---------- */
function paintBackdrop() {
  const host = el("capsules");
  if (!host) return;
  // Deterministic so the backdrop never reshuffles between renders.
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

/* ---------- View routing ---------- */
function showView(name) {
  currentView = name;
  for (const [key, node] of Object.entries(VIEWS)) {
    node.classList.toggle("hidden", key !== name);
  }
  backBtn.classList.toggle("hidden", name === "picker" || name === "signing");
  footlinks.classList.toggle("hidden", name === "signing");
}

/* ---------- Account picker ---------- */
/// An account imported from a bare "steamid||token" code has no username, so its
/// name ends up being the SteamID — which then renders as the ID twice, once as
/// the name and once as the login. Steam knows the real persona name; use it.
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
  // Steam knows this account's avatar but hasn't cached the image locally, so
  // let the webview pull it from Steam's CDN. Falls back to the silhouette if
  // the request fails (offline, deleted avatar, blocked network).
  if (view.avatar_url) {
    return `<img class="remote-avatar" src="${escapeAttr(view.avatar_url)}" alt="" />`;
  }
  return DEFAULT_AVATAR_SVG;
}

// `error` does not bubble, so listen in the capture phase.
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
// Steam's own name tints: online #6dcff6, in-game #8cd61d. Applied to the persona
// name that already fades in on hover, which is exactly where Steam puts it.
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

/* ---------- Metadata: tags, cooldowns, last-used ---------- */
const COLORS = ["red", "amber", "green", "blue", "purple", "gray"];
// The CS2 competitive ladder, which is what a cooldown almost always is.
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

/// "6h 12m", "2d 4h" — the panel has room for two units, unlike the tray.
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

  // Most recently used first, the way Steam surfaces the current user first.
  const ordered = accounts
    .map((acc, index) => ({ acc, index }))
    .sort((a, b) => Number(b.acc.most_recent) - Number(a.acc.most_recent));

  // Search matches what is on screen, not the underlying record — otherwise a
  // query would confirm a real name that streamer mode is hiding.
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
  // Steam caps the row at 8 (GetLoginUsers().slice(0, 8)); we keep the rest
  // reachable behind a toggle rather than dropping them. A search is already a
  // deliberate narrowing, so it shows everything it found.
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
    // The count only survives as the tooltip now, so it has to carry the label.
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
      // Greying a banned account out reads as "unavailable" on its own, before
      // anyone reads the ribbon.
      const state = [
        info?.banned ? "is-banned" : "",
        onCooldown(m) ? "is-cooldown" : "",
        statusClass(info),
      ]
        .filter(Boolean)
        .join(" ");
      const color = m.color ? ` data-color="${escapeAttr(m.color)}"` : "";
      return `
        <div class="user existing ${state}"${color} role="button" tabindex="0" data-account="${escapeAttr(acc.steamid)}" aria-label="${escapeAttr(view.display_name)} — open account">
          <div class="icon">${iconInner(view)}</div>
          <div class="name persona">${escapeHtml(view.display_name)}</div>
          <div class="name account">${escapeHtml(view.account_name)}</div>
        </div>`;
    })
    .join("");

  // While filtering, "Add Account" is not one of the things you searched for.
  const addTile = filtering
    ? ""
    : `<div class="user new" role="button" tabindex="0" id="addTile" aria-label="Add Account">
       <div class="icon">${ADD_SVG}</div>
       <div class="name persona">Add Account</div>
       <div class="name account"></div>
     </div>`;
  tiles.innerHTML = accountTiles + addTile;
  renderSelection();
  // The CS2 source list is a list of accounts, so it goes stale with this one.
  syncCs2Source();
}

/// Public read-only lookups (level / bans / online status). Never fatal: the
/// picker works fine without them, so a failure only logs.
async function refreshIntel(force = false) {
  const steamids = accounts.map((a) => a.steamid);
  if (!steamids.length) {
    intel = {};
    lastIntelIds = "";
    return;
  }
  // Steam rate-limits, so don't re-ask on every internal refresh — but a changed
  // roster must always refetch, or new accounts sit there with no badges while
  // removed ones keep theirs.
  const roster = steamids.join(",");
  const rosterChanged = roster !== lastIntelIds;
  if (!force && !rosterChanged && Date.now() - lastIntelAt < 30000) return;
  lastIntelAt = Date.now();
  lastIntelIds = roster;
  try {
    const result = await invoke("fetch_account_intel", { steamids });
    // A roster change while this was in flight means the reply is stale —
    // applying it would wipe the badges for the accounts now on screen.
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

/* ---------- Account detail ----------
   Everything the tiles used to hint at with badges lives here instead, spelled
   out, one click away — along with the two things you can actually do. */
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
  if (info.game) return `In game — ${info.game}`;
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

/// `copy` is what lands on the clipboard when the value is clicked — pass the
/// literal text, or "token:<steamid>" to have the backend hand over the saved code
/// (which the frontend never holds).
function factRow(label, value, kind, copy, hint) {
  const attr = copy ? ` data-copy="${escapeAttr(copy)}"` : "";
  const tip = hint || (copy ? "Click to copy" : "");
  const title = tip ? ` title="${escapeAttr(tip)}"` : "";
  return `<div class="fact${kind ? ` ${kind}` : ""}"><dt>${escapeHtml(label)}</dt><dd${attr}${title}>${escapeHtml(value)}</dd></div>`;
}

/// A fact whose value is a button in disguise. Same affordance as a copyable
/// value — no extra row, which the panel has no height for.
function actionFact(label, value, action, kind, hint) {
  return `<div class="fact${kind ? ` ${kind}` : ""}"><dt>${escapeHtml(label)}</dt><dd data-act="${escapeAttr(action)}"${hint ? ` title="${escapeAttr(hint)}"` : ""}>${escapeHtml(value)}</dd></div>`;
}

/// How the saved login code is doing: missing, expired, or good for a while.
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
  // Without a username the login line repeats the name verbatim, which is just
  // the same string printed twice.
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
  // The fade only makes sense when there is genuinely something below the fold.
  accountFacts.classList.toggle(
    "is-scrollable",
    accountFacts.scrollHeight > accountFacts.clientHeight
  );

  // When a cooldown is running, the chip that clears it also *is* the readout —
  // a separate "Cooldown: 2d 23h left" fact row said the same thing twice and
  // cost the panel a line it does not have.
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

  // Signing in during a cooldown is legitimate (the cooldown is on matchmaking,
  // not the account) — but it should be a deliberate press, not a reflex.
  accountSignIn.textContent = cooling ? "Sign in anyway" : "Sign in";

  showView("account");
}

/* ---------- Search + filter ---------- */
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
    // Leaving the bar open with a live query but no way to see it would silently
    // hide accounts, so closing always clears.
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

/* ---------- Tags ---------- */
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
  // Park it at the origin before measuring: left over from a previous open, the
  // menu can sit against the right edge, where a fixed element shrink-wraps to
  // the space left and reports a width narrower than it will actually render.
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

/* ---------- Copying ----------
   A copy that silently fails is worse than useless — you paste the last thing you
   copied and never notice. So failures always toast; successes only do when there
   is no inline confirmation to show instead. */
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

/* ---------- Cooldowns ---------- */
async function applyCooldown(steamid, seconds) {
  const until = seconds > 0 ? nowUnix() + seconds : null;
  try {
    await invoke("set_account_cooldown", { steamid, until });
    meta = { ...meta, [steamid]: { ...metaFor(steamid), cooldown_until: until } };
    render();
    // Re-open so the facts, the chips and the CTA all reflect the new state.
    if (currentView === "account" && openAccountId === steamid) openAccount(steamid);
  } catch (e) {
    toast(formatError(e), "err");
  }
}

/* ---------- Multi-select ----------
   Ctrl+click gathers accounts so a tag or a purge can be done once instead of
   N times. Signing in is deliberately absent: you can only be one account. */
const selBar = el("selBar");
const selCount = el("selCount");
const selColor = el("selColor");
const selected = new Set();

function renderSelection() {
  // Accounts can disappear (removal, filtering) while selected; drop those.
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
      // Silent on success — the tiles vanishing is the confirmation.
      if (failed.length) toast(`${failed.length} could not be removed.`, "err");
    }
  );
}

/* ---------- Signing in ---------- */
const signingAvatar = el("signingAvatar");
const signingName = el("signingName");

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
  showView("signing");

  const settled = invoke("sign_in", { steamid });
  const minimumDwell = new Promise((r) => setTimeout(r, 700));

  try {
    await Promise.all([settled, minimumDwell]);
    await refresh();
    showView("picker");
    // No toast: the signing screen just showed who, and the tile moves to the
    // front. Announcing it again only restates what you already watched happen.
  } catch (e) {
    await minimumDwell;
    await refresh();
    showView("picker");
    toast(formatError(e), "err");
  }
}

/* ---------- Add account ---------- */
const importInput = el("importInput");
const importStatus = el("importStatus");
const doImportBtn = el("doImportBtn");
const addForm = document.querySelector(".add-form");
const stageEl = document.querySelector(".stage");

function codeCount() {
  return importInput.value.split("\n").filter((line) => line.trim()).length;
}

// The box starts one line tall and grows with what you paste, rather than
// reserving an empty void.
function syncImportForm() {
  importInput.style.height = "auto";
  const wanted = Math.max(40, importInput.scrollHeight);
  importInput.style.height = wanted + "px";

  // A single pasted code wraps over many lines; without this the box grows
  // until it pushes the Import button out of the stage entirely.
  const room =
    stageEl.getBoundingClientRect().bottom - addForm.getBoundingClientRect().bottom - 8;
  if (room < 0) {
    // Measure what the box is ACTUALLY rendering at — `wanted` may already have
    // been capped by max-height, in which case subtracting from it does nothing.
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
  // The message changes the form's height, so the input has to give room back.
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

/// Shared tail for the alternative import routes. `CANCELLED` comes back when the
/// user closed the file picker, which is not an error worth shouting about.
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
    // The backend names the account it imported, which streamer mode is meant to
    // keep off screen — so fall back to a count it can't leak anything through.
    toast(settings.streamer_mode ? `Imported ${n} account${n > 1 ? "s" : ""}.` : msg, "ok");
  } catch (e) {
    setImportStatus(formatError(e), "err");
  }
}

/* ---------- Destructive actions ---------- */
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
      // The tile disappearing is the confirmation.
      await refresh();
      // The account we were looking at is gone, so don't sit on its panel.
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

/* ---------- Confirm ---------- */
const confirmModal = el("confirmModal");
/// `tone` is "danger" (the default — this dialog exists mostly to guard deletes)
/// or "primary" for a confirmation that isn't destructive. A red button on
/// "Open download page" tells the user to be careful about nothing.
function openConfirm(title, text, label, handler, tone = "danger") {
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

/* ---------- Settings ---------- */
const settingsView = VIEWS.settings;

/// The CS2 config source is picked by account, so the list has to be rebuilt
/// whenever the roster changes — and it must survive an account being removed,
/// which is what the "no longer saved" entry is for.
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
  // Same rule as the API key field: don't clobber what the user is typing when a
  // save round-trips back through settings-changed.
  const launch = settings.cs2_launch_options || "";
  if (cs2LaunchInput.value !== launch && document.activeElement !== cs2LaunchInput) {
    cs2LaunchInput.value = launch;
  }
  // Saving emits settings-changed, which lands back here. Only touch the key
  // field when the stored value actually differs from what is on screen —
  // otherwise a save would wipe the "Key accepted" verdict the user just earned,
  // or clobber what they are still typing.
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

/* ---------- Steam Web API key ---------- */
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
      // The user kept typing; this verdict is about a key they no longer have.
      if (apiKeyInput.value.trim() !== key) return;
      setKeyState(ok ? "Key accepted" : "Steam rejected this key", ok ? "ok" : "err");
      if (ok) refreshIntel(true);
    } catch (e) {
      if (apiKeyInput.value.trim() !== key) return;
      // Could not reach Steam — say so rather than blaming the key.
      setKeyState(formatError(e), "err");
    }
  }, 600);
}

/* ---------- About: version, updates, diagnostics ---------- */
const versionText = el("versionText");
const updateBtn = el("updateBtn");
const logBox = el("logBox");

async function checkForUpdate() {
  updateBtn.disabled = true;
  updateBtn.textContent = "Checking…";
  try {
    const info = await invoke("check_for_update");
    if (!info.available) {
      toast(`You're on the latest version (${info.current}).`, "ok");
      return;
    }
    openConfirm(
      `Version ${info.latest} is available`,
      info.notes || "No release notes.",
      "Open download page",
      () => invoke("open_url", { url: info.url }).catch((e) => toast(formatError(e), "err")),
      "primary"
    );
  } catch (e) {
    toast(formatError(e), "err");
  } finally {
    updateBtn.disabled = false;
    updateBtn.textContent = "Check for updates";
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
      : "Nothing to report — no quiet failures this session.";
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

/* ---------- Wiring ---------- */
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
  // Clicking the active colour clears the filter, so one control does both.
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
      // Streamer mode hides these names on screen; copying one would put it
      // straight back on the clipboard.
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
    // Closing the save dialog is not a failure.
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
  // Deleting saved accounts is not something to do quietly on a refresh, however
  // dead the token is — the record is the only copy the user has.
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

// Debounced: saving on every keystroke would round-trip a settings-changed event
// back into the field the user is still typing in.
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
  const fn = confirmHandler;
  closeConfirm();
  if (fn) fn();
});

confirmModal.addEventListener("click", (e) => {
  if (e.target === confirmModal) closeConfirm();
});

// Custom titlebar window controls (frameless window).
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
  // Any click outside the tag menu dismisses it, including the one that opens a
  // tile — so check this before anything else acts on the click.
  if (colorMenuFor && !e.target.closest("#colorMenu")) closeColorMenu();

  const closer = e.target.closest('[data-action="close-confirm"]');
  if (closer) return closeConfirm();

  if (e.target.closest("#addTile")) return openAdd();
  const tile = e.target.closest("[data-account]");
  if (tile) {
    if (e.ctrlKey || e.metaKey) return toggleSelected(tile.dataset.account);
    // A plain click leaves the picker, so a selection left behind would be
    // invisible state waiting to surprise someone.
    clearSelection();
    return openAccount(tile.dataset.account);
  }
});

const isTypingTarget = (node) =>
  node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement;

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    // Unwind one layer at a time, innermost first.
    if (!confirmModal.classList.contains("hidden")) return closeConfirm();
    if (colorMenuFor) return closeColorMenu();
    if (currentView === "picker" && clearSelection()) return;
    if (currentView === "picker" && filterOpen) return setFilterOpen(false);
    // Anything that shows the back chevron is escapable, so a new view added
    // later doesn't silently miss out.
    if (currentView !== "picker" && currentView !== "signing") showView("picker");
    return;
  }

  // Type-to-search: on the picker, a bare letter opens the filter bar and lands
  // in it, so finding one of 40 accounts doesn't start with hunting for a button.
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
  // Enter from the search box takes the top hit — the whole point of typing.
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
// Tray sign-in has no signing screen to watch, so it is the one success worth
// announcing. The backend sends only the steamid; the name is resolved here so
// it goes through the same masking the tiles use.
listen("signed-in", async (e) => {
  await refresh();
  const index = accounts.findIndex((a) => a.steamid === e.payload);
  if (index < 0) return;
  toast(`Signed in as ${displayAccount(accounts[index], index).display_name}`, "ok");
});
listen("status", (e) => {
  refresh().then(() => toast(e.payload, "ok"));
});
listen("status-error", (e) => toast(e.payload, "err"));

paintBackdrop();
loadSettings().then(() => refresh());
invoke("app_version")
  .then((v) => {
    versionText.textContent = `v${v}`;
  })
  .catch(() => {
    versionText.textContent = "unknown";
  });
