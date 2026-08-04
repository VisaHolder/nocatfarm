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
let commands = [];
let logLines = [];
let localLines = [];
let history = JSON.parse(localStorage.getItem('nocatfarm-history') || '[]');
let historyAt = -1;
let view = location.hash.replace('#', '') || 'overview';
let acctFilter = '';

// A symbol, not the string "global" - an account legitimately called "global" would otherwise hijack the pane
// and post its edits into the global config file.
const GLOBAL = Symbol('global');
const CLEAR_SECRET = ' clear';   // matches WebHost.ClearSecret
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

async function doLogin(e) {
  e.preventDefault();
  const res = await fetch('/api/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ Password: $('pw').value })
  }).then((r) => r.json()).catch(() => ({ ok: false }));

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
function paint(id, html) {
  const el = $(id);

  if (!el || ((el.innerHTML === html) && (html !== ''))) {
    return;
  }

  el.innerHTML = html;
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
}

// ── tooltips ─────────────────────────────────────────────────────────
// One handler for the whole document, so anything with data-tip gets a tooltip without registering anything.
document.addEventListener('mouseover', (e) => {
  const el = e.target.closest('[data-tip]');
  if (!el) return;
  const tip = $('tip');
  tip.textContent = el.dataset.tip;
  tip.classList.remove('hidden');
  const r = el.getBoundingClientRect();
  const w = Math.min(340, tip.offsetWidth);
  tip.style.left = Math.max(8, Math.min(window.innerWidth - w - 12, r.left)) + 'px';
  tip.style.top = (r.bottom + 8 + tip.offsetHeight > window.innerHeight ? r.top - tip.offsetHeight - 8 : r.bottom + 8) + 'px';
});
document.addEventListener('mouseout', (e) => {
  if (e.target.closest('[data-tip]')) $('tip').classList.add('hidden');
});
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

async function refreshInventory(name) {
  await post(`/api/bots/${encodeURIComponent(name)}/inventory/refresh`, {});
  toast(tf('Reading {0}’s inventory again…', name));
}

