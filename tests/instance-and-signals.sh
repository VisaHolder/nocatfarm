#!/bin/sh
# One copy per config folder, and closing the terminal, on Linux and a Mac:
#   1. a second copy on the same folder - in a session of its own (setsid) where there is one, which a per-session lock
#      never saw - says which process has the folder, and exits;
#   2. SIGHUP (the Terminal window closed, an SSH session ended) is a clean shutdown: "shutting down" in the log, and
#      the lifetime totals written on the way out;
#   3. started with nohup, SIGHUP is ignored as asked, and it keeps running.
#   sh tests/instance-and-signals.sh <app folder>
set -e
APP="$1"
PORT=7270
T="$PWD/signals-test"
fails=0
check() { name="$1"; shift; if "$@"; then echo "PASS  $name"; else echo "FAIL  $name"; fails=$((fails + 1)); fi; }

rm -rf "$T" && mkdir -p "$T/data/config"
echo "{\"WebPort\":$PORT,\"CheckForUpdates\":false,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false}" > "$T/data/config/nocatFarm.json"
echo '{"Enabled":false,"SteamLogin":"not_a_real_account"}' > "$T/data/config/demo.json"

up() { for i in $(seq 1 60); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && return 0; sleep 1; done; return 1; }
pid() { head -n 1 "$T/data/config/state/instance.lock"; }
gone() { for i in $(seq 1 40); do kill -0 "$1" 2>/dev/null || return 0; sleep 1; done; return 1; }
# SIGHUP back to its default for what's started here, however this step was started: a signal that was ignored on entry
# stays ignored in sh, and inherited that way nocat.farm would (rightly) leave it ignored.
hup_default() { python3 -c 'import os, signal, sys; signal.signal(signal.SIGHUP, signal.SIG_DFL); os.execv(sys.argv[1], sys.argv[1:])' "$@"; }
detached() { if command -v setsid > /dev/null 2>&1; then setsid "$@"; else "$@"; fi; }

# ── 1. a second copy on the same folder ──
(cd "$T" && hup_default "$APP/nocatFarm" --no-gui --path data < /dev/null > "$T/first.log" 2>&1 &)
up
first=$(pid)
start=$(date +%s)
set +e
(cd "$T" && detached "$APP/nocatFarm" --no-gui --path data < /dev/null > "$T/second.log" 2>&1)
code=$?
set -e
took=$(( $(date +%s) - start ))
cat "$T/second.log"
check "second copy: exits, and not with success" test "$code" -ne 0
check "second copy: says the folder is taken, and by which process" sh -c "grep -q 'already running for this folder' '$T/second.log' && grep -q 'process $first' '$T/second.log'"
check "second copy: straight away (${took}s)" test "$took" -lt 20
check "second copy: the first one is still the one running" test "$(pid)" = "$first"
curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && echo "PASS  the first copy still answers" || { echo "FAIL  the first copy stopped answering"; fails=$((fails + 1)); }

# ── 2. SIGHUP is a clean shutdown ──
rm -f "$T/data/config/state/lifetime.json"
kill -HUP "$first"
check "SIGHUP: it closes" gone "$first"
check "SIGHUP: through the clean shutdown" grep -q 'shutting down' "$T/first.log"
check "SIGHUP: the lifetime totals were written on the way out" test -f "$T/data/config/state/lifetime.json"

# ── 3. nohup: SIGHUP ignored, as asked ──
(cd "$T" && nohup "$APP/nocatFarm" --no-gui --path data < /dev/null > "$T/nohup.log" 2>&1 &)
up
third=$(pid)
kill -HUP "$third"
sleep 5
check "nohup: SIGHUP is ignored and it keeps running" kill -0 "$third"
kill -TERM "$third"
check "nohup: SIGTERM still closes it cleanly" gone "$third"
check "nohup: ...through the clean shutdown" grep -q 'shutting down' "$T/nohup.log"

if [ "$fails" -gt 0 ]; then echo "$fails failed"; tail -n 20 "$T/first.log"; exit 1; fi
echo "all passed"
