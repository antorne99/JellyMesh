#!/usr/bin/env bash
# No remote token and no remote address reach a client or a log (§5). The tokens are the ones that really crossed the gates,
# so a check that found none of them would prove nothing: it fails then. Answers that clients got are kept in $WORK by the
# other scripts. usage: MEDIA=... WORK=... leaks.sh [output of a proof script]...
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
LOOK=$WORK/leaks
rm -rf "$LOOK"
mkdir -p "$LOOK"

cat "$WORK"/gate-*.tokens 2>/dev/null | sort -u >"$LOOK.tokens"
check "the gates saw the tokens of the remotes" true "$([ -s "$LOOK.tokens" ] && echo true || echo false)"

gateway=$(docker exec "$PREFIX-b" getent hosts "$HOST_GATE" | awk '{print $1; exit}')
check "the address of the gate is known" true "$([ -n "$gateway" ] && echo true || echo false)"
addresses="$HOST_GATE|:($GATE_A|$GATE_B|$GATE_STOCK)|${gateway//./\\.}"

no_token() { check "no remote token in $1" 0 "$(cat "${@:2}" 2>/dev/null | grep -c -F -f "${TOKENS:-$LOOK.tokens}")"; }
no_address() { check "no remote address in $1" 0 "$(cat "${@:2}" 2>/dev/null | grep -c -E "$addresses")"; }
clean() { # <what> <file>...: both checks, and there has to be something to check
  check "$1 were kept" true "$([ "$(cat "${@:2}" 2>/dev/null | wc -c)" -gt 0 ] && echo true || echo false)"
  no_token "$@"
  no_address "$@"
}

echo "--- what clients received"
clean "PlaybackInfo answers" "$WORK"/open-*
clean "refusal messages" "$WORK"/ws-*
clean "playlists" "$WORK"/seen-*

mario=$(curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"leaks\", DeviceId=\"$PREFIX-leaks\", Version=\"0.1\"" \
  -H 'Content-Type: application/json' -d '{"Username":"mario","Pw":"jellymesh-test"}' "http://127.0.0.1:$PORT_B/Users/AuthenticateByName" | jq -r .AccessToken)
as_client() { curl -sS -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"leaks\", DeviceId=\"$PREFIX-leaks\", Version=\"0.1\", Token=\"$mario\"" "http://127.0.0.1:$PORT_B$1"; }
as_client '/Items?Recursive=true&IncludeItemTypes=Movie,Series,Season,Episode&Fields=Path,MediaSources,MediaStreams,ProviderIds,ExternalUrls,Overview' >"$LOOK/client-items"
check "the listing of a client is not empty" true "$(jq '.Items | length > 0' "$LOOK/client-items")"
clean "the items a client lists" "$LOOK/client-items"
as_client /Sessions >"$LOOK/client-sessions"
clean "the sessions a client lists" "$LOOK/client-sessions"
as_client /System/Info/Public >"$LOOK/client-info"
clean "the public server info" "$LOOK/client-info"

echo "--- logs and files of the servers"
for s in a b; do
  name=$(container_of "$s")
  docker logs "$name" >"$LOOK/$s-stdout" 2>&1
  docker exec "$name" sh -c 'cat /config/log/* 2>/dev/null' >"$LOOK/$s-log-files"
  docker exec "$name" sh -c 'find /federata -type f \( -name "*.strm" -o -name "*.nfo" \) -exec cat {} +' >"$LOOK/$s-federata"
  docker exec "$name" sh -c 'cat /config/plugins/configurations/*JellyMesh*.xml 2>/dev/null' >"$LOOK/$s-config"
  # The tokens of the other servers. Its own are not remote here: stock logs one it issued when a login replaces it.
  find "$WORK" -maxdepth 1 -name 'gate-*.tokens' ! -name "gate-$s.tokens" -exec cat {} + | sort -u >"$LOOK.$s.tokens"
  TOKENS=$LOOK.$s.tokens
  check "$s logged something" true "$([ -s "$LOOK/$s-stdout" ] && echo true || echo false)"
  clean "the output of $s" "$LOOK/$s-stdout"
  clean "the log files of $s" "$LOOK/$s-log-files"
  clean "the .strm and .nfo of $s" "$LOOK/$s-federata"
  no_token "the plugin configuration of $s" "$LOOK/$s-config"
  TOKENS=
done

no_token "the gate logs" "$WORK"/gate-*.log

echo "--- the output of the proofs"
for output in "$@"; do
  clean "$(basename "$output")" "$output"
done

exit "$FAILED"
