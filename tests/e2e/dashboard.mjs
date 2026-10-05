// Every button, switch, tab, chip and form of the nocat.farm dashboard, clicked in a real browser (Chromium, through
// Playwright) against a copy that is already running:
//   node tests/e2e/dashboard.mjs <base url> <dashboard password> [--fixture <folder>] [--set-password] [--ping-stub <url>] [--headed]
// --set-password  the copy has no password yet: set this one first, from this PC, the way the Phone page does
// --fixture       the folder make-fixture.mjs wrote (for the pretend ArchiSteamFarm folder the import dialog is shown)
// --ping-stub     ping-stub.mjs, which the copy was started to send its "Count me as a user" ping to (NOCATFARM_PING_URL):
//                 the user count is checked against it. Without it the setting is left alone - it would tell nocat.lol.
//
// It signs in (a wrong password first), then on every page lists everything that can be clicked, typed in or picked,
// and uses each in a safe way: switches and boxes are put back afterwards, anything that deletes, signs in to Steam,
// installs, restores, opens the router or the firewall, or changes the password it signs in with is skipped by name
// (listed as SKIP), and every confirm() is answered "no". After each one: no script error, no console error, no answer
// of 500 or more from the app, nothing left greyed out, and the page still drawn. Then every setting, global and per
// account, is changed through its own control, saved, the page reloaded and the value checked through /api/config,
// and everything is put back. Then the console, the keyboard (Tab reaches every role=button, Enter and Space press
// it) and the layout at a phone's 375px and a desktop's 1280px.
//
// The browser can only reach the copy under test: every request anywhere else is refused, so nothing here can talk
// to Steam, GitHub, rep4rep or anyone else. One PASS/FAIL line per check, then "all passed" or "N failed".
import { chromium } from 'playwright';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// ── arguments ────────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const option = (name) => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : undefined; };
const positional = argv.filter((a, i) => !a.startsWith('--') && !(i > 0 && ['--fixture', '--ping-stub'].includes(argv[i - 1])));
const BASE = (positional[0] || '').replace(/\/+$/, '');
const PASSWORD = positional[1] || '';
const FIXTURE = option('--fixture') || '';
const PING_STUB = (option('--ping-stub') || '').replace(/\/+$/, '');
if (!BASE || !PASSWORD) {
  console.error('usage: node dashboard.mjs <base url> <dashboard password> [--fixture <folder>] [--set-password] [--ping-stub <url>] [--headed]');
  process.exit(2);
}
const HOST = new URL(BASE).host;
const OUT = fs.mkdtempSync(path.join(os.tmpdir(), 'nocatfarm-e2e-'));

// ── reporting ────────────────────────────────────────────────────────────────
let fails = 0;
let passes = 0;
const skipped = [];
function check(name, ok, detail) {
  if (ok) { passes++; console.log(`PASS  ${name}`); } else { fails++; console.log(`FAIL  ${name}${detail ? ` - ${detail}` : ''}`); }
  return ok;
}
function skip(what, why) {
  const line = `${what} - ${why}`;
  if (!skipped.includes(line)) { skipped.push(line); console.log(`SKIP  ${line}`); }
}
const note = (text) => console.log(`      ${text}`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ── what is never pressed ────────────────────────────────────────────────────
// By the function a control calls (or its data-act), never only by its words: the words change with the language, the
// function doesn't. Anything with a confirm() in front of it IS pressed - the confirm is answered no.
const DENY_HANDLER = {
  doRemoveBot: 'deletes the account',
  applyRestore: 'restores a backup over this copy',
  doUpdate: 'downloads and installs an update',
  doUnlockAll: 'unlocks every achievement on Steam, for good',
  phoneRestart: 'restarts the dashboard it is being tested through',
  allowFirewall: 'changes the Windows Firewall',
  tutFirewall: 'changes the Windows Firewall',
  phoneRemote: 'asks the router to open a port (UPnP)',
  phoneSavePw: 'changes the password this test signs in with',
  tutSaveSecret: 'changes the password this test signs in with',
  openLogFolder: 'opens a file manager window on the PC',
  createBot: 'adds an account, which starts signing in to Steam',
  createFirstBot: 'adds an account, which starts signing in to Steam',
  impApply: 'imports accounts into this copy',
  connectRep4Rep: 'talks to rep4rep.com',
  replaceToken: 'talks to rep4rep.com',
  enableAllRep4Rep: 'switches rep4rep on for every account',
  loadRep4Rep: 'talks to rep4rep.com',
  window: 'opens an outside website',
  authAct: 'confirms or denies on Steam',
  startTutorial: 'walked through step by step on its own below',
  tutorialOpenImport: 'the walkthrough, walked through on its own',
};
const DENY_ACT = {
  start: 'signs in to Steam',
  resume: 'signs in to Steam',
  postnow: 'posts a rep4rep comment',
  register: 'registers the profile on rep4rep.com',
  inventory: 'asks Steam for the inventory',
};
// Words that mean "can't be taken back", for anything the lists above don't know by its function.
const DENY_WORDS = /\b(uninstall|shut ?down|exit|quit|stop all|update to|install|erase|delete)\b/i;

// ── settings the loop leaves alone ───────────────────────────────────────────
const SKIP_SETTING = {
  WebEnabled: 'switches the dashboard off at the next start',
  WebHost: 'moves the dashboard to another address at the next start',
  WebPort: 'moves the dashboard to another port at the next start',
  WebPassword: 'the password this test signs in with',
  WebRemoteAccess: 'asks the router to open a port (UPnP)',
  StartWithWindows: "would replace the real copy's Windows startup entry",
  ExitWhenAllFinished: 'closes the app under test (every account here is finished)',
  // What the app shows on this PC's own screen: none of it may ever appear while a test runs.
  Language: 'the language the app window and its tray talk in',
  OpenBrowserOnStart: 'opens a browser tab on this PC',
  OpenDashboardAfterAdd: 'opens a browser tab on this PC',
  Tray: 'a tray icon on this PC',
  MinimizeToTray: 'the app window on this PC',
  StartMinimized: 'the app window on this PC',
  MiniOnTop: 'the app window on this PC',
  KeepAwake: "keeps this PC from sleeping",
  TrayNotifications: 'pop-ups on this PC',
  NotifyEarnings: 'pop-ups on this PC',
  NotifySocial: 'pop-ups on this PC',
  NotifyProblems: 'pop-ups on this PC',
  NotifyTrades: 'pop-ups on this PC',
  CheckForUpdates: 'asks GitHub for a newer version',
  UpdateMode: 'could install a newer version mid-test',
  DiscordPresence: "shows on the Discord running on this PC",
  ...(PING_STUB ? {} : { CountMeAsUser: 'tells nocat.lol this copy is running (no --ping-stub to send it to instead)' }),
  TelegramBotToken: 'connects to Telegram',
  DiscordBotToken: 'connects to Discord',
  DiscordWebhookUrl: 'posts to Discord',
  Rep4RepApiToken: 'connects to rep4rep.com',
  Enabled: 'signs the account in to Steam',
  SharedSecret: 'an authenticator secret - the Authenticator page is tested with the one in the fixture',
  IdentitySecret: 'an authenticator secret - confirmations are fetched from Steam',
};
// Switched last and put back first, each saved on its own: they change which other settings exist (human mode hides the
// robot ones, rep4rep off hides its sections) and the server rewrites fields when they change.
const MODE_SWITCHES = ['LegitMode', 'Rep4RepEnabled'];
// A value the server accepts for each free-text setting (text isn't checked, but these are the shapes they take).
const TEXT_VALUE = {
  WebPublicAddress: 'http://192.0.2.1:7377', WebTrustedProxies: '192.0.2.10', WebProxy: 'http://127.0.0.1:9', WebProxyUsername: 'e2e',
  TelegramChatId: '123456789', DiscordOwnerId: '123456789012345678', GroupsToJoin: 'e2e-group', SteamLogin: 'not_a_real_account_e2e',
  Notes: 'e2e note', MachineName: 'e2e-pc', AccountProxy: 'http://127.0.0.1:9', AccountProxyUsername: 'e2e', CustomGameName: 'e2e game',
  HourTargets: '440:10', SendItemTypes: 'cards', TradeMasters: '76561197960287930', AutoTradeWith: '76561197960287930',
  TradeMasterToken: 'e2etoken', CommandMasters: '76561197960287930', AutoReply: 'e2e reply', ExtraGroupsToJoin: 'e2e-group',
};
// A choice that shows other settings under one answer is changed TO that answer, so those settings appear and get
// changed and put back too ("Share of sittings that farm cards" only shows with "mixed").
const CHOICE_VALUE = { FarmCardsWhen: 3 };
const SECRET_VALUE = { SteamPassword: 'e2e-not-a-password', SteamParentalCode: '1234', AccountProxyPassword: 'e2e-proxy-pw', WebProxyPassword: 'e2e-proxy-pw' };

// ── the page's side: what can be clicked, found again by a key that survives a redraw ──
const PAGE_HELPERS = `
window.__e2e = (() => {
  const SEL = 'button, [role=button], input, select, textarea, label.switch, a[href], .chip, summary, [data-act], [data-jump], [data-log-source], [data-auth-pick], [data-task], [onclick]';
  const visible = (el) => {
    if (!el.isConnected || !el.getClientRects().length) return false;
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none') return false;
    return !el.closest('.hidden');
  };
  const text = (el) => {
    if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return (el.getAttribute('placeholder') || el.getAttribute('aria-label') || el.type || '');
    if (el.tagName === 'SELECT') return 'select ' + (el.id || el.getAttribute('aria-label') || '');
    return (el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('data-tip') || '').replace(/\\s+/g, ' ').trim();
  };
  const handlerOf = (el) => {
    const on = el.getAttribute('onclick') || el.getAttribute('onchange') || '';
    const m = on.match(/^\\s*(?:\\(\\)\\s*=>\\s*)?([A-Za-z_$][\\w$]*)/);
    return m ? m[1] : '';
  };
  const clickable = (el) => el.matches('button, [role=button], a[href], summary, [data-act], [onclick], [data-jump], [data-log-source], [data-auth-pick], [data-task], label.switch');
  const keyOf = (el, withBot) => [el.tagName, el.id, el.getAttribute('type') || '', el.getAttribute('data-act') || '',
    withBot ? (el.getAttribute('data-bot') || '') : '', el.getAttribute('data-view') || '', el.getAttribute('data-jump') || '',
    el.getAttribute('data-log-source') || '', el.getAttribute('data-auth-pick') || '', el.getAttribute('data-setting') || '',
    (el.getAttribute('onclick') || el.getAttribute('onchange') || el.getAttribute('oninput') || '').slice(0, 90),
    el.className && typeof el.className === 'string' ? el.className.replace(/\\b(on|active|sel|changed|dragging)\\b/g, '').trim() : '',
    // Numbers only blurred in the words: a count on a chip changes, setHistRange(7) and (30) are two things.
    text(el).slice(0, 40).replace(/\\d+/g, '#')].join('|');
  function list(rootSel, excludeSel, withBot) {
    const out = [];
    for (const root of document.querySelectorAll(rootSel)) {
      if (!visible(root)) continue;
      for (const el of root.querySelectorAll(SEL)) {
        if (!visible(el)) continue;
        if (excludeSel && el.closest(excludeSel)) continue;
        if (el.tagName === 'INPUT' && el.closest('label.switch')) continue;       // pressed through its switch
        if (el.tagName === 'INPUT' && el.type === 'hidden') continue;
        if (el.matches('label.switch span')) continue;
        // A chip or row inside something that is itself pressed (a status chip in a clickable table row).
        const up = el.parentElement && el.parentElement.closest('[data-act], [onclick], [role=button], button, a[href]');
        if (up && !el.matches('button, input, select, textarea, a[href]') && root.contains(up)) continue;
        out.push({
          key: keyOf(el, withBot), tag: el.tagName.toLowerCase(), type: (el.getAttribute('type') || '').toLowerCase(),
          id: el.id, role: el.getAttribute('role') || '', act: el.getAttribute('data-act') || '', bot: el.getAttribute('data-bot') || '',
          handler: handlerOf(el), onclick: (el.getAttribute('onclick') || el.getAttribute('onchange') || '').slice(0, 160),
          href: el.getAttribute('href') || '', target: el.getAttribute('target') || '', rel: el.getAttribute('rel') || '',
          text: text(el).slice(0, 60), disabled: !!el.disabled, isSwitch: el.matches('label.switch'),
          checked: el.matches('label.switch') ? !!(el.querySelector('input') || {}).checked : !!el.checked,
          value: 'value' in el ? String(el.value) : '', clickable: clickable(el),
          options: el.tagName === 'SELECT' ? [...el.options].map((o) => o.value) : null,
        });
      }
    }
    return out;
  }
  function mark(rootSel, key, withBot) {
    document.querySelectorAll('[data-e2e-now]').forEach((e) => e.removeAttribute('data-e2e-now'));
    for (const root of document.querySelectorAll(rootSel)) {
      for (const el of root.querySelectorAll(SEL)) {
        if (visible(el) && keyOf(el, withBot) === key) { el.setAttribute('data-e2e-now', '1'); return true; }
      }
    }
    return false;
  }
  function state(rootSel, key, withBot) {
    for (const root of document.querySelectorAll(rootSel)) {
      for (const el of root.querySelectorAll(SEL)) {
        if (visible(el) && keyOf(el, withBot) === key) return { found: true, disabled: !!el.disabled };
      }
    }
    return { found: false };
  }
  // Every element Tab lands on, in order.
  const focused = [];
  document.addEventListener('focusin', (e) => { focused.push(e.target); }, true);
  // Every element a click reaches (a keyboard Enter or Space on a role=button included).
  const clicked = [];
  document.addEventListener('click', (e) => { clicked.push(e.target); }, true);
  return { list, mark, state, focused, clicked, visible, keyOf };
})();
`;

// ── the browser ──────────────────────────────────────────────────────────────
const browser = await chromium.launch({ headless: !flag('--headed') });
const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: new URL(BASE).origin });
await context.addInitScript(PAGE_HELPERS);