// The hover breakdown: which games hold the value, biggest first.
const nlChar = String.fromCharCode(10);
function valueTip(b) {
  if (b.InventoryOn === false) return tf('Not being valued. To switch it back on, go to Settings, pick {0}, open Trades and tick "Work out what its inventory is worth".', b.Name);
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

  view = name;
  location.hash = name;
  document.querySelectorAll('.navitem').forEach((n) => n.classList.toggle('active', n.dataset.view === name));
  document.querySelectorAll('.view').forEach((v) => v.classList.toggle('hidden', v.id !== 'view-' + name));

  if (name === 'settings') { loadConfig().then(renderSettings); }
  if (name === 'rep4rep') { loadRep4Rep(); }
  if (name === 'console') { $('cmd').focus(); renderCommandList(); }
  if (name === 'log') renderLog();
  if (name === 'plugins') loadPlugins();
  render();
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
  const map = { '1': 'overview', '2': 'accounts', '3': 'rep4rep', '4': 'log', '5': 'console', '6': 'settings' };
  if (map[e.key]) go(map[e.key]);
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
  $('navPoints').textContent = r4rOn() && state.Rep4RepToken ? state.Points : '';

  // rail chips
  const counts = {};
  bots.forEach((b) => { counts[b.Group] = (counts[b.Group] || 0) + 1; });
  $('railChips').innerHTML = Object.keys(STATUS_META)
    .filter((k) => counts[k])
    .map((k) => `<span class="chip ${k}" data-tip="${esc(t(STATUS_META[k].tip))}" onclick="filterTo('${k}')"><i class="dot"></i>${esc(t(STATUS_META[k].label))}<b>${counts[k]}</b></span>`)
    .join('') || `<span class="muted small">${esc(t('no accounts yet'))}</span>`;

  paint('railStats', `
    <dt data-tip="${esc(t("Trading cards still to drop across every account that's farming."))}">${esc(t('Cards left'))}</dt><dd>${state.CardsLeft}</dd>
    ${r4rOn() && state.Rep4RepToken ? `<dt data-tip="${esc(t("Points you can spend on rep4rep. Pending ones are comments rep4rep hasn't verified yet."))}">${esc(t('Points'))}</dt><dd>${state.Points}${state.PendingPoints ? ' <span class="muted">+' + state.PendingPoints + '</span>' : ''}</dd>` : ''}
    <dt data-tip="${esc(t('How long nocat.farm has been running.'))}">${esc(t('Up'))}</dt><dd>${hm(state.UptimeMinutes)}</dd>`);

  renderAlerts();

  if (view === 'overview') renderOverview();
  if (view === 'accounts') renderAccounts();
  if (view === 'rep4rep') renderRep4RepPacing();
}

function filterTo(group) { acctFilter = acctFilter === group ? '' : group; go('accounts'); }

function renderAlerts() {
  const out = [];

  if (state.Prompt) {
    out.push(`<div class="alert warn">
      <span data-tip="${esc(t("Type the code from the Steam app on your phone, or from your email. nocat.farm can't finish logging in until you do."))}">${esc(state.Prompt)}</span>
      <input id="promptInput" type="${state.PromptSecret ? 'password' : 'text'}" autocomplete="off">
      <button onclick="sendPrompt()">${esc(t('Submit'))}</button></div>`);
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
    $('alerts').innerHTML = html;
    const input = $('promptInput');
    if (input) { input.focus(); input.onkeydown = (e) => { if (e.key === 'Enter') sendPrompt(); }; }
  }
}

function goSetting(name) {
  settingsTarget = GLOBAL;
  go('settings');
  setTimeout(() => {
    const el = document.querySelector(`[data-setting="${name}"]`);
    if (el) { el.scrollIntoView({ block: 'center' }); el.focus(); }
  }, 200);
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
  const busy = bots.filter((b) => b.Group === 'farming' || b.Group === 'idling');

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
      ver.classList.remove('update');
    }
  }

  // The Plugins tab stays put whether plugins are on or off, and says which inside.
  //
  // Hiding it was meant to read as "this is off". It read as "this app has no plugins": the panel behind it
  // explains what the switch does and offers to take you to it, and hiding the tab was the one thing making
  // that explanation unreachable by anybody who had not already found the setting.
  const navPlug = $('navPlugins');
  if (navPlug) {
    navPlug.classList.remove('hidden');
    navPlug.classList.toggle('off', !state.PluginsOn);
    const tag = navPlug.querySelector('.navtag');
    if (tag) tag.textContent = state.PluginsOn ? '' : t('off');
  }

  // The button that installs it, beside the chip that announces it.
  //
  // Separate from the link on purpose: the link is "what changed", this is "do it". Nothing updates on its
  // own and there is no setting to make it - plenty of people would rather keep a build that works than take
  // whatever is newest, and an update that lands unasked mid-session costs them a night's farming.
  const upd = $('updateBtn');
  if (upd) {
    const busy = state.UpdateBusy;
    upd.classList.toggle('hidden', !state.UpdateAvailable);
    upd.disabled = !!busy;
    upd.textContent = busy ? (state.UpdateProgress || t('working…')) : tf('Update to {0}', state.UpdateAvailable || '');
    upd.dataset.tip = busy
      ? t('Downloading. It restarts by itself when it lands.')
      : tf('Download {0} and restart into it. Your accounts, tokens, settings and logs are left exactly as they are.', state.UpdateAvailable || '');
  }

  paint('glance', bots.length ? `<div class="tablewrap"><table>
    <tr><th>${esc(t('Account'))}</th><th>${esc(t('State'))}</th><th>${esc(t('Playing'))}</th><th>${esc(t('Cards'))}</th><th data-tip="${esc(t("What everything in this account's inventory would fetch at the market's median price. Items with no market listing count as nothing; items it merely can't sell right now (trade holds, bans) are still counted at what they are worth."))}">${esc(t('Value'))}</th>${r4rOn() ? `<th>${esc(t('rep4rep'))}</th>` : ''}<th>${esc(t('Up'))}</th></tr>
    ${bots.map((b) => `<tr class="click" data-act="cards" data-bot="${esc(b.Name)}">
      <td><b>${esc(b.Name)}</b></td>
      <td><span class="chip ${b.Group}"><i class="dot"></i>${esc(b.Status)}</span></td>
      <td>${esc(b.Playing || '—')}</td>
      <td>${b.Cards || '—'}</td>
      <td data-tip="${esc(valueTip(b))}">${b.InventoryValue > 0 ? usd(b.InventoryValue) + (b.InventoryPending > 0 ? '<span class="muted">+</span>' : '') + valueDelta(b) : (b.InventoryOn === false || b.InventoryReady ? '—' : '<span class="muted">…</span>')}</td>
      ${r4rOn() ? `<td>${b.Rep4Rep ? b.Rep4RepToday + '/' + b.Rep4RepCap : '—'}</td>` : ''}
      <td>${b.UptimeMinutes ? hm(b.UptimeMinutes) : '—'}</td></tr>`).join('')}
    </table></div>`
    : `<p class="muted">${esc(t('No accounts yet.'))} <a href="#console" onclick="go('console')">${esc(t('Add one'))}</a> ${esc(t('or type'))} <code>add mybot mysteamlogin</code>.</p>`);

  renderToday();

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

// ── render: accounts ─────────────────────────────────────────────────
function renderAccounts() {
  // Never rebuild the list out from under a drag in progress.
  if (dragging) return;

  const q = ($('acctSearch').value || '').toLowerCase();

  const chips = Object.keys(STATUS_META).map((k) =>
    `<span class="chip ${k} ${acctFilter === k ? 'on' : ''}" data-tip="${esc(t(STATUS_META[k].tip))}" onclick="acctFilter='${acctFilter === k ? '' : k}';render()"><i class="dot"></i>${esc(t(STATUS_META[k].label))}</span>`).join('');

  if (chips !== $('acctFilters').innerHTML) {
    $('acctFilters').innerHTML = chips;
  }

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
      ondragstart="dragStart(event)" ondragover="dragOver(event)" ondragend="dragEnd()" ondrop="dragEnd()">
      <div class="bot-head">
        <span class="bot-name" title="${esc(b.Name)}" data-tip="${esc(t('Drag this card to move the account. The order is used everywhere - here, the app window and the console.'))}">${esc(b.Name)}</span>
        <span class="chip ${b.Group}" data-tip="${esc(t(STATUS_META[b.Group].tip))}"><i class="dot"></i>${esc(b.Status)}</span>
      </div>
      <div class="bot-login" title="${esc(b.Login)}">${esc(b.Login)}${b.SteamId !== '0' ? ' · ' + esc(b.SteamId) : ''}</div>
      ${b.Notes ? `<div class="bot-notes" title="${esc(b.Notes)}">${esc(b.Notes)}</div>` : ''}
      ${b.Guard ? `<div class="bot-guard">${esc(tf('Waiting on you: {0}', b.Guard))}</div>` : ''}
      <div class="bot-playing" title="${esc(b.Playing || '')}">${b.Playing ? esc(b.Playing) : `<span class="real">${esc(t('not playing anything'))}</span>`}</div>
      ${b.Online ? `<div class="bot-persona ${b.PersonaHidden ? 'hidden-persona' : ''}"
        data-tip="${esc(t("What your friends list shows for this account. The GAME comes straight back from Steam, so it's what other people genuinely see; the status is what nocat.farm set it to. Human mode changes the status by itself: invisible overnight, away on a break, Snooze over a meal."))}">${esc(t('your friends see:'))} <b>${esc(b.Persona)}</b>${b.Seen ? ` · <b>${esc(b.Seen)}</b>` : ''}</div>` : ''}
      ${b.NameNotShowing
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
        ${b.InventoryValue > 0 || b.InventoryReady ? `<button data-tip="${esc(t("Read this account's inventory again. Prices are kept for a day, so only what changed is looked up."))}" onclick="refreshInventory('${esc(b.Name)}')">${esc(t('Value'))}</button>` : ''}
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
  const known = (state && state.Bots ? state.Bots : []).map((b) => b.Name);
  const hidden = known.filter((n) => !visible.includes(n));

  // Hidden accounts keep their existing relative order, which is what config already holds.
  const saved = (config && config.AccountOrder) || [];
  hidden.sort((a, b) => {
    const ia = saved.indexOf(a), ib = saved.indexOf(b);
    return (ia < 0 ? 1e9 : ia) - (ib < 0 ? 1e9 : ib);
  });

  const order = [...visible, ...hidden];
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
    </div>
    <p id="addError" class="error"></p>
    <div class="actions"><button onclick="createBot()">${esc(t('Add account'))}</button><button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button></div>`);
  setTimeout(() => $('a-name').focus(), 50);
}

async function createBot() {
  const res = await post('/api/bots', { Name: $('a-name').value.trim(), SteamLogin: $('a-login').value.trim(), Password: $('a-pass').value });
  if (!res.ok) { $('addError').textContent = res.error; return; }
  closeModal();
  refresh();
}

// ── importing from ArchiSteamFarm ────────────────────────────────────
// Retyping five accounts by hand is the reason people don't switch tools. If an ASF install is sitting there,
// say so before asking anyone to type anything.
async function checkForAsf() {
  const found = await api('/api/import/asf').catch(() => null);
  if (!found || !found.Found || !found.Accounts.length) return;

  const withTokens = found.Accounts.filter((a) => a.HasToken).length;
  $('importBanner').classList.remove('hidden');
  const n = found.Accounts.length;
  $('importBanner').innerHTML = `
    <div class="alert info" style="margin-bottom:18px;display:block">
      <b>${esc(n === 1 ? t('Found an ArchiSteamFarm install with one account.') : tf('Found an ArchiSteamFarm install with {0} accounts.', n))}</b>
      <div class="muted small" style="margin:4px 0 10px">
        ${esc(found.Path)}<br>
        ${esc(withTokens
          ? tf('{0} of them can come across with their saved logins — no passwords, no Steam Guard codes.', withTokens)
          : t('You will still need to sign in to each one once.'))}
      </div>
      <button onclick="runImport()">${esc(n === 1 ? t('Import one account') : tf('Import {0} accounts', n))}</button>
    </div>`;
}

function showImport() {
  api('/api/import/asf').then((found) => {
    if (!found || !found.Found) {
      modal(`<h2>${esc(t('Import from ArchiSteamFarm'))}</h2>
        <p class="muted">${t('No ASF install found automatically. Point at its {0} folder:').replace('{0}', '<code>config</code>')}</p>
        <input id="importPath" type="text" placeholder="C:\\...\\ArchiSteamFarm\\config" autocomplete="off">
        <p id="importError" class="error"></p>
        <div class="actions"><button onclick="runImport()">${esc(t('Import'))}</button><button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button></div>`);
      return;
    }

    modal(`<h2>${esc(t('Import from ArchiSteamFarm'))}</h2>
      <p class="muted small">${esc(found.Path)}</p>
      <div class="tablewrap"><table>
        <tr><th>${esc(t('Account'))}</th><th>${esc(t('Steam login'))}</th><th>${esc(t('Sign-in'))}</th></tr>
        ${found.Accounts.map((a) => `<tr><td><b>${esc(a.Name)}</b></td><td class="muted">${esc(a.SteamLogin)}</td>
          <td>${a.HasToken ? `<span class="pill good">${esc(t('token — no password needed'))}</span>`
            : a.HasPassword ? `<span class="pill warn">${esc(t('password only'))}</span>`
            : `<span class="pill">${esc(t('will ask on first login'))}</span>`}</td></tr>`).join('')}
      </table></div>
      <p class="muted small" style="margin-top:12px">${esc(t("Accounts you already have here are left alone. Don't run ASF and nocat.farm on the same account at once — they'd take turns kicking each other off Steam."))}</p>
      <p id="importError" class="error"></p>
      <div class="actions"><button onclick="runImport()">${esc(tf('Import {0}', found.Accounts.length))}</button><button class="ghost" onclick="closeModal()">${esc(t('Cancel'))}</button></div>`);
  });
}

async function runImport() {
  const pathEl = $('importPath');
  const res = await post('/api/import/asf', { Path: pathEl ? pathEl.value.trim() : '' });

  if (!res.ok) {
    const err = $('importError');
    if (err) err.textContent = res.error; else toast(res.error, true);
    return;
  }

  closeModal();
  $('importBanner').classList.add('hidden');
  sessionStorage.setItem('skip-welcome', '1');
  const done = res.Imported === 1 ? t('Imported one account') : tf('Imported {0} accounts', res.Imported);
  toast(done);
  await loadConfig();
  await refresh();
  go('accounts');

  if (res.Notes && res.Notes.length) {
    modal(`<h2>${esc(done)}</h2>
      <div class="rows">${res.Notes.map((n) => `<div class="row"><span>${esc(n)}</span></div>`).join('')}</div>
      <p class="muted small" style="margin-top:12px">${t("They're added but not started. Press Start on each, or run {0}.").replace('{0}', '<code>start all</code>')}</p>
      <div class="actions"><button onclick="closeModal();run('start all')">${esc(t('Start them all'))}</button><button class="ghost" onclick="closeModal()">${esc(t('Not yet'))}</button></div>`);
  }
}

async function createFirstBot() {
  const res = await post('/api/bots', { Name: $('w-name').value.trim(), SteamLogin: $('w-login').value.trim(), Password: $('w-pass').value });
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
          <input type="checkbox" ${p.Enabled ? 'checked' : ''} onchange="togglePlugin('${esc(p.Name)}', this.checked)"><span></span>
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
    const set = `setPluginSetting('${esc(p.Name)}','${esc(sett.Name)}',this)`;
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
    await api('/api/plugins/setting', { method: 'POST', body: JSON.stringify({ Plugin: plugin, Name: name, Value: value }) });
    toast(tf('{0} saved', name));
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
  modal(`
    <h2>${esc(tf('Update to {0}?', to))}</h2>
    <p>${tf('It downloads {0}, closes, swaps itself over and starts back up. Takes about a minute.', `<b>${esc(to)}</b>`)}</p>
    <ul class="muted small">
      <li>${esc(t('Your accounts, login tokens, settings and logs are left exactly as they are.'))}</li>
      <li>${esc(t('Every account signs out of Steam for a few seconds while it restarts.'))}</li>
      <li>${esc(t('Anything mid-session - a farm, a grind - stops there and picks up after.'))}</li>
      <li>${esc(t('If the download fails, nothing is changed and the current version keeps running.'))}</li>
    </ul>
    <div class="actions">
      <button class="ghost" onclick="closeModal()">${esc(t('Not now'))}</button>
      <button id="updateGo" onclick="doUpdate(true)">${esc(t('Download and restart'))}</button>
    </div>`);
}

async function doUpdate(confirmed) {
  if (!confirmed) { askUpdate(); return; }

  closeModal();
  const btn = $('updateBtn');
  if (btn) { btn.disabled = true; btn.textContent = t('working…'); }

  try {
    const r = await api('/api/update', { method: 'POST' });
    toast(r && r.Message ? r.Message : t('Downloading. It restarts by itself when it lands.'));
  } catch (e) {
    toast(tf('Update failed: {0}', e.message || e));
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

function setTheme(name, save) {
  document.documentElement.setAttribute('data-theme', name);
  try { localStorage.setItem('nocatfarm-theme', name); } catch (_) { /* private mode - it just will not stick */ }
  const btn = $('themeToggle');
  if (btn) btn.textContent = name === 'light' ? 'dark' : 'light';

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
  if (config && config.Global) {
    config.Global.Language = code;
    await post('/api/config', config.Global).catch(() => {});
  }

  await loadLanguage(code);
  translateChrome();
  renderTutorial();
}
let tutorialAsf = null;

function shouldShowTutorial() {
  if (!config || !state) return false;
  if (config.Global && config.Global.TutorialDone) return false;
  return Object.keys(config.Bots || {}).length === 0;
}

async function startTutorial() {
  tutorialStep = 0;
  tutorialAsf = await api('/api/import/asf').catch(() => null);
  renderTutorial();
}

function renderTutorial() {
  const found = tutorialAsf && tutorialAsf.Found && (tutorialAsf.Accounts || []).length > 0;

  const steps = [
    {
      // Language first, before a word of the rest is read. Skipping leaves it English, which is also what an
      // untranslated string falls back to - so there is no way to end up looking at blanks.
      title: t('Language'),
      body: `<p>${esc(t('Pick the language for this dashboard. You can change it later under Settings › Global.'))}</p>
        <div class="langpick">${LANGUAGES.map((l) =>
          `<span class="p ${(config && config.Global && config.Global.Language || 'en') === l.code ? 'on' : ''}"
            onclick="pickTutorialLanguage('${l.code}')">${esc(l.name)}</span>`).join('')}</div>
        <p class="muted small">${esc(t("Anything a translation hasn't covered yet stays in English rather than showing a blank, so a part-finished language is still perfectly usable. The console and the log stay in English."))}</p>`,
      next: t('Continue'),
    },
    {
      title: t('Welcome to nocat.farm'),
      body: `<p>${esc(t('It signs your Steam accounts in, plays games so the hours count, farms trading cards and posts rep4rep comments - all from this machine. Your accounts never leave it.'))}</p>
        <p class="muted small">${esc(t('This takes about a minute. You can skip it and come back from the Overview page.'))}</p>`,
      next: t('Start'),
    },
    {
      title: found ? t('Bring your ArchiSteamFarm accounts across') : t('Add your first account'),
      body: found
        ? `<p>${tf('Found an ArchiSteamFarm setup at {0} with {1}.', `<code>${esc(tutorialAsf.Path)}</code>`,
             `<b>${esc(tutorialAsf.Accounts.length === 1 ? t('one account') : tf('{0} accounts', tutorialAsf.Accounts.length))}</b>`)}</p>
           <ul class="muted small">
             ${tutorialAsf.Accounts.map((a) => `<li>${esc(a.Name)} - ${esc(a.SteamLogin)}${a.HasToken ? ' ' + esc(t('(login token comes across, so no password needed)')) : ''}</li>`).join('')}
           </ul>
           <p class="muted small">${esc(t('Importing copies the accounts and their login tokens. It changes nothing in ArchiSteamFarm.'))}</p>`
        : `<p>${tf('Add the Steam account you want it to run. You will be asked for the password {0}, and for a Steam Guard code - after that it remembers a login token and never needs the password again.', `<b>${esc(t('once'))}</b>`)}</p>
           <p class="muted small">${esc(t('No ArchiSteamFarm install was found on this machine, so there is nothing to import.'))}</p>`,
      next: found ? t('Import them') : t('Add an account'),
      act: found ? doTutorialImport : () => { closeTutorial(); go('accounts'); showAddAccount(); },
    },
    {
      title: t('Tell it what to play'),
      body: `<p>${esc(t('An account does nothing until it knows what to run. Two ways:'))}</p>
        <ul class="muted small">
          <li>${tf('{0} - it works through everything in your library that still has drops, then stops. Nothing to configure.', `<b>${esc(t('Trading cards'))}</b>`)}</li>
          <li>${tf('{0} - it keeps a believable daily routine: a few games, one at a time, with breaks, meals and a bedtime. Use this on an account you care about.', `<b>${esc(t('Human mode'))}</b>`)}</li>
        </ul>
        <p class="muted small">${esc(t('Both live under Settings, per account.'))}</p>`,
      next: t('Next'),
    },
    {
      title: t('rep4rep, if you want it'),
      body: `<p>${esc(t("Optional. Your accounts post comments on other people's Steam profiles and earn points you can spend on comments for your own."))}</p>
        <p class="muted small">${tf('Needs a free account - {0} - and the API token pasted into Settings. Skip it entirely if you only want cards and playtime.',
          '<a href="https://rep4rep.com/?r=reap" target="_blank" rel="noopener">rep4rep.com ↗</a>')}</p>`,
      next: t('Next'),
    },
    {
      title: t("That's it"),
      body: `<p>${tf('Everything has an explanation attached - hover the {0} beside any setting.', '<i class="info" style="display:inline-flex"></i>')}</p>
        <p class="muted small">${tf('The Console tab does anything the other tabs do, by typing. {0} lists it all.', '<code>help</code>')}</p>`,
      next: t('Finish'),
      act: closeTutorial,
    },
  ];

  const st = steps[Math.min(tutorialStep, steps.length - 1)];
  const last = tutorialStep >= steps.length - 1;

  modal(`
    <div class="muted small" style="letter-spacing:1px">${esc(tf('STEP {0} OF {1}', tutorialStep + 1, steps.length))}</div>
    <h2>${esc(st.title)}</h2>
    ${st.body}
    <div class="actions">
      ${tutorialStep > 0 ? `<button class="ghost" onclick="tutorialStep--;renderTutorial()">${esc(t('Back'))}</button>` : ''}
      <button class="ghost" onclick="closeTutorial()">${esc(last ? t('Close') : t('Skip'))}</button>
      <button id="tutNext">${esc(st.next)}</button>
    </div>`);

  $('tutNext').onclick = st.act || (() => { tutorialStep++; renderTutorial(); });
}

async function doTutorialImport() {
  const btn = $('tutNext');
  btn.disabled = true;
  btn.textContent = t('Importing…');

  const res = await post('/api/import/asf', { Path: tutorialAsf.Path });

  if (!res.ok) {
    toast(res.error || t('Import failed'), true);
    btn.disabled = false;
    btn.textContent = t('Import them');
    return;
  }

  toast(res.Imported === 1 ? t('Imported one account') : tf('Imported {0} accounts', res.Imported || 0));
  await loadConfig();
  refresh();
  tutorialStep++;
  renderTutorial();
}

async function closeTutorial() {
  closeModal();
  // Marked done however it was dismissed - being shown it again after skipping is worse than never seeing it.
  await post('/api/tutorial/done', {}).catch(() => {});
  if (config && config.Global) config.Global.TutorialDone = true;
}

// help, /help and /? open the reference rather than dumping 60 lines into the output pane, where it pushes
// everything you were reading off the top and cannot be scrolled independently.
function helpModal(filter) {
  const q = (filter || '').trim().toLowerCase();
  const groups = {};

  (commands || []).forEach((c) => {
    if (q && !(c.Name + ' ' + c.Args + ' ' + c.Help).toLowerCase().includes(q)) return;
    (groups[c.Group] = groups[c.Group] || []).push(c);
  });

  const body = Object.keys(groups).length
    ? Object.keys(groups).map((g) => `<div class="grp">${esc(g)}</div>${groups[g].map((c) => `
        <div class="c" onclick="useCommand('${esc(c.Name)}')">
          <code>${esc(c.Display || c.Name)}${c.Args ? ' ' + esc(c.Args) : ''}</code>
          <span class="h">${esc(c.Help)}</span>
        </div>`).join('')}`).join('')
    : `<p class="muted">${esc(t('Nothing matches.'))}</p>`;

  modal(`
    <h2>${esc(t('Commands'))}</h2>
    <input type="text" id="helpFilter" autocomplete="off" spellcheck="false" placeholder="${esc(t('filter…'))}"
           oninput="helpModal(this.value)" value="${esc(filter || '')}">
    <div class="helplist">${body}</div>
    <div class="actions"><button class="ghost" onclick="closeModal()">${esc(t('Close'))}</button></div>`);

  const f = $('helpFilter');
  if (f) { f.focus(); f.setSelectionRange(f.value.length, f.value.length); }
}

function modal(html) { $('modalCard').innerHTML = html; $('modal').classList.remove('hidden'); }
function closeModal() { $('modal').classList.add('hidden'); }
$('modal').addEventListener('click', (e) => { if (e.target.id === 'modal') closeModal(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });

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
  }
});

document.addEventListener('click', (e) => {
  const el = e.target.closest('[data-task]');
  if (!el) return;
  el.disabled = true;
  el.textContent = t('posting…');
  post('/api/rep4rep/tasks/' + encodeURIComponent(el.dataset.task) + '/post?bot=' + encodeURIComponent($('r4rBot').value))
    .then((res) => { toast(res.message, !res.ok); loadTasks(); refresh(); });
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
  for (const b of state.Bots) {
    if (b.Rep4Rep) continue;
    const base = config.Bots[b.Name];
    if (!base) continue;
    await post('/api/bots/' + encodeURIComponent(b.Name) + '/config', { ...base, Rep4Rep: true });
  }
  toast(t('rep4rep commenting switched on for every account'));
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

function renderRep4RepPacing() {
  if (!state) return;
  const keys = ['Rep4RepDailyCap', 'Rep4RepGapMinMinutes', 'Rep4RepGapMaxMinutes', 'Rep4RepStartHour', 'Rep4RepEndHour'];
  const defs = keys.map((k) => (schema ? schema.Bot.find((d) => d.Name === k) : null));

  $('r4rPacing').innerHTML = `<div class="tablewrap"><table>
    <tr><th>${esc(t('Account'))}</th>${defs.map((d, i) => `<th${d ? ` data-tip="${esc(tSetting(d, 'tip'))}"` : ''}>${esc(d ? tSetting(d, 'label') : keys[i])}</th>`).join('')}</tr>
    ${state.Bots.map((b) => `<tr>
      <td><b>${esc(b.Name)}</b></td>
      ${keys.map((k) => `<td><input type="number" style="max-width:80px" value="${config && config.Bots[b.Name] ? config.Bots[b.Name][k] : ''}"
        data-qs="${k}" data-bot="${esc(b.Name)}"></td>`).join('')}
      </tr>`).join('')}
    </table></div>`;
}

// Goes through the typed endpoint rather than the command line, so an account name with a space in it can't
// turn into a different command.
async function quickSet(bot, key, value) {
  // Reload first. Spreading an undefined config (an account added since the page last loaded) produced a
  // body with ONE key, and the server materialised every other field as its default - wiping the Steam login.
  if (!config || !config.Bots[bot]) await loadConfig();
  const base = config && config.Bots[bot];
  if (!base) { toast(t('That account is not loaded yet - try again in a second'), true); return; }

  const cfg = { ...base };
  cfg[key] = Number(value) || 0;
  const res = await post('/api/bots/' + encodeURIComponent(bot) + '/config', cfg);
  if (!res.ok) { toast(res.error || t('Could not save that'), true); return; }
  toast((res.Adjusted && res.Adjusted.length) ? res.Adjusted[0] : tf('{0}: saved', bot), !!(res.Adjusted && res.Adjusted.length));
  await loadConfig();
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
    ? rows.map((l) => `<div class="l ${esc(l.Level)}"><span class="t">${esc(l.Time)}</span><span class="s"${sourceStyle(l.Source)} onclick="$('logBot').value='${esc(l.Source)}';renderLog()">${esc(l.Source)}</span><span class="m">${highlight(l.Text, q)}</span></div>`).join('')
    : `<p class="muted">${esc(t('Nothing matches.'))}</p>`;

  if (atBottom && $('logFollow').checked) body.scrollTop = body.scrollHeight;
}

// The colour an account chose for itself, straight from the palette the server sent. Index 0 is "automatic",
// which means leave the stylesheet alone.
function sourceStyle(source) {
  const choice = config && config.Bots && config.Bots[source] ? config.Bots[source].LogColour : 0;
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
  const text = logLines.map((l) => `${l.Time} ${l.Source} ${l.Text}`).join('\n');
  // navigator.clipboard only exists in a secure context - over http://<lan-ip> (which this app supports) it's
  // undefined, so guard and fall back to a hidden textarea instead of throwing and copying nothing.
  try {
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(() => toast(t('Log copied'))).catch(() => toast(t('Copy failed - select it manually'), true));
      return;
    }
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    toast(ok ? t('Log copied') : t('Copy failed - select it manually'), !ok);
  } catch {
    toast(t('Copy failed - select it manually'), true);
  }
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

  if (asHelp === 'help' || asHelp === '?' || asHelp.startsWith('help ')) {
    helpModal(asHelp.startsWith('help ') ? asHelp.slice(5) : '');
    return '';
  }

  const res = await post('/api/command', { Line: line });
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
    `<div class="grp">${esc(g)}</div>` + commands.filter((c) => c.Group === g).map((c) =>
      `<div class="c" onclick="useCommand('${esc(c.Name)}','${esc(c.Args)}')"><code>${esc(c.Name)} ${esc(c.Args)}</code><span class="h">${esc(c.Help)}</span></div>`).join('')).join('');
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
}

function selectSettings(name) {
  // Kick off the pacer read for this account; it re-renders itself when it lands.
  if (name) loadPacer(name); else { pacerFor = null; pacerRows = null; }

  if (Object.keys(pending).length && !confirm(t('You have unsaved changes here. Discard them?'))) return;
  settingsTarget = name == null ? GLOBAL : name;
  pending = {};
  renderSettings();
}

// Matched on the data-section attribute, not the heading text: the heading is translated and the jump link
// carries the English name, so comparing what is on screen stopped finding anything in every language but one.
function jumpTo(section) {
  const target = document.querySelector(`#settingsBody .section h3[data-section="${CSS.escape(section)}"]`);
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
  const sectionNames = [...new Set(settingsDefs().map((d) => d.Section))];
  const jump = `<div class="jump">${sectionNames.map((n) =>
    `<a class="j" onclick="jumpTo('${esc(n)}')">${esc(t(n))}</a>`).join('')}</div>`;

  $('settingsNavBots').innerHTML = Object.keys(config.Bots).length
    ? Object.keys(config.Bots).map((n) =>
        `<div class="s ${settingsTarget === n ? 'active' : ''}" data-bot="${esc(n)}" onclick="selectSettings(this.dataset.bot)">${esc(n)}</div>`).join('')
    : `<p class="muted small">${esc(t('No accounts yet.'))}</p>`;

  $('settingsNavJump').innerHTML = jump;

  const defs = settingsDefs();
  const values = settingsValues();
  const defaults = settingsDefaults();
  const advanced = $('setAdvanced').checked;
  const onlyChanged = $('setChanged').checked;
  const q = ($('setSearch').value || '').toLowerCase();

  $('settingsHeader').innerHTML = settingsTarget === GLOBAL
    ? `<p class="muted small">${esc(t('These apply to nocat.farm as a whole.'))} <code>config/nocat.farm.json</code></p>`
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
      if (!advanced && d.Advanced) { hiddenAdvanced++; return false; }
      if (q && !(tSetting(d, 'label').toLowerCase().includes(q) || d.Label.toLowerCase().includes(q)
        || d.Name.toLowerCase().includes(q) || tSetting(d, 'tip').toLowerCase().includes(q))) return false;
      if (onlyChanged && !isChanged(d, values, defaults)) return false;
      return true;
    });

    if (!fields.length) continue;
    html += `<div class="section"><h3 data-section="${esc(section)}">${esc(t(section))}</h3>${sectionIntro(section, values)}${fields.map((d) => fieldHtml(d, values, defaults)).join('')}</div>`;
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
    pacerRows = d.Games || [];
    pacerRecent = d.Recent || [];
    pacerHunt = d.Hunt || null;
    // Resolve any "app 12345" names against Steam so the table reads with real game names; learnNames redraws
    // the settings pane (which this pacer lives in) once they land.
    learnNames(pacerRows.map((g) => g.App).filter(Boolean));
  } catch { pacerRows = []; pacerRecent = []; pacerHunt = null; }

  if (view === 'settings' && settingsTarget === name) renderSettings();
}

