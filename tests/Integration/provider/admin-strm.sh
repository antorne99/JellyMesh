#!/usr/bin/env bash
# Row 570: an admin's .strm on a, shared with b, plays on a client of b with a direct-capable profile through the transcode
# of a; its direct streams stay 404 for the peer, and no answer or log of b gives the URL it reads. Runs after up.sh and
# federate.sh with STOCK=own: the .strm reads a film of the private stock server through its gate, with a token of its own.
# ROUND names the build under test; a round after one named old also compares what must not change.
# usage: MEDIA=... WORK=... STOCK=own ROUND=<old|new> admin-strm.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
ROUND=${ROUND:-new}
TITLE="JM Strm Admin"
FOLDER="/extra/film/$TITLE (2026)"
OUT=$WORK/strm-$ROUND
rm -rf "$OUT"
mkdir -p "$OUT"
[ "$STOCK" = own ] || { echo "run with STOCK=own" >&2; exit 1; }

# The film of the stock server and a login that only the .strm holds.
login s
film=$(get s '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | endswith(".mkv"))][0].Id')
secret=$(curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"strm\", DeviceId=\"$PREFIX-strm\", Version=\"0.1\"" \
  -H 'Content-Type: application/json' -d '{"Username":"jellymesh","Pw":"jellymesh-test"}' "http://127.0.0.1:$STOCK_PORT/Users/AuthenticateByName" | jq -r .AccessToken)
url="http://$HOST_GATE:$GATE_STOCK/Videos/$film/stream.mkv?static=true&api_key=$secret"
docker exec "$PREFIX-a" sh -c 'mkdir -p "$1" && printf "%s\n" "$2" > "$1/$3 (2026).strm" && printf "1\n00:00:01,000 --> 00:00:04,000\nSottotitolo di prova\n" > "$1/$3 (2026).it.srt"' sh "$FOLDER" "$url" "$TITLE"
scan a
strm=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$TITLE" '[.Items[] | select(.Path | contains($t))][0].Id')
file=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | (startswith("/media/") and endswith(".mkv")))][0].Id')
check "the .strm is an item of a" true "$([ -n "$strm" ] && [ "$strm" != null ] && echo true || echo false)"

# A limit above 1, so that the probes of the peer and the clients of b do not wait for each other's slot.
share a 4 Film Serie
ptoken=$(peer_token a peer)
# A direct-capable profile that also takes the sidecar subtitles: without them stock transcodes for the subtitles alone.
PROFILE=$(jq -c '.DeviceProfile.SubtitleProfiles = [{Format: "srt", Method: "External"}, {Format: "subrip", Method: "External"}, {Format: "vtt", Method: "External"}]' <<<"$PROFILE_DIRECT")
# give_back: ends what the last answer of the peer opened on a, and gives its slot back.
give_back() {
  local session
  session=$(jq -r '.PlaySessionId // empty' "$WORK/body" 2>/dev/null)
  [ -n "$session" ] || return 0
  as_peer a "$ptoken" DELETE "/Videos/ActiveEncodings?deviceId=$PREFIX-probe&playSessionId=$session" >/dev/null
  as_peer a "$ptoken" DELETE "/JellyMesh/Provider/Streams/$session" >/dev/null
}
# info <token> <item> <profile> [query]: PlaybackInfo on a that names the version. Prints the status; the answer is in $WORK/body.
info() {
  curl -sS -o "$WORK/body" -w '%{http_code}' -X POST -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"$PREFIX-probe\", Version=\"0.1\", Token=\"$1\"" \
    -H 'Content-Type: application/json' -d "$(jq -c --arg v "$2" '. + {MediaSourceId: $v}' <<<"$3")" "http://127.0.0.1:$PORT_A/Items/$2/PlaybackInfo${4:-}"
}
direct_of() { jq -c '.MediaSources[0] | {SupportsDirectPlay, SupportsDirectStream, Transcoding: (.TranscodingUrl != null)}' "$WORK/body"; }
reasons_of() { jq -c '.MediaSources[0] | {Container, Protocol, TranscodeReasons: (.TranscodingUrl // "" | (capture("TranscodeReasons=(?<r>[^&]*)").r? // null))}' "$1" 2>/dev/null; }

