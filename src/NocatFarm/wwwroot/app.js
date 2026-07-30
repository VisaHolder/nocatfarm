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

  if (!res.ok) { $('loginError').textContent = 'Wrong password.'; return false; }
  token = res.token;
  localStorage.setItem('nocatfarm-token', token);
  $('loginError').textContent = '';
  boot();
  return false;
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

// ── formatting ───────────────────────────────────────────────────────
function hm(minutes) {
  if (!minutes) return '0m';
  return minutes < 60 ? minutes + 'm' : Math.floor(minutes / 60) + 'h' + String(minutes % 60).padStart(2, '0') + 'm';
}

function ago(iso) {
  if (!iso) return 'never';
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 1) return 'just now';
  return hm(mins) + ' ago';
}

// A future time: "in 45m" when it's close, a clock time "~14:30" when it's further off.
function until(iso) {
  if (!iso) return '';
  const mins = Math.round((new Date(iso).getTime() - Date.now()) / 60000);
  if (mins <= 0) return 'now';
  if (mins < 90) return 'in ' + hm(mins);
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
    .map((k) => `<span class="chip ${k}" data-tip="${esc(STATUS_META[k].tip)}" onclick="filterTo('${k}')"><i class="dot"></i>${STATUS_META[k].label}<b>${counts[k]}</b></span>`)
    .join('') || '<span class="muted small">no accounts yet</span>';

  $('railStats').innerHTML = `
    <dt data-tip="Trading cards still to drop across every account that's farming.">Cards left</dt><dd>${state.CardsLeft}</dd>
    ${r4rOn() && state.Rep4RepToken ? `<dt data-tip="Points you can spend on rep4rep. Pending ones are comments rep4rep hasn't verified yet.">Points</dt><dd>${state.Points}${state.PendingPoints ? ' <span class="muted">+' + state.PendingPoints + '</span>' : ''}</dd>` : ''}
    <dt data-tip="How long nocat.farm has been running.">Up</dt><dd>${hm(state.UptimeMinutes)}</dd>`;

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
      <span data-tip="Type the code from the Steam app on your phone, or from your email. nocat.farm can't finish logging in until you do.">${esc(state.Prompt)}</span>
      <input id="promptInput" type="${state.PromptSecret ? 'password' : 'text'}" autocomplete="off">
      <button onclick="sendPrompt()">Submit</button></div>`);
  }

  // Exposed only fires when a password IS set and is short — so "anyone can open this" was simply untrue, and
  // "set a password" pointed at a box that already had one in it. Say the thing that's actually wrong.
  if (state.Exposed) {
    out.push(`<div class="alert bad"><span>This dashboard is reachable from your network and its password is short enough to guess. Your Steam accounts are behind it.</span>
      <span class="spacer"></span><button onclick="goSetting('WebPassword')">Use a longer one</button></div>`);
  }

  // The case that genuinely means "nobody else can get in" was computed and then never shown.
  if (state.LockedToThisPc) {
    out.push(`<div class="alert warn"><span>This dashboard is set to listen on the network, but with no password it refuses every connection that isn't from this PC — so it's only reachable here.</span>
      <span class="spacer"></span><button onclick="goSetting('WebPassword')">Set a password to open it up</button></div>`);
  }

  if (state.Rep4RepWanted > 0 && !state.Rep4RepToken) {
    out.push(`<div class="alert warn"><span>rep4rep is switched on for ${state.Rep4RepWanted} account${state.Rep4RepWanted > 1 ? 's' : ''} but there's no API token, so nothing is being posted.</span>
      <span class="spacer"></span><button onclick="go('rep4rep')">Add token</button></div>`);
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
    if (!res.ok) toast('Nothing was waiting for that answer', true);
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
  if (!bots.length) verdict = 'No accounts yet.';
  else if (need.length) verdict = `${need.map((b) => b.Name).join(', ')} ${need.length > 1 ? 'need' : 'needs'} something from you.`;
  else if (bad.length) verdict = `${bad.length} account${bad.length > 1 ? 's have' : ' has'} a problem.`;
  else verdict = `${bots.length} account${bots.length > 1 ? 's' : ''} · ${busy.length} working · everything's fine.`;
  $('verdict').textContent = verdict;

  const tile = (n, k, tip, sub) =>
    `<div class="tile"><div class="n">${n}</div><div class="k">${k}${tipIcon(tip)}</div>${sub ? `<div class="sub">${sub}</div>` : ''}</div>`;

  $('tiles').innerHTML =
    tile(state.CardsLeft, 'Cards left', "Trading cards still to drop across every account.", state.CardsLeft ? '' : 'nothing left to farm') +
    tile(state.GamesLeft, 'Games left', 'Games with at least one card still to drop, across every account.') +
    tile(state.CardsToday, 'Cards today', 'Trading cards that dropped in the last 24 hours.') +
    (!r4rOn()
      ? ''
      : state.Rep4RepToken
        ? tile(state.Points, 'rep4rep points', "Points you can spend. Pending points are comments rep4rep hasn't verified yet - they turn into real points on their own, usually within a few hours. Nothing is lost.",
            state.PendingPoints ? state.PendingPoints + ' pending' : '')
        : tile(state.CommentsToday, 'Comments today', 'rep4rep comments posted in the last 24 hours.'));

  $('glance').innerHTML = bots.length ? `<div class="tablewrap"><table>
    <tr><th>Account</th><th>State</th><th>Playing</th><th>Cards</th>${r4rOn() ? '<th>rep4rep</th>' : ''}<th>Up</th></tr>
    ${bots.map((b) => `<tr class="click" data-act="cards" data-bot="${esc(b.Name)}">
      <td><b>${esc(b.Name)}</b></td>
      <td><span class="chip ${b.Group}"><i class="dot"></i>${esc(b.Status)}</span></td>
      <td>${esc(b.Playing || '—')}</td>
      <td>${b.Cards || '—'}</td>
      ${r4rOn() ? `<td>${b.Rep4Rep ? b.Rep4RepToday + '/' + b.Rep4RepCap : '—'}</td>` : ''}
      <td>${b.UptimeMinutes ? hm(b.UptimeMinutes) : '—'}</td></tr>`).join('')}
    </table></div>`
    : `<p class="muted">No accounts yet. <a href="#console" onclick="go('console')">Add one</a> or type <code>add mybot mysteamlogin</code>.</p>`;

  renderToday();

  const interesting = logLines.filter((l) => l.Level !== 'INFO' && l.Level !== 'DEBUG').slice(-8).reverse();
  $('recent').innerHTML = interesting.length
    ? interesting.map((l) => `<div class="line"><span class="who">${esc(l.Source)}</span><span>${esc(l.Text)}</span><span class="when">${esc(l.Time)}</span></div>`).join('')
    : '<p class="muted">Nothing worth reporting yet.</p>';
}

// Per-account activity in the last 24h. A tidy table, because the old by-hour bar chart was 24 near-empty bars
// the moment a farm finished its cards and was just idling - which reads as broken, not informative.
function renderToday() {
  const bots = state.Bots;
  if (!bots.length) { $('today').innerHTML = '<p class="muted empty">No accounts yet.</p>'; return; }

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

  $('today').innerHTML = `<div class="tablewrap"><table class="today">
    <tr><th>Account</th><th data-tip="Trading cards that dropped in the last 24 hours.">Cards</th>${r4r ? '<th data-tip="rep4rep comments posted in the last 24 hours.">Comments</th>' : ''}</tr>
    ${rows}
    <tr class="fleet"><td>fleet</td><td>${totCards}</td>${r4r ? `<td>${totComments}</td>` : ''}</tr>
    </table></div>`;
}

