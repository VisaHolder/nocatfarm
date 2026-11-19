#!/bin/bash
# gamer.farm on a Mac. Double-click this to start it; it opens in Terminal and the dashboard opens in your browser.
# Keep the Terminal window open (minimise it) - closing it stops gamer.farm.
cd "$(dirname "$0")" || exit 1

# Restarted by an update: open can't hand a program its options, so the updater leaves them in restart-args.txt, one
# per line (a --path, --no-gui). Read once and removed, and only when fresh, so a double-click later starts plain.
if [ "$#" -eq 0 ] && [ -f restart-args.txt ]; then
	if [ -n "$(find restart-args.txt -mmin -5 2>/dev/null)" ]; then
		while IFS= read -r a || [ -n "$a" ]; do set -- "$@" "$a"; done < restart-args.txt
	fi
	rm -f restart-args.txt
fi

# A Mac marks everything downloaded as "from the internet" and won't run it. This folder is yours, so the mark comes
# off everything in it - otherwise gamerFarm and its libraries would each be refused.
xattr -dr com.apple.quarantine . 2>/dev/null
chmod +x gamerFarm 2>/dev/null

# So an update can open the new version the same way: in a Terminal window of its own.
export GAMERFARM_STARTER=start.command

# 2.0.1: just updated from a version before 2.0.1, its updater looks for the program by its old name - through the small
# script under that name while it's there (it keeps that name on the command line). The app deletes it once it stays.
if [ -f nocatFarm ] && [ "$(head -c 2 nocatFarm)" = '#!' ]; then exec /bin/sh "$PWD/nocatFarm" "$@"; fi

# The full path, so the updater can tell whether the new version is running.
exec "$PWD/gamerFarm" "$@"