// Only the copy under test. Anything else - Steam's store pictures, GitHub, rep4rep, a pop-up - is refused and noted.
const blocked = new Set();
await context.route('**/*', (route) => {
  const url = new URL(route.request().url());
  if (url.host === HOST || url.protocol === 'data:' || url.protocol === 'blob:') return route.continue();
  blocked.add(url.origin);
  return route.abort();
});

const page = await context.newPage();
page.setDefaultTimeout(8000);
const problems = [];      // since the last check: script errors, console errors, answers >= 500
page.on('pageerror', (e) => problems.push(`script error: ${e.message}`));
page.on('console', (m) => {
  if (m.type() !== 'error') return;
  const where = (m.location() && m.location().url) || '';
  // A request this test refused on purpose (an outside address) - not the page's fault.
  if (/Failed to load resource: net::ERR_FAILED/.test(m.text()) && where && !where.includes(HOST)) return;
  problems.push(`console error: ${m.text()}${where ? ` (${where})` : ''}`);
});
page.on('response', (r) => { if (r.status() >= 500) problems.push(`HTTP ${r.status()} ${r.request().method()} ${r.url()}`); });
page.on('popup', async (p) => { note(`a pop-up opened (${p.url()}) - closed`); await p.close().catch(() => {}); });

// confirm(): "no", unless a step here means yes (putting back a secret this test stored itself).
let dialogAnswer = 'dismiss';
const dialogs = [];
page.on('dialog', async (d) => {
  dialogs.push(d.message());
  note(`${d.type()}() answered ${dialogAnswer === 'accept' ? 'yes' : 'no'}: "${d.message()}"`);
  if (dialogAnswer === 'accept') await d.accept(); else await d.dismiss();
});

// The backup the Settings page downloads is the one the restore check is shown.
let backupFile = '';
page.on('download', async (d) => {
  backupFile = path.join(OUT, d.suggestedFilename() || 'backup.zip');
  await d.saveAs(backupFile).catch(() => { backupFile = ''; });
  note(`downloaded ${d.suggestedFilename()}`);
});
page.on('filechooser', async (fc) => {
  if (backupFile && fs.existsSync(backupFile)) { note(`picked ${path.basename(backupFile)} in the file dialog`); await fc.setFiles(backupFile); } else await fc.setFiles([]);
});

// Requests still on their way, so a check waits for the page to settle rather than a guess.
let inflight = 0;
page.on('request', (r) => { if (r.url().includes(HOST)) inflight++; });
const done = (r) => { if (r.url().includes(HOST)) inflight = Math.max(0, inflight - 1); };
page.on('requestfinished', done);
page.on('requestfailed', done);

async function settle(ms = 250) {
  await sleep(ms);
  for (let i = 0; i < 30 && inflight > 0; i++) await sleep(100);
  await sleep(80);
}

const evalq = (fn, arg) => page.evaluate(fn, arg);
async function api(p, method = 'GET', body) {
  return evalq(async ({ p, method, body }) => {
    const res = await fetch(p, { method, headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + (localStorage.getItem('nocatfarm-token') || '') }, body: body ? JSON.stringify(body) : undefined });
    return res.json();
  }, { p, method, body });
}
const currentView = () => evalq(() => (typeof view === 'string' ? view : ''));

// After every action: nothing broke, and the page is still there.
async function healthy(label, extra = []) {
  await settle(150);
  const signedOut = await evalq(() => !document.getElementById('login').classList.contains('hidden'));
  const v = await currentView();
  const drawn = await evalq((v) => {
    const s = document.getElementById('view-' + v);
    return !!s && !s.classList.contains('hidden') && s.getBoundingClientRect().height > 0 && !document.getElementById('app').classList.contains('hidden');
  }, v);
  const bad = [...problems, ...extra];
  problems.length = 0;
  if (signedOut) bad.push('the page went back to the sign-in box');
  if (!drawn) bad.push(`the ${v} page isn't drawn`);
  const badToasts = await evalq(() => [...document.querySelectorAll('.toast.bad')].map((t) => t.textContent));
  for (const t of badToasts) note(`red toast: ${t}`);
  await evalq(() => document.querySelectorAll('.toast').forEach((t) => t.remove()));
  check(label, bad.length === 0, bad.join('; '));
  if (signedOut) await signIn();
  return bad.length === 0;
}

