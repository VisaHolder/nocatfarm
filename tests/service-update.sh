#!/bin/bash
# nocat.farm updating itself as a real systemd service: run as root on a machine (or LXC container) with systemd, no Steam
# accounts. Each scenario installs the zip fresh in /opt/nocatfarm, under the unit from tools/linux, with its data in a
# separate --path folder, running as a user of its own - and checks "exactly one copy" with ps every time.
#
#   bash tests/service-update.sh all <folder with the zips>     every scenario that needs no reboot; PASS/FAIL per scenario
#   bash tests/service-update.sh one <scenario> <zips>          one of them, or the first or second half of a reboot one
#
# The zips, all made by tools/package-release.ps1 from this code unless it says otherwise:
#   cur.zip   as its own version; good.zip  as 9.9.9; hand.zip  as 9.9.8; prev.zip  the newest GitHub release (optional)
# NF_TEST_POWER=host: a power cut comes from outside (lxc-stop -k), not from this machine's own kernel.
set -u

MODE="${1:-}"
SCN=""
if [ "$MODE" = one ]; then SCN="${2:-}"; ZIPS="${3:-}"; else ZIPS="${2:-}"; fi
ZIPS=$(cd "$ZIPS" && pwd) || exit 2
SRC=$(cd "$(dirname "$0")/.." && pwd)
UNIT=nocatfarm
HERE=/opt/nocatfarm
DATA=/var/lib/nocatfarm
JOB=$HERE/.nocatfarm-update/job
USR=nftest
PORT=7250
FEED=8765
WORK=/var/tmp/nf-service-test
LOGS=$DATA/logs
UNITFILE=/etc/systemd/system/$UNIT.service
DROPIN=/etc/systemd/system/$UNIT.service.d
VER=$(cd "$WORK" 2>/dev/null && cat cur.ver 2>/dev/null || true)