echo "--- the peer asks a for the .strm with a direct-capable profile"
check "PlaybackInfo" 200 "$(info "$ptoken" "$strm" "$PROFILE")"
cp "$WORK/body" "$OUT/peer-direct"
echo "what a answered: $(reasons_of "$OUT/peer-direct")"
check "only transcoded" '{"SupportsDirectPlay":false,"SupportsDirectStream":false,"Transcoding":true}' "$(direct_of)"
check "PlaybackInfo asking for direct in the query" 200 "$(info "$ptoken" "$strm" "$PROFILE" '?enableDirectPlay=true&enableDirectStream=true')"
cp "$WORK/body" "$OUT/peer-query"
check "still only transcoded" '{"SupportsDirectPlay":false,"SupportsDirectStream":false,"Transcoding":true}' "$(direct_of)"
give_back
cp "$OUT/peer-direct" "$WORK/body"
: >"$OUT/peer-master"
check "the transcode of a answers the peer" 200 "$(curl -sS -o "$OUT/peer-master" -w '%{http_code}' "http://127.0.0.1:$PORT_A$(jq -r '.MediaSources[0].TranscodingUrl' "$OUT/peer-direct")")"
give_back
for path in "/Videos/$strm/stream?static=true&MediaSourceId=$strm" "/Videos/$strm/stream.mkv?static=true" "/Videos/$strm/stream?Static=TRUE"; do
  check "the direct stream $path stays 404 for the peer" 404 "$(as_peer a "$ptoken" GET "$path")"
done

echo "--- what must not change: a file of a for the peer, the .strm for a user of a"
info "$ptoken" "$file" "$PROFILE" >"$OUT/file-status"
direct_of >>"$OUT/file-status"
give_back
admin_token=$(curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"strm\", DeviceId=\"$PREFIX-strm-admin\", Version=\"0.1\"" \
  -H 'Content-Type: application/json' -d '{"Username":"admin","Pw":"jellymesh-test"}' "http://127.0.0.1:$PORT_A/Users/AuthenticateByName" | jq -r .AccessToken)
info "$admin_token" "$strm" "$PROFILE" >"$OUT/admin-status"
direct_of >>"$OUT/admin-status"
echo "file for the peer: $(tr '\n' ' ' <"$OUT/file-status")"
echo ".strm for the admin of a: $(tr '\n' ' ' <"$OUT/admin-status")"
if [ "$ROUND" != old ] && [ -d "$WORK/strm-old" ]; then
  check "a file of a for the peer as on the old build" "$(cat "$WORK/strm-old/file-status")" "$(cat "$OUT/file-status")"
  check "the .strm for the admin of a as on the old build" "$(cat "$WORK/strm-old/admin-status")" "$(cat "$OUT/admin-status")"
fi

echo "--- a client of b with a direct-capable profile"
sync_now b
scan b
stub=$(movie_on_b "$TITLE")
check "the title reached b" true "$([ -n "$stub" ] && [ "$stub" != null ] && echo true || echo false)"
device=strm-$ROUND
rm -f "$WORK/client-$device"
check "PlaybackInfo on b" 200 "$(open_stream "$device" "$stub" "$PROFILE")"
cp "$WORK/open-$device" "$OUT/client-direct"
echo "what b answered: $(reasons_of "$OUT/client-direct") $(jq -c '.MediaSources[0] | {SupportsDirectPlay, SupportsDirectStream}' "$OUT/client-direct" 2>/dev/null)"
check "b gives a transcoding URL" true "$(jq '.MediaSources[0].TranscodingUrl != null' "$WORK/open-$device" 2>/dev/null || echo false)"
check "its first segments play" "200 200 200" "$(hls_segments "$device" 3 2>/dev/null | tr '\n' ' ' | sed 's/ $//')"
cp "$WORK"/seen-"$device"-* "$OUT/" 2>/dev/null
close_stream "$device"
device=strm-hls-$ROUND
rm -f "$WORK/client-$device"
check "an HLS-only client of b too" 200 "$(open_stream "$device" "$stub" "$PROFILE_HLS")"
cp "$WORK/open-$device" "$OUT/client-hls"
close_stream "$device"

echo "--- no URL of the .strm in what the peer and the clients got, no token of it in the logs of b"
docker logs "$PREFIX-b" >"$OUT/b-stdout" 2>&1
docker exec "$PREFIX-b" sh -c 'cat /config/log/* 2>/dev/null' >"$OUT/b-log-files"
# The logs of b name its own remotes, which go through the same gates: only the token counts there, as in leaks.sh.
for what in peer-direct peer-query peer-master client-direct client-hls b-stdout b-log-files; do
  check "no token of the .strm in $what" 0 "$(grep -c -F "$secret" "$OUT/$what")"
done
for what in peer-direct peer-query peer-master client-direct client-hls; do
  check "no address of the .strm in $what" 0 "$(grep -c -E "$HOST_GATE|:$GATE_STOCK" "$OUT/$what")"
done
check "nor in the playlists of b" 0 "$(cat "$OUT"/seen-* 2>/dev/null | grep -c -E "$secret|$HOST_GATE|:$GATE_STOCK")"

docker exec "$PREFIX-a" rm -rf "$FOLDER"
scan a
share a 1 Film Serie
exit "$FAILED"