// The rarity ladder, drawn as the thing it is: a set of gates that open as the hours build.
const RARITY_TIERS = [
  { h: 0.5, floor: 40 }, { h: 2, floor: 25 }, { h: 6, floor: 15 }, { h: 12, floor: 9 },
  { h: 22, floor: 5 }, { h: 35, floor: 3 }, { h: 50, floor: 2 }, { h: 80, floor: 1 },
];

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

function pacerTable() {
  if (pacerRows === null) return `<p class="muted small">${esc(t('Reading what it has done so far…'))}</p>`;
  if (!pacerRows.length) return `<p class="muted small empty">${esc(t('Nothing tracked yet. It starts counting the first minute a game is running.'))}</p>`;

  // One line, not a grid.
  //
  // A game only ever earns while it is being PLAYED, so a table listing sixty-odd owned games was sixty-odd rows
  // of "not read yet / when it next plays" wrapped around the single row that meant anything. The one fact worth
  // stating is which game is earning right now and how far along it is; everything else is answered by `cheevo`.
  const live = pacerRows.find((g) => g.Running && !g.Blocked);

  if (!live) {
    return `<p class="muted small">${esc(t('Nothing is earning right now — a game only earns while this account is playing it.'))}</p>`;
  }

  const name = esc(GAME_NAMES[live.App] || live.Game);
  const hrs = live.PlayedMinutes >= 60 ? (live.PlayedMinutes / 60).toFixed(1) + t('h') : live.PlayedMinutes + t('m');
  const done = live.Total > 0
    ? tf('{0} of {1} done', live.Unlocked, live.Total)
    : t('reading what it has so far');

  return `<p class="earning">${tf('Earning in {0} — {1} played, {2}.', `<b>${name}</b>`, hrs, done)}</p>`;
}

