#!/bin/sh
# Docker the way the docs say to run it: the real docker-compose.example.yml, a .env with the password and time zone,
# ./config ./logs ./backups next to it. Checked:
#   - without a password in .env, compose refuses to start it;
#   - it comes up healthy (the image's own health check), on the time zone from .env;
#   - a backup lands in ./backups, survives `up -d --force-recreate`, and restores;
#   - signing in from outside through a proxy that isn't loopback (Docker's network), with NOCATFARM_TRUSTED_PROXIES:
#     tests/security-check.sh against the container;
#   - `docker compose down` signs out cleanly.
# Run from the repo root:  sh tests/docker-compose.sh
set -e
PW="a-long-test-password-123"
B="http://127.0.0.1:7242"
W="$PWD/compose-test"

rm -rf "$W" && mkdir -p "$W" && cp docker-compose.example.yml "$W/docker-compose.yml"
# The build context is the repo, wherever the test folder is.
sed -i.bak "s#^    build: \.#    build: $PWD#" "$W/docker-compose.yml" && rm -f "$W/docker-compose.yml.bak"
cd "$W"
mkdir -p config logs backups

# This runner's own user owns the folders (the compose file's "1000:1000" is the usual desktop user), a quick health
# check for the test, and the proxy it's reached through - Docker's network - trusted.
cat > docker-compose.override.yml <<EOF
services:
  nocatfarm:
    user: "$(id -u):$(id -g)"
    environment:
      NOCATFARM_TRUSTED_PROXIES: 172.16.0.0/12
      # A test container never counts itself on nocat.lol: its user-count ping goes to a dead port inside it.
      NOCATFARM_PING_URL: http://127.0.0.1:9/api/farm/ping
    healthcheck:
      interval: 5s
      start_period: 5s
EOF

echo "== without a password"
if docker compose up -d > up.txt 2>&1; then echo "FAIL: it started without a password"; cat up.txt; exit 1; fi
cat up.txt
grep -q 'NOCATFARM_WEB_PASSWORD' up.txt
echo "PASS: compose refuses to start without NOCATFARM_WEB_PASSWORD, and says what to put in .env"

echo "NOCATFARM_WEB_PASSWORD=$PW" > .env
echo "TZ=Asia/Tokyo" >> .env
docker compose up -d --build

up() { for i in $(seq 1 90); do curl -s -o /dev/null -w '%{http_code}' "$B/api/ping" | grep -q 200 && return 0; sleep 1; done; return 1; }
healthy() { for i in $(seq 1 90); do [ "$(docker inspect -f '{{.State.Health.Status}}' nocatfarm)" = "healthy" ] && return 0; sleep 2; done; return 1; }
token() { curl -sf -X POST "$B/api/login" -H 'Content-Type: application/json' -d "{\"Password\":\"$PW\"}" | sed -E 's/.*"token":"([^"]+)".*/\1/'; }
cmd() { curl -sf -X POST "$B/api/command" -H 'Content-Type: application/json' -H "Authorization: Bearer $1" -d "{\"Line\":\"$2\"}"; echo; }

up
healthy || { docker inspect -f '{{json .State.Health}}' nocatfarm; exit 1; }
echo "PASS: up and healthy (the image's --ping health check)"
test "$(docker compose exec -T nocatfarm printenv TZ)" = "Asia/Tokyo"
echo "PASS: on the time zone from .env"

echo "== a backup survives a re-create, and restores"
TOKEN=$(token)
cmd "$TOKEN" backup | tee backup.txt
ls -la backups
zip=$(ls backups/*.zip | head -n 1)
test -f "$zip"
[ "$(stat -c %a "$zip")" = "600" ] || { echo "FAIL: the backup is $(stat -c %a "$zip"), not 600"; exit 1; }
echo "PASS: the backup is in ./backups, owner-only"
cmd "$TOKEN" 'set UpdateCheckHours 7' > /dev/null
docker compose up -d --force-recreate
up
test -f "$zip"
TOKEN=$(token)
check=$(curl -sf -X POST "$B/api/restore/check" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/zip' -H "Origin: $B" --data-binary "@$zip")
echo "$check" | head -c 300; echo
restore=$(echo "$check" | sed -E 's/.*"Token":"([^"]+)".*/\1/')
curl -sf -X POST "$B/api/restore/apply" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -H "Origin: $B" -d "{\"Token\":\"$restore\"}" | head -c 300; echo
grep -Eq '"UpdateCheckHours": *2,?[[:space:]]*$' config/nocatFarm.json || { echo "FAIL: the restore didn't put the settings back"; grep UpdateCheckHours config/nocatFarm.json; exit 1; }
test "$(ls backups/*.zip | wc -l)" -ge 2
echo "PASS: the backup was still there after the re-create, restored, and kept a copy of what it replaced"

echo "== signing in from outside, through Docker's network (trusted proxy)"
cd ..
sh tests/security-check.sh "$B" "$W/config" "$PW"
cd "$W"

echo "== docker compose down"
docker compose down
grep -rq 'shutting down' logs/
echo "PASS: signed out cleanly"
