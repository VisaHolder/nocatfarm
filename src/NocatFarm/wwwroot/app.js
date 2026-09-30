'use strict';

// nocat.farm dashboard. Vanilla JS on purpose: no build step, no framework, no CDN. The whole UI is three files
// served off the exe, so the dashboard can never be the reason the tool won't start.
//
// Every label, tooltip, range and default comes from /api/settings/schema, which is generated from the same
// C# registry the console reads. Nothing about a setting is written twice.

const $ = (id) => document.getElementById(id);
// Escapes the apostrophe too. Account names, game names, notes and rep4rep comment text all end up inside
// inline onclick="..." attributes; a name like  o'brien  would otherwise break the handler outright, and
// everything here is text that came from outside this program.
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let token = localStorage.getItem('nocatfarm-token') || '';
let refreshSeconds = 3;
let state = null;          // /api/status
let schema = null;         // /api/settings/schema
let config = null;         // /api/config
let configLoaded = null;   // /api/config exactly as it came - a save sends its part back as the Base
let commands = [];
let logLines = [];
// 'clear' in this dashboard hides everything up to here - in this browser only. The window and the file keep theirs.
let logClearedAt = 0;
let localLines = [];
let history = JSON.parse(localStorage.getItem('nocatfarm-history') || '[]');
let historyAt = -1;
let view = location.hash.replace('#', '') || 'overview';
let acctFilter = '';

// A symbol, not the string "global" - an account legitimately called "global" would otherwise hijack the pane
// and post its edits into the global config file.
const GLOBAL = Symbol('global');
// Matches WebHost.ClearSecret exactly. It was ' clear' with a space, which the server didn't recognise - so
// clearing the dashboard password SET it to the word " clear" instead and locked people out.
const CLEAR_SECRET = '\u0000clear';
let settingsTarget = GLOBAL;
let pollTimer = null;
let pollSeconds = 0;
let bootId = null;
let pending = {};                // settings edited but not saved
let logLevels = { INFO: true, GOOD: true, WARN: true, ERROR: true, DEBUG: false };
let r4r = null;
let r4rProfiles = [];
let r4rTasks = [];

const STATUS_META = {
  farming:    { label: 'Farming',   tip: 'Playing a game that still has trading cards to drop.' },
  idling:     { label: 'Idling',    tip: "Nothing left to farm, so it's building playtime on the games you chose." },
  online:     { label: 'Online',    tip: 'Logged in and doing nothing. Give it games to idle or cards to farm.' },
  connecting: { label: 'Connecting', tip: 'On its way in.' },
  needsyou:   { label: 'Needs you', tip: 'Waiting on you: a Steam Guard code, a password, or a decision.' },
  problem:    { label: 'Problem',   tip: 'Something is wrong - the login failed, or Steam is refusing this account.' },
  playing:    { label: 'Playing',   tip: 'Human mode: in a game right now, one at a time, like a person.' },
  break:      { label: 'On a break', tip: 'Human mode: stepped away for a few minutes.' },
  done:       { label: 'Done for today', tip: "Human mode: today's hours are played. Still signed in and showing online, just not in a game - it plays again tomorrow." },
  dayoff:     { label: 'Day off',    tip: 'Human mode: not playing today, the way people skip a day now and then. Still signed in and showing online.' },
  nightidle:  { label: 'Night idle', tip: 'Offline for the night but quietly banking hours - nobody can see it.' },
  asleep:     { label: 'Asleep',     tip: 'Done for the night. It comes back on its own in the morning.' },
  off:        { label: 'Off',       tip: "Disabled or stopped. It won't log in until you start it." }
};

// ── plumbing ─────────────────────────────────────────────────────────
async function api(path, options = {}) {
  const opts = { ...options, headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } };
  if (token) opts.headers['Authorization'] = 'Bearer ' + token;
  const res = await fetch(path, opts);
  if (res.status === 401) { showLogin(); throw new Error('unauthorised'); }
  return res.json();
}

const post = (p, b) => api(p, { method: 'POST', body: JSON.stringify(b || {}) });
const del = (p) => api(p, { method: 'DELETE' });

function toast(message, bad) {
  const el = document.createElement('div');
  el.className = 'toast' + (bad ? ' bad' : '');
  el.textContent = message;
  document.body.appendChild(el);
  setTimeout(() => el.remove(), 4500);
}

function showLogin(message) {
  $('login').classList.remove('hidden');
  $('app').classList.add('hidden');
  $('welcome').classList.add('hidden');
  if (message) $('loginError').textContent = message;
}

// Too many wrong passwords: say that, count down, and say how to skip the wait - not "Wrong password" for the right one.
let loginLockTimer = null;
function loginLocked(seconds, thisPc) {
  clearInterval(loginLockTimer);
  let left = Math.max(1, seconds | 0);
  const draw = () => {
    const wait = left >= 60 ? `${Math.ceil(left / 60)}m` : `${left}s`;
    $('loginError').textContent = tf('Too many wrong passwords - try again in {0}.', wait)
      + (thisPc ? ' ' + t('Or type unlock in the nocat.farm window.') : '');
  };
  draw();
  loginLockTimer = setInterval(() => {
    left--;
    if (left <= 0) { clearInterval(loginLockTimer); $('loginError').textContent = t('You can try again now.'); return; }
    draw();
  }, 1000);
}

async function doLogin(e) {
  e.preventDefault();
  const res = await fetch('/api/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ Password: $('pw').value })
  }).then((r) => r.json()).catch(() => ({ ok: false }));

  if (!res.ok && res.error === 'locked') { loginLocked(res.seconds, res.thisPc); return false; }
  if (!res.ok) { $('loginError').textContent = t('Wrong password.'); return false; }
  token = res.token;
  localStorage.setItem('nocatfarm-token', token);
  $('loginError').textContent = '';
  boot();
  return false;
}

// ── language ─────────────────────────────────────────────────────────
// One JSON file per language under /lang, and English is not one of them: English lives in the markup and in
// Settings.cs, and every lookup falls back to it. That is what makes a half-finished translation safe to ship -
// an untranslated string shows the English rather than a key or a blank, so a language can be filled in over
// time without anything ever looking broken.
let lang = { ui: {}, settings: {} };

async function loadLanguage(code) {
  if (!code || code === 'en') { lang = { ui: {}, settings: {} }; return; }
  try {
    const res = await fetch(`lang/${encodeURIComponent(code)}.json`, { cache: 'no-cache' });
    lang = res.ok ? await res.json() : { ui: {}, settings: {} };
    lang.ui = lang.ui || {};
    lang.settings = lang.settings || {};
  } catch { lang = { ui: {}, settings: {} }; }
}

/// Write markup into an element only when it has actually changed.
///
/// Every one of these panels was rebuilt from scratch on each poll - three seconds - which destroyed and
/// recreated every button, link and row inside it. A click landing during a rebuild went nowhere, hover
/// tooltips vanished mid-read, and any text you were selecting was dropped. Comparing first costs a string
/// compare and keeps the DOM still whenever nothing has moved.
///
/// Compared with the markup it was last given, not with innerHTML: the browser never hands back what it was given -
/// it adds a <tbody> to a table and turns &#39; back into ' - so the two never matched, and nearly every panel was
/// rebuilt on every poll anyway. Whatever the page looked like after that write is kept too, so a panel something
/// else has since written into is still repainted.
function paint(id, html) {
  const el = $(id);

  if (!el || ((el._paintedFrom === html) && (el._paintedAs === el.innerHTML) && (html !== ''))) {
    return;
  }

  el.innerHTML = html;
  el._paintedFrom = html;
  el._paintedAs = el.innerHTML;
}

/// Translate a chrome string. The English text IS the key, so nothing has to be kept in sync by hand.
const t = (english) => (lang.ui && lang.ui[english]) || english;

/// Translate, then fill in the blanks. Placeholders are {0}, {1}... rather than the sentence being glued
/// together from fragments, because word order differs between languages and a translator has to be able to
/// move the value to wherever it belongs - which is impossible once the English order is baked into the code.
const tf = (english, ...args) => t(english).replace(/\{(\d+)\}/g, (whole, i) => (args[i] === undefined ? whole : args[i]));

/// A setting's translated label / explanation / choice labels, falling back to whatever the schema carries.
function tSetting(def, field) {
  const entry = lang.settings && lang.settings[def.Name];
  const value = entry && entry[field];
  return value || def[field === 'label' ? 'Label' : field === 'tip' ? 'Tooltip' : field === 'placeholder' ? 'Placeholder' : 'Choices'];
}

/// Walk the static markup once and translate anything tagged. data-t on an element translates its text;
/// data-t-ph translates a placeholder; data-t-tip translates a tooltip.
function translateChrome(root) {
  (root || document).querySelectorAll('[data-t]').forEach((el) => { el.textContent = t(el.dataset.t); });
  (root || document).querySelectorAll('[data-t-ph]').forEach((el) => { el.placeholder = t(el.dataset.tPh); });
  (root || document).querySelectorAll('[data-t-tip]').forEach((el) => { el.dataset.tip = t(el.dataset.tTip); });
  // Its text is set from code, not tagged, because it depends on which theme is showing.
  const theme = $('themeToggle');
  if (theme && !root) theme.textContent = themeLabel();
}

// ── tooltips ─────────────────────────────────────────────────────────
// One handler for the whole document, so anything with data-tip gets a tooltip without registering anything.
document.addEventListener('mouseover', (e) => {
  const el = e.target.closest('[data-tip]');
  if (!el) return;
  const tip = $('tip');
  tip.innerHTML = tipHtml(el.dataset.tip);
  tip.classList.remove('hidden');
  const r = el.getBoundingClientRect();
  const w = tip.offsetWidth;
  tip.style.left = Math.max(8, Math.min(window.innerWidth - w - 12, r.left)) + 'px';
  tip.style.top = (r.bottom + 8 + tip.offsetHeight > window.innerHeight ? r.top - tip.offsetHeight - 8 : r.bottom + 8) + 'px';
});
document.addEventListener('mouseout', (e) => {
  if (e.target.closest('[data-tip]')) $('tip').classList.add('hidden');
});

/// A long explanation read as one block is a wall. Its first sentence becomes a bold headline - usually the
/// whole answer ("Human mode only.", "Leave a newly bought game alone...") - and the rest is cut into short
/// paragraphs at sentence ends. Short tips and ones with their own line breaks are shown exactly as written.
///
/// Built from a string, not written as a /.../ literal: the look-behind in it is a syntax error to Safari before iOS
/// 16.4, and as a literal that stopped the whole of app.js loading - an older iPhone got no dashboard at all. There,
/// a long tip is simply shown as one block.
let sentenceEnd = null;
try {
  sentenceEnd = new RegExp('(?<=[.!?](?:["\'»”)])?)\\s+(?=[\\p{Lu}\\d"\'«“(*])|(?<=[。！？])', 'u');
} catch { /* no look-behind in this browser */ }

function tipHtml(text) {
  if (!text || text.length < 140 || text.includes(String.fromCharCode(10))) return esc(text || '');

  // A sentence ends at . ! ? followed by a space and something that starts a sentence (so "2.5 GB", "e.g. this"
  // and "730:70, 440:20" don't split), or right after a CJK full stop, which has no space after it.
  if (!sentenceEnd) return esc(text);
  const parts = text.split(sentenceEnd).map((s) => s.trim()).filter(Boolean);
  if (parts.length < 2) return esc(text);

  const paras = [];
  let cur = '';
  for (const s of parts.slice(1)) {
    if (cur && (cur.length + s.length > 230)) { paras.push(cur); cur = s; } else cur = cur ? cur + ' ' + s : s;
  }
  if (cur) paras.push(cur);

  return `<span class="lead">${esc(parts[0])}</span>${paras.map((p) => `<p>${esc(p)}</p>`).join('')}`;
}
document.addEventListener('focusin', (e) => {
  const el = e.target.closest('[data-tip]');
  if (el) el.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
});

const tipIcon = (text) => text ? `<i class="info" data-tip="${esc(text)}"></i>` : '';

// Inventory money. Whole dollars in the table - cents on a four-figure inventory are noise - and the full
// figure in the tooltip, where the breakdown lives.
const cur = () => (state && state.Currency) || '$';
const usd = (n) => cur() + (Number(n) || 0).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
const usdExact = (n) => cur() + (Number(n) || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

// Up or down over the last day. Nothing at all until there is a reading old enough to compare against - a
// percentage worked out from twenty minutes of history is noise dressed up as information.
function valueDelta(b) {
  if (b.InventoryChangePct === null || b.InventoryChangePct === undefined) return '';
  const up = b.InventoryChangePct >= 0;
  const tip = up
    ? tf('Up {0} over the last 24 hours, at the market median.', usdExact(Math.abs(b.InventoryChange)))
    : tf('Down {0} over the last 24 hours, at the market median.', usdExact(Math.abs(b.InventoryChange)));
  return `<span class="delta ${up ? 'up' : 'down'}" data-tip="${esc(tip)}">${up ? '+' : '-'}${Math.abs(b.InventoryChangePct).toFixed(1)}%</span>`;
}

// Copy text, over plain http too. navigator.clipboard only exists in a secure context - over http://<lan-ip>
// (which this app supports) it's undefined, so fall back to a hidden textarea instead of throwing and copying
// nothing.
function copyText(text, done) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(() => toast(done)).catch(() => toast(t('Copy failed - select it manually'), true));
      return;
    }
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    toast(ok ? done : t('Copy failed - select it manually'), !ok);
  } catch {
    toast(t('Copy failed - select it manually'), true);
  }
}

async function refreshInventory(name) {
  await post(`/api/bots/${encodeURIComponent(name)}/inventory/refresh`, {});
  toast(tf('Reading {0}’s inventory again…', name));
}

// The hover breakdown: which games hold the value, biggest first.
const nlChar = String.fromCharCode(10);
function valueTip(b) {
  // Where the switch really is: under Inventory & bans, and only with Show advanced ticked - it said Trades.
  if (b.InventoryOn === false) return tf('Not being valued. To switch it back on, go to Settings, pick {0}, tick Show advanced, open Inventory & bans and tick "Work out what its inventory is worth".', b.Name);
  if (!b.InventoryReady) return t('Reading this inventory...');
  const rows = (b.InventoryByGame || []).filter((g) => g.Value > 0 || g.Blocked);
  if (!rows.length) return t('Nothing with a market price in this inventory.');
  const items = (n) => (n === 1 ? tf('{0} item', n) : tf('{0} items', n));
  const lines = rows.map((g) => g.Blocked
    ? tf('{0} - skipped, nothing in it can be sold ({1})', g.Game, items(g.Items))
    : tf('{0} - {1}  ({2})', g.Game, usdExact(g.Value), items(g.Items)));
  if (b.InventoryPending > 0) lines.push(tf('...{0} more still being priced', items(b.InventoryPending)));
  return tf('{0} at market median', usdExact(b.InventoryValue)) + nlChar + nlChar + lines.join(nlChar);
}

// ── formatting ───────────────────────────────────────────────────────
function hm(minutes) {
  if (!minutes) return '0' + t('m');
  return minutes < 60
    ? minutes + t('m')
    : Math.floor(minutes / 60) + t('h') + String(minutes % 60).padStart(2, '0') + t('m');
}

function ago(iso) {
  if (!iso) return t('never');
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 1) return t('just now');
  return tf('{0} ago', hm(mins));
}

// A future time: "in 45m" when it's close, a clock time "~14:30" when it's further off.
function until(iso) {
  if (!iso) return '';
  const mins = Math.round((new Date(iso).getTime() - Date.now()) / 60000);
  if (mins <= 0) return t('now');
  if (mins < 90) return tf('in {0}', hm(mins));
  const d = new Date(iso);
  return '~' + String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

// rep4rep is optional and can be switched off entirely (a third-party site many users won't touch). When it's
// off the whole feature vanishes from the UI. Default to ON if we haven't heard from the server yet.
const r4rOn = () => !state || state.Rep4RepEnabled !== false;

// ── navigation ───────────────────────────────────────────────────────
function go(name) {
  // rep4rep is switched off - there is no tab to land on, so send them to the overview instead.
  if (name === 'rep4rep' && !r4rOn()) name = 'overview';
  // An old bookmark or a typo in the address bar (#setings) hid every view and left a blank page.
  if (!$('view-' + name)) name = 'overview';

  view = name;
  location.hash = name;
  document.querySelectorAll('.navitem').forEach((n) => n.classList.toggle('active', n.dataset.view === name));
  // On a phone the tab bar scrolls sideways - bring the tab you're on into view, not left off the edge.
  const tab = document.querySelector('.navitem.active');
  if (tab && tab.parentElement.scrollWidth > tab.parentElement.clientWidth) tab.scrollIntoView({ block: 'nearest', inline: 'center' });
  document.querySelectorAll('.view').forEach((v) => v.classList.toggle('hidden', v.id !== 'view-' + name));

  // Returned so a caller can wait for the settings form to be drawn before reaching into it.
  let ready = Promise.resolve();
  if (name === 'settings') { ready = loadConfig().then(renderSettings); }
  if (name === 'rep4rep') { loadRep4Rep(); }
  if (name === 'console') { $('cmd').focus(); renderCommandList(); }
  if (name === 'log') renderLog();
  if (name === 'plugins') loadPlugins();
  if (name === 'auth') openAuth();
  if (name !== 'auth') closeAuth();
  if (name === 'phone') { renderPhone(); loadConfig().catch(() => {}).then(() => loadPhone(true)); }
  render();
  return ready;
}

document.querySelectorAll('.navitem').forEach((n) => {
  n.addEventListener('click', () => go(n.dataset.view));
  // An <a> with no href gets no keyboard activation for free, so the focus ring would have led
  // somewhere that could be seen but not used.
  n.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(n.dataset.view); }
  });
});
window.addEventListener('hashchange', () => { const h = location.hash.replace('#', ''); if (h && h !== view) go(h); });

document.addEventListener('keydown', (e) => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT') return;
  // 1-9 are the tabs as they're shown, left to right - so they still line up when one is hidden or a new one is added.
  const tabs = [...document.querySelectorAll('.navitem')].filter((el) => el.offsetParent !== null);
  const n = parseInt(e.key, 10);
  if (n >= 1 && n <= tabs.length) go(tabs[n - 1].dataset.view);
  if (e.key === '`') { go('console'); e.preventDefault(); }
});

// ── render: shell ────────────────────────────────────────────────────
function render() {
  if (!state) return;
  const bots = state.Bots;

  $('navAccounts').textContent = bots.length || '';

  // rep4rep off -> drop its nav tab entirely; on -> show the points beside it once a token's set.
  const r4rTab = document.querySelector('.navitem[data-view="rep4rep"]');
  if (r4rTab) r4rTab.classList.toggle('hidden', !r4rOn());

  // No account's authenticator is in nocat.farm: the Authenticator tab would only ever say so - it goes, and comes back
  // by itself the moment one is added.
  const authTab = document.querySelector('.navitem[data-view="auth"]');
  if (authTab) authTab.classList.toggle('hidden', !bots.some((b) => b.HasAuthenticator));
  $('navPoints').textContent = r4rOn() && state.Rep4RepToken ? state.Points : '';

  // The Plugins tab stays put whether plugins are on or off, and says which inside.
  //
  // Kept HERE, with the rest of the rail, and not in renderOverview where it started: that only runs while
  // the Overview tab is showing, so toggling the setting from Settings or from the Plugins panel itself -
  // which is where anybody actually toggles it - left the tab saying whatever it said before, until a reload.
  // Turning plugins on and straight back off was the visible version: the OFF tag left and never came back.
  const navPlug = $('navPlugins');
  if (navPlug) {
    navPlug.classList.remove('hidden');
    navPlug.classList.toggle('off', !state.PluginsOn);
    const tag = navPlug.querySelector('.navtag');
    if (tag) tag.textContent = state.PluginsOn ? '' : t('off');
  }

  // rail chips
  const counts = {};
  bots.forEach((b) => { counts[b.Group] = (counts[b.Group] || 0) + 1; });
  $('railChips').innerHTML = Object.keys(STATUS_META)
    .filter((k) => counts[k])
    // Lit like the Accounts page's own filter pills when it is the filter showing there - the two are one filter.
    .map((k) => `<span class="chip ${k} ${view === 'accounts' && acctFilter === k ? 'on' : ''}" data-tip="${esc(t(STATUS_META[k].tip))}" onclick="filterTo('${k}')"><i class="dot"></i>${esc(t(STATUS_META[k].label))}<b>${counts[k]}</b></span>`)
    .join('') || `<span class="muted small">${esc(t('no accounts yet'))}</span>`;
  fitRailChips();

  paint('railStats', `
    <dt data-tip="${esc(t("Trading cards still to drop across every account that's farming."))}">${esc(t('Cards left'))}</dt><dd>${state.CardsLeft}</dd>
    ${r4rOn() && state.Rep4RepToken ? `<dt data-tip="${esc(t("Points you can spend on rep4rep. Pending ones are comments rep4rep hasn't verified yet."))}">${esc(t('Points'))}</dt><dd>${state.Points}${state.PendingPoints ? ' <span class="muted">+' + state.PendingPoints + '</span>' : ''}</dd>` : ''}
    <dt data-tip="${esc(t('How long nocat.farm has been running.'))}">${esc(t('Up'))}</dt><dd>${hm(state.UptimeMinutes)}</dd>`);

  // Kept with the rail rather than in renderOverview, for the same reason as the Plugins tag above: that only
  // runs on the Overview tab, so a dashboard opened on any other tab showed the placeholder version from the
  // page and never offered the update until you happened to visit Overview.
  // Version, and whether there's a newer one. The link always goes to the repo; when an update exists it says
  // so and points at that release instead.
  const ver = $('version');
  if (ver) {
    if (state.UpdateAvailable) {
      ver.textContent = `v${state.Version} → ${state.UpdateAvailable}`;
      ver.href = state.UpdateUrl || 'https://github.com/VisaHolder/nocatfarm/releases';
      ver.classList.add('update');
      ver.dataset.tip = tf('{0} is out - you have {1}. Click to see what changed.', state.UpdateAvailable, state.Version);
    } else {
      ver.textContent = 'v' + state.Version;
      ver.href = 'https://github.com/VisaHolder/nocatfarm';
      ver.classList.remove('update');
      ver.dataset.tip = t('nocat.farm on GitHub - source, releases and issues.');
    }
  }

  // The button that installs it, beside the chip that announces it.
  //
  // Separate from the link on purpose: the link is "what changed", this is "do it". Nothing updates on its
  // own and there is no setting to make it - plenty of people would rather keep a build that works than take
  // whatever is newest, and an update that lands unasked mid-session costs them a night's farming.
  const upd = $('updateBtn');
  if (upd) {
    const busy = state.UpdateBusy;
    upd.classList.toggle('hidden', !state.UpdateAvailable || state.CanSelfUpdate === false);
    upd.disabled = !!busy;
    upd.textContent = busy ? (state.UpdateProgress || t('working…')) : tf('Update to {0}', state.UpdateAvailable || '');
    upd.dataset.tip = busy
      ? t('Downloading. It restarts by itself when it lands.')
      : state.UpdateWaits
        ? tf('Install {0} once your accounts are asleep. Your accounts, tokens, settings and logs are left exactly as they are.', state.UpdateAvailable || '')
        : tf('Download {0} and restart into it. Your accounts, tokens, settings and logs are left exactly as they are.', state.UpdateAvailable || '');
  }

  renderAlerts();

  if (view === 'overview') renderOverview();
  if (view === 'accounts') renderAccounts();
  if (view === 'rep4rep') renderRep4RepPacing();
}

/// A status chip in the rail: show those accounts. It clears the filter only where you can see it's on - on the Accounts
/// page, lit up, the way a second click on a filter pill there clears it. From anywhere else it toggled a filter left
/// over from an earlier visit, so "3 farming" opened a page showing every account.
function filterTo(group) {
  acctFilter = view === 'accounts' && acctFilter === group ? '' : group;
  go('accounts');
}

function renderAlerts() {
  const out = [];

  // An account signing in by QR: the code to scan, big enough for a phone camera.
  (state.QrWaiting || []).forEach((q) => {
    out.push(`<div class="alert warn qr"><img src="/api/bots/${encodeURIComponent(q.Name)}/qr.svg?v=${q.QrVersion}" alt="QR code" width="168" height="168">
      <span>${esc(tf('Scan this with the Steam app on your phone to sign {0} in - the Steam Guard tab has the QR scanner.', q.Name))}</span></div>`);
  });

  // Not while the first-run setup is asking it itself - two boxes for one code, and this one would take the focus.
  if (state.Prompt && !tutorialSignin) {
    out.push(`<div class="alert warn">
      <span data-tip="${esc(t("Type the code from the Steam app on your phone, or from your email. nocat.farm can't finish logging in until you do."))}">${esc(state.Prompt)}</span>
      <input id="promptInput" type="${state.PromptSecret ? 'password' : 'text'}" autocomplete="off">
      <button onclick="sendPrompt()">${esc(t('Submit'))}</button></div>`);
  }

  // An update that failed, and why - red, with the reason, and a way to try again. Said in the log as well; this
  // is so it can't be missed after a restart that brought the old version back.
  if (state.UpdateFailed) {
    out.push(`<div class="alert bad"><span><b>${esc(t('Update failed'))}</b> - ${esc(state.UpdateFailed)}</span>
      <span class="spacer"></span><button onclick="doUpdate(true)">${esc(t('Try again'))}</button></div>`);
  }

  // Exposed only fires when a password IS set and is short — so "anyone can open this" was simply untrue, and
  // "set a password" pointed at a box that already had one in it. Say the thing that's actually wrong.
  if (state.Exposed) {
    out.push(`<div class="alert bad"><span>${esc(t('This dashboard is reachable from your network and its password is short enough to guess. Your Steam accounts are behind it.'))}</span>
      <span class="spacer"></span><button onclick="goSetting('WebPassword')">${esc(t('Use a longer one'))}</button></div>`);
  }

  // The case that genuinely means "nobody else can get in" was computed and then never shown.
  if (state.LockedToThisPc) {
    out.push(`<div class="alert warn"><span>${esc(t("This dashboard is set to listen on the network, but with no password it refuses every connection that isn't from this PC — so it's only reachable here."))}</span>
      <span class="spacer"></span><button onclick="goSetting('WebPassword')">${esc(t('Set a password to open it up'))}</button></div>`);
  }

  if (state.Rep4RepWanted > 0 && !state.Rep4RepToken) {
    const many = state.Rep4RepWanted > 1;
    out.push(`<div class="alert warn"><span>${esc(many
      ? tf("rep4rep is switched on for {0} accounts but there's no API token, so nothing is being posted.", state.Rep4RepWanted)
      : t("rep4rep is switched on for one account but there's no API token, so nothing is being posted."))}</span>
      <span class="spacer"></span><button onclick="go('rep4rep')">${esc(t('Add token'))}</button></div>`);
  }

  state.Bots.filter((b) => b.Group === 'problem').forEach((b) => {
    out.push(`<div class="alert bad"><span><b>${esc(b.Name)}</b> — ${esc(b.Detail)}</span></div>`);
  });

  // Only rewrite when the content actually changed. This ran on every poll, and the Steam Guard box lives
  // inside it - so typing a 5-digit code meant racing a 3-second timer that wiped the field.
  const html = out.join('');

  if (html !== lastAlertsHtml) {
    lastAlertsHtml = html;

    // Whatever is half typed into the Steam Guard box survives the redraw. Any other alert changing - a QR code
    // refreshing, an update turning up - rebuilt the box and threw away the code being typed. Kept only while it's the
    // same question; the focus only goes to the box for a new one, or back to it if it had it.
    const old = $('promptInput');
    const sameAsk = !!old && (state.Prompt === lastPromptAsked);
    const typed = sameAsk ? old.value : '';
    const hadFocus = !!old && (document.activeElement === old);

    $('alerts').innerHTML = html;
    const input = $('promptInput');
    if (input) {
      input.value = typed;
      if (!sameAsk || hadFocus) input.focus();
      input.onkeydown = (e) => { if (e.key === 'Enter') sendPrompt(); };
    }
  }

  lastPromptAsked = state.Prompt || null;
}

// Jump to one global setting. Through selectSettings, so edits pending on an account's page get the usual "discard
// them?" question instead of vanishing; with Show advanced ticked when the setting is an advanced one (the password
// and the plugin switch both are - the jump used to land on a form that didn't draw them); and after the form is
// actually drawn, not after a 200ms guess that a slow load overran.
async function goSetting(name) {
  if (settingsTarget !== GLOBAL && !selectSettings(null)) return;
  try { await go('settings'); } catch { return; }

  const def = schema && schema.Global.find((d) => d.Name === name);
  const hidden = def && ((def.Advanced && !$('setAdvanced').checked) || $('setChanged').checked || $('setSearch').value);
  if (hidden) {
    if (def.Advanced) $('setAdvanced').checked = true;
    $('setChanged').checked = false;
    $('setSearch').value = '';
    renderSettings();
  }

  const el = document.querySelector(`#settingsBody [data-setting="${CSS.escape(name)}"]`);
  if (el) { el.scrollIntoView({ block: 'center' }); el.focus(); }
}

function sendPrompt() {
  const el = $('promptInput');
  if (!el) return;
  const value = el.value;
  el.value = '';
  post('/api/prompt', { Value: value }).then((res) => {
    if (!res.ok) toast(t('Nothing was waiting for that answer'), true);
    refresh();
  });
}

// ── render: overview ─────────────────────────────────────────────────
function renderOverview() {
  const bots = state.Bots;
  const need = bots.filter((b) => b.Group === 'needsyou');
  const bad = bots.filter((b) => b.Group === 'problem');
  // Playing (human mode in a game, or a grind) and night idle bank hours too - leaving them out read
  // "3 accounts · 2 working" while all three were busy.
  const busy = bots.filter((b) => ['farming', 'idling', 'playing', 'nightidle'].includes(b.Group));

  let verdict;
  if (!bots.length) verdict = t('No accounts yet.');
  else if (need.length) {
    const who = need.map((b) => b.Name).join(', ');
    verdict = need.length > 1 ? tf('{0} need something from you.', who) : tf('{0} needs something from you.', who);
  } else if (bad.length) {
    verdict = bad.length > 1 ? tf('{0} accounts have a problem.', bad.length) : t('One account has a problem.');
  } else {
    const many = bots.length > 1 ? tf('{0} accounts', bots.length) : tf('{0} account', bots.length);
    verdict = tf("{0} · {1} working · everything's fine.", many, busy.length);
  }
  $('verdict').textContent = verdict;

  const tile = (n, k, tip, sub) =>
    `<div class="tile"><div class="n">${n}</div><div class="k">${esc(t(k))}${tipIcon(t(tip))}</div>${sub ? `<div class="sub">${esc(sub)}</div>` : ''}</div>`;

  paint('tiles',
    tile(state.CardsLeft, 'Cards left', 'Trading cards still to drop across every account.', state.CardsLeft ? '' : t('nothing left to farm')) +
    tile(state.GamesLeft, 'Games left', 'Games with at least one card still to drop, across every account.') +
    tile(state.CardsToday, 'Cards today', 'Trading cards that dropped in the last 24 hours.') +
    tile(usd(state.InventoryValue), 'Inventory', "What every account's inventory would fetch at the market's median price. Everything in there is counted at what it's worth, whether or not it can be sold right now.",
      state.InventoryPending > 0 ? tf('still pricing {0}', state.InventoryPending) : '') +
    (!r4rOn()
      ? ''
      : state.Rep4RepToken
        ? tile(state.Points, 'rep4rep points', "Points you can spend. Pending points are comments rep4rep hasn't verified yet - they turn into real points on their own, usually within a few hours. Nothing is lost.",
            state.PendingPoints ? tf('{0} pending', state.PendingPoints) : '')
        : tile(state.CommentsToday, 'Comments today', 'rep4rep comments posted in the last 24 hours.')));

  paint('glance', bots.length ? `<div class="tablewrap"><table>
    <tr><th>${esc(t('Account'))}</th><th>${esc(t('State'))}</th><th>${esc(t('Playing'))}</th><th>${esc(t('Cards'))}</th><th data-tip="${esc(t("What everything in this account's inventory would fetch at the market's median price. Items with no market listing count as nothing; items it merely can't sell right now (trade holds, bans) are still counted at what they are worth."))}">${esc(t('Value'))}</th>${r4rOn() ? `<th class="r4r">${esc(t('rep4rep'))}</th>` : ''}<th class="up">${esc(t('Up'))}</th></tr>
    ${bots.map((b) => `<tr class="click" data-act="cards" data-bot="${esc(b.Name)}">
      <td><b>${esc(b.Name)}</b></td>
      <td><span class="chip ${b.Group}"><i class="dot"></i>${esc(b.Status)}</span></td>
      <td>${esc(b.Playing || '—')}</td>
      <td>${b.Cards || '—'}</td>
      <td data-tip="${esc(valueTip(b))}">${b.InventoryValue > 0 ? usd(b.InventoryValue) + (b.InventoryPending > 0 ? '<span class="muted">+</span>' : '') + valueDelta(b) : (b.InventoryOn === false || b.InventoryReady ? '—' : '<span class="muted">…</span>')}</td>
      ${r4rOn() ? `<td class="r4r">${b.Rep4Rep ? b.Rep4RepToday + '/' + b.Rep4RepCap : '—'}</td>` : ''}
      <td class="up">${b.UptimeMinutes ? hm(b.UptimeMinutes) : '—'}</td></tr>`).join('')}
    </table></div>`
    : `<p class="muted">${esc(t('No accounts yet.'))} <a href="#console" onclick="go('console')">${esc(t('Add one'))}</a> ${esc(t('or type'))} <code>add mybot mysteamlogin</code>.</p>`);

  renderToday();
  renderHistory();
  loadHistory();

  const interesting = logLines.filter((l) => l.Level !== 'INFO' && l.Level !== 'DEBUG').slice(-8).reverse();
  paint('recent', interesting.length
    ? interesting.map((l) => `<div class="line"><span class="who">${esc(l.Source)}</span><span>${esc(l.Text)}</span><span class="when">${esc(l.Time)}</span></div>`).join('')
    : `<p class="muted">${esc(t('Nothing worth reporting yet.'))}</p>`);
}

// Per-account activity in the last 24h. A tidy table, because the old by-hour bar chart was 24 near-empty bars
// the moment a farm finished its cards and was just idling - which reads as broken, not informative.
function renderToday() {
  const bots = state.Bots;
  if (!bots.length) { $('today').innerHTML = `<p class="muted empty">${esc(t('No accounts yet.'))}</p>`; return; }

  const r4r = r4rOn();
  const num = (n) => n ? n : '<span class="muted">0</span>';
  let totCards = 0, totComments = 0;

  const rows = bots.map((b) => {
    const c = b.CardsToday || 0;
    const cm = b.Rep4RepToday || 0;
    totCards += c; totComments += cm;
    return `<tr>
      <td><b>${esc(b.Name)}</b></td>
      <td>${num(c)}</td>
      ${r4r ? `<td>${num(cm)}</td>` : ''}</tr>`;
  }).join('');

  paint('today', `<div class="tablewrap"><table class="today">
    <tr><th>${esc(t('Account'))}</th><th data-tip="${esc(t('Trading cards that dropped in the last 24 hours.'))}">${esc(t('Cards'))}</th>${r4r ? `<th data-tip="${esc(t('rep4rep comments posted in the last 24 hours.'))}">${esc(t('Comments'))}</th>` : ''}</tr>
    ${rows}
    <tr class="fleet"><td>${esc(t('fleet'))}</td><td>${totCards}</td>${r4r ? `<td>${totComments}</td>` : ''}</tr>
    </table></div>`);
}

// ── render: history ──────────────────────────────────────────────────
// The Today table answers "what did it do in the last 24 hours"; this asks the same question over days and weeks.
// Fetched once a minute while the Overview is open - all 90 days in one go, so flipping between 7, 30 and 90 is
// instant - and drawn as plain inline SVG in the page's own greys. One series per chart, so there's no legend and
// no colour to decode: the account picker changes what is added up, not what is drawn. The server does the per-day
// bookkeeping; the page only sums accounts and picks the window.
let histData = null;       // /api/history
let histAt = 0;            // when it was last asked for, answered or not
let histBusy = false;
let histScope = '';        // '' = the whole fleet, otherwise one account's name
let histRange = 30;
try {
  const saved = Number(localStorage.getItem('nocatfarm-histrange'));
  if ([7, 30, 90].includes(saved)) histRange = saved;
} catch { /* a private window - 30 days it is */ }
const HIST_DAYS = 90;

async function loadHistory(force) {
  if (histBusy || (!force && Date.now() - histAt < 60000)) return;
  histBusy = true;
  try {
    histData = await api('/api/history?days=' + HIST_DAYS);
  } catch { /* keep the last frame; the next minute tries again */ }
  finally { histBusy = false; histAt = Date.now(); }
  if (view === 'overview') renderHistory();
}

function setHistRange(n) {
  histRange = n;
  try { localStorage.setItem('nocatfarm-histrange', String(n)); } catch { /* remembered for this visit only */ }
  renderHistory();
}

function setHistScope(name) { histScope = name; renderHistory(); }

/// paint(), but compared against the markup we generated rather than innerHTML. A browser writes SVG back out
/// differently from how it went in, so paint() would see a change on every poll and rebuild the charts under the
/// pointer every three seconds - the hover highlight and the tooltip with them.
function histPaint(el, html) {
  if (!el || el._histHtml === html) return;
  el._histHtml = html;
  el.innerHTML = html;
}

// 2026-09-28 -> "28 Sep", or "Mon 28 Sep" with long, in the dashboard's own language.
function histDate(key, long) {
  const [y, m, d] = key.split('-').map(Number);
  const code = config && config.Global && config.Global.Language;
  try {
    return new Date(y, m - 1, d).toLocaleDateString(code && code !== 'en' ? code : undefined,
      long ? { weekday: 'short', day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short' });
  } catch { return key.slice(5); }
}

const histR = (x) => Math.round(x * 10) / 10;
const histSum = (arr) => (arr || []).reduce((s, v) => s + (v || 0), 0);
const histSigned = (n, fmt) => (n > 0 ? '+' : n < 0 ? '-' : '±') + fmt(Math.abs(n));
const histCards = (n) => (n === 1 ? t('1 card') : tf('{0} cards', n));
const histComments = (n) => (n === 1 ? t('1 comment') : tf('{0} comments', n));
const histName = (app) => (histData.Names && histData.Names[app]) || ('app ' + app);

/// A translated sentence with some blanks filled by markup (a bold number). The sentence is escaped first and the
/// values go in after, so a translation can move the numbers about but can never inject anything.
const histFill = (template, ...html) => esc(template).replace(/\{(\d+)\}/g, (whole, i) => (html[i] === undefined ? whole : html[i]));
const histB = (s) => `<b>${esc(s)}</b>`;

/// Everything the charts need for the chosen account (or the whole fleet), over all the days fetched. Each chart
/// takes the last 7, 30 or 90 of it; the week-on-week sentences always use the last 14.
function histModel() {
  const d = histData;
  const all = d.Days.length;
  const n = Math.min(histRange, all);
  const accts = histScope ? d.Accounts.filter((a) => a.Name === histScope) : d.Accounts;
  const total = (field) => {
    const out = new Array(all).fill(0);
    accts.forEach((a) => (a[field] || []).forEach((v, i) => { out[i] += v || 0; }));
    return out;
  };

  // An inventory isn't re-priced every day. A day with no reading carries the last one over - otherwise the fleet
  // total would plunge every time one account simply wasn't re-read that day.
  const carried = accts.map((a) => {
    let last = a.ValueBefore == null ? null : a.ValueBefore;
    return (a.Value || []).map((v) => {
      if (v != null) { last = v; return { v, real: true }; }
      return last == null ? null : { v: last, real: false };
    });
  });
  const value = Array.from({ length: all }, (_, i) => {
    let sum = null, real = true;
    carried.forEach((c) => { if (c[i]) { sum = (sum || 0) + c[i].v; real = real && c[i].real; } });
    return sum == null ? null : { v: sum, real };
  });

  const games = Array.from({ length: all }, () => ({}));
  accts.forEach((a) => (a.Games || []).forEach(([i, app, mins]) => {
    if (i >= 0 && i < all) games[i][app] = (games[i][app] || 0) + mins;
  }));

  return { all, n, off: all - n, accts, carried, value, games, cards: total('Cards'), comments: total('Comments'), minutes: total('Minutes') };
}

// The first line of every day's tooltip.
const histDay = (m, i) => histDate(histData.Days[i], true) + (i === m.all - 1 ? ' · ' + t('today so far') : '');

/// Who contributed what on one day, biggest first - only when the chart is the whole fleet and there is more than
/// one account to split it between.
function histSplit(m, i, get, fmt) {
  if (histScope || m.accts.length < 2) return '';
  const rows = m.accts.map((a, k) => [a.Name, get(a, k)]).filter((r) => r[1] > 0).sort((x, y) => y[1] - x[1]);
  if (!rows.length) return '';
  return nlChar + rows.slice(0, 10).map(([name, v]) => `${name}: ${fmt(v)}`).join(nlChar) + (rows.length > 10 ? nlChar + '…' : '');
}

// A 1-2-5 step that covers span in about count steps.
function histStep(span, count) {
  const raw = span / count;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  const f = raw / mag;
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
}

// y-axis ticks for whole numbers (cards, comments) and for minutes, which read as hours past three of them.
const histCountAxis = (max) => ({ step: Math.max(1, Math.round(histStep(max, 3))), label: (v) => String(v) });
const histMinuteAxis = (max) => ({
  step: max <= 180 ? ([5, 10, 15, 30, 60].find((s) => s * 3 >= max) || 60) : histStep(max / 60, 3) * 60,
  label: (v) => (v === 0 ? '0' : v % 60 === 0 ? (v / 60) + t('h') : v + t('m'))
});

// Day labels along the bottom, as many as fit without touching, always ending on today.
function histXLabels(m, padL, pw, slot, h) {
  const every = Math.max(1, Math.ceil(m.n / Math.max(1, Math.floor(pw / 64))));
  let s = '';
  for (let j = m.n - 1; j >= 0; j -= every) {
    const text = j === m.n - 1 ? t('today') : histDate(histData.Days[m.off + j]);
    const half = text.length * 3.1;
    const x = Math.min(padL + pw - half, Math.max(padL + half, padL + j * slot + slot / 2));
    s += `<text class="xl" x="${histR(x)}" y="${h - 3}" text-anchor="middle">${esc(text)}</text>`;
  }
  return s;
}

/// One column per day, square-cornered like everything else here. Each day is a hover target the full height of
/// the chart, so a day with nothing in it still answers when you point at it. Today is drawn lighter: it isn't over.
/// The class is hbar, not bar: the progress bars own .bar, and a CSS height on it squashed every rect to 4px.
function histBars(w, h, m, vals, axis, tipFor, label) {
  const slice = vals.slice(m.off);
  const max = Math.max(1, ...slice);
  const { step, label: tick } = axis(max);
  const top = Math.max(step, Math.ceil(max / step) * step);
  const ticks = [];
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(v);
  const padL = 8 + 6.2 * Math.max(...ticks.map((v) => tick(v).length));
  const padR = 2, padT = 6, padB = 18;
  const pw = w - padL - padR, ph = h - padT - padB;
  const slot = pw / m.n;
  const bw = Math.max(1, Math.min(24, slot * 0.7, slot - 1));

  let s = `<svg viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" role="img" aria-label="${esc(label)}">`;
  ticks.forEach((v) => {
    const y = Math.round(padT + ph - (v / top) * ph) + 0.5;
    if (v > 0) s += `<line class="grid" x1="${histR(padL)}" x2="${w - padR}" y1="${y}" y2="${y}"/>`;
    s += `<text class="yl" x="${histR(padL - 6)}" y="${y + 3.5}" text-anchor="end">${esc(tick(v))}</text>`;
  });
  slice.forEach((v, j) => {
    const x0 = padL + j * slot;
    const bh = v > 0 ? Math.max(1.5, (v / top) * ph) : 0;
    s += `<g class="col" data-tip="${esc(tipFor(m.off + j))}"><rect class="hit" x="${histR(x0)}" y="${padT}" width="${histR(slot)}" height="${ph}"/>`
      + (bh ? `<rect class="hbar${j === m.n - 1 ? ' today' : ''}" x="${histR(x0 + (slot - bw) / 2)}" y="${histR(padT + ph - bh)}" width="${histR(bw)}" height="${histR(bh)}"/>` : '')
      + '</g>';
  });
  const base = Math.round(padT + ph) + 0.5;
  s += `<line class="base" x1="${histR(padL)}" x2="${w - padR}" y1="${base}" y2="${base}"/>`;
  return s + histXLabels(m, padL, pw, slot, h) + '</svg>';
}

/// The inventory's value as a line, with a faint wash under it. Not from zero: an inventory moves a few percent in
/// a month, and a zero baseline would flatten that into a ruler. Hovering a day shows a hairline and the value.
function histLine(w, h, m, tipFor, label) {
  const pts = m.value.slice(m.off);
  const vals = pts.filter(Boolean).map((p) => p.v);
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const step = histStep((hi - lo) || Math.max(1, hi * 0.1), 3);
  let bottom = Math.max(0, Math.floor(lo / step) * step);
  let top = Math.ceil(hi / step) * step;
  if (top <= bottom) { top = bottom + step; bottom = Math.max(0, bottom - step); }
  const ticks = [];
  for (let v = bottom; v <= top + step / 2; v += step) ticks.push(v);
  const money = (v) => (step < 1 ? usdExact(v) : v >= 10000 ? cur() + v.toLocaleString('en-US', { notation: 'compact', maximumFractionDigits: 1 }) : usd(v));
  const padL = 8 + 6.2 * Math.max(...ticks.map((v) => money(v).length));
  const padR = 6, padT = 8, padB = 18;
  const pw = w - padL - padR, ph = h - padT - padB;
  const slot = pw / m.n;
  const x = (j) => padL + j * slot + slot / 2;
  const y = (v) => padT + ph - ((v - bottom) / (top - bottom)) * ph;

  let s = `<svg viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" role="img" aria-label="${esc(label)}">`;
  ticks.forEach((v, k) => {
    const yy = Math.round(y(v)) + 0.5;
    s += `<line class="${k === 0 ? 'base' : 'grid'}" x1="${histR(padL)}" x2="${w - padR}" y1="${yy}" y2="${yy}"/>`
      + `<text class="yl" x="${histR(padL - 6)}" y="${yy + 3.5}" text-anchor="end">${esc(money(v))}</text>`;
  });

  const drawn = pts.map((p, j) => (p ? [histR(x(j)), histR(y(p.v))] : null)).filter(Boolean);
  if (drawn.length > 1) {
    const line = drawn.map((p, k) => (k ? 'L' : 'M') + p[0] + ',' + p[1]).join('');
    const floor = histR(padT + ph);
    s += `<path class="area" d="${line}L${drawn[drawn.length - 1][0]},${floor}L${drawn[0][0]},${floor}Z"/><path class="line" d="${line}"/>`;
  }
  if (drawn.length) {
    const last = drawn[drawn.length - 1];
    s += `<circle class="dot" cx="${last[0]}" cy="${last[1]}" r="4"/>`;
  }
  pts.forEach((p, j) => {
    const cx = histR(x(j));
    s += `<g class="col" data-tip="${esc(tipFor(m.off + j))}"><rect class="hit" x="${histR(padL + j * slot)}" y="${padT}" width="${histR(slot)}" height="${ph}"/>`
      + `<line class="xh" x1="${cx}" x2="${cx}" y1="${padT}" y2="${histR(padT + ph)}"/>`
      + (p ? `<circle class="hd" cx="${cx}" cy="${histR(y(p.v))}" r="4"/>` : '')
      + '</g>';
  });
  return s + histXLabels(m, padL, pw, slot, h) + '</svg>';
}

/// Hours per game over the window: the eight biggest, then everything else as one row, so a card-farming week
/// across forty games still fits in a panel.
function histGames(m) {
  const sums = {};
  for (let i = m.off; i < m.all; i++) {
    Object.entries(m.games[i]).forEach(([app, mins]) => { sums[app] = (sums[app] || 0) + mins; });
  }
  const ranked = Object.entries(sums).filter((g) => g[1] > 0).sort((a, b) => b[1] - a[1]);
  if (!ranked.length) return null;

  const total = histSum(ranked.map((g) => g[1]));
  const pct = (mins) => Math.max(1, Math.round((mins / total) * 100));
  const rows = ranked.slice(0, 8).map(([app, mins]) => ({
    name: histName(app), mins,
    tip: histName(app) + nlChar + tf('{0} in {1} days, {2}% of all the game time', hm(mins), m.n, pct(mins))
  }));
  const rest = ranked.slice(8);
  if (rest.length) {
    const mins = histSum(rest.map((g) => g[1]));
    rows.push({
      other: true, name: t('Other'), mins,
      tip: tf('{0} more games', rest.length) + nlChar + rest.slice(0, 12).map(([app, v]) => `${histName(app)}: ${hm(v)}`).join(nlChar) + (rest.length > 12 ? nlChar + '…' : '')
    });
  }
  const max = Math.max(...rows.map((r) => r.mins));

  return {
    count: ranked.length,
    html: `<div class="hgames">${rows.map((r) => `<div class="hg${r.other ? ' other' : ''}" data-tip="${esc(r.tip)}">
      <span class="gn">${esc(r.name)}</span><span class="gb"><i style="width:${Math.max(1, (r.mins / max) * 100).toFixed(1)}%"></i></span><span class="gv">${esc(hm(r.mins))}</span></div>`).join('')}</div>`
  };
}

function renderHistory() {
  const box = $('history'), bar = $('histBar');
  if (!box || !bar || !histData || !Array.isArray(histData.Days) || !histData.Days.length || !Array.isArray(histData.Accounts)) return;
  const d = histData;
  if (histScope && !d.Accounts.some((a) => a.Name === histScope)) histScope = '';

  const anything = d.Accounts.some((a) => histSum(a.Cards) || histSum(a.Comments) || histSum(a.Minutes)
    || a.ValueBefore != null || (a.Value || []).some((v) => v != null));
  if (!anything) {
    histPaint(bar, '');
    histPaint(box, `<p class="muted empty">${esc(t('No history yet - it fills in day by day.'))}</p>`);
    return;
  }

  histPaint(bar, `<div class="pills">${[7, 30, 90].map((n) =>
    `<span class="p${histRange === n ? ' on' : ''}" onclick="setHistRange(${n})">${esc(tf('{0} days', n))}</span>`).join('')}</div>`
    + (d.Accounts.length > 1
      ? `<select onchange="setHistScope(this.value)" aria-label="${esc(t('Show the history of'))}"><option value="">${esc(t('Whole fleet'))}</option>${d.Accounts.map((a) =>
        `<option value="${esc(a.Name)}"${a.Name === histScope ? ' selected' : ''}>${esc(a.Name)}</option>`).join('')}</select>`
      : ''));

  const m = histModel();
  const r4r = r4rOn();
  const inDays = tf('in {0} days', m.n);
  const week = (arr) => [histSum(arr.slice(-7)), histSum(arr.slice(-14, -7))];

  // This week against last, in words - the one line that says whether things are going up or down.
  const said = [];
  const [c1, c0] = week(m.cards);
  said.push(histFill(t('{0} this week, {1} vs last week'), histB(histCards(c1)), histB(histSigned(c1 - c0, String))));
  const [h1, h0] = week(m.minutes);
  said.push(histFill(t('{0} banked this week, {1} vs last week'), histB(hm(h1)), histB(histSigned(h1 - h0, hm))));
  if (r4r) {
    const [r1, r0] = week(m.comments);
    said.push(histFill(t('{0} this week, {1} vs last week'), histB(histComments(r1)), histB(histSigned(r1 - r0, String))));
  }
  const vNow = m.value[m.all - 1], vThen = m.value[m.all - 8];
  if (vNow && vThen) {
    said.push(histFill(t('Inventory {0}, {1} this week'), histB(usd(vNow.v)), histB(histSigned(Math.round(vNow.v - vThen.v), usd))));
  }

  const panels = [];
  const draws = {};
  const panel = (title, tip, big, sub, body) => `<div class="hpanel"><div class="hhead"><span class="t">${esc(title)}${tipIcon(tip)}</span>`
    + `<span class="n">${big}${sub ? ` <small>${sub}</small>` : ''}</span></div>${body}</div>`;
  const chart = (id) => `<div class="hchart" id="hc-${id}"></div>`;
  const empty = (text) => `<div class="hempty">${esc(text)}</div>`;

  // cards
  const cardsIn = histSum(m.cards.slice(m.off));
  panels.push(panel(t('Cards dropped'), '', esc(cardsIn), esc(inDays),
    cardsIn ? chart('cards') : empty(tf('No cards dropped in the last {0} days.', m.n))));
  draws.cards = (w, h) => histBars(w, h, m, m.cards, histCountAxis,
    (i) => histDay(m, i) + nlChar + histCards(m.cards[i]) + histSplit(m, i, (a) => a.Cards[i] || 0, String), t('Cards dropped'));

  // comments - only while rep4rep is on at all, the same as everywhere else on the page
  if (r4r) {
    const commentsIn = histSum(m.comments.slice(m.off));
    panels.push(panel(t('rep4rep comments'), '', esc(commentsIn), esc(inDays),
      commentsIn ? chart('comments') : empty(tf('No comments posted in the last {0} days.', m.n))));
    draws.comments = (w, h) => histBars(w, h, m, m.comments, histCountAxis,
      (i) => histDay(m, i) + nlChar + histComments(m.comments[i]) + histSplit(m, i, (a) => a.Comments[i] || 0, String), t('rep4rep comments'));
  }

  // hours banked, per day
  const minutesIn = histSum(m.minutes.slice(m.off));
  panels.push(panel(t('Hours banked'), t('Every running game counts, the way Steam counts it - 32 games for an hour is 32 hours.'), esc(hm(minutesIn)), esc(inDays),
    minutesIn ? chart('hours') : empty(tf('Nothing played in the last {0} days.', m.n))));
  draws.hours = (w, h) => histBars(w, h, m, m.minutes, histMinuteAxis, (i) => {
    const top = Object.entries(m.games[i]).sort((a, b) => b[1] - a[1]).slice(0, 3);
    return histDay(m, i) + nlChar + hm(m.minutes[i]) + (top.length ? nlChar + top.map(([app, v]) => `${histName(app)}: ${hm(v)}`).join(nlChar) : '');
  }, t('Hours banked'));

  // hours per game
  const games = histGames(m);
  panels.push(panel(t('Hours by game'), t('How the hours banked split between the games.'),
    games ? esc(games.count === 1 ? t('one game') : tf('{0} games', games.count)) : '', games ? esc(inDays) : '',
    games ? games.html : empty(tf('Nothing played in the last {0} days.', m.n))));

  // inventory value
  const inRange = m.value.slice(m.off);
  const firstAt = inRange.findIndex(Boolean);
  const last = m.value[m.all - 1];
  let valueSub = '';
  if (firstAt >= 0 && last && firstAt < m.n - 1 && inRange[firstAt].v > 0) {
    const change = last.v - inRange[firstAt].v;
    const up = change >= 0;
    // "since" the first closing value in the window, which is exactly what the change is measured from - "in 30
    // days" would be a day out, because the first day's close is already the end of that day.
    valueSub = `<span class="delta ${up ? 'up' : 'down'}">${esc(histSigned(Math.round(change), usd))} (${up ? '+' : '-'}${Math.abs((change / inRange[firstAt].v) * 100).toFixed(1)}%)</span> `
      + esc(tf('since {0}', histDate(histData.Days[m.off + firstAt])));
  }
  panels.push(panel(t('Inventory value'), t('What the inventory was worth at the end of each day, at the market median. A day with no new reading carries the last one over.'),
    last ? esc(usd(last.v)) : '', valueSub,
    firstAt >= 0 ? chart('value') : empty(t('No inventory readings yet.'))));
  draws.value = (w, h) => histLine(w, h, m, (i) => {
    const p = m.value[i];
    if (!p) return histDay(m, i) + nlChar + t('No inventory readings yet.');
    return histDay(m, i) + nlChar + usdExact(p.v) + (p.real ? '' : nlChar + t('no new reading that day - carried over'))
      + histSplit(m, i, (a, k) => (m.carried[k][i] ? m.carried[k][i].v : 0), usdExact);
  }, t('Inventory value'));

  histPaint(box, `<p class="histsum" data-tip="${esc(t('This week is the last 7 days, today included. Last week is the 7 days before that.'))}">${said.map((x) => `<span>${x}</span>`).join('')}</p>`
    + `<div class="histgrid">${panels.join('')}</div>`);

  // Measured after the panels are in, so each chart is drawn at its real width - text in an SVG that is scaled to
  // fit goes blurry and changes size with the window.
  Object.keys(draws).forEach((id) => {
    const el = $('hc-' + id);
    if (!el || el.clientWidth < 80) return;
    histPaint(el, draws[id](el.clientWidth, el.clientHeight || 160));
  });
}

let histResize = null;
window.addEventListener('resize', () => {
  fitRailChips();
  clearTimeout(histResize);
  histResize = setTimeout(() => { if (view === 'overview') renderHistory(); }, 150);
});

// The top bar's status chips drop out whole, from the right, when there isn't room for them all. They used to just
// vanish - an account on its day off was simply missing from the count. Now a "+N" takes their place, and its tip
// says which.
function fitRailChips() {
  const box = $('railChips');
  if (!box) return;
  box.querySelector('.more')?.remove();
  const chips = [...box.querySelectorAll('.chip')];
  chips.forEach((c) => c.classList.remove('hidden'));
  if (chips.length < 2 || !box.offsetParent) return;

  const row = chips[0].offsetTop;
  const out = chips.filter((c) => c.offsetTop > row);
  if (!out.length) return;

  const kept = chips.filter((c) => !out.includes(c));
  out.forEach((c) => c.classList.add('hidden'));
  const more = document.createElement('span');
  more.className = 'chip more';
  more.onclick = () => go('accounts');
  box.appendChild(more);
  const paint = () => {
    more.innerHTML = `<b>+${out.length}</b>`;
    more.dataset.tip = out.map((c) => `${c.textContent.slice(0, -c.querySelector('b').textContent.length).trim()} ${c.querySelector('b').textContent}`).join(' · ');
  };
  paint();

  // The "+N" needs room of its own: give up chips from the right until it fits on the one line.
  while (more.offsetTop > row && kept.length > 1) {
    const c = kept.pop();
    c.classList.add('hidden');
    out.unshift(c);
    paint();
  }
}

// ── render: accounts ─────────────────────────────────────────────────
function renderAccounts() {
  // Never rebuild the list out from under a drag in progress.
  if (dragging) return;

  const q = ($('acctSearch').value || '').toLowerCase();

  const chips = Object.keys(STATUS_META).map((k) =>
    `<span class="chip ${k} ${acctFilter === k ? 'on' : ''}" data-tip="${esc(t(STATUS_META[k].tip))}" onclick="acctFilter='${acctFilter === k ? '' : k}';render()"><i class="dot"></i>${esc(t(STATUS_META[k].label))}</span>`).join('');

  paint('acctFilters', chips);

  const bots = state.Bots.filter((b) =>
    (!acctFilter || b.Group === acctFilter) &&
    (!q || (b.Name + ' ' + b.Login + ' ' + b.Notes + ' ' + b.Playing).toLowerCase().includes(q)));

  if (!bots.length) {
    $('bots').innerHTML = state.Bots.length
      ? `<p class="muted">${esc(t('No accounts match that.'))}</p>`
      : `<div class="card"><h2>${esc(t('No accounts yet'))}</h2>
         <p class="muted">${esc(t('Add one and nocat.farm will ask for the password and a Steam Guard code once, then remember it with a login token.'))}</p>
         <button onclick="showAddAccount()">+ ${esc(t('Add account'))}</button></div>`;
    return;
  }

  const cards = bots.map((b) => {
    // rep4rep is the only figure here with a real denominator, so it is the only one that gets a bar. The
    // card count has no known total, and inventing one (this used to be 100 - cards*5) is worse than a number.
    const capPct = b.Rep4Rep && b.Rep4RepCap ? Math.min(100, Math.round((b.Rep4RepToday / b.Rep4RepCap) * 100)) : 0;

    return `<div class="card bot ${b.Group}" draggable="true" data-name="${esc(b.Name)}"
      ondragstart="dragStart(event)" ondragover="dragOver(event)" ondragend="dragEnd()" ondrop="event.preventDefault();dragEnd()">
      <div class="bot-head">
        <span class="bot-name" title="${esc(b.Name)}" data-tip="${esc(t('Drag this card to move the account. The order is used everywhere - here, the app window and the console.'))}">${esc(b.Name)}</span>
        <span class="chip ${b.Group}" data-tip="${esc(t(STATUS_META[b.Group].tip))}"><i class="dot"></i>${esc(b.Status)}</span>
      </div>
      <div class="bot-login" title="${esc(b.Login)}">${esc(b.Login)}${b.SteamId !== '0' ? ' · ' + esc(b.SteamId) : ''}</div>
      ${b.Notes ? `<div class="bot-notes" title="${esc(b.Notes)}">${esc(b.Notes)}</div>` : ''}
      ${b.Guard ? `<div class="bot-guard">${esc(tf('Waiting on you: {0}', b.Guard))}</div>` : ''}
      <div class="bot-playing" title="${esc(b.Playing || '')}">${b.Playing ? esc(b.Playing) : `<span class="real">${esc(t('not playing anything'))}</span>`}</div>
      ${b.Online ? `<div class="bot-persona ${b.PersonaHidden ? 'hidden-persona' : ''}"
        data-tip="${esc(t("What your friends list shows for this account. The status is what nocat.farm set it to, and the game comes straight back from Steam. While it is invisible, friends see it as offline with no game - the hours still count. Human mode changes the status by itself: invisible overnight, away on a break, Snooze over a meal."))}">${esc(t('your friends see:'))} <b>${esc(b.PersonaHidden ? t('offline') : b.Persona)}</b>${b.Seen && !b.PersonaHidden ? ` · <b>${esc(b.Seen)}</b>` : ''}</div>` : ''}
      ${b.Bans ? `<div class="bot-bans" data-tip="${esc(t('What Steam shows about this account\'s bans. nocat.farm checks every few hours and tells you when a new one appears. Games it is banned in are left out of trades; trading cards still trade.'))}">${esc(tf('bans: {0}', b.Bans))}</div>` : ''}
      ${b.NameNotShowing && !b.PersonaHidden
        ? `<div class="bot-mismatch" data-tip="${esc(t("Steam decides what to display when a custom name is sent alongside real games, and it has settled on the real one. Idling fewer games, or only the custom name, makes it show yours. A brief mismatch right after signing in is normal and isn't reported here."))}">${esc(tf('Steam is showing {0}, not your custom name', b.Seen))}</div>`
        : ''}
      <div class="statline">
        <span>${b.Cards === 1 ? tf('<b>{0}</b> card', b.Cards) : tf('<b>{0}</b> cards', b.Cards)}${b.Games ? ' ' + esc(tf('in {0}', b.Games)) : ''}</span>
        ${r4rOn() && b.Rep4Rep ? `<span>${tf('<b>{0}</b>/{1} comments', b.Rep4RepToday, b.Rep4RepCap)}</span>` : ''}
        <span>${tf('<b>{0}</b> up', b.UptimeMinutes ? hm(b.UptimeMinutes) : '—')}</span>
        ${b.InventoryValue > 0 ? `<span data-tip="${esc(valueTip(b))}">${tf('<b>{0}</b> inventory', usd(b.InventoryValue))} ${valueDelta(b)}</span>` : ''}
      </div>
      ${r4rOn() && b.Rep4Rep ? `<div class="bar" data-tip="${esc(t("Comments posted in the last 24 hours against this account's daily cap."))}"><i style="width:${capPct}%"></i></div>` : ''}
      <div class="rows">
        ${b.Modules.filter((m) => !m.Quiet).map((m) =>
          `<div class="row"><span class="k">${esc(m.Name)}</span><span class="v" title="${esc(m.Status)}">${esc(m.Status)}</span></div>`).join('')}
      </div>
      <div class="actions">
        ${b.InventoryValue > 0 || b.InventoryReady ? `<button data-tip="${esc(t("Read this account's inventory again. Only prices that have gone stale are looked up, so it's quick."))}" data-act="inventory" data-bot="${esc(b.Name)}">${esc(t('Value'))}</button>` : ''}
        ${b.Online
          ? (b.Paused
            ? `<button data-tip="${esc(t('Start playing, farming and commenting again.'))}" data-act="resume" data-bot="${esc(b.Name)}">${esc(t('Resume'))}</button>`
            : `<button class="ghost" data-tip="${esc(t('Stop playing, farming and commenting. The account stays logged in.'))}" data-act="pause" data-bot="${esc(b.Name)}">${esc(t('Pause'))}</button>`)
          : `<button data-tip="${esc(t('Log this account in.'))}" data-act="start" data-bot="${esc(b.Name)}">${esc(t('Start'))}</button>`}
        ${b.State !== 'Stopped' ? `<button class="ghost" data-tip="${esc(t('Log this account out. It stays configured.'))}" data-act="stop" data-bot="${esc(b.Name)}">${esc(t('Stop'))}</button>` : ''}
        <button class="ghost" data-tip="${esc(t('What this account still has left to farm.'))}" data-act="cards" data-bot="${esc(b.Name)}">${esc(t('Cards'))}</button>
        <button class="ghost" data-tip="${esc(t("This account's settings."))}" data-act="settings" data-bot="${esc(b.Name)}">${esc(t('Settings'))}</button>
      </div></div>`;
  });

  paintCards(bots.map((b) => b.Name), cards);
}

/// Update the account list a CARD AT A TIME.
///
/// The whole list used to be rebuilt from its markup every poll - three seconds - so every button on every
/// card was destroyed and recreated under the cursor. A click landing during a rebuild went nowhere, open
/// tooltips vanished mid-read, and focus was lost.
///
/// Comparing the list as a whole is not enough: one account with a live countdown in it (a human-mode account
/// settling in, say) makes the combined markup differ every single time, and takes every other card down with
/// it. So each card is compared against the markup it was last built from - kept on the element, because the
/// browser rewrites attribute quoting and whitespace the moment it parses, and reading outerHTML back would
/// never match what we generated.
function paintCards(names, cards) {
  const container = $('bots');
  const existing = [...container.children];
  const sameAccounts = (existing.length === cards.length)
    && existing.every((el, i) => el.dataset.name === names[i]);

  // The set of accounts or their order changed - the cheap comparison no longer applies.
  if (!sameAccounts) {
    container.innerHTML = cards.join('');
    [...container.children].forEach((el, i) => { el._html = cards[i]; });

    return;
  }

  existing.forEach((el, i) => {
    if (el._html === cards[i]) {
      return;   // nothing about this account moved; leave its buttons exactly where they are
    }

    const tmp = document.createElement('div');
    tmp.innerHTML = cards[i];

    const fresh = tmp.firstElementChild;

    if (fresh) {
      fresh._html = cards[i];
      el.replaceWith(fresh);
    }
  });
}

// ── arranging the accounts ────────────────────────────────────────────────
// Dragging is live: the card moves under the cursor and the order is saved once, on drop. The dragging flag
// exists because the page re-renders itself every few seconds from the poll, and a re-render mid-drag would
// rebuild the list under your hand and drop the card back where it started.
let dragging = null;

function dragStart(e) {
  const card = e.target.closest('.card.bot');
  if (!card) return;
  dragging = card;
  card.classList.add('dragging');
  e.dataTransfer.effectAllowed = 'move';
  // Firefox refuses to start a drag at all unless something is written here.
  try { e.dataTransfer.setData('text/plain', card.dataset.name); } catch (_) {}
}

function dragOver(e) {
  if (!dragging) return;
  e.preventDefault();
  const over = e.target.closest('.card.bot');
  if (!over || over === dragging) return;

  // Insert before or after depending on which half of the card the cursor is in, so the gap follows the
  // pointer instead of the card jumping only once you pass the far edge.
  const box = over.getBoundingClientRect();
  const after = (e.clientX - box.left) > box.width / 2;
  over.parentNode.insertBefore(dragging, after ? over.nextSibling : over);
}

async function dragEnd() {
  if (!dragging) return;
  dragging.classList.remove('dragging');
  dragging = null;

  // Merge with what is already saved, rather than sending only what is on screen.
  //
  // The cards rendered are the FILTERED set. Dragging one while a search or a status filter was active used to
  // POST just the visible names, and the server takes that list as the whole order - so every account not
  // matching the filter silently lost its place and fell to the end, alphabetically. Searching for one account
  // and nudging it was enough to scramble the arrangement of all the others.
  const visible = [...document.querySelectorAll('#bots .card.bot')].map((c) => c.dataset.name);
  const known = (state && state.Bots ? state.Bots : []).map((b) => b.Name);   // the order as it stands, from the server
  const shown = new Set(visible);

  // The hidden accounts stay exactly where they were, and the shown ones fill their own places in the new order.
  // Putting every hidden account after every shown one moved them: [a, b, c] searched to b and c, c dragged ahead of
  // b, came back [c, b, a] - a went from first to last. (Sorted by the order loaded at start, a second drag used an
  // old one as well.)
  let k = 0;
  const order = known.map((n) => (shown.has(n) ? visible[k++] : n));
  visible.slice(k).forEach((n) => order.push(n));
  const res = await post('/api/accounts/order', order);
  if (!res.ok) toast(res.error || t("Couldn't save that order"), true);
  refresh();
}

// Every one of these used to ignore the reply, so a failure looked exactly like a success.
async function act(name, action) {
  const res = await post(`/api/bots/${encodeURIComponent(name)}/${action}`);
  if (!res.ok) toast(res.error || t("That didn't work"), true);
  refresh();
}

async function openBot(name) {
  const data = await api(`/api/bots/${encodeURIComponent(name)}/cards`);
  const bot = state.Bots.find((b) => b.Name === name);
  modal(`
    <h2>${esc(tf('{0} — trading cards', name))}</h2>
    <p class="muted small">${esc(data.Status || '')}</p>
    ${data.Games.length ? `<div class="tablewrap"><table>
      <tr><th>${esc(t('Cards'))}</th><th data-tip="${esc(t("How long this account has played this game. Steam drops nothing until it passes the threshold in this account's settings."))}">${esc(t('Hours'))}</th><th>${esc(t('Game'))}</th></tr>
      ${data.Games.map((g) => `<tr><td>${g.Cards}</td><td>${g.Hours}</td><td>${esc(g.Name)}</td></tr>`).join('')}
      </table></div>` : `<p class="muted">${esc(t('Nothing left to farm on this account.'))}</p>`}
    <div class="actions">
      <button class="ghost" data-act="settings" data-bot="${esc(name)}">${esc(t('Settings'))}</button>
      <button class="ghost" onclick="closeModal()">${esc(t('Close'))}</button>
      <span class="spacer"></span>
      <button class="danger" data-act="remove" data-bot="${esc(name)}">${esc(t('Remove'))}</button>
    </div>`);
}

function showAddAccount() {
  modal(`
    <h2>${esc(t('Add a Steam account'))}</h2>
    <div class="form2">
      <label for="a-name">${esc(t('Name'))}${tipIcon(t("A nickname just for you. It names the config file and it's what you type in commands - it doesn't have to match anything on Steam."))}</label>
      <input id="a-name" type="text" placeholder="mybot" autocomplete="off">
      <label for="a-login">${esc(t('Steam account name'))}${tipIcon(t("What you type into Steam's sign-in box. Not your display name, not your email."))}</label>
      <input id="a-login" type="text" autocomplete="off">
      <label for="a-pass">${esc(t('Password'))}${tipIcon(t('Optional. Leave it blank and nocat.farm asks once, then remembers the account with a login token instead - which is safer than a password in a file.'))}</label>
      <input id="a-pass" type="password" placeholder="${esc(t("leave blank and it'll ask"))}" autocomplete="off">
      <label for="a-qr">${esc(t('Sign in with a QR code'))}${tipIcon(t("Scan a code with the Steam app on your phone instead of typing a password - the account name comes from Steam, so both boxes above can stay empty."))}</label>
      <input id="a-qr" type="checkbox" onchange="['a-login','a-pass'].forEach((id) => { $(id).disabled = this.checked; })">
    </div>
    <p style="margin:14px 0 8px"><b>${esc(t('What kind of account is it?'))}</b></p>
    <div class="pickcards">
      <div class="pickcard" id="a-type-human" onclick="addPickType(true)">
        <b>${esc(t('My main - I play on it'))}</b>
        <span>${esc(t("Human mode: it keeps a believable day - sleeps, takes breaks, plays one game at a time and waits a person's while before it trades or replies. Slower, but nothing about it looks automated."))}</span>
      </div>
      <div class="pickcard" id="a-type-robot" onclick="addPickType(false)">
        <b>${esc(t('A spare or farm account'))}</b>
        <span>${esc(t('Robot mode: farms cards and idles games around the clock at full speed. Best for accounts nobody looks at.'))}</span>
      </div>
    </div>
    <p id="addError" class="error"></p>
    <div class="actions"><button onclick="createBot()">${esc(t('Add account'))}</button><button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button></div>`);
  addType = null;
  setTimeout(() => $('a-name').focus(), 50);
}

// Human or robot is always asked - a small unticked box made every account a robot by accident.
let addType = null;
function addPickType(human) {
  addType = human;
  $('a-type-human').classList.toggle('on', human);
  $('a-type-robot').classList.toggle('on', !human);
}

async function createBot() {
  if (addType === null) { $('addError').textContent = t('Pick one first'); return; }
  const res = await post('/api/bots', { Name: $('a-name').value.trim(), SteamLogin: $('a-login').value.trim(), Password: $('a-pass').value, Qr: $('a-qr').checked, Human: addType });
  if (!res.ok) { $('addError').textContent = res.error; return; }
  closeModal();
  refresh();
}

// ── importing from another idler ─────────────────────────────────────
// Retyping five accounts by hand is the reason people don't switch tools. So: pick the program they're in now (or a
// folder, and it works out which), see exactly what was found and where it looked, tick what to bring, and only then
// is anything written. The other program's files are only ever read. Passwords and tokens never come to the browser -
// the preview only says whether each is there, and importing reads the files again on the server.
let imp = null;   // { tutorial, tools, tool, path, scan, off, human, signin, logins, settingsOff }

function impReset(tutorial) {
  imp = { tutorial: !!tutorial, tools: null, tool: '', path: '', scan: null,
    off: new Set(), human: new Set(), signin: new Set(), logins: {}, settingsOff: new Set() };
}

/// The standalone dialog: from the Accounts page, Settings, the welcome banner, or the installer's "coming from".
async function openImport(tool, path) {
  impReset(false);
  modal(`<h2>${esc(t('Import from another idler'))}</h2><p class="muted">${esc(t('Looking…'))}</p>`);
  if (tool) await impScan(tool, path); else await impLoadTools();
  renderImport();
}

async function impLoadTools() {
  imp.tools = await api('/api/import/tools').catch(() => null);
}

async function impScan(tool, path, keep) {
  // Looked at again in a new language (keep): the ticks, sign-ins and typed names stay as they were.
  const kept = keep ? { off: imp.off, signin: imp.signin, logins: imp.logins, settingsOff: imp.settingsOff } : null;
  imp.tool = tool;
  imp.path = path || '';
  imp.scan = await api('/api/import/scan?tool=' + encodeURIComponent(tool) + (path ? '&path=' + encodeURIComponent(path) : '')).catch(() => null);
  imp.off = new Set();
  imp.signin = new Set();
  imp.logins = {};
  imp.settingsOff = new Set();
  const accts = (imp.scan && imp.scan.Accounts) || [];
  // Ones already here are left alone anyway, so they start unticked rather than looking like they'd be replaced.
  accts.forEach((a) => { if (a.Exists) imp.off.add(a.Key); });
  if (kept) {
    const keys = new Set(accts.map((a) => a.Key));
    imp.off = new Set([...kept.off].filter((k) => keys.has(k)));
    imp.signin = new Set([...kept.signin].filter((k) => keys.has(k)));
    imp.logins = Object.fromEntries(Object.entries(kept.logins).filter(([k]) => keys.has(k)));
    imp.settingsOff = kept.settingsOff;
  }
  // The walkthrough already asked "your main or a spare?" - for a single account, that is the answer.
  if (imp.tutorial && tutorialHuman === true && accts.length === 1) imp.human.add(accts[0].Key);
}

/// Redraw - from the top when it's a different screen (the list, a preview) rather than the same one again.
function renderImport(fresh) {
  if (!imp) return;
  if (imp.tutorial) renderTutorial(); else renderImportDialog();
  if (fresh && $('modalCard')) $('modalCard').scrollTop = 0;
  if (fresh && $('tutStage')) $('tutStage').scrollTop = 0;
}

function renderImportDialog() {
  const s = imp.scan;
  const ready = s && s.Found && ((s.Accounts || []).length || (s.Settings || []).length);
  modal(`<h2>${esc(s && s.Found ? tf('Import from {0}', s.ToolName) : t('Import from another idler'))}</h2>
    ${s ? importPreviewHtml() : importToolsHtml()}
    <p id="importError" class="error"></p>
    <div class="actions">
      ${s ? `<button class="ghost" onclick="impBack()">${esc(t('Back'))}</button>` : ''}
      <button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button>
      ${ready ? `<button id="impGo" onclick="impApply()">${esc(t('Import'))}</button>` : ''}
    </div>`);
}

/// Back from a preview to the list of programs.
async function impBack() {
  imp.scan = null;
  imp.tool = '';
  if (!imp.tools) await impLoadTools();
  renderImport(true);
}

async function impPick(tool) {
  await impScan(tool, '');
  renderImport(true);
}

/// The example in the folder box, in the shape of the machine nocat.farm runs on (Windows, or a Mac/Linux/Docker path).
function folderHint() { return state && state.CanSelfUpdate === false ? '/path/to/folder' : 'C:\\...\\folder'; }

/// "Pick a folder", or "look in this folder" on a preview that found nothing: the path box decides.
async function impPickFolder(tool) {
  const el = $('impPath');
  const path = el ? el.value.trim() : '';
  if (!path) { toast(t('Type or paste the folder first'), true); return; }
  await impScan(tool || 'auto', path);
  renderImport(true);
}

function impToggle(key, what, on) {
  const set = imp[what];
  // No redraw: nothing else on screen depends on it, and redrawing would drop a half-typed account name.
  if (on) set.add(key); else set.delete(key);
}

function impSetting(index, on) {
  if (on) imp.settingsOff.delete(index); else imp.settingsOff.add(index);
}

/// The programs it knows, each with what a look at its usual places found.
function importToolsHtml() {
  const tools = (imp.tools && imp.tools.Tools) || [];
  const found = (tl) => {
    if (!tl.Found) return t('Not in its usual places - pick its folder below if you have it somewhere else.');
    const what = tl.Accounts === 1 ? t('one account') : tl.Accounts ? tf('{0} accounts', tl.Accounts) : tf('{0} setting(s)', tl.Settings);
    return tf('Found at {0} - {1}.', tl.Path, what);
  };

  return `<p class="muted small">${esc(t('Pick the program your accounts are in now. nocat.farm only reads its files - it never changes them.'))}</p>
    <div class="pickcards">
      ${tools.map((tl) => `<div class="pickcard ${tl.Found ? 'on' : ''}" data-tool="${esc(tl.Id)}" onclick="impPick(this.dataset.tool)">
        <b>${esc(tl.Name)}</b><span>${esc(found(tl))}</span></div>`).join('')}
    </div>
    <p class="muted small">${esc(t('Somewhere else? Paste the folder the idler is in and nocat.farm works out which one it is.'))}</p>
    <div class="impfolder"><input id="impPath" type="text" placeholder="${esc(folderHint())}" autocomplete="off" onkeydown="if(event.key==='Enter')impPickFolder('auto')">
      <button class="ghost" onclick="impPickFolder('auto')">${esc(t('Look here'))}</button></div>`;
}

/// One look's result: where it looked, what it found, and the choices - per account, and for the settings.
function importPreviewHtml() {
  const s = imp.scan;
  const looked = s && (s.Looked || []).length
    ? `<details class="implooked muted small"><summary>${esc(t('Where it looked'))}</summary>
        <ul>${s.Looked.map((p) => `<li><code>${esc(p)}</code></li>`).join('')}</ul></details>`
    : '';
  const notes = (list) => (list || []).map((n) => `<p class="muted small">${esc(n)}</p>`).join('');
  const retry = `<div class="impfolder"><input id="impPath" type="text" placeholder="${esc(folderHint())}" autocomplete="off" value="${esc(imp.path || '')}"
      onkeydown="if(event.key==='Enter')impPickFolder(${esc(JSON.stringify(imp.tool || 'auto'))})">
      <button class="ghost" onclick="impPickFolder(${esc(JSON.stringify(imp.tool || 'auto'))})">${esc(t('Look here'))}</button></div>`;

  if (!s) return `<p class="error">${esc(t("Couldn't look there."))}</p>`;

  if (!s.Found) {
    return `<p>${esc(s.ToolName ? tf('No {0} files were found.', s.ToolName) : t('Nothing nocat.farm can import was found there.'))}</p>
      ${looked}${notes(s.Notes)}
      <p class="muted small">${esc(t('If it is somewhere else, paste its folder:'))}</p>${retry}`;
  }

  const accts = s.Accounts || [];
  const sets = s.Settings || [];
  let html = `<p>${tf('Found {0} at {1}.', `<b>${esc(s.ToolName)}</b>`, `<code>${esc(s.Path)}</code>`)}</p>${looked}`;

  if (accts.length) {
    html += `<p class="muted small">${esc(t('Tick the accounts to bring over. Tick human mode on the ones you play on yourself - the rest farm at full speed.'))}</p>
      <div class="improws">${accts.map((a) => importAccountHtml(a)).join('')}</div>`;
  }

  if (sets.length) {
    html += `<p class="muted small" style="margin-bottom:4px">${esc(t('Settings it brings too:'))}</p>
      <div class="improws">${sets.map((x) => `<label class="improw"><span class="impmain">
        <input type="checkbox" class="impon" ${imp.settingsOff.has(x.Index) ? '' : 'checked'} onchange="impSetting(${x.Index}, this.checked)">
        <span><b>${esc(tSetting({ Name: x.Name, Label: x.Label }, 'label'))}</b> <span class="muted">${esc(x.Value)}</span>
        ${x.EveryAccount ? `<br><span class="muted small">${esc(t('onto every account already here'))}</span>` : ''}</span></span></label>`).join('')}</div>`;
  }

  if (!accts.length && !sets.length) html += `<p>${esc(t('There is nothing in it to bring over.'))}</p>`;

  html += notes(s.Notes);

  if (accts.length) {
    html += `<div class="alert warn" style="display:block;margin-top:10px">${esc(tf('Close {0} before these accounts start here - two programs on one Steam account keep signing each other out.', s.ToolName))}</div>`;
  }

  return html;
}

function importAccountHtml(a) {
  const key = esc(a.Key);
  const signin = a.HasToken ? `<span class="pill good">${esc(t('token — no password needed'))}</span>`
    : a.HasPassword ? `<span class="pill warn">${esc(t('password'))}</span>`
    : a.CanBringSignIn ? `<span class="pill">${esc(t('sign-in can come over'))}</span>`
    : `<span class="pill">${esc(t('will ask on first login'))}</span>`;
  const auth = a.HasAuthenticator ? ` <span class="pill good">${esc(t('authenticator'))}</span>` : '';

  return `<div class="improw">
    <label class="impmain"><input type="checkbox" class="impon" data-key="${key}" ${imp.off.has(a.Key) ? '' : 'checked'}
      onchange="impToggle(this.dataset.key, 'off', !this.checked)">
      <span><b>${esc(a.Name)}</b>${a.SteamLogin && a.SteamLogin !== a.Name ? ` <span class="muted">- ${esc(a.SteamLogin)}</span>` : ''}
      ${a.Exists ? ` <span class="muted small">${esc(t('(already here - left alone)'))}</span>` : ''}</span></label>
    <div class="small">${signin}${auth}</div>
    ${(a.Brings || []).length ? `<div class="muted small">${esc(a.Brings.join(' · '))}</div>` : ''}
    ${a.SteamLogin ? '' : `<input type="text" data-key="${key}" placeholder="${esc(t('Steam account name - or leave empty for a QR code'))}" autocomplete="off"
      value="${esc(imp.logins[a.Key] || '')}" oninput="imp.logins[this.dataset.key] = this.value">`}
    <div class="impopts">
      <label class="inline"><input type="checkbox" data-key="${key}" ${imp.human.has(a.Key) ? 'checked' : ''}
        onchange="impToggle(this.dataset.key, 'human', this.checked)"> ${esc(t('Human mode'))}</label>
      ${a.CanBringSignIn ? `<label class="inline"><input type="checkbox" data-key="${key}" ${imp.signin.has(a.Key) ? 'checked' : ''}
        onchange="impToggle(this.dataset.key, 'signin', this.checked)"> ${esc(t('Bring its sign-in over'))}${tipIcon(t("Reads this account's saved sign-in from Windows, where the other idler keeps it, so it signs in with no password. Left unticked, it asks for the password (or a QR code) instead."))}</label>` : ''}
    </div>
    ${(a.Notes || []).map((n) => `<div class="muted small">${esc(n)}</div>`).join('')}
  </div>`;
}

/// Write what's ticked. The server reads the files again - the browser only says which accounts and choices.
async function impApply() {
  const s = imp && imp.scan;
  if (!s || !s.Found) { toast(t('Pick one first'), true); return; }

  const accounts = (s.Accounts || []).filter((a) => !imp.off.has(a.Key)).map((a) => ({
    Key: a.Key, Human: imp.human.has(a.Key), SignIn: imp.signin.has(a.Key), SteamLogin: (imp.logins[a.Key] || '').trim(),
  }));
  const settings = (s.Settings || []).filter((x) => !imp.settingsOff.has(x.Index)).map((x) => x.Index);
  if (!accounts.length && !settings.length) { toast(t('Nothing is ticked'), true); return; }

  const btn = $('impGo') || $('tutNext');
  const label = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = t('Importing…'); }

  const res = await post('/api/import/apply', { Tool: s.Tool, Path: s.Path, Accounts: accounts, Settings: settings }).catch(() => null);

  if (!res || !res.ok) {
    const err = $('importError') || $('tutError');
    const why = (res && res.error) || t('Import failed');
    if (err) err.textContent = why; else toast(why, true);
    if (btn) { btn.disabled = false; btn.textContent = label; }
    return;
  }

  const tutorial = imp.tutorial;
  const toolName = s.ToolName;
  imp = null;

  if (tutorial) { await tutorialAfterImport(res, toolName); return; }

  closeModal();
  await afterImport(res);
}

// The welcome screen says so before anybody types anything: the first idler found with accounts in it.
async function checkForIdlers() {
  const tools = await api('/api/import/tools').catch(() => null);
  const found = tools && (tools.Tools || []).find((tl) => tl.Found && tl.Accounts > 0);
  if (!found) return;

  const n = found.Accounts;
  $('importBanner').classList.remove('hidden');
  $('importBanner').innerHTML = `
    <div class="alert accent" style="margin-bottom:18px;display:block">
      <b>${esc(n === 1 ? tf('Found {0} with one account.', found.Name) : tf('Found {0} with {1} accounts.', found.Name, n))}</b>
      <div class="muted small" style="margin:4px 0 10px">
        ${esc(found.Path)}<br>
        ${esc(found.Tokens
          ? tf('{0} of them can come across with their saved logins — no passwords, no Steam Guard codes.', found.Tokens)
          : t('You will still need to sign in to each one once.'))}
      </div>
      <button data-tool="${esc(found.Id)}" onclick="openImport(this.dataset.tool)">${esc(n === 1 ? t('Import one account') : tf('Import {0} accounts', n))}</button>
    </div>`;
}

/// After a successful import, from the import dialog or the walkthrough: say what came across and offer to start it.
async function afterImport(res) {
  $('importBanner').classList.add('hidden');
  sessionStorage.setItem('skip-welcome', '1');
  // Settings only (SingleBoostr), or every account already here: nothing new to start.
  const done = !res.Imported ? (res.Skipped ? t('Nothing was imported') : t('Settings brought across'))
    : res.Imported === 1 ? t('Imported one account') : tf('Imported {0} accounts', res.Imported);
  toast(done);
  await loadConfig();
  await refresh();
  go('accounts');

  if (res.Notes && res.Notes.length) {
    modal(`<h2>${esc(done)}</h2>
      <div class="rows">${res.Notes.map((n) => `<div class="row"><span>${esc(n)}</span></div>`).join('')}</div>
      ${res.Imported ? `<p class="muted small" style="margin-top:12px">${t("They're added but not started. Press Start on each, or run {0}.").replace('{0}', '<code>start all</code>')}</p>
      <div class="actions"><button onclick="closeModal();run('start all')">${esc(t('Start them all'))}</button><button class="ghost" onclick="closeModal()">${esc(t('Not yet'))}</button></div>`
      : `<div class="actions"><button onclick="closeModal()">${esc(t('Close'))}</button></div>`}`);
  }
}

// The welcome form asks human or robot too - it's the big button on a fresh install, and sent nothing, so every
// account added there became a robot.
let welcomeType = null;
function welcomePickType(human) {
  welcomeType = human;
  $('w-type-human').classList.toggle('on', human);
  $('w-type-robot').classList.toggle('on', !human);
}

async function createFirstBot() {
  if (welcomeType === null) { $('welcomeError').textContent = t('Pick one first'); return; }
  const res = await post('/api/bots', { Name: $('w-name').value.trim(), SteamLogin: $('w-login').value.trim(), Password: $('w-pass').value, Human: welcomeType });
  if (!res.ok) { $('welcomeError').textContent = res.error; return; }
  sessionStorage.setItem('skip-welcome', '1');
  await refresh();
  go('accounts');
}

// The name is compared via a data-attribute, NOT by interpolating JSON.stringify into the handler: that
// emits the name WITH double quotes, which closes the HTML attribute early and silently breaks the
// handler - the button then never enables however correctly you type.
// A browser confirm() is one click, and this deletes the stored login token - so getting the account back
// means the password and a fresh Steam Guard code, not just re-adding a name. Typing the account NAME rather
// than a fixed word is deliberate: with several accounts configured, the thing you can get wrong is WHICH one,
// and a fixed word would not catch that.
function removeBot(name) {
  modal(`
    <h2>${esc(tf('Remove {0}?', name))}</h2>
    <p>${tf('This deletes {0} and the stored login token for it.', `<code>config/${esc(name)}.json</code>`)}</p>
    <ul class="muted small">
      <li>${esc(t('Nothing happens to the Steam account itself - it is only removed from nocat.farm.'))}</li>
      <li>${esc(t('Adding it back needs the password and a fresh Steam Guard code.'))}</li>
      <li>${esc(t('Anything it was farming stops.'))}</li>
    </ul>
    <p class="small">${tf('Type {0} to enable the button.', `<code>${esc(name)}</code>`)}</p>
    <input type="text" id="removeConfirm" autocomplete="off" spellcheck="false" placeholder="${esc(t('type the account name'))}"
           data-expect="${esc(name)}"
           oninput="$('removeGo').disabled = this.value.trim() !== this.dataset.expect">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button>
      <button class="danger" id="removeGo" disabled data-bot="${esc(name)}" onclick="doRemoveBot(this.dataset.bot)">${esc(t('Remove it'))}</button>
    </div>`);
  setTimeout(() => { const el = $('removeConfirm'); if (el) el.focus(); }, 30);
}

async function doRemoveBot(name) {
  const res = await del('/api/bots/' + encodeURIComponent(name));
  if (!res.ok) { toast(res.error || t('Could not remove that account'), true); return; }
  toast(tf('Removed {0}', name));
  if (settingsTarget === name) { settingsTarget = GLOBAL; pending = {}; }
  closeModal();
  await loadConfig();
  if (view === 'settings') renderSettings();
  refresh();
}

// ── authenticator ─────────────────────────────────────────────────────────
// The Steam app's two jobs, per account: the sign-in code, and the list of things waiting to be confirmed - trades,
// market listings, account changes - each with Confirm and Deny. The code is worked out here every second from
// what the server sent (the code and how long it has left), so the ring moves smoothly without asking the server.
let authAccounts = null;      // [{Name, HasCode, CanConfirm, Code, SecondsLeft, At}]
let authPick = null;          // the account on screen
let authConfs = null;         // the last confirmations answer for authPick
let authSelected = new Set();
let authTimer = null, authListTimer = null;
let authBusy = false;

async function openAuth() {
  await loadAuth();
  clearInterval(authTimer);
  authTimer = setInterval(tickAuth, 1000);
  clearInterval(authListTimer);
  // Not while the tab is hidden, as the main poll isn't: each look is a real request to Steam, and a tab left open in
  // the background asked about 2,900 times a day.
  authListTimer = setInterval(() => { if (!authBusy && !document.hidden) loadConfirmations(false); }, 30000);
}

function closeAuth() {
  clearInterval(authTimer); authTimer = null;
  clearInterval(authListTimer); authListTimer = null;
}

async function loadAuth() {
  try {
    const r = await api('/api/auth');
    const now = Date.now();
    authAccounts = (r.Accounts || []).map((a) => ({ ...a, At: now }));
  } catch (e) {
    authAccounts = [];
  }
  if (!authPick || !authAccounts.some((a) => a.Name === authPick)) {
    const first = authAccounts.find((a) => a.CanConfirm) || authAccounts.find((a) => a.HasCode) || authAccounts[0];
    authPick = first ? first.Name : null;
  }
  renderAuth();
  loadConfirmations(true);
}

function authPickAccount(name) {
  authPick = name;
  authConfs = null;
  authSelected = new Set();
  renderAuth();
  loadConfirmations(true);
}

async function loadConfirmations(show) {
  const a = (authAccounts || []).find((x) => x.Name === authPick);
  if (!a || !a.CanConfirm) return;
  if (show) { authConfs = { Loading: true }; renderAuthList(); }
  // Whose list this is. Reading one takes a while (each trade's offer is fetched too), and another account picked
  // meanwhile got this one's list under its name - Confirm then only said "already gone".
  const who = authPick;
  let got;
  try {
    got = await api('/api/auth/' + encodeURIComponent(who) + '/confirmations');
  } catch (e) {
    got = { Ok: false, Error: e.message || String(e) };
  }
  if (authPick !== who) return;
  authConfs = got;
  const live = new Set(((authConfs && authConfs.Items) || []).map((c) => String(c.Id)));
  authSelected = new Set([...authSelected].filter((id) => live.has(id)));
  const nav = $('navAuth');
  if (nav) nav.textContent = authConfs && authConfs.Items && authConfs.Items.length ? String(authConfs.Items.length) : '';
  renderAuthList();
}

// The code, re-counted every second; a new one is fetched as the old one runs out.
function tickAuth() {
  if (view !== 'auth' || !authAccounts) return;
  let stale = false;
  for (const a of authAccounts) {
    if (!a.HasCode) continue;
    const left = a.SecondsLeft - Math.floor((Date.now() - a.At) / 1000);
    if (left <= 0) stale = true;
  }
  if (stale) { loadAuthCodes(); return; }
  renderAuthCode();
}

async function loadAuthCodes() {
  try {
    const r = await api('/api/auth');
    const now = Date.now();
    authAccounts = (r.Accounts || []).map((a) => ({ ...a, At: now }));
  } catch (e) { /* the next tick tries again */ }
  renderAuthCode();
}

function renderAuth() {
  const body = $('authBody');
  if (!body) return;
  const accts = authAccounts || [];

  if (!accts.length) {
    body.innerHTML = `<div class="card"><h2>${esc(t('Authenticator'))}</h2><p class="muted">${esc(t('No accounts yet.'))}</p></div>`;
    return;
  }

  body.innerHTML = `
    <div class="card auth-card">
      <div class="auth-top">
        <h2>${esc(t('Authenticator'))}</h2>
        <div class="langpick auth-accounts">${accts.map((a) =>
          `<span class="p ${a.Name === authPick ? 'on' : ''} ${a.HasCode ? '' : 'dim'}" data-auth-pick="${esc(a.Name)}">${esc(a.Name)}</span>`).join('')}</div>
      </div>
      <div id="authCode"></div>
    </div>
    <div class="card auth-card">
      <div class="auth-listhead">
        <h2>${esc(t('Confirmations'))}</h2>
        <div class="auth-tools" id="authTools"></div>
      </div>
      <div id="authList"></div>
      <div id="authRules"></div>
    </div>`;
  renderAuthCode();
  renderAuthList();
}

function renderAuthCode() {
  const box = $('authCode');
  if (!box) return;
  const a = (authAccounts || []).find((x) => x.Name === authPick);

  if (!a) { box.innerHTML = ''; return; }
  if (!a.HasCode) {
    box.innerHTML = `<p class="muted auth-none">${tf('{0} has no authenticator in nocat.farm. Put its maFile in {1} (or paste its secrets in the account settings) and its codes and confirmations show up here.',
      esc(a.Name), '<code>config/authenticators</code>')}</p>`;
    return;
  }

  const left = Math.max(0, a.SecondsLeft - Math.floor((Date.now() - a.At) / 1000));
  const r = 26, c = 2 * Math.PI * r;
  const dash = (c * left / 30).toFixed(2);

  // The same code on screen: only the ring and its number move. Rebuilt every second, the Copy button under the
  // pointer was replaced mid-click and the digits couldn't stay selected long enough to copy by hand.
  const shown = box.querySelector('.auth-code');
  if (shown && shown.dataset.for === a.Name && shown.dataset.code === (a.Code || '')) {
    shown.querySelector('.auth-ring').classList.toggle('low', left <= 5);
    shown.querySelector('.auth-ring .left').setAttribute('stroke-dasharray', `${dash} ${c.toFixed(2)}`);
    shown.querySelector('.auth-ring text').textContent = String(left);
    return;
  }

  box.innerHTML = `
    <div class="auth-code" data-for="${esc(a.Name)}" data-code="${esc(a.Code || '')}">
      <svg class="auth-ring ${left <= 5 ? 'low' : ''}" viewBox="0 0 64 64" aria-hidden="true">
        <circle cx="32" cy="32" r="${r}" class="track"></circle>
        <circle cx="32" cy="32" r="${r}" class="left" stroke-dasharray="${dash} ${c.toFixed(2)}" transform="rotate(-90 32 32)"></circle>
        <text x="32" y="37" text-anchor="middle">${left}</text>
      </svg>
      <span class="auth-digits" title="${esc(t('The Steam Guard code for signing in.'))}">${esc(a.Code || '-----')}</span>
      <button class="ghost" onclick="copyAuthCode()">${esc(t('Copy'))}</button>
    </div>`;
}

function copyAuthCode() {
  const a = (authAccounts || []).find((x) => x.Name === authPick);
  if (!a || !a.Code) return;
  copyText(a.Code, t('Code copied'));
}

function authAgo(unix) {
  if (!unix) return '';
  const mins = Math.max(0, Math.round((Date.now() / 1000 - unix) / 60));
  return mins < 1 ? t('just now') : mins < 60 ? tf('{0}m ago', mins) : mins < 1440 ? tf('{0}h ago', Math.floor(mins / 60)) : tf('{0}d ago', Math.floor(mins / 1440));
}

function authItems(list) {
  return (list || []).map((i) =>
    `<span class="auth-item" title="${esc(i.Name + (i.Amount > 1 ? ' x' + i.Amount : ''))}">${i.Icon
      ? `<img src="https://community.cloudflare.steamstatic.com/economy/image/${esc(i.Icon)}/64fx64f" alt="" loading="lazy">`
      : '<i></i>'}${i.Amount > 1 ? `<b>${i.Amount}</b>` : ''}</span>`).join('');
}

function renderAuthList() {
  const box = $('authList'), tools = $('authTools'), rules = $('authRules');
  if (!box) return;
  const a = (authAccounts || []).find((x) => x.Name === authPick);

  if (tools) tools.innerHTML = '';
  if (rules) rules.innerHTML = '';
  if (!a) { box.innerHTML = ''; return; }
  if (!a.CanConfirm) {
    box.innerHTML = `<p class="muted">${esc(t('Confirmations need this account\'s authenticator in nocat.farm (its identity secret). Until then they are confirmed on your phone.'))}</p>`;
    return;
  }

  const d = authConfs;
  if (!d || d.Loading) { box.innerHTML = `<p class="muted">${esc(t('Asking Steam...'))}</p>`; return; }
  if (d.Ok === false) { box.innerHTML = `<p class="muted">${esc(tf('Steam didn\'t give the list: {0}', d.Error || '?'))}</p>`; return; }

  const items = d.Items || [];
  if (tools) {
    const some = authSelected.size > 0;
    tools.innerHTML = items.length ? `
      <button class="ghost" onclick="authSelectAll()">${esc(authSelected.size === items.length ? t('Select none') : t('Select all'))}</button>
      <button ${some ? '' : 'disabled'} onclick="authAct(true, [...authSelected])">${esc(some ? tf('Confirm {0}', authSelected.size) : t('Confirm'))}</button>
      <button class="danger" ${some ? '' : 'disabled'} onclick="authAct(false, [...authSelected])">${esc(some ? tf('Deny {0}', authSelected.size) : t('Deny'))}</button>
      <button class="ghost" onclick="loadConfirmations(true)">${esc(t('Refresh'))}</button>`
      : `<button class="ghost" onclick="loadConfirmations(true)">${esc(t('Refresh'))}</button>`;
  }

  box.innerHTML = !items.length
    ? `<p class="muted auth-empty">${esc(t('Nothing waiting to be confirmed.'))}</p>`
    : items.map((c) => {
      const id = String(c.Id);
      const trade = c.Offer ? `
        <div class="auth-trade">
          <div><span class="muted small">${esc(t('you give'))}</span>${authItems(c.Offer.Give) || `<span class="muted small">${esc(t('no items'))}</span>`}</div>
          <div><span class="muted small">${esc(t('you get'))}</span>${authItems(c.Offer.Get) || `<span class="muted small">${esc(t('no items'))}</span>`}</div>
        </div>` : '';
      return `<div class="auth-conf ${authSelected.has(id) ? 'sel' : ''}">
        <label class="auth-check"><input type="checkbox" ${authSelected.has(id) ? 'checked' : ''} onchange="authToggle('${id}', this.checked)"></label>
        ${c.Icon ? `<img class="auth-icon" src="${esc(c.Icon)}" alt="" loading="lazy">` : '<span class="auth-icon"></span>'}
        <div class="auth-main">
          <div class="auth-line"><span class="chip">${esc(c.TypeName || t('Confirmation'))}</span> <b>${esc(c.Headline || '')}</b> <span class="muted small">${esc(authAgo(c.Created))}</span></div>
          ${(c.Summary || []).map((s) => `<div class="small">${esc(s)}</div>`).join('')}
          ${trade}
        </div>
        <div class="auth-buttons">
          <button onclick="authAct(true, ['${id}'])">${esc(t('Confirm'))}</button>
          <button class="danger" onclick="authAct(false, ['${id}'])">${esc(t('Deny'))}</button>
        </div>
      </div>`;
    }).join('');

  if (rules && d.Rules) {
    rules.innerHTML = `<p class="muted small auth-rules">${d.Rules.length
      ? tf('Confirmed by itself: trades with {0}. Everything else waits here. Change it under Settings, Trades ("Trade by itself with").', d.Rules.map((r) => `<b>${esc(r.Who)}</b> (${esc(t(r.Way))})`).join(', '))
      : esc(t('Nothing is confirmed by itself - everything waits here for you. Change it under Settings, Trades ("Trade by itself with").'))}</p>`;
  }
}

function authToggle(id, on) {
  if (on) authSelected.add(id); else authSelected.delete(id);
  renderAuthList();
}

function authSelectAll() {
  const items = (authConfs && authConfs.Items) || [];
  authSelected = authSelected.size === items.length ? new Set() : new Set(items.map((c) => String(c.Id)));
  renderAuthList();
}

async function authAct(accept, ids) {
  if (!ids.length || authBusy) return;
  authBusy = true;
  try {
    const r = await post('/api/auth/' + encodeURIComponent(authPick) + '/confirmations', { Ids: ids, Accept: accept });
    if (r.ok === false || r.Ok === false) toast(r.error || r.Error || t('Steam didn\'t take that'), true);
    else toast(accept ? tf('Confirmed {0}', r.Done || ids.length) : tf('Denied {0}', r.Done || ids.length));
  } finally {
    authBusy = false;
    authSelected = new Set();
    loadConfirmations(false);
  }
}

// ── plugins ───────────────────────────────────────────────────────────────
// The tab only exists when plugins are switched on, because a page telling you about a feature you have not
// enabled is just another thing to scroll past.
let pluginData = null;

async function loadPlugins() {
  const body = $('pluginsBody');
  if (!body) return;

  try {
    pluginData = await api('/api/plugins');
  } catch (e) {
    body.innerHTML = `<p class="muted">${esc(tf('Could not read the plugin list: {0}', e.message || e))}</p>`;
    return;
  }

  renderPlugins();
}

function renderPlugins() {
  const body = $('pluginsBody');
  const d = pluginData;
  if (!body || !d) return;

  if (!d.Enabled) {
    body.innerHTML = `<p class="explain">${esc(t("Plugins are switched off. A plugin is somebody else's code running inside this app, with access to everything it can reach — including your Steam login tokens. Turn it on only for plugins you wrote or whose author you trust."))}</p>
      <button onclick="goSetting('PluginsEnabled')">${esc(t('Go to the setting'))}</button>`;
    return;
  }

  const rows = (d.Installed || []).map((p) => `
    <div class="pcard">
      <div class="prow">
        <span class="pname"><b>${esc(p.Name)}</b> <span class="muted small">${esc(p.Version)}</span><i class="wid">${esc(p.File)}</i></span>
        <label class="switch" data-tip="${esc(t('Takes effect after a restart — a plugin wires itself up as the app starts.'))}">
          <input type="checkbox" ${p.Enabled ? 'checked' : ''} onchange="togglePlugin(${esc(JSON.stringify(p.Name))}, this.checked)"><span></span>
        </label>
      </div>
      ${pluginSettings(p)}
    </div>`).join('');

  const cmds = (d.Commands || []).map((c) =>
    `<li><code>${esc(c.Verb)} ${esc(c.Usage || '')}</code> <span class="muted">${esc(c.Help || '')}</span></li>`).join('');

  body.innerHTML = `
    ${rows || `<p class="muted empty">${esc(t('Nothing installed yet.'))}</p>`}
    <p class="muted small">${esc(t('Drop a .dll in this folder and restart:'))} <code>${esc(d.Folder)}</code></p>
    ${cmds ? `<h2 style="margin-top:18px" data-t="Commands they added">${esc(t('Commands they added'))}</h2><ul class="unlocks">${cmds}</ul>` : ''}
    <p class="muted small"><a href="https://github.com/VisaHolder/nocatfarm/blob/main/PLUGINS.md" target="_blank" rel="noopener noreferrer">${esc(t('How to write one'))}</a></p>`;
}

// A plugin's own settings, drawn from what it declared. Same controls as everywhere else, so a plugin gets a
// real settings UI without building one and the operator edits it where they edit everything else.
function pluginSettings(p) {
  const list = p.Settings || [];
  if (!list.length) return '';

  const rows = list.map((sett) => {
    const id = `ps-${p.Name}-${sett.Name}`.replace(/[^A-Za-z0-9_-]/g, '_');
    const tip = sett.Help ? ` data-tip="${esc(sett.Help)}"` : '';
    const set = `setPluginSetting(${esc(JSON.stringify(p.Name))},${esc(JSON.stringify(sett.Name))},this)`;
    let ctl;

    if (sett.Kind === 'Bool') {
      ctl = `<label class="switch"><input type="checkbox" id="${id}" ${sett.Value === 'true' ? 'checked' : ''} onchange="${set}"><span></span></label>`;
    } else if (sett.Kind === 'Int') {
      ctl = `<input type="number" id="${id}" value="${esc(sett.Value)}" onchange="${set}">`;
    } else if (sett.Kind === 'Choice') {
      const opts = (sett.Choices || []).map((c) => {
        const sp = String(c).indexOf(' ');
        const v = sp < 0 ? c : String(c).slice(0, sp);
        const lab = sp < 0 ? c : String(c).slice(sp + 1);
        return `<option value="${esc(v)}" ${String(sett.Value) === String(v) ? 'selected' : ''}>${esc(lab)}</option>`;
      }).join('');
      ctl = `<select id="${id}" onchange="${set}">${opts}</select>`;
    } else {
      ctl = `<input type="text" id="${id}" value="${esc(sett.Value)}" onchange="${set}">`;
    }

    return `<div class="field"><label for="${id}"${tip}>${esc(sett.Label || sett.Name)}</label><div class="ctl">${ctl}</div></div>`;
  }).join('');

  return `<div class="psettings">${rows}</div>`;
}

async function setPluginSetting(plugin, name, el) {
  const value = el.type === 'checkbox' ? String(el.checked) : String(el.value);

  try {
    // What the server said, not a "saved" regardless - a setting the plugin no longer has used to report success.
    const r = await api('/api/plugins/setting', { method: 'POST', body: JSON.stringify({ Plugin: plugin, Name: name, Value: value }) });
    if (r && r.Ok) toast(tf('{0} saved', name));
    else toast(tf('Could not save that: {0}', (r && r.Message) || '?'), true);
  } catch (e) {
    toast(tf('Could not save that: {0}', e.message || e), true);
  }
}

async function togglePlugin(name, enabled) {
  try {
    const r = await api('/api/plugins/toggle', { method: 'POST', body: JSON.stringify({ Name: name, Enabled: enabled }) });
    toast(r && r.Message ? r.Message : '');
    await loadPlugins();
  } catch (e) {
    toast(tf('Could not change that: {0}', e.message || e), true);
  }
}

// ── updating ──────────────────────────────────────────────────────────────
// Asked for, never automatic. The confirm exists because this restarts the app: accounts drop off Steam for a
// few seconds and anything mid-session stops there. Everything that matters - accounts, tokens, settings,
// logs - lives in config/ and is never part of the archive, so it survives untouched.
function askUpdate() {
  const to = (state && state.UpdateAvailable) || '';
  // "When I say update" set to wait: the button queues it rather than installing now, and the dialog says so - it used
  // to promise a download and restart that then didn't happen until the night.
  const waits = !!(state && state.UpdateWaits);
  modal(`
    <h2>${esc(tf('Update to {0}?', to))}</h2>
    <p>${waits
      ? tf("It installs {0} once your accounts are asleep - no human-mode account awake, nobody playing, no trade or gift waiting. Then it closes, swaps itself over and starts back up.", `<b>${esc(to)}</b>`)
      : tf('It downloads {0}, closes, swaps itself over and starts back up. Takes about a minute.', `<b>${esc(to)}</b>`)}</p>
    <ul class="muted small">
      <li>${esc(t('Your accounts, login tokens, settings and logs are left exactly as they are.'))}</li>
      <li>${esc(t('Every account signs out of Steam for a few seconds while it restarts.'))}</li>
      <li>${esc(t('Anything mid-session - a farm, a grind - stops there and picks up after.'))}</li>
      <li>${esc(t('If the download fails, nothing is changed and the current version keeps running.'))}</li>
    </ul>
    <div class="actions">
      <button class="ghost" onclick="closeModal()">${esc(t('Not now'))}</button>
      <button id="updateGo" onclick="doUpdate(true)">${esc(waits ? t("Install when they're asleep") : t('Download and restart'))}</button>
    </div>`);
}

async function doUpdate(confirmed) {
  if (!confirmed) { askUpdate(); return; }

  closeModal();
  const btn = $('updateBtn');
  if (btn) { btn.disabled = true; btn.textContent = t('working…'); }

  try {
    const r = await api('/api/update', { method: 'POST' });
    // Red when it didn't work: a failed download came up looking just like "Downloading".
    const failed = !!(r && r.Ok === false);
    toast(r && r.Message ? r.Message : t('Downloading. It restarts by itself when it lands.'), failed);
    if (btn && ((state && state.UpdateWaits) || failed)) btn.disabled = false;   // only queued, or nothing is downloading
  } catch (e) {
    toast(tf('Update failed: {0}', e.message || e), true);
    if (btn) btn.disabled = false;
  }
}

// ── theme ─────────────────────────────────────────────────────────────────
// Applied to <html> rather than <body>, and read back in a tiny inline script in index.html that runs before
// the stylesheet paints - otherwise a light-theme user gets a full dark flash on every single load.
function toggleTheme() {
  const light = document.documentElement.getAttribute('data-theme') === 'light';
  setTheme(light ? 'dark' : 'light');
}

/// The toggle names the theme it switches TO. Translated, and redrawn when the language changes (translateChrome).
function themeLabel() {
  return document.documentElement.getAttribute('data-theme') === 'light' ? t('dark') : t('light');
}

function setTheme(name, save) {
  document.documentElement.setAttribute('data-theme', name);
  try { localStorage.setItem('nocatfarm-theme', name); } catch (_) { /* private mode - it just will not stick */ }
  const btn = $('themeToggle');
  if (btn) btn.textContent = themeLabel();

  // Also stored server-side, so the 'theme' command and this toggle cannot disagree, and so the choice
  // follows you to another browser. localStorage stays the fast path that paints before the first request.
  if (save !== false) {
    if (config && config.Global) config.Global.Theme = name;
    post('/api/theme', { Theme: name }).catch(() => {});
  }
}

// ── getting started ───────────────────────────────────────────────────────
// Shown once, on a machine that has no accounts yet. NOT shown to anybody already running accounts, however
// this flag ended up unset - somebody mid-flight does not need a walkthrough thrown in front of their farm,
// and an upgrade that suddenly blocked the dashboard would be the worst possible first impression of a new
// version. Replayable from the Overview page whenever you want it.
let tutorialStep = 0;

// Open right now - so Escape or a click outside counts as Skip, not as "close and show it again next time".
let tutorialOpen = false;

// Another idler was found (or picked), but they would rather type an account in than import.
let tutorialManual = false;

// 'easy' asks a couple of plain questions on the way to the account; 'advanced' asks about everything - the phone,
// opening it from anywhere, notifications, the update window - and ends on all of the new account's settings.
// Easy is the default: somebody new wants their account running, not a lecture.
let tutorialMode = 'easy';

// What the account is for: true = your main, human mode; false = a spare, robot defaults; null = not picked yet.
let tutorialHuman = null;

// Human mode, and you also sign into it from your own Steam client.
let tutorialSelf = false;

// The dashboard's own steps - phone, from anywhere, password, notifications, updates. What was picked on the way,
// and the last look at /api/phone. Each is saved as its step is left, through the same config POST the settings
// page uses, so leaving halfway keeps everything already answered. The password is only ever what was typed
// here, and only until it's saved: a saved one never comes back to the browser.
let tutDash = null;

// Which screen was drawn last and which way the walkthrough is moving, for the slide between screens; and how many
// steps the bar had, so the sign-in and set-up screens after it can show it finished.
let tutLastKey = '';
let tutDir = 1;
let tutBarSteps = 1;

/// The account-type step: remembers the choice, and for a one-account import ticks that account to match.
function tutorialPickType(human) {
  tutorialHuman = human;
  const accts = (imp && imp.scan && imp.scan.Accounts) || [];
  if (imp) imp.human = new Set(human && accts.length === 1 ? [accts[0].Key] : []);
  renderTutorial();
}

// Kept here rather than read from the settings schema: the tutorial runs before the schema is needed, and this
// list is what the picker shows. It must stay in step with the Language setting's choices in Settings.cs.
const LANGUAGES = [
  { code: 'en', name: 'English' }, { code: 'es', name: 'Español' }, { code: 'pt-BR', name: 'Português (BR)' },
  { code: 'ru', name: 'Русский' }, { code: 'de', name: 'Deutsch' }, { code: 'fr', name: 'Français' },
  { code: 'zh-CN', name: '简体中文' }, { code: 'tr', name: 'Türkçe' }, { code: 'pl', name: 'Polski' },
  { code: 'ja', name: '日本語' }, { code: 'ko', name: '한국어' },
];

/// Chosen from the tutorial: saved, loaded and applied at once, so the very next step is already translated.
async function pickTutorialLanguage(code) {
  // Read again first, like every other save: the copy from when the page opened would put back what changed since.
  await loadConfig().catch(() => {});
  if (config && config.Global) {
    config.Global.Language = code;
    await postGlobal(config.Global).catch(() => {});
  }

  await loadLanguage(code);
  translateChrome();

  // The welcome screen's import banner is built in script, not tagged for translateChrome - redraw it. And what an
  // import found is worded by the server, in the language it had then - look again for the new one.
  if (!$('importBanner').classList.contains('hidden')) checkForIdlers();
  if (imp && imp.tutorial && imp.tool) await impScan(imp.tool, imp.path, true);
  if (tutDash) tutDash.info = await api('/api/phone').catch(() => tutDash.info);

  renderTutorial();
}

function shouldShowTutorial() {
  if (!config || !state) return false;
  if (config.Global && config.Global.TutorialDone) return false;
  return Object.keys(config.Bots || {}).length === 0;
}

async function startTutorial() {
  tutorialStep = 0;
  tutorialManual = false;
  tutorialMode = 'easy';
  tutorialHuman = null;
  tutorialSelf = false;
  tutLastKey = '';
  tutDir = 1;
  imp = null;
  await tutLoadDash();

  // The installer's "coming from": open straight on bringing the accounts over from that idler.
  const pending = await api('/api/import/pending').catch(() => null);

  if (pending && pending.Pending) {
    impReset(true);
    if (pending.Pending.Tool) await impScan(pending.Pending.Tool, pending.Pending.Path);
    else await impLoadTools();
    tutorialStep = 99;   // clamped to the last step, where the import is; Back still reaches the rest
  } else if (!(state && state.Bots && state.Bots.length)) {
    // Otherwise, on a first run, if an idler with accounts in it is sitting in its usual place, offer those instead
    // of typing. (Replayed with accounts already here, the walkthrough ends without adding one.)
    const tools = await api('/api/import/tools').catch(() => null);
    const found = tools && (tools.Tools || []).find((tl) => tl.Found && tl.Accounts > 0);
    if (found) {
      impReset(true);
      imp.tools = tools;
      await impScan(found.Id, '');
    }
  }

  renderTutorial();
}

/// What the dashboard steps start from: the settings as they are now, so a replay shows what's already set up.
async function tutLoadDash() {
  await loadConfig().catch(() => {});
  if (!schema) schema = await api('/api/settings/schema').catch(() => null);
  const info = await api('/api/phone').catch(() => null);
  const g = (config && config.Global) || {};
  tutDash = {
    info,
    phone: info && info.ListensBeyondThisPc ? true : null,
    anywhere: g.WebRemoteAccess ? true : null,
    pw: '', pwShow: false, pwDone: false, fwMsg: '', fwBad: false,
    autoUpdate: g.AutoUpdate === 1,
    fromHour: g.AutoUpdateFromHour ?? 3, untilHour: g.AutoUpdateUntilHour ?? 6,
    waitHours: g.AutoUpdateWaitHours ?? 2, checkHours: g.UpdateCheckHours ?? 2,
  };
}

/// From the add-an-account step: the list of idlers, inside the walkthrough.
async function tutorialOpenImport() {
  tutorialManual = false;
  impReset(true);
  await impLoadTools();
  renderTutorial();
}

// ── the walkthrough's frame ──
// Every screen of it - the language, the questions, signing in, the account's games, the last "you're all set" - is
// drawn in the same frame: the progress bar, a big plain heading, and the buttons in the same places. Before, each
// was its own pop-up, and it read as a string of dialogs rather than one setup.

/// o = { key, kicker, title, lead, body, hero, badge, back, skip, skipLabel, next, nextOff, at, of }
/// back and skip are onclick code (strings), next is the main button's label - its handler comes from tutShow. A missing
/// one draws no button.
function tutFrame(o) {
  const of = Math.max(1, o.of || tutBarSteps);
  const at = o.at ?? of;
  const bar = Array.from({ length: of }, (_, i) =>
    `<i class="${i < at ? 'done' : i === at ? 'now' : ''}"></i>`).join('');

  return `<div class="tut-head">
      <img src="logo.png" alt="" class="tut-logo"><span class="tut-brand">nocat.<b>farm</b></span>
      <span class="spacer"></span>
      <button class="tut-x" onclick="closeTutorial()" data-tip="${esc(t('You can skip this and come back from the Overview page.'))}" aria-label="${esc(t('Skip'))}">✕</button>
    </div>
    <div class="tut-bar" aria-hidden="true">${bar}</div>
    <div class="tut-stage" id="tutStage">
      ${o.hero ? `<div class="tut-hero"><img src="logo.png" alt=""><h1 class="wordmark">nocat.<span>farm</span></h1></div>` : ''}
      ${o.badge ? `<div class="tut-check-big" aria-hidden="true"><svg viewBox="0 0 24 24" width="28" height="28" fill="none" stroke="currentColor"
        stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg></div>` : ''}
      ${o.kicker ? `<div class="tut-kicker">${esc(o.kicker)}</div>` : ''}
      ${o.title ? `<h2 class="tut-title">${esc(o.title)}</h2>` : ''}
      ${o.lead ? `<p class="tut-lead">${o.lead}</p>` : ''}
      ${o.body || ''}
    </div>
    <div class="tut-foot">
      ${o.back ? `<button class="ghost" onclick="${o.back}">${esc(t('Back'))}</button>` : ''}
      <span class="spacer"></span>
      ${o.skip ? `<button class="ghost tut-skip" onclick="${o.skip}">${esc(o.skipLabel || t('Skip'))}</button>` : ''}
      ${o.next ? `<button id="tutNext" class="tut-go" ${o.nextOff ? 'disabled' : ''}>${esc(o.next)}</button>` : ''}
    </div>`;
}

/// Put a drawn frame on screen. A new screen slides in (from the side it came from); the same screen drawn again -
/// a card picked, a box ticked - just swaps in place, so nothing jumps under the pointer.
function tutPaint(key, html) {
  const fresh = key !== tutLastKey;
  const was = !fresh && $('tutStage') ? $('tutStage').scrollTop : 0;
  tutorialOpen = true;
  modal(html);
  $('modal').classList.add('tut');
  const stage = $('tutStage');
  if (fresh && stage) {
    stage.classList.add(tutDir < 0 ? 'in-back' : 'in-fwd');
    const now = document.querySelector('.tut-bar .now');
    if (now) now.classList.add('grow');
  }
  // Drawn again in place (a firewall answer, a card picked further down): stay where the reader was.
  if (stage) stage.scrollTop = was;
  tutLastKey = key;
  tutDir = 1;
}

function tutShow(o, onNext) {
  tutPaint(o.key, tutFrame(o));
  const next = $('tutNext');
  if (next && onNext) next.onclick = onNext;
}

function tutBack() { tutDir = -1; tutorialStep--; renderTutorial(); }
function tutForward() { tutorialStep++; renderTutorial(); }

// Line icons for the walkthrough's cards - drawn rather than emoji, so they look the same on every system and take
// the theme's colours.
const TUT_ICONS = {
  easy: '<path d="M12 3l2.6 5.6 6.1.7-4.5 4.2 1.2 6L12 16.5 6.6 19.5l1.2-6-4.5-4.2 6.1-.7z"/>',
  advanced: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
  phone: '<rect x="7" y="2.5" width="10" height="19" rx="2"/><path d="M11 18.5h2"/>',
  pc: '<rect x="3" y="4" width="18" height="12" rx="1"/><path d="M8 20h8M12 16v4"/>',
  globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3.2 3 14.8 0 18M12 3c-3 3.2-3 14.8 0 18"/>',
  home: '<path d="M4 11l8-7 8 7v9h-5v-6H9v6H4z"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21c1.5-4 4.5-6 8-6s6.5 2 8 6"/>',
  bolt: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
};
const tutIcon = (name) => `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.6"
  stroke-linecap="round" stroke-linejoin="round">${TUT_ICONS[name] || ''}</svg>`;

/// A big either-or card. Picked, it gets the purple edge.
const tutCard = (on, onclick, icon, title, text, tag) => `<div class="pickcard tut-card ${on ? 'on' : ''}" role="button" tabindex="0" onclick="${onclick}">
    <span class="tut-ico" aria-hidden="true">${icon}</span>
    <span class="tut-cardtext"><b>${esc(title)}${tag ? ` <em class="tut-tag">${esc(tag)}</em>` : ''}</b><span>${esc(text)}</span></span></div>`;

/// A switch with a sentence beside it - the walkthrough's on/off questions.
const tutSwitch = (id, on, onchange, title, text) => `<label class="tut-switch" for="${id}">
    <span><b>${esc(title)}</b>${text ? `<span class="muted small">${esc(text)}</span>` : ''}</span>
    <span class="switch"><input type="checkbox" id="${id}" ${on ? 'checked' : ''} onchange="${onchange}"><span></span></span></label>`;

/// The steps, in order, for the mode picked. Worked out again on every draw: saying yes to the phone adds the password
/// step after it, and the bar grows by one.
function tutSteps() {
  const adv = tutorialMode === 'advanced';
  const hasAccounts = !!(state && state.Bots && state.Bots.length);
  return ['language', 'welcome', 'phone', adv ? 'anywhere' : null,
    // Easy never asks about from anywhere, so only a yes to the phone brings the password step.
    adv || (tutDash && tutDash.phone === true) ? 'password' : null,
    adv ? 'notify' : null, 'updates', hasAccounts ? null : 'type', 'final'].filter(Boolean);
}

function tutStepName(key) {
  switch (key) {
    case 'language': return t('Language');
    case 'welcome': return t('Welcome');
    case 'phone': return t('Phone');
    case 'anywhere': return t('From anywhere');
    case 'password': return t('Password');
    case 'notify': return t('Notifications');
    case 'updates': return t('Updates');
    case 'type': return t('Account type');
    default: return state && state.Bots && state.Bots.length ? t('Done') : t('Account');
  }
}

/// Saved the way the settings page saves: read again first, so nothing changed elsewhere in the meantime is put back,
/// then only these on top. A new dashboard password signs every browser out - this one gets a fresh session back.
async function tutSaveGlobal(changes) {
  await loadConfig().catch(() => {});
  if (!config || !config.Global) { toast(t('Save failed'), true); return null; }
  const res = await postGlobal({ ...config.Global, ...changes }).catch(() => null);
  if (!res || !res.ok) { toast((res && res.error) || t('Save failed'), true); return null; }
  if (res.Token) {
    token = res.Token;
    localStorage.setItem('nocatfarm-token', token);
  }
  const adjusted = (res.Adjusted || []).filter(Boolean);
  if (adjusted.length) toast(adjusted.join(' · '), true);
  await loadConfig().catch(() => {});
  return res;
}

function renderTutorial() {
  tutorialOpen = true;
  if (!tutDash) tutDash = { info: null, phone: null, anywhere: null, pw: '', autoUpdate: false, fromHour: 3, untilHour: 6, waitHours: 2, checkHours: 2 };

  const steps = tutSteps();
  if (tutorialStep > steps.length - 1) tutorialStep = steps.length - 1;
  if (tutorialStep < 0) tutorialStep = 0;
  tutBarSteps = steps.length;

  const key = steps[tutorialStep];
  const st = tutStep(key);
  const last = tutorialStep >= steps.length - 1;
  const hasAccounts = !!(state && state.Bots && state.Bots.length);

  tutShow({
    key: key + (st.sub || ''),
    at: tutorialStep, of: steps.length,
    kicker: tf('STEP {0} OF {1}', tutorialStep + 1, steps.length) + ' · ' + tutStepName(key),
    hero: st.hero, title: st.title, lead: st.lead, body: st.body,
    back: st.back || (tutorialStep > 0 ? 'tutBack()' : ''),
    // The account step's Skip is the old "Not yet": it leaves without adding one. Replayed with accounts already
    // here there's nothing to leave undone, so only Finish.
    skip: st.skip || (last && !hasAccounts ? 'closeTutorial()' : ''),
    skipLabel: st.skipLabel || (last ? t('Not yet') : ''),
    next: st.next || t('Next'),
  }, st.act || tutForward);

  // A step with boxes to fill in: the first one gets the caret.
  const first = st.focus && $(st.focus);
  if (first) setTimeout(() => first.focus(), 60);
}

/// One step's content. Returns { title, lead, body, hero, next, act, back, skip, skipLabel, focus, sub }.
function tutStep(key) {
  switch (key) {
    case 'language': return tutStepLanguage();
    case 'welcome': return tutStepWelcome();
    case 'phone': return tutStepPhone();
    case 'anywhere': return tutStepAnywhere();
    case 'password': return tutStepPassword();
    case 'notify': return tutStepNotify();
    case 'updates': return tutStepUpdates();
    case 'type': return tutStepType();
    default: return tutStepFinal();
  }
}

function tutStepLanguage() {
  // Language first, before a word of the rest is read. Skipping leaves it English, which is also what an
  // untranslated string falls back to - so there is no way to end up looking at blanks.
  const now = (config && config.Global && config.Global.Language) || 'en';
  return {
    hero: true,
    lead: esc(t('Pick the language for this dashboard. You can change it later under Settings › Global.')),
    body: `<div class="langpick tut-langs">${LANGUAGES.map((l) =>
        `<span class="p ${now === l.code ? 'on' : ''}" role="button" tabindex="0" onclick="pickTutorialLanguage('${l.code}')">${esc(l.name)}</span>`).join('')}</div>
      <p class="muted small">${esc(t("Anything a translation hasn't covered yet stays in English rather than showing a blank, so a part-finished language is still perfectly usable. Status and log lines follow it too; replies to commands typed in the console stay in English."))}</p>`,
    next: t('Continue'),
  };
}

function tutStepWelcome() {
  const b = (name) => `<b>${esc(name)}</b>`;
  return {
    title: t('Welcome to nocat.farm'),
    lead: esc(t('It signs your Steam accounts in, farms their trading cards, plays games so the hours count and picks up free stuff - all from this machine. Your accounts never leave it.')),
    body: `<div class="pickcards tut-cards">
        ${tutCard(tutorialMode === 'easy', "tutorialMode='easy';renderTutorial()", tutIcon('easy'), t('Easy'),
          t('A few plain questions, then your account is running. The defaults are good. About two minutes.'), t('Recommended'))}
        ${tutCard(tutorialMode === 'advanced', "tutorialMode='advanced';renderTutorial()", tutIcon('advanced'), t('Advanced'),
          t("Everything: your phone, opening it from anywhere, notifications, the update hours, then every setting of the account. For anyone who has used ArchiSteamFarm or another idler."))}
      </div>
      <details class="tut-more"><summary>${esc(t('What does it do?'))}</summary><ul>
        <li>${tf('{0} - it works through everything in your library that still has drops, then stops. Nothing to configure.', b(t('Trading cards')))}</li>
        <li>${tf('{0} - it keeps a believable daily routine: a few games, one at a time, with breaks, meals and a bedtime. Use this on an account you care about.', b(t('Human mode')))}</li>
        <li>${tf('{0} - the daily sticker during a Steam sale, and anything in the Points Shop that costs 0 points.', b(t('Free event items')))}</li>
        <li>${tf('{0} - Steam wallet gift cards and guest passes people send you.', b(t('Gifts')))}</li>
      </ul><p class="muted small">${esc(t('Free games, booster packs from gems and fair card swaps are one switch each under Settings, per account.'))}</p></details>`,
    next: t('Start'),
  };
}

// ── the phone ──

function tutStepPhone() {
  const d = tutDash;
  const p = d.info;
  return {
    title: t('Use it from your phone'),
    lead: esc(t('Check on your accounts from your phone while it is on the same Wi-Fi. It opens this same dashboard.')),
    body: `<div class="pickcards tut-cards two">
        ${tutCard(d.phone === true, 'tutPickPhone(true)', tutIcon('phone'), t('Yes, set it up'), t('You pick a password next, then scan a code with your phone.'))}
        ${tutCard(d.phone === false, 'tutPickPhone(false)', tutIcon('pc'), t('Not now'), t('Only this PC can open it. You can turn it on later in Settings.'))}
      </div>
      ${d.phone === true && p && p.OpenAtHome ? tutPhoneStatus(p) : ''}`,
    act: tutPhoneNext,
    skip: 'tutForward()',
  };
}

function tutPickPhone(on) {
  tutDash.phone = on;
  renderTutorial();
}

async function tutPhoneNext() {
  const d = tutDash;
  // "Not now" on a dashboard that is open to the phone already closes it again - that's what was asked. Not from the
  // phone itself, though: that would lock out the very screen asking, after the next restart.
  if (d.phone === false && d.info && d.info.ListensBeyondThisPc && d.info.OnThisPc) {
    if (!await tutSaveGlobal({ WebHost: '127.0.0.1' })) return;
    d.info = await api('/api/phone').catch(() => d.info);
  }
  tutForward();
}

/// What the phone needs next, from a fresh look at /api/phone: a restart, the firewall, then the code to scan. And from
/// anywhere, when that's on: the link, or why the router won't.
function tutPhoneStatus(p) {
  const link = (url) => `<a href="${esc(url)}" target="_blank" rel="noopener">${esc(url)}</a>`;
  const d = tutDash;
  let html = '';

  if (p.NeedsRestart) {
    html += `<div class="tut-note warn"><b>${esc(t('One more thing: restart nocat.farm'))}</b>
      <span>${esc(t('It starts listening for your phone the next time it starts. Close it and open it again once.'))}</span>
      <span><button class="small" onclick="phoneRestart(this, tutAfterRestart)">${esc(t('Restart the dashboard now'))}</button></span></div>`;
  }

  if (p.FirewallBlocks) {
    html += `<div class="tut-note"><b>${esc(t('Windows Firewall is blocking your phone'))}</b>
      <span>${esc(t('Your phone will just keep loading until Windows lets it in. This allows the dashboard only, only on home networks - Windows asks you to confirm.'))}</span>
      ${p.OnThisPc ? `<span><button class="small" id="tutFw" onclick="tutFirewall(this)">${esc(t('Let it through the firewall'))}</button></span>`
        : `<span class="muted small">${esc(t('Press it on the PC nocat.farm runs on.'))}</span>`}
      ${d && d.fwMsg ? `<span class="small ${d.fwBad ? 'error' : ''}" style="margin:0">${esc(d.fwMsg)}</span>` : ''}</div>`;
  }

  if (p.OpenAtHome && p.NeedsHomeAddress) {
    html += `<div class="tut-note"><b>${esc(t('Running in Docker'))}</b>
      <span>${esc(t("From inside Docker nocat.farm can't see this computer's address on your wifi. On your phone, open this computer's address with the port Docker publishes - or put it in NOCATFARM_HOME_ADDRESS in docker-compose.yml, and this shows the link and a code to scan."))}</span></div>`;
  }

  if (p.OpenAtHome && p.Qr) {
    html += `<div class="tut-qr"><div class="phoneqr">${p.Qr}</div>
      <div><p class="small">${esc(t('Scan this with your phone\'s camera while it is on the same wifi, then sign in with the dashboard password.'))}</p>
      <p class="muted small">${link(p.Home[0])}${p.Home.length > 1 ? ' · ' + p.Home.slice(1).map(link).join(' · ') : ''}</p></div></div>`;
  }

  if (p.RemoteOn) {
    html += `<p class="muted small">${p.Outside ? `${esc(t('From outside your home:'))} ${link(p.Outside)}`
      : p.RemoteProblem ? `${esc(t('From anywhere:'))} ${esc(p.RemoteProblem)}`
      : esc(t('From anywhere: asking your router to forward the port...'))}</p>`;
  }

  return html;
}

/// Back from "Restart the dashboard now" in the walkthrough: a fresh look, so the note makes way for the code to scan.
async function tutAfterRestart() {
  if (!tutDash) return;
  tutDash.info = await api('/api/phone').catch(() => tutDash.info);
  if (tutorialOpen) renderTutorial();
}

/// "Let it through the firewall": Windows asks the person at this PC. Whatever comes back is said on the step - done,
/// said no on the prompt, a Public network, or anything else - and the button stays for another go.
async function tutFirewall(btn) {
  btn.disabled = true;
  btn.textContent = t('Check the Windows prompt…');
  const r = await api('/api/phone/firewall', { method: 'POST' }).catch(() => null);
  const d = tutDash;

  if (r && r.Ok) {
    toast(t('Done - your phone can open it now.'));
    d.fwMsg = '';
    d.fwBad = false;
  } else {
    d.fwMsg = (r && r.Error) || t("That didn't work");
    d.fwBad = true;
  }

  d.info = await api('/api/phone').catch(() => d.info);
  if (tutorialOpen) renderTutorial();   // closed while Windows asked: stay closed
}

// ── from anywhere ──

function tutStepAnywhere() {
  const d = tutDash;
  const p = d.info;
  return {
    title: t('Open it from anywhere?'),
    lead: esc(t('Check on your accounts away from home, on mobile data. Like Jellyfin: nocat.farm asks your router to pass the dashboard through to this PC. No other app needed.')),
    body: `<ul class="tut-list">
        <li>${esc(t('The link is plain http, not encrypted. Sign in on mobile data or a network you trust.'))}</li>
        <li>${esc(t('It needs a strong password: at least 12 characters. Five wrong tries lock someone out for an hour.'))}</li>
        <li>${esc(t("Some routers and internet providers don't allow it. If yours doesn't, it says why."))}</li>
      </ul>
      <div class="pickcards tut-cards two">
        ${tutCard(d.anywhere === true, 'tutPickAnywhere(true)', tutIcon('globe'), t('Yes, open it up'), t('You pick a strong password next.'))}
        ${tutCard(d.anywhere === false, 'tutPickAnywhere(false)', tutIcon('home'), t('No, home only'), t('Nothing changes on your router.'))}
      </div>
      ${p && p.RemoteOn && p.RemoteProblem ? `<div class="tut-note warn"><b>${esc(t('Right now'))}</b><span>${esc(p.RemoteProblem)}</span></div>` : ''}`,
    act: tutAnywhereNext,
    skip: 'tutForward()',
  };
}

function tutPickAnywhere(on) {
  tutDash.anywhere = on;
  renderTutorial();
}

async function tutAnywhereNext() {
  // Turning it off needs no password, so it's saved straight away; turning it on waits for the password step.
  if (tutDash.anywhere === false && config && config.Global && config.Global.WebRemoteAccess) {
    if (!await tutSaveGlobal({ WebRemoteAccess: false })) return;
    tutDash.info = await api('/api/phone').catch(() => tutDash.info);
  }
  tutForward();
}

// ── the dashboard password ──

/// How much a password holds up, 0-4: nothing, too short, weak, okay, strong. Rough on purpose - it's a hint, the
/// only hard rules are the lengths.
function tutPwScore(pw) {
  if (!pw) return 0;
  if (pw.length < 8) return 1;
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(pw)).length;
  if (/^(.)\1+$/.test(pw) || /password|passwort|123456|qwerty|nocat/i.test(pw)) return 2;
  if (pw.length >= 16 || (pw.length >= 12 && kinds >= 3)) return 4;
  if (pw.length >= 12 || kinds >= 3) return 3;
  return 2;
}

/// The shortest password the steps picked need: 12 to open it from anywhere, 8 for the phone.
const tutPwMin = () => (tutDash.anywhere === true ? 12 : 8);

function tutPwMeter() {
  const pw = tutDash.pw;
  const score = tutPwScore(pw);
  const words = ['', t('Too short'), t('Weak'), t('Okay'), t('Strong')];
  const min = tutPwMin();
  const hint = pw && pw.length < min
    ? (tutDash.anywhere === true ? tf('Open from anywhere needs at least {0} characters.', min) : tf('Use at least {0} characters.', min))
    : (pw ? words[score] : '');
  return `<div class="tut-meter s${score}" aria-hidden="true"><i></i><i></i><i></i><i></i></div>
    <span class="muted small">${esc(hint)}</span>`;
}

function tutPwInput(el) {
  tutDash.pw = el.value;
  const m = $('tutMeter');
  if (m) { m.innerHTML = tutPwMeter(); m.classList.remove('bad'); }
  if ($('tutError')) $('tutError').textContent = '';
}

function tutPwToggle() {
  tutDash.pwShow = !tutDash.pwShow;
  const el = $('tut-pw');
  if (el) el.type = tutDash.pwShow ? 'text' : 'password';
  const b = $('tutPwShow');
  if (b) b.textContent = tutDash.pwShow ? t('Hide') : t('Show');
}

function tutStepPassword() {
  const d = tutDash;
  const p = d.info || {};
  const wanted = d.phone === true || d.anywhere === true;

  // Saved: now say what happens next - the code to scan, the firewall, a restart.
  if (d.pwDone) {
    return {
      sub: '-done',
      title: d.phone === true ? t('Scan it with your phone') : t('Password saved'),
      lead: d.phone === true ? '' : esc(t('Anyone who opens the dashboard from another device signs in with it.')),
      body: tutPhoneStatus(p) || `<p class="muted small">${esc(t('Saved.'))}</p>`,
      back: "tutDash.pwDone=false;tutDir=-1;renderTutorial()",
      act: tutForward,
    };
  }

  return {
    title: t('Pick a dashboard password'),
    lead: esc(wanted
      ? t('Your phone signs in with it. Without one, only this PC can open the dashboard - so nobody else on your Wi-Fi can reach your accounts.')
      : t('Optional while only this PC opens the dashboard. Your phone, or anywhere, needs one first.')),
    body: `${p.HasPassword ? `<p class="tut-note"><span>${esc(t('A password is already set. Leave the box empty to keep it.'))}</span></p>` : ''}
      <div class="tut-pw">
        <input id="tut-pw" type="${d.pwShow ? 'text' : 'password'}" autocomplete="new-password" spellcheck="false"
          placeholder="${esc(p.HasPassword ? t('leave empty to keep it') : t('a password only you know'))}" value="${esc(d.pw)}" oninput="tutPwInput(this)">
        <button type="button" class="ghost" id="tutPwShow" onclick="tutPwToggle()">${esc(d.pwShow ? t('Hide') : t('Show'))}</button>
      </div>
      <div class="tut-meterrow" id="tutMeter">${tutPwMeter()}</div>
      <p id="tutError" class="error"></p>`,
    next: wanted ? t('Save and continue') : t('Next'),
    act: tutSavePassword,
    skip: wanted ? '' : 'tutForward()',
    focus: 'tut-pw',
  };
}

/// Saves the password with whatever the phone and from-anywhere steps asked for, in one go: listening on the network
/// only ever goes out together with a password.
async function tutSavePassword() {
  const d = tutDash;
  const p = d.info || {};
  const pw = d.pw;
  const err = $('tutError');
  // Easy doesn't ask about from anywhere: a setting already on stays as it is, and doesn't make this step demand anything.
  const anywhere = tutorialMode === 'advanced' && d.anywhere === true;
  const wanted = d.phone === true || anywhere;
  const say = (text) => { if (err) err.textContent = text; };

  if (!pw && !wanted) { tutForward(); return; }
  if (!pw && !p.HasPassword) { say(t('Type a password first - your phone signs in with it.')); return; }
  // The hint under the box already says how long - it turns red rather than being said twice.
  if (pw && pw.length < tutPwMin()) {
    const m = $('tutMeter');
    if (m) m.classList.add('bad');
    return;
  }
  // Keeping the saved one for from anywhere: it has to be long enough, and only the server knows how long it is.
  if (!pw && anywhere && !p.LongPassword) { say(tf('Open from anywhere needs at least {0} characters.', 12)); return; }

  const btn = $('tutNext');
  if (btn) btn.disabled = true;
  const changes = {};
  if (pw) changes.WebPassword = pw;
  if (wanted && !p.ListensBeyondThisPc) changes.WebHost = '0.0.0.0';
  if (anywhere) changes.WebRemoteAccess = true;

  const res = await tutSaveGlobal(changes);
  if (!res) { if (btn) btn.disabled = false; return; }

  // Gone from the page the moment it's saved - it never comes back from the server, and it shouldn't linger here.
  d.pw = '';
  d.pwShow = false;
  d.info = await api('/api/phone').catch(() => d.info);

  if (!wanted) { tutForward(); return; }
  d.pwDone = true;
  renderTutorial();

  // The router takes a few seconds to answer - look again once, so the step says how that went.
  if (d.anywhere === true) {
    setTimeout(async () => {
      if (!tutorialOpen || !d.pwDone || tutDash !== d) return;
      d.info = await api('/api/phone').catch(() => d.info);
      if (tutorialOpen && d.pwDone && tutDash === d && tutLastKey === 'password-done') renderTutorial();
    }, 4000);
  }
}

// ── notifications ──

function tutStepNotify() {
  const set = (name) => (config && config.GlobalSecretsSet || []).includes(name);
  const secret = (id, name, label, ph) => `<div class="tut-secret">
      <label for="${id}">${esc(label)}${set(name) ? ` <span class="pill good">${esc(t('saved'))}</span>` : ''}</label>
      <div class="tut-pw"><input id="${id}" type="password" autocomplete="off" spellcheck="false"
        placeholder="${esc(set(name) ? t('leave empty to keep it') : ph)}" onkeydown="if(event.key==='Enter')tutSaveSecret('${id}','${name}')">
      <button class="ghost" onclick="tutSaveSecret('${id}','${name}')">${esc(t('Save'))}</button></div></div>`;
  const g = (config && config.Global) || {};

  return {
    title: t('Hear from it on your phone'),
    lead: esc(t('A message when cards drop, a gift arrives or something needs you. Discord, Telegram, both - or neither.')),
    body: `<div class="tut-svc">
        <div class="tut-svchead"><b>Telegram</b></div>
        ${notifyGuide('telegram')}
        ${secret('tut-tg', 'TelegramBotToken', t('Telegram bot token'), '123456789:ABC...')}
        ${telegramConnect()}
      </div>
      <div class="tut-svc">
        <div class="tut-svchead"><b>Discord</b></div>
        ${notifyGuide('discord')}
        ${secret('tut-dh', 'DiscordWebhookUrl', t('Discord webhook'), 'https://discord.com/api/webhooks/...')}
        <details class="tut-more"><summary>${esc(t('Discord commands too (optional)'))}</summary>
          ${notifyGuide('discordbot')}
          ${secret('tut-db', 'DiscordBotToken', t('Discord bot token'), 'MTIz...')}
          ${discordConnect()}
        </details>
      </div>
      ${tutSwitch('tut-installs', g.SendInstalls, 'tutSaveGlobal({ SendInstalls: this.checked })',
        t('Tell me when an update installs'), t('Off unless you turn it on.'))}`,
    act: tutSaveNotify,
    skip: 'tutForward()',
  };
}

/// Whatever is pasted into the notification boxes right now, as settings. Empty boxes keep what's saved.
function tutNotifyTyped() {
  const changes = {};
  for (const [id, name] of [['tut-tg', 'TelegramBotToken'], ['tut-dh', 'DiscordWebhookUrl'], ['tut-db', 'DiscordBotToken']]) {
    const v = $(id) ? $(id).value.trim() : '';
    if (v) changes[name] = v;
  }
  return changes;
}

/// A token or webhook, saved the moment Save is pressed - together with anything pasted into the other boxes, which the
/// redraw would otherwise empty. Connect Telegram only appears once the token is saved and Telegram has said yes to it,
/// a few seconds later (refresh() redraws it in place).
async function tutSaveSecret(id) {
  const changes = tutNotifyTyped();
  if (!$(id) || !$(id).value.trim()) { toast(t('Paste it first'), true); return; }
  const res = await tutSaveGlobal(changes);
  if (!res) return;
  toast(t('Saved.'));
  renderTutorial();
  refresh();
}

/// Next on the notifications step: anything pasted and not yet saved is saved, not dropped.
async function tutSaveNotify() {
  const changes = tutNotifyTyped();
  if (Object.keys(changes).length) {
    const btn = $('tutNext');
    if (btn) btn.disabled = true;
    if (!await tutSaveGlobal(changes)) { if (btn) btn.disabled = false; return; }
    toast(t('Saved.'));
    refresh();
  }
  tutForward();
}

// ── updates ──

function tutStepUpdates() {
  const d = tutDash;
  const adv = tutorialMode === 'advanced';
  const def = (n) => schema && (schema.Global || []).find((x) => x.Name === n);
  const label = (n, fallback) => { const x = def(n); return x ? tSetting(x, 'label') : fallback; };
  const num = (key, min, max) => `<input type="number" min="${min}" max="${max}" value="${esc(d[key])}" oninput="tutDash.${key}=+this.value">`;

  // Docker and a Linux service can't swap themselves over: it says when a new version is out, and that's all it can do.
  if (state && state.CanSelfUpdate === false) {
    return {
      title: t('Keep it up to date'),
      lead: esc(t('New versions fix things and add new ones.')),
      body: `<p class="small">${esc(t('It tells you when a new version is out. Here it can\'t install it by itself - in Docker, get the new version and run docker compose up -d --build; run as a Linux service, stop it, unzip the new zip over the folder and start it again. Your accounts and settings stay where they are.'))}</p>
        ${adv ? `<div class="tut-grid">
          <label>${esc(label('UpdateCheckHours', 'Look for updates every'))}</label>
          <span class="tut-hours">${num('checkHours', 1, 24)}<span>${esc(t('hours'))}</span></span>
        </div>` : `<p class="muted small tut-later">${esc(t('You can connect Discord or Telegram later in Settings, to get a message when something happens.'))}</p>`}`,
      act: tutSaveUpdates,
      skip: 'tutForward()',
    };
  }

  return {
    title: t('Keep it up to date'),
    lead: esc(t('New versions fix things and add new ones.')),
    body: `${tutSwitch('tut-auto', d.autoUpdate, 'tutDash.autoUpdate=this.checked;renderTutorial()',
        t('Keep it up to date by itself'), t('Installs at night while your accounts sleep, and only when nobody is playing.'))}
      ${d.autoUpdate ? '' : `<p class="muted small">${esc(t('Off: it tells you when a new version is out, and you install it with one click.'))}</p>`}
      ${adv ? `<div class="tut-grid">
          <label>${esc(t('Installs between'))}</label>
          <span class="tut-hours">${num('fromHour', 0, 23)}<span>:00 ${esc(t('and'))}</span>${num('untilHour', 0, 24)}<span>:00</span></span>
          <label>${esc(label('AutoUpdateWaitHours', 'Wait after a release for'))}</label>
          <span class="tut-hours">${num('waitHours', 0, 168)}<span>${esc(t('hours'))}</span></span>
          <label>${esc(label('UpdateCheckHours', 'Look for updates every'))}</label>
          <span class="tut-hours">${num('checkHours', 1, 24)}<span>${esc(t('hours'))}</span></span>
        </div>` : `<p class="muted small tut-later">${esc(t('You can connect Discord or Telegram later in Settings, to get a message when something happens.'))}</p>`}`,
    act: tutSaveUpdates,
    skip: 'tutForward()',
  };
}

async function tutSaveUpdates() {
  const d = tutDash;
  const g = (config && config.Global) || {};
  const changes = {};
  const auto = d.autoUpdate ? 1 : 0;
  if (auto !== g.AutoUpdate) changes.AutoUpdate = auto;
  // Looking for updates switched off stops installing them too - so switching installing on has to switch it back on.
  if (auto === 1 && g.CheckForUpdates === false) changes.CheckForUpdates = true;

  if (tutorialMode === 'advanced') {
    const clamp = (v, lo, hi, was) => (Number.isFinite(v) ? Math.max(lo, Math.min(hi, Math.round(v))) : was);
    const set = (name, v) => { if (v !== g[name]) changes[name] = v; };
    set('AutoUpdateFromHour', clamp(d.fromHour, 0, 23, g.AutoUpdateFromHour));
    set('AutoUpdateUntilHour', clamp(d.untilHour, 0, 24, g.AutoUpdateUntilHour));
    set('AutoUpdateWaitHours', clamp(d.waitHours, 0, 168, g.AutoUpdateWaitHours));
    set('UpdateCheckHours', clamp(d.checkHours, 1, 24, g.UpdateCheckHours));
  }

  if (Object.keys(changes).length) {
    const btn = $('tutNext');
    if (btn) btn.disabled = true;
    if (!await tutSaveGlobal(changes)) { if (btn) btn.disabled = false; return; }
  }
  tutForward();
}

// ── the account ──

function tutStepType() {
  // What the account is for - asked once there is no account yet, and turned into its settings when it's added.
  const importing = !!(imp && imp.tutorial) && !tutorialManual;
  const importAccts = (importing && imp.scan && imp.scan.Accounts) || [];
  return {
    title: t('What kind of account is it?'),
    lead: esc(t('This picks the right settings for it. Anything can be changed later, per account.')),
    body: `<div class="pickcards tut-cards">
        ${tutCard(tutorialHuman === true, 'tutorialPickType(true)', tutIcon('user'), t('My main - I play on it'),
          t("Human mode: it keeps a believable day - sleeps, takes breaks, plays one game at a time and waits a person's while before it trades or replies. Slower, but nothing about it looks automated."))}
        ${tutCard(tutorialHuman === false, 'tutorialPickType(false)', tutIcon('bolt'), t('A spare or farm account'),
          t('Robot mode: farms cards and idles games around the clock at full speed. Best for accounts nobody looks at.'))}
      </div>
      ${importAccts.length > 1 ? `<p class="muted small">${esc(t('Importing several? You pick which ones on the next step.'))}</p>` : ''}`,
    act: () => {
      if (tutorialHuman === null) { toast(t('Pick one first'), true); return; }
      tutForward();
    },
  };
}

function tutStepFinal() {
  const hasAccounts = !!(state && state.Bots && state.Bots.length);
  const importing = !!(imp && imp.tutorial) && !tutorialManual;

  const tips = `<p class="muted small">${tf('Everything has an explanation attached - tap or hover the {0} beside any setting.', '<i class="info" style="display:inline-flex"></i>')}
      ${tf('The Console tab does anything the other tabs do, by typing. {0} lists it all.', '<code>help</code>')}</p>`;

  // The account comes LAST. Adding one starts it signing in, and every question before it is best out of the way
  // first - so the final button adds the account (or imports) and the sign-in carries on right here.
  // It used to be step three, and "Add an account" there closed the walkthrough outright: nobody who took
  // that path ever saw the rest of it.
  if (hasAccounts) {
    // Replayed from the Overview page on a machine that is already running accounts.
    return { title: t("That's it"), lead: esc(t('Everything you picked is saved. You can change any of it later under Settings.')), body: tips, next: t('Finish'), act: closeTutorial };
  }

  if (importing) {
    const sc = imp.scan;
    return {
      title: sc && sc.Found ? tf('Bring your {0} accounts across', sc.ToolName) : t('Import from another idler'),
      body: `${sc ? importPreviewHtml() : importToolsHtml()}
           <p id="tutError" class="error"></p>
           <p class="muted small">${esc(t('Importing copies the accounts, their sign-ins and settings. It changes nothing in the other program.'))}
             ${sc ? `<a style="cursor:pointer" onclick="impBack()">${esc(t('A different idler'))}</a> ·` : ''}
             <a style="cursor:pointer" onclick="tutorialManual=true;renderTutorial()">${esc(t('Add one by hand instead'))}</a></p>
           ${tips}`,
      next: t('Import them'),
      act: impApply,
    };
  }

  const field = (id, label, tip, input) => `<div class="tut-field"><label for="${id}">${esc(label)}${tipIcon(tip)}</label>${input}</div>`;
  return {
    title: t('Add your first account'),
    lead: tf('Add the Steam account you want it to run. You will be asked for the password {0}, and for a Steam Guard code - after that it remembers a login token and never needs the password again.', `<b>${esc(t('once'))}</b>`),
    body: `<div class="tut-fields">
        ${field('tut-login', t('Steam account name'), t("What you type into Steam's sign-in box. Not your display name, not your email."),
          `<input id="tut-login" type="text" placeholder="${esc(t('your steam login'))}" autocomplete="off" spellcheck="false">`)}
        ${field('tut-pass', t('Password'), t('Optional. Leave it blank and nocat.farm asks once, then remembers the account with a login token instead - which is safer than a password in a file.'),
          `<input id="tut-pass" type="password" placeholder="${esc(t("leave blank and it'll ask"))}" autocomplete="off">`)}
        ${field('tut-name', t('Nickname (optional)'), t("What this account is called in nocat.farm. Leave it empty and it uses the Steam account name."),
          `<input id="tut-name" type="text" placeholder="${esc(t('same as the Steam account name'))}" autocomplete="off" spellcheck="false">`)}
      </div>
      ${tutorialMode === 'advanced' ? `<label class="tut-check"><input id="tut-qr" type="checkbox" onchange="['tut-login','tut-pass'].forEach((id) => { $(id).disabled = this.checked; })">
        <span>${esc(t('Sign in with a QR code'))}${tipIcon(t("Scan a code with the Steam app on your phone instead of typing a password - the account name comes from Steam, so both boxes above can stay empty."))}</span></label>` : ''}
      ${tutorialHuman === true ? `<label class="tut-check"><input id="tut-self" type="checkbox" ${tutorialSelf ? 'checked' : ''} onchange="tutorialSelf=this.checked">
        <span>${esc(t('I also sign into it from my own Steam app'))}${tipIcon(t('Then nocat.farm never changes its online status - if it did, Steam would sign your own client out of Friends and Chat.'))}</span></label>` : ''}
      <p id="tutError" class="error"></p>
      <p class="muted small"><a style="cursor:pointer" onclick="${imp && imp.tutorial ? 'tutorialManual=false;renderTutorial()' : 'tutorialOpenImport()'}">${esc(t('Coming from another idler? Import from it'))}</a></p>
      ${tips}`,
    next: t('Add account'),
    act: tutorialAddAccount,
    focus: 'tut-login',
  };
}

async function tutorialAddAccount() {
  const btn = $('tutNext');
  if (btn.disabled) return;

  // Never added without being asked what it's for. Opened straight on the installer's import and switched to adding
  // one by hand, the "what kind of account" step was jumped past - and the account went in as a robot, unasked.
  if (tutorialHuman === null) {
    const at = tutSteps().indexOf('type');
    if (at >= 0) { toast(t('Pick one first'), true); tutorialStep = at; renderTutorial(); return; }
  }

  btn.disabled = true;

  const qr = !!($('tut-qr') && $('tut-qr').checked);
  const login = $('tut-login').value.trim();
  const typed = $('tut-name').value.trim();

  // Said here, before anything is sent, in the same words the server uses: the nickname becomes a file name and
  // what you type in commands, so a space or an apostrophe in it would only come back as a refusal anyway.
  if (typed && !/^[A-Za-z0-9_-]+$/.test(typed)) {
    $('tutError').textContent = t("Letters, numbers, dashes and underscores. 'nocatFarm' is taken by the global config.");
    btn.disabled = false;
    return;
  }

  const name = typed || nameFromLogin(login);
  const res = await post('/api/bots', {
    Name: name, SteamLogin: login, Password: $('tut-pass').value, Qr: qr,
    Human: tutorialHuman === true, SelfSignIn: tutorialHuman === true && tutorialSelf,
  });

  if (!res.ok) {
    $('tutError').textContent = res.error || t("Couldn't add that account.");
    btn.disabled = false;
    return;
  }

  // Stay open. Signing in is exactly where somebody new gets lost - the password and Steam Guard questions used
  // to appear in a bar at the top of the dashboard after this closed, easy to miss behind the page. Now they're
  // asked right here, and the last screen says what happens next.
  sessionStorage.setItem('skip-welcome', '1');
  post('/api/tutorial/done', {}).catch(() => {});
  if (config && config.Global) config.Global.TutorialDone = true;
  tutorialSignin = name;
  tutorialAdded = name;
  tutorialSigninHtml = '';
  tutorialSetupDone = false;
  await refresh();
  renderSignin();
}

// ── setting the account up, once it's signed in ───────────────────────────
// Plain choices, not settings: tap its games, drag a slider, type a few numbers. Everything is written to the
// account's real settings on the last step, and all of it can be changed later under Settings.
let tutorialSetupDone = false;
let tutSetup = null;   // { name, steps, at, lib, main, sides, pct, idle, routine, customName }

async function startTutorialSetup(name, steps) {
  tutorialSignin = null;   // the poll-driven sign-in screen is finished; these steps draw themselves
  tutorialOpen = true;
  if (!schema) schema = await api('/api/settings/schema').catch(() => null);
  const d = (schema && schema.BotDefaults) || {};
  tutSetup = {
    name, steps, at: 0, lib: null, main: 0, sides: new Set(), pct: 70, idle: new Set(), customName: '',
    routine: {
      WeekdayHours: d.WeekdayHours ?? 6, WeekendHours: d.WeekendHours ?? 9, DayStartHour: d.DayStartHour ?? 11,
      BedHour: d.BedHour ?? 1, DayOffChancePct: d.DayOffChancePct ?? 10,
    },
  };
  tutShow({ key: 'setup-loading', at: tutBarSteps - 1, kicker: tf('SETTING UP {0}', name.toUpperCase()),
    title: t('Looking at its games...'), body: `<div class="tut-spin" aria-hidden="true"></div><p class="muted small">${esc(t('This takes a few seconds...'))}</p>` });

  // The library lands a few seconds after signing in.
  for (let i = 0; i < 15; i++) {
    const lib = await api('/api/bots/' + encodeURIComponent(name) + '/library').catch(() => null);
    if (lib && lib.Ready) { tutSetup.lib = lib.Games || []; break; }
    await new Promise((r) => setTimeout(r, 2000));
    if (!tutorialOpen) return;   // closed while it looked - don't come back over the dashboard
  }
  if (!tutorialOpen) return;
  tutSetup.lib = tutSetup.lib || [];
  tutSetup.lib.forEach((g) => { if (g.Name) GAME_NAMES[g.AppId] = g.Name; });

  // Already has games (brought across from ArchiSteamFarm, say): start from those, so it's a tweak, not a redo.
  if (!config || !config.Bots) await loadConfig().catch(() => {});
  const have = parseWeights((config && config.Bots && config.Bots[name] && config.Bots[name].GameWeights) || '');
  if (have.length) {
    tutSetup.main = +have[0].game;
    have.slice(1).forEach((r) => tutSetup.sides.add(+r.game));
    const total = have.reduce((sum, r) => sum + (r.weight || 0), 0);
    if (have[0].weight && total) tutSetup.pct = Math.max(40, Math.min(100, Math.round(have[0].weight * 100 / total / 5) * 5));
    learnNames(have.map((r) => +r.game));
  }
  renderTutorialSetup();
}

const tutHours = (m) => m >= 60 ? Math.round(m / 60) + 'h' : (m > 0 ? m + 'm' : '');

function tutGameChips(selected, onclick, extra) {
  const games = tutSetup.lib.slice(0, 18);
  const ids = new Set(games.map((g) => g.AppId));
  // Anything picked by hand that isn't in the top list still shows, so it can be seen and un-picked.
  const added = [...extra].filter((id) => !ids.has(id)).map((id) => ({ AppId: id, Name: gameLabel(id), Minutes: 0 }));
  return `<div class="langpick">${[...games, ...added].map((g) =>
    `<span class="p ${selected(g.AppId)}" onclick="${onclick}(${g.AppId})">${esc(g.Name || gameLabel(g.AppId))}${g.Minutes ? ` <span class="muted">${tutHours(g.Minutes)}</span>` : ''}</span>`).join('')}</div>`;
}

function renderTutorialSetup() {
  const s = tutSetup;
  const step = s.steps[s.at];
  const last = s.at === s.steps.length - 1;
  let title;
  let body;

  if (step === 'games') {
    title = t('What does it play?');
    const sides = [...s.sides];
    body = `<p>${esc(t('Tap the game it plays the most - its main game. Then tap a few others it plays now and then.'))}</p>
      ${s.lib.length ? tutGameChips((id) => id === s.main ? 'on' : (s.sides.has(id) ? 'side' : ''), 'tutPickGame', [s.main, ...sides].filter(Boolean))
        : `<p class="muted small">${esc(t("Its game list didn't load - add games by their store link below."))}</p>`}
      <div style="display:flex;gap:6px;margin:6px 0 12px"><input id="tutAddGame" type="text" placeholder="${esc(t('Not in the list? Paste a store link or game ID'))}" style="flex:1">
        <button class="ghost" onclick="tutAddGame()">${esc(t('Add'))}</button></div>
      <div class="rows small">
        <div class="row"><span class="muted">${esc(t('Main game'))}</span><b>${esc(s.main ? gameLabel(s.main) : t('tap one above'))}</b></div>
        <div class="row"><span class="muted">${esc(t('Side games'))}</span><span>${esc(sides.length ? sides.map(gameLabel).join(', ') : t('none - that is fine'))}</span></div>
      </div>
      <label class="tut-range"><span id="tutPct">${esc(tf('Time on the main game: {0}%', s.pct))}</span>
        <input type="range" min="40" max="100" step="5" value="${s.pct}" oninput="tutSetup.pct=+this.value;$('tutPct').textContent=tf('Time on the main game: {0}%', this.value)"></label>
      <p class="muted small">${esc(t('The side games share the rest. You can fine-tune all of this later under Settings.'))}</p>
      <p id="tutError" class="error"></p>`;
  } else if (step === 'routine') {
    title = t('Its daily routine');
    const def = (n) => (schema && schema.Bot || []).find((d) => d.Name === n);
    const field = (n, min, max) => {
      const d = def(n);
      return `<label for="tut-${n}">${esc(d ? tSetting(d, 'label') : n)}${d ? tipIcon(tSetting(d, 'tip')) : ''}</label>
        <input id="tut-${n}" type="number" min="${min}" max="${max}" value="${s.routine[n]}" oninput="tutSetup.routine['${n}']=+this.value;tutRoutinePreview()">`;
    };
    body = `<p>${esc(t('Roughly how a person would use this account. Every day comes out a bit different around these numbers.'))}</p>
      <div class="form2 tut-form2">
        ${field('WeekdayHours', 0, 20)}${field('WeekendHours', 0, 20)}${field('DayStartHour', 0, 23)}${field('BedHour', 0, 23)}${field('DayOffChancePct', 0, 90)}
      </div>
      <p id="tutRoutinePreview" class="muted small" style="margin-top:12px"></p>`;
  } else {
    title = t('What should it idle?');
    body = `<p>${esc(t('It farms trading cards first. Once they are done, it plays these games to add hours. Tap any you like - or none.'))}</p>
      ${s.lib.length ? tutGameChips((id) => s.idle.has(id) ? 'on' : '', 'tutPickIdle', [...s.idle]) : ''}
      <div style="display:flex;gap:6px;margin:6px 0 12px"><input id="tutAddGame" type="text" placeholder="${esc(t('Not in the list? Paste a store link or game ID'))}" style="flex:1">
        <button class="ghost" onclick="tutAddGame()">${esc(t('Add'))}</button></div>
      <div class="tut-field"><label for="tut-custom">${esc(t('Show friends a custom game name (optional)'))}</label>
      <input id="tut-custom" type="text" value="${esc(s.customName)}" placeholder="${esc(t('leave empty to show the real game'))}" oninput="tutSetup.customName=this.value"></div>`;
  }

  tutShow({
    key: 'setup-' + step, at: tutBarSteps - 1,
    kicker: tf('SETTING UP {0}', s.name.toUpperCase()), title, body,
    back: s.at > 0 ? 'tutDir=-1;tutSetup.at--;renderTutorialSetup()' : '',
    skip: 'tutSkipSetup()', skipLabel: t('Skip - use the defaults'),
    next: last ? t('Save') : t('Next'),
  }, tutSetupNext);

  if (step === 'routine') tutRoutinePreview();
  const add = $('tutAddGame');
  if (add) add.onkeydown = (e) => { if (e.key === 'Enter') tutAddGame(); };
}

function tutRoutinePreview() {
  const r = tutSetup.routine;
  const el = $('tutRoutinePreview');
  if (el) el.textContent = tf('About {0}h on weekdays and {1}h at weekends, from around {2}:00 until about {3}:00, with a day off {4}% of the time.',
    r.WeekdayHours, r.WeekendHours, String(r.DayStartHour).padStart(2, '0'), String(r.BedHour).padStart(2, '0'), r.DayOffChancePct);
}

/// First tap is the main game; tapping the main again clears it; any other tap adds or removes a side game.
function tutPickGame(id) {
  const s = tutSetup;
  if (s.main === id) { s.main = 0; } else if (!s.main && !s.sides.has(id)) { s.main = id; } else if (s.sides.has(id)) { s.sides.delete(id); } else { s.sides.add(id); }
  renderTutorialSetup();
}

function tutPickIdle(id) {
  if (tutSetup.idle.has(id)) tutSetup.idle.delete(id); else tutSetup.idle.add(id);
  renderTutorialSetup();
}

/// A store link or a bare number, added as if tapped in the list - and its name looked up from Steam.
async function tutAddGame() {
  const input = $('tutAddGame');
  const m = input && input.value.match(/(?:app\/)?(\d{2,8})/);
  if (!m) return;
  const id = +m[1];
  if (tutSetup.steps[tutSetup.at] === 'idle') tutSetup.idle.add(id); else tutPickGame(id);
  await learnNames([id]);
  renderTutorialSetup();
}

async function tutSetupNext() {
  const s = tutSetup;
  if (s.steps[s.at] === 'games' && !s.main) {
    $('tutError').textContent = t('Tap its main game first.');
    return;
  }
  if (s.at < s.steps.length - 1) { s.at++; renderTutorialSetup(); return; }
  await tutSaveSetup();
}

async function tutSaveSetup() {
  const s = tutSetup;
  const btn = $('tutNext');
  if (btn) btn.disabled = true;
  // No answer: the button came back, not left greyed out with nothing said.
  try { await loadConfig(); } catch { if (btn) btn.disabled = false; toast(t("Couldn't save those settings"), true); return; }   // a partial body resets the whole account, so start from what it has
  const base = config.Bots[s.name];
  if (!base) { tutSkipSetup(); return; }
  const changes = {};

  if (s.steps.includes('games')) {
    // "730:70, 440, 570" - the main game's share, the side games splitting what's left.
    changes.GameWeights = [`${s.main}:${s.pct}`, ...[...s.sides]].join(', ');
  }
  if (s.steps.includes('routine')) Object.assign(changes, s.routine);
  if (s.steps.includes('idle')) {
    changes.IdleGames = [...s.idle];
    if (s.customName.trim()) { changes.CustomGameName = s.customName.trim(); changes.CustomGameNameEnabled = true; }
  }

  const res = await postBot(s.name, { ...base, ...changes }).catch(() => null);
  if (!res || !res.ok) toast((res && res.error) || t("Couldn't save those settings"), true);
  tutShowDone();
}

function tutSkipSetup() { tutShowDone(); }

/// The last screen: what it's going to do now, in a sentence or two.
function tutShowDone() {
  if (tutImportQueue.length) {
    tutSetup = null;
    tutNextImported();
    return;
  }

  const s = tutSetup;
  const b = (state && state.Bots || []).find((x) => x.Name === (s && s.name));
  tutSetup = null;
  let what;

  if (b && b.Legit) {
    what = s && s.main
      ? tf('It will mostly play {0}{1}, with breaks, meals and a bedtime. First it settles in for a few minutes, like somebody who just opened Steam.',
        gameLabel(s.main), s.sides.size ? tf(', and {0} now and then', [...s.sides].map(gameLabel).join(', ')) : '')
      : t('Human mode is on: it settles in for a few minutes first, like somebody who just opened Steam, then starts its day - games, breaks and a bedtime.');
  } else {
    what = b && b.Cards > 0
      ? tf('It is farming trading cards: {0} to go across {1} games. When they are done it idles your games.', b.Cards, b.Games)
      : t('It is checking which of your games still have cards to drop - that takes a minute. Then it farms them, and idles your games after that.');
  }

  tutShow({
    key: 'done', at: tutBarSteps, kicker: t('Done'),
    title: t("You're all set"),
    badge: true,
    body: `<p>${esc(tf('{0} is signed in.', b ? b.Name : ''))} ${esc(what)}</p>
      <p class="muted small">${esc(t('You can close this window - nocat.farm keeps running by the clock (in the tray). The Accounts page shows what each account is doing, and that is where you add another one.'))}</p>
      ${tutImportOthers.length ? `<p class="muted small">${esc(tf('The other {0} imported account(s) are added but not started yet.', tutImportOthers.length))}</p>` : ''}
      ${tutDash && tutDash.info && tutDash.info.NeedsRestart ? `<div class="tut-note warn"><b>${esc(t('One more thing: restart nocat.farm'))}</b>
        <span>${esc(t('It starts listening for your phone the next time it starts. Close it and open it again once.'))}</span></div>` : ''}
      ${tutSwitch('tut-dpres', config && config.Global && config.Global.DiscordPresence, 'tutDiscordPresence(this.checked)',
        t('Show on my Discord profile'), t('Your Discord shows Playing nocat.farm while it is open, like a game. You can change this any time under Settings, Discord profile.'))}`,
    skip: tutImportOthers.length ? 'tutStartOthers()' : '', skipLabel: t('Start the others too'),
    next: t('Show me my account'),
  }, finishSignin);
}

// The first-run question for the Discord card: saved the moment it's ticked, nothing else to press.
async function tutDiscordPresence(on) {
  await loadConfig();
  if (!config || !config.Global) return;
  config.Global.DiscordPresence = on;
  await postGlobal(config.Global).catch(() => {});
}

async function tutStartOthers() {
  const names = tutImportOthers;
  tutImportOthers = [];
  for (const n of names) await post('/api/bots/' + encodeURIComponent(n) + '/start', {}).catch(() => {});
  toast(tf('Starting {0} more account(s) - they sign in one after another.', names.length));
  finishSignin();
}

/// A config name from a Steam login - letters, numbers, dashes and underscores - that isn't taken yet.
function nameFromLogin(login) {
  let base = (login || '').replace(/[^A-Za-z0-9_-]/g, '').slice(0, 32) || 'account';
  if (base.toLowerCase() === 'nocatfarm') base += '1';
  const taken = new Set(Object.keys((config && config.Bots) || {}).map((n) => n.toLowerCase()));
  let name = base;
  for (let i = 2; taken.has(name.toLowerCase()); i++) name = base + i;
  return name;
}

// The account the setup is walking through signing in, and what it last drew - redrawn only when something
// changed, so a half-typed Steam Guard code isn't wiped by the next poll.
let tutorialSignin = null;
// The account the walkthrough added, kept past the sign-in screens - Advanced setup opens its settings at the end.
let tutorialAdded = null;
let tutorialSigninHtml = '';

function renderSignin() {
  if (!tutorialSignin || !state) return;

  const b = (state.Bots || []).find((x) => x.Name === tutorialSignin);
  const qr = (state.QrWaiting || []).find((q) => q.Name === tutorialSignin);
  let title;
  let body;
  let key;
  let next = null;
  let act = null;
  let skip = '';
  let skipLabel = '';

  // Signed in: now its library is known, so the setup can offer the account's own games instead of asking for IDs.
  // A human-mode account always gets the games step - without games it has nothing to play. The routine, and a
  // robot's idle list, are the full tour's.
  if (b && b.Online && !tutorialSetupDone) {
    tutorialSetupDone = true;
    const adv = tutorialMode === 'advanced';
    const steps = b.Legit ? (adv ? ['games', 'routine'] : ['games']) : (adv ? ['idle'] : []);
    if (steps.length) {
      startTutorialSetup(b.Name, steps);
      return;
    }
  }

  if (b && b.Online) {
    title = t("You're all set");
    const what = b.Legit
      ? t('Human mode is on: it settles in for a few minutes first, like somebody who just opened Steam, then starts its day - games, breaks and a bedtime.')
      : (b.Cards > 0
        ? tf('It is farming trading cards: {0} to go across {1} games. When they are done it idles your games.', b.Cards, b.Games)
        : t('It is checking which of your games still have cards to drop - that takes a minute. Then it farms them, and idles your games after that.'));
    body = `<p>${esc(tf('{0} is signed in.', b.Name))} ${esc(what)}</p>
      <p class="muted small">${esc(t('You can close this window - nocat.farm keeps running by the clock (in the tray). The Accounts page shows what each account is doing, and that is where you add another one.'))}</p>`;
    key = 'signin-done';
    next = t('Show me my account');
    act = finishSignin;
  } else if (b && b.Group === 'problem') {
    title = t("Couldn't sign in");
    body = `<div class="tut-note bad"><span>${esc(b.Detail || '')}</span></div>
      <p class="muted small">${esc(t('Check the account name and password on the Accounts page, then press Start on it to try again.'))}</p>`;
    key = 'signin-problem';
    // One imported account failing mustn't strand the rest of them.
    next = tutImportQueue.length ? t('Next account') : t('Go to Accounts');
    act = tutImportQueue.length ? tutNextImported : finishSignin;
  } else if (qr) {
    title = t('Scan the code');
    body = `<p>${esc(t('Open the Steam app on your phone, tap the Steam Guard tab and scan this.'))}</p>
      <div class="tut-steamqr"><img src="/api/bots/${encodeURIComponent(qr.Name)}/qr.svg?v=${qr.QrVersion}" alt="QR code" width="200" height="200"></div>`;
    key = 'signin-qr';
    skip = 'finishSignin()';
    skipLabel = t('Hide this');
  } else if (state.Prompt) {
    title = state.PromptSecret ? t('Your Steam password') : t('Steam Guard code');
    body = `<p>${esc(state.Prompt)}</p>
      <p class="muted small">${esc(state.PromptSecret
        ? t('Asked once. After this it remembers the account with a login token, not the password.')
        : t('Open the Steam app on your phone (Steam Guard tab), or check your email, and type the code here.'))}</p>
      <input id="tutPrompt" class="tut-code" type="${state.PromptSecret ? 'password' : 'text'}" autocomplete="off" spellcheck="false">`;
    key = 'signin-prompt';
    next = t('Continue');
    act = sendTutorialPrompt;
  } else {
    title = t('Signing in to Steam');
    body = `<div class="tut-spin" aria-hidden="true"></div><p class="muted small">${esc(t('This takes a few seconds...'))}</p>`;
    key = 'signin-wait';
    skip = 'finishSignin()';
    skipLabel = t('Hide this');
  }

  const html = tutFrame({ key, at: key === 'signin-done' ? tutBarSteps : tutBarSteps - 1, kicker: tf('{0} · Steam', tutorialSignin),
    badge: key === 'signin-done', title, body, skip, skipLabel, next });
  if (html === tutorialSigninHtml) return;
  tutorialSigninHtml = html;
  tutPaint(key, html);
  if (act) $('tutNext').onclick = act;

  const input = $('tutPrompt');
  if (input) {
    input.onkeydown = (e) => { if (e.key === 'Enter') sendTutorialPrompt(); };
    setTimeout(() => input.focus(), 50);
  }
}

function sendTutorialPrompt() {
  const input = $('tutPrompt');
  if (!input || !input.value) return;
  const value = input.value;
  input.value = '';
  tutorialSigninHtml = '';   // redraw on the next poll even if the question looks the same
  post('/api/prompt', { Value: value }).then((res) => {
    if (!res.ok) toast(t('Nothing was waiting for that answer'), true);
    refresh();
  });
}

async function finishSignin() {
  const name = tutorialSignin || tutorialAdded;
  tutorialSignin = null;
  tutorialSigninHtml = '';
  await closeTutorial();

  // Advanced setup ends where somebody who knows idlers wants to be: every setting of the new account, the
  // advanced ones showing.
  if (tutorialMode === 'advanced') await loadConfig().catch(() => {});
  if ((tutorialMode === 'advanced') && name && config && config.Bots && config.Bots[name]) {
    try { await go('settings'); } catch { go('accounts'); return; }
    $('setAdvanced').checked = true;
    selectSettings(name);
    return;
  }

  go('accounts');
}

/// After an import from the walkthrough. Accounts brought over in human mode need games before human mode has anything
/// to play, and games are picked from the account's own library - which needs it signed in. So: check the other idler
/// is out of the way, sign those in, and walk each through the same steps as an account added by hand.
async function tutorialAfterImport(res, toolName) {
  // Settings only (SingleBoostr), or nothing new: the walkthrough still ends on adding an account.
  if (!res.Imported) {
    toast((res.Notes || []).slice(-1)[0] || t('Nothing was imported'));
    tutorialManual = true;
    renderTutorial();
    return;
  }

  const human = res.Human || [];

  // Nothing in human mode: nothing to set up, the import summary is the end of it.
  if (!human.length) {
    await closeTutorial();
    await afterImport(res);
    return;
  }

  sessionStorage.setItem('skip-welcome', '1');
  post('/api/tutorial/done', {}).catch(() => {});
  if (config && config.Global) config.Global.TutorialDone = true;
  $('importBanner').classList.add('hidden');
  await loadConfig();
  tutImportQueue = human.filter((n) => config.Bots[n]);
  tutImportOthers = (res.Names || []).filter((n) => config.Bots[n] && !tutImportQueue.includes(n));
  tutShow({
    key: 'import-close', at: tutBarSteps - 1, kicker: t('Import from another idler'),
    title: tf('Close {0} first', toolName),
    lead: esc(tf('Next it signs in the {0} you play on, so it can set them up from their own games.', tutImportQueue.length === 1 ? t('one account') : tf('{0} accounts', tutImportQueue.length))),
    body: `<div class="tut-note warn"><span>${esc(tf('If {0} is still running, close it now - two programs on one account keep signing each other out.', toolName))}</span></div>`,
    skip: `tutImportQueue=[];closeTutorial();afterImport(${esc(JSON.stringify({ Imported: res.Imported, Notes: res.Notes || [] }))})`,
    skipLabel: t('Not now'),
    next: t("It's closed - sign them in"),
  }, tutImportSignIn);
}

// Imported accounts ticked as human mode, still to set up; and the rest, added but not started.
let tutImportQueue = [];
let tutImportOthers = [];

async function tutImportSignIn() {
  const btn = $('tutNext');
  if (btn) btn.disabled = true;
  for (const n of tutImportQueue) await post('/api/bots/' + encodeURIComponent(n) + '/start', {}).catch(() => {});
  tutNextImported();
}

/// The next imported account through sign-in and set-up - or, when none are left, the last screen.
function tutNextImported() {
  const next = tutImportQueue.shift();
  if (!next) return false;
  tutorialSignin = next;
  tutorialSigninHtml = '';
  tutorialSetupDone = false;
  refresh().then(renderSignin);
  return true;
}

async function closeTutorial() {
  tutorialOpen = false;
  tutorialSignin = null;
  closeModal();
  // The installer's "coming from" was a one-time hint - shown now, whether it was used or not.
  if (imp && imp.tutorial) imp = null;
  post('/api/import/pending/clear', {}).catch(() => {});
  // Marked done however it was dismissed - being shown it again after skipping is worse than never seeing it.
  await post('/api/tutorial/done', {}).catch(() => {});
  if (config && config.Global) config.Global.TutorialDone = true;
}

// help, /help and /? open the reference rather than dumping 60 lines into the output pane, where it pushes
// everything you were reading off the top and cannot be scrolled independently.
function helpModal(filter) {
  modal(`
    <h2>${esc(t('Commands'))}</h2>
    <input type="text" id="helpFilter" autocomplete="off" spellcheck="false" placeholder="${esc(t('filter…'))}"
           oninput="helpFilterList(this.value)" value="${esc(filter || '')}">
    <div class="helplist" id="helpList">${helpListHtml(filter)}</div>
    <div class="actions"><button class="ghost" onclick="closeModal()">${esc(t('Close'))}</button></div>`);

  const f = $('helpFilter');
  if (f) { f.focus(); f.setSelectionRange(f.value.length, f.value.length); }
}

// Typing in the filter redraws the list and nothing else. Rebuilding the whole modal per keystroke replaced the box
// being typed in: the caret jumped to the end and a Japanese or Chinese input method lost the word mid-compose.
function helpFilterList(filter) {
  const list = $('helpList');
  if (list) list.innerHTML = helpListHtml(filter);
}

function helpListHtml(filter) {
  const q = (filter || '').trim().toLowerCase();
  const groups = {};

  (commands || []).forEach((c) => {
    if (q && !(c.Name + ' ' + c.Args + ' ' + c.Help).toLowerCase().includes(q)) return;
    (groups[c.Group] = groups[c.Group] || []).push(c);
  });

  return Object.keys(groups).length
    ? Object.keys(groups).map((g) => `<div class="grp">${esc(t(g))}</div>${groups[g].map((c) => `
        <div class="c" onclick="useCommand(${esc(JSON.stringify(c.Name))})">
          <code>${esc(c.Display || c.Name)}${c.Args ? ' ' + esc(c.Args) : ''}</code>
          <span class="h">${esc(c.Help)}</span>
        </div>`).join('')}`).join('')
    : `<p class="muted">${esc(t('Nothing matches.'))}</p>`;
}

function modal(html) { $('modalCard').innerHTML = html; $('modal').classList.remove('hidden', 'tut'); }

// ── the Phone page ───────────────────────────────────────────────────
// Everything about opening the dashboard on a phone, on one page: the home link and the away link, each with a code
// to scan; a checklist that ticks itself as things get fixed, with the fix beside each; Telegram and Discord; and what
// it means for safety. It reads /api/phone every few seconds while it's open, so a tick flips the moment a fix lands.
let phone = { info: null, at: 0, loading: false, pw: '', pwShow: false, pwOpen: false, restarting: false, painted: {} };

async function loadPhone(force) {
  if (phone.loading || (!force && Date.now() - phone.at < 4000)) return;
  phone.loading = true;
  try {
    const p = await api('/api/phone').catch(() => null);
    if (p) { phone.info = p; phone.at = Date.now(); }
  } finally { phone.loading = false; }
  if (view === 'phone') renderPhone();
}

/// paint() compares against the browser's own serialising of the markup, which never matches ours exactly (checked=""),
/// so it redrew every few seconds - under a finger about to tap a switch. This remembers what it last drew instead.
function phPaint(id, html) {
  const el = $(id);
  if (!el || phone.painted[id] === html) return;
  phone.painted[id] = html;
  el.innerHTML = html;
}

const phIcon = (name) => `<span class="ph-ico" aria-hidden="true">${tutIcon(name)}</span>`;
const phTick = (ok) => `<span class="ph-tick ${ok ? 'ok' : ''}" aria-hidden="true">${ok
  ? '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>'
  : ''}</span>`;
const phPill = (kind, text) => `<span class="ph-pill ${kind}">${esc(text)}</span>`;
const phCopy = (url) => `<button class="ghost ph-copy" onclick="copyText(${esc(JSON.stringify(url))}, t('Link copied'))">${esc(t('Copy'))}</button>`;

/// The parts of the page, from one look at /api/phone. Each is painted on its own, so a change in one doesn't redraw
/// the others - and the password box isn't redrawn while somebody is typing in it.
function renderPhone() {
  const p = phone.info;
  if (!p) { phPaint('phoneTop', `<p class="muted empty">${esc(t('Loading…'))}</p>`); return; }

  const homeReady = p.OpenAtHome && p.Home.length > 0 && !p.NeedsRestart && !p.FirewallBlocks && !p.NeedsHomeAddress;
  const awayReady = !!p.Outside && homeReady;
  const items = phoneChecklist(p);
  // The router only counts once away from home is wanted - home alone is a whole setup, not 3 of 4.
  const counted = items.filter((i) => !i.optional || p.RemoteOn || p.Outside);
  const done = counted.filter((i) => i.ok).length;

  phPaint('phoneTop', `<div class="ph-hero">
      <div><h2 class="ph-title">${esc(t('Use it from your phone'))}</h2>
      <p class="ph-lead">${esc(t('Scan a code and this dashboard opens on your phone - at home on your Wi-Fi, or anywhere on mobile data.'))}</p></div>
      <div class="ph-score ${done === counted.length ? 'all' : ''}"><b>${done}<i>/${counted.length}</i></b><span>${esc(t('ready'))}</span></div>
    </div>`);
  phPaint('phoneHome', phoneHomeCard(p, homeReady));
  phPaint('phoneAway', phoneAwayCard(p, awayReady));
  $('phoneHome').classList.toggle('ready', homeReady);
  $('phoneAway').classList.toggle('ready', awayReady);

  const pw = $('phPw');
  if (!(pw && document.activeElement === pw)) {
    phPaint('phoneReady', `<h2>${esc(t('Ready?'))}</h2><div class="ph-list">${items.map(phoneRow).join('')}</div>`);
    const again = $('phPw');
    if (again) again.value = phone.pw;
  }

  phPaint('phoneChat', phoneChatCard());
  phPaint('phoneSafe', phoneSafeCard(p));
}

function phoneHomeCard(p, ready) {
  const head = `<div class="ph-head">${phIcon('home')}<div><div class="ph-kicker">${esc(t('At home'))}</div>
      <div class="ph-name">${esc(t('On the same Wi-Fi'))}</div></div>
      ${ready ? phPill('ok', t('Ready')) : phPill('', t('Not ready yet'))}</div>`;

  // In Docker the addresses nocat.farm can see are Docker's own - showing one would send the phone nowhere.
  if (p.NeedsHomeAddress) {
    return `${head}<p class="ph-text">${esc(t("From inside Docker nocat.farm can't see this computer's address on your wifi. On your phone, open this computer's address with the port Docker publishes - or put it in NOCATFARM_HOME_ADDRESS in docker-compose.yml, and this shows the link and a code to scan."))}</p>
      <pre class="ph-code">environment:\n  - NOCATFARM_HOME_ADDRESS=192.168.1.50</pre>`;
  }

  const link = p.Home[0];
  const others = p.Home.slice(1);
  const qr = p.Qr && ready ? `<div class="ph-qr">${p.Qr}</div>`
    : `<div class="ph-qr off">${p.Qr || ''}<div class="ph-qrcover"><b>${esc(t('Not ready yet'))}</b><span>${esc(t('Finish the list below and the code shows here.'))}</span></div></div>`;

  return `${head}<div class="ph-body">${qr}
      <div class="ph-side">
        ${link ? `<div class="ph-link"><span class="ph-url">${esc(link)}</span>${phCopy(link)}</div>` : ''}
        <p class="ph-text">${esc(ready ? t('Scan this with your phone\'s camera while it is on the same wifi, then sign in with the dashboard password.')
          : t('This is the address your phone will open.'))}</p>
        ${others.length ? `<p class="muted small">${esc(t('Other addresses of this PC:'))} ${others.map((h) => `<span class="ph-mono">${esc(h)}</span>`).join(' · ')}</p>` : ''}
      </div></div>
    <p class="ph-note">${esc(tf('Sign in once and the phone stays signed in for {0} days.', (config && config.Global && config.Global.WebSessionDays) || 7))}</p>`;
}

function phoneAwayCard(p, ready) {
  const setting = tSetting({ Name: 'WebRemoteAccess', Label: 'Open from anywhere' }, 'label');
  const asking = p.RemoteOn && !p.Outside && !p.RemoteProblem;
  const pill = p.Outside ? (ready ? phPill('ok', t('Working')) : phPill('', t('Not ready yet')))
    : p.RemoteOn && p.RemoteProblem ? phPill('bad', t('Not working'))
    : asking ? phPill('wait', t('Asking...'))
    : phPill('', t('Off'));
  const head = `<div class="ph-head">${phIcon('globe')}<div><div class="ph-kicker">${esc(t('Away from home'))}</div>
      <div class="ph-name">${esc(t('Anywhere, on mobile data'))}</div></div>${pill}</div>`;

  // A switch that can't be turned on says why, instead of saving something the server will only refuse.
  const canTurnOn = p.LongPassword;
  const toggle = p.ManualOutside && !p.RemoteOn ? '' : `<label class="ph-switch" for="phRemote">
      <span><b>${esc(setting)}</b><span class="muted small">${esc(!p.RemoteOn && !canTurnOn
        ? tf('Needs a dashboard password of at least {0} characters - set one below.', p.MinPassword)
        : t('Your router passes the dashboard through to this PC. No other app needed.'))}</span></span>
      <span class="switch"><input type="checkbox" id="phRemote" ${p.RemoteOn ? 'checked' : ''} ${!p.RemoteOn && !canTurnOn ? 'disabled' : ''}
        onchange="phoneRemote(this.checked, this)"><span></span></span></label>`;

  let body;
  if (p.Outside) {
    const qr = p.QrOutside && ready ? `<div class="ph-qr">${p.QrOutside}</div>`
      : `<div class="ph-qr off">${p.QrOutside || ''}<div class="ph-qrcover"><b>${esc(t('Not ready yet'))}</b><span>${esc(t('Finish the list below and the code shows here.'))}</span></div></div>`;
    body = `<div class="ph-body">${qr}<div class="ph-side">
        <div class="ph-link"><span class="ph-url">${esc(p.Outside)}</span>${phCopy(p.Outside)}</div>
        <p class="ph-text">${esc(t('Scan this on mobile data, then sign in with the dashboard password.'))}</p>
        ${p.ManualOutside ? `<p class="muted small">${esc(t('Set by hand in Public address (Settings).'))}</p>` : ''}
      </div></div>`;
  } else if (p.RemoteOn && p.RemoteProblem) {
    body = `<div class="ph-state bad">${esc(p.RemoteProblem)}</div>`;
  } else if (asking) {
    body = `<div class="ph-state"><span class="ph-spin" aria-hidden="true"></span>${esc(t('Asking your router to forward the port...'))}</div>`;
  } else {
    body = `<div class="ph-state">${esc(t('Off. Only your home Wi-Fi can open it.'))}</div>`;
  }

  return `${head}${toggle}${body}
    <p class="ph-note">${esc(t("Test it with Wi-Fi off on your phone, on mobile data. From inside your home your own internet address often won't open - that is the router, not nocat.farm."))}</p>`;
}

/// The checklist: each thing a phone needs, ticked or not, and the fix beside it. Firewall only on Windows, the
/// computer's address only in Docker, and the router last - it's only for away from home.
function phoneChecklist(p) {
  const items = [];

  // The password: never shown, only replaced. A box to set it, with the same strength hint as the walkthrough.
  const pwState = !p.HasPassword ? t('Not set. Your phone signs in with it.')
    : p.LongPassword ? t('Set, and long enough for away from home.')
    : tf('Set. Away from home needs {0} or more characters.', p.MinPassword);
  const pwBox = !p.HasPassword || phone.pwOpen ? `<div class="ph-pwrow">
      <input id="phPw" type="${phone.pwShow ? 'text' : 'password'}" autocomplete="new-password" spellcheck="false"
        placeholder="${esc(t('New password'))}" oninput="phonePwInput(this)" onkeydown="if(event.key==='Enter')phoneSavePw()">
      <button class="ghost" onclick="phonePwToggle()">${esc(phone.pwShow ? t('Hide') : t('Show'))}</button>
      <button class="ph-go" onclick="phoneSavePw()">${esc(t('Save'))}</button>
      ${p.HasPassword ? `<button class="ghost" onclick="phone.pwOpen=false;phone.pw='';renderPhone()">${esc(t('Cancel'))}</button>` : ''}
    </div><div class="tut-meterrow" id="phMeter">${phoneMeter(p)}</div>`
    : `<button class="ghost" onclick="phone.pwOpen=true;renderPhone();setTimeout(()=>$('phPw')&&$('phPw').focus())">${esc(t('Change'))}</button>`;
  items.push({ ok: p.HasPassword, name: tSetting({ Name: 'WebPassword', Label: 'Dashboard password' }, 'label'), text: pwState, fix: pwBox });

  // Open to other devices: the switch saves 0.0.0.0 (or 127.0.0.1), and the restart makes it real.
  const listens = p.ListensBeyondThisPc;
  const lockedOn = listens && !p.OnThisPc;   // switching it off from the phone would lock the phone out
  const openText = p.RestartProblem ? p.RestartProblem
    : p.NeedsRestart ? t('Saved - restart the dashboard to use it.')
    : listens ? t('On. Phones and PCs on your Wi-Fi can open it.')
    : !p.HasPassword ? t('Set a password first.')
    : t('Off. Only this PC can open the dashboard.');
  const openFix = `<span class="switch" ${lockedOn ? `data-tip="${esc(t('Switch it off on the PC itself.'))}"` : ''}><input type="checkbox" id="phOpen" aria-label="${esc(t('Open to other devices'))}"
      ${listens ? 'checked' : ''} ${(!listens && !p.HasPassword) || lockedOn || phone.restarting ? 'disabled' : ''} onchange="phoneOpen(this.checked, this)"><span></span></span>
    ${p.NeedsRestart ? `<button class="ph-go" ${phone.restarting ? 'disabled' : ''} onclick="phoneRestart(this)">${esc(phone.restarting ? t('Restarting the dashboard...') : t('Restart the dashboard now'))}</button>
      <span class="muted small">${esc(t('Your accounts stay signed in.'))}</span>` : ''}`;
  items.push({ ok: listens && !p.NeedsRestart, name: t('Open to other devices'), text: openText, fix: openFix, bad: !!p.RestartProblem });

  if (p.InDocker) {
    items.push({ ok: !p.NeedsHomeAddress, name: t("This computer's address"),
      text: p.NeedsHomeAddress ? t("set NOCATFARM_HOME_ADDRESS in docker-compose.yml to this computer's address")
        : t('Set with NOCATFARM_HOME_ADDRESS.'), fix: '' });
  } else if (p.Windows) {
    const fwOk = listens && !p.FirewallBlocks;
    items.push({ ok: fwOk, name: t('Windows Firewall'),
      text: !listens ? t('Checked once it is open to other devices.')
        : p.FirewallBlocks ? t('Your phone will just keep loading until Windows lets it in. This allows the dashboard only, only on home networks - Windows asks you to confirm.')
        : t('Lets it in.'),
      fix: listens && p.FirewallBlocks ? (p.OnThisPc ? `<button class="ph-go" onclick="allowFirewall(this)">${esc(t('Allow through Windows Firewall'))}</button>`
        : `<span class="muted small">${esc(t('Press it on the PC nocat.farm runs on.'))}</span>`) : '' });
  }

  const routerOk = !!p.Outside;
  items.push({ ok: routerOk, name: t('Router (away from home only)'),
    text: p.Outside ? (p.ManualOutside ? t('Set by hand in Public address (Settings).') : t('Forwarding the port.'))
      : p.RemoteOn && p.RemoteProblem ? p.RemoteProblem
      : p.RemoteOn ? t('Asking your router to forward the port...')
      : t('Only needed away from home. Turn on Open from anywhere above.'),
    fix: '', bad: p.RemoteOn && !!p.RemoteProblem && !p.Outside, optional: true });

  return items;
}

function phoneRow(i) {
  return `<div class="ph-row ${i.ok ? 'ok' : ''} ${i.bad ? 'bad' : ''}">${phTick(i.ok)}
    <div class="ph-rowtext"><b>${esc(i.name)}</b><span>${esc(i.text)}</span>${i.fix ? `<div class="ph-fix">${i.fix}</div>` : ''}</div></div>`;
}

function phoneMeter(p) {
  const pw = phone.pw;
  const score = tutPwScore(pw);
  const words = ['', t('Too short'), t('Weak'), t('Okay'), t('Strong')];
  const hint = !pw ? '' : pw.length < 8 ? tf('Use at least {0} characters.', 8)
    : pw.length < p.MinPassword ? `${words[score]} - ${tf('Open from anywhere needs at least {0} characters.', p.MinPassword)}`
    : words[score];
  return `<div class="tut-meter s${score}" aria-hidden="true"><i></i><i></i><i></i><i></i></div><span class="muted small">${esc(hint)}</span>`;
}

function phonePwInput(el) {
  phone.pw = el.value;
  const m = $('phMeter');
  if (m && phone.info) m.innerHTML = phoneMeter(phone.info);
}

function phonePwToggle() {
  phone.pwShow = !phone.pwShow;
  renderPhone();
}

async function phoneSavePw() {
  const pw = phone.pw;
  if (!pw || pw.length < 8) { toast(tf('Use at least {0} characters.', 8), true); return; }
  // Enter pressed twice: the second save went out under the session the first had just ended.
  if (phone.savingPw) return;
  phone.savingPw = true;
  const res = await tutSaveGlobal({ WebPassword: pw }).finally(() => { phone.savingPw = false; });
  if (!res) return;
  phone.pw = '';
  phone.pwOpen = false;
  if ($('phPw')) $('phPw').value = '';
  if (document.activeElement) document.activeElement.blur();
  toast(t('Saved.'));
  loadPhone(true);
}

async function phoneOpen(on, el) {
  el.disabled = true;
  const ok = await tutSaveGlobal({ WebHost: on ? '0.0.0.0' : '127.0.0.1' });
  if (!ok) el.checked = !on;
  el.disabled = false;
  loadPhone(true);
}

async function phoneRemote(on, el) {
  el.disabled = true;
  const ok = await tutSaveGlobal({ WebRemoteAccess: on });
  if (!ok) el.checked = !on;
  // As phoneOpen does: a save that failed changed nothing, so the page may not be redrawn - and the switch stayed dead.
  el.disabled = false;
  loadPhone(true);
}

/// "Restart the dashboard now": the server answers, then stops and starts listening again a moment later. Same port:
/// wait for it to answer again. A new port is a new address, so the page goes there.
async function phoneRestart(btn, after) {
  if (btn) btn.disabled = true;
  const r = await api('/api/phone/restart', { method: 'POST' }).catch(() => null);
  if (!r || !r.Ok) { toast(t("That didn't work"), true); if (btn) btn.disabled = false; return; }
  phone.restarting = true;
  if (view === 'phone') renderPhone();
  toast(t('Restarting the dashboard...'));

  const here = Number(location.port || (location.protocol === 'https:' ? 443 : 80));
  if (r.Needed && r.Port && r.Port !== here) {
    setTimeout(() => { location.href = `${location.protocol}//${location.hostname}:${r.Port}/#phone`; }, 2500);
    return;
  }

  await new Promise((ok) => setTimeout(ok, 1500));
  let back = false;
  for (let i = 0; i < 24 && !back; i++) {
    back = await fetch('/api/ping', { cache: 'no-store' }).then((x) => x.ok).catch(() => false);
    if (!back) await new Promise((ok) => setTimeout(ok, 750));
  }
  phone.restarting = false;
  await loadPhone(true);
  const p = phone.info;
  if (!back) toast(t("It didn't come back. Check the nocat.farm window."), true);
  else if (p && p.RestartProblem) toast(p.RestartProblem, true);
  else toast(t('The dashboard is back.'));
  if (after) after();
}

async function allowFirewall(btn) {
  btn.disabled = true; btn.textContent = t('Check the Windows prompt…');
  const r = await api('/api/phone/firewall', { method: 'POST' }).catch(() => null);
  if (r && r.Ok) toast(t('Done - your phone can open it now.'));
  else { toast((r && r.Error) || t('That didn\'t work'), true); btn.disabled = false; btn.textContent = t('Allow through Windows Firewall'); }
  loadPhone(true);
}

/// Telegram and Discord: the phone's other way in, from anywhere and with no router involved. The connect parts are
/// the same ones Settings shows, so there's one flow, not two.
function phoneChatCard() {
  const secrets = (config && config.GlobalSecretsSet) || [];
  const g = (config && config.Global) || {};
  const acct = (state && state.Bots && state.Bots[0] && state.Bots[0].Name) || 'myaccount';

  const tgSet = secrets.includes('TelegramBotToken');
  const tgOn = !!(state && state.TelegramConnected);
  const tgBody = !tgSet ? `<button class="ghost" onclick="goSetting('TelegramBotToken')">${esc(t('Set it up'))}</button>`
    : tgOn && g.TelegramCommands === false ? `<span class="muted small">${esc(t('Commands are off. Turn on Take commands from Telegram.'))}</span>
      <button class="ghost" onclick="goSetting('TelegramCommands')">${esc(t('Set it up'))}</button>`
    : (state && (state.TelegramConnectLink || state.TelegramConnected)) ? telegramConnect()
    : `<span class="muted small">${esc(t('Checking the bot token...'))}</span>`;

  const dcSet = !!(state && state.DiscordBotSet);
  const dcOn = !!(state && state.DiscordConnected && state.DiscordBotOnline);
  const dcBody = !dcSet ? `<button class="ghost" onclick="goSetting('DiscordBotToken')">${esc(t('Set it up'))}</button>` : discordConnect();

  const box = (name, on, set, body) => `<div class="ph-svc">
      <div class="ph-svchead"><b>${esc(name)}</b>${on ? phPill('ok', t('Connected')) : phPill('', set ? t('Not connected') : t('Not set up'))}</div>
      <div class="ph-svcbody">${body}</div></div>`;

  const cmds = [['/status', t('What every account is doing')], ['/cards', t('Cards left to farm')],
    [`/pause ${acct}`, t('Stop playing for a while, stay signed in')], [`/resume ${acct}`, t('Undo a pause')],
    ['/dashboard', t('Open the dashboard on your phone')], ['/help', t('Every command')]];

  return `<h2>${esc(t('Control it from your phone'))}</h2>
    <p class="ph-text">${esc(t('Telegram and Discord work from anywhere, with no router setup. /dashboard sends you the links above.'))}</p>
    <div class="ph-svcs">${box('Telegram', tgOn, tgSet, tgBody)}${box('Discord', dcOn, dcSet, dcBody)}</div>
    <div class="ph-kicker" style="margin-top:16px">${esc(t('Handy commands'))}</div>
    <div class="ph-cmds">${cmds.map(([c, d]) => `<div><code>${esc(c)}</code><span>${esc(d)}</span></div>`).join('')}</div>
    <p class="muted small">${tf('In Discord, the rest go through /nocat, like {0}.', `<code>/nocat pause ${esc(acct)}</code>`)}</p>`;
}

function phoneSafeCard(p) {
  return `<h2>${esc(t('Keep it safe'))}</h2>
    <ul class="ph-safe">
      <li>${esc(t('Anyone with the away-from-home link reaches the sign-in page, and nothing more without the password.'))}</li>
      <li>${esc(t('Five wrong passwords lock that address out for an hour.'))}</li>
      <li>${esc(t("It's plain http, not encrypted - use a password you don't use anywhere else."))}</li>
    </ul>
    ${p.RemoteOn ? `<button class="ghost ph-off" onclick="phoneRemote(false, this)">${esc(t('Turn Open from anywhere off'))}</button>`
      : `<p class="muted small">${esc(t('Off. Only your home Wi-Fi can open it.'))}</p>`}`;
}

function closeModal() { $('modal').classList.add('hidden'); $('modal').classList.remove('tut'); }
// Escape and a click outside close whatever is open - and for the walkthrough, that counts as Skip. Before, it
// closed the dialog without marking it seen, so it came back on the next reload.
function dismissModal() { if (tutorialOpen) closeTutorial(); else closeModal(); }
$('modal').addEventListener('click', (e) => { if (e.target.id === 'modal') dismissModal(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') dismissModal(); });

// Enter moves the walkthrough on, like its main button. A box with its own Enter (a game to add, the Steam Guard code,
// a folder to look in) keeps it; a focused card or language picks itself, the way a click would; and a button or a
// link does what it does anyway.
document.addEventListener('keydown', (e) => {
  if ((e.key !== 'Enter') || e.isComposing || !tutorialOpen || $('modal').classList.contains('hidden')) return;
  const el = e.target;
  if (el && el.getAttribute && (el.getAttribute('role') === 'button')) { e.preventDefault(); el.click(); return; }
  if (el && (['BUTTON', 'A', 'TEXTAREA', 'SELECT', 'SUMMARY'].includes(el.tagName) || el.onkeydown)) return;
  const next = $('tutNext');
  if (next && !next.disabled) { e.preventDefault(); next.click(); }
});

// One delegated listener instead of interpolating account names into inline onclick attributes. HTML-escaping
// an apostrophe as &#39; does NOT help there: the parser decodes it back to ' before the JS is parsed, so a
// name like  o'brien  broke the handler outright. Data attributes never get parsed as code.
document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-act]');
  if (!el) return;
  const name = el.dataset.bot;
  switch (el.dataset.act) {
    case 'start': case 'stop': case 'pause': case 'resume': act(name, el.dataset.act); break;
    case 'cards': openBot(name); break;
    case 'settings': closeModal(); selectSettings(name); go('settings'); break;
    case 'remove': removeBot(name); break;
    case 'postnow': postNow(name); break;
    case 'register': registerProfile(name); break;
    case 'inventory': refreshInventory(name); break;
  }
});

// Same reason as above, for the Authenticator's account picker and the log's click-a-name-to-filter.
document.addEventListener('click', (e) => {
  const pick = e.target.closest('[data-auth-pick]');
  if (pick) { authPickAccount(pick.dataset.authPick); return; }
  const source = e.target.closest('[data-log-source]');
  if (source) { $('logBot').value = source.dataset.logSource; renderLog(); return; }
  const jump = e.target.closest('[data-jump]');
  if (jump) jumpTo(jump.dataset.jump);
});

document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-task]');
  if (!el) return;
  el.disabled = true;
  el.textContent = t('posting…');
  post('/api/rep4rep/tasks/' + encodeURIComponent(el.dataset.task) + '/post?bot=' + encodeURIComponent($('r4rBot').value))
    .then((res) => { toast(res.message || res.error, !res.ok); loadTasks(); refresh(); })
    // No answer at all: the button stood on "posting…", greyed out, until the page was reloaded.
    .catch(() => { toast(t("That didn't work"), true); loadTasks(); });
});

document.addEventListener('change', (e) => {
  const el = e.target.closest('[data-qs]');
  if (el) quickSet(el.dataset.bot, el.dataset.qs, el.value);
});

// ── render: rep4rep ──────────────────────────────────────────────────
async function loadRep4Rep() {
  r4r = await api('/api/rep4rep').catch(() => null);
  renderRep4RepAccount();
  if (r4r && r4r.Connected) {
    r4rProfiles = await api('/api/rep4rep/profiles').catch(() => []);
    renderRep4RepProfiles();
    renderBotPicker();
    loadTasks();
  } else {
    $('r4rProfiles').innerHTML = `<p class="muted">${esc(t('Connect your rep4rep account first.'))}</p>`;
    $('r4rTasks').innerHTML = '';
  }
  renderRep4RepPacing();
}

function renderRep4RepAccount() {
  if (!r4r || !r4r.Connected) {
    $('r4rAccount').innerHTML = `
      <h2>${esc(t('Set up rep4rep'))}</h2>
      <p class="explain" style="margin-bottom:18px">
        ${tf("{0} your accounts post a comment on someone else's Steam profile → you earn points → you spend those points to get comments on {1} profile. nocat.farm does the posting for you, on a schedule that keeps every account under Steam's daily limit.",
          `<b>${esc(t('How it works:'))}</b>`, `<i>${esc(t('your'))}</i>`)}</p>

      <ol class="steps">
        <li>
          <b>${esc(t('Make a rep4rep account'))}</b>
          <div class="muted">${esc(t("Free, and it takes a minute. Sign up, then click the verification link in your email — the token below won't work until you do."))}</div>
          <div class="toolbar" style="margin:10px 0 0">
            <button onclick="window.open('https://rep4rep.com/?r=reap', '_blank', 'noopener')">${esc(t('Sign up for rep4rep ↗'))}</button>
          </div>
        </li>
        <li>
          <b>${esc(t('Copy your API token'))}</b>
          <div class="muted">${tf("It's on {0}. Copy the whole string.",
            '<a href="https://rep4rep.com/user/settings" target="_blank" rel="noopener">rep4rep.com → Settings ↗</a>')}</div>
          <div class="toolbar" style="margin:10px 0 0">
            <input id="r4rToken" type="password" placeholder="${esc(t('paste the token here'))}" autocomplete="off" style="max-width:340px"
                   onkeydown="if(event.key==='Enter')connectRep4Rep()">
            <button onclick="connectRep4Rep()">${esc(t('Connect'))}</button>
          </div>
          <p id="r4rError" class="error">${r4r && r4r.Error ? esc(r4r.Error) : ''}</p>
        </li>
        <li class="muted">
          <b>${esc(t("That's it"))}</b>
          <div>${esc(t("nocat.farm registers your Steam accounts with rep4rep by itself and starts posting. You don't have to add anything on their website."))}</div>
        </li>
      </ol>`;
    return;
  }

  const on = state ? state.Bots.filter((b) => b.Rep4Rep).length : 0;
  const total = state ? state.Bots.length : 0;

  $('r4rAccount').innerHTML = `
    <h2>${esc(t('Your rep4rep account'))}</h2>
    <div class="tiles">
      <div class="tile"><div class="n">${r4r.Points}</div><div class="k">${esc(t('Points you can spend'))}${tipIcon(t('Spend these on comments for your own Steam profile, over on rep4rep.com. Read fresh from rep4rep every time you open this tab, so it matches the site.'))}</div>
        ${r4r.SyncedAt ? `<div class="muted small">${esc(tf('as of {0}', new Date(r4r.SyncedAt).toLocaleTimeString()))}</div>` : ''}</div>
      <div class="tile"><div class="n">${r4r.Pending}</div><div class="k">${esc(t('Waiting to be verified'))}${tipIcon(t("Comments you've posted that rep4rep hasn't checked yet. They turn into spendable points on their own, usually within a few hours - nothing is lost and nothing is stuck."))}</div>
        <div class="sub">${esc(t('turns into points on its own'))}</div></div>
    </div>
    ${on === 0 && total > 0
      ? `<div class="alert warn" style="margin:4px 0 12px"><span>${esc(t("Connected, but commenting isn't switched on for any account yet."))}</span>
         <span class="spacer"></span><button onclick="enableAllRep4Rep()">${esc(tf('Turn it on for all {0}', total))}</button></div>`
      : `<p class="muted small">${tf('Posting from {0} of {1}.', `<b>${on}</b>`,
          total === 1 ? t('one account') : tf('{0} accounts', total))}</p>`}
    <div class="toolbar">
      <span class="muted small">${esc(tf('Token set · synced {0}', ago(r4r.SyncedAt)))}</span>
      <button class="ghost" onclick="loadRep4Rep()">${esc(t('Refresh'))}</button>
      <button class="ghost" onclick="replaceToken()">${esc(t('Replace token'))}</button>
      <a class="muted small" href="https://rep4rep.com/dashboard/" target="_blank" rel="noopener" style="margin-left:auto">${esc(t('Spend your points ↗'))}</a>
    </div>`;
}

async function enableAllRep4Rep() {
  await loadConfig();   // same reason as quickSet: a partial body resets the whole account
  const failed = [];
  for (const b of state.Bots) {
    if (b.Rep4Rep) continue;
    const base = config.Bots[b.Name];
    if (!base) continue;
    const res = await postBot(b.Name, { ...base, Rep4Rep: true }).catch(() => null);
    if (!res || !res.ok) failed.push(b.Name);
  }
  // "Every account" only when it's true - a save that was turned down was reported as switched on with the rest.
  if (failed.length) toast(tf("Couldn't switch it on for {0}", failed.join(', ')), true);
  else toast(t('rep4rep commenting switched on for every account'));
  await loadConfig();
  loadRep4Rep();
  refresh();
}

function replaceToken() { r4r = { Connected: false }; renderRep4RepAccount(); }

async function connectRep4Rep() {
  const res = await post('/api/rep4rep/token', { Token: $('r4rToken').value.trim() });
  if (!res.ok) { $('r4rError').textContent = res.error; return; }
  toast(t('rep4rep connected'));
  loadRep4Rep();
  refresh();
}

// The "Today" cell: count / cap, plus WHY it's stuck and WHEN it frees up - the two things the rolling window
// never made obvious.
function capCell(p) {
  let s = `${p.Today} / ${p.Cap}`;
  if (p.CapIsSteamLimit) {
    s += ` <span class="muted small" data-tip="${esc(t("This is the ceiling Steam enforced on this account, not the number you set - so raising the configured cap won't lift it."))}">${esc(t("(Steam's limit)"))}</span>`;
  }
  if (p.Today >= p.Cap && p.NextSlot) {
    s += ` <span class="muted small" data-tip="${esc(t('Rolling 24h window: the count drops as each comment ages past 24h. This is the soonest this account can post again.'))}">${esc(tf('· frees {0}', until(p.NextSlot)))}</span>`;
  }
  return s;
}

function renderRep4RepProfiles() {
  if (!r4rProfiles.length) { $('r4rProfiles').innerHTML = `<p class="muted">${esc(t('No accounts configured yet.'))}</p>`; return; }

  $('r4rProfiles').innerHTML = `<div class="tablewrap"><table>
    <tr><th>${esc(t('Account'))}</th><th>SteamID64</th><th data-tip="${esc(t("rep4rep's own id for this profile. It is not your SteamID - this is the one their support and their API ask for."))}">${esc(t('rep4rep ID'))}</th><th>${esc(t('Status'))}</th><th data-tip="${esc(t('Comments posted in the last ROLLING 24 hours - not a midnight reset. Each one ages off on its own 24h after it went out, so the count frees up gradually. Shown against the cap.'))}">${esc(t('Today'))}</th><th></th></tr>
    ${r4rProfiles.map((p) => `<tr>
      <td><b>${esc(p.Account)}</b></td>
      <td class="muted small">${esc(p.SteamId === '0' ? '—' : p.SteamId)}</td>
      <td class="muted small">${esc(p.Rep4RepId || '—')}</td>
      <td>${p.Registered
        ? (p.Enabled ? `<span class="pill good">${esc(t('Ready'))}</span>` : `<span class="pill">${esc(t('Commenting off'))}</span>`)
        : (p.Online ? `<span class="pill warn">${esc(t('Not on rep4rep'))}</span>` : `<span class="pill">${esc(t('Log in first'))}</span>`)}</td>
      <td>${p.Enabled ? capCell(p) : '—'}</td>
      <td>${p.Registered
        ? `<button class="ghost" data-tip="${esc(t('Skip the wait and post the next comment as soon as the daily cap allows. It never goes over the cap.'))}" data-act="postnow" data-bot="${esc(p.Account)}">${esc(t('Post now'))}</button>`
        : (p.Online ? `<button class="ghost" data-act="register" data-bot="${esc(p.Account)}">${esc(t('Register'))}</button>` : '')}</td>
      </tr>`).join('')}
    </table></div>
    <p class="muted small" style="margin-top:10px">${esc(t("This is rep4rep's cached copy of your Steam settings, not live. If you just changed something on Steam, it can take a while to show here."))}</p>`;
}

async function registerProfile(name) {
  const res = await post(`/api/rep4rep/profiles/${encodeURIComponent(name)}/register`);
  toast(res.ok ? tf('{0} registered with rep4rep', name) : res.error, !res.ok);
  loadRep4Rep();
}

async function postNow(name) {
  const res = await post('/api/bots/' + encodeURIComponent(name) + '/rep4repnow');
  toast(res.ok ? tf('{0}: will post as soon as the daily cap allows', name) : (res.error || t("That didn't work")), !res.ok);
}

function renderBotPicker() {
  const sel = $('r4rBot');
  const current = sel.value;
  sel.innerHTML = r4rProfiles.filter((p) => p.Registered).map((p) => `<option value="${esc(p.Account)}">${esc(p.Account)}</option>`).join('');
  if (current) sel.value = current;
}

async function loadTasks() {
  const bot = $('r4rBot').value;
  if (!bot) { $('r4rTasks').innerHTML = `<p class="muted">${esc(t('No registered accounts yet.'))}</p>`; return; }
  r4rTasks = await api('/api/rep4rep/tasks?bot=' + encodeURIComponent(bot)).catch(() => []);

  $('r4rTasks').innerHTML = r4rTasks.length
    ? `<p class="muted small">${esc(tf("{0} waiting. Every task is worth the same to rep4rep — their API doesn't rank them, so there's nothing to pick between.",
         r4rTasks.length === 1 ? t('One task') : tf('{0} tasks', r4rTasks.length)))}</p>
       <div class="tablewrap"><table>
       <tr><th>${esc(t('Target'))}</th><th>${esc(t('Comment to post'))}</th><th></th></tr>
       ${r4rTasks.map((task) => `<tr>
         <td><a href="https://steamcommunity.com/profiles/${esc(task.TargetSteamId)}" target="_blank" rel="noopener">${esc(task.TargetName)} ↗</a></td>
         <td><code>${esc(task.Comment)}</code></td>
         <td><button class="ghost small" data-tip="${esc(t('Post this one right now instead of waiting for the schedule. It still counts against the daily cap.'))}" data-task="${esc(task.TaskId)}">${esc(t('Post now'))}</button></td>
         </tr>`).join('')}
       </table></div>
       <p class="muted small" style="margin-top:10px">${esc(t('rep4rep checks the text matches exactly, so these are posted character for character.'))}</p>`
    : `<p class="muted">${esc(t('No tasks for this account right now. rep4rep hands them out in batches — check back later.'))}</p>`;
}

// The markup the pacing table was last built from. Compared against rather than the element's innerHTML, which the
// browser re-serialises and so never matches what was generated.
let r4rPacingHtml = '';

function renderRep4RepPacing() {
  if (!state) return;
  const box = $('r4rPacing');
  // This runs on every poll, and rebuilding the table threw away a number half-typed into it - the box reset under
  // your fingers every few seconds. Leave it alone while one of its boxes has the focus; the next poll catches up.
  if (box.contains(document.activeElement) && document.activeElement.tagName === 'INPUT') return;

  const keys = ['Rep4RepDailyCap', 'Rep4RepGapMinMinutes', 'Rep4RepGapMaxMinutes', 'Rep4RepStartHour', 'Rep4RepEndHour'];
  const defs = keys.map((k) => (schema ? schema.Bot.find((d) => d.Name === k) : null));

  const html = `<div class="tablewrap"><table>
    <tr><th>${esc(t('Account'))}</th>${defs.map((d, i) => `<th${d ? ` data-tip="${esc(tSetting(d, 'tip'))}"` : ''}>${esc(d ? tSetting(d, 'label') : keys[i])}</th>`).join('')}</tr>
    ${state.Bots.map((b) => `<tr>
      <td><b>${esc(b.Name)}</b></td>
      ${keys.map((k) => `<td><input type="number" style="max-width:80px" value="${config && config.Bots[b.Name] ? config.Bots[b.Name][k] : ''}"
        data-qs="${k}" data-bot="${esc(b.Name)}"></td>`).join('')}
      </tr>`).join('')}
    </table></div>`;

  if (html === r4rPacingHtml && box.innerHTML) return;
  r4rPacingHtml = html;
  box.innerHTML = html;
}

// Goes through the typed endpoint rather than the command line, so an account name with a space in it can't
// turn into a different command.
async function quickSet(bot, key, value) {
  // Reload first. Spreading an undefined config (an account added since the page last loaded) produced a
  // body with ONE key, and the server materialised every other field as its default - wiping the Steam login.
  // And always, not only when the account is missing: the copy from when the page opened put back anything changed
  // since - '/set kylro Rep4Rep off' from Telegram, then a new gap here, and rep4rep was on again.
  await loadConfig().catch(() => {});
  const base = config && config.Bots[bot];
  if (!base) { toast(t('That account is not loaded yet - try again in a second'), true); return; }

  const cfg = { ...base };
  cfg[key] = Number(value) || 0;
  const res = await postBot(bot, cfg);
  // Refused: put the saved number back in the box rather than leave the rejected one looking accepted.
  if (!res.ok) { toast(res.error || t('Could not save that'), true); r4rPacingHtml = ''; renderRep4RepPacing(); return; }
  toast((res.Adjusted && res.Adjusted.length) ? res.Adjusted[0] : tf('{0}: saved', bot), !!(res.Adjusted && res.Adjusted.length));
  await loadConfig();
  // Forced: a number the server pulled back into range can leave the saved config - and so the markup - unchanged,
  // and the box would keep showing what was typed.
  r4rPacingHtml = '';
  renderRep4RepPacing();
}

// ── render: log ──────────────────────────────────────────────────────
function renderLog() {
  const q = ($('logSearch').value || '').toLowerCase();
  const who = $('logBot').value;

  $('logLevels').innerHTML = Object.keys(logLevels).map((l) =>
    `<span class="chip ${logLevels[l] ? 'on' : ''}" onclick="logLevels['${l}']=!logLevels['${l}'];renderLog()">${esc(t(l === 'GOOD' ? 'Good' : l[0] + l.slice(1).toLowerCase()))}</span>`).join('');

  const sources = [...new Set(logLines.map((l) => l.Source))].sort();
  if ($('logBot').options.length !== sources.length + 1) {
    $('logBot').innerHTML = `<option value="">${esc(t('All accounts'))}</option>` + sources.map((s) => `<option>${esc(s)}</option>`).join('');
    $('logBot').value = who;
  }

  const rows = logLines.filter((l) => logLevels[l.Level] !== false && (!who || l.Source === who) &&
    (!q || l.Text.toLowerCase().includes(q) || l.Source.toLowerCase().includes(q)));

  const body = $('logBody');
  const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 60;

  body.innerHTML = rows.length
    ? rows.map((l) => `<div class="l ${esc(l.Level)}"><span class="t">${esc(l.Time)}</span><span class="s"${sourceStyle(l.Source)} data-log-source="${esc(l.Source)}">${esc(l.Source)}</span><span class="m">${highlight(l.Text, q)}</span></div>`).join('')
    : `<p class="muted">${esc(t('Nothing matches.'))}</p>`;

  if (atBottom && $('logFollow').checked) body.scrollTop = body.scrollHeight;
}

// The colour an account chose for itself, straight from the palette the server sent. Index 0 is "automatic",
// which means leave the stylesheet alone.
function sourceStyle(source) {
  const g = (config && config.Global) || {};
  const choice = config && config.Bots && config.Bots[source] ? config.Bots[source].LogColour
    : source === 'telegram' ? g.TelegramLogColour
    : source === 'discord' ? g.DiscordLogColour
    : 0;
  const css = choice > 0 && schema && schema.NameColours ? schema.NameColours[choice] : null;
  return css ? ` style="color:${css}"` : '';
}

function highlight(text, q) {
  if (!q) return esc(text);
  const i = text.toLowerCase().indexOf(q);
  if (i < 0) return esc(text);
  return esc(text.slice(0, i)) + '<mark>' + esc(text.slice(i, i + q.length)) + '</mark>' + esc(text.slice(i + q.length));
}

function copyLog() {
  copyText(logLines.map((l) => `${l.Time} ${l.Source} ${l.Text}`).join('\n'), t('Log copied'));
}

// ── render: console ──────────────────────────────────────────────────
function renderOut() {
  const out = $('out');
  const atBottom = out.scrollHeight - out.scrollTop - out.clientHeight < 60;
  out.innerHTML = localLines.join('');
  if (atBottom) out.scrollTop = out.scrollHeight;
}

function pushLocal(html) {
  localLines.push(html);
  if (localLines.length > 200) localLines = localLines.slice(-200);
  renderOut();
  $('out').scrollTop = $('out').scrollHeight;
}

async function run(line) {
  pushLocal(`<div><span class="echo">&gt; ${esc(line)}</span></div>`);
  // help is a reference, not output. /? and /help work too, because both are what people try.
  const asHelp = line.trim().replace(/^\//, '').toLowerCase();

  if (asHelp === 'help' || asHelp === '?' || asHelp === 'h') {
    helpModal('');
    return '';
  }

  // 'help <word>' opens the reference filtered to matching commands - unless the word is a setting, or matches
  // no command, and then it goes to the server, which explains settings. It used to always filter, so
  // 'help HoursUntilCardDrops' - what the tutorial tells people to type - answered "Nothing matches."
  if (asHelp.startsWith('help ')) {
    const q = asHelp.slice(5).trim();

    // The settings list is only fetched once the Settings tab has been opened - fetch it here if it hasn't been,
    // or a setting whose name happens to appear in some command's help text would be filtered instead.
    if (!schema) schema = await api('/api/settings/schema').catch(() => null);

    const defs = schema ? [...(schema.Global || []), ...(schema.Bot || [])] : [];
    const isSetting = defs.some((d) => d.Name.toLowerCase() === q);
    const isCommand = (commands || []).some((c) => (c.Name + ' ' + c.Args + ' ' + c.Help).toLowerCase().includes(q));

    if (!isSetting && isCommand) {
      helpModal(q);
      return '';
    }
  }

  // '/help x' is the same request as 'help x'; the server doesn't strip the slash, so send it without one.
  // 'clear' is this screen's own business: the Log tab and this console empty here, and the nocat.farm window keeps
  // its lines (and the other way round). Nothing goes to the server.
  if (asHelp === 'clear' || asHelp === 'cls') {
    logClearedAt = logLines.length ? logLines[logLines.length - 1].Seq : logClearedAt;
    logLines = [];
    localLines = [];
    renderOut();
    if (view === 'log') renderLog();
    return '';
  }

  const res = await post('/api/command', { Line: asHelp.startsWith('help ') ? line.trim().replace(/^\//, '') : line });
  if (res.output) pushLocal(`<div class="reply">${esc(res.output)}</div>`);
  refresh();
  return res.output;
}

function sendCommand(e) {
  e.preventDefault();
  const input = $('cmd');
  const line = input.value.trim();
  if (!line) return false;
  history.unshift(line);
  history = history.slice(0, 100);
  localStorage.setItem('nocatfarm-history', JSON.stringify(history));
  historyAt = -1;
  input.value = '';
  run(line);
  return false;
}

function renderCommandList() {
  if (!commands.length) return;
  const groups = [...new Set(commands.map((c) => c.Group))];
  $('cmdList').innerHTML = groups.map((g) =>
    `<div class="grp">${esc(t(g))}</div>` + commands.filter((c) => c.Group === g).map((c) =>
      `<div class="c" onclick="useCommand(${esc(JSON.stringify(c.Name))},${esc(JSON.stringify(c.Args))})"><code>${esc(c.Name)} ${esc(c.Args)}</code><span class="h">${esc(c.Help)}</span></div>`).join('')).join('');
}

function useCommand(name, args) {
  // Also reached from the help modal, which needs it to close the modal and jump to the console - the old
  // second definition (this one) shadowed the first via hoisting and did neither, so help-modal clicks silently
  // filled a box behind the overlay.
  closeModal();
  go('console');
  const input = $('cmd');
  if (input) { input.value = args ? name + ' ' : name; input.focus(); }
}

$('cmd').addEventListener('keydown', (e) => {
  const input = $('cmd');
  if (e.key === 'ArrowUp' && history.length) {
    historyAt = Math.min(historyAt + 1, history.length - 1);
    input.value = history[historyAt];
    e.preventDefault();
  } else if (e.key === 'ArrowDown') {
    historyAt = Math.max(historyAt - 1, -1);
    input.value = historyAt < 0 ? '' : history[historyAt];
    e.preventDefault();
  } else if (e.key === 'Tab') {
    e.preventDefault();
    const parts = input.value.split(' ');
    if (parts.length === 1) {
      const hit = commands.find((c) => c.Name.startsWith(parts[0]));
      if (hit) input.value = hit.Name + ' ';
    } else if (state) {
      const bot = state.Bots.map((b) => b.Name).find((n) => n.toLowerCase().startsWith(parts[parts.length - 1].toLowerCase()));
      if (bot) { parts[parts.length - 1] = bot; input.value = parts.join(' ') + ' '; }
    }
  }
});

// ── render: settings ─────────────────────────────────────────────────
async function loadConfig() {
  if (!schema) schema = await api('/api/settings/schema');
  config = await api('/api/config');
  // Kept apart from config, which the page edits in place. A save sends it along, so the server can tell a field
  // the page changed from one it merely had an old copy of - the Telegram chat connected a moment ago, say.
  configLoaded = config ? JSON.parse(JSON.stringify(config)) : null;
}

/// Save the global settings: the page's whole copy with its edits, plus the copy it loaded, so a field it didn't touch
/// can't put back an old value over one the app has written since.
function postGlobal(body) {
  const loaded = configLoaded && configLoaded.Global;
  return post('/api/config', loaded ? { ...body, Base: loaded } : body);
}

/// The same for one account - a game learned to be banned during a send isn't put back by a save a moment later.
function postBot(name, body) {
  const loaded = configLoaded && configLoaded.Bots && configLoaded.Bots[name];
  return post('/api/bots/' + encodeURIComponent(name) + '/config', loaded ? { ...body, Base: loaded } : body);
}

/// Switch the settings pane to an account (or null for Global). Returns false when the user chose to keep their
/// unsaved edits instead.
function selectSettings(name) {
  if (Object.keys(pending).length && !confirm(t('You have unsaved changes here. Discard them?'))) return false;

  // Only once the switch is certain. Started before the question, a "no, keep them" still pointed the pacer at
  // the account that wasn't opened, and the pane that stayed open lost its own.
  if (name) loadPacer(name); else { pacerFor = null; pacerRows = null; }

  settingsTarget = name == null ? GLOBAL : name;
  pending = {};
  renderSettings();
  return true;
}

// Matched on the data-section attribute, not the heading text: the heading is translated and the jump link
// carries the English name, so comparing what is on screen stopped finding anything in every language but one.
function jumpTo(section) {
  const find = () => document.querySelector(`#settingsBody .section h3[data-section="${CSS.escape(section)}"]`);
  let target = find();

  // A section made only of advanced settings isn't drawn while Show advanced is off (and a search or "only
  // changed" can hide one too), so the link used to do nothing. Show everything, then jump.
  if (!target) {
    $('setAdvanced').checked = true;
    $('setChanged').checked = false;
    $('setSearch').value = '';
    renderSettings();
    target = find();
  }

  if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function settingsDefs() {
  const all = settingsTarget === GLOBAL ? schema.Global : schema.Bot;
  if (r4rOn()) return all;

  // rep4rep is switched off: drop both its sections ("rep4rep account" and "rep4rep commenting") from the
  // settings entirely - jump links and all - but keep the master toggle itself so it can be turned back on.
  return all.filter((d) =>
    (d.Section !== 'rep4rep account' && d.Section !== 'rep4rep commenting') || d.Name === 'Rep4RepEnabled');
}
function settingsValues() { return settingsTarget === GLOBAL ? config.Global : config.Bots[settingsTarget]; }
function settingsDefaults() { return settingsTarget === GLOBAL ? schema.GlobalDefaults : schema.BotDefaults; }

function secretIsSet(name) {
  if (settingsTarget === GLOBAL) return (config.GlobalSecretsSet || []).includes(name);
  return ((config.BotSecretsSet || {})[settingsTarget] || []).includes(name);
}

// Start OR Stop, never both.
//
// The account cards already got this right; this header did not, and showed both regardless of state - so
// half the buttons on screen did nothing. Same shape as every other bug in this project: two places doing one
// job with only one of them careful. Now there is one function and both call it.
function botActions(name) {
  const bot = state && state.Bots ? state.Bots.find((b) => b.Name === name) : null;
  const stopped = !bot || bot.State === 'Stopped';

  return stopped
    ? `<button data-tip="${esc(t('Log this account in.'))}" data-act="start" data-bot="${esc(name)}">${esc(t('Start'))}</button>`
    : `<button class="ghost" data-tip="${esc(t('Log this account out. It stays configured.'))}" data-act="stop" data-bot="${esc(name)}">${esc(t('Stop'))}</button>`;
}

function renderSettings() {
  if (!schema || !config) return;

  if (settingsTarget !== GLOBAL && !config.Bots[settingsTarget]) { settingsTarget = GLOBAL; pending = {}; }

  // left pane: Global, then one entry per account
  $('settingsNavGlobal').innerHTML =
    `<div class="s ${settingsTarget === GLOBAL ? 'active' : ''}" onclick="selectSettings(null)">${esc(t('Global settings'))}</div>`;
  // Section jump-list for whatever pane is open. A 40-setting page without one is a scroll hunt.
  // Only sections this account can show at all: human mode hides the robot-only ones and the other way round,
  // and a link to a section that never appears is a link that does nothing.
  const jumpLegit = settingsTarget !== GLOBAL && !!liveValue('LegitMode', settingsValues());
  const sectionNames = [...new Set(settingsDefs()
    .filter((d) => !(d.Mode === 'rage' && jumpLegit) && !(d.Mode === 'legit' && !jumpLegit)
      && !(settingsTarget === GLOBAL && PANEL_ROWS.has(d.Name) && !PANEL_SECTIONS.has(d.Section)))
    .map((d) => d.Section))];
  const jump = `<div class="jump">${sectionNames.map((n) =>
    `<a class="j" data-jump="${esc(n)}">${esc(t(n))}</a>`).join('')}</div>`;

  $('settingsNavBots').innerHTML = Object.keys(config.Bots).length
    ? Object.keys(config.Bots).map((n) =>
        `<div class="s ${settingsTarget === n ? 'active' : ''}" data-bot="${esc(n)}" onclick="selectSettings(this.dataset.bot)">${esc(n)}</div>`).join('')
    : `<p class="muted small">${esc(t('No accounts yet.'))}</p>`;
  // Accounts and their settings from another idler, straight from where the accounts are listed.
  $('settingsNavBots').insertAdjacentHTML('beforeend',
    `<div class="s muted" onclick="openImport()">+ ${esc(t('Import from another idler'))}</div>`);

  $('settingsNavJump').innerHTML = jump;

  const defs = settingsDefs();
  const values = settingsValues();
  const defaults = settingsDefaults();
  const advanced = $('setAdvanced').checked;
  const onlyChanged = $('setChanged').checked;
  const q = ($('setSearch').value || '').toLowerCase();

  $('settingsHeader').innerHTML = settingsTarget === GLOBAL
    ? `<p class="muted small">${esc(t('These apply to nocat.farm as a whole.'))} <code>config/nocatFarm.json</code></p>`
    : `<div class="toolbar"><b>${esc(settingsTarget)}</b>
        <span class="muted small">config/${esc(settingsTarget)}.json</span>
        <span class="spacer"></span>
        ${botActions(settingsTarget)}
        <button class="danger" data-act="remove" data-bot="${esc(settingsTarget)}">${esc(t('Remove'))}</button></div>`;

  const sections = [...new Set(defs.map((d) => d.Section))];
  let html = '';
  let hiddenAdvanced = 0;

  for (const section of sections) {
    const legitOn = settingsTarget !== GLOBAL && !!liveValue('LegitMode', values);

    const fields = defs.filter((d) => {
      if (d.Section !== section) return false;
      // Human mode hides the settings that would give the account away, and the human-only settings stay out
      // of the way until it's switched on.
      if (d.Mode === 'rage' && legitOn) return false;
      if (d.Mode === 'legit' && !legitOn) return false;
      // The Discord profile panel and the Notifications chips have their own switch and chips for these - a
      // second row for each was the same setting twice.
      if (settingsTarget === GLOBAL && PANEL_ROWS.has(d.Name)) return false;
      if (!advanced && d.Advanced) { hiddenAdvanced++; return false; }
      if (q && !(tSetting(d, 'label').toLowerCase().includes(q) || d.Label.toLowerCase().includes(q)
        || d.Name.toLowerCase().includes(q) || tSetting(d, 'tip').toLowerCase().includes(q))) return false;
      if (onlyChanged && !isChanged(d, values, defaults)) return false;
      return true;
    });

    // A section whose rows are all drawn by its own panel (Discord profile) still shows, panel and all.
    const intro = sectionIntro(section, values);
    if (!fields.length && !(intro && PANEL_SECTIONS.has(section) && !q && !onlyChanged)) continue;
    html += `<div class="section"><h3 data-section="${esc(section)}">${esc(t(section))}</h3>${intro}${fields.map((d) => fieldHtml(d, values, defaults)).join('')}</div>`;
  }

  // Only on a real account, and only when nothing is being searched or filtered - it is not a setting and
  // has no business appearing in a filtered list of settings.
  // Behind "Show advanced" as well. Unlocking every achievement is not something to stumble across while
  // scrolling an account's ordinary settings, and anyone who needs it can find the switch.
  if (settingsTarget !== GLOBAL && !q && !onlyChanged && advanced) html += dangerZone(settingsTarget);

  if (!html) html = `<p class="muted">${esc(t('Nothing matches.'))}</p>`;
  if (hiddenAdvanced && !q) {
    const many = hiddenAdvanced === 1 ? t('One advanced setting is hidden.') : tf('{0} advanced settings are hidden.', hiddenAdvanced);
    html += `<p class="muted small" style="margin-top:16px">${esc(many)} ${esc(t('Tick “Show advanced” to see them.'))}</p>`;
  }

  $('settingsBody').innerHTML = html;
  updateSaveButton();
}

// A live "this is what will actually happen" panel for the sections where two settings combine and the result
// isn't obvious from either one on its own.
// What the pacer is actually doing, per game. Cached per account so switching panes does not re-fetch, and
// refreshed whenever settings are saved.
let pacerFor = null;
let pacerRows = null;
let pacerRecent = [];
let pacerHunt = null;

async function loadPacer(name) {
  if (pacerFor === name) return;
  pacerFor = name;
  pacerRows = null;

  try {
    const d = await api(`/api/bots/${encodeURIComponent(name)}/achievements`);
    // Another account picked while this was on its way: its answer is the one to keep, not this one.
    if (pacerFor !== name) return;
    pacerRows = d.Games || [];
    pacerRecent = d.Recent || [];
    pacerHunt = d.Hunt || null;
    // Resolve any "app 12345" names against Steam so the table reads with real game names; learnNames redraws
    // the settings pane (which this pacer lives in) once they land.
    learnNames(pacerRows.map((g) => g.App).filter(Boolean));
  } catch {
    if (pacerFor !== name) return;
    pacerRows = []; pacerRecent = []; pacerHunt = null;
  }

  if (view === 'settings' && settingsTarget === name) renderSettings();
}

// What the hunter is doing and what it will do next. This is the part that was missing: the table below says
// how the PACE works, and said nothing at all about which games are actually queued up.
function huntPanel() {
  const h = pacerHunt;
  if (!h || h.Mode === 'off') return '';

  const now = h.Now
    ? `<b>${esc(tf('Hunting {0}', h.Now))}</b>${h.Left ? ` <span class="muted">${esc(tf('- about {0} left', hm(h.Left)))}</span>` : ''}`
    : `<b>${esc(t('Standing by'))}</b> <span class="muted">- ${esc(h.Status || t('waiting'))}</span>`;

  const chip = (g) => `<span class="hchip${g.Shared ? ' shared' : ''}" data-tip="${esc(g.Shared ? t('Shared with this account by a Steam Family.') : t('Owned by this account.'))} ${esc(tf('{0} played.', hm(g.Minutes)))}">${esc(g.Game)}</span>`;

  const shown = (h.Next || []).slice(0, 6);
  const next = shown.map(chip).join('');
  const more = h.NextCount > shown.length ? `<span class="hchip ghost">${esc(tf('+{0} more', h.NextCount - shown.length))}</span>` : '';

  // Everything it ruled out is a COUNT, not a list. A family library leaves a thousand games out, and one
  // line saying why beats a wall of names nobody is going to read. The counts are worked out server-side over
  // the WHOLE list, so they always add up to the total.
  const shownOut = (h.OutReasons || []).map((r) => `${r.Count} ${t(r.Why)}`).join(' · ');
  const outCount = h.OutCount === 1 ? t('one game') : tf('{0} games', h.OutCount);
  const outLine = h.OutCount > 0
    ? `<div class="huntrow"><span class="k">${esc(t('Left out'))}</span><span class="muted small">${esc(outCount)}${shownOut ? ' — ' + esc(shownOut) : ''}</span></div>`
    : '';

  return `<div class="hunt">
    <div class="huntnow">${now}<span class="spacer"></span><span class="muted small">${esc(t(h.Mode))}</span></div>
    <div class="huntrow"><span class="k">${esc(t('Next up'))}</span><div class="chips">${next || `<span class="muted small">${esc(t('nothing queued'))}</span>`}${more}</div></div>
    ${outLine}
  </div>`;
}

// The last few that actually popped. Cheap to render, and it is the only place that shows the thing working.
function recentUnlocks() {
  if (!pacerRecent || !pacerRecent.length) return '';

  const rows = pacerRecent.map((u) => {
    const mins = Math.max(0, Math.round((Date.now() - new Date(u.When).getTime()) / 60000));
    const rarity = u.Percent == null ? '' : `<span class="rare">${Number(u.Percent).toFixed(1)}%</span>`;
    const when = mins < 1 ? t('just now') : tf('{0} ago', hm(mins));
    return `<li><b>${esc(u.Name)}</b> ${rarity}<span class="muted"> ${esc(GAME_NAMES[u.App] || u.Game)} · ${u.Unlocked}/${u.Total} · ${esc(when)}</span></li>`;
  }).join('');

  return `<ul class="unlocks">${rows}</ul>`;
}

// Why the pacer leaves a game out. These arrive from the server as data, so they are spelled out here as literal
// t() calls - that keeps them translating live when the language changes, and lets the translation check see them.
const pacerWhy = (why) => ({
  'on your never list': t('on your never list'),
  'the main game - left alone': t('the main game - left alone'),
  'not on your allow list': t('not on your allow list'),
  'not enough hours yet': t('not enough hours yet'),
}[why] || t(why));

function pacerTable() {
  if (pacerRows === null) return `<p class="muted small">${esc(t('Reading what it has done so far…'))}</p>`;
  if (!pacerRows.length) return `<p class="muted small empty">${esc(t('Nothing tracked yet. It starts counting the first minute a game is running.'))}</p>`;

  // One line, not a grid.
  //
  // A game only ever earns while it is being PLAYED, so a table listing sixty-odd owned games was sixty-odd rows
  // of "not read yet / when it next plays" wrapped around the single row that meant anything. The one fact worth
  // stating is which game is earning right now and how far along it is; everything else is answered by `cheevo`.
  const running = pacerRows.filter((g) => g.Running);
  const live = running.find((g) => !g.Blocked);

  if (!live) {
    // Playing something the settings leave out is not the same as playing nothing, and saying "nothing is
    // earning - a game only earns while it is being played" to an account that is visibly playing reads as broken.
    const out = running.find((g) => g.Blocked);

    return out
      ? `<p class="muted small">${tf('Playing {0} — left out: {1}.', `<b>${esc(GAME_NAMES[out.App] || out.Game)}</b>`, esc(pacerWhy(out.Why)))}</p>`
      : `<p class="muted small">${esc(t('Nothing is earning right now — a game only earns while this account is playing it.'))}</p>`;
  }

  const name = `<b>${esc(GAME_NAMES[live.App] || live.Game)}</b>`;
  const hrs = live.PlayedMinutes >= 60 ? (live.PlayedMinutes / 60).toFixed(1) + t('h') : live.PlayedMinutes + t('m');
  const done = esc(tf('{0} of {1} done', live.Unlocked, live.Total));

  // "Earning" only when there is something left to earn.
  //
  // Every running game used to be "Earning in X", whether it had fifty to go, had hit the ceiling, was already
  // finished, or had no achievements at all - so an account playing only finished games looked broken rather
  // than done. The server says which of those it is, from the last time it actually read Steam.
  switch (live.State) {
    case 'None':
      return `<p class="muted small">${tf('Playing {0} — it has no achievements to earn.', name)}</p>`;
    case 'Complete':
      return `<p class="muted small">${tf('Playing {0} — every achievement is already done ({1}).', name, `${live.Unlocked}/${live.Total}`)}</p>`;
    case 'SteamOnly':
      return `<p class="muted small">${tf('Playing {0} — {1}; the rest can only be awarded by Steam itself.', name, done)}</p>`;
    case 'Capped': {
      const setting = esc(tSetting({ Name: 'AchievementMaxCompletionPct', Label: 'Finish no more than' }, 'label'));
      return `<p class="muted small">${tf('Playing {0} — {1}, which is your {2}% ceiling, so it has stopped here. Raise “{3}” to let it carry on.', name, done, live.CeilingPercent, setting)}</p>`;
    }
    case 'NeedsHours':
      return `<p class="earning">${tf('Earning in {0} — {1} played, {2}. The next ones need more hours in it first.', name, hrs, done)}</p>`;
  }

  const progress = live.Total > 0 ? done : esc(t('reading what it has so far'));

  return `<p class="earning">${tf('Earning in {0} — {1} played, {2}.', name, hrs, progress)}</p>`;
}

async function openLogFolder() {
  const r = await post('/api/logs/open', {}).catch(() => null);
  if (!r || !r.Path) { toast(t('File logging is off - turn on "Write a log file" below.'), true); return; }
  toast(r.Opened ? tf('Opened {0}', r.Path) : tf('The log files are in {0} on the PC nocat.farm runs on.', r.Path));
}

function sectionIntro(section, values) {
  const val = (k) => (pending[k] !== undefined ? pending[k] : values[k]);

  // What gets sent to Discord / Telegram, as chips to tap rather than nine more rows - and a test button, so you
  // know it works before the first card drops.
  if (section === 'Notifications' && settingsTarget === GLOBAL) {
    const kinds = [['SendCardDrops', 'Card drops'], ['SendFreeStuff', 'Free stuff'], ['SendTrades', 'Trades'],
      ['SendProblems', 'Needs you'], ['SendUpdates', 'Updates'], ['SendInstalls', 'Install progress'], ['SendDailySummary', 'Daily summary'],
      ['SendComments', 'Profile comments'], ['SendAchievements', 'Achievements'], ['SendRep4Rep', 'rep4rep']];
    const setUp = (config.GlobalSecretsSet || []).includes('DiscordWebhookUrl') || (config.GlobalSecretsSet || []).includes('TelegramBotToken');
    return `<div class="explain">
      <b>${esc(t('Get notified on Discord or Telegram'))}</b>
      <p style="margin:6px 0 10px">${esc(t('Paste a Discord webhook link or a Telegram bot token below, save, then pick what gets sent. Busy moments are bundled into one message.'))}</p>
      <div class="langpick">${kinds.map(([k, label]) =>
        `<span class="p ${val(k) ? 'on' : ''}" onclick="editAndRender('${k}', ${!val(k)})">${esc(t(label))}</span>`).join('')}</div>
      <button class="ghost" ${setUp ? '' : 'disabled'} onclick="notifyTest(this)">${esc(t('Send a test message'))}</button>
      ${setUp ? '' : `<span class="muted small" style="margin-left:8px">${esc(t('Save a webhook link or bot token first.'))}</span>`}
      ${telegramConnect()}
      ${discordConnect()}
      ${notifyGuides()}
    </div>`;
  }

  if (section === 'Discord profile' && settingsTarget === GLOBAL) {
    return discordCardIntro(val);
  }

  // Where the files are, one click away - the log on screen is only the last few hundred lines.
  if (section === 'Logging' && settingsTarget === GLOBAL) {
    return `<div class="explain"><b>${esc(t('Log files'))}</b>
      <p style="margin:6px 0 10px">${esc(t('Every line goes in a file per day, kept for the days set below. clear (in the window or the Console) only clears that screen - the files keep everything.'))}</p>
      <button class="ghost" onclick="openLogFolder()">${esc(t('Open the log folder'))}</button></div>`;
  }

  if (section === 'Dashboard' && settingsTarget === GLOBAL) {
    return `<div class="explain"><b>${esc(t('Open on your phone'))}</b>
      <p style="margin:6px 0 10px">${esc(t('Use the dashboard from your phone or another PC on the same wifi - a QR code to scan, or the two settings that open it up.'))}</p>
      <button class="ghost" onclick="go('phone')">${esc(t('Open on your phone'))}</button></div>`;
  }

  // Achievements are the one area where the settings alone tell you nothing useful. Three dials and an
  // appID list do not convey that this thing refuses to unlock anything the hours cannot justify - which is
  // the entire reason to trust it - so the section says so in words.
  if (section === 'Achievements') {
    if (!val('UnlockAchievements')) {
      return `<div class="explain">${esc(t('Off. This account earns no achievements at all - nothing is written to any game. Turn it on and it starts earning them at a pace that follows the hours actually put in.'))}</div>`;
    }

    const pace = ['at twice the normal spacing', '', 'at half the normal spacing'][val('AchievementPace') ?? 1] || '';
    const cap = val('AchievementMaxCompletionPct');

    return `<div class="explain">
      <b>${esc(pace
        ? tf('Earns achievements slowly, from real playtime, {0}.', t(pace))
        : t('Earns achievements slowly, from real playtime.'))}</b>
      <p style="margin:8px 0 0">${esc(t('Unlocking a pile of achievements the second it logs in is what gives a bot away. This drips them out the way a real player would instead - a few at a time, only the common ones, paced to the hours actually put in.'))}
      ${esc(tf('It stops at {0}% of any one game, and never unlocks a milestone before the achievements it is a milestone of.', cap || 90))}</p>
    </div>
    ${huntPanel()}
    ${recentUnlocks()}
    ${pacerTable()}`;
  }

  if (section === 'What it plays') {
    const name = (val('CustomGameName') || '').trim();

    // In human mode the games come from the weighted rotation, not from a list here. Saying "nothing set yet"
    // when a full schedule is running was simply wrong.
    if (val('LegitMode')) {
      const top = parseWeights(val('GameWeights'))[0];
      const main = top ? gameLabel(top.game) : t('the games you list under Human mode');

      return `<div class="preview"><span class="k">${esc(t('Right now'))}</span>
        ${tf('Human mode picks what it plays — mostly {0}, one game at a time.', `<b>${esc(main)}</b>`)}
        ${esc(t('Your friends see the real game.'))}
        </div>`;
    }

    const games = val('IdleGames') || [];
    const black = val('BlacklistedGames') || [];
    const playing = games.filter((g) => !black.includes(g));

    const shownName = `<b>${esc(name)}</b>`;
    const list = `<b>${esc(playing.map(gameLabel).join(', '))}</b>`;
    let line;
    if (name && playing.length) {
      line = tf('Your friends see {0}. Underneath, {1} keep gaining real playtime — both at the same time.', shownName, list);
    } else if (name) {
      line = tf('Your friends see {0}. No games are listed, so no playtime is being banked — add some below if you want hours too.', shownName);
    } else if (playing.length) {
      line = tf('{0} gain playtime, all at once. Set a name below to show something else instead while the hours still count.', list);
    } else {
      line = esc(t('Nothing set yet. Add games to bank playtime, and optionally a name to display instead of the real one.'));
    }

    return `<div class="preview"><span class="k">${esc(t('Right now'))}</span>${line}</div>`;
  }

  if (section === 'Human mode') {
    if (!val('LegitMode')) {
      return `<div class="preview"><span class="k">${esc(t('Off'))}</span>
        ${esc(t('This account idles everything at once, around the clock. Switch Human mode on to play one game at a time on a believable schedule — the settings that would give it away are hidden and put back if you switch it off.'))}</div>`;
    }

    const w = parseWeights(val('GameWeights'));
    const from = val('DayStartHour'), to = val('BedHour');
    // The scheduler reads WeekdayHours/WeekendHours. This used to show DailyHoursTarget, which was retired —
    // so the preview quoted a number nothing used and that no control on the page could move.
    const weekday = val('WeekdayHours'), weekend = val('WeekendHours');
    const dayOff = val('DayOffChancePct');
    const night = val('OfflineIdleAtNight') && (val('OfflineIdleGames') || []).length;

    const others = w.length - 1;
    let mostly;
    if (!w.length) {
      mostly = esc(t('No games listed yet.'));
    } else if (others <= 0) {
      mostly = tf('Mostly {0}.', `<b>${esc(gameLabel(w[0].game))}</b>`);
    } else {
      mostly = tf('Mostly {0}, with {1} in bursts rather than every day.', `<b>${esc(gameLabel(w[0].game))}</b>`,
        others === 1 ? t('one other') : tf('{0} others', others));
    }

    return `<div class="preview"><span class="k">${esc(t('A day looks like'))}</span>
      ${tf('On around {0}, bed around {1} — both jittered daily.',
        `<b>${String(from).padStart(2, '0')}:00</b>`, `<b>${String(to).padStart(2, '0')}:00</b>`)}
      ${tf('About {0} on a weekday and {1} at the weekend.', `<b>${weekday}h</b>`, `<b>${weekend}h</b>`)}
      ${dayOff > 0 ? tf('Roughly {0} days off entirely.', `<b>${dayOff} in 100</b>`) : ''}
      ${tf('One game at a time, in sittings of {0}.', `<b>${val('SessionMinMinutes')}–${val('SessionMaxMinutes')} min</b>`)}
      ${mostly}
      ${night ? esc(t('Overnight it goes invisible and keeps banking hours.')) : ''}
      </div>`;
  }

  if (section === 'rep4rep commenting') {
    const cap = val('Rep4RepDailyCap') || 0;
    const lo = val('Rep4RepGapMinMinutes') || 0;
    const hi = Math.max(lo, val('Rep4RepGapMaxMinutes') || 0);
    const from = val('Rep4RepStartHour');
    const to = val('Rep4RepEndHour');
    const hours = from === to
      ? t('around the clock')
      : tf('between {0} and {1}', String(from).padStart(2, '0') + ':00', String(to).padStart(2, '0') + ':00');
    const span = Math.round((cap * (lo + hi) / 2) / 60 * 10) / 10;

    return `<div class="preview"><span class="k">${esc(t('Right now'))}</span>
      ${tf('Up to {0} comments a day, {1} apart, {2}.', `<b>${cap}</b>`, `<b>${lo}–${hi} ${esc(t('minutes'))}</b>`, esc(hours))}
      ${tf("That's roughly {0} of posting spread across the day.", `<b>${span}h</b>`)}</div>`;
  }

  return '';
}

const GAME_NAMES = {
  730: 'Counter-Strike 2', 440: 'Team Fortress 2', 570: 'Dota 2', 550: 'Left 4 Dead 2',
  500: 'Left 4 Dead', 252490: 'Rust', 578080: 'PUBG: BATTLEGROUNDS', 590830: 's&box',
  4000: "Garry's Mod", 271590: 'Grand Theft Auto V', 1623730: 'Palworld', 892970: 'Valheim'
};

const gameLabel = (id) => GAME_NAMES[id] || ('app ' + id);

// AppIDs we've asked the server about, so a name that genuinely can't be resolved isn't re-requested forever.
const namesAsked = new Set();

// Names come back from Steam, so they land after the form has already drawn. Rather than re-render on every
// single answer (which would fight whatever the user is typing), collect a batch and redraw once.
async function learnNames(ids) {
  const missing = ids.filter((id) => id && !GAME_NAMES[id] && !namesAsked.has(id));
  if (!missing.length) return;
  missing.forEach((id) => namesAsked.add(id));

  try {
    const got = await api('/api/appnames?ids=' + missing.join(','));
    let learned = false;
    for (const [id, name] of Object.entries(got || {})) {
      if (name && !/^app \d+$/.test(name)) { GAME_NAMES[id] = name; learned = true; }
    }
    // Only redraw if nothing is being typed. A name arriving from Steam mid-edit used to replace the whole
    // form and throw away the focused input along with whatever had been half-typed into it.
    const busy = document.activeElement;
    const typing = busy && (busy.tagName === 'INPUT' || busy.tagName === 'TEXTAREA' || busy.tagName === 'SELECT');
    if (learned && view === 'settings' && !typing) renderSettings();
  } catch { /* cosmetic only - the appID still shows */ }
}

// Mirrors HumanMode.ParseWeights on the server. It has to: if the two disagree, the editor and the preview
// show one thing and the scheduler does another, which is worse than having no preview at all.
// A weight left out shares whatever is spare; an explicit 0 benches that game.
function parseWeights(spec) {
  if (!spec) return [];

  const rows = [];
  const blanks = [];

  for (const part of String(spec).split(/[,;]/)) {
    if (!part.trim()) continue;
    const [g, w] = part.split(':');
    const game = parseInt(String(g).trim());
    if (!game || rows.some((r) => r.game === game)) continue;

    if (w === undefined) {
      blanks.push(rows.length);
      rows.push({ game, weight: 0 });
    } else {
      const weight = parseInt(String(w).replace('%', '').trim()) || 0;
      if (weight <= 0) continue;          // explicitly benched
      rows.push({ game, weight });
    }
  }

  if (blanks.length) {
    const given = rows.reduce((sum, r) => sum + r.weight, 0);
    const each = Math.max(1, Math.floor(Math.max(blanks.length, 100 - given) / blanks.length));
    blanks.forEach((i) => { rows[i].weight = each; });
  }

  return rows;
}

// ── the weights editor ────────────────────────────────────────────────────
// One row per game: its real name, its share of the week, and a bar you can see at a glance. The first row is
// the MAIN game — the one this account is supposed to be into — and everything else is what it dips into.
// On a human-mode account with the achievement hunt on, the game being hunted joins these games as one more side
// game, at "hunt game's weight". It used to be invisible here - the list read 70/15/15 while the day really split
// 70/10/10/10 - and its weight and daily cap were two settings under Achievements, behind Show advanced.
function huntRow() {
  const values = settingsValues() || {};
  // The hunt only runs with "Earn achievements over time" on as well (AchievementBoost.On) - without it the row showed a
  // share the day never gave, and the side games looked smaller than they were.
  if (settingsTarget === GLOBAL || !liveValue('LegitMode', values) || !(Number(liveValue('AchievementBoost', values)) > 0)
    || !liveValue('UnlockAchievements', values)) return null;
  return { weight: Math.max(1, Math.min(95, Number(liveValue('BoostWeight', values)) || 15)), hours: Number(liveValue('BoostHoursPerDay', values)) || 0 };
}

function weightsEditor(spec) {
  const rows = parseWeights(spec);
  learnNames(rows.map((r) => r.game));

  const total = rows.reduce((sum, r) => sum + r.weight, 0) || 1;
  const hunt = rows.length ? huntRow() : null;

  // Row zero's own number is the main game's share now, and the scheduler reads it. It used to be owned by a
  // separate "Main game gets" box, so this row was shown read-only — you could not set the one figure the whole
  // schedule turns on from the list it belongs to, and the number sitting in the spec was ignored.
  // The scheduler reads the main game's own number AS its share (5-95), whatever the others add up to - so that is
  // what's shown. A one-game list with no hunt is simply that game all day.
  const mainPct = rows.length === 1 && !hunt ? 100 : Math.max(5, Math.min(95, Math.round(rows[0]?.weight ?? 70)));

  // What each row is worth across a week rather than on a mixed day. Main-game-only days carry no side games at
  // all, so every side share is worth less over a week than it reads here - the gap is wide enough at a high
  // pure-main chance that showing only the configured figure reads as a promise the schedule never made.
  const pure = Math.max(0, Math.min(100, liveValue('PureMainDayChancePct', settingsValues() || {}) ?? 25));

  // The side games and the hunt share what the main game leaves, by weight - the same sum the scheduler does.
  const sidePool = Math.max(1, total - (rows[0]?.weight ?? 0) + (hunt ? hunt.weight : 0));
  const shares = rows.map((r, i) => i === 0 ? mainPct : Math.round((r.weight / sidePool) * (100 - mainPct)));
  const huntShare = hunt ? Math.round((hunt.weight / sidePool) * (100 - mainPct)) : 0;

  // The exact weekly figures always total 100, so the rounded ones have to as well. Rounding each on its own
  // put a column of 78/6/6/11 on screen — 101, from two values that were really 77.5 and 10.5. Largest
  // remainder instead: floor everything, then hand the leftover points to whichever rows were cut hardest.
  const weeklies = roundToTotal([...shares, ...(hunt ? [huntShare] : [])].map((s, i) => i === 0 ? pure + ((100 - pure) * s / 100) : (100 - pure) * s / 100), 100);

  const body = rows.map((r, i) => {
    const share = shares[i];
    const weekly = weeklies[i];
    const weeklyTip = rows.length > 1 && pure > 0
      ? tf('About {0} of an average week once the main-game-only days are counted in.', `${weekly}%`)
      : '';

    // "wmain", not "main": the page's own content-area class is .main, and this row was quietly picking up
    // its `padding: 22px 26px 40px`. That is the whole reason the first row sat 26px to the right of every
    // other one and stood three times as tall - it was being laid out as if it were the page.
    return `<div class="wrow ${i === 0 ? 'wmain' : ''}">
      <span class="wname">${i === 0 && changingMain
        ? `<input class="wmainedit" type="text" placeholder="${esc(t('appID or store URL'))}" title="${esc(t('the new main game: appID or store URL'))}" onkeydown="if(event.key==='Enter'){setMainGame(this.value);event.preventDefault();}else if(event.key==='Escape'){setMainGame('');}" onblur="setMainGame(this.value)">`
        : `<b class="wgame" title="${esc(gameLabel(r.game))}">${esc(gameLabel(r.game))}</b>${i === 0 ? `<b class="wtag">${esc(t('main'))}</b>` : ''}<i class="wid">${r.game}</i>`}</span>
      <span class="wbar"><i style="width:${share}%"></i></span>
      <input class="wpct" type="number" min="1" max="95" value="${share}" data-w-index="${i}"
             onchange="setShare(${i},parseInt(this.value)||1)" data-tip="${esc(
               (i === 0
                 ? t("The main game's share of a mixed day, held there however many other games you add. It's rolled within about 10 points of this each morning.")
                 : t("This game's share of a mixed day. Every row adds up to 100 - change one and the others move to make room."))
               + (weeklyTip ? ' ' + weeklyTip : ''))}">
      <span class="wsign">%</span>
      <span class="wweek"${weeklyTip ? ` data-tip="${esc(weeklyTip)}"` : ''}>${weeklyTip ? `${weekly}%<i>${esc(t('/week'))}</i>` : ''}</span>
      ${i === 0 ? `<span class="wact"><b onclick="changingMain=true;renderSettings();setTimeout(()=>{const e=document.querySelector('.wmainedit');if(e)e.focus();},0)" data-tip="${esc(t('Change the main game'))}">⇄</b></span>` : `<span class="wact"><b onclick="makeMain(${i})" data-tip="${esc(t('Make this the main game'))}">↑</b><b onclick="dropWeight(${i})" data-tip="${esc(t('Remove'))}">×</b></span>`}
    </div>`;
  }).join('');

  // The hunt's own row: which game it's on, its share like any other row, and how long a day at most.
  const huntWeekly = weeklies[rows.length];
  const huntTip = rows.length > 1 && pure > 0 ? tf('About {0} of an average week once the main-game-only days are counted in.', `${huntWeekly}%`) : '';
  const huntNow = pacerHunt && pacerHunt.Now ? pacerHunt.Now : '';
  const huntHtml = hunt ? `<div class="wrow whunt">
      <span class="wname"><b class="wgame">${esc(huntNow || t('Achievement hunt'))}</b><b class="wtag">${esc(t('hunt'))}</b></span>
      <span class="wbar"><i style="width:${huntShare}%"></i></span>
      <input class="wpct" type="number" min="1" max="95" value="${huntShare}"
             onchange="setShare(${rows.length},parseInt(this.value)||1)" data-tip="${esc(t("The achievement hunt's share of a mixed day. It plays the game it's hunting like a side game, in normal sittings, and moves on to the next game by itself.") + (huntTip ? ' ' + huntTip : ''))}">
      <span class="wsign">%</span>
      <span class="wweek"${huntTip ? ` data-tip="${esc(huntTip)}"` : ''}>${huntTip ? `${huntWeekly}%<i>${esc(t('/week'))}</i>` : ''}</span>
      <span class="wact"></span>
    </div>
    <div class="whours muted small" data-tip="${esc(t("Once it has hunted this long in a day, the hunt's game leaves the list until tomorrow. 0 is no limit.") + ' ' + t('Which games it hunts is set under Achievements.'))}">
      <span>${esc(t('Hunt at most'))}</span>
      <input type="number" min="0" max="24" value="${hunt.hours}" onchange="editAndRender('BoostHoursPerDay', Math.max(0, Math.min(24, parseInt(this.value) || 0)))">
      <span>${esc(t('hours a day (0 = no limit)'))}</span></div>` : '';

  return `<div class="weights" data-setting="GameWeights">
    ${body || `<p class="muted small" style="margin:0 0 8px">${esc(t('No games yet — add the one this account is meant to be into first.'))}</p>`}
    ${huntHtml}
    <div class="wadd">
      <input type="text" placeholder="${esc(t('appID or store URL'))}" onkeydown="if(event.key==='Enter'){addWeight(this);event.preventDefault();}" onblur="addWeight(this)">
      <span class="muted small">${esc(t('The first game added is the main one.'))}</span>
    </div>
  </div>`;
}

const weightsSpec = (rows) => rows.map((r) => `${r.game}:${r.weight}`).join(', ');

/// Round a set of exact percentages to whole numbers that still add up to `total` (largest remainder / Hare).
/// Rounding each value on its own is what puts a column of 78/6/6/11 on screen when the exact figures were
/// 77.5/6/6/10.5 — three of them round up and the total gains a point that does not exist.
function roundToTotal(values, total) {
  const floors = values.map((v) => Math.floor(v));
  let left = total - floors.reduce((a, b) => a + b, 0);

  // Hand the leftover points out to the largest fractional parts first, biggest row winning any tie so the
  // point lands where it is least visible.
  const order = values
    .map((v, i) => ({ i, frac: v - Math.floor(v), size: v }))
    .sort((a, b) => b.frac - a.frac || b.size - a.size);

  for (const { i } of order) {
    if (left <= 0) break;
    floors[i]++;
    left--;
  }

  return floors;
}

/// Set one game's share and even the remainder out across the others, so the row you didn't touch never has to
/// be worked out by hand and the total is always 100.
// Set one side game's SHARE OF THE WEEK, and move the other side games to make room.
//
// The box used to show the raw stored weight while the bar beside it showed the share - two different numbers
// for one row, and changing "Main game gets" moved the bar and left the box alone. Everything is a share now,
// so the column always adds up to 100 and the main game's slider visibly pushes the others around.
//
// The main game is edited here like any other row - its number is the share the scheduler actually holds it at.
function setShare(index, wantPct) {
  const spec = parseWeights(liveWeights());
  const hunt = spec.length ? huntRow() : null;

  // The hunt takes part in the balancing as one more side row (the last one), then goes back to its own setting.
  const rows = hunt ? [...spec, { game: 0, weight: hunt.weight }] : spec;
  if (!rows[index]) return;

  const sides = rows.length - 1;

  // One game listed: it takes the lot, and there is nothing to balance against.
  if (sides < 1) { rows[0].weight = 100; editAndRender('GameWeights', weightsSpec(rows)); return; }

  // Dragging the main game moves every side game together; dragging a side game moves only its peers. Both
  // leave the column adding up to 100, so no row ever has to be worked out by hand.
  // The main game's number is its share, as the scheduler reads it.
  const mainPct = index === 0
    ? Math.max(5, Math.min(100 - sides, wantPct))
    : Math.max(5, Math.min(95, Math.round(spec[0].weight)));

  const pool = 100 - mainPct;                       // what all the side games share between them
  const otherIdx = rows.map((_, i) => i).filter((i) => i !== 0 && i !== index);

  // Split what is left in proportion to what the others already had, so nudging one game does not flatten
  // the balance between the rest.
  const prior = otherIdx.map((i) => Math.max(1, rows[i].weight));
  const priorSum = prior.reduce((a, b) => a + b, 0) || 1;

  rows[0].weight = mainPct;

  if (index === 0) {
    otherIdx.forEach((i, k) => { rows[i].weight = Math.max(1, Math.round(pool * prior[k] / priorSum)); });
  } else {
    // Everyone else needs at least 1, so this one cannot take the whole pool.
    const mine = Math.max(1, Math.min(pool - (sides - 1), wantPct));
    const rest = pool - mine;

    rows[index].weight = mine;
    otherIdx.forEach((i, k) => { rows[i].weight = Math.max(1, Math.round(rest * prior[k] / priorSum)); });
  }

  // Rounding the side games individually leaves the column summing to 99 or 101, and every row is then drawn as
  // its slice of that total — so typing 70 into the main game showed 71 back. Push the drift onto a row the user
  // is not currently looking at, so the number they just typed is the number they see.
  const drift = 100 - rows.reduce((sum, r) => sum + r.weight, 0);

  if (drift !== 0) {
    const soak = otherIdx.length ? otherIdx.reduce((best, i) => (rows[i].weight > rows[best].weight ? i : best), otherIdx[0]) : index;
    rows[soak].weight = Math.max(1, rows[soak].weight + drift);
  }

  if (hunt) {
    // Back to its own setting. The main game keeps its number (that IS its share); the side games and the hunt
    // split the rest by weight, so theirs only have to be right relative to each other.
    pending.BoostWeight = Math.max(1, Math.min(95, rows.pop().weight));
  }

  editAndRender('GameWeights', weightsSpec(rows));
}

function addWeight(input) {
  const id = parseAppId(input.value);
  input.value = '';
  if (!id) return;

  const rows = parseWeights(liveWeights());
  if (rows.some((r) => r.game === id)) return;

  rows.push({ game: id, weight: 1 });
  learnNames([id]);

  // A brand new list is one game at 100%. After that the newcomer takes a share and the rest even out.
  //
  // The rebalance has to happen on THIS array, not by calling setWeight afterwards: setWeight re-reads the
  // committed value, and the new row is not committed yet — so it looked up an index that did not exist, hit
  // its own guard, and returned silently. Adding a second game did nothing at all.
  //
  // The main game keeps its number - that number IS its share, however many games are added. Evening every row out
  // flattened it: 730:70, 440:30 plus a third game came out 42/42/16, and the main game dropped to 42% of the day.
  // Only the second game takes a share off it, as that one ends "the main game all day".
  if (rows.length > 1) {
    const sides = rows.length - 1;
    const main = sides === 1
      ? 100 - Math.max(5, Math.floor(100 / rows.length / 2))
      : Math.max(5, Math.min(100 - sides, rows[0].weight));
    const pool = 100 - main;
    const mine = Math.max(1, Math.floor(pool / sides));
    rows[0].weight = main;
    rows[rows.length - 1].weight = mine;
    fitSides(rows, rows.length - 1);
  } else {
    rows[0].weight = 100;
  }

  editAndRender('GameWeights', weightsSpec(rows));
}

/// The side games scaled to share what the main game leaves (100 minus its number), keeping their balance with each
/// other; `keep` is a row whose number stays as it is. Adds up to exactly 100, the drift on the biggest other side game.
function fitSides(rows, keep) {
  const pool = 100 - rows[0].weight;
  const idx = rows.map((_, i) => i).filter((i) => i !== 0 && i !== keep);
  if (!idx.length) return rows;

  const room = Math.max(idx.length, pool - (keep > 0 ? rows[keep].weight : 0));
  const sum = idx.reduce((a, i) => a + Math.max(1, rows[i].weight), 0) || 1;
  idx.forEach((i) => { rows[i].weight = Math.max(1, Math.round(room * Math.max(1, rows[i].weight) / sum)); });

  const drift = 100 - rows.reduce((a, r) => a + r.weight, 0);
  if (drift !== 0) {
    const soak = idx.reduce((best, i) => (rows[i].weight > rows[best].weight ? i : best), idx[0]);
    rows[soak].weight = Math.max(1, rows[soak].weight + drift);
  }

  return rows;
}

function dropWeight(index) {
  const rows = parseWeights(liveWeights()).filter((_, i) => i !== index);
  if (!rows.length) { editAndRender('GameWeights', ''); return; }

  // The share the removed game had goes to the other side games, not the main game: the main game's number is its
  // share, so handing it the leftover took 730:70, 440:15, 570:15 to 85% main by removing one side game. Just the
  // main game left, it's that game all day.
  if (rows.length > 1) fitSides(rows, -1);
  editAndRender('GameWeights', weightsSpec(rows));
}

/// Promote a game to main. Being first in the list is what makes it the main game, so this is a move, not a flag.
// The main game swapped for another in place: it keeps the main game's share. A game already in the list moves up
// instead, so it isn't listed twice. Before, the only way was to add the game, move it up, then remove the old one -
// and with just one game listed there was no arrow to press at all.
let changingMain = false;
function setMainGame(raw) {
  if (!changingMain) return;
  changingMain = false;
  const id = parseAppId(raw);
  const rows = parseWeights(liveWeights());
  if (!id || !rows.length || rows[0].game === id) { renderSettings(); return; }

  const already = rows.findIndex((r) => r.game === id);
  if (already > 0) { makeMain(already); return; }

  rows[0].game = id;
  learnNames([id]);
  editAndRender('GameWeights', weightsSpec(rows));
}

function makeMain(index) {
  const rows = parseWeights(liveWeights());
  if (!rows[index]) return;
  // The two swap numbers as well as places: the first number is the main game's share, so moving 440:15 up over
  // 730:70 made a "main" game that played 15% of the day, with the old main still at 70 as a side game.
  const mainWeight = rows[0].weight;
  const [moved] = rows.splice(index, 1);
  rows[0].weight = moved.weight;
  moved.weight = mainWeight;
  rows.unshift(moved);
  editAndRender('GameWeights', weightsSpec(rows));
}

function liveWeights() {
  return pending.GameWeights !== undefined ? pending.GameWeights : (settingsValues() || {}).GameWeights || '';
}

function liveValue(name, values) {
  return pending[name] !== undefined ? pending[name] : values[name];
}

function isChanged(def, values, defaults) {
  // A secret is never sent to the browser, so comparing it to the default would always say "unchanged" even
  // when one is stored. Treat "is set" or "edited right now" as changed.
  if (def.Kind === 'Secret') return pending[def.Name] !== undefined || secretIsSet(def.Name);

  const a = pending[def.Name] !== undefined ? pending[def.Name] : values[def.Name];
  return JSON.stringify(a) !== JSON.stringify(defaults[def.Name]);
}

// ── the one irreversible thing in here ────────────────────────────────────
// Kept away from the settings themselves, and deliberately awkward to trigger. Everything else on this page is
// a preference that can be changed back; this writes several thousand achievements onto a Steam profile, all
// sharing one timestamp, and Steam stamps that time server-side so there is no undoing it.
function dangerZone(name) {
  return `<div class="section danger">
    <h3 data-section="Careful">${esc(t('Careful'))}</h3>
    <div class="dangerbox">
      <b>${esc(t('Unlock every achievement, in every game this account owns'))}</b>
      <p class="muted small">${esc(t('This is for accounts that are not pretending to be anyone. Thousands of achievements appear at once, all stamped with the same moment, and that stamp is set by Steam and cannot be changed or hidden. Anyone looking at the profile can see it, permanently. If this account is meant to look played, leave this alone and let the pacer earn them instead.'))}</p>
      <button class="danger" data-bot="${esc(name)}" onclick="askUnlockAll(this.dataset.bot)">${esc(t('Unlock everything…'))}</button>
    </div>
  </div>`;
}

// The name travels in a data-attribute, never interpolated into the handler string. esc() escapes for HTML
// TEXT, not for a JavaScript string literal inside an attribute - an account named  it's  would close the
// quote and break the handler, and on the one irreversible action in the app that is not a risk worth taking.
function askUnlockAll(name) {
  modal(`
    <h2>${esc(tf('Unlock everything on {0}?', name))}</h2>
    <p>${tf('Every achievement in every game {0} owns will be unlocked, right now.', `<b>${esc(name)}</b>`)}</p>
    <ul class="muted small">
      <li>${esc(t('They all get the same unlock time. That is what makes it obvious.'))}</li>
      <li>${esc(t('Steam sets that time itself - it cannot be back-dated or hidden.'))}</li>
      <li>${esc(t('It runs for a long time on a big library, and it cannot be meaningfully undone.'))}</li>
    </ul>
    <p class="small">${tf('Type {0} below to enable the button.', '<code>confirm</code>')}</p>
    <input type="text" id="unlockConfirm" autocomplete="off" spellcheck="false" placeholder="${esc(t('type confirm'))}"
           oninput="$('unlockGo').disabled = this.value.trim().toLowerCase() !== 'confirm'">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button>
      <button class="danger" id="unlockGo" disabled data-bot="${esc(name)}" onclick="doUnlockAll(this.dataset.bot)">${esc(t('Unlock everything'))}</button>
    </div>`);
  setTimeout(() => { const el = $('unlockConfirm'); if (el) el.focus(); }, 30);
}

async function doUnlockAll(name) {
  const typed = ($('unlockConfirm').value || '').trim();

  // Sent to the server as well as checked here. A disabled button is a courtesy, not a guard.
  const res = await post(`/api/bots/${encodeURIComponent(name)}/achievements/unlock-all`, { Confirm: typed });
  closeModal();

  if (!res.ok) { toast(res.error || t("That didn't work"), true); return; }

  toast(tf('{0}: unlocking everything - watch the log', name));
  go('log');
}

// Settings you can switch off, but should be asked about first.
//
// Not a general "are you sure" on every toggle - that trains people to click through warnings. Only where
// switching it OFF leaves the app quietly less useful in a way that will not be obvious later.
const GUARDED_OFF = {
  OpenDashboardAfterAdd: {
    title: () => t('Turn off opening the dashboard?'),
    body: () => `<p>${tf('A newly added account farms its trading cards and {0} until it is told what to play, and the app window has no form for that - only the dashboard does.', `<b>${esc(t('nothing more'))}</b>`)}</p>
      <p class="muted small">${esc(t('With this off, adding an account leaves you at a command line with an account that farms its cards and then sits idle until you remember to go and configure it. This is for people who already know that.'))}</p>`,
  },
};

function editBool(name, el) {
  const guard = GUARDED_OFF[name];

  // Only when switching OFF, and only for the handful listed above.
  if (!guard || el.checked) {
    edit(name, el.checked);
    return;
  }

  // Put it back until the question is answered, so a cancelled dialog cannot leave the switch showing a state
  // that was never saved.
  el.checked = true;

  modal(`
    <h2>${esc(guard.title())}</h2>
    ${guard.body()}
    <p class="small">${tf('Type {0} to confirm.', '<code>off</code>')}</p>
    <input type="text" id="guardConfirm" autocomplete="off" spellcheck="false" placeholder="${esc(t('type off'))}"
           oninput="$('guardGo').disabled = this.value.trim().toLowerCase() !== 'off'">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">${esc(t('Keep it on'))}</button>
      <button class="danger" id="guardGo" disabled data-setting="${esc(name)}"
              onclick="edit(this.dataset.setting, false); closeModal(); renderSettings();">${esc(t('Turn it off'))}</button>
    </div>`);
  setTimeout(() => { const i = $('guardConfirm'); if (i) i.focus(); }, 30);
}

// The Discord card's account and button settings as choices - they were boxes to type an account name, a code word
// ("github") or "Label | https://link" into, and nobody could tell what went in them.
function discordControl(def, cur, id) {
  const bots = (state && state.Bots) || [];
  const who = (b) => (b.SteamName && b.SteamName !== b.Name ? `${b.Name} (${b.SteamName})` : b.Name);
  const v = String(cur || '').trim();

  if (def.Name === 'DiscordFeatured') {
    return `<select id="${id}" data-setting="${def.Name}" onchange="editAndRender('${def.Name}',this.value)">
      <option value="" ${v ? '' : 'selected'}>${esc(t('the first account shown'))}</option>
      ${bots.map((b) => `<option value="${esc(b.Name)}" ${v.toLowerCase() === b.Name.toLowerCase() ? 'selected' : ''}>${esc(who(b))}</option>`).join('')}</select>`;
  }

  if (def.Name === 'DiscordPresenceAccounts') {
    const auto = v === '';
    const listed = v.toLowerCase() === 'all' ? bots.map((b) => b.Name.toLowerCase()) : v.split(/[, ]+/).filter(Boolean).map((n) => n.toLowerCase());
    return `<div class="pills" data-setting="${def.Name}">
      <span class="p ${auto ? 'on' : ''}" onclick="editAndRender('${def.Name}','')">${esc(t('every account not in human mode'))}</span>
      ${bots.map((b) => `<span class="p ${!auto && listed.includes(b.Name.toLowerCase()) ? 'on' : ''}" onclick="toggleShownAccount(${esc(JSON.stringify(b.Name))})">${esc(b.Name)}</span>`).join('')}</div>`;
  }

  if (def.Name === 'DiscordButton1' || def.Name === 'DiscordButton2') {
    const bar = v.indexOf('|');
    const acct = bots.find((b) => b.Name.toLowerCase() === v.toLowerCase());
    const pick = !v ? '' : v.toLowerCase() === 'github' ? 'github' : acct ? acct.Name : 'custom';
    const label = bar > 0 ? v.slice(0, bar).trim() : '';
    const link = bar > 0 ? v.slice(bar + 1).trim() : '';
    return `<div class="dbtnpick" data-setting="${def.Name}">
      <select id="${id}" onchange="discordButtonPick('${def.Name}',this.value)">
        <option value="" ${pick === '' ? 'selected' : ''}>${esc(t('none'))}</option>
        <option value="github" ${pick === 'github' ? 'selected' : ''}>${esc(t('Get nocat.farm'))}</option>
        ${bots.map((b) => `<option value="${esc(b.Name)}" ${pick === b.Name ? 'selected' : ''}>${esc(tf('{0} on Steam', who(b)))}</option>`).join('')}
        <option value="custom" ${pick === 'custom' ? 'selected' : ''}>${esc(t('Your own link'))}</option>
      </select>
      ${pick === 'custom' ? `<input type="text" id="${id}-label" placeholder="${esc(t('Button text'))}" value="${esc(label)}" maxlength="32" oninput="discordButtonCustom('${def.Name}')">
        <input type="text" id="${id}-link" placeholder="https://..." value="${esc(link)}" oninput="discordButtonCustom('${def.Name}')">` : ''}
    </div>`;
  }

  return null;
}

function toggleShownAccount(name) {
  const bots = (state && state.Bots) || [];
  const v = String(liveValue('DiscordPresenceAccounts', settingsValues() || {}) || '').trim();
  // From "automatic" the list starts as what automatic meant, so ticking one more adds to it rather than replacing it.
  let list = v === '' ? bots.filter((b) => !b.Legit).map((b) => b.Name)
    : v.toLowerCase() === 'all' ? bots.map((b) => b.Name) : v.split(/[, ]+/).filter(Boolean);
  list = list.some((n) => n.toLowerCase() === name.toLowerCase()) ? list.filter((n) => n.toLowerCase() !== name.toLowerCase()) : [...list, name];
  editAndRender('DiscordPresenceAccounts', list.join(', '));
}

function discordButtonPick(setting, choice) {
  editAndRender(setting, choice === 'custom' ? `${t('My link')} | https://` : choice);
}

function discordButtonCustom(setting) {
  const id = 'f-' + setting;
  const label = ($(id + '-label')?.value || '').replace(/\|/g, '').trim();
  const link = ($(id + '-link')?.value || '').trim();
  edit(setting, `${label} | ${link}`);
}

function fieldHtml(def, values, defaults) {
  const cur = pending[def.Name] !== undefined ? pending[def.Name] : values[def.Name];
  const id = 'f-' + def.Name;
  const changed = pending[def.Name] !== undefined;
  let ctl = discordControl(def, cur, id);

  if (ctl === null) switch (def.Kind) {
    case 'Bool':
      ctl = `<label class="switch"><input type="checkbox" id="${id}" data-setting="${def.Name}" ${cur ? 'checked' : ''} onchange="editBool('${def.Name}',this)"><span></span></label>`;
      break;
    case 'Int':
    case 'Float':
      ctl = `<input type="number" id="${id}" data-setting="${def.Name}" style="max-width:140px" value="${esc(cur)}"
             ${def.Min > -1e300 ? `min="${def.Min}"` : ''} ${def.Max < 1e300 ? `max="${def.Max}"` : ''} ${def.Kind === 'Float' ? 'step="any"' : ''}
             onchange="editAndRender('${def.Name}',${def.Kind === 'Float' ? 'parseFloat' : 'parseInt'}(this.value)||0)">`;
      break;
    case 'Secret': {
      const isSet = secretIsSet(def.Name);
      const cleared = cur === CLEAR_SECRET;

      // The server sends '' for every secret and lists the ones it holds separately, so a non-empty value here
      // means the box is genuinely being typed into rather than showing a stored value.
      const typing = !cleared && typeof cur === 'string' && cur.length > 0;

      // Whether a secret is stored gets its own visible chip.
      //
      // It used to be inferable only from grey placeholder text - which disappears the second you click into
      // the box - and from whether a Clear button happened to be rendered beside it. Neither answers "do I have
      // a token saved?" at a glance, which is the only question anyone actually asks of this field. The chip
      // lives inside .tags rather than in the meta column because .tags already wraps; .field .meta is nowrap
      // with no min-width, so a chip there would push a narrow settings row out of its card.
      const state = cleared
        ? `<span class="pill bad">${esc(t('will be erased when you save'))}</span>`
        : typing
          ? `<span class="pill warn">${esc(t('will be replaced when you save'))}</span>`
          : isSet
            ? `<span class="pill good">${esc(t('saved'))}</span>`
            : `<span class="pill">${esc(t('not set'))}</span>`;

      // Locked while something is stored, so a stray keystroke in a focused box cannot quietly overwrite a
      // working token with half a word. Clear is the deliberate act that unlocks it. Still enabled once a
      // replacement is being typed, so a redraw mid-edit cannot lock the box and discard what was in it.
      const locked = isSet && !cleared && !typing;

      ctl = `<div class="tags">
        <input type="password" id="${id}" data-setting="${def.Name}" style="max-width:260px"
               ${locked ? 'disabled' : ''}
               title="${locked ? esc(t('Locked so it cannot be changed by accident. Press Clear to replace it.')) : ''}"
               placeholder="${esc(cleared ? t('paste the new one here') : isSet ? t('stored - press Clear to replace') : t('paste it here'))}"
               value="${cleared || cur === undefined ? '' : esc(cur)}" oninput="edit('${def.Name}',this.value)">
        ${state}
        ${isSet && !cleared ? `<button class="ghost small" data-tip="${esc(t("Erase the stored value. There's no undo."))}" onclick="clearSecret('${def.Name}')">${esc(t('Clear'))}</button>` : ''}
        ${cleared ? `<button class="ghost small" onclick="editAndRender('${def.Name}','')">${esc(t('Undo'))}</button>` : ''}
      </div>`;
      break;
    }
    case 'Pick': {
      const opts = (tSetting(def, 'choices') || '').split('|').map((o) => o.trim()).filter(Boolean).map((o) => {
        const gap = o.indexOf(' ');
        return { value: o.slice(0, gap), label: o.slice(gap + 1).trim() };
      });
      ctl = `<select id="${id}" data-setting="${def.Name}" onchange="editAndRender('${def.Name}',this.value)">${opts.map((o) =>
        `<option value="${esc(o.value)}" ${cur === o.value ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}</select>`;
      break;
    }
    case 'Choice': {
      const opts = parseChoices(tSetting(def, 'choices'));
      ctl = opts.length <= 6
        ? `<div class="pills" data-setting="${def.Name}">${opts.map((o) =>
            `<span class="p ${Number(cur) === o.value ? 'on' : ''}" onclick="editAndRender('${def.Name}',${o.value})">${esc(o.label)}</span>`).join('')}</div>`
        : `<select id="${id}" data-setting="${def.Name}" onchange="editAndRender('${def.Name}',parseInt(this.value))">${opts.map((o) =>
            `<option value="${o.value}" ${Number(cur) === o.value ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}</select>`;
      break;
    }
    case 'AppIds': {
      const list = cur || [];
      // A bare "2767030" means nothing to anybody - each game shows its name, with the appID beside it linking to
      // its store page. Names arrive from Steam after the first draw (learnNames redraws once they're in).
      learnNames(list);
      ctl = `<div class="tags" data-setting="${def.Name}">
        ${list.map((a, i) => `<span class="tag">${GAME_NAMES[a] ? `<span>${esc(GAME_NAMES[a])}</span>` : ''}<a class="tagid" href="https://store.steampowered.com/app/${a}" target="_blank" rel="noopener" data-tip="${esc(t('Open its Steam store page'))}">${a}</a><b onclick="removeApp('${def.Name}',${i})">×</b></span>`).join('')}
        <input type="text" class="appin" placeholder="${esc(t('appID or store URL'))}" onkeydown="if(event.key==='Enter'||event.key===','){addApp('${def.Name}',this);event.preventDefault();}" onblur="addApp('${def.Name}',this)">
      </div>`;
      break;
    }
    default:
      // The weights are the one setting people actually tune, and "730:70, 440:20" is a terrible thing to have
      // to hand-write. It gets a real editor; everything else is a text box.
      ctl = def.Name === 'GameWeights'
        ? weightsEditor(cur)
        : `<input type="text" id="${id}" data-setting="${def.Name}" value="${esc(cur)}" placeholder="${esc(tSetting(def, 'placeholder') || '')}" oninput="edit('${def.Name}',this.value)">`;
  }

  const def0 = defaults[def.Name];
  // A choice's default by its name, not its number - "default 1" under a list reading "online" meant nothing.
  const choiceName = () => {
    if (def.Kind === 'Choice') return (parseChoices(tSetting(def, 'choices')).find((o) => o.value === Number(def0)) || {}).label;
    const pick = (tSetting(def, 'choices') || '').split('|').map((o) => o.trim()).find((o) => o.split(' ')[0] === String(def0));
    // An option that already calls itself "(default)" would read "default desktop (default)".
    return pick ? pick.slice(pick.indexOf(' ') + 1).replace(/\s*\([^)]*\)\s*$/, '').trim() : undefined;
  };
  const defText = Array.isArray(def0) ? (def0.length ? def0.join(', ') : t('none'))
    : def.Kind === 'Secret' ? ''
    : def.Kind === 'Bool' ? (def0 ? t('on') : t('off'))
    : (def.Kind === 'Choice' || def.Kind === 'Pick') ? (choiceName() || String(def0))
    // the Discord buttons' code word, and an empty account list, in the words the dropdowns use
    : /^DiscordButton[12]$/.test(def.Name) ? (String(def0).toLowerCase() === 'github' ? t('Get nocat.farm') : def0 ? String(def0) : t('none'))
    : def.Name === 'DiscordPresenceAccounts' && !def0 ? t('every account not in human mode')
    : def.Name === 'DiscordFeatured' && !def0 ? t('the first account shown')
    : String(def0);

  return `<div class="field ${changed ? 'changed' : ''}">
    <label for="${id}">${esc(tSetting(def, 'label'))}${tipIcon(tSetting(def, 'tip'))}</label>
    <div class="ctl">${ctl}</div>
    <div class="meta">
      ${def.NeedsRestart ? `<span class="restart" data-tip="${esc(t('This takes effect the next time nocat.farm starts.'))}">⟳</span>` : ''}
      ${defText !== '' ? `<span class="revert" data-tip="${esc(t('Put this back to the default.'))}" onclick="editAndRender('${def.Name}',${JSON.stringify(def0).replace(/"/g, '&quot;')})">${esc(tf('default {0}', defText))}</span>` : ''}
    </div></div>`;
}

function parseChoices(choices) {
  if (!choices) return [];
  return choices.split('|').map((o) => {
    const text = o.trim();
    const sp = text.indexOf(' ');
    return { value: parseInt(text.slice(0, sp)), label: text.slice(sp + 1) };
  });
}

// Typing into a text or password box must NOT rebuild the form: replacing innerHTML destroys the focused
// input, so you get exactly one character per click and the rest of your keystrokes go to the document (where
// digits are view hotkeys). Keystroke-driven fields update quietly and only repaint the live preview.
function edit(name, value) {
  pending[name] = value;
  const el = document.querySelector(`[data-setting="${name}"]`);
  if (el) el.closest('.field')?.classList.add('changed');
  refreshPreviews();
  updateSaveButton();
}

// Discrete controls (switches, choice pills, tag lists, the revert link) have no caret to lose, and they do
// need the form redrawn so the control reflects the new value.
/// The two "how do I get one of those" walkthroughs, folded away until clicked. Everyone makes their own Telegram
/// bot: Telegram lets only one program read a bot's messages, so a shared bot can't work - and its token would be
/// the key to everybody's notifications.
// The Discord profile card: one switch, a chip per part, and a preview drawn the way Discord draws it - so what
// each chip does is obvious before saving. Same data the app sends: the picked accounts, their Steam names and
// avatars, and the two buttons.
const PANEL_SECTIONS = new Set(['Discord profile']);

// Settings drawn by a section's own panel (chips and switches) instead of as rows.
const PANEL_ROWS = new Set(['DiscordPresence', 'DiscordShowNames', 'DiscordSecondLine', 'DiscordShowCounter', 'DiscordShowAvatar', 'DiscordShowTimer',
  'SendCardDrops', 'SendFreeStuff', 'SendTrades', 'SendProblems', 'SendUpdates', 'SendInstalls', 'SendDailySummary', 'SendComments',
  'SendAchievements', 'SendRep4Rep']);

function discordCardIntro(val) {
  const on = !!val('DiscordPresence');
  const parts = [['DiscordShowCounter', 'Accounts online'], ['DiscordShowAvatar', 'Avatar'], ['DiscordShowTimer', 'Timer']];
  // The second line is one pick of four: the names, or one of three counts.
  const names = !!val('DiscordShowNames');
  const line = Number(val('DiscordSecondLine') ?? 1);
  const lines = [[-1, 'Account names'], [0, 'Cards today'], [1, 'Hours past week'], [2, 'Hours past month']];
  const pickLine = (n) => n < 0 ? `editAndRender('DiscordShowNames', true)` : `pending.DiscordShowNames = false; editAndRender('DiscordSecondLine', ${n})`;
  return `<div class="explain dcard-intro">
    <div class="dhead"><b>${esc(t('Show on my Discord profile'))}</b>
      <label class="switch"><input type="checkbox" ${on ? 'checked' : ''} onchange="editAndRender('DiscordPresence', this.checked)"><span></span></label></div>
    <p style="margin:6px 0 0">${esc(t('While nocat.farm is open, your Discord profile shows it like a game. Pick what the card shows - the preview is what people see.'))}</p>
    <div class="langpick">${parts.map(([k, label]) =>
      `<span class="p ${val(k) ? 'on' : ''}" onclick="editAndRender('${k}', ${!val(k)})">${esc(t(label))}</span>`).join('')}</div>
    <div class="dline"><span class="muted small">${esc(t('Second line'))}</span><div class="langpick">${lines.map(([n, label]) =>
      `<span class="p ${(n < 0 ? names : !names && line === n) ? 'on' : ''}" onclick="${pickLine(n)}">${esc(t(label))}</span>`).join('')}</div></div>
    ${discordPreview(val)}
    <p class="muted small" style="margin:8px 0 0">${esc(t('Buttons and which accounts it shows are under Show advanced. Discord shows your buttons to everyone but you.'))}</p>
  </div>`;
}

function discordPreview(val) {
  const bots = (state && state.Bots) || [];
  const list = String(val('DiscordPresenceAccounts') || '').trim();
  // Nothing picked means every robot account, the same as the real card - an empty list is not "none".
  const names = (list === '' || list.toLowerCase() === 'all') ? null : list.split(/[, ]+/).filter(Boolean).map((n) => n.toLowerCase());
  const featName = String(val('DiscordFeatured') || '').trim().toLowerCase();
  const featured = bots.find((b) => b.Name.toLowerCase() === featName) || null;
  const shown = bots.filter((b) => (names ? names.includes(b.Name.toLowerCase()) : list === '' ? !b.Legit : true));
  const online = shown.filter((b) => b.Online);
  // Paused, or standing down while you play on it: not farming or idling anything - as on the real card.
  const working = online.filter((b) => !b.Paused && !b.Blocked);
  const cardsLeft = working.reduce((n, b) => n + Math.max(0, b.Cards || 0), 0);
  // The numbers are the whole farm's; which accounts are shown only decides the names and the top line.
  const today = bots.reduce((n, b) => n + (b.CardsToday || 0), 0);
  const connected = bots.filter((b) => b.Online).length;
  const display = (b) => b.SteamName || b.Name;
  const ordered = [...online, ...shown.filter((b) => !b.Online)];

  const details = !shown.length && !featured ? t('No accounts picked') : !online.length ? t('Resting')
    : !working.length ? t('Paused')
    : cardsLeft > 0 ? tf('Farming cards · {0} left', cardsLeft) : t('Idling games');
  // The featured account is left out of the names only while it's the picture - as on the real card.
  const featuredShown = !!(featured && val('DiscordShowAvatar') && featured.Avatar);
  const who = ordered.filter((b) => !featuredShown || b !== featured).map(display);
  const hours = (key) => {
    const h = bots.reduce((n, b) => n + (b[key] || 0), 0) / 60;
    return h < 10 ? String(Math.round(h * 10) / 10) : Math.round(h).toLocaleString('en-US');
  };
  const line = Number(val('DiscordSecondLine') ?? 1);
  const counted = line === 1 ? tf('{0} hrs past week', hours('MinutesWeek'))
    : line === 2 ? tf('{0} hrs past month', hours('MinutesMonth'))
    : tf('{0} cards today', today);
  const stateLine = val('DiscordShowNames') && who.length
    ? (who.length <= 3 ? who.join(' · ') : who.slice(0, 2).join(' · ') + ' · +' + (who.length - 2))
    : counted;
  const lead = featured || ordered.find((b) => b.Avatar) || ordered[0];
  const face = val('DiscordShowAvatar') && lead && lead.Avatar ? lead : null;
  const up = Math.max(0, (state && state.UptimeMinutes) || 0);
  // Discord's own format: 32:17 under an hour, 1:02:17 after.
  const timer = up < 60 ? `${up}:00` : `${Math.floor(up / 60)}:${String(up % 60).padStart(2, '0')}:00`;

  const button = (v) => {
    v = String(v || '').trim();
    if (!v) return null;
    if (v.toLowerCase() === 'github') return t('Get nocat.farm');
    const bar = v.indexOf('|');
    if (bar > 0) return v.slice(0, bar).trim();
    const b = bots.find((x) => x.Name.toLowerCase() === v.toLowerCase());
    return b ? tf('{0} on Steam', display(b)) : null;
  };
  const buttons = [button(val('DiscordButton1')), button(val('DiscordButton2'))].filter(Boolean);

  // The last line as Discord draws it: the state line by the party icon, then the timer by a controller.
  const pad = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M7 6h10a5 5 0 0 1 4.9 6l-.8 4a3 3 0 0 1-5 1.6L14 16h-4l-2.1 1.6a3 3 0 0 1-5-1.6l-.8-4A5 5 0 0 1 7 6Zm1 3v2H6v2h2v2h2v-2h2v-2h-2V9H8Zm7.5 1a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Zm3-1a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z"/></svg>';
  const party = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm7 0a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM2 19c0-3.3 3.1-6 7-6s7 2.7 7 6v1H2v-1Zm16 1v-1c0-1.9-.8-3.6-2.1-4.9 3.5.1 6.1 2.2 6.1 4.9v1h-4Z"/></svg>';
  // As on the real card: the short "3 linked" when the long one would run past what Discord shows (37 characters).
  const all = connected === bots.length;
  const long = all ? (connected === 1 ? t('1 account linked') : tf('{0} accounts linked', connected)) : tf('{0} of {1} accounts linked', connected, bots.length);
  const short = all ? tf('{0} linked', connected) : tf('{0} of {1} linked', connected, bots.length);
  const stateWithCounter = val('DiscordShowCounter') && bots.length ? `${stateLine} · ${(stateLine + ' · ' + long).length <= 37 ? long : short}` : stateLine;
  const meta = (shown.length ? `<span class="dstate">${party}${esc(stateWithCounter)}</span>` : '')
    + (val('DiscordShowTimer') ? `<span class="dtime">${pad}${esc(timer)}</span>` : '');

  return `<div class="dcard ${val('DiscordPresence') ? '' : 'off'}">
    <div class="dlabel">${esc(t('Playing'))}</div>
    <div class="drow">
      <div class="dart"><img src="logo-liquid.gif" alt="">${face ? `<img class="dface" src="${esc(face.Avatar)}" alt="">` : ''}</div>
      <div class="dtext">
        <div class="dname">nocat.farm</div>
        <div>${esc(details)}</div>
        ${meta ? `<div class="dmeta">${meta}</div>` : ''}
      </div>
    </div>
    ${buttons.map((l) => `<div class="dbtn">${esc(l)}</div>`).join('')}
  </div>`;
}

// Connecting goes through a private link (t.me/yourbot?start=secret), never "whoever messages the bot first" - with
// commands on, that chat controls every account.
// Wrapped in a fixed spot that refresh() updates on its own: the link only exists a few seconds after the token is
// saved (once Telegram has confirmed the bot), and "connected" arrives when Start is pressed - neither should need
// a page reload.
// A class, not an id: the settings page keeps its copy in the page while the walkthrough draws its own, and an id only
// ever found the first - the hidden one.
function telegramConnect() {
  const html = telegramConnectInner();
  return `<div class="tg-connect" data-html="${esc(html)}">${html}</div>`;
}

function syncTelegramConnect() {
  const html = telegramConnectInner();
  document.querySelectorAll('.tg-connect').forEach((el) => {
    if (el.dataset.html === html) return;
    el.dataset.html = html;
    el.innerHTML = html;
  });
}

function telegramConnectInner() {
  const link = state && state.TelegramConnectLink;
  if (link) {
    return `<div class="tgconnect"><a class="btn" href="${esc(link)}" target="_blank" rel="noopener">${esc(t('Connect Telegram'))}</a>
      <span class="muted small">${esc(t('Opens your bot - press Start there and it connects.'))}</span></div>`;
  }
  if (state && state.TelegramConnected) {
    return `<div class="tgconnect"><span class="muted small">${esc(t('Telegram is connected.'))}</span></div>`;
  }
  return '';
}

// The Discord bot's corner: is it online, the link that adds it to a server, and Connect Discord. The one-time code
// is kept here rather than in the page, so the next refresh doesn't wipe it off the screen before it's typed - and
// dropped once someone has connected with it.
let discordCode = null;

function discordConnect() {
  const html = discordConnectInner();
  return `<div class="dc-connect" data-html="${esc(html)}">${html}</div>`;
}

function syncDiscordConnect() {
  const html = discordConnectInner();
  document.querySelectorAll('.dc-connect').forEach((el) => {
    if (el.dataset.html === html) return;
    el.dataset.html = html;
    el.innerHTML = html;
  });
}

function discordConnectInner() {
  if (!state || !state.DiscordBotSet) return '';
  if (!state.DiscordBotOn) {
    return `<div class="tgconnect"><span class="muted small">${esc(t('The Discord bot is off - turn on "Take commands from Discord" to use it.'))}</span></div>`;
  }
  if (!state.DiscordBotOnline) {
    return `<div class="tgconnect"><span class="muted small">${esc(state.DiscordBotProblem ? tf('Discord bot: {0}', state.DiscordBotProblem) : t('Discord bot: connecting...'))}</span></div>`;
  }
  if (discordCode && (discordCode.until < Date.now() || (state.DiscordOwnerId && state.DiscordOwnerId !== discordCode.owner))) discordCode = null;
  const who = state.DiscordConnected ? tf('Discord bot: online as {0}, connected to {1}', state.DiscordBotName, state.DiscordOwner)
    : tf('Discord bot online as {0}', state.DiscordBotName);
  return `<div class="tgconnect">
    ${state.DiscordInviteUrl ? `<a class="btn" href="${esc(state.DiscordInviteUrl)}" target="_blank" rel="noopener">${esc(t('Add the bot to your server'))}</a>` : ''}
    <button class="ghost" onclick="discordConnectCode(this)">${esc(t('Connect Discord'))}</button>
    <span class="muted small">${esc(who)}</span>
    ${discordCode ? `<span class="small" style="flex-basis:100%">${tf('Send {0} to your bot in Discord - the code works once, for {1} minutes.',
      `<code>/connect code: ${esc(discordCode.code)}</code>`, discordCode.minutes)}</span>` : ''}
  </div>`;
}

async function discordConnectCode(btn) {
  btn.disabled = true;
  const r = await post('/api/discord/connect', {}).catch(() => null);
  btn.disabled = false;
  if (!r || !r.Code) {
    toast(t("Couldn't reach nocat.farm"), true);
    return;
  }
  discordCode = { code: r.Code, minutes: r.Minutes || 10, until: Date.now() + (r.Minutes || 10) * 60000, owner: r.OwnerId || '' };
  syncDiscordConnect();
}

function notifyGuides() {
  return `<div class="guides">${notifyGuide('telegram')}${notifyGuide('discord')}${notifyGuide('discordbot')}</div>`;
}

/// One of them on its own - the walkthrough puts each beside the box it fills in.
function notifyGuide(which) {
  const step = (html) => `<li>${html}</li>`;
  const b = (s) => `<b>${esc(s)}</b>`;
  if (which === 'telegram') {
    return `<details class="guide"><summary>${esc(t('How to set up Telegram (2 minutes)'))}</summary><ol>
      ${step(tf('Open Telegram, search for {0} (the one with the blue check) and press {1}.', b('@BotFather'), b(t('Start'))))}
      ${step(tf('Send {0}. Give it a name, like {1}, then a username that ends in "bot", like {2}.', '<code>/newbot</code>', b('nocat.farm'), b('myname_farm_bot')))}
      ${step(tf('BotFather sends you a token that looks like {0}. Copy it.', '<code>123456789:AAH...</code>'))}
      ${step(tf('Paste it into {0} below and press {1}.', b(t('Telegram bot token')), b(t('Save'))))}
      ${step(tf('Press {0} here (it shows up once the token is saved), then press {1} in Telegram. nocat.farm connects and says hello.', b(t('Connect Telegram')), b(t('Start'))))}
      ${step(tf('Optional: send BotFather {0} and give your bot the {1}.', '<code>/setuserpic</code>',
        `<a href="https://raw.githubusercontent.com/VisaHolder/nocatfarm/main/assets/logo.png" target="_blank" rel="noopener">${esc(t('nocat.farm logo'))}</a>`))}
    </ol><p class="muted small">${esc(t('Keep the token private - anyone who has it can control your bot. Everyone makes their own bot: Telegram only lets one program read each bot.'))}</p></details>`;
  }
  if (which === 'discord') {
    return `<details class="guide"><summary>${esc(t('How to set up Discord (1 minute)'))}</summary><ol>
      ${step(tf('In your Discord server, open {0}.', b(t('Server Settings → Integrations → Webhooks'))))}
      ${step(tf('Press {0}, pick the channel notifications should go to, and name it {1} if you like.', b(t('New Webhook')), b('nocat.farm')))}
      ${step(tf('Press {0}, paste it into {1} below and press {2}.', b(t('Copy Webhook URL')), b(t('Discord webhook')), b(t('Save'))))}
    </ol><p class="muted small">${esc(t('Anyone with the webhook link can post in that channel, so keep it private.'))}</p></details>`;
  }
  return `<details class="guide"><summary>${esc(t('How to set up Discord commands (3 minutes)'))}</summary><ol>
      ${step(tf('Open the {0} and sign in with your Discord account.',
        `<a href="https://discord.com/developers/applications" target="_blank" rel="noopener">Discord Developer Portal</a>`))}
      ${step(tf('Press {0}, name it {1}, tick the box and press {2}.', b('New Application'), b('nocat.farm'), b('Create')))}
      ${step(tf('Open {0} on the left, press {1} and copy the token it shows.', b('Bot'), b('Reset Token')))}
      ${step(tf('Paste it into {0} below and press {1}.', b(t('Discord bot token')), b(t('Save'))))}
      ${step(tf('A few seconds later {0} shows up here. Open it and add the bot to a server you own - a new, empty one is fine.', b(t('Add the bot to your server'))))}
      ${step(tf('Press {0} here. In Discord, type {1} and the code it shows - in the server, or in a private chat with the bot (click the bot in the member list and send it a message).', b(t('Connect Discord')), '<code>/connect</code>'))}
      ${step(tf('Done. Type {0} for a summary, {1} for every command, or {2} to run any console command.', '<code>/status</code>', '<code>/help</code>', '<code>/nocat</code>'))}
    </ol><p class="muted small">${esc(t('Keep the token private - anyone who has it can control your bot. Only your connected Discord account can use the commands, and in a server only you see the answers.'))}</p></details>`;
}

async function notifyTest(btn) {
  btn.disabled = true;
  const old = btn.textContent;
  btn.textContent = t('Sending...');
  const r = await post('/api/notify/test', {}).catch(() => null);
  btn.disabled = false;
  btn.textContent = old;
  // Each line comes with whether it worked. Matching the words instead only ever worked in English - the lines
  // arrive translated, so a failed test showed as a green toast in every other language.
  const lines = (r && r.Results) || [{ Ok: false, Text: t("Couldn't reach nocat.farm") }];
  toast(lines.map((l) => l.Text).join(' · '), lines.some((l) => !l.Ok));
}

function editAndRender(name, value) {
  pending[name] = value;
  renderSettings();
}

function refreshPreviews() {
  if (!schema || !config) return;
  const values = settingsValues();
  if (!values) return;
  document.querySelectorAll('#settingsBody .section').forEach((sec) => {
    const title = sec.querySelector('h3');
    const prev = sec.querySelector('.preview');
    if (!title || !prev) return;
    // Update the panel's CONTENTS, never replace the node: swapping an element out from under a live form can
    // move focus, and this runs on every keystroke.
    //
    // Keyed on data-section, which holds the ENGLISH name. sectionIntro switches on that name, so reading the
    // heading's own text matched nothing once the heading was translated - every live preview would have
    // frozen in any language but English.
    const html = sectionIntro(title.dataset.section || title.textContent, values);

    if (html) {
      const tmp = document.createElement('div');
      tmp.innerHTML = html;
      const fresh = tmp.firstElementChild;
      if (fresh) prev.innerHTML = fresh.innerHTML;
    }
  });
}

function clearSecret(name) {
  if (!confirm(t('Erase the stored value? There is no undo.'))) return;
  editAndRender(name, CLEAR_SECRET);
}

/// An appID, or the store URL somebody pasted instead of one. Returns 0 when it's neither.
function parseAppId(raw) {
  const text = String(raw || '').trim().replace(/,$/, '');
  if (!text) return 0;
  const m = text.match(/\/app\/(\d+)/) || text.match(/^(\d+)$/);
  return m ? parseInt(m[1]) : 0;
}

function addApp(name, input) {
  const raw = input.value.trim().replace(/,$/, '');
  if (!raw) return;
  const m = raw.match(/\/app\/(\d+)/) || raw.match(/^(\d+)$/);
  if (!m) { toast(t('That is not an appID'), true); return; }
  const list = (pending[name] !== undefined ? pending[name] : settingsValues()[name] || []).slice();
  const id = parseInt(m[1]);
  if (!list.includes(id)) list.push(id);
  input.value = '';
  editAndRender(name, list);
}

function removeApp(name, index) {
  const list = (pending[name] !== undefined ? pending[name] : settingsValues()[name] || []).slice();
  list.splice(index, 1);
  editAndRender(name, list);
}

function updateSaveButton() {
  const n = Object.keys(pending).length;
  const btn = $('saveBtn');
  btn.disabled = n === 0;
  btn.textContent = n === 0 ? t('Save') : n === 1 ? t('Save one change') : tf('Save {0} changes', n);
}

/// One save at a time. A double click on Save with a new dashboard password sent two: the first signed every browser
/// out, the second went with the old session and got the sign-in page back.
let savingSettings = false;
async function saveSettings() {
  if (savingSettings) return;
  savingSettings = true;
  try { await saveSettingsOnce(); } finally { savingSettings = false; }
}

async function saveSettingsOnce() {
  const target = settingsTarget;
  const edits = { ...pending };

  // Re-read first, then apply only what was actually edited. Posting the snapshot taken when the page was
  // opened would quietly revert anything changed from the console (or another tab) in the meantime.
  await loadConfig();
  const base = target === GLOBAL ? config.Global : config.Bots[target];
  if (!base) { toast(t('That account is gone'), true); settingsTarget = GLOBAL; renderSettings(); return; }

  const body = { ...base, ...edits };
  const res = target === GLOBAL
    ? await postGlobal(body)
    : await postBot(target, body);

  if (!res.ok) { toast(res.error || t('Save failed'), true); return; }

  // A new dashboard password signs every browser out, this one included - the server hands back a fresh session
  // for whoever saved it, so keep that or the very next poll would bounce us to the sign-in box.
  if (res.Token) {
    token = res.Token;
    localStorage.setItem('nocatfarm-token', token);
  }

  // The pacer panel is "refreshed whenever settings are saved" - forget the cached read so it really is.
  if (target !== GLOBAL) { pacerFor = null; loadPacer(target); }

  // Changing the language has to take effect NOW, not on the next reload. The walkthrough's picker has always
  // applied it immediately; saving the same setting from this page saved it and then carried on in the old
  // language, which reads as the setting having done nothing at all.
  if (edits.Language !== undefined) {
    await loadLanguage(edits.Language);
    translateChrome();
  }

  const restart = (res.RestartNeeded || []).filter(Boolean);
  const adjusted = (res.Adjusted || []).filter(Boolean);

  if (adjusted.length) toast(adjusted.join(' · '), true);
  else if (!restart.length) toast(t('Saved.'));
  else toast(restart.length > 1
    ? tf('Saved. {0} apply after a restart.', restart.join(', '))
    : tf('Saved. {0} applies after a restart.', restart[0]));

  pending = {};
  await loadConfig();
  renderSettings();
  refresh();
}

// ── loop ─────────────────────────────────────────────────────────────
// The welcome screen is an overlay, not a branch: an account added from the console (or a second browser tab)
// has to dismiss it on its own, or you end up staring at a setup screen for a farm that's already running.
let askedAboutAsf = false;
let lastAlertsHtml = null;
let lastPromptAsked = null;   // the Steam Guard question the alert box holds, so what's typed survives a redraw

function syncWelcome() {
  const show = state && !state.Bots.length && !sessionStorage.getItem('skip-welcome');
  $('welcome').classList.toggle('hidden', !show);
  $('app').classList.toggle('hidden', !!show);

  if (show && !askedAboutAsf) {
    askedAboutAsf = true;
    checkForIdlers();
  }
}

// One timer, re-armed only when the interval actually changes.
function armPolling(seconds) {
  if (document.hidden) return;   // see the visibilitychange handler below
  if (pollTimer && pollSeconds === seconds) return;
  if (pollTimer) clearInterval(pollTimer);
  pollSeconds = seconds;
  pollTimer = setInterval(refresh, Math.max(1, seconds) * 1000);
}

// A tab nobody is looking at has no reason to ask every few seconds - left open in the background it was 17,000
// requests a day that nobody read. Stop while hidden; catch up the moment it is looked at again (refresh re-arms).
document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    if (pollTimer) clearInterval(pollTimer);
    pollTimer = null;
  } else {
    refresh();
  }
});

async function refresh() {
  try {
    state = await api('/api/status');
    refreshSeconds = state.RefreshSeconds || refreshSeconds;
    armPolling(refreshSeconds);
    syncWelcome();
    syncTelegramConnect();
    syncDiscordConnect();
    if (tutorialSignin) renderSignin();
    if (view === 'phone') loadPhone();

    // nocat.farm restarting resets its sequence numbers. Without noticing that, "everything after seq 812"
    // matches nothing forever and the log tab silently freezes.
    if (bootId !== null && state.BootId !== bootId) { logLines = []; logClearedAt = 0; }
    bootId = state.BootId;

    const since = logLines.length ? logLines[logLines.length - 1].Seq : logClearedAt;
    const got = since ? await api('/api/log?since=' + since) : await api('/api/log?n=300');
    // Only what's newer than the last line held NOW: two refreshes in flight at once (the timer and one after a
    // button) asked from the same place and both appended it, so the Log showed every line twice.
    const last = logLines.length ? logLines[logLines.length - 1].Seq : 0;
    const fresh = got.filter((e) => e.Seq > last);
    if (fresh.length) {
      logLines = logLines.concat(fresh).slice(-1000);
      if (view === 'log') renderLog();
    }

    render();
  } catch {
    // showLogin already fired, or the server is restarting - the next tick retries. There has to BE a next tick:
    // coming back to a hidden tab stops the timer and only a refresh that worked started it again, so one that failed
    // (the phone's network still waking up) left the page frozen until it was reloaded.
    if (!pollTimer && $('login').classList.contains('hidden')) armPolling(refreshSeconds);
  }
}

async function boot() {
  const ping = await fetch('/api/ping').then((r) => r.json()).catch(() => null);
  if (!ping) { showLogin(t("Can't reach nocat.farm.")); return; }
  if (ping.needsPassword && !ping.authorised && !token) { showLogin(); return; }

  $('login').classList.add('hidden');

  state = await api('/api/status').catch(() => null);
  if (!state) { showLogin(); return; }

  // Nothing configured yet: the first screen is adding an account, not an empty dashboard.
  syncWelcome();

  // localStorage/the DOM attr got us through the first paint; keep that for now.
  setTheme(document.documentElement.getAttribute('data-theme') || 'dark', false);
  commands = await api('/api/commands').catch(() => []);
  await loadConfig().catch(() => {});

  // NOW config exists, so the server-stored theme (which is meant to follow you between browsers) can actually
  // be applied - the old code read config.Global.Theme one line BEFORE loadConfig, when config was still null.
  if (config && config.Global && config.Global.Theme) {
    setTheme(config.Global.Theme, false);
  }

  // Language, once config exists, and before the first render so nothing flashes English and then changes.
  await loadLanguage(config && config.Global ? config.Global.Language : 'en');
  translateChrome();

  go(view);
  await refresh();   // arms the single polling timer

  if (shouldShowTutorial()) { startTutorial(); return; }

  // "Coming from another idler" in the installer, on a copy that already has its walkthrough behind it (installed over
  // an existing config): still open on that import, once.
  const pending = await api('/api/import/pending').catch(() => null);
  if (pending && pending.Pending) {
    post('/api/import/pending/clear', {}).catch(() => {});
    openImport(pending.Pending.Tool, pending.Pending.Path);
  }
}

boot();
