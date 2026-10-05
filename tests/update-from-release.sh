#!/bin/sh
# Updating from the release people have now: the newest GitHub release's zip for this machine is unpacked and started,
# and its own updater (the one in that release, not this build's) installs THIS build - served from this machine as
# 9.9.9 - exactly as it will when this build is released. Then it must stay up, on the same settings.
#   sh tests/update-from-release.sh <rid> <zip of this build as 9.9.9>
# Needs GH_TOKEN (or a logged-in gh) to download the release.
set -e
RID="$1"
NEW="$2"
PORT=7260
FEED=8767
T="$PWD/from-release"

rm -rf "$T" && mkdir -p "$T/feed" "$T/app"
# The newest release that HAS a zip for this machine. Not simply the newest: the run that builds a release's Mac zips
# runs this before they're up, and the newest release - that one - had nothing to download.
# v1.6.4's Linux updater can't run (its script has \r\n line endings - sh fails on the first command, and the app it
# closed doesn't come back), so it can't be what's tested: people on it update by hand once, as its release notes say.
SKIP=""
case "$RID" in linux-*) SKIP="v1.6.4" ;; esac
TAG=$(gh release list --repo VisaHolder/nocatfarm --limit 10 --json tagName -q '.[].tagName' | while read -r t; do
	[ "$t" = "$SKIP" ] && continue
	if gh release view "$t" --repo VisaHolder/nocatfarm --json assets -q '.assets[].name' | grep -q "_${RID}\.zip\$"; then
		echo "$t"
		break
	fi
done)
test -n "$TAG" || { echo "FAIL  no release has a zip for $RID"; exit 1; }
gh release download "$TAG" --repo VisaHolder/nocatfarm --pattern "*_${RID}.zip" --dir "$T"
OLDZIP=$(ls "$T"/*_"$RID".zip | head -n 1)
FROM=$(basename "$OLDZIP" | sed -n 's/^nocat\.farm-v\(.*\)_'"$RID"'\.zip$/\1/p')
echo "the newest release: $FROM ($OLDZIP)"
unzip -q "$OLDZIP" -d "$T/app"
test -x "$T/app/nocatFarm"

cp "$NEW" "$T/feed/new.zip"
size=$(wc -c < "$T/feed/new.zip" | tr -d ' ')
printf '{"tag_name":"v9.9.9","body":"- this build","assets":[{"name":"nocat.farm-v9.9.9_%s.zip","browser_download_url":"http://127.0.0.1:%s/new.zip","size":%s}]}' \
	"$RID" "$FEED" "$size" > "$T/feed/latest.json"
(cd "$T/feed" && nohup python3 -m http.server "$FEED" --bind 127.0.0.1 > "$T/feed.log" 2>&1 &)
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$FEED/latest.json" > /dev/null && break; sleep 1; done

mkdir -p "$T/app/config"
echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$T/app/config/nocatFarm.json"
echo '{"Enabled":false,"SteamLogin":"not_a_real_account"}' > "$T/app/config/demo.json"

ver() { curl -sf "http://127.0.0.1:$PORT/api/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Version"])' 2>/dev/null || true; }
# Its user-count ping to a dead port here as well, never nocat.lol: the release it starts as doesn't know CountMeAsUser,
# and a config it writes back without it would be taken as on by this build.
(cd "$T/app" && env -u INVOCATION_ID -u JOURNAL_STREAM NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/latest.json" NOCATFARM_PING_URL="http://127.0.0.1:9/api/farm/ping" \
	nohup "$T/app/nocatFarm" --no-gui > "$T/run.log" 2>&1 &)
for i in $(seq 1 60); do [ -n "$(ver)" ] && break; sleep 1; done
test "$(ver)" = "$FROM" || { echo "FAIL: the release didn't start ($(ver))"; tail -n 30 "$T/run.log"; exit 1; }

curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d '{"line":"update now"}'; echo
for i in $(seq 1 120); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
cat "$T"/app/logs/*.log 2>/dev/null | grep -E 'update|undone|crash' | tail -n 20
test "$(ver)" = "9.9.9" || { echo "FAIL: $FROM didn't update itself to this build"; exit 1; }
sleep 40   # past the half minute before the new version says "ok"
test "$(ver)" = "9.9.9" || { echo "FAIL: this build was put back after $FROM installed it"; exit 1; }
grep -q '"WebPort": *'"$PORT" "$T/app/config/nocatFarm.json"
curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d '{"line":"status"}' | grep -q demo
if [ "$(uname)" = "Darwin" ]; then
	# Whatever carries a signature still verifies (one changed in place under its signature is "Killed: 9").
	signed=0
	for f in "$T/app/nocatFarm" "$T"/app/*.dylib; do
		if codesign -d "$f" > /dev/null 2>&1; then
			codesign --verify "$f" || { echo "FAIL: $f's signature doesn't verify after the update"; exit 1; }
			signed=$((signed + 1))
		fi
	done
	echo "PASS: $signed signed files verify after the update"
fi
echo "PASS: $FROM updated itself to this build, kept its settings and accounts, and stayed"

p=$(head -n 1 "$T/app/config/state/instance.lock" 2>/dev/null || true)
[ -n "$p" ] && kill -TERM "$p" 2>/dev/null || true
for i in $(seq 1 30); do [ -z "$p" ] || ! kill -0 "$p" 2>/dev/null && break; sleep 1; done
pkill -f "http.server $FEED" || true