// ── render: accounts ─────────────────────────────────────────────────
function renderAccounts() {
  // Never rebuild the list out from under a drag in progress.
  if (dragging) return;

  const q = ($('acctSearch').value || '').toLowerCase();

  $('acctFilters').innerHTML = Object.keys(STATUS_META).map((k) =>
    `<span class="chip ${k} ${acctFilter === k ? 'on' : ''}" data-tip="${esc(STATUS_META[k].tip)}" onclick="acctFilter='${acctFilter === k ? '' : k}';render()"><i class="dot"></i>${STATUS_META[k].label}</span>`).join('');

  const bots = state.Bots.filter((b) =>
    (!acctFilter || b.Group === acctFilter) &&
    (!q || (b.Name + ' ' + b.Login + ' ' + b.Notes + ' ' + b.Playing).toLowerCase().includes(q)));

  if (!bots.length) {
    $('bots').innerHTML = state.Bots.length
      ? '<p class="muted">No accounts match that.</p>'
      : `<div class="card"><h2>No accounts yet</h2>
         <p class="muted">Add one and nocat.farm will ask for the password and a Steam Guard code once, then remember it with a login token.</p>
         <button onclick="showAddAccount()">+ Add account</button></div>`;
    return;
  }

  $('bots').innerHTML = bots.map((b) => {
    // rep4rep is the only figure here with a real denominator, so it is the only one that gets a bar. The
    // card count has no known total, and inventing one (this used to be 100 - cards*5) is worse than a number.
    const capPct = b.Rep4Rep && b.Rep4RepCap ? Math.min(100, Math.round((b.Rep4RepToday / b.Rep4RepCap) * 100)) : 0;

    return `<div class="card bot ${b.Group}" draggable="true" data-name="${esc(b.Name)}"
      ondragstart="dragStart(event)" ondragover="dragOver(event)" ondragend="dragEnd()" ondrop="dragEnd()">
      <div class="bot-head">
        <span class="bot-name" title="${esc(b.Name)}" data-tip="Drag this card to move the account. The order is used everywhere - here, the app window and the console.">${esc(b.Name)}</span>
        <span class="chip ${b.Group}" data-tip="${esc(STATUS_META[b.Group].tip)}"><i class="dot"></i>${esc(b.Status)}</span>
      </div>
      <div class="bot-login" title="${esc(b.Login)}">${esc(b.Login)}${b.SteamId !== '0' ? ' · ' + esc(b.SteamId) : ''}</div>
      ${b.Notes ? `<div class="bot-notes" title="${esc(b.Notes)}">${esc(b.Notes)}</div>` : ''}
      ${b.Guard ? `<div class="bot-guard">Waiting on you: ${esc(b.Guard)}</div>` : ''}
      <div class="bot-playing" title="${esc(b.Playing || '')}">${b.Playing ? esc(b.Playing) : '<span class="real">not playing anything</span>'}</div>
      ${b.Online ? `<div class="bot-persona ${b.Persona === 'invisible' || b.Persona === 'offline' ? 'hidden-persona' : ''}"
        data-tip="What your friends list shows for this account. The GAME comes straight back from Steam, so it's what other people genuinely see; the status is what nocat.farm set it to. Human mode changes the status by itself: invisible overnight, away on a break, Snooze over a meal.">your friends see: <b>${esc(b.Persona)}</b>${b.Seen ? ` · <b>${esc(b.Seen)}</b>` : ''}</div>` : ''}
      ${b.NameNotShowing
        ? `<div class="bot-mismatch" data-tip="Steam decides what to display when a custom name is sent alongside real games, and it has settled on the real one. Idling fewer games, or only the custom name, makes it show yours. A brief mismatch right after signing in is normal and isn't reported here.">Steam is showing ${esc(b.Seen)}, not your custom name</div>`
        : ''}
      <div class="statline">
        <span><b>${b.Cards}</b> card${b.Cards === 1 ? '' : 's'}${b.Games ? ` in ${b.Games}` : ''}</span>
        ${r4rOn() && b.Rep4Rep ? `<span><b>${b.Rep4RepToday}</b>/${b.Rep4RepCap} comments</span>` : ''}
        <span><b>${b.UptimeMinutes ? hm(b.UptimeMinutes) : '—'}</b> up</span>
      </div>
      ${r4rOn() && b.Rep4Rep ? `<div class="bar" data-tip="Comments posted in the last 24 hours against this account's daily cap."><i style="width:${capPct}%"></i></div>` : ''}
      <div class="rows">
        ${b.Modules.filter((m) => m.Status && m.Status !== 'off' && m.Status !== 'idle').map((m) =>
          `<div class="row"><span class="k">${esc(m.Name)}</span><span class="v" title="${esc(m.Status)}">${esc(m.Status)}</span></div>`).join('')}
      </div>
      <div class="actions">
        ${b.Online
          ? (b.Paused
            ? `<button data-tip="Start playing, farming and commenting again." data-act="resume" data-bot="${esc(b.Name)}">Resume</button>`
            : `<button class="ghost" data-tip="Stop playing, farming and commenting. The account stays logged in." data-act="pause" data-bot="${esc(b.Name)}">Pause</button>`)
          : `<button data-tip="Log this account in." data-act="start" data-bot="${esc(b.Name)}">Start</button>`}
        ${b.State !== 'Stopped' ? `<button class="ghost" data-tip="Log this account out. It stays configured." data-act="stop" data-bot="${esc(b.Name)}">Stop</button>` : ''}
        <button class="ghost" data-tip="What this account still has left to farm." data-act="cards" data-bot="${esc(b.Name)}">Cards</button>
        <button class="ghost" data-tip="This account's settings." data-act="settings" data-bot="${esc(b.Name)}">Settings</button>
      </div></div>`;
  }).join('');
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
  if (!res.ok) toast(res.error || "Couldn't save that order", true);
  refresh();
}

// Every one of these used to ignore the reply, so a failure looked exactly like a success.
async function act(name, action) {
  const res = await post(`/api/bots/${encodeURIComponent(name)}/${action}`);
  if (!res.ok) toast(res.error || "That didn't work", true);
  refresh();
}

async function openBot(name) {
  const data = await api(`/api/bots/${encodeURIComponent(name)}/cards`);
  const bot = state.Bots.find((b) => b.Name === name);
  modal(`
    <h2>${esc(name)} — trading cards</h2>
    <p class="muted small">${esc(data.Status || '')}</p>
    ${data.Games.length ? `<div class="tablewrap"><table>
      <tr><th>Cards</th><th data-tip="How long this account has played this game. Steam drops nothing until it passes the threshold in this account's settings.">Hours</th><th>Game</th></tr>
      ${data.Games.map((g) => `<tr><td>${g.Cards}</td><td>${g.Hours}</td><td>${esc(g.Name)}</td></tr>`).join('')}
      </table></div>` : '<p class="muted">Nothing left to farm on this account.</p>'}
    <div class="actions">
      <button class="ghost" data-act="settings" data-bot="${esc(name)}">Settings</button>
      <button class="ghost" onclick="closeModal()">Close</button>
      <span class="spacer"></span>
      <button class="danger" data-act="remove" data-bot="${esc(name)}">Remove</button>
    </div>`);
}

function showAddAccount() {
  modal(`
    <h2>Add a Steam account</h2>
    <div class="form2">
      <label for="a-name">Name${tipIcon("A nickname just for you. It names the config file and it's what you type in commands - it doesn't have to match anything on Steam.")}</label>
      <input id="a-name" type="text" placeholder="mybot" autocomplete="off">
      <label for="a-login">Steam account name${tipIcon("What you type into Steam's sign-in box. Not your display name, not your email.")}</label>
      <input id="a-login" type="text" autocomplete="off">
      <label for="a-pass">Password${tipIcon("Optional. Leave it blank and nocat.farm asks once, then remembers the account with a login token instead - which is safer than a password in a file.")}</label>
      <input id="a-pass" type="password" placeholder="leave blank and it'll ask" autocomplete="off">
    </div>
    <p id="addError" class="error"></p>
    <div class="actions"><button onclick="createBot()">Add account</button><button class="ghost" onclick="closeModal()">Cancel</button></div>`);
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
  $('importBanner').innerHTML = `
    <div class="alert info" style="margin-bottom:18px;display:block">
      <b>Found an ArchiSteamFarm install with ${found.Accounts.length} account${found.Accounts.length === 1 ? '' : 's'}.</b>
      <div class="muted small" style="margin:4px 0 10px">
        ${esc(found.Path)}<br>
        ${withTokens ? `${withTokens} of them can come across with their saved logins — no passwords, no Steam Guard codes.` : 'You will still need to sign in to each one once.'}
      </div>
      <button onclick="runImport()">Import ${found.Accounts.length} account${found.Accounts.length === 1 ? '' : 's'}</button>
    </div>`;
}

