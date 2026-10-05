#!/bin/sh
# Every button of the dashboard on Linux or a Mac: starts the built app headless (--no-gui) on a throwaway config in a
# folder of its own (three switched-off accounts with made-up logins - nothing signs in to Steam), waits for it to
# answer, runs tests/e2e/dashboard.mjs against it in a headless Chromium, and stops it again.
#   sh tests/e2e/run.sh <folder the app was unzipped into> [port]
# Needs node, and Playwright's Chromium (npx playwright install [--with-deps] chromium) - installed here if missing.
set -e
APP="$1"
PORT="${2:-7377}"
HERE=$(cd "$(dirname "$0")" && pwd)
[ -x "$APP/nocatFarm" ] || { echo "FAIL  no nocatFarm in $APP"; exit 1; }
[ "$PORT" != 7242 ] || { echo "FAIL  7242 is the real dashboard's port - pick another"; exit 1; }
# Its "Count me as a user" ping goes to ping-stub.mjs on this machine (port+1), never to nocat.lol.
STUB_PORT=$((PORT + 1))
[ "$STUB_PORT" != 7242 ] || { echo "FAIL  the ping stub would sit on 7242 - pick another port"; exit 1; }

T=$(mktemp -d "${TMPDIR:-/tmp}/nocatfarm-e2e.XXXXXX")
node "$HERE/make-fixture.mjs" "$T" "$PORT" --ping-stub

[ -d "$HERE/node_modules/playwright" ] || (cd "$HERE" && npm ci --no-audit --no-fund)
(cd "$HERE" && npx playwright install chromium > /dev/null)

nohup node "$HERE/ping-stub.mjs" "$STUB_PORT" < /dev/null > "$T/stub.log" 2>&1 &
STUB=$!
NOCATFARM_PING_URL="http://127.0.0.1:$STUB_PORT/api/farm/ping" nohup "$APP/nocatFarm" --no-gui --path "$T" < /dev/null > "$T/run.log" 2>&1 &
PID=$!
stop() {
	kill -TERM "$STUB" 2>/dev/null || true
	kill -TERM "$PID" 2>/dev/null || true
	for i in $(seq 1 30); do kill -0 "$PID" 2>/dev/null || break; sleep 1; done
	kill -KILL "$PID" 2>/dev/null || true
}
trap stop EXIT

for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && break; sleep 1; done
if ! curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null; then
	echo "FAIL  the copy didn't start on port $PORT"
	tail -n 40 "$T/run.log"
	exit 1
fi

set +e
node "$HERE/dashboard.mjs" "http://127.0.0.1:$PORT" "e2e-$(date +%s)-$$-password" --fixture "$T" --set-password --ping-stub "http://127.0.0.1:$STUB_PORT"
code=$?
set -e
if [ "$code" -ne 0 ]; then echo "== the app's own log"; tail -n 60 "$T/run.log"; fi
stop
trap - EXIT
rm -rf "$T"
exit "$code"