function sectionIntro(section, values) {
  const val = (k) => (pending[k] !== undefined ? pending[k] : values[k]);

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
        ${name
          ? tf('Your friends see {0} instead of the real game; the hours still count.', `<b>${esc(name)}</b>`)
          : esc(t('Your friends see the real game.'))}
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
function weightsEditor(spec) {
  const rows = parseWeights(spec);
  learnNames(rows.map((r) => r.game));

  const total = rows.reduce((sum, r) => sum + r.weight, 0) || 1;

  // Row zero's own number is the main game's share now, and the scheduler reads it. It used to be owned by a
  // separate "Main game gets" box, so this row was shown read-only — you could not set the one figure the whole
  // schedule turns on from the list it belongs to, and the number sitting in the spec was ignored.
  const mainPct = Math.max(5, Math.min(95, Math.round((rows[0]?.weight ?? 70) * 100 / total)));

  // What each row is worth across a week rather than on a mixed day. Main-game-only days carry no side games at
  // all, so every side share is worth less over a week than it reads here - the gap is wide enough at a high
  // pure-main chance that showing only the configured figure reads as a promise the schedule never made.
  const pure = Math.max(0, Math.min(100, liveValue('PureMainDayChancePct', settingsValues() || {}) ?? 25));

  const shares = rows.map((r, i) => i === 0 ? mainPct : Math.round(((r.weight / Math.max(1, total - rows[0].weight)) * (100 - mainPct))));

  // The exact weekly figures always total 100, so the rounded ones have to as well. Rounding each on its own
  // put a column of 78/6/6/11 on screen — 101, from two values that were really 77.5 and 10.5. Largest
  // remainder instead: floor everything, then hand the leftover points to whichever rows were cut hardest.
  const weeklies = roundToTotal(shares.map((s, i) => i === 0 ? pure + ((100 - pure) * s / 100) : (100 - pure) * s / 100), 100);

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
      <span class="wname"><b class="wgame" title="${esc(gameLabel(r.game))}">${esc(gameLabel(r.game))}</b>${i === 0 ? `<b class="wtag">${esc(t('main'))}</b>` : ''}<i class="wid">${r.game}</i></span>
      <span class="wbar"><i style="width:${share}%"></i></span>
      <input class="wpct" type="number" min="1" max="95" value="${share}" data-w-index="${i}"
             onchange="setShare(${i},parseInt(this.value)||1)" data-tip="${esc(
               (i === 0
                 ? t("The main game's share of a mixed day, held there however many other games you add. It's rolled within about 10 points of this each morning.")
                 : t("This game's share of a mixed day. Every row adds up to 100 - change one and the others move to make room."))
               + (weeklyTip ? ' ' + weeklyTip : ''))}">
      <span class="wsign">%</span>
      <span class="wweek"${weeklyTip ? ` data-tip="${esc(weeklyTip)}"` : ''}>${weeklyTip ? `${weekly}%<i>${esc(t('/week'))}</i>` : ''}</span>
      ${i === 0 ? '<span class="wact"></span>' : `<span class="wact"><b onclick="makeMain(${i})" data-tip="${esc(t('Make this the main game'))}">↑</b><b onclick="dropWeight(${i})" data-tip="${esc(t('Remove'))}">×</b></span>`}
    </div>`;
  }).join('');

  return `<div class="weights" data-setting="GameWeights">
    ${body || `<p class="muted small" style="margin:0 0 8px">${esc(t('No games yet — add the one this account is meant to be into first.'))}</p>`}
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
  const rows = parseWeights(liveWeights());
  if (!rows[index]) return;

  const sides = rows.length - 1;

  // One game listed: it takes the lot, and there is nothing to balance against.
  if (sides < 1) { rows[0].weight = 100; editAndRender('GameWeights', weightsSpec(rows)); return; }

  // Dragging the main game moves every side game together; dragging a side game moves only its peers. Both
  // leave the column adding up to 100, so no row ever has to be worked out by hand.
  const mainPct = index === 0
    ? Math.max(5, Math.min(100 - sides, wantPct))
    : Math.max(5, Math.min(95, Math.round((rows[0].weight * 100) / (rows.reduce((s, r) => s + r.weight, 0) || 1))));

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
  if (rows.length > 1) {
    rows[rows.length - 1].weight = Math.max(5, Math.floor(100 / rows.length / 2));
    balance(rows, rows.length - 1);
  } else {
    rows[0].weight = 100;
  }

  editAndRender('GameWeights', weightsSpec(rows));
}

