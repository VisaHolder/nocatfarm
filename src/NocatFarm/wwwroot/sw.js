// nocat.farm's service worker: part of what lets the dashboard be installed like an app. Browsers only run one on this
// PC itself (localhost) or over https; anywhere else the page simply works without it.
//
// It keeps a copy of the page itself - the shell: the HTML, the script, the styles, the icons and fonts - so the app
// opens even when the PC is briefly out of reach, and says so instead of a browser error. It NEVER keeps anything from
// /api/: account names, inventories, codes and settings always come fresh from the PC, or not at all. The shell is
// fetched fresh first too, so an update to nocat.farm shows straight away; the copy is only for when the PC can't be reached.
'use strict';

const SHELL = 'nocatfarm-shell-v1';
const SHELL_FILES = ['./', 'index.html', 'app.js', 'style.css', 'logo.png', 'favicon.ico', 'manifest.webmanifest',
  'icon-192.png', 'icon-512.png', 'apple-touch-icon.png', 'fonts/jbmono-Regular.woff2', 'fonts/jbmono-Bold.woff2'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(SHELL).then((c) => c.addAll(SHELL_FILES)).catch(() => {}).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== SHELL).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

/// The shell, and only the shell: anything else - /api/ above all - is left to the network, untouched and never stored.
function isShell(url) {
  if (url.origin !== self.location.origin) return false;
  const path = url.pathname;
  if (path.startsWith('/api/') || path === '/api') return false;
  return path === '/' || /\.(html|js|css|png|ico|woff2|webmanifest|json)$/.test(path);
}

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (!isShell(url)) return;

  // Fresh from the PC first; the stored copy only when that fails. A fresh answer replaces the stored copy.
  event.respondWith(fetch(req).then((res) => {
    if (res && res.ok && res.type === 'basic') {
      const copy = res.clone();
      caches.open(SHELL).then((c) => c.put(req, copy)).catch(() => {});
    }
    return res;
  }).catch(() => caches.match(req, { ignoreSearch: true }).then((hit) => hit || caches.match('index.html'))));
});
