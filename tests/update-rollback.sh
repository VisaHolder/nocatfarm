#!/bin/sh
# The self-update on Linux and a Mac, end to end, against releases served from this machine:
#   1. a new version that crashes the moment it starts is put back, and the old one says so;
#   2. a good new version goes in, and the settings are still there.
# Run from the repo root with the release already built in ./app:  sh tests/update-rollback.sh <rid>
set -e
RID="$1"
VER=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/NocatFarm/NocatFarm.csproj)
PORT=7250
FEED=8765

# The "new" versions: a good 9.9.9, and a 9.9.8 whose program exits at once.
dotnet publish src/NocatFarm -c Release -o upd/good -r "$RID" --self-contained true \
	-p:PublishSingleFile=false -p:DebugType=none -p:Version=9.9.9 --nologo -v q
cp -R upd/good upd/broken
printf '#!/bin/sh\nexit 3\n' > upd/broken/nocatFarm
chmod +x upd/broken/nocatFarm
(cd upd/good && zip -qry ../good.zip .)
(cd upd/broken && zip -qry ../broken.zip .)

feed() {   # name tag zip
	size=$(wc -c < "upd/$3" | tr -d ' ')
	printf '{"tag_name":"%s","body":"- a test release","assets":[{"name":"nocat.farm-%s_%s.zip","browser_download_url":"http://127.0.0.1:%s/%s","size":%s}]}' \
		"$2" "$2" "$RID" "$FEED" "$3" "$size" > "upd/$1.json"
}
feed broken v9.9.8 broken.zip
feed good v9.9.9 good.zip
(cd upd && nohup python3 -m http.server "$FEED" --bind 127.0.0.1 > ../feed.log 2>&1 &)
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$FEED/good.json" > /dev/null && break; sleep 1; done
curl -sf "http://127.0.0.1:$FEED/good.json" > /dev/null || { echo "the test feed didn't start"; cat feed.log; exit 1; }

# A fresh copy of the release being tested, with nothing of any earlier run in it.
HERE="$PWD/t"
rm -rf "$HERE" && cp -R app "$HERE" && rm -rf "$HERE/config" "$HERE/logs" && mkdir -p "$HERE/config"
echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false}" > "$HERE/config/nocatFarm.json"

# As from a terminal: a CI runner is itself a systemd service, and what it starts inherits the markers that make
# nocat.farm (rightly) refuse to update itself as a service.
start() { (cd "$HERE" && env -u INVOCATION_ID -u JOURNAL_STREAM NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/$1.json" nohup "$HERE/nocatFarm" --no-gui > "../run-$1.log" 2>&1 &)
	for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && return 0; sleep 1; done; return 1; }
ver() { curl -sf "http://127.0.0.1:$PORT/api/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Version"])' 2>/dev/null || true; }
cmd() { curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d "{\"line\":\"$1\"}"; echo; }
stop() { pkill -TERM -f "$HERE/nocatFarm" || true; for i in $(seq 1 30); do pgrep -f "$HERE/nocatFarm" > /dev/null || return 0; sleep 1; done; pkill -KILL -f "$HERE/nocatFarm" || true; }
logs() { cat "$HERE"/logs/*.log 2>/dev/null | grep -E 'update|undone|crash' | tail -n 20; }

echo "== 1. a broken new version is put back"
start broken
test "$(ver)" = "$VER"
cmd 'update now'
back=""
for i in $(seq 1 90); do
	sleep 2
	if [ "$(ver)" = "$VER" ] && grep -q 'update undone' "$HERE"/logs/*.log 2>/dev/null; then back=1; break; fi
done
logs
[ -n "$back" ] || { echo "FAIL: not put back"; exit 1; }
grep -q 'update undone: 9.9.8' "$HERE"/logs/*.log
test -x "$HERE/nocatFarm" && ! grep -q 'exit 3' "$HERE/nocatFarm"
echo "PASS: back on $VER, and it said why"
stop

echo "== 2. a good new version goes in"
start good
cmd 'update now'
for i in $(seq 1 90); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
logs
test "$(ver)" = "9.9.9"
grep -q '"WebPort": *'"$PORT" "$HERE/config/nocatFarm.json"
sleep 40   # past the half minute before it says "ok" - it must not be put back after that
test "$(ver)" = "9.9.9"
echo "PASS: updated $VER -> 9.9.9, settings kept, and it stayed"
stop

# 3. On a Mac, started from start.command: the new version opens in a Terminal window of its own, like the old one.
if [ "$(uname)" = "Darwin" ] && [ -x "$HERE/start.command" ]; then
	echo "== 3. Mac, started from start.command"
	rm -rf "$HERE" && cp -R app "$HERE" && rm -rf "$HERE/config" "$HERE/logs" && mkdir -p "$HERE/config"
	echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false}" > "$HERE/config/nocatFarm.json"
	(cd "$HERE" && NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/good.json" nohup "$HERE/start.command" --no-gui > ../run-command.log 2>&1 &)
	for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && break; sleep 1; done
	test "$(ver)" = "$VER"
	cmd 'update now'
	for i in $(seq 1 90); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
	logs
	test "$(ver)" = "9.9.9"
	sleep 40
	test "$(ver)" = "9.9.9"
	echo "PASS: updated through start.command, back up in its own Terminal window, and it stayed"
	stop
	osascript -e 'tell application "Terminal" to quit' 2>/dev/null || true
fi
pkill -f "http.server $FEED" || true