async function signIn() {
  await page.fill('#pw', PASSWORD);
  await page.click('#login button[type=submit]');
  await page.waitForSelector('#app:not(.hidden)', { timeout: 15000 });
  await settle(500);
}

async function gotoView(v) {
  if ((await currentView()) === v && await evalq((v) => !document.getElementById('view-' + v).classList.contains('hidden'), v)) return;
  const tab = page.locator(`.navitem[data-view="${v}"]`);
  if (await tab.isVisible()) await tab.click(); else await evalq((v) => go(v), v);
  await settle(400);
}

async function closeModalIfOpen() {
  for (let i = 0; i < 3; i++) {
    const open = await evalq(() => !document.getElementById('modal').classList.contains('hidden'));
    if (!open) return;
    await page.keyboard.press('Escape');
    await settle(200);
  }
}

const modalOpen = () => evalq(() => !document.getElementById('modal').classList.contains('hidden'));
const isExternal = (href) => /^[a-z]+:/i.test(href) && !href.startsWith(BASE);

function denyReason(c) {
  if (DENY_HANDLER[c.handler]) return DENY_HANDLER[c.handler];
  if (c.act && DENY_ACT[c.act]) return DENY_ACT[c.act];
  if (c.handler === 'phone' && /pwOpen=true/.test(c.onclick)) return '';
  if (/data-task/.test(c.key)) return 'posts a rep4rep comment';
  if (c.clickable && DENY_WORDS.test(c.text) && !c.handler && !c.act) return `reads like "${c.text}"`;
  return '';
}

// ── using one control ────────────────────────────────────────────────────────
// The value typed into a box nobody has told this test about.
function textFor(c) {
  if (c.id === 'impPath') return FIXTURE ? path.join(FIXTURE, 'fake-asf') : 'e2e';
  if (c.id === 'cmd') return 'status';
  return 'e2e';
}

// Click the control marked for this step. The pages redraw themselves every few seconds, and a redraw between marking and
// clicking takes the mark with it: the click then waited for an element that no longer existed and timed out - on the
// slow Intel Mac runner, a different control each run. Marked again and tried again, up to three times.
async function clickMarked(c, rootSel, withBot) {
  for (let i = 0; ; i++) {
    try {
      await page.locator('[data-e2e-now]').click({ timeout: 4000 });
      return;
    } catch (e) {
      if ((i >= 2) || !/Timeout/i.test(String(e.message || e))) throw e;
      if (!(await evalq(({ r, k, b }) => window.__e2e.mark(r, k, b), { r: rootSel, k: c.key, b: withBot }))) throw e;
    }
  }
}

async function use(c, rootSel, withBot) {
  if (!(await evalq(({ r, k, b }) => window.__e2e.mark(r, k, b), { r: rootSel, k: c.key, b: withBot }))) return 'gone';
  const el = page.locator('[data-e2e-now]');
  const opts = { timeout: 5000 };

  if (c.tag === 'a' && c.href && (isExternal(c.href) || c.target === '_blank')) {
    // Leaves the page: only whether it opens safely, in a new tab that can't reach back.
    check(`link "${c.text || c.href}" opens in a new tab, with noopener`, c.target === '_blank' && /noopener/.test(c.rel), `target=${c.target} rel=${c.rel}`);
    return 'link';
  }
  if (c.isSwitch || (c.tag === 'input' && (c.type === 'checkbox' || c.type === 'radio'))) {
    await clickMarked(c, rootSel, withBot);
    await settle();
    if (await modalOpen()) return 'modal';
    // Put it back - it may have been redrawn, so found again.
    if (await evalq(({ r, k, b }) => window.__e2e.mark(r, k, b), { r: rootSel, k: c.key, b: withBot })) {
      const again = page.locator('[data-e2e-now]');
      if (!(await again.isDisabled().catch(() => true))) { await clickMarked(c, rootSel, withBot); await settle(); }
    }
    return 'toggled';
  }
  if (c.tag === 'select') {
    const other = (c.options || []).find((o) => o !== c.value);
    if (other === undefined) return 'one option';
    await el.selectOption(other, opts);
    await settle();
    if (await evalq(({ r, k, b }) => window.__e2e.mark(r, k, b), { r: rootSel, k: c.key, b: withBot })) {
      await page.locator('[data-e2e-now]').selectOption(c.value, opts).catch(() => {});
      await settle();
    }
    return 'picked';
  }
  if ((c.tag === 'input' && !['button', 'submit', 'file', 'range', 'color'].includes(c.type)) || c.tag === 'textarea') {
    const typed = c.type === 'number' ? String((Number(c.value) || 0) + 1) : textFor(c);
    await el.fill(typed, opts);
    await el.blur().catch(() => {});
    await settle();
    if (c.id === 'impPath' || c.id === 'cmd') return 'typed';
    if (await evalq(({ r, k, b }) => window.__e2e.mark(r, k, b), { r: rootSel, k: c.key, b: withBot })) {
      const again = page.locator('[data-e2e-now]');
      if (!(await again.isDisabled().catch(() => true))) { await again.fill(c.value, opts).catch(() => {}); await again.blur().catch(() => {}); await settle(); }
    }
    return 'typed';
  }
  if (c.tag === 'input' && c.type === 'range') {
    await el.fill(String(c.value), opts).catch(() => {});
    return 'slid';
  }
  await clickMarked(c, rootSel, withBot);
  return 'clicked';
}

// ── the crawl: everything on a page, one at a time ───────────────────────────
const label = (c) => `${c.tag}${c.id ? '#' + c.id : ''}${c.act ? `[${c.act}]` : ''} "${(c.text || c.handler || c.onclick).slice(0, 48)}"`;

async function crawl(where, rootSel, excludeSel, max = 400) {
  const visited = new Set();
  let actions = 0;
  let disabledSeen = 0;
  let sampled = 0;
  const sameKind = {};
  for (;;) {
    if (actions >= max) { note(`${where}: stopped after ${max} controls`); break; }
    const v = await currentView();
    const list = await evalq(({ r, x }) => window.__e2e.list(r, x, true), { r: rootSel, x: excludeSel });
    // What switches the page to another account or section goes last, after everything the current one shows.
    const switches = (x) => ['selectSettings', 'filterTo', 'go'].includes(x.handler) || !!x.key.split('|')[8] || !!x.key.split('|')[5];
    const c = list.find((x) => !visited.has(x.key) && !switches(x)) || list.find((x) => !visited.has(x.key));
    if (!c) break;
    visited.add(c.key);
    if (c.disabled) { disabledSeen++; continue; }
    // A long list of the same thing (a hundred commands, a name on every log line): the first few stand for the rest.
    const sig = `${c.tag}|${c.handler}|${c.act}|${c.key.split('|')[11]}`;
    sameKind[sig] = (sameKind[sig] || 0) + 1;
    if (sameKind[sig] > 6 && (c.handler || c.act || /data-log-source/.test(c.key) || c.key.split('|')[7])) { sampled++; continue; }
    const why = denyReason(c);
    if (why) { skip(`${where}: ${label(c)}`, why); continue; }
    actions++;
    let result;
    try {
      result = await use(c, rootSel, true);
    } catch (e) {
      problems.push(`couldn't use it: ${String(e.message || e).split('\n')[0]}`);
    }
    if (result === 'gone') { actions--; visited.delete(c.key); visited.add(c.key + '#gone'); continue; }
    await settle();
    if (await modalOpen()) await crawlModal(`${where} > ${c.text.slice(0, 30) || c.handler}`);
    // Left greyed out for good (a button that disables itself while it works must come back).
    let stuck = false;
    const st = await evalq(({ r, k }) => window.__e2e.state(r, k, true), { r: rootSel, k: c.key });
    if (st.found && st.disabled && c.clickable && c.id !== 'saveBtn') {
      stuck = true;
      for (let i = 0; i < 30 && stuck; i++) { await sleep(200); stuck = (await evalq(({ r, k }) => window.__e2e.state(r, k, true), { r: rootSel, k: c.key })).disabled; }
    }
    await healthy(`${where}: ${label(c)}${result && result !== 'clicked' ? ` (${result})` : ''}`, stuck ? ['it stayed greyed out'] : []);
    // A control that went to another page (a Settings button, "Open on your phone"): back to this one.
    const now = await currentView();
    if (now !== v && where !== 'rail') await gotoView(v);
  }
  if (disabledSeen) note(`${where}: ${disabledSeen} greyed-out control(s) left alone, as they should be`);
  if (sampled) note(`${where}: ${sampled} more of the same kind (list rows) - the first 6 of each kind stand for them`);
  return actions;
}

// A pop-up: everything in it, closing buttons last, then Escape if it's still up.
const modalKeys = new Set();
async function crawlModal(where) {
  for (let n = 0; n < 60; n++) {
    if (!(await modalOpen())) return;
    const list = await evalq(() => window.__e2e.list('#modalCard', '', false));
    const closes = (c) => c.handler === 'closeModal' || c.handler === 'closeTutorial' || /tut-x/.test(c.key);
    const leaves = (c) => ['settings', 'remove'].includes(c.act) || ['useCommand', 'go', 'goSetting', 'openBotSettings'].includes(c.handler);
    const fresh = list.filter((c) => !modalKeys.has(c.key));
    const c = fresh.find((x) => !closes(x) && !leaves(x)) || fresh.find((x) => !closes(x)) || fresh.find(closes);
    if (!c) break;
    modalKeys.add(c.key);
    if (c.disabled) { note(`${where}: ${label(c)} greyed out, left alone`); continue; }
    const why = denyReason(c);
    if (why) { skip(`${where}: ${label(c)}`, why); continue; }
    try {
      await use(c, '#modalCard', false);
    } catch (e) {
      problems.push(`couldn't use it: ${String(e.message || e).split('\n')[0]}`);
    }
    await settle();
    await healthy(`${where}: ${label(c)}`);
  }
  if (await modalOpen()) {
    await page.keyboard.press('Escape');
    await settle();
    check(`${where}: Escape closes the pop-up`, !(await modalOpen()));
    await closeModalIfOpen();
  }
}

