#!/bin/sh
# Signing in from outside, end to end, against a running copy - the same on Linux, a Mac and Windows (Git's sh):
#   the security headers; a wrong password from the internet; five of them switching "Open from anywhere" off (the
#   default), after which even the right password from outside is refused while home still gets in; the visitor log;
#   and turning it back on opening it again.
# The internet is played by X-Forwarded-For from this machine: nocat.farm only believes that header from loopback,
# which is exactly how a proxy on the same PC reaches it - or from a proxy listed in WebTrustedProxies, which is how
# Docker (reached from its own network, not loopback) is checked.
#   sh tests/security-check.sh http://127.0.0.1:7242 path/to/config [the password it already has]
set -e
B="$1"
CONFIG="$2"
PW="${3:-security-check-$(date +%s)-password}"
fails=0

check() {   # name, then the test as a command
	name="$1"
	shift
	if "$@"; then echo "PASS  $name"; else echo "FAIL  $name"; fails=$((fails + 1)); fi
}

code() {   # status of a sign-in: forwarded-for (or "" for this PC), password
	if [ -n "$1" ]; then
		curl -s -o body.txt -w '%{http_code}' -X POST "$B/api/login" -H 'Content-Type: application/json' -H "X-Forwarded-For: $1" -d "{\"Password\":\"$2\"}"
	else
		curl -s -o body.txt -w '%{http_code}' -X POST "$B/api/login" -H 'Content-Type: application/json' -d "{\"Password\":\"$2\"}"
	fi
}

cmd() {   # a command as the signed-in owner
	curl -s -X POST "$B/api/command" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "{\"Line\":\"$1\"}"
}

# No password yet, so this PC may set one - the way the Phone page does. (Docker has one from its .env already.)
if [ -z "$3" ]; then
	curl -sf -X POST "$B/api/command" -H 'Content-Type: application/json' -d "{\"Line\":\"set WebPassword $PW\"}" > /dev/null
fi

curl -sI "$B/" > headers.txt
check "headers: never inside another site's frame" grep -qi 'X-Frame-Options: DENY' headers.txt
check "headers: no guessed file types, no referrer" sh -c "grep -qi 'X-Content-Type-Options: nosniff' headers.txt && grep -qi 'Referrer-Policy: no-referrer' headers.txt"

check "home: the right password signs in" test "$(code '' "$PW")" = 200
TOKEN=$(sed -n 's/.*"token":"\([0-9a-f]*\)".*/\1/p' body.txt)
check "home: a session comes back" test -n "$TOKEN"

check "outside: a wrong password is refused" test "$(code 198.51.100.1 wrong-one)" = 401
code 198.51.100.2 wrong-two > /dev/null
code 198.51.100.3 wrong-three > /dev/null
code 198.51.100.4 wrong-four > /dev/null
check "outside: the 5th wrong one from the internet switches it off" test "$(code 198.51.100.5 wrong-five)" = 403
check "outside: ...and says it's closed, not 'wrong password'" grep -q '"closed"' body.txt
check "outside: even the right password is refused now" test "$(code 203.0.113.9 "$PW")" = 403
check "outside: it's remembered on disk (a restart keeps it shut)" test -f "$CONFIG/state/internet-shut.txt"
check "home: still signs in" test "$(code '' "$PW")" = 200
TOKEN=$(sed -n 's/.*"token":"\([0-9a-f]*\)".*/\1/p' body.txt)

cmd visitors > visitors.txt
check "visitors: the wrong passwords are listed, from the internet" sh -c "grep -q 'wrong password' visitors.txt && grep -q '198.51.100.1' visitors.txt"
check "visitors: switching itself off is listed" grep -q 'closed to the internet' visitors.txt
check "settings: Open from anywhere was switched off and saved" grep -q '"WebRemoteAccess": false' "$CONFIG/nocatFarm.json"

cmd unlock > /dev/null
check "unlock: opens it again (a forward set up by hand needs no Open from anywhere)" test "$(code 203.0.113.10 "$PW")" = 200
check "unlock: the shut is forgotten" test ! -f "$CONFIG/state/internet-shut.txt"

for i in 1 2 3 4 5; do code "198.51.100.3$i" wrong > /dev/null; done
check "outside: five more wrong ones switch it off again" test -f "$CONFIG/state/internet-shut.txt"
check "outside: made-up text for an address is one 'unknown', counted as the internet" sh -c "curl -s -o /dev/null -X POST '$B/api/login' -H 'Content-Type: application/json' -H 'X-Forwarded-For: <b>not an address</b>' -d '{\"Password\":\"x\"}'; true"
cmd visitors > visitors.txt
check "visitors: text a visitor made up never reaches the log" sh -c "! grep -q 'not an address' visitors.txt"
cmd 'set WebRemoteAccess true' > /dev/null
check "turned back on: the right password from outside works again" test "$(code 203.0.113.11 "$PW")" = 200
check "turned back on: the shut is forgotten" test ! -f "$CONFIG/state/internet-shut.txt"
cmd 'set WebRemoteAccess false' > /dev/null

rm -f body.txt headers.txt visitors.txt
if [ "$fails" -gt 0 ]; then echo "$fails failed"; exit 1; fi
echo "all passed"
