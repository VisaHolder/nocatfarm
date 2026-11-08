// The dashboard hammered, for tools/release-tests/stress.ps1:  node stress-hammer.mjs <base url> <seconds> [workers]
// A few workers (4 by default - the PC is someone's own) each firing request after request at every route the test copy
// has: reads, commands, settings saved against each other, a plugin's page and commands, malformed JSON, bodies far too big,
// and clients that open a connection and drip the request out a byte at a time. Nothing here reaches the internet: only
// commands that answer from the copy itself, no game names looked up on Steam (/api/appnames), and no add-on is ever
// installed (the list is never asked for fresh - GitHub is asked once at most, then remembered, answer or not).
// Prints one JSON line at the end: how many requests, how many answered with each status, how many failed outright.
import net from 'node:net';

const [base, secondsArg, workersArg] = process.argv.slice(2);
const seconds = Number(secondsArg || 60);
const workers = Number(workersArg || 4);
const until = Date.now() + seconds * 1000;
const url = new URL(base);
const stats = { requests: 0, status: {}, failed: 0, errors: {}, slowClients: 0, slowHeld: 0, slowest: [] };

// The slowest requests, by route - what to look at when a burst gets few answers.
function timed(method, path, ms) {
  stats.slowest.push({ ms: Math.round(ms), route: `${method} ${path.slice(0, 80)}` });
  stats.slowest.sort((a, b) => b.ms - a.ms);
  stats.slowest.length = Math.min(stats.slowest.length, 5);
}

const accounts = ['human', 'robot', 'authbot'];
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const junk = (n) => Array.from({ length: n }, () => String.fromCharCode(Math.floor(Math.random() * 0xd7ff))).join('');

// Only commands that answer from the copy itself - never update, exit, restart, addons install, steam open, redeem.
const commands = ['status', 'help', 'plugins', 'version', 'stats', 'report', 'log 50', 'human', 'settings', 'stress a b c',
  'stress-huge', 'stress-throw', 'stress-void', 'stress-block', 'stress7', 'status all', 'status nobody', '', '   ',
  'pause nobody', 'notacommand', 'stauts', 'set Theme dark', 'set Theme light', 'set WebRefreshSeconds 5',
  'help stress', 'addons', '\u0000\u0001\u0002', 'x'.repeat(20000), junk(500)];

const routes = [
  () => ['GET', '/api/status'],
  () => ['GET', '/api/ping'],
  () => ['GET', '/api/log?n=500'],
  () => ['GET', `/api/log?since=${Math.floor(Math.random() * 1e6)}`],
  () => ['GET', '/api/history?days=400'],
  () => ['GET', '/api/commands'],
  () => ['GET', '/api/settings/schema'],
  () => ['GET', '/api/config'],
  () => ['GET', '/api/plugins'],
  () => ['GET', `/api/plugins/page?plugin=stress-flood&view=${pick(['', 'throw', 'block', 'null', junk(5)])}`],
  () => ['GET', `/api/plugins/page?plugin=${encodeURIComponent(junk(20))}`],
  () => ['GET', '/api/addons'],
  () => ['GET', `/api/bots/${pick(accounts)}/library`],
  () => ['GET', `/api/bots/${pick(accounts)}/cards`],
  () => ['GET', `/api/bots/${pick(accounts)}/achievements`],
  () => ['GET', `/api/bots/${encodeURIComponent(junk(8))}/library`],
  () => ['GET', '/api/auth'],
  () => ['GET', '/api/backups/status'],
  () => ['POST', '/api/command', { line: pick(commands) }],
  () => ['POST', '/api/command', { line: pick(commands) }],
  () => ['POST', '/api/command/history', { lines: Array.from({ length: 200 }, () => pick(commands)) }],
  () => ['POST', '/api/config', { Theme: pick(['dark', 'light']) }],
  () => ['POST', '/api/config', { WebRefreshSeconds: 1 + Math.floor(Math.random() * 30) }],
  () => ['POST', '/api/config', { Theme: 7, WebRefreshSeconds: 'lots', NoSuchSetting: [1, 2, 3] }],
  () => ['POST', `/api/bots/${pick(accounts)}/config`, { Notes: `stress ${Math.random()}` }],
  () => ['POST', `/api/bots/${pick(accounts)}/config`, { Notes: junk(2000), IdleGames: 'not a list' }],
  () => ['POST', '/api/plugins/setting', { plugin: 'stress-flood', name: `Setting${Math.floor(Math.random() * 60)}`, value: `v${Math.random()}` }],
  () => ['POST', '/api/plugins/setting', { plugin: 'stress-flood', name: 'Setting1', value: junk(5000) }],
  () => ['POST', '/api/plugins/setting', { plugin: junk(10), name: junk(10), value: '', account: junk(5) }],
  () => ['POST', '/api/plugins/toggle', { name: pick(['stress-flood', 'stress-never']), enabled: true }],
  () => ['POST', '/api/addons/install', { id: '' }],
  () => ['POST', '/api/addons/remove', { id: 'not-an-addon' }],
  () => ['POST', '/api/theme', { theme: pick(['dark', 'light']) }],
  () => ['POST', '/api/prompt', { answer: junk(20) }],
  // Malformed, and not JSON at all.
  () => ['POST', '/api/command', '{"line": "status"'],
  () => ['POST', '/api/config', '[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[['.repeat(30)],
  () => ['POST', '/api/plugins/setting', junk(300)],
  () => ['DELETE', `/api/bots/${encodeURIComponent(junk(6))}`],
  () => ['GET', '/api/' + junk(30)],
  () => ['GET', '/' + 'a/'.repeat(500)],
];