// Filled in as it goes, and reported at the end even when something threw part-way.
let views = [];
const covered = {};
const settingsCount = {};

try {
// ── 1. signing in ────────────────────────────────────────────────────────────
const ping = await fetch(BASE + '/api/ping').then((r) => r.json()).catch(() => null);
if (!ping) { check('the copy answers /api/ping', false, BASE); process.exit(1); }
if (flag('--set-password') && !ping.needsPassword) {
  const r = await fetch(BASE + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ Line: `set WebPassword ${PASSWORD}` }) }).then((x) => x.json()).catch(() => null);
  check('a dashboard password is set from this PC', !!r);
}

await page.goto(BASE + '/');
await page.waitForSelector('#login:not(.hidden)', { timeout: 15000 });
check('the sign-in box shows before anything else', await page.isVisible('#pw'));
await page.fill('#pw', 'not-the-password');
await page.click('#login button[type=submit]');
await page.waitForFunction(() => document.getElementById('loginError').textContent.trim() !== '', null, { timeout: 5000 }).catch(() => {});
check('a wrong password is refused, and says so', (await page.textContent('#loginError')).trim().length > 0 && await page.isVisible('#login'));
problems.length = 0;   // the 401 for the wrong password is the point of that check
await signIn();
check('the right password opens the dashboard', await page.isVisible('#app'));
await healthy('first draw after signing in');

// ── 2. every page, every control ─────────────────────────────────────────────
views = await evalq(() => [...document.querySelectorAll('.navitem')].filter((n) => window.__e2e.visible(n)).map((n) => n.dataset.view));
const inMarkup = await evalq(() => [...document.querySelectorAll('.navitem')].map((n) => n.dataset.view));
check(`every page has its tab (${views.join(', ')})`, inMarkup.every((v) => views.includes(v)), `hidden: ${inMarkup.filter((v) => !views.includes(v)).join(', ')}`);
for (const v of views) {
  await gotoView(v);
  check(`${v}: the tab opens its page`, (await currentView()) === v && await page.isVisible(`#view-${v}`));
  await healthy(`${v}: first draw`);
  // The settings rows themselves are the settings loop's (section 4); everything around them is here.
  const exclude = v === 'settings' ? '#settingsBody .field, #settingsBody .langpick, #settingsBody .dhead, #settingsBody .dline' : '';
  covered[v] = await crawl(v, `#view-${v}, #alerts`, exclude);
  // A command picked from the list only fills the box - and it stays filled with whatever was picked last ("exit").
  // Emptied, so no Enter later can run it.
  if (v === 'console') await evalq(() => { document.getElementById('cmd').value = ''; });
}

// The Authenticator: the account with a secret shows its Steam Guard code, worked out here from the clock.
if (views.includes('auth')) {
  await gotoView('auth');
  await page.click('[data-auth-pick="authbot"]');
  await settle(300);
  const code = ((await page.textContent('.auth-digits').catch(() => '')) || '').trim();
  check('auth: the account with a secret shows a Steam Guard code', /^[23456789BCDFGHJKMNPQRTVWXY]{5}$/.test(code), code);
  const left = async () => Number(await page.textContent('.auth-ring text').catch(() => 'NaN'));
  const a = await left();
  await sleep(2100);
  const b = await left();
  check('auth: its ring counts down', Number.isFinite(a) && Number.isFinite(b) && (b < a || b > 25), `${a} then ${b}`);
  await healthy('auth: healthy after reading a code');
}

// The rail: every tab, the status chips, the theme switch.
await gotoView('overview');
covered.rail = await crawl('rail', 'header.rail', '');
await gotoView('overview');

// ── 3. the walkthrough, step by step ─────────────────────────────────────────
{
  const before = await api('/api/config');
  await evalq(() => startTutorial());
  await settle(500);
  check('walkthrough: opens from "Show the walkthrough again"', await modalOpen());
  const seen = [];
  for (let step = 0; step < 15 && await modalOpen(); step++) {
    const kicker = await evalq(() => (document.querySelector('.tut-kicker') || {}).textContent || '');
    seen.push(kicker);
    // The either-or cards and the language pills: each pressed (English and "not now" last, so those are what stays).
    const cards = await evalq(() => [...document.querySelectorAll('#modalCard .tut-card, #modalCard .tut-langs .p')].map((e, i) => ({ i, text: e.textContent.trim().slice(0, 30), on: e.classList.contains('on'), click: e.getAttribute('onclick') || '' })));
    const order = cards.filter((c) => !/pickTutorialLanguage/.test(c.click) || /'en'/.test(c.click))
      .sort((a, b) => (/Not now|No, home only|tutPickPhone\(false\)|tutPickAnywhere\(false\)|'easy'/.test(a.click + a.text) ? 1 : 0) - (/Not now|No, home only|tutPickPhone\(false\)|tutPickAnywhere\(false\)|'easy'/.test(b.click + b.text) ? 1 : 0));
    for (const c of order) {
      await page.locator('#modalCard .tut-card, #modalCard .tut-langs .p').nth(c.i).click({ timeout: 5000 });
      await settle(300);
      await healthy(`walkthrough ${kicker}: "${c.text}"`);
    }
    // A switch on the step: on and back off.
    const sw = page.locator('#modalCard .tut-switch');
    for (let i = 0; i < await sw.count(); i++) {
      await sw.nth(i).click(); await settle(200);
      await page.locator('#modalCard .tut-switch').nth(i).click(); await settle(200);
      await healthy(`walkthrough ${kicker}: switch ${i + 1} on and off`);
    }
    // Back once, on the second step.
    if (step === 1 && await page.locator('#modalCard .tut-foot button.ghost:not(.tut-skip)').count()) {
      await page.locator('#modalCard .tut-foot button.ghost:not(.tut-skip)').first().click();
      await settle(300);
      await healthy(`walkthrough ${kicker}: Back`);
      await page.click('#tutNext'); await settle(300);
    }
    const next = page.locator('#tutNext');
    if (!(await next.count())) break;
    check(`walkthrough ${kicker}: the main button can be pressed`, !(await next.isDisabled()));
    await next.click();
    await settle(500);
    await healthy(`walkthrough ${kicker}: next`);
  }
  check(`walkthrough: finished and closed (${seen.length} steps)`, !(await modalOpen()));
  const after = await api('/api/config');
  const changedKeys = Object.keys(before.Global).filter((k) => JSON.stringify(before.Global[k]) !== JSON.stringify(after.Global[k]) && k !== 'TutorialDone');
  check('walkthrough: walking it with the defaults changed no setting', changedKeys.length === 0, changedKeys.join(', '));
  await closeModalIfOpen();
}

// ── 4. the console ───────────────────────────────────────────────────────────
await gotoView('console');
for (const line of ['status', 'version', 'help', 'help rot', 'config', 'stats', 'visitors']) {
  const before = await evalq(() => document.getElementById('out').children.length);
  await page.fill('#cmd', line);
  await page.press('#cmd', 'Enter');
  await settle(600);
  if (await modalOpen()) {
    const rows = await evalq(() => document.querySelectorAll('#helpList .c').length);
    check(`console: "${line}" opens the command reference (${rows} commands)`, rows > 0);
    await page.keyboard.press('Escape');
    await settle(200);
  } else {
    const reply = await evalq((n) => [...document.getElementById('out').children].slice(n).map((e) => e.textContent).join('\n'), before);
    // The echo of the line, then the answer under it.
    const answer = reply.replace(`> ${line}`, '').trim();
    check(`console: "${line}" shows its reply`, answer.length > 0, reply.slice(0, 120));
  }
  await healthy(`console: "${line}" left the page healthy`);
}
// The history arrows bring back what was typed.
await page.fill('#cmd', '');
await page.press('#cmd', 'ArrowUp');
check('console: the up arrow brings back the last command', (await page.inputValue('#cmd')) === 'visitors');
await page.fill('#cmd', 'sta');
await page.press('#cmd', 'Tab');
check('console: Tab completes a command', (await page.inputValue('#cmd')).startsWith('sta') && (await page.inputValue('#cmd')).endsWith(' '));
await page.fill('#cmd', '');

// ── 5. every setting: changed, saved, reloaded, checked, put back ────────────
const schema = await api('/api/settings/schema');

async function settingsOn(target) {
  await gotoView('settings');
  await closeModalIfOpen();
  if (target) await page.click(`#settingsNavBots .s[data-bot="${target}"]`); else await page.click('#settingsNavGlobal .s');
  await settle(300);
  if (!(await page.isChecked('#setAdvanced'))) { await page.click('#setAdvanced'); await settle(200); }
  if (await page.isChecked('#setChanged')) { await page.click('#setChanged'); await settle(200); }
  if (await page.inputValue('#setSearch')) { await page.fill('#setSearch', ''); await settle(200); }
}