/// Even the remainder out across every row except the one that was just set, and make the total exactly 100.
function balance(rows, fixedIndex) {
  const others = rows.filter((_, i) => i !== fixedIndex);
  if (!others.length) { rows[0].weight = 100; return rows; }

  const spare = Math.max(others.length, 100 - rows[fixedIndex].weight);
  const each = Math.floor(spare / others.length);
  others.forEach((r) => { r.weight = Math.max(1, each); });

  // Whatever integer division dropped goes on the main game, so it always adds up to 100.
  const drift = 100 - rows.reduce((sum, r) => sum + r.weight, 0);
  if (drift !== 0) {
    const soakIndex = fixedIndex === 0 ? (rows[1] ? 1 : 0) : 0;
    rows[soakIndex].weight = Math.max(1, rows[soakIndex].weight + drift);
  }

  return rows;
}

function dropWeight(index) {
  const rows = parseWeights(liveWeights()).filter((_, i) => i !== index);
  if (!rows.length) { editAndRender('GameWeights', ''); return; }

  // The share the removed game had goes back to the main game, rather than being left to add up to 90.
  const drift = 100 - rows.reduce((sum, r) => sum + r.weight, 0);
  rows[0].weight = Math.max(1, rows[0].weight + drift);
  editAndRender('GameWeights', weightsSpec(rows));
}

