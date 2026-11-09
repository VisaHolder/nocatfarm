#!/bin/sh
# nocat.farm's helper for running as a systemd service: it puts an update in while nothing is running, inside what
# systemd supervises, so there is never a second copy and never a script that outlives the service.
#
#   nocatfarm-service.sh prepare   ExecStartPre: runs before every start.
#   nocatfarm-service.sh watch     ExecStartPost: runs after every start, and waits only while an update is on trial.
#
# How an update goes. nocat.farm unpacks and checks it in .nocatfarm-update/job in this folder, signs the accounts out,
# writes job/ready, and exits with code 75. systemd restarts it, and "prepare" moves the new files in, keeping what it
# replaced in job/backup. The new version then has three minutes to say it's fine, in job/started.txt.
#
# Every step leaves job/phase saying how far it got, so whatever stops it half way (a crash, kill -9, a power cut, a
# stop) is finished by the next "prepare":
#   no job/ready     the update never got as far as asking: its folder goes, nothing was touched
#   no job/phase     nothing touched yet: the update goes in
#   swapping         stopped half way through the files: the old ones go back
#   trial            the new version was tried. It said "ok": it stays. Anything else: the old one goes back
#
# Files are moved, never copied: job is on the same disk, a move can't half happen and needs no free space. This file
# itself is only replaced once the new version has said it's fine, so a bad release can't take away the way back.
#
# Runs as the service's own user, who owns this folder. Never give it root (no "+" in the unit): this folder is writable
# by that user, so root running a script from it would hand them root.

self=nocatfarm-service.sh
here=$(cd "$(dirname "$0")" && pwd -P) || exit 1
box=$here/.nocatfarm-update
job=$box/job

say() { printf 'nocat.farm update: %s\n' "$*"; }

# One small file the app left in the job, or nothing.
from_job() { cat "$job/$1" 2>/dev/null; }

set_phase() {
	printf '%s\n' "$1" > "$job/phase.new" && mv -f "$job/phase.new" "$job/phase" || return 1
	sync
}

# Left where the version that starts next finds it, and says it (SelfUpdate.AnnounceIfJustUpdated).
leave_note() {
	f=$(from_job fail-path)
	if [ -n "$f" ]; then printf '%s\n' "$1" > "$f"; fi
}

# In one move first: a job half deleted must never look like one to act on.
forget_job() {
	v=$(from_job verify-path)
	if [ -n "$v" ]; then rm -f "${v:?}"; fi
	rm -rf "${box:?}/old-job"
	mv -f "$job" "$box/old-job" && rm -rf "${box:?}/old-job"
}

# Process ids of other copies of nocat.farm running from this folder, by the program each one runs.
from_here() {
	for d in /proc/[0-9]*; do
		t=$(readlink "$d/exe" 2>/dev/null) || continue
		if [ "$t" = "$here/nocatFarm" ]; then echo "${d#/proc/}"; fi
	done
}