// The Discord card's second-line pills, in the order they're drawn.
const DISCORD_LINES = [3, 0, 1, 2];
const valuesOf = (cfg, target) => (target ? cfg.Bots[target] : cfg.Global);
const secretsOf = (cfg, target) => (target ? (cfg.BotSecretsSet || {})[target] || [] : cfg.GlobalSecretsSet || []);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// Setting names on screen right now, in order - rows, and the chips of the panels (Notifications, Discord card).
const shownSettings = () => evalq(() => {
  const out = [];
  const add = (n, el) => {
    const h3 = el.closest('.section') && el.closest('.section').querySelector('h3');
    if (n && !out.some((x) => x.name === n)) out.push({ name: n, section: h3 ? h3.dataset.section : '' });
  };
  document.querySelectorAll('#settingsBody [data-setting], #settingsBody .langpick .p[onclick], #settingsBody .dhead input, #settingsBody .dline .p').forEach((el) => {
    if (!window.__e2e.visible(el) && !el.closest('label.switch')) return;
    if (el.dataset.setting) { add(el.dataset.setting, el); return; }
    if (el.closest('.dhead')) { add('DiscordPresence', el); return; }
    if (el.closest('.dline')) { add('DiscordSecondLine', el); return; }
    const m = (el.getAttribute('onclick') || '').match(/editAndRender\('(\w+)'/);
    if (m) add(m[1], el);
  });
  return out;
});
const pendingNow = () => evalq(() => JSON.parse(JSON.stringify(pending)));
const live = (n) => evalq((n) => { const v = liveValue(n, settingsValues()); return v === undefined ? null : JSON.parse(JSON.stringify(v)); }, n);

async function saveNow(target) {
  const want = target ? `/api/bots/${encodeURIComponent(target)}/config` : '/api/config';
  const wait = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === want, { timeout: 10000 });
  await page.click('#saveBtn');
  const res = await wait;
  const body = await res.json().catch(() => ({}));
  await page.waitForFunction(() => document.getElementById('saveBtn').disabled, null, { timeout: 10000 }).catch(() => {});
  await settle(200);
  return body;
}

// The field a setting lives in, found fresh each time (the form is redrawn after every edit).
const field = (n) => page.locator(`#settingsBody .field:has([data-setting="${n}"])`);
const boxOf = (n) => page.locator(`#settingsBody [data-setting="${n}"]`).first();

async function guardedOff() {
  // "Type off to confirm" - the one setting that asks before it goes off.
  if (!(await modalOpen())) return false;
  if (!(await page.locator('#guardConfirm').count())) return false;
  check('settings: the guarded switch asks first, and its button waits for "off"', await page.isDisabled('#guardGo'));
  await page.fill('#guardConfirm', 'off');
  await page.click('#guardGo');
  await settle(200);
  return true;
}

function nextInt(def, cur) {
  const lo = def.Min > -1e300 ? def.Min : -1e9;
  const hi = def.Max < 1e300 ? def.Max : 1e9;
  const step = def.Kind === 'Float' ? 0.5 : 1;
  // The longest of a pair goes up and the shortest down, so a pair never crosses ("Minutes" is not a "Min").
  const up = /Max(?!imum)/.test(def.Name) && !/Min(?!ute)/.test(def.Name);
  let v = up ? cur + step : cur - step;
  if (v > hi || v < lo) v = up ? cur - step : cur + step;
  return Math.max(lo, Math.min(hi, v));
}

// Change one setting through its own control. Returns what the page now holds for it, or a reason it couldn't.
async function change(def, target) {
  const n = def.Name;
  const cur = await live(n);
  const kind = def.Kind;
  const box = boxOf(n);
  const tag = (await box.count()) ? await box.evaluate((e) => e.tagName.toLowerCase() + ':' + (e.type || '') + ':' + e.className) : '';

  if (n === 'DiscordSecondLine') {
    const pills = page.locator('#settingsBody .dline .p');
    const on = await pills.evaluateAll((els) => els.findIndex((e) => e.classList.contains('on')));
    await pills.nth(on === 1 ? 2 : 1).click();
  } else if (n === 'GameWeights') {
    await weightsScenario();
  } else if (!tag) {
    // A chip on a panel (what gets sent to Discord/Telegram, the parts of the Discord card).
    const pill = page.locator(`#settingsBody .langpick .p[onclick*="editAndRender('${n}'"]`).first();
    if (!(await pill.count())) return { why: 'no control for it on screen' };
    await pill.click();
  } else if (kind === 'Bool') {
    await field(n).locator('label.switch').first().click();
    await settle(150);
    await guardedOff();
  } else if (kind === 'Int' || kind === 'Float') {
    await box.fill(String(nextInt(def, Number(cur) || 0)));
    await box.blur();
  } else if (kind === 'Hour') {
    // A list of hours: the next one along, so a from/until pair never crosses.
    await box.selectOption(String(nextInt(def, Number(cur) || 0)));
  } else if (tag.startsWith('div') && /pills/.test(tag)) {
    // Choice pills, or the accounts shown on the Discord card.
    const want = CHOICE_VALUE[n];
    if (want !== undefined && Number(cur) !== want) await box.locator(`.p[onclick*="editAndRender('${n}',${want})"]`).first().click();
    else await box.locator('.p:not(.on)').first().click();
  } else if (tag.startsWith('div') && /dbtnpick/.test(tag)) {
    await box.locator('select').selectOption('custom');
    await settle(150);
    await page.fill(`#f-${n}-label`, 'E2E');
    await page.fill(`#f-${n}-link`, 'https://example.com/e2e');
  } else if (tag.startsWith('select')) {
    const opts = await box.evaluate((e) => [...e.options].map((o) => o.value));
    const now = await box.inputValue();
    const other = opts.find((o) => o !== now && o !== '');
    if (other === undefined) return { why: 'only one choice' };
    await box.selectOption(other);
  } else if (kind === 'Secret') {
    if (!SECRET_VALUE[n]) return { why: 'a secret with an outside use - left alone' };
    if (await box.isDisabled()) return { why: 'already stored (locked)' };
    await box.fill(SECRET_VALUE[n]);
  } else if (kind === 'AppIds') {
    const list = Array.isArray(cur) ? cur : [];
    const id = [570, 550, 500, 4000].find((x) => !list.includes(x));
    const input = box.locator('.appin');
    await input.fill(String(id));
    await input.press('Enter');
  } else {
    await box.fill(TEXT_VALUE[n] || 'e2e');
  }
  await settle(150);
  const p = await pendingNow();
  if (!(n in p)) return { why: 'the control changed nothing', fail: true };
  return { pending: p };
}

// The weights editor: add a game, move a share, promote one, drop one, swap the main game.
async function weightsScenario() {
  const games = async () => String((await live('GameWeights')) || '').split(/[,;]/).map((s) => parseInt(s)).filter(Boolean);
  const unused = async () => { const g = await games(); return [570, 550, 500, 4000, 440, 730].find((x) => !g.includes(x)); };
  const rows = () => page.locator('#settingsBody .weights .wrow:not(.whunt)').count();
  const action = (sign) => page.locator('#settingsBody .weights .wact b').filter({ hasText: sign });
  const step = async (what, fn) => {
    const before = await live('GameWeights');
    await fn();
    await settle(200);
    const after = (await pendingNow()).GameWeights;
    check(`settings weights: ${what}`, after !== undefined && after !== before, `${before} -> ${after}`);
  };
  const add = async () => { const input = page.locator('#settingsBody .weights .wadd .appin'); await input.fill(String(await unused())); await input.press('Enter'); };
  await step('adding a game', add);
  while (await rows() < 3) await step('adding another game', add);
  await step("changing a side game's share", async () => { const box = page.locator('#settingsBody .weights .wpct').nth(1); await box.fill('20'); await box.blur(); });
  await step('making a side game the main one', () => action('↑').first().click());
  await step('dropping a game', () => action('×').last().click());
  await step('swapping the main game', async () => {
    await action('⇄').first().click();
    await settle(200);
    await page.locator('#settingsBody .wmainedit').fill(String(await unused()));
    await page.locator('#settingsBody .wmainedit').press('Enter');
  });
}