say() { echo "    $*"; }
die() {
	echo "    FAIL: $*"
	systemctl --no-pager --full status "$UNIT" 2>&1 | tail -n 15 | sed 's/^/    | /'
	journalctl -u "$UNIT" --no-pager -n 40 2>/dev/null | sed 's/^/    | /'
	tail -n 15 "$LOGS"/*.log 2>/dev/null | sed 's/^/    | /'
	exit 1
}
must() { "$@" || die "$*"; }

# ── the zips, the pretend GitHub ─────────────────────────────────────────────
make_variant() {   # name, then the text of the nocatFarm that replaces the program
	rm -rf "${WORK:?}/v" && mkdir -p "$WORK/v"
	unzip -q "$ZIPS/good.zip" -d "$WORK/v"
	printf '%b' "$2" > "$WORK/v/nocatFarm"
	chmod 755 "$WORK/v/nocatFarm"
	rm -f "$WORK/feed/$1.zip"
	(cd "$WORK/v" && zip -qry "$WORK/feed/$1.zip" .)
	rm -rf "${WORK:?}/v"
}

offer() {   # the zip in the feed folder (no .zip), the version
	size=$(wc -c < "$WORK/feed/$1.zip")
	printf '{"tag_name":"v%s","body":"- a test release","assets":[{"name":"nocat.farm-v%s_linux-x64.zip","browser_download_url":"http://127.0.0.1:%s/%s.zip","size":%s}]}' \
		"$2" "$2" "$FEED" "$1" "$size" > "$WORK/feed/latest.tmp"
	mv -f "$WORK/feed/latest.tmp" "$WORK/feed/latest.json"
}

prepare_machine() {
	id "$USR" > /dev/null 2>&1 || useradd -m -s /bin/sh "$USR"
	mkdir -p "$WORK/feed"
	if [ ! -f "$WORK/feed/good.zip" ]; then
		cp "$ZIPS/good.zip" "$WORK/feed/good.zip"
		make_variant exit3 '#!/bin/sh\nexit 3\n'
		make_variant exit0 '#!/bin/sh\nexit 0\n'
		make_variant latecrash '#!/bin/sh\nsleep 8\nexit 134\n'
		make_variant garbage 'not a program at all\n'
		make_variant hang "#!/bin/sh\ntrap '' TERM\nwhile :; do sleep 3600; done\n"
		sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$SRC/src/NocatFarm/NocatFarm.csproj" > "$WORK/cur.ver"
	fi
	VER=$(cat "$WORK/cur.ver")
	if ! curl -sf "http://127.0.0.1:$FEED/good.zip" -o /dev/null -r 0-1; then
		(cd "$WORK/feed" && setsid nohup python3 -m http.server "$FEED" --bind 127.0.0.1 > "$WORK/feed.log" 2>&1 &)
		for _ in $(seq 1 30); do curl -sf "http://127.0.0.1:$FEED/good.zip" -o /dev/null -r 0-1 && break; sleep 1; done
	fi
	offer good 9.9.9
}

# ── installing ───────────────────────────────────────────────────────────────
write_new_unit() {
	sed -e "s/^User=youruser/User=$USR/" -e "s|^ExecStart=/opt/nocatfarm/nocatFarm|ExecStart=/opt/nocatfarm/nocatFarm --no-gui --path $DATA|" \
		"$SRC/tools/linux/nocatfarm.service" > "$UNITFILE"
}

# The unit the docs gave up to 1.9.0.
write_old_unit() {
	cat > "$UNITFILE" <<EOF
[Unit]
Description=nocat.farm
After=network-online.target
Wants=network-online.target

[Service]
User=$USR
WorkingDirectory=$HERE
ExecStart=$HERE/nocatFarm --no-gui --path $DATA
Restart=on-failure
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
EOF
}

write_dropin() {
	mkdir -p "$DROPIN"
	cat > "$DROPIN/test.conf" <<EOF
[Service]
Environment=NOCATFARM_UPDATE_FEED=http://127.0.0.1:$FEED/latest.json
Environment=NOCATFARM_PING_URL=http://127.0.0.1:9/api/farm/ping
EOF
}

config_with() {   # extra json fields
	mkdir -p "$DATA/config"
	echo "{\"WebPort\":$PORT,\"CheckForUpdates\":true,\"JoinGroup\":false,\"OpenBrowserOnStart\":false,\"CountMeAsUser\":false${1:-}}" > "$DATA/config/nocatFarm.json"
}

wipe() {
	systemctl stop "$UNIT" 2>/dev/null
	systemctl reset-failed "$UNIT" 2>/dev/null
	rm -rf "${HERE:?}" "${DATA:?}" "${DROPIN:?}/slow.conf"
	mkdir -p "$HERE" "$DATA"
}

ready() {   # waits for the dashboard
	for _ in $(seq 1 "${1:-90}"); do curl -sf "http://127.0.0.1:$PORT/api/status" > /dev/null && return 0; sleep 1; done
	return 1
}

fresh() {   # zip, unit (new|old), extra config
	wipe
	unzip -q "$1" -d "$HERE" || die "unzip $1"
	config_with "${3:-}"
	chown -R "$USR:" "$HERE" "$DATA"
	if [ "${2:-new}" = new ]; then write_new_unit; else write_old_unit; fi
	write_dropin
	systemctl daemon-reload
	systemctl start "$UNIT" || die "the service didn't start"
	ready || die "the dashboard didn't come up"
}

manifest() { (cd "$HERE" && find . -type f ! -path './.nocatfarm-update/*' ! -name '.nf-*' -print0 | sort -z | xargs -0 sha256sum); }

ver() { curl -sf "http://127.0.0.1:$PORT/api/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Version"])' 2>/dev/null; }
cmd() { curl -sf -X POST "http://127.0.0.1:$PORT/api/command" -H 'Content-Type: application/json' -d "{\"line\":\"$1\"}"; echo; }
main_pid() { systemctl show -p MainPID --value "$UNIT"; }
restarts() { systemctl show -p NRestarts --value "$UNIT"; }

# Exactly one copy, and it's the one systemd knows about.
one_copy() {
	ps -o pid,ppid,user,etimes,args -C nocatFarm 2>/dev/null | sed 's/^/    ps: /'
	n=$(pgrep -x nocatFarm | wc -l)
	[ "$n" = 1 ] || die "$n copies running"
	[ "$(pgrep -x nocatFarm)" = "$(main_pid)" ] || die "the copy running ($(pgrep -x nocatFarm)) isn't systemd's main process ($(main_pid))"
}

no_copy() {
	n=$(pgrep -x nocatFarm | wc -l)
	[ "$n" = 0 ] || die "$n copies running, none expected"
	systemctl is-active -q "$UNIT" && die "the service is active, it should be stopped"
	return 0
}

wait_ver() {   # version, seconds
	for _ in $(seq 1 "$2"); do [ "$(ver)" = "$1" ] && return 0; sleep 1; done
	return 1
}

in_log() { grep -q "$1" "$LOGS"/*.log 2>/dev/null; }
wait_log() {   # text, seconds
	for _ in $(seq 1 "$2"); do in_log "$1" && return 0; sleep 1; done
	return 1
}

wait_committed() {   # seconds
	for _ in $(seq 1 "$1"); do [ ! -d "$JOB" ] && return 0; sleep 1; done
	return 1
}

wait_phase() {   # phase, seconds
	for _ in $(seq 1 "$2"); do [ "$(cat "$JOB/phase" 2>/dev/null)" = "$1" ] && return 0; sleep 0.2; done
	return 1
}

# The helper made to go slowly, so a stop lands half way through its files.
slow_helper() {
	sed -i 's/^\(\s*\)mv -f "\$1\/\$f" "\$here\/\$f" || exit 1$/&\n\1sleep 6/' "$HERE/nocatfarm-service.sh"
	grep -q 'sleep 6' "$HERE/nocatfarm-service.sh" || die "couldn't slow the helper down"
	chown "$USR:" "$HERE/nocatfarm-service.sh"
}

# Half way: some of the files have gone in, not all.
wait_half_way() {
	wait_phase swapping 240 || die "the swap never started"
	for _ in $(seq 1 60); do
		# Only what differs between the two versions is moved: gone from the job and in place, or still waiting there.
		moved=0; left=0
		while IFS= read -r f; do
			f=${f#./}
			[ "$f" = nocatfarm-service.sh ] && continue
			cmp -s "$JOB/backup/$f" "$HERE/$f" || moved=$((moved + 1))
		done < <(cd "$JOB/backup" && find . -type f)
		while IFS= read -r f; do
			f=${f#./}
			[ "$f" = nocatfarm-service.sh ] && continue
			cmp -s "$JOB/new/$f" "$HERE/$f" || left=$((left + 1))
		done < <(cd "$JOB/new" && find . -type f)
		if [ "$moved" -gt 0 ] && [ "$left" -gt 0 ]; then
			say "half way: $moved files in, $left still to go"
			return 0
		fi
		sleep 0.5
	done
	die "never caught it half way: $moved in, $left to go"
}

power_cut() {
	sync
	if [ "${NF_TEST_POWER:-}" = host ]; then return 0; fi
	echo 1 > /proc/sys/kernel/sysrq
	echo b > /proc/sysrq-trigger
	sleep 60
}

# The service's own files after an update: still the service user's, the secret key still only theirs.
ownership_ok() {
	bad=$(find "$HERE" "$DATA" ! -user "$USR" | head -n 5)
	[ -z "$bad" ] || die "not owned by $USR: $bad"
	[ "$(stat -c %a "$DATA/config/state/secret.key")" = 600 ] || die "secret.key is $(stat -c %a "$DATA/config/state/secret.key")"
	[ "$(stat -c %a "$HERE/.nocatfarm-update")" = 700 ] || die ".nocatfarm-update is $(stat -c %a "$HERE/.nocatfarm-update")"
}

went_in() {   # version, the program's pid before
	wait_ver "$1" 120 || die "never reached $1 (on $(ver))"
	one_copy
	[ "$(main_pid)" != "$2" ] || die "the same process - it didn't restart"
	wait_committed 90 || die "the job is still there after the new version should have said it's fine"
	[ "$(ver)" = "$1" ] || die "went back from $1"
	one_copy
	wait_log "updated to $1" 60 || die "it never said 'updated to $1'"
	test -z "$(find "$HERE" -name '*.nf-new')" || die "leftover files"
}

went_back() {   # text in the log
	wait_log "$1" 300 || die "it never said '$1' (on $(ver))"
	wait_ver "$VER" 60 || die "not back on $VER (on $(ver))"
	one_copy
	[ ! -d "$JOB" ] || die "the job is still there"
	manifest > "$WORK/after"
	diff "$WORK/before" "$WORK/after" > /dev/null || { diff "$WORK/before" "$WORK/after" | head -n 10; die "the files aren't the old ones, byte for byte"; }
	test -z "$(find "$HERE" -name '*.nf-new')" || die "leftover files"
	say "old files back byte for byte, running $VER"
}

# ── the scenarios ────────────────────────────────────────────────────────────
scn_first_update_from_previous_release() {
	[ -f "$ZIPS/prev.zip" ] || die "no prev.zip"
	offer good 9.9.9
	fresh "$ZIPS/prev.zip" old
	from=$(ver)
	say "the release people have now: $from, under the unit the docs gave"
	pid=$(main_pid)
	cmd 'update now' | tee "$WORK/out" | sed 's/^/    /'
	grep -q 'by hand\|update itself\|doesn.t update\|extract' "$WORK/out" || die "it should have said how to update by hand"
	sleep 20
	[ "$(main_pid)" = "$pid" ] || die "it restarted"
	one_copy
	say "by hand, as it says: stop, unzip over the folder, start"
	systemctl stop "$UNIT"
	unzip -qo "$ZIPS/cur.zip" -d "$HERE"
	chown -R "$USR:" "$HERE"
	systemctl start "$UNIT"
	ready || die "it didn't come up after updating by hand"
	[ "$(ver)" = "$VER" ] || die "on $(ver), not $VER"
	grep -q "\"WebPort\": *$PORT" "$DATA/config/nocatFarm.json" || die "settings lost"
	say "still under the old unit: it says how to get the new one"
	pid=$(main_pid)
	cmd 'update now' | tee "$WORK/out" | sed 's/^/    /'
	grep -q 'nocatfarm.service' "$WORK/out" || die "it should have named the new unit"
	sleep 15
	[ "$(main_pid)" = "$pid" ] || die "it restarted"
	[ ! -d "$JOB" ] || die "something was staged"
	one_copy
	say "the new unit in: from now on it updates itself"
	write_new_unit
	systemctl daemon-reload
	systemctl restart "$UNIT"
	ready || die "it didn't come up under the new unit"
	pid=$(main_pid)
	cmd 'update now' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
}

scn_update_now() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	cmd 'set TelegramBotToken 123456789:not-a-real-token' > /dev/null
	test -f "$DATA/config/state/secret.key" || die "no secret.key to check"
	pid=$(main_pid)
	cmd 'update now' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
	journalctl -u "$UNIT" --no-pager | grep -q 'status=75' || die "the journal doesn't show exit code 75"
	grep -q 'nocat-aes1:' "$DATA/config/nocatFarm.json" || die "the settings' secret isn't encrypted any more"
	ownership_ok
	sleep 40
	[ "$(ver)" = 9.9.9 ] || die "put back after the half minute"
	one_copy
}

scn_update_accept() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	pid=$(main_pid)
	cmd 'update' | sed 's/^/    /'
	for _ in $(seq 1 90); do cmd 'update' | grep -q '9.9.9' && break; sleep 2; done
	cmd 'update accept' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
}

scn_dashboard_button() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	pid=$(main_pid)
	curl -sf -X POST "http://127.0.0.1:$PORT/api/update" | head -c 300 | sed 's/^/    /'; echo
	went_in 9.9.9 "$pid"
}

scn_update_file() {
	fresh "$ZIPS/cur.zip" new
	cp "$ZIPS/good.zip" /var/tmp/nf-good.zip
	chmod 644 /var/tmp/nf-good.zip
	pid=$(main_pid)
	# Not a program for this machine: refused before anything is touched.
	cp "$WORK/feed/exit3.zip" /var/tmp/nf-bad.zip
	chmod 644 /var/tmp/nf-bad.zip
	cmd 'update file /var/tmp/nf-bad.zip' | tee "$WORK/out" | sed 's/^/    /'
	grep -q "isn't a program" "$WORK/out" || die "a zip with no real program was accepted"
	[ "$(main_pid)" = "$pid" ] || die "it restarted"
	cmd 'update file /var/tmp/nf-good.zip' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
}

scn_night_install() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new ',"UpdateMode":2,"AutoUpdateFromHour":0,"AutoUpdateUntilHour":24,"AutoUpdateWaitHours":0,"UpdateCheckHours":1'
	pid=$(main_pid)
	say "nothing typed: waiting for it to install by itself"
	wait_ver 9.9.9 400 || die "it never installed by itself (on $(ver))"
	went_in 9.9.9 "$pid"
	in_log 'by itself' || die "it never said it was by itself"
}

bad_update() {   # the zip in the feed, its version, text expected in the log
	offer "$1" "$2"
	fresh "$ZIPS/cur.zip" new
	manifest > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	went_back "$3"
}

scn_bad_exit3() {
	bad_update exit3 9.9.8 'update undone: 9.9.8'
	cmd 'update' | tee "$WORK/out" | sed 's/^/    /'
	sleep 45
	[ "$(ver)" = "$VER" ] || die "it went for the broken version again"
	one_copy
	in_log 'Install complete' && die "it said the install was complete" || true
}
scn_bad_exit0() { bad_update exit0 9.9.8 'update undone: 9.9.8'; }
scn_bad_late_crash() { bad_update latecrash 9.9.8 'update undone: 9.9.8'; }
scn_bad_not_a_program() { bad_update garbage 9.9.8 'update undone: 9.9.8'; }
scn_bad_hangs_and_ignores_stop() { bad_update hang 9.9.8 'update undone: 9.9.8'; }

# A version that was good before it went in and fails only by being killed while it's being tried out.
scn_kill9_new_version_on_trial() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	manifest > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	wait_ver 9.9.9 120 || die "never reached 9.9.9"
	[ "$(cat "$JOB/phase" 2>/dev/null)" = trial ] || die "not on trial"
	kill -9 "$(main_pid)"
	went_back 'update undone: 9.9.9'
}

scn_kill9_mid_swap() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	slow_helper
	manifest | grep -v nocatfarm-service.sh > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	wait_half_way
	say "kill -9 everything in the service, half way through the files"
	systemctl kill -s KILL "$UNIT"
	wait_log 'was stopped half way' 300 || die "it never said it was stopped half way (on $(ver))"
	wait_ver "$VER" 60 || die "not back on $VER"
	one_copy
	[ ! -d "$JOB" ] || die "the job is still there"
	manifest | grep -v nocatfarm-service.sh > "$WORK/after"
	diff "$WORK/before" "$WORK/after" > /dev/null || { diff "$WORK/before" "$WORK/after" | head; die "the files aren't the old ones"; }
	say "old files back byte for byte; trying again works, it wasn't skipped"
	cp "$SRC/tools/linux/nocatfarm-service.sh" "$HERE/nocatfarm-service.sh"; chown "$USR:" "$HERE/nocatfarm-service.sh"
	sleep 3
	pid=$(main_pid)
	cmd 'update now' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
}

scn_stop_mid_swap() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	slow_helper
	manifest | grep -v nocatfarm-service.sh > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	wait_half_way
	systemctl stop "$UNIT"
	sleep 8
	no_copy
	say "stopped half way stays stopped; starting it puts the old files back"
	systemctl start "$UNIT"
	wait_log 'was stopped half way' 300 || die "it never said it was stopped half way (on $(ver))"
	ready || die "not up"
	[ "$(ver)" = "$VER" ] || die "on $(ver)"
	one_copy
	manifest | grep -v nocatfarm-service.sh > "$WORK/after"
	diff "$WORK/before" "$WORK/after" > /dev/null || die "the files aren't the old ones"
}

scn_stop_during_countdown() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	manifest > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	wait_log 'restarting in' 60 || die "the countdown never started"
	sleep 2
	systemctl stop "$UNIT"
	sleep 8
	no_copy
	[ ! -f "$JOB/ready" ] || die "stopped during the countdown, and it's still going in"
	systemctl start "$UNIT"
	ready || die "not up"
	[ "$(ver)" = "$VER" ] || die "on $(ver)"
	one_copy
	[ ! -d "$JOB" ] || die "a job left"
}

# Between the exit for the update and the restart systemd waits RestartSec: stopped in that moment, the update waits.
stop_while_staged() {
	cmd 'update now' | sed 's/^/    /'
	for _ in $(seq 1 1500); do [ "$(systemctl show -p SubState --value "$UNIT")" = auto-restart ] && break; sleep 0.1; done
	[ "$(systemctl show -p SubState --value "$UNIT")" = auto-restart ] || die "never saw it waiting to restart"
	[ -f "$JOB/ready" ] || die "exited without an update staged"
	systemctl stop "$UNIT"
	sleep 10
	no_copy
	[ -f "$JOB/ready" ] || die "the staged update went"
}

scn_stop_while_staged() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	pid=$(main_pid)
	stop_while_staged
	say "stopped with the update waiting, and it stays stopped; starting it puts it in"
	systemctl start "$UNIT"
	went_in 9.9.9 "$pid"
}

scn_hand_install_while_staged() {
	[ -f "$ZIPS/hand.zip" ] || die "no hand.zip"
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	stop_while_staged
	say "another version unzipped over the folder by hand while it waited: that one stays"
	unzip -qo "$ZIPS/hand.zip" -d "$HERE"
	chown -R "$USR:" "$HERE"
	systemctl start "$UNIT"
	wait_log 'update dropped' 120 || die "it never said it dropped the update"
	ready || die "not up"
	[ "$(ver)" = 9.9.8 ] || die "on $(ver), not the 9.9.8 put in by hand"
	one_copy
	[ ! -d "$JOB" ] || die "a job left"
}

scn_stop_on_trial() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	cmd 'update now' | sed 's/^/    /'
	wait_ver 9.9.9 120 || die "never reached 9.9.9"
	[ "$(cat "$JOB/phase" 2>/dev/null)" = trial ] || die "not on trial"
	say "a clean stop while it's being tried out is somebody stopping it, not a crash"
	systemctl stop "$UNIT"
	sleep 5
	no_copy
	systemctl start "$UNIT"
	ready || die "not up"
	[ "$(ver)" = 9.9.9 ] || die "put back after a clean stop (on $(ver))"
	one_copy
	wait_committed 10 || die "the job is still there"
}

scn_older_unit_is_by_hand() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	test -f "$HERE/.nocatfarm-update/unit-ok" || die "the helper left no mark under the new unit"
	say "the same folder under the old unit: the mark from the last start doesn't count"
	write_old_unit
	systemctl daemon-reload
	systemctl restart "$UNIT"
	ready || die "not up"
	pid=$(main_pid)
	cmd 'update now' | tee "$WORK/out" | sed 's/^/    /'
	grep -q 'nocatfarm.service' "$WORK/out" || die "it should have said how"
	curl -sf "http://127.0.0.1:$PORT/api/status" | grep -q '"CanSelfUpdate":true' && die "the dashboard offers to update" || true
	sleep 25
	[ "$(main_pid)" = "$pid" ] || die "it exited"
	[ ! -d "$JOB" ] || die "something was staged"
	[ "$(ver)" = "$VER" ] || die "on $(ver)"
	one_copy
}

scn_folder_not_writable_is_by_hand() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	systemctl stop "$UNIT"
	chown -R root: "$HERE"
	chmod -R go-w "$HERE"
	rm -rf "${HERE:?}/.nocatfarm-update"
	systemctl start "$UNIT"
	ready || die "it must still start from a folder it can't write"
	pid=$(main_pid)
	cmd 'update now' | tee "$WORK/out" | sed 's/^/    /'
	grep -q 'nocatfarm.service' "$WORK/out" || die "it should have said how"
	sleep 20
	[ "$(main_pid)" = "$pid" ] || die "it exited"
	one_copy
}

scn_never_gives_up_restarting() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	systemctl stop "$UNIT"
	mv "$HERE/nocatFarm" "$HERE/nocatFarm.real"
	printf '#!/bin/sh\nexit 3\n' > "$HERE/nocatFarm"; chmod 755 "$HERE/nocatFarm"; chown "$USR:" "$HERE/nocatFarm"
	cat > "$DROPIN/slow.conf" <<EOF
[Service]
RestartSec=200ms
EOF
	systemctl daemon-reload
	systemctl start "$UNIT"
	sleep 25
	n=$(restarts)
	say "$n restarts in 25 seconds"
	[ "$n" -gt 20 ] || die "only $n restarts"
	[ "$(systemctl show -p Result --value "$UNIT")" != start-limit-hit ] || die "systemd gave up"
	mv -f "$HERE/nocatFarm.real" "$HERE/nocatFarm"
	ready 60 || die "it didn't come back by itself once the program was fixed"
	rm -f "$DROPIN/slow.conf"; systemctl daemon-reload
	one_copy
}

scn_second_copy_refused() {
	fresh "$ZIPS/cur.zip" new
	out=$(timeout 60 su -s /bin/sh "$USR" -c "$HERE/nocatFarm --no-gui --path $DATA" < /dev/null 2>&1); code=$?
	say "second copy: exit $code"
	[ "$code" -ne 0 ] && [ "$code" -ne 124 ] || die "second copy: exit $code"
	echo "$out" | grep -q 'already running for this folder' || die "it didn't say another copy has the folder"
	one_copy
}

# ── reboots and power cuts: on a machine that can take them. Each has a first half and a second half, after the boot.
offer_and_enable() {
	offer good 9.9.9
	fresh "$ZIPS/cur.zip" new
	systemctl enable "$UNIT"
	manifest > "$WORK/before"
}

check_booted() {   # expected version
	systemctl is-enabled -q "$UNIT" || die "not enabled"
	ready 240 || die "didn't come back on boot"
	[ "$(ver)" = "$1" ] || { wait_ver "$1" 120 || die "on $(ver), not $1"; }
	one_copy
	say "$(uptime -p 2>/dev/null): up on $1, $(pgrep -x nocatFarm | wc -l) copy"
}

scn_reboot_after_update_a() {
	offer_and_enable
	pid=$(main_pid)
	cmd 'update now' | sed 's/^/    /'
	went_in 9.9.9 "$pid"
	sync
}
scn_reboot_after_update_b() { check_booted 9.9.9; }

scn_power_cut_mid_swap_a() {
	offer_and_enable
	slow_helper
	manifest | grep -v nocatfarm-service.sh > "$WORK/before"
	cmd 'update now' | sed 's/^/    /'
	wait_half_way
	power_cut
}
scn_power_cut_mid_swap_b() {
	check_booted "$VER"
	wait_log 'was stopped half way' 120 || die "it never said it was stopped half way"
	[ ! -d "$JOB" ] || die "the job is still there"
	manifest | grep -v nocatfarm-service.sh > "$WORK/after"
	diff "$WORK/before" "$WORK/after" > /dev/null || { diff "$WORK/before" "$WORK/after" | head; die "the files aren't the old ones"; }
	say "old files back byte for byte after the power cut"
}

scn_power_cut_on_trial_a() {
	offer_and_enable
	cmd 'update now' | sed 's/^/    /'
	wait_ver 9.9.9 120 || die "never reached 9.9.9"
	[ "$(cat "$JOB/phase" 2>/dev/null)" = trial ] || die "not on trial"
	power_cut
}
scn_power_cut_on_trial_b() {
	check_booted "$VER"
	[ ! -d "$JOB" ] || die "the job is still there"
	manifest > "$WORK/after"
	diff "$WORK/before" "$WORK/after" > /dev/null || die "the files aren't the old ones"
	if [ "${NF_TEST_POWER:-}" = host ]; then
		# A container's kernel is the host's, so its boot id doesn't change: read as a crash. A real power cut changes it.
		wait_log 'update undone: 9.9.9' 60 || die "it never said it was undone"
	else
		wait_log 'was stopped half way' 60 || die "it never said it was stopped half way"
		cmd 'update' | tee "$WORK/out" | sed 's/^/    /'
		in_log 'Skipping' && die "that version was skipped" || true
	fi
}

scn_power_cut_while_staged_a() {
	offer_and_enable
	stop_while_staged
	power_cut
}
scn_power_cut_while_staged_b() {
	check_booted 9.9.9
	wait_committed 90 || die "the job is still there"
	[ "$(ver)" = 9.9.9 ] || die "went back"
	one_copy
}

# ── running them ─────────────────────────────────────────────────────────────
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 2; }
[ -d /run/systemd/system ] || { echo "no systemd running here"; exit 2; }
for z in cur good; do [ -f "$ZIPS/$z.zip" ] || { echo "no $z.zip in $ZIPS"; exit 2; }; done

prepare_machine

if [ "$MODE" = one ]; then
	set -e
	echo "== $SCN"
	"scn_$SCN"
	echo "PASS: $SCN"
	exit 0
fi

ALL="${NF_SCENARIOS:-update_now update_accept dashboard_button update_file night_install bad_exit3 bad_exit0 bad_late_crash bad_not_a_program bad_hangs_and_ignores_stop kill9_new_version_on_trial kill9_mid_swap stop_mid_swap stop_during_countdown stop_while_staged hand_install_while_staged stop_on_trial first_update_from_previous_release older_unit_is_by_hand folder_not_writable_is_by_hand never_gives_up_restarting second_copy_refused}"
results=""
failed=0
for s in $ALL; do
	started=$(date +%s)
	if bash "$0" one "$s" "$ZIPS"; then
		results="$results
PASS  $s ($(($(date +%s) - started))s)"
	else
		results="$results
FAIL  $s ($(($(date +%s) - started))s)"
		failed=$((failed + 1))
	fi
done
systemctl stop "$UNIT" 2>/dev/null
pkill -f "http.server $FEED" 2>/dev/null
echo "== results:$results"
[ "$failed" = 0 ]
