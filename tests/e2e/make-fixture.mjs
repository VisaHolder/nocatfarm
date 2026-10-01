// The throwaway config the browser test runs against, written into <folder>/config before the copy starts:
//   node tests/e2e/make-fixture.mjs <folder> <port>
// Three accounts, all switched off with made-up logins, so nothing ever signs in to Steam: a human-mode one, a robot
// one and one with an authenticator secret (so the Authenticator tab shows). Updates, the Steam group, the startup
// entry, the tray icon, pop-ups, keeping the PC awake and the browser opening by itself are all off. A few days of history so the charts draw, and a
// pretend ArchiSteamFarm folder for the import dialog to find.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [dir, portArg] = process.argv.slice(2);
if (!dir || !portArg) {
  console.error('usage: node make-fixture.mjs <folder> <port>');
  process.exit(2);
}

const config = path.join(dir, 'config');
const write = (file, value) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, typeof value === 'string' ? value : JSON.stringify(value, null, 2));
};

write(path.join(config, 'nocatFarm.json'), {
  WebPort: Number(portArg),
  WebHost: '127.0.0.1',
  CheckForUpdates: false,
  UpdateMode: 1,
  StartWithWindows: false,
  TrayNotifications: false,
  Tray: false,
  StartMinimized: true,
  MiniOnTop: false,
  KeepAwake: false,
  OpenBrowserOnStart: false,
  OpenDashboardAfterAdd: false,
  DiscordPresence: false,
  Rep4RepEnabled: true,
  TutorialDone: true,
});

const account = (extra) => ({ Enabled: false, JoinGroup: false, ...extra });
write(path.join(config, 'human.json'), account({ SteamLogin: 'not_a_real_account', LegitMode: true, GameWeights: '730:70, 440:30', Notes: 'e2e human' }));
write(path.join(config, 'robot.json'), account({ SteamLogin: 'not_a_real_account_2', IdleGames: [440], Rep4Rep: true }));
// A made-up 20-byte secret: codes are worked out locally from the clock, nothing is asked of Steam.
write(path.join(config, 'authbot.json'), account({ SteamLogin: 'not_a_real_account_3', SharedSecret: crypto.randomBytes(20).toString('base64') }));

// History, the format History.cs keeps: one file per month, day -> account -> totals.
const months = {};
for (let i = 1; i <= 12; i++) {
  const d = new Date(Date.now() - i * 86400000);
  const day = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const month = day.slice(0, 7);
  months[month] = months[month] || {};
  months[month][day] = {
    robot: { Cards: i % 4, Comments: i % 3, Minutes: 60 + i * 10, Games: { 440: 60 + i * 10 }, Value: 10 + i / 10, Currency: 1 },
    human: { Cards: 0, Comments: 0, Minutes: 90, Games: { 730: 60, 440: 30 }, Value: 5, Currency: 1 },
  };
}
for (const [month, data] of Object.entries(months)) write(path.join(config, 'state', 'history', `${month}.json`), data);

// Another idler's folder for the import dialog - read only, never imported.
write(path.join(dir, 'fake-asf', 'config', 'ASF.json'), '{}');
write(path.join(dir, 'fake-asf', 'config', 'pretend.json'), { SteamLogin: 'not_a_real_login', Enabled: true });

console.log(`fixture written to ${config}`);