/// Promote a game to main. Being first in the list is what makes it the main game, so this is a move, not a flag.
function makeMain(index) {
  const rows = parseWeights(liveWeights());
  if (!rows[index]) return;
  const [moved] = rows.splice(index, 1);
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
    body: () => `<p>${tf('A newly added account does {0} until it is told what to play, and the app window has no form for that - only the dashboard does.', `<b>${esc(t('nothing at all'))}</b>`)}</p>
      <p class="muted small">${esc(t('With this off, adding an account leaves you at a command line with an account that will sit there idle until you remember to go and configure it. This is for people who already know that.'))}</p>`,
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

function fieldHtml(def, values, defaults) {
  const cur = pending[def.Name] !== undefined ? pending[def.Name] : values[def.Name];
  const id = 'f-' + def.Name;
  const changed = pending[def.Name] !== undefined;
  let ctl;

  switch (def.Kind) {
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
      ctl = `<div class="tags" data-setting="${def.Name}">
        ${list.map((a, i) => `<span class="tag">${a}<b onclick="removeApp('${def.Name}',${i})">×</b></span>`).join('')}
        <input type="text" style="max-width:150px" placeholder="${esc(t('appID or store URL'))}" onkeydown="if(event.key==='Enter'||event.key===','){addApp('${def.Name}',this);event.preventDefault();}" onblur="addApp('${def.Name}',this)">
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
  const defText = Array.isArray(def0) ? (def0.length ? def0.join(', ') : t('none')) : (def.Kind === 'Secret' ? '' : String(def0));

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

async function saveSettings() {
  const target = settingsTarget;
  const edits = { ...pending };

  // Re-read first, then apply only what was actually edited. Posting the snapshot taken when the page was
  // opened would quietly revert anything changed from the console (or another tab) in the meantime.
  await loadConfig();
  const base = target === GLOBAL ? config.Global : config.Bots[target];
  if (!base) { toast(t('That account is gone'), true); settingsTarget = GLOBAL; renderSettings(); return; }

  const body = { ...base, ...edits };
  const res = target === GLOBAL
    ? await post('/api/config', body)
    : await post('/api/bots/' + encodeURIComponent(target) + '/config', body);

  if (!res.ok) { toast(res.error || t('Save failed'), true); return; }

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

function syncWelcome() {
  const show = state && !state.Bots.length && !sessionStorage.getItem('skip-welcome');
  $('welcome').classList.toggle('hidden', !show);
  $('app').classList.toggle('hidden', !!show);

  if (show && !askedAboutAsf) {
    askedAboutAsf = true;
    checkForAsf();
  }
}

// One timer, re-armed only when the interval actually changes.
function armPolling(seconds) {
  if (pollTimer && pollSeconds === seconds) return;
  if (pollTimer) clearInterval(pollTimer);
  pollSeconds = seconds;
  pollTimer = setInterval(refresh, Math.max(1, seconds) * 1000);
}

async function refresh() {
  try {
    state = await api('/api/status');
    refreshSeconds = state.RefreshSeconds || refreshSeconds;
    armPolling(refreshSeconds);
    syncWelcome();

    // nocat.farm restarting resets its sequence numbers. Without noticing that, "everything after seq 812"
    // matches nothing forever and the log tab silently freezes.
    if (bootId !== null && state.BootId !== bootId) logLines = [];
    bootId = state.BootId;

    const since = logLines.length ? logLines[logLines.length - 1].Seq : 0;
    const fresh = since ? await api('/api/log?since=' + since) : await api('/api/log?n=300');
    if (fresh.length) {
      logLines = logLines.concat(fresh).slice(-1000);
      if (view === 'log') renderLog();
    }

    render();
  } catch {
    // showLogin already fired, or the server is restarting - the next tick retries
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

  if (shouldShowTutorial()) startTutorial();
}

boot();
