#!/bin/sh
# Updating from the release people have now: the newest GitHub release's zip for this machine is unpacked and started,
# and its own updater (the one in that release, not this build's) installs THIS build - served from this machine as
# 9.9.9 - exactly as it will when this build is released. Then it must stay up, on the same settings.
#   sh tests/update-from-release.sh <rid> <zip of this build as 9.9.9> [<its zip for updaters from before 2.0.1>]
# 2.0.1: both are served the way GitHub lists them, by name - so an updater from before 2.0.1 takes the second (its first
# zip for this machine), and the program it starts is the one under the old name. Without the second, only the first.
# Needs GH_TOKEN (or a logged-in gh) to download the release.
set -e
RID="$1"
NEW="$2"
NEWOLD="${3:-}"
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
# The repository: this run's own on GitHub.
REPO="${GITHUB_REPOSITORY:-VisaHolder/gamerfarm}"
TAG=$(gh release list --repo "$REPO" --limit 10 --json tagName -q '.[].tagName' | while read -r t; do
	[ "$t" = "$SKIP" ] && continue
	if gh release view "$t" --repo "$REPO" --json assets -q '.assets[].name' | grep -q "_${RID}\.zip\$"; then
		echo "$t"
		break
	fi
done)
test -n "$TAG" || { echo "FAIL  no release has a zip for $RID"; exit 1; }
# A release can have more than one zip for this machine (2.0.0 had the same one under two names, 2.0.1 one for updaters
# from before): the plain gamer.farm-v* one, else whatever it has.
gh release download "$TAG" --repo "$REPO" --pattern "*_${RID}.zip" --dir "$T"
OLDZIP=$(ls "$T"/gamer.farm-v*_"$RID".zip 2>/dev/null | head -n 1)
[ -n "$OLDZIP" ] || OLDZIP=$(ls "$T"/*_"$RID".zip | head -n 1)
FROM=$(basename "$OLDZIP" | sed -n -e 's/^gamer\.farm-v\(.*\)_'"$RID"'\.zip$/\1/p' -e 's/^nocat\.farm-v\(.*\)_'"$RID"'\.zip$/\1/p')
echo "the newest release: $FROM ($OLDZIP)"
unzip -q "$OLDZIP" -d "$T/app"
# The program and the settings file under the names that release has: before 2.0.1, nocatFarm and nocatFarm.json.
PROG=gamerFarm
CFG=gamerFarm.json
if [ ! -e "$T/app/gamerFarm" ]; then PROG=nocatFarm; CFG=nocatFarm.json; fi
test -x "$T/app/$PROG"

cp "$NEW" "$T/feed/new.zip"
size=$(wc -c < "$T/feed/new.zip" | tr -d ' ')
asset=$(printf '{"name":"gamer.farm-v9.9.9_%s.zip","browser_download_url":"http://127.0.0.1:%s/new.zip","size":%s}' "$RID" "$FEED" "$size")
if [ -n "$NEWOLD" ]; then
	cp "$NEWOLD" "$T/feed/new-old.zip"
	osize=$(wc -c < "$T/feed/new-old.zip" | tr -d ' ')
	# Sorted by name, as GitHub lists them: "gamer.farm-old-updater-v" before "gamer.farm-v".
	asset=$(printf '{"name":"gamer.farm-old-updater-v9.9.9_%s.zip","browser_download_url":"http://127.0.0.1:%s/new-old.zip","size":%s},%s' "$RID" "$FEED" "$osize" "$asset")
fi
printf '{"tag_name":"v9.9.9","body":"- this build","assets":[%s]}' "$asset" > "$T/feed/latest.json"
(cd "$T/feed" && nohup python3 -m http.server "$FEED" --bind 127.0.0.1 > "$T/feed.log" 2>&1 &)
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$FEED/latest.json" > /dev/null && break; sleep 1; done

mkdir -p "$T/app/config"
echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$T/app/config/$CFG"
echo '{"Enabled":false,"SteamLogin":"not_a_real_account"}' > "$T/app/config/demo.json"

ver() { curl -sf "http://127.0.0.1:$PORT/api/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Version"])' 2>/dev/null || true; }
# Its user-count ping to a dead port here as well, never gamer.mom: the release it starts as doesn't know CountMeAsUser,
# and a config it writes back without it would be taken as on by this build.
# Both names for the variables: a release from before 2.0.1 reads only NOCATFARM_*, and this build takes those too.
(cd "$T/app" && env -u INVOCATION_ID -u JOURNAL_STREAM NOCATFARM_UPDATE_FEED="http://127.0.0.1:$FEED/latest.json" NOCATFARM_PING_URL="http://127.0.0.1:9/api/farm/ping" \
	GAMERFARM_PING_URL="http://127.0.0.1:9/api/farm/ping" nohup "$T/app/$PROG" --no-gui > "$T/run.log" 2>&1 &)
for i in $(seq 1 60); do [ -n "$(ver)" ] && break; sleep 1; done
test "$(ver)" = "$FROM" || { echo "FAIL: the release didn't start ($(ver))"; tail -n 30 "$T/run.log"; exit 1; }

curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d '{"line":"update now"}'; echo
for i in $(seq 1 120); do sleep 2; [ "$(ver)" = "9.9.9" ] && break; done
cat "$T"/app/logs/*.log 2>/dev/null | grep -E 'update|undone|crash' | tail -n 20
test "$(ver)" = "9.9.9" || { echo "FAIL: $FROM didn't update itself to this build"; exit 1; }
sleep 40   # past the half minute before the new version says "ok"
test "$(ver)" = "9.9.9" || { echo "FAIL: this build was put back after $FROM installed it"; exit 1; }
grep -q '"WebPort": *'"$PORT" "$T/app/config/gamerFarm.json"
# 2.0.1: once it stayed, everything under the names from before has moved over or gone - nothing called nocat is left.
if [ "$PROG" = nocatFarm ]; then
	sleep 5
	left=$(cd "$T/app" && find . -iname '*nocat*' | head -n 5)
	[ -z "$left" ] || { echo "FAIL: left under the old names: $left"; exit 1; }
	# The program the running copy runs - the copy that holds the folder's lock, by the file it was started from. Never by
	# its name: on a Mac the old updater started it through the script under the old name, which keeps that name as the
	# start of its command line on purpose (tools/linux/old-program.sh) - so pgrep -x gamerFarm found nothing there,
	# though gamerFarm was what ran.
	p=$(head -n 1 "$T/app/config/state/instance.lock" 2>/dev/null || true)
	exe=""
	if [ -n "$p" ] && [ -e "/proc/$p/exe" ]; then
		exe=$(readlink "/proc/$p/exe" 2>/dev/null || true)
	elif [ -n "$p" ]; then
		# A Mac: the files it has mapped (its program and libraries) - the program is the one called gamerFarm, if it's there
		exe=$(lsof -a -p "$p" -d txt -Fn 2>/dev/null | sed -n 's/^n//p' | grep '/gamerFarm$' | head -n 1 || true)
	fi
	if [ "$(basename "${exe:-none}")" != gamerFarm ]; then
		echo "FAIL: it isn't running as gamerFarm (process ${p:-none} runs ${exe:-nothing})"
		ps -A -ww -o pid= -o args= | grep -i farm | grep -v grep || true
		exit 1
	fi
	echo "running as gamerFarm: process $p, $exe (started as: $(ps -ww -o args= -p "$p" | cut -c 1-200))"
	echo "PASS: moved over from $FROM - settings, program and logs under the new names, nothing left under the old ones"
fi
curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d '{"line":"status"}' | grep -q demo
if [ "$(uname)" = "Darwin" ]; then
	# Whatever carries a signature still verifies (one changed in place under its signature is "Killed: 9").
	signed=0
	for f in "$T/app/gamerFarm" "$T"/app/*.dylib; do
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
