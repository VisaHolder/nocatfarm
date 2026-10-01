#!/bin/bash
# nocat.farm on a Mac. Double-click this to start it; it opens in Terminal and the dashboard opens in your browser.
# Keep the Terminal window open (minimise it) - closing it stops nocat.farm.
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
# off everything in it - otherwise nocatFarm and its libraries would each be refused.
xattr -dr com.apple.quarantine . 2>/dev/null
chmod +x nocatFarm 2>/dev/null

# So an update can open the new version the same way: in a Terminal window of its own.
export NOCATFARM_STARTER=start.command

# The full path, so the updater can tell whether the new version is running.
exec "$PWD/nocatFarm" "$@"