async function one() {
  const [method, path, body] = pick(routes)();
  const init = { method, headers: {}, signal: AbortSignal.timeout(60000) };
  if (body !== undefined) {
    init.body = typeof body === 'string' ? body : JSON.stringify(body);
    init.headers['content-type'] = 'application/json';
  }
  stats.requests++;
  const started = performance.now();
  try {
    const r = await fetch(new URL(path, base), init);
    await r.arrayBuffer();
    stats.status[r.status] = (stats.status[r.status] || 0) + 1;
  } catch (e) {
    stats.failed++;
    const why = e?.cause?.code || e?.name || 'error';
    stats.errors[why] = (stats.errors[why] || 0) + 1;
  }
  timed(method, typeof body === 'object' && body?.line !== undefined ? `${path} ${String(body.line).slice(0, 30)}` : path, performance.now() - started);
}

// A body far bigger than the dashboard takes: 40 MB, once in a while.
async function oversized() {
  stats.requests++;
  try {
    const r = await fetch(new URL('/api/command', base), { method: 'POST', headers: { 'content-type': 'application/json' },
      body: '{"line":"' + 'x'.repeat(40 * 1024 * 1024) + '"}', signal: AbortSignal.timeout(60000) });
    await r.arrayBuffer();
    stats.status[r.status] = (stats.status[r.status] || 0) + 1;
  } catch (e) {
    const why = e?.cause?.code || e?.name || 'error';
    stats.errors['oversized:' + why] = (stats.errors['oversized:' + why] || 0) + 1;
  }
}

// Slow clients: a connection that sends its request a byte every second, never finishing. Kestrel should close it.
function slowClient() {
  return new Promise((resolve) => {
    stats.slowClients++;
    const started = Date.now();
    const s = net.connect(Number(url.port), url.hostname);
    const head = `POST /api/command HTTP/1.1\r\nHost: ${url.host}\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n`;
    let i = 0;
    const tick = setInterval(() => {
      if (s.destroyed || Date.now() > until) { clearInterval(tick); s.destroy(); return; }
      s.write(head[i % head.length] ?? 'x');
      i++;
    }, 1000);
    s.on('error', () => {});
    s.on('close', () => {
      clearInterval(tick);
      if (Date.now() - started > 120000) stats.slowHeld++;
      resolve();
    });
  });
}

const slow = Array.from({ length: 20 }, () => slowClient());
let n = 0;
await Promise.all(Array.from({ length: workers }, async () => {
  while (Date.now() < until) {
    if (++n % 2000 === 0) await oversized();
    else await one();
  }
}));
await Promise.race([Promise.all(slow), new Promise((r) => setTimeout(r, 5000))]);
console.log(JSON.stringify(stats));
process.exit(0);
