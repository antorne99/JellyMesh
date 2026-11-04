#!/usr/bin/env bash
# Decision 17 end to end: the local ffmpeg of the fallback reads the signed .strm, in real jellyfin-web, also after the key
# is lost and the next sync has signed everything again. Run after endpoint.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

login local
login remote
uid=$(user_id remote jellymesh)
policy=$(get remote "/Users/$uid" | jq -c '.Policy | .EnableVideoPlaybackTranscoding = false | .EnableAudioPlaybackTranscoding = false')
post remote "/Users/$uid/Policy" "$policy" >/dev/null
docker restart jm-play-local >/dev/null # Forgets the cached policy of the remote.
sleep 3
wait_ready local
login local
viewer_login
tears=$(stub_item local "Tears of Steel")

played() { # played <capture>: the time the video reached
  grep -a '^PROGRESS' "$WORK/web/$1.out" | sed -E "s/.*'t': ([0-9.]+).*/\1/"
}

run() { # run <capture>
  web "$tears" play "$1" 14 | tee "$WORK/web/$1.out" | grep -aE '^(STARTED|PROGRESS|DIALOGS|RES (4|5))' || true
}

docker exec jm-play-local sh -c 'rm -f /config/log/FFmpeg.*'
run fallback-signed
got=$(played fallback-signed)
check "the video advances through the local transcode" 1 "$(awk -v t="${got:-0}" 'BEGIN { print (t > 1) ? 1 : 0 }')"
ffmpeg=$(docker exec jm-play-local sh -c 'cat /config/log/FFmpeg.* 2>/dev/null')
check "ffmpeg opened the signed URL" 1 "$(echo "$ffmpeg" | grep -c -m1 'JellyMesh/Play/[0-9a-f]*?sig=[0-9a-f]\{64\}')"
check "ffmpeg never saw the remote" 0 "$(echo "$ffmpeg" | grep -c '18431\|host.docker.internal' || true)"

# The key is lost.
docker exec jm-play-local sh -c 'rm -f "$(find /config -name strm-signing.key)"'
docker restart jm-play-local >/dev/null
sleep 3
wait_ready local
login local
viewer_login
old=$(stub_url local "$tears")
ids=$(get local '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -c '[.Items[].Id] | sort')
check "before the next sync the old signature is refused" 403 "$(docker exec jm-play-local curl -s -o /dev/null -w '%{http_code}' "$old")"
sync_now local
new=$(stub_url local "$tears")
check "the next sync signs the stub again" 1 "$([ "$new" != "$old" ] && echo 1 || echo 0)"
check "the file agrees" "$new" "$(strm_text local "Tears of Steel")"
check "the new signature opens" 206 "$(docker exec jm-play-local curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "$new")"
check "no item changed" "$ids" "$(get local '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -c '[.Items[].Id] | sort')"

docker exec jm-play-local sh -c 'rm -f /config/log/FFmpeg.*'
run fallback-resigned
got=$(played fallback-resigned)
check "the fallback plays after signing again, with no scan" 1 "$(awk -v t="${got:-0}" 'BEGIN { print (t > 1) ? 1 : 0 }')"

# Back to normal for what follows.
login remote
policy=$(get remote "/Users/$uid" | jq -c '.Policy | .EnableVideoPlaybackTranscoding = true | .EnableAudioPlaybackTranscoding = true')
post remote "/Users/$uid/Policy" "$policy" >/dev/null
exit $FAILED