function showImport() {
  api('/api/import/asf').then((found) => {
    if (!found || !found.Found) {
      modal(`<h2>Import from ArchiSteamFarm</h2>
        <p class="muted">No ASF install found automatically. Point at its <code>config</code> folder:</p>
        <input id="importPath" type="text" placeholder="C:\\...\\ArchiSteamFarm\\config" autocomplete="off">
        <p id="importError" class="error"></p>
        <div class="actions"><button onclick="runImport()">Import</button><button class="ghost" onclick="closeModal()">Cancel</button></div>`);
      return;
    }

    modal(`<h2>Import from ArchiSteamFarm</h2>
      <p class="muted small">${esc(found.Path)}</p>
      <div class="tablewrap"><table>
        <tr><th>Account</th><th>Steam login</th><th>Sign-in</th></tr>
        ${found.Accounts.map((a) => `<tr><td><b>${esc(a.Name)}</b></td><td class="muted">${esc(a.SteamLogin)}</td>
          <td>${a.HasToken ? '<span class="pill good">token — no password needed</span>'
            : a.HasPassword ? '<span class="pill warn">password only</span>'
            : '<span class="pill">will ask on first login</span>'}</td></tr>`).join('')}
      </table></div>
      <p class="muted small" style="margin-top:12px">Accounts you already have here are left alone.
        Don't run ASF and nocat.farm on the same account at once — they'd take turns kicking each other off Steam.</p>
      <p id="importError" class="error"></p>
      <div class="actions"><button onclick="runImport()">Import ${found.Accounts.length}</button><button class="ghost" onclick="closeModal()">Cancel</button></div>`);
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
  toast(`Imported ${res.Imported} account${res.Imported === 1 ? '' : 's'}`);
  await loadConfig();
  await refresh();
  go('accounts');

  if (res.Notes && res.Notes.length) {
    modal(`<h2>Imported ${res.Imported} account${res.Imported === 1 ? '' : 's'}</h2>
      <div class="rows">${res.Notes.map((n) => `<div class="row"><span>${esc(n)}</span></div>`).join('')}</div>
      <p class="muted small" style="margin-top:12px">They're added but not started. Press Start on each, or run <code>start all</code>.</p>
      <div class="actions"><button onclick="closeModal();run('start all')">Start them all</button><button class="ghost" onclick="closeModal()">Not yet</button></div>`);
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
    <h2>Remove ${esc(name)}?</h2>
    <p>This deletes <code>config/${esc(name)}.json</code> and the stored login token for it.</p>
    <ul class="muted small">
      <li>Nothing happens to the Steam account itself - it is only removed from nocat.farm.</li>
      <li>Adding it back needs the password and a fresh Steam Guard code.</li>
      <li>Anything it was farming stops.</li>
    </ul>
    <p class="small">Type <code>${esc(name)}</code> to enable the button.</p>
    <input type="text" id="removeConfirm" autocomplete="off" spellcheck="false" placeholder="type the account name"
           data-expect="${esc(name)}"
           oninput="$('removeGo').disabled = this.value.trim() !== this.dataset.expect">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">Cancel</button>
      <button class="danger" id="removeGo" disabled data-bot="${esc(name)}" onclick="doRemoveBot(this.dataset.bot)">Remove it</button>
    </div>`);
  setTimeout(() => { const el = $('removeConfirm'); if (el) el.focus(); }, 30);
}

async function doRemoveBot(name) {
  const res = await del('/api/bots/' + encodeURIComponent(name));
  if (!res.ok) { toast(res.error || 'Could not remove that account', true); return; }
  toast(`Removed ${name}`);
  if (settingsTarget === name) { settingsTarget = GLOBAL; pending = {}; }
  closeModal();
  await loadConfig();
  if (view === 'settings') renderSettings();
  refresh();
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
      title: 'Welcome to nocat.farm',
      body: `<p>It signs your Steam accounts in, plays games so the hours count, farms trading cards and posts
        rep4rep comments - all from this machine. Your accounts never leave it.</p>
        <p class="muted small">This takes about a minute. You can skip it and come back from the Overview page.</p>`,
      next: 'Start',
    },
    {
      title: found ? 'Bring your ArchiSteamFarm accounts across' : 'Add your first account',
      body: found
        ? `<p>Found an ArchiSteamFarm setup at <code>${esc(tutorialAsf.Path)}</code> with
            <b>${tutorialAsf.Accounts.length} account${tutorialAsf.Accounts.length === 1 ? '' : 's'}</b>.</p>
           <ul class="muted small">
             ${tutorialAsf.Accounts.map((a) => `<li>${esc(a.Name)} - ${esc(a.SteamLogin)}${a.HasToken ? ' (login token comes across, so no password needed)' : ''}</li>`).join('')}
           </ul>
           <p class="muted small">Importing copies the accounts and their login tokens. It changes nothing in ArchiSteamFarm.</p>`
        : `<p>Add the Steam account you want it to run. You will be asked for the password <b>once</b>, and for a
            Steam Guard code - after that it remembers a login token and never needs the password again.</p>
           <p class="muted small">No ArchiSteamFarm install was found on this machine, so there is nothing to import.</p>`,
      next: found ? 'Import them' : 'Add an account',
      act: found ? doTutorialImport : () => { closeTutorial(); go('accounts'); showAddAccount(); },
    },
    {
      title: 'Tell it what to play',
      body: `<p>An account does nothing until it knows what to run. Two ways:</p>
        <ul class="muted small">
          <li><b>Trading cards</b> - it works through everything in your library that still has drops, then stops. Nothing to configure.</li>
          <li><b>Human mode</b> - it keeps a believable daily routine: a few games, one at a time, with breaks, meals and a bedtime. Use this on an account you care about.</li>
        </ul>
        <p class="muted small">Both live under Settings, per account.</p>`,
      next: 'Next',
    },
    {
      title: 'rep4rep, if you want it',
      body: `<p>Optional. Your accounts post comments on other people's Steam profiles and earn points you can
        spend on comments for your own.</p>
        <p class="muted small">Needs a free account -
        <a href="https://rep4rep.com/?r=reap" target="_blank" rel="noopener">rep4rep.com ↗</a> - and the API token
        pasted into Settings. Skip it entirely if you only want cards and playtime.</p>`,
      next: 'Next',
    },
    {
      title: "That's it",
      body: `<p>Everything has an explanation attached - hover the <i class="info" style="display:inline-flex"></i>
        beside any setting.</p>
        <p class="muted small">The Console tab does anything the other tabs do, by typing. <code>help</code> lists it all.</p>`,
      next: 'Finish',
      act: closeTutorial,
    },
  ];

  const st = steps[Math.min(tutorialStep, steps.length - 1)];
  const last = tutorialStep >= steps.length - 1;

  modal(`
    <div class="muted small" style="letter-spacing:1px">STEP ${tutorialStep + 1} OF ${steps.length}</div>
    <h2>${esc(st.title)}</h2>
    ${st.body}
    <div class="actions">
      ${tutorialStep > 0 ? `<button class="ghost" onclick="tutorialStep--;renderTutorial()">Back</button>` : ''}
      <button class="ghost" onclick="closeTutorial()">${last ? 'Close' : 'Skip'}</button>
      <button id="tutNext">${esc(st.next)}</button>
    </div>`);

  $('tutNext').onclick = st.act || (() => { tutorialStep++; renderTutorial(); });
}

async function doTutorialImport() {
  const btn = $('tutNext');
  btn.disabled = true;
  btn.textContent = 'Importing…';

  const res = await post('/api/import/asf', { Path: tutorialAsf.Path });

  if (!res.ok) {
    toast(res.error || 'Import failed', true);
    btn.disabled = false;
    btn.textContent = 'Import them';
    return;
  }

  toast(`Imported ${res.Imported || ''} account(s)`.replace('  ', ' '));
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
    : '<p class="muted">Nothing matches.</p>';

  modal(`
    <h2>Commands</h2>
    <input type="text" id="helpFilter" autocomplete="off" spellcheck="false" placeholder="filter…"
           oninput="helpModal(this.value)" value="${esc(filter || '')}">
    <div class="helplist">${body}</div>
    <div class="actions"><button class="ghost" onclick="closeModal()">Close</button></div>`);

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
  el.textContent = 'posting…';
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
    $('r4rProfiles').innerHTML = '<p class="muted">Connect your rep4rep account first.</p>';
    $('r4rTasks').innerHTML = '';
  }
  renderRep4RepPacing();
}

function renderRep4RepAccount() {
  if (!r4r || !r4r.Connected) {
    $('r4rAccount').innerHTML = `
      <h2>Set up rep4rep</h2>
      <p class="explain" style="margin-bottom:18px">
        <b>How it works:</b> your accounts post a comment on someone else's Steam profile → you earn points →
        you spend those points to get comments on <i>your</i> profile. nocat.farm does the posting for you,
        on a schedule that keeps every account under Steam's daily limit.</p>

      <ol class="steps">
        <li>
          <b>Make a rep4rep account</b>
          <div class="muted">Free. <a href="https://rep4rep.com/?r=reap" target="_blank" rel="noopener">rep4rep.com/?r=reap ↗</a>
            — then click the verification link in your email, or the token below won't work.</div>
        </li>
        <li>
          <b>Copy your API token</b>
          <div class="muted">It's on <a href="https://rep4rep.com/user/settings" target="_blank" rel="noopener">rep4rep.com → Settings ↗</a>.
            Copy the whole string.</div>
          <div class="toolbar" style="margin:10px 0 0">
            <input id="r4rToken" type="password" placeholder="paste the token here" autocomplete="off" style="max-width:340px"
                   onkeydown="if(event.key==='Enter')connectRep4Rep()">
            <button onclick="connectRep4Rep()">Connect</button>
          </div>
          <p id="r4rError" class="error">${r4r && r4r.Error ? esc(r4r.Error) : ''}</p>
        </li>
        <li class="muted">
          <b>That's it</b>
          <div>nocat.farm registers your Steam accounts with rep4rep by itself and starts posting.
            You don't have to add anything on their website.</div>
        </li>
      </ol>`;
    return;
  }

  const on = state ? state.Bots.filter((b) => b.Rep4Rep).length : 0;
  const total = state ? state.Bots.length : 0;

  $('r4rAccount').innerHTML = `
    <h2>Your rep4rep account</h2>
    <div class="tiles">
      <div class="tile"><div class="n">${r4r.Points}</div><div class="k">Points you can spend${tipIcon('Spend these on comments for your own Steam profile, over on rep4rep.com. Read fresh from rep4rep every time you open this tab, so it matches the site.')}</div>
        ${r4r.SyncedAt ? `<div class="muted small">as of ${new Date(r4r.SyncedAt).toLocaleTimeString()}</div>` : ''}</div>
      <div class="tile"><div class="n">${r4r.Pending}</div><div class="k">Waiting to be verified${tipIcon("Comments you've posted that rep4rep hasn't checked yet. They turn into spendable points on their own, usually within a few hours - nothing is lost and nothing is stuck.")}</div>
        <div class="sub">turns into points on its own</div></div>
    </div>
    ${on === 0 && total > 0
      ? `<div class="alert warn" style="margin:4px 0 12px"><span>Connected, but commenting isn't switched on for any account yet.</span>
         <span class="spacer"></span><button onclick="enableAllRep4Rep()">Turn it on for all ${total}</button></div>`
      : `<p class="muted small">Posting from <b>${on}</b> of ${total} account${total === 1 ? '' : 's'}.</p>`}
    <div class="toolbar">
      <span class="muted small">Token set · synced ${esc(ago(r4r.SyncedAt))}</span>
      <button class="ghost" onclick="loadRep4Rep()">Refresh</button>
      <button class="ghost" onclick="replaceToken()">Replace token</button>
      <a class="muted small" href="https://rep4rep.com/dashboard/" target="_blank" rel="noopener" style="margin-left:auto">Spend your points ↗</a>
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
  toast('rep4rep commenting switched on for every account');
  await loadConfig();
  loadRep4Rep();
  refresh();
}

function replaceToken() { r4r = { Connected: false }; renderRep4RepAccount(); }

async function connectRep4Rep() {
  const res = await post('/api/rep4rep/token', { Token: $('r4rToken').value.trim() });
  if (!res.ok) { $('r4rError').textContent = res.error; return; }
  toast('rep4rep connected');
  loadRep4Rep();
  refresh();
}

// The "Today" cell: count / cap, plus WHY it's stuck and WHEN it frees up - the two things the rolling window
// never made obvious.
function capCell(p) {
  let s = `${p.Today} / ${p.Cap}`;
  if (p.CapIsSteamLimit) {
    s += ` <span class="muted small" data-tip="This is the ceiling Steam enforced on this account, not the number you set - so raising the configured cap won't lift it.">(Steam's limit)</span>`;
  }
  if (p.Today >= p.Cap && p.NextSlot) {
    s += ` <span class="muted small" data-tip="Rolling 24h window: the count drops as each comment ages past 24h. This is the soonest this account can post again.">· frees ${esc(until(p.NextSlot))}</span>`;
  }
  return s;
}

function renderRep4RepProfiles() {
  if (!r4rProfiles.length) { $('r4rProfiles').innerHTML = '<p class="muted">No accounts configured yet.</p>'; return; }

  $('r4rProfiles').innerHTML = `<div class="tablewrap"><table>
    <tr><th>Account</th><th>SteamID64</th><th data-tip="rep4rep's own id for this profile. It is not your SteamID - this is the one their support and their API ask for.">rep4rep ID</th><th>Status</th><th data-tip="Comments posted in the last ROLLING 24 hours - not a midnight reset. Each one ages off on its own 24h after it went out, so the count frees up gradually. Shown against the cap.">Today</th><th></th></tr>
    ${r4rProfiles.map((p) => `<tr>
      <td><b>${esc(p.Account)}</b></td>
      <td class="muted small">${esc(p.SteamId === '0' ? '—' : p.SteamId)}</td>
      <td class="muted small">${esc(p.Rep4RepId || '—')}</td>
      <td>${p.Registered
        ? (p.Enabled ? '<span class="pill good">Ready</span>' : '<span class="pill">Commenting off</span>')
        : (p.Online ? '<span class="pill warn">Not on rep4rep</span>' : '<span class="pill">Log in first</span>')}</td>
      <td>${p.Enabled ? capCell(p) : '—'}</td>
      <td>${p.Registered
        ? `<button class="ghost" data-tip="Skip the wait and post the next comment as soon as the daily cap allows. It never goes over the cap." data-act="postnow" data-bot="${esc(p.Account)}">Post now</button>`
        : (p.Online ? `<button class="ghost" data-act="register" data-bot="${esc(p.Account)}">Register</button>` : '')}</td>
      </tr>`).join('')}
    </table></div>
    <p class="muted small" style="margin-top:10px">This is rep4rep's cached copy of your Steam settings, not live. If you just changed something on Steam, it can take a while to show here.</p>`;
}

async function registerProfile(name) {
  const res = await post(`/api/rep4rep/profiles/${encodeURIComponent(name)}/register`);
  toast(res.ok ? `${name} registered with rep4rep` : res.error, !res.ok);
  loadRep4Rep();
}

async function postNow(name) {
  const res = await post('/api/bots/' + encodeURIComponent(name) + '/rep4repnow');
  toast(res.ok ? `${name}: will post as soon as the daily cap allows` : (res.error || "That didn't work"), !res.ok);
}

function renderBotPicker() {
  const sel = $('r4rBot');
  const current = sel.value;
  sel.innerHTML = r4rProfiles.filter((p) => p.Registered).map((p) => `<option value="${esc(p.Account)}">${esc(p.Account)}</option>`).join('');
  if (current) sel.value = current;
}

async function loadTasks() {
  const bot = $('r4rBot').value;
  if (!bot) { $('r4rTasks').innerHTML = '<p class="muted">No registered accounts yet.</p>'; return; }
  r4rTasks = await api('/api/rep4rep/tasks?bot=' + encodeURIComponent(bot)).catch(() => []);

  $('r4rTasks').innerHTML = r4rTasks.length
    ? `<p class="muted small">${r4rTasks.length} task(s) waiting. Every task is worth the same to rep4rep — their API doesn't rank them, so there's nothing to pick between.</p>
       <div class="tablewrap"><table>
       <tr><th>Target</th><th>Comment to post</th><th></th></tr>
       ${r4rTasks.map((t) => `<tr>
         <td><a href="https://steamcommunity.com/profiles/${esc(t.TargetSteamId)}" target="_blank" rel="noopener">${esc(t.TargetName)} ↗</a></td>
         <td><code>${esc(t.Comment)}</code></td>
         <td><button class="ghost small" data-tip="Post this one right now instead of waiting for the schedule. It still counts against the daily cap." data-task="${esc(t.TaskId)}">Post now</button></td>
         </tr>`).join('')}
       </table></div>
       <p class="muted small" style="margin-top:10px">rep4rep checks the text matches exactly, so these are posted character for character.</p>`
    : '<p class="muted">No tasks for this account right now. rep4rep hands them out in batches — check back later.</p>';
}

function renderRep4RepPacing() {
  if (!state) return;
  const keys = ['Rep4RepDailyCap', 'Rep4RepGapMinMinutes', 'Rep4RepGapMaxMinutes', 'Rep4RepStartHour', 'Rep4RepEndHour'];
  const defs = keys.map((k) => (schema ? schema.Bot.find((d) => d.Name === k) : null));

  $('r4rPacing').innerHTML = `<div class="tablewrap"><table>
    <tr><th>Account</th>${defs.map((d, i) => `<th${d ? ` data-tip="${esc(d.Tooltip)}"` : ''}>${esc(d ? d.Label : keys[i])}</th>`).join('')}</tr>
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
  if (!base) { toast('That account is not loaded yet - try again in a second', true); return; }

  const cfg = { ...base };
  cfg[key] = Number(value) || 0;
  const res = await post('/api/bots/' + encodeURIComponent(bot) + '/config', cfg);
  if (!res.ok) { toast(res.error || 'Could not save that', true); return; }
  toast((res.Adjusted && res.Adjusted.length) ? res.Adjusted[0] : `${bot}: saved`, !!(res.Adjusted && res.Adjusted.length));
  await loadConfig();
  renderRep4RepPacing();
}

// ── render: log ──────────────────────────────────────────────────────
function renderLog() {
  const q = ($('logSearch').value || '').toLowerCase();
  const who = $('logBot').value;

  $('logLevels').innerHTML = Object.keys(logLevels).map((l) =>
    `<span class="chip ${logLevels[l] ? 'on' : ''}" onclick="logLevels['${l}']=!logLevels['${l}'];renderLog()">${l === 'GOOD' ? 'Good' : l[0] + l.slice(1).toLowerCase()}</span>`).join('');

  const sources = [...new Set(logLines.map((l) => l.Source))].sort();
  if ($('logBot').options.length !== sources.length + 1) {
    $('logBot').innerHTML = '<option value="">All accounts</option>' + sources.map((s) => `<option>${esc(s)}</option>`).join('');
    $('logBot').value = who;
  }

  const rows = logLines.filter((l) => logLevels[l.Level] !== false && (!who || l.Source === who) &&
    (!q || l.Text.toLowerCase().includes(q) || l.Source.toLowerCase().includes(q)));

  const body = $('logBody');
  const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 60;

  body.innerHTML = rows.length
    ? rows.map((l) => `<div class="l ${esc(l.Level)}"><span class="t">${esc(l.Time)}</span><span class="s"${sourceStyle(l.Source)} onclick="$('logBot').value='${esc(l.Source)}';renderLog()">${esc(l.Source)}</span><span class="m">${highlight(l.Text, q)}</span></div>`).join('')
    : '<p class="muted">Nothing matches.</p>';

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
      navigator.clipboard.writeText(text).then(() => toast('Log copied')).catch(() => toast('Copy failed - select it manually', true));
      return;
    }
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    toast(ok ? 'Log copied' : 'Copy failed - select it manually', !ok);
  } catch {
    toast('Copy failed - select it manually', true);
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

  if (Object.keys(pending).length && !confirm('You have unsaved changes here. Discard them?')) return;
  settingsTarget = name == null ? GLOBAL : name;
  pending = {};
  renderSettings();
}

function jumpTo(section) {
  const target = [...document.querySelectorAll('#settingsBody .section h3')].find((h) => h.textContent.trim() === section);
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
    ? `<button data-tip="Log this account in." data-act="start" data-bot="${esc(name)}">Start</button>`
    : `<button class="ghost" data-tip="Log this account out. It stays configured." data-act="stop" data-bot="${esc(name)}">Stop</button>`;
}

function renderSettings() {
  if (!schema || !config) return;

  if (settingsTarget !== GLOBAL && !config.Bots[settingsTarget]) { settingsTarget = GLOBAL; pending = {}; }

  // left pane: Global, then one entry per account
  $('settingsNavGlobal').innerHTML =
    `<div class="s ${settingsTarget === GLOBAL ? 'active' : ''}" onclick="selectSettings(null)">Global settings</div>`;
  // Section jump-list for whatever pane is open. A 40-setting page without one is a scroll hunt.
  const sectionNames = [...new Set(settingsDefs().map((d) => d.Section))];
  const jump = `<div class="jump">${sectionNames.map((n) =>
    `<a class="j" onclick="jumpTo('${esc(n)}')">${esc(n)}</a>`).join('')}</div>`;

  $('settingsNavBots').innerHTML = Object.keys(config.Bots).length
    ? Object.keys(config.Bots).map((n) =>
        `<div class="s ${settingsTarget === n ? 'active' : ''}" data-bot="${esc(n)}" onclick="selectSettings(this.dataset.bot)">${esc(n)}</div>`).join('')
    : '<p class="muted small">No accounts yet.</p>';

  $('settingsNavJump').innerHTML = jump;

  const defs = settingsDefs();
  const values = settingsValues();
  const defaults = settingsDefaults();
  const advanced = $('setAdvanced').checked;
  const onlyChanged = $('setChanged').checked;
  const q = ($('setSearch').value || '').toLowerCase();

  $('settingsHeader').innerHTML = settingsTarget === GLOBAL
    ? `<p class="muted small">These apply to nocat.farm as a whole. <code>config/nocat.farm.json</code></p>`
    : `<div class="toolbar"><b>${esc(settingsTarget)}</b>
        <span class="muted small">config/${esc(settingsTarget)}.json</span>
        <span class="spacer"></span>
        ${botActions(settingsTarget)}
        <button class="danger" data-act="remove" data-bot="${esc(settingsTarget)}">Remove</button></div>`;

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
      if (q && !(d.Label.toLowerCase().includes(q) || d.Name.toLowerCase().includes(q) || d.Tooltip.toLowerCase().includes(q))) return false;
      if (onlyChanged && !isChanged(d, values, defaults)) return false;
      return true;
    });

    if (!fields.length) continue;
    html += `<div class="section"><h3>${esc(section)}</h3>${sectionIntro(section, values)}${fields.map((d) => fieldHtml(d, values, defaults)).join('')}</div>`;
  }

  // Only on a real account, and only when nothing is being searched or filtered - it is not a setting and
  // has no business appearing in a filtered list of settings.
  // Behind "Show advanced" as well. Unlocking every achievement is not something to stumble across while
  // scrolling an account's ordinary settings, and anyone who needs it can find the switch.
  if (settingsTarget !== GLOBAL && !q && !onlyChanged && advanced) html += dangerZone(settingsTarget);

  if (!html) html = '<p class="muted">Nothing matches.</p>';
  if (hiddenAdvanced && !q) html += `<p class="muted small" style="margin-top:16px">${hiddenAdvanced} advanced setting(s) hidden. Tick “Show advanced” to see them.</p>`;

  $('settingsBody').innerHTML = html;
  updateSaveButton();
}

// A live "this is what will actually happen" panel for the sections where two settings combine and the result
// isn't obvious from either one on its own.
// What the pacer is actually doing, per game. Cached per account so switching panes does not re-fetch, and
// refreshed whenever settings are saved.
let pacerFor = null;
let pacerRows = null;

async function loadPacer(name) {
  if (pacerFor === name) return;
  pacerFor = name;
  pacerRows = null;

  try {
    const d = await api(`/api/bots/${encodeURIComponent(name)}/achievements`);
    pacerRows = d.Games || [];
  } catch { pacerRows = []; }

  if (view === 'settings' && settingsTarget === name) renderSettings();
}

// The rarity ladder, drawn as the thing it is: a set of gates that open as the hours build.
const RARITY_TIERS = [
  { h: 0.5, floor: 40 }, { h: 2, floor: 25 }, { h: 6, floor: 15 }, { h: 12, floor: 9 },
  { h: 22, floor: 5 }, { h: 35, floor: 3 }, { h: 50, floor: 2 }, { h: 80, floor: 1 },
];

function pacerTable() {
  if (pacerRows === null) return '<p class="muted small">Reading what it has done so far…</p>';
  if (!pacerRows.length) return '<p class="muted small empty">Nothing tracked yet. It starts counting the first minute a game is running.</p>';

  const rows = pacerRows.slice(0, 12).map((g) => {
    const blocked = g.Blocked;
    const next = new Date(g.NextAllow);
    const soon = next <= new Date();
    const pct = Math.max(0, Math.min(100, Math.round((g.EffectiveHours / 80) * 100)));

    return `<tr class="${blocked ? 'off' : ''}">
      <td class="g">${esc(g.Game)}</td>
      <td class="n">${g.PlayedMinutes >= 60 ? (g.PlayedMinutes / 60).toFixed(1) + 'h' : g.PlayedMinutes + 'm'}</td>
      <td class="bar"><span style="width:${pct}%"></span></td>
      <td class="n">${blocked ? '—' : g.FloorPercent > 100 ? 'none yet' : g.FloorPercent + '%'}</td>
      <td class="n">${g.CeilingPercent > 0 ? g.CeilingPercent + '%' : '—'}</td>
      <td class="w">${blocked ? esc(g.Why) : soon ? 'due' : next.toLocaleTimeString([], {hour: '2-digit', minute: '2-digit'})}</td>
    </tr>`;
  }).join('');

  return `<div class="tablewrap pacer"><table>
    <tr>
      <th>Game</th>
      <th data-tip="Hours this account has actually spent running that game, as counted by nocat.farm.">Played</th>
      <th data-tip="How far through the rarity ladder those hours have taken it. Full bar is eighty hours, where the last tier opens.">Progress</th>
      <th data-tip="The rarest achievement currently eligible. Nothing below this can be unlocked yet however long it waits.">Floor</th>
      <th data-tip="The most of this game that will ever be completed. Rolled once per game and never reached exactly.">Ceiling</th>
      <th data-tip="When the next unlock is even allowed to happen. Not a promise that one will.">Next</th>
    </tr>
    ${rows}
  </table></div>${pacerRows.length > 12 ? `<p class="muted small">…and ${pacerRows.length - 12} more.</p>` : ''}`;
}

function sectionIntro(section, values) {
  const val = (k) => (pending[k] !== undefined ? pending[k] : values[k]);

  // Achievements are the one area where the settings alone tell you nothing useful. Three dials and an
  // appID list do not convey that this thing refuses to unlock anything the hours cannot justify - which is
  // the entire reason to trust it - so the section says so in words.
  if (section === 'Achievements') {
    if (!val('UnlockAchievements')) {
      return `<div class="explain">Off. This account earns no achievements at all - nothing is written to any
        game. Turn it on and it starts earning them at a pace that follows the hours actually put in.</div>`;
    }

    const pace = [' at twice the normal spacing', '', ' at half the normal spacing'][val('AchievementPace') ?? 1] || '';
    const cap = val('AchievementMaxCompletionPct');

    return `<div class="explain">
      <b>Earned against playtime, not on a timer${esc(pace)}.</b>
      <p style="margin:8px 0 0">An achievement only becomes eligible once this account has enough hours in that
      game for it to be plausible. The bar starts at <b>40% of owners or more</b> and drops as the hours build -
      to 5% at around 35 hours, and to 1% at eighty. It never reaches 0%: an achievement that fewer than one in
      two hundred players has is the thing that gives an idled account away.</p>
      <p style="margin:8px 0 0">Each game is paced to its own shape, so a three-hour puzzle game front-loads its
      easy ones while a co-op grind crawls. Unlocks cluster the way real ones do - two or three together, then
      nothing for a long stretch - and the most common still-locked one is always taken first.</p>
      <p style="margin:8px 0 0">${cap > 0
        ? `No game is taken past <b>${cap}%</b> complete.`
        : 'No game is ever finished - each stops well short, because 100% on an idled account is its own tell.'}
      <b>Counter-Strike 2 is never touched</b>, nor is whatever this account plays most.</p>
    </div>
    ${pacerTable()}`;
  }

  if (section === 'What it plays') {
    const name = (val('CustomGameName') || '').trim();

    // In human mode the games come from the weighted rotation, not from a list here. Saying "nothing set yet"
    // when a full schedule is running was simply wrong.
    if (val('LegitMode')) {
      const top = parseWeights(val('GameWeights'))[0];
      const main = top ? gameLabel(top.game) : 'the games you list under Human mode';

      return `<div class="preview"><span class="k">Right now</span>
        Human mode picks what it plays — mostly <b>${esc(main)}</b>, one game at a time.
        ${name ? `Your friends see <b>${esc(name)}</b> instead of the real game; the hours still count.` : 'Your friends see the real game.'}
        </div>`;
    }

    const games = val('IdleGames') || [];
    const black = val('BlacklistedGames') || [];
    const playing = games.filter((g) => !black.includes(g));

    let line;
    if (name && playing.length) {
      line = `Your friends see <b>${esc(name)}</b>. Underneath, <b>${playing.map(gameLabel).join(', ')}</b> keep gaining real playtime — both at the same time.`;
    } else if (name) {
      line = `Your friends see <b>${esc(name)}</b>. No games are listed, so no playtime is being banked — add some below if you want hours too.`;
    } else if (playing.length) {
      line = `<b>${playing.map(gameLabel).join(', ')}</b> gain playtime, all at once. Set a name below to show something else instead while the hours still count.`;
    } else {
      line = `Nothing set yet. Add games to bank playtime, and optionally a name to display instead of the real one.`;
    }

    return `<div class="preview"><span class="k">Right now</span>${line}</div>`;
  }

  if (section === 'Human mode') {
    if (!val('LegitMode')) {
      return `<div class="preview"><span class="k">Off</span>
        This account idles everything at once, around the clock. Switch Human mode on to play one game at a time
        on a believable schedule — the settings that would give it away are hidden and put back if you switch it off.</div>`;
    }

    const w = parseWeights(val('GameWeights'));
    const from = val('DayStartHour'), to = val('BedHour');
    // The scheduler reads WeekdayHours/WeekendHours. This used to show DailyHoursTarget, which was retired —
    // so the preview quoted a number nothing used and that no control on the page could move.
    const weekday = val('WeekdayHours'), weekend = val('WeekendHours');
    const dayOff = val('DayOffChancePct');
    const night = val('OfflineIdleAtNight') && (val('OfflineIdleGames') || []).length;

    return `<div class="preview"><span class="k">A day looks like</span>
      On around <b>${String(from).padStart(2, '0')}:00</b>, bed around <b>${String(to).padStart(2, '0')}:00</b> — both jittered daily.
      About <b>${weekday}h</b> on a weekday and <b>${weekend}h</b> at the weekend${dayOff > 0 ? `, and roughly <b>${dayOff} in 100</b> days off entirely` : ''}.
      One game at a time, in sittings of <b>${val('SessionMinMinutes')}–${val('SessionMaxMinutes')} min</b>.
      ${w.length ? `Mostly <b>${esc(gameLabel(w[0].game))}</b>${w.length > 1 ? `, with ${w.length - 1} other${w.length > 2 ? 's' : ''} in bursts rather than every day` : ''}.` : 'No games listed yet.'}
      ${night ? ' Overnight it goes invisible and keeps banking hours.' : ''}
      </div>`;
  }

  if (section === 'rep4rep commenting') {
    const cap = val('Rep4RepDailyCap') || 0;
    const lo = val('Rep4RepGapMinMinutes') || 0;
    const hi = Math.max(lo, val('Rep4RepGapMaxMinutes') || 0);
    const from = val('Rep4RepStartHour');
    const to = val('Rep4RepEndHour');
    const hours = from === to ? 'around the clock' : `between ${String(from).padStart(2, '0')}:00 and ${String(to).padStart(2, '0')}:00`;
    const span = Math.round((cap * (lo + hi) / 2) / 60 * 10) / 10;

    return `<div class="preview"><span class="k">Right now</span>
      Up to <b>${cap}</b> comments a day, <b>${lo}–${hi} minutes</b> apart, ${hours}.
      That's roughly <b>${span}h</b> of posting spread across the day.</div>`;
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

  // The main game's share is owned by MainGameSharePct, not by this list — the scheduler sizes row 0 against
  // the side games every morning and never reads its stored weight. Showing an editable box here was a lie: you
  // could drag it to 40 and nothing whatsoever would change. It's shown as a read-only figure that links to the
  // setting that does control it.
  const mainPct = Math.max(5, Math.min(95, liveValue('MainGameSharePct', settingsValues() || {}) ?? 70));

  const body = rows.map((r, i) => {
    const share = i === 0 ? mainPct : Math.round(((r.weight / Math.max(1, total - rows[0].weight)) * (100 - mainPct)));

    // "wmain", not "main": the page's own content-area class is .main, and this row was quietly picking up
    // its `padding: 22px 26px 40px`. That is the whole reason the first row sat 26px to the right of every
    // other one and stood three times as tall - it was being laid out as if it were the page.
    return `<div class="wrow ${i === 0 ? 'wmain' : ''}">
      <span class="wname"><b class="wgame" title="${esc(gameLabel(r.game))}">${esc(gameLabel(r.game))}</b>${i === 0 ? '<b class="wtag">main</b>' : ''}<i class="wid">${r.game}</i></span>
      <span class="wbar"><i style="width:${share}%"></i></span>
      ${i === 0
        ? `<span class="wpct wfixed" data-tip="The main game's share is set by &quot;Main game gets&quot; below, not here — it's held at that share however many other games you add. Click to jump to it.">${share}</span>`
        : `<input class="wpct" type="number" min="1" max="99" value="${share}" data-w-index="${i}"
             onchange="setShare(${i},parseInt(this.value)||1)" data-tip="This game's share of the week. Every row here adds up to 100, including the main game - change one and the others move to make room.">`}
      <span class="wsign">%</span>
      ${i === 0 ? '<span class="wact"></span>' : `<span class="wact"><b onclick="makeMain(${i})" data-tip="Make this the main game">↑</b><b onclick="dropWeight(${i})" data-tip="Remove">×</b></span>`}
    </div>`;
  }).join('');

  return `<div class="weights" data-setting="GameWeights">
    ${body || '<p class="muted small" style="margin:0 0 8px">No games yet — add the one this account is meant to be into first.</p>'}
    <div class="wadd">
      <input type="text" placeholder="appID or store URL" onkeydown="if(event.key==='Enter'){addWeight(this);event.preventDefault();}" onblur="addWeight(this)">
      <span class="muted small">The first game added is the main one.</span>
    </div>
  </div>`;
}

const weightsSpec = (rows) => rows.map((r) => `${r.game}:${r.weight}`).join(', ');

/// Set one game's share and even the remainder out across the others, so the row you didn't touch never has to
/// be worked out by hand and the total is always 100.
// Set one side game's SHARE OF THE WEEK, and move the other side games to make room.
//
// The box used to show the raw stored weight while the bar beside it showed the share - two different numbers
// for one row, and changing "Main game gets" moved the bar and left the box alone. Everything is a share now,
// so the column always adds up to 100 and the main game's slider visibly pushes the others around.
//
// The main game is never edited here. Its share is owned by MainGameSharePct and the scheduler re-derives row
// zero against the side games every morning, so a number typed into row zero would simply be ignored.
function setShare(index, wantPct) {
  const rows = parseWeights(liveWeights());
  if (!rows[index] || index === 0) return;

  const mainPct = Math.max(5, Math.min(95, liveValue('MainGameSharePct', settingsValues() || {}) ?? 70));
  const pool = 100 - mainPct;                       // what all the side games share between them
  const sides = rows.length - 1;
  if (sides < 1) return;

  // Everyone else needs at least 1, so this one cannot take the whole pool.
  const mine = Math.max(1, Math.min(pool - (sides - 1), wantPct));
  const rest = pool - mine;
  const otherIdx = rows.map((_, i) => i).filter((i) => i !== 0 && i !== index);

  // Split what is left in proportion to what the others already had, so nudging one game does not flatten
  // the balance between the rest.
  const prior = otherIdx.map((i) => Math.max(1, rows[i].weight));
  const priorSum = prior.reduce((a, b) => a + b, 0) || 1;

  rows[index].weight = mine;
  otherIdx.forEach((i, k) => { rows[i].weight = Math.max(1, Math.round(rest * prior[k] / priorSum)); });

  // Row zero's stored weight is not used by the scheduler, but keeping it consistent means the spec written to
  // disk reads the way the screen looks.
  rows[0].weight = mainPct;

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
    <h3>Careful</h3>
    <div class="dangerbox">
      <b>Unlock every achievement, in every game this account owns</b>
      <p class="muted small">This is for accounts that are not pretending to be anyone. Thousands of achievements
      appear at once, all stamped with the same moment, and that stamp is set by Steam and cannot be changed or
      hidden. Anyone looking at the profile can see it, permanently. If this account is meant to look played,
      leave this alone and let the pacer earn them instead.</p>
      <button class="danger" data-bot="${esc(name)}" onclick="askUnlockAll(this.dataset.bot)">Unlock everything…</button>
    </div>
  </div>`;
}

// The name travels in a data-attribute, never interpolated into the handler string. esc() escapes for HTML
// TEXT, not for a JavaScript string literal inside an attribute - an account named  it's  would close the
// quote and break the handler, and on the one irreversible action in the app that is not a risk worth taking.
function askUnlockAll(name) {
  modal(`
    <h2>Unlock everything on ${esc(name)}?</h2>
    <p>Every achievement in every game <b>${esc(name)}</b> owns will be unlocked, right now.</p>
    <ul class="muted small">
      <li>They all get the same unlock time. That is what makes it obvious.</li>
      <li>Steam sets that time itself - it cannot be back-dated or hidden.</li>
      <li>It runs for a long time on a big library, and it cannot be meaningfully undone.</li>
    </ul>
    <p class="small">Type <code>confirm</code> below to enable the button.</p>
    <input type="text" id="unlockConfirm" autocomplete="off" spellcheck="false" placeholder="type confirm"
           oninput="$('unlockGo').disabled = this.value.trim().toLowerCase() !== 'confirm'">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">Cancel</button>
      <button class="danger" id="unlockGo" disabled data-bot="${esc(name)}" onclick="doUnlockAll(this.dataset.bot)">Unlock everything</button>
    </div>`);
  setTimeout(() => { const el = $('unlockConfirm'); if (el) el.focus(); }, 30);
}

async function doUnlockAll(name) {
  const typed = ($('unlockConfirm').value || '').trim();

  // Sent to the server as well as checked here. A disabled button is a courtesy, not a guard.
  const res = await post(`/api/bots/${encodeURIComponent(name)}/achievements/unlock-all`, { Confirm: typed });
  closeModal();

  if (!res.ok) { toast(res.error || "That didn't work", true); return; }

  toast(`${name}: unlocking everything - watch the log`);
  go('log');
}

// Settings you can switch off, but should be asked about first.
//
// Not a general "are you sure" on every toggle - that trains people to click through warnings. Only where
// switching it OFF leaves the app quietly less useful in a way that will not be obvious later.
const GUARDED_OFF = {
  OpenDashboardAfterAdd: {
    title: 'Turn off opening the dashboard?',
    body: `<p>A newly added account does <b>nothing at all</b> until it is told what to play, and the app window
      has no form for that - only the dashboard does.</p>
      <p class="muted small">With this off, adding an account leaves you at a command line with an account that
      will sit there idle until you remember to go and configure it. This is for people who already know that.</p>`,
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
    <h2>${esc(guard.title)}</h2>
    ${guard.body}
    <p class="small">Type <code>off</code> to confirm.</p>
    <input type="text" id="guardConfirm" autocomplete="off" spellcheck="false" placeholder="type off"
           oninput="$('guardGo').disabled = this.value.trim().toLowerCase() !== 'off'">
    <div class="actions">
      <button class="ghost" onclick="closeModal()">Keep it on</button>
      <button class="danger" id="guardGo" disabled data-setting="${esc(name)}"
              onclick="edit(this.dataset.setting, false); closeModal(); renderSettings();">Turn it off</button>
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
        ? '<span class="pill bad">will be erased when you save</span>'
        : typing
          ? '<span class="pill warn">will be replaced when you save</span>'
          : isSet
            ? '<span class="pill good">saved</span>'
            : '<span class="pill">not set</span>';

      // Locked while something is stored, so a stray keystroke in a focused box cannot quietly overwrite a
      // working token with half a word. Clear is the deliberate act that unlocks it. Still enabled once a
      // replacement is being typed, so a redraw mid-edit cannot lock the box and discard what was in it.
      const locked = isSet && !cleared && !typing;

      ctl = `<div class="tags">
        <input type="password" id="${id}" data-setting="${def.Name}" style="max-width:260px"
               ${locked ? 'disabled' : ''}
               title="${locked ? 'Locked so it cannot be changed by accident. Press Clear to replace it.' : ''}"
               placeholder="${cleared ? 'paste the new one here' : isSet ? 'stored - press Clear to replace' : 'paste it here'}"
               value="${cleared || cur === undefined ? '' : esc(cur)}" oninput="edit('${def.Name}',this.value)">
        ${state}
        ${isSet && !cleared ? `<button class="ghost small" data-tip="Erase the stored value. There's no undo." onclick="clearSecret('${def.Name}')">Clear</button>` : ''}
        ${cleared ? `<button class="ghost small" onclick="editAndRender('${def.Name}','')">Undo</button>` : ''}
      </div>`;
      break;
    }
    case 'Choice': {
      const opts = parseChoices(def.Choices);
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
        <input type="text" style="max-width:150px" placeholder="appID or store URL" onkeydown="if(event.key==='Enter'||event.key===','){addApp('${def.Name}',this);event.preventDefault();}" onblur="addApp('${def.Name}',this)">
      </div>`;
      break;
    }
    default:
      // The weights are the one setting people actually tune, and "730:70, 440:20" is a terrible thing to have
      // to hand-write. It gets a real editor; everything else is a text box.
      ctl = def.Name === 'GameWeights'
        ? weightsEditor(cur)
        : `<input type="text" id="${id}" data-setting="${def.Name}" value="${esc(cur)}" placeholder="${esc(def.Placeholder || '')}" oninput="edit('${def.Name}',this.value)">`;
  }

  const def0 = defaults[def.Name];
  const defText = Array.isArray(def0) ? (def0.length ? def0.join(', ') : 'none') : (def.Kind === 'Secret' ? '' : String(def0));

  return `<div class="field ${changed ? 'changed' : ''}">
    <label for="${id}">${esc(def.Label)}${tipIcon(def.Tooltip)}</label>
    <div class="ctl">${ctl}</div>
    <div class="meta">
      ${def.NeedsRestart ? `<span class="restart" data-tip="This takes effect the next time nocat.farm starts.">⟳</span>` : ''}
      ${defText !== '' ? `<span class="revert" data-tip="Put this back to the default." onclick="editAndRender('${def.Name}',${JSON.stringify(def0).replace(/"/g, '&quot;')})">default ${esc(defText)}</span>` : ''}
    </div></div>`;
}

function parseChoices(choices) {
  if (!choices) return [];
  return choices.split('|').map((o) => {
    const t = o.trim();
    const sp = t.indexOf(' ');
    return { value: parseInt(t.slice(0, sp)), label: t.slice(sp + 1) };
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
    const html = sectionIntro(title.textContent, values);

    if (html) {
      const tmp = document.createElement('div');
      tmp.innerHTML = html;
      const fresh = tmp.firstElementChild;
      if (fresh) prev.innerHTML = fresh.innerHTML;
    }
  });
}

function clearSecret(name) {
  if (!confirm('Erase the stored value? There is no undo.')) return;
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
  if (!m) { toast('That is not an appID', true); return; }
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
  btn.textContent = n === 0 ? 'Save' : `Save ${n} change${n > 1 ? 's' : ''}`;
}

async function saveSettings() {
  const target = settingsTarget;
  const edits = { ...pending };

  // Re-read first, then apply only what was actually edited. Posting the snapshot taken when the page was
  // opened would quietly revert anything changed from the console (or another tab) in the meantime.
  await loadConfig();
  const base = target === GLOBAL ? config.Global : config.Bots[target];
  if (!base) { toast('That account is gone', true); settingsTarget = GLOBAL; renderSettings(); return; }

  const body = { ...base, ...edits };
  const res = target === GLOBAL
    ? await post('/api/config', body)
    : await post('/api/bots/' + encodeURIComponent(target) + '/config', body);

  if (!res.ok) { toast(res.error || 'Save failed', true); return; }

  const restart = (res.RestartNeeded || []).filter(Boolean);
  const adjusted = (res.Adjusted || []).filter(Boolean);

  if (adjusted.length) toast(adjusted.join(' · '), true);
  else toast(restart.length
    ? `Saved. ${restart.join(', ')} ${restart.length > 1 ? 'apply' : 'applies'} after a restart.`
    : 'Saved.');

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
  if (!ping) { showLogin("Can't reach nocat.farm."); return; }
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
  go(view);
  await refresh();   // arms the single polling timer

  if (shouldShowTutorial()) startTutorial();
}

boot();
