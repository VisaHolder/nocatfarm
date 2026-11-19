#!/bin/sh
# gamer.farm's program was called nocatFarm before 2.0.1. This file goes in under that old name, only in the zips made
# for the updater of a version from before 2.0.1 (gamer.farm-old-updater-v*): that updater starts nocatFarm after an
# update, and a systemd unit written then starts it too. It starts the program as it's called now, as the same process.
# gamer.farm deletes it again once nothing needs it - it stays while a unit, a script or a cron line still starts it.
here=$(cd "$(dirname "$0")" && pwd -P) || exit 1
chmod +x "$here/gamerFarm" 2>/dev/null
GAMERFARM_STARTED_AS=nocatFarm
export GAMERFARM_STARTED_AS
# On a Mac the old updater knows its new copy by the program its command line starts with: it keeps the old name there.
if [ "$(uname)" = Darwin ]; then exec -a "$here/nocatFarm" "$here/gamerFarm" "$@"; fi
exec "$here/gamerFarm" "$@"