// Put one setting back to the value it had, through the same controls (or its "default" link, when that's the value).
async function putBack(def, orig, target, defaults) {
  const n = def.Name;
  const cur = await live(n);
  if (same(cur, orig) && def.Kind !== 'Secret') return true;
  const box = boxOf(n);
  const tag = (await box.count()) ? await box.evaluate((e) => e.tagName.toLowerCase() + ':' + (e.type || '') + ':' + e.className) : '';
  const revert = field(n).locator('.revert');

  if (def.Kind === 'Secret') {
    dialogAnswer = 'accept';
    try {
      const btn = field(n).locator('button[onclick^="clearSecret"]');
      if (await btn.count()) await btn.click(); else return false;
    } finally { dialogAnswer = 'dismiss'; }
  } else if (n === 'DiscordSecondLine') {
    // The pills in their order on screen: people using nocat.farm (3), then cards today, hours past week, past month.
    await page.locator('#settingsBody .dline .p').nth(DISCORD_LINES.indexOf(Number(orig))).click();
  } else if (n === 'GameWeights') {
    // Built back through the editor: every side game dropped, the main game swapped back, the others added, the share set.
    const want = String(orig || '').split(',').map((s) => s.trim()).filter(Boolean).map((s) => s.split(':').map(Number));
    if (!want.length) { if (await revert.count()) await revert.click(); } else {
      // Side games only: the main game has a remove button too now, and taking it off left nothing to swap back.
      const gameRows = page.locator('#settingsBody .weights .wrow:not(.whunt)');
      for (let i = 0; i < 6 && await gameRows.count() > 1; i++) {
        await page.locator('#settingsBody .weights .wact b').filter({ hasText: '×' }).last().click(); await settle(150);
      }
      await page.locator('#settingsBody .weights .wact b').filter({ hasText: '⇄' }).first().click(); await settle(150);
      await page.locator('#settingsBody .wmainedit').fill(String(want[0][0])); await page.locator('#settingsBody .wmainedit').press('Enter'); await settle(150);
      for (const [g] of want.slice(1)) { await page.locator('#settingsBody .weights .wadd .appin').fill(String(g)); await page.locator('#settingsBody .weights .wadd .appin').press('Enter'); await settle(150); }
      const main = page.locator('#settingsBody .weights .wpct').first();
      await main.fill(String(want[0][1])); await main.blur();
    }
  } else if (same(orig, defaults[n]) && await revert.count()) {
    await revert.first().click();
  } else if (!tag) {
    const pill = page.locator(`#settingsBody .langpick .p[onclick*="editAndRender('${n}'"]`).first();
    if (!(await pill.count())) return false;
    await pill.click();
  } else if (def.Kind === 'Bool') {
    await field(n).locator('label.switch').first().click();
    await settle(150);
    await guardedOff();
  } else if (def.Kind === 'Int' || def.Kind === 'Float') {
    await box.fill(String(orig)); await box.blur();
  } else if (def.Kind === 'Hour') {
    await box.selectOption(String(orig));
  } else if (tag.startsWith('div') && /dbtnpick/.test(tag)) {
    const bots = await evalq(() => state.Bots.map((b) => b.Name.toLowerCase()));
    const v = String(orig || '');
    await box.locator('select').selectOption(!v ? '' : v.toLowerCase() === 'github' ? 'github' : bots.includes(v.toLowerCase()) ? v : 'custom');
  } else if (tag.startsWith('div') && /pills/.test(tag)) {
    if (n === 'DiscordPresenceAccounts') {
      if (!orig) await box.locator('.p').first().click(); else return false;
    } else {
      await box.locator(`.p[onclick*="editAndRender('${n}',${orig})"]`).first().click();
    }
  } else if (tag.startsWith('select')) {
    await box.selectOption(String(orig));
  } else if (def.Kind === 'AppIds') {
    const want = orig || [];
    for (let guard = 0; guard < 8; guard++) {
      const now = (await live(n)) || [];
      const extra = now.findIndex((x) => !want.includes(x));
      if (extra < 0) break;
      await box.locator('.tag b[role=button]').nth(extra).click(); await settle(150);
    }
    for (const id of want) {
      if (((await live(n)) || []).includes(id)) continue;
      await box.locator('.appin').fill(String(id)); await box.locator('.appin').press('Enter'); await settle(150);
    }
  } else {
    await box.fill(orig == null ? '' : String(orig));
  }
  await settle(150);
  return true;
}

async function settingsRound(target) {
  const who = target || 'global';
  const defs = target ? schema.Bot : schema.Global;
  const defaults = target ? schema.BotDefaults : schema.GlobalDefaults;
  await settingsOn(target);
  const original = await api('/api/config');
  const orig = valuesOf(original, target);
  const origSecrets = secretsOf(original, target).slice().sort();
  const doneNames = new Set();
  const changed = [];   // in the order they were changed
  let lastSaved = null;

  // A section at a time: every setting in it changed, then one save for the lot. A mode switch goes on its own.
  for (let guard = 0; guard < 200; guard++) {
    let shown = await shownSettings();
    const first = shown.find((x) => !doneNames.has(x.name) && !MODE_SWITCHES.includes(x.name)) || shown.find((x) => !doneNames.has(x.name));
    if (!first) break;
    const mode = MODE_SWITCHES.includes(first.name);
    const batch = [];   // [{ def, before }]
    for (let inner = 0; inner < 80; inner++) {
      const x = inner === 0 ? first
        : mode ? null : shown.find((y) => y.section === first.section && !doneNames.has(y.name) && !MODE_SWITCHES.includes(y.name));
      if (!x) break;
      const n = x.name;
      doneNames.add(n);
      const def = defs.find((d) => d.Name === n);
      if (!def) { note(`${who}: ${n} is on screen but not in the schema`); continue; }
      if (SKIP_SETTING[n]) {
        skip(`settings ${who}: ${n}`, SKIP_SETTING[n]);
        // A stored secret it leaves alone still has its Clear button pressed - and the "no undo" question answered no.
        const clear = field(n).locator('button[onclick^="clearSecret"]');
        if (def && def.Kind === 'Secret' && await clear.count()) {
          const asked = dialogs.length;
          await clear.click();
          await settle(150);
          check(`settings ${who}: ${n} Clear asks first, and "no" keeps it`, dialogs.length === asked + 1 && !(n in await pendingNow()));
        }
        shown = await shownSettings();
        continue;
      }
      const kept = await pendingNow();
      const before = await live(n);
      let r;
      try { r = await change(def, target); } catch (e) { r = { why: String(e.message || e).split('\n')[0], fail: true }; }
      if (r.why) {
        if (r.fail) check(`settings ${who}: ${n} can be changed`, false, r.why); else skip(`settings ${who}: ${n}`, r.why);
        // Back to the edits the batch had before this one.
        await evalq((kept) => { pending = kept; changingMain = false; closeModal(); renderSettings(); }, kept);
      } else {
        batch.push({ def, before });
      }
      shown = await shownSettings();
    }
    if (!batch.length) continue;
    const edits = await pendingNow();
    const res = await saveNow(target);
    const saved = await api('/api/config');
    const vals = valuesOf(saved, target);
    if (res && res.Adjusted && res.Adjusted.length) note(`${who}: the save adjusted: ${res.Adjusted.join(' · ')}`);
    for (const { def, before } of batch) {
      const n = def.Name;
      // What this setting's control put in.
      const keys = Object.keys(edits).filter((k) => k === n);
      const wrong = keys.filter((k) => {
        const d = defs.find((y) => y.Name === k);
        if (d && d.Kind === 'Secret') return !secretsOf(saved, target).includes(k);
        return !same(vals[k], edits[k]);
      }).map((k) => `${k}: wanted ${JSON.stringify(edits[k])}, saved ${JSON.stringify(vals[k])}`);
      const shownValue = def.Kind === 'Secret' ? '(a secret)' : JSON.stringify(edits[n] ?? vals[n]);
      check(`settings ${who}: ${n} ${def.Kind === 'Secret' ? '' : JSON.stringify(before) + ' '}-> ${shownValue} saved`, res && res.ok && !wrong.length, wrong.join('; ') || (res && res.error));
      changed.push({ def, orig: orig[n] });
    }
    lastSaved = saved;
    await healthy(`settings ${who}: saving ${batch.length} change(s) in "${first.section}" left the page healthy`);
  }

  // Reloaded: still there.
  await page.reload();
  await page.waitForSelector('#app:not(.hidden)', { timeout: 15000 });
  await settle(500);
  await settingsOn(target);
  const reread = await api('/api/config');
  if (lastSaved) {
    const diff = defs.filter((d) => d.Kind !== 'Secret' && !same(valuesOf(reread, target)[d.Name], valuesOf(lastSaved, target)[d.Name])).map((d) => d.Name);
    check(`settings ${who}: after a reload /api/config still holds all ${changed.length} changes`, diff.length === 0, diff.join(', '));
  }

  // Put back: the last changed first; a mode switch saved on its own, with a save before it too. So is a setting another
  // one shows under (ShowWhen, like "When to farm cards" for the card-sittings share - "X=1", or "X!=0"): changing its
  // answer back hides the other one, and an unsaved edit to a setting that gets hidden is dropped - so what was put back
  // is saved first.
  const controllers = new Set(defs.filter((d) => d.ShowWhen).map((d) => d.ShowWhen.split('=')[0].replace(/!$/, '')));
  const segments = [];
  let cur = [];
  for (const c of changed.slice().reverse()) {
    if (MODE_SWITCHES.includes(c.def.Name) || controllers.has(c.def.Name)) { if (cur.length) segments.push(cur); segments.push([c]); cur = []; } else cur.push(c);
  }
  if (cur.length) segments.push(cur);
  for (const seg of segments) {
    for (const c of seg) {
      let ok = false;
      try { ok = await putBack(c.def, c.orig, target, defaults); } catch (e) { note(`${who}: putting ${c.def.Name} back: ${String(e.message || e).split('\n')[0]}`); }
      if (!ok) note(`${who}: ${c.def.Name} couldn't be put back through its control`);
    }
    if (Object.keys(await pendingNow()).length) {
      const res = await saveNow(target);
      if (!res || !res.ok) check(`settings ${who}: saving the put-back values`, false, res && res.error);
    }
  }
  let after = await api('/api/config');
  // Human mode switched on fills the game list in from the games it idled. Emptied again with the page's own remove
  // buttons - the main game has one now - and saved like any other change.
  if (target && orig.GameWeights === '' && valuesOf(after, target).GameWeights !== '') {
    const remove = page.locator('#settingsBody .weights .wact b').filter({ hasText: '×' });
    const shown = await remove.count() > 0;
    for (let i = 0; i < 12 && await remove.count(); i++) { await remove.last().click(); await settle(150); }
    if (Object.keys(await pendingNow()).length) await saveNow(target);
    after = await api('/api/config');
    if (shown) {
      check(`settings ${who}: the game list empties with its own remove buttons`, valuesOf(after, target).GameWeights === '', valuesOf(after, target).GameWeights);
    } else {
      // Human mode is off again by now, and the list isn't drawn without it: back through the page's own save.
      note(`${who}: GameWeights put back through the page's own save - the list isn't shown with human mode off`);
      await evalq(async (name) => { await loadConfig(); await postBot(name, { ...config.Bots[name], GameWeights: '' }); await loadConfig(); renderSettings(); }, target);
      after = await api('/api/config');
    }
  }
  const left = defs.filter((d) => d.Kind !== 'Secret' && !same(valuesOf(after, target)[d.Name], orig[d.Name]))
    .map((d) => `${d.Name}: ${JSON.stringify(valuesOf(after, target)[d.Name])} (was ${JSON.stringify(orig[d.Name])})`);
  const secretsNow = secretsOf(after, target).slice().sort();
  if (!same(secretsNow, origSecrets)) left.push(`stored secrets ${secretsNow.join(',')} (were ${origSecrets.join(',')})`);
  check(`settings ${who}: all ${changed.length} changed settings are back as they were`, left.length === 0, left.join('; '));
  await healthy(`settings ${who}: healthy after putting everything back`);
  return changed.length;
}

