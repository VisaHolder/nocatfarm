// A stand-in for nocat.lol's user count, so a test copy never counts itself on the real site:
//   node tests/e2e/ping-stub.mjs <port>
// The copy under test is started with NOCATFARM_PING_URL=http://127.0.0.1:<port>/api/farm/ping (the app only takes an
// address on this PC there). POST /api/farm/ping answers {"users":212,"accounts":1240} the way nocat.lol does - the
// accounts big enough to need a thousands separator; GET /seen lists what was sent, for dashboard.mjs to check. Listens
// on 127.0.0.1 only.
import http from 'node:http';

const port = Number(process.argv[2]);
if (!port || port === 7242) {
  console.error('usage: node ping-stub.mjs <port> (not 7242)');
  process.exit(2);
}

export const USERS = 212;
export const ACCOUNTS = 1240;
const seen = [];

http.createServer((req, res) => {
  if (req.method === 'POST' && req.url === '/api/farm/ping') {
    let body = '';
    req.on('data', (c) => { body += c; if (body.length > 65536) req.destroy(); });
    req.on('end', () => {
      seen.push({ at: Date.now(), userAgent: req.headers['user-agent'] || '', contentType: req.headers['content-type'] || '', body });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ users: USERS, accounts: ACCOUNTS }));
    });
    return;
  }
  if (req.method === 'GET' && req.url === '/seen') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(seen));
    return;
  }
  res.writeHead(404);
  res.end();
}).listen(port, '127.0.0.1', () => console.log(`ping stub on 127.0.0.1:${port}`));
