#!/bin/sh
# The self-update on Linux and a Mac, end to end, against releases served from this machine - installed in a folder
# with a space and "(1)" in its name (a pattern-matching updater never found itself there), started from ANOTHER folder
# with a relative --path (the restart used to come back on an empty folder next to the program):
#   1. a new version that crashes the moment it starts is put back, and the old one says so;
#   2. a good new version goes in, on the same settings, and stays;
#   3. with a second copy running from the same install folder (on its own --path), the update is refused and both keep
#      running;
#   4. on a Mac, started from start.command: the new version opens in a Terminal window of its own.
# Run from the repo root with the release already unpacked:  sh tests/update-rollback.sh <rid> [app folder]
set -e
RID="$1"
APP="${2:-app}"
VER=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/NocatFarm/NocatFarm.csproj)
PORT=7250
PORT2=7251
FEED=8765
T="$PWD/upd-test"

# The "new" versions: a good 9.9.9, and a 9.9.8 whose program exits at once.
dotnet publish src/NocatFarm -c Release -o upd/good -r "$RID" --self-contained true \
	-p:PublishSingleFile=false -p:DebugType=none -p:Version=9.9.9 --nologo -v q
rm -rf upd/broken && cp -R upd/good upd/broken
printf '#!/bin/sh\nexit 3\n' > upd/broken/nocatFarm
chmod +x upd/broken/nocatFarm
rm -f upd/good.zip upd/broken.zip
(cd upd/good && zip -qry "$PWD/../good.zip" .) || { echo "zip failed: $?"; exit 1; }
(cd upd/broken && zip -qry "$PWD/../broken.zip" .) || { echo "zip failed: $?"; exit 1; }
ls -la upd | head -n 10
# Each check on a line of its own: under set -e a failure anywhere but the end of an a && b list, or in a ! command,
# doesn't stop the script - such a check could never fail.
test -s upd/good.zip
test -s upd/broken.zip

feed() {   # name tag zip
	size=$(wc -c < "upd/$3" | tr -d ' ')
	printf '{"tag_name":"%s","body":"- a test release","assets":[{"name":"nocat.farm-%s_%s.zip","browser_download_url":"http://127.0.0.1:%s/%s","size":%s}]}' \
		"$2" "$2" "$RID" "$FEED" "$3" "$size" > "upd/$1.json"
}
feed broken v9.9.8 broken.zip
feed good v9.9.9 good.zip
(cd upd && nohup python3 -m http.server "$FEED" --bind 127.0.0.1 > ../feed.log 2>&1 &)
for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$FEED/good.json" > /dev/null && break; sleep 1; done
curl -sf "http://127.0.0.1:$FEED/good.json" > /dev/null || { echo "the test feed didn't start"; cat feed.log; exit 1; }

# A fresh copy of the release being tested, with nothing of any earlier run in it, and a data folder elsewhere.
HERE="$T/nocat.farm (1)"
CWD="$T/started from here"
DATA="$CWD/data"
fresh() {
	rm -rf "$T"
	mkdir -p "$T"
	cp -R "$APP" "$HERE"
	rm -rf "$HERE/config" "$HERE/logs"
	mkdir -p "$DATA/config" "$CWD/data2/config"
	echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$DATA/config/nocatFarm.json"
	echo "{\"WebPort\":$PORT2,\"CheckForUpdates\":false,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$CWD/data2/config/nocatFarm.json"
}