settingsCount.global = await settingsRound(null);
for (const bot of Object.keys((await api('/api/config')).Bots)) settingsCount[bot] = await settingsRound(bot);

// A game list searched by name: the test accounts never sign in, so their libraries are empty - typing a name says so
// and adds nothing, and the box keeps what was typed and its focus (only the little list under it is drawn).
{
  await settingsOn('robot');
  const box = page.locator('#settingsBody [data-setting="IdleGames"] .appin');
  const before = await live('IdleGames');
  await box.click();
  await box.type('portal', { delay: 20 });
  await page.waitForSelector('#settingsBody [data-setting="IdleGames"] .appsug', { timeout: 5000 }).catch(() => {});
  const sug = page.locator('#settingsBody [data-setting="IdleGames"] .appsug');
  check('game lists: typing a name drops a list down under the box, and with no library read it says so',
    (await sug.count()) === 1 && (await sug.locator('.none').count()) === 1);
  check('game lists: the box keeps its text and focus while it searches',
    (await box.inputValue()) === 'portal' && await box.evaluate((e) => document.activeElement === e));
  await box.press('Escape');
  check('game lists: Escape closes the list', (await sug.count()) === 0);
  await box.press('Enter');
  check('game lists: Enter on a name nothing matches adds nothing', JSON.stringify(await live('IdleGames')) === JSON.stringify(before));
  await box.fill('');
  await evalq(() => { pending = {}; renderSettings(); });
  await healthy('game lists: healthy after searching');
}

// "Games and how often" searches the library like every other game list: its add box and the main game's swap box. The
// test accounts' libraries are empty, so one is handed to the page first (the human account's, the one with the list).
{
  await settingsOn('human');
  const library = [{ id: 400, name: 'Portal' }, { id: 620, name: 'Portal 2' }, { id: 220, name: 'Half-Life 2' }];
  await evalq((games) => {
    libCache.set('human', { games: games.map((g) => ({ ...g, low: g.name.toLowerCase(), mins: 90 })), at: Date.now() });
    pending = {}; renderSettings();
  }, library);
  await settle(200);
  const weights = page.locator('#settingsBody .weights');
  const ids = async () => String((await live('GameWeights')) || '').split(',').map((x) => parseInt(x)).filter(Boolean);
  const before = await ids();
  const add = weights.locator('.wadd .appin');
  check('weights picker: the add box is a library search', (await add.count()) === 1
    && /search/i.test(await add.getAttribute('placeholder') || ''));
  await add.click();
  await add.type('port', { delay: 20 });
  await page.waitForSelector('#settingsBody .weights .wadd .appsug .s', { timeout: 5000 }).catch(() => {});
  const hits = await weights.locator('.wadd .appsug .s').allTextContents();
  check('weights picker: typing part of a name lists the library\'s games', hits.length === 2 && hits[0].startsWith('Portal'), hits.join(' | '));
  check('weights picker: the box keeps its text and focus while it searches',
    (await add.inputValue()) === 'port' && await add.evaluate((e) => document.activeElement === e));
  await add.press('ArrowDown');
  await add.press('Enter');
  await settle(200);
  const added = await ids();
  check('weights picker: Enter takes the picked game, added at the end with a share', added.length === before.length + 1 && added[added.length - 1] === 620
    && added[0] === before[0], `${before} -> ${added}`);
  check('weights picker: back in the box for the next one', await weights.locator('.wadd .appin').evaluate((e) => document.activeElement === e));
  check('weights picker: a game already listed isn\'t offered again', await (async () => {
    await weights.locator('.wadd .appin').type('portal', { delay: 20 });
    await page.waitForSelector('#settingsBody .weights .wadd .appsug', { timeout: 5000 }).catch(() => {});
    const offered = await weights.locator('.wadd .appsug .s').evaluateAll((els) => els.map((e) => Number(e.dataset.app)));
    await weights.locator('.wadd .appin').press('Escape');
    await weights.locator('.wadd .appin').fill('');
    return JSON.stringify(offered) === '[400]';
  })());
  // An appID or a store link typed in still works.
  await weights.locator('.wadd .appin').fill('https://store.steampowered.com/app/4000/Garrys_Mod/');
  await weights.locator('.wadd .appin').press('Enter');
  await settle(200);
  check('weights picker: a pasted store link still goes in', (await ids()).includes(4000), String(await ids()));

  // The main game swapped from the library: it keeps the main game's share.
  const share = String((await live('GameWeights')) || '').split(',')[0].split(':')[1];
  await weights.locator('.wact b').filter({ hasText: '⇄' }).first().click();
  await settle(200);
  const swap = page.locator('#settingsBody .weights .wmainedit');
  check('weights picker: the main game\'s swap box is a library search too', (await swap.count()) === 1 && await swap.evaluate((e) => document.activeElement === e));
  await swap.type('half', { delay: 20 });
  await page.waitForSelector('#settingsBody .weights .wname .appsug .s', { timeout: 5000 }).catch(() => {});
  await page.locator('#settingsBody .weights .wname .appsug .s').first().click();
  await settle(200);
  const swapped = String((await live('GameWeights')) || '').split(',')[0].trim();
  check('weights picker: a game picked by name becomes the main one, with the main game\'s share', swapped === `220:${share}`, swapped);
  // Escape (nothing dropped down) leaves the main game as it is.
  await weights.locator('.wact b').filter({ hasText: '⇄' }).first().click();
  await settle(200);
  await page.locator('#settingsBody .weights .wmainedit').press('Escape');
  await settle(200);
  check('weights picker: Escape in the swap box changes nothing', (await page.locator('#settingsBody .weights .wmainedit').count()) === 0
    && String((await live('GameWeights')) || '').split(',')[0].trim() === swapped);
  await evalq(() => { pending = {}; libCache.delete('human'); renderSettings(); });
  await healthy('weights picker: healthy after searching');
}

// The user count: the copy pinged the stand-in for nocat.lol (never the real one), the Overview says how many people use
// nocat.farm, the Discord card's preview counts them, and "Count me as a user" off hides it all.
if (PING_STUB) {
  let users = null;
  for (let i = 0; i < 60 && users !== 212; i++) { users = (await api('/api/status')).Users; if (users !== 212) await sleep(1000); }
  check('user count: the status says 212 people, from the stand-in\'s answer', users === 212, String(users));
  const seen = await fetch(`${PING_STUB}/seen`).then((r) => r.json()).catch(() => []);
  const first = seen[0] || {};
  let body = {};
  try { body = JSON.parse(first.body || '{}'); } catch { /* checked below */ }
  check('user count: the ping is a JSON POST of the install id, the version and the platform - nothing else',
    seen.length >= 1 && JSON.stringify(Object.keys(body)) === '["id","v","os"]' && /^[0-9a-f]{32}$/.test(body.id || '')
      && ['windows', 'linux', 'mac', 'docker'].includes(body.os) && /^application\/json/.test(first.contentType || '')
      && (first.userAgent || '') === `nocat.farm/${body.v}`, JSON.stringify(first));
  await gotoView('overview');
  await settle(300);
  const line = page.locator('#usercount');
  check('user count: the Overview says "212 people using nocat.farm today"', await line.isVisible() && (await line.textContent()) === '212 people using nocat.farm today',
    await line.textContent());
  await settingsOn(null);
  check('user count: the Discord card\'s preview counts the people', (await page.locator('#settingsBody .dcard .dstate').textContent() || '').includes('212 people using nocat.farm'),
    await page.locator('#settingsBody .dcard .dstate').textContent());
  await field('CountMeAsUser').locator('label.switch').first().click();
  await settle(150);
  await saveNow(null);
  check('user count: Count me as a user off - the status has no count', (await api('/api/status')).Users == null);
  check('user count: ...and the Discord card falls back to the cards today', /cards today/.test(await page.locator('#settingsBody .dcard .dstate').textContent() || ''),
    await page.locator('#settingsBody .dcard .dstate').textContent());
  await gotoView('overview');
  await settle(300);
  check('user count: ...and the Overview hides the line', !(await line.isVisible()));
  await settingsOn(null);
  await field('CountMeAsUser').locator('label.switch').first().click();
  await settle(150);
  await saveNow(null);
  check('user count: switched back on, the count is back', (await api('/api/status')).Users === 212);
  await healthy('user count: healthy after switching it off and on');
} else {
  skip('user count', 'no --ping-stub: the copy would tell nocat.lol it is running');
}