# Every file in a folder into this one. A file already the same is left: going back after a failed try, a file in a
# folder this user can't write was never changed, and must not count as a failure to put it back.
install_from() {
	(cd "$1" && find . \( -type f -o -type l \)) | while IFS= read -r f; do
		f=${f#./}
		if [ "$f" = "$self" ]; then continue; fi
		if cmp -s "$1/$f" "$here/$f"; then continue; fi
		mkdir -p "$(dirname "$here/$f")" || exit 1
		mv -f "$1/$f" "$here/$f" || exit 1
	done
}

# Stopped half way through this, the next "prepare" carries on from what's left in job/backup.
put_back() {
	if ! install_from "$job/backup"; then
		say "couldn't put the old version back yet - trying again at the next start (disk full? files owned by someone else?)"
		return 1
	fi
	if [ -f "$job/added.txt" ]; then
		while IFS= read -r f; do
			if [ -n "$f" ] && [ "$f" != "$self" ]; then rm -f "${here:?}/${f:?}"; fi
		done < "$job/added.txt"
	fi
	chmod +x "$here/nocatFarm" 2>/dev/null
	sync
}

swap_in() {
	other=$(from_here | head -n 1)
	if [ -n "$other" ]; then
		say "another nocat.farm (process $other) is running from this folder - leaving it alone"
		leave_note "busy $other"
		forget_job
		return 0
	fi

	# Stopped with an update waiting, and a version put in by hand before starting again: that one stays.
	if [ -f "$job/backup/nocatFarm.dll" ] && [ -f "$here/nocatFarm.dll" ] && ! cmp -s "$job/backup/nocatFarm.dll" "$here/nocatFarm.dll"; then
		say "the program in this folder changed while the update waited - leaving it alone"
		leave_note changed
		forget_job
		return 0
	fi

	sync
	set_phase swapping || return 1
	say "putting in $(from_job tag)"
	# test point: swapping
	if ! install_from "$job/new"; then
		say "couldn't put the new files in - putting the old ones back"
		put_back || return 1
		leave_note 8
		forget_job
		return 0
	fi
	chmod +x "$here/nocatFarm" 2>/dev/null

	# The new version answers in the file this names, read only while it's fresh - and the service may have been stopped
	# for days with the update waiting.
	v=$(from_job verify-path)
	if [ -n "$v" ]; then touch "$v"; fi
	cat /proc/sys/kernel/random/boot_id > "$job/boot" 2>/dev/null
	set_phase trial
}

# It said it's fine: done. This file goes in now, checked first - a broken one would stop every start from here on.
commit() {
	say "$(from_job tag) is in"
	if [ -f "$job/new/$self" ] && sh -n "$job/new/$self" 2>/dev/null; then
		mv -f "$job/new/$self" "$here/$self"
	fi
	forget_job
}

# The service started again with the new version on trial.
settle_trial() {
	if [ ! -f "$job/failed" ] && [ -s "$job/started.txt" ] && ! grep -q '^crashed' "$job/started.txt"; then
		commit
		return 0
	fi

	say "$(from_job tag) didn't start properly - putting the old version back"
	put_back || return 1

	# The machine went down while it was on trial: nothing says this version is to blame, so it isn't skipped.
	was=$(from_job boot)
	now=$(cat /proc/sys/kernel/random/boot_id 2>/dev/null)
	if [ ! -f "$job/failed" ] && [ -n "$was" ] && [ -n "$now" ] && [ "$was" != "$now" ]; then
		leave_note "interrupted $(from_job tag)"
	else
		leave_note "crashed $(from_job tag)"
	fi
	forget_job
}

# What tells nocat.farm the unit it runs under has these pieces: this very start's id, written by this script. An older
# unit never runs it, so the app there keeps saying "update by hand" and never exits for an update. Also not written
# when this folder can't be written, where an update couldn't go in anyway.
stamp() {
	if [ -z "$INVOCATION_ID" ]; then return 0; fi
	t=$here/.nf-write-test.$$
	if mkdir -p "$box" 2>/dev/null && chmod 700 "$box" 2>/dev/null && (: > "$t") 2>/dev/null; then
		rm -f "${t:?}"
		printf '%s\n' "$INVOCATION_ID" > "$box/unit-ok.new" && mv -f "$box/unit-ok.new" "$box/unit-ok"
	else
		rm -f "${box:?}/unit-ok" 2>/dev/null
		say "this service's user can't write $here, so updates go by hand"
	fi
}

prepare() {
	rm -rf "${box:?}/old-job"

	if [ -d "$job" ]; then
		if [ ! -f "$job/ready" ]; then
			rm -rf "${job:?}"
		else
			case "$(from_job phase)" in
				"") swap_in || exit 1 ;;
				trial) settle_trial || exit 1 ;;
				*)
					say "an update was stopped half way - putting the old version back"
					put_back || exit 1
					leave_note "interrupted $(from_job tag)"
					forget_job
					;;
			esac
		fi
	fi

	stamp
	exit 0
}

# Marked first: asked to stop now, the new version closes cleanly and says "ok" as it goes - which must not undo this.
trial_failed() {
	: > "$job/failed"
	say "$(from_job tag) didn't say it's fine in time - stopping it"
	exit 1
}

watch() {
	if [ ! -f "$job/ready" ] || [ "$(from_job phase)" != trial ]; then exit 0; fi

	waited=0
	while [ -d "$job" ]; do
		if [ -s "$job/started.txt" ]; then
			if grep -q '^crashed' "$job/started.txt"; then trial_failed; fi
			commit
			exit 0
		fi
		if [ "$waited" -ge 180 ]; then trial_failed; fi
		if [ -n "$MAINPID" ] && ! kill -0 "$MAINPID" 2>/dev/null && [ ! -s "$job/started.txt" ]; then trial_failed; fi
		sleep 2
		waited=$((waited + 2))
	done
	exit 0
}

case "$1" in
	prepare) prepare ;;
	watch) watch ;;
	*) echo "usage: $0 prepare|watch" >&2; exit 2 ;;
esac