# As from a terminal: a CI runner is itself a systemd service, and what it starts inherits the markers that make
# nocat.farm (rightly) refuse to update itself as a service. --path is relative, to the folder it's started from.
start() {   # feed [data folder] [port]
	(cd "$CWD" && env -u INVOCATION_ID -u JOURNAL_STREAM NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/$1.json" \
		nohup "$HERE/nocatFarm" --no-gui --path "${2:-data}" > "$T/run-$1.log" 2>&1 &)
	for i in $(seq 1 60); do curl -sf "http://127.0.0.1:${3:-$PORT}/api/status" > /dev/null && return 0; sleep 1; done; return 1; }
ver() { curl -sf "http://127.0.0.1:${1:-$PORT}/api/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Version"])' 2>/dev/null || true; }
cmd() { curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d "{\"line\":\"$1\"}"; echo; }
# By the process id in the lock file - never by a pattern, which "(1)" breaks.
pid_of() { head -n 1 "$1/config/state/instance.lock" 2>/dev/null || true; }
stop() {   # [data folder]
	p=$(pid_of "${1:-$DATA}")
	[ -n "$p" ] || return 0
	kill -TERM "$p" 2>/dev/null || return 0
	for i in $(seq 1 30); do kill -0 "$p" 2>/dev/null || return 0; sleep 1; done
	kill -KILL "$p" 2>/dev/null || true
}
logs() { cat "$DATA"/logs/*.log 2>/dev/null | grep -E 'update|undone|crash|another' | tail -n 20; }

echo "== 1. a broken new version is put back"
fresh
start broken
test "$(ver)" = "$VER"
cmd 'update now'
back=""
for i in $(seq 1 90); do
	sleep 2
	if [ "$(ver)" = "$VER" ] && grep -q 'update undone' "$DATA"/logs/*.log 2>/dev/null; then back=1; break; fi
done
logs
[ -n "$back" ] || { echo "FAIL: not put back"; exit 1; }
grep -q 'update undone: 9.9.8' "$DATA"/logs/*.log
test -x "$HERE/nocatFarm"
if grep -q 'exit 3' "$HERE/nocatFarm"; then echo "FAIL: the broken program was left in place"; exit 1; fi
echo "PASS: back on $VER, on the same --path data folder, and it said why"
stop

echo "== 2. a good new version goes in"
start good
cmd 'update now'
for i in $(seq 1 90); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
logs
test "$(ver)" = "9.9.9"
grep -q '"WebPort": *'"$PORT" "$DATA/config/nocatFarm.json"
# Started on the same folder: the new version's log is in it, and nothing was made next to the program instead.
grep -q 'updated .* 9.9.9' "$DATA"/logs/*.log
if [ -e "$HERE/config/nocatFarm.json" ]; then echo "FAIL: a config/nocatFarm.json was made next to the program"; exit 1; fi
if [ -e "$HERE/data" ]; then echo "FAIL: a data folder was made next to the program"; exit 1; fi
sleep 40   # past the half minute before it says "ok" - it must not be put back after that
test "$(ver)" = "9.9.9"
test -z "$(find "$HERE" -name '*.nf-new')"
echo "PASS: updated $VER -> 9.9.9 in \"nocat.farm (1)\", restarted on the relative --path's folder, and it stayed"
if [ "$(uname)" = "Darwin" ]; then
	# Every library and the program itself still carries a valid signature after being replaced (a file changed in place
	# under its signature is what gets a Mac program "Killed: 9"). Only the ones signed to begin with.
	signed=0
	for f in "$HERE/nocatFarm" "$HERE"/*.dylib; do
		if codesign -d "$f" > /dev/null 2>&1; then
			codesign --verify "$f" || { echo "FAIL: $f's signature is broken after the update"; exit 1; }
			signed=$((signed + 1))
		fi
	done
	echo "PASS: $signed signed files verify after the update"
fi
stop

echo "== 3. another copy running from the same install folder: the update is refused, both keep running"
fresh
start good data "$PORT"
start good data2 "$PORT2"
test "$(ver "$PORT2")" = "$VER"
cmd 'update now'
for i in $(seq 1 30); do sleep 1; grep -q 'another nocat.farm is running from this folder' "$DATA"/logs/*.log 2>/dev/null && break; done
logs
grep -q "another nocat.farm is running from this folder (process $(pid_of "$CWD/data2"))" "$DATA"/logs/*.log
test "$(ver)" = "$VER" || { echo "FAIL: the first copy is on $(ver), not $VER"; exit 1; }
test "$(ver "$PORT2")" = "$VER" || { echo "FAIL: the second copy is on $(ver "$PORT2"), not $VER"; exit 1; }
echo "PASS: refused with the other copy's process id, nothing changed, both still up"
stop "$CWD/data2"
stop

# 4. On a Mac, started from start.command: the new version opens in a Terminal window of its own, like the old one.
if [ "$(uname)" = "Darwin" ] && [ -x "$HERE/start.command" ]; then
	echo "== 4. Mac, started from start.command"
	fresh
	echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$HERE/config.json.tmp"
	mkdir -p "$HERE/config" && mv "$HERE/config.json.tmp" "$HERE/config/nocatFarm.json"
	(cd "$HERE" && NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/good.json" nohup "$HERE/start.command" --no-gui > "$T/run-command.log" 2>&1 &)
	for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && break; sleep 1; done
	test "$(ver)" = "$VER"
	cmd 'update now'
	for i in $(seq 1 90); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
	cat "$HERE"/logs/*.log 2>/dev/null | grep -E 'update|undone|crash' | tail -n 20
	test "$(ver)" = "9.9.9"
	sleep 40
	test "$(ver)" = "9.9.9"
	echo "PASS: updated through start.command, back up in its own Terminal window, and it stayed"
	stop "$HERE"
	osascript -e 'tell application "Terminal" to quit' 2>/dev/null || true
fi
pkill -f "http.server $FEED" || true