// The Discord card's second line: people using nocat.farm first (the default), no account names any more.
{
  await settingsOn(null);
  const pills = await page.locator('#settingsBody .dline .p').allTextContents();
  check('discord card: the second line is people using nocat.farm, cards today, hours past week or month - no account names',
    pills.length === 4 && pills[0] === 'People using nocat.farm' && !pills.some((x) => /account names/i.test(x)), pills.join(' | '));
  check('discord card: people using nocat.farm is picked by default', Number(await live('DiscordSecondLine')) === 3
    && await page.locator('#settingsBody .dline .p').first().evaluate((e) => e.classList.contains('on')));
}

// ── 6. the keyboard ──────────────────────────────────────────────────────────
await page.reload();
await page.waitForSelector('#app:not(.hidden)', { timeout: 15000 });
await settle(500);
for (const v of views) {
  await gotoView(v);
  await closeModalIfOpen();
  await evalq(() => { window.__e2e.focused.length = 0; document.activeElement && document.activeElement.blur(); window.scrollTo(0, 0); });
  const want = await evalq((v) => [...document.querySelectorAll(`#view-${v} [role=button], header.rail [role=button]`)]
    .filter((e) => window.__e2e.visible(e)).map((e) => window.__e2e.keyOf(e, true)), v);
  const reached = new Set();
  for (let i = 0; i < 1500; i++) {
    await page.keyboard.press('Tab');
    if (i % 40 === 39 || i === 1499) {
      const got = await evalq(() => window.__e2e.focused.splice(0).filter((e) => e.getAttribute && e.getAttribute('role') === 'button').map((e) => window.__e2e.keyOf(e, true)));
      got.forEach((k) => reached.add(k));
      if (want.every((k) => reached.has(k))) break;
    }
    if (await modalOpen()) await closeModalIfOpen();
  }
  const missed = want.filter((k) => !reached.has(k));
  check(`keyboard ${v}: Tab reaches all ${want.length} role=button controls`, missed.length === 0, missed.slice(0, 5).map((k) => k.split('|').pop()).join(', '));
}

// Enter and Space press a role=button, the way they press a real button.
async function pressWith(sel, key, what, expect) {
  const el = page.locator(sel).first();
  if (!(await el.count()) || !(await el.isVisible())) { note(`keyboard: ${what} not on screen`); return; }
  await el.focus();
  await evalq(() => { window.__e2e.clicked.length = 0; });
  await page.keyboard.press(key);
  await settle(300);
  const hit = await evalq(() => window.__e2e.clicked.length);
  // A tab has a key handler of its own (it switches the page without a click); anything else is pressed as a click.
  const ok = expect ? await expect() : hit > 0;
  check(`keyboard: ${key === ' ' ? 'Space' : key} presses ${what}`, ok);
  await closeModalIfOpen();
}
// The command box completes a command with Tab - but an empty box has nothing to complete, and Tab must move on
// from it (and Shift+Tab back), or a keyboard is stuck in it for good.
await gotoView('console');
for (const [key, what] of [['Tab', 'Tab'], ['Shift+Tab', 'Shift+Tab']]) {
  await page.fill('#cmd', '');
  await page.focus('#cmd');
  await page.keyboard.press(key);
  check(`keyboard console: ${what} leaves the empty command box`, await evalq(() => document.activeElement !== document.getElementById('cmd')));
}
await page.fill('#cmd', 'sta');
await page.focus('#cmd');
await page.keyboard.press('Tab');
check('keyboard console: Tab in a half-typed command still completes it, and stays in the box',
  /^sta\w+ $/.test(await page.inputValue('#cmd')) && await evalq(() => document.activeElement === document.getElementById('cmd')));
await page.fill('#cmd', '');
await gotoView('overview');
await pressWith('.navitem[data-view="log"]', 'Enter', 'the Log tab', async () => (await currentView()) === 'log');
await pressWith('.navitem[data-view="console"]', ' ', 'the Console tab', async () => (await currentView()) === 'console');
await gotoView('log');
await pressWith('#logLevels .chip', 'Enter', 'a log level chip');
await pressWith('#logLevels .chip', ' ', 'a log level chip again');
await gotoView('console');
await pressWith('#cmdList .c', ' ', 'a command in the list', async () => (await page.inputValue('#cmd')).length > 0);
await gotoView('accounts');
await pressWith('#acctFilters .chip', 'Enter', 'an account filter chip');
await pressWith('#acctFilters .chip.on', ' ', 'the same filter chip, clearing it');
await gotoView('settings');
await pressWith('#settingsNavJump .j', 'Enter', 'a Jump to link');
await pressWith('#settingsNavGlobal .s', ' ', 'Global settings in the list');
await gotoView('overview');
await pressWith('#histBar .p:not(.on)', 'Enter', 'a history range');
await pressWith('#railChips .chip', 'Enter', 'a status chip in the rail', async () => (await currentView()) === 'accounts');
await healthy('keyboard: healthy after all of that');

// Every (i) explains itself on hover.
await gotoView('settings');
{
  const icons = page.locator('#settingsBody i.info');
  const n = Math.min(6, await icons.count());
  let shown = 0;
  for (let i = 0; i < n; i++) {
    await icons.nth(i).scrollIntoViewIfNeeded();
    await icons.nth(i).hover();
    await sleep(120);
    if (await evalq(() => !document.getElementById('tip').classList.contains('hidden') && document.getElementById('tip').textContent.trim().length > 0)) shown++;
  }
  check(`tooltips: hovering an (i) shows its explanation (${shown} of ${n})`, n > 0 && shown === n);
  await page.mouse.move(0, 0);
}

// ── 7. layout: nothing wider than the screen ─────────────────────────────────
for (const width of [1280, 375]) {
  await page.setViewportSize({ width, height: width < 500 ? 812 : 900 });
  await settle(300);
  for (const v of views) {
    await gotoView(v);
    await closeModalIfOpen();
    await settle(300);
    const o = await evalq(() => ({ doc: document.documentElement.scrollWidth, body: document.body.scrollWidth, win: window.innerWidth,
      wide: [...document.querySelectorAll('#app *')].filter((e) => window.__e2e.visible(e) && e.getBoundingClientRect().right > window.innerWidth + 1
        && !e.closest('nav, .tablewrap, .out, pre, .ph-visits, .auth-accounts')).slice(0, 3).map((e) => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + '.' + String(e.className).split(' ')[0]) }));
    check(`layout ${width}px ${v}: no sideways scrolling`, Math.max(o.doc, o.body) <= o.win + 1, `page is ${Math.max(o.doc, o.body)}px wide in ${o.win}px; ${o.wide.join(', ')}`);
  }
  // A pop-up at this width too.
  await gotoView('accounts');
  await evalq(() => showAddAccount());
  await settle(300);
  const m = await evalq(() => { const r = document.getElementById('modalCard').getBoundingClientRect(); return { left: r.left, right: r.right, win: window.innerWidth }; });
  check(`layout ${width}px: a pop-up fits the screen`, m.left >= 0 && m.right <= m.win + 1, JSON.stringify(m));
  await closeModalIfOpen();
}
await page.setViewportSize({ width: 1280, height: 900 });
} catch (e) {
  check('the test ran to the end', false, String((e && e.stack) || e).split(/\r?\n/).slice(0, 3).join(' | '));
}

// ── done ─────────────────────────────────────────────────────────────────────
await healthy('the page is healthy at the end').catch((e) => check('the page is healthy at the end', false, String(e.message || e)));
note(`controls used: ${Object.entries(covered).map(([k, n]) => `${k} ${n}`).join(', ')}`);
note(`settings changed and put back: ${Object.entries(settingsCount).map(([k, n]) => `${k} ${n}`).join(', ')}`);
note(`confirm() dialogs answered: ${dialogs.length}`);
if (blocked.size) note(`outside addresses the page tried and was refused: ${[...blocked].join(', ')}`);
note(`skipped on purpose: ${skipped.length}`);
await browser.close();
fs.rmSync(OUT, { recursive: true, force: true });

if (fails > 0) { console.log(`${fails} failed`); process.exit(1); }
console.log(`all passed (${passes} checks)`);
