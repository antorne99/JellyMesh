#!/usr/bin/env bash
# Decisions 15 and 17: the minimum interval and the playback times, through the real stock save, a restart and the real
# configuration page. Run after fallback.sh and before localcopy.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

XML=/config/plugins/configurations/Jellyfin.Plugin.JellyMesh.xml
login local

interval_ticks() {
  get local /ScheduledTasks | jq -r '.[] | select(.Name == "JellyMesh incremental sync") | .Triggers[0].IntervalTicks'
}

echo "--- a value below the minimum already saved"
docker stop jm-play-local >/dev/null
docker run --rm --entrypoint sed -v jm-play-local-config:/config "$IMAGE" -i 's|<SyncIntervalSeconds>[0-9]*</SyncIntervalSeconds>|<SyncIntervalSeconds>60</SyncIntervalSeconds>|' "$XML"
check "the file holds 60" 1 "$(docker run --rm --entrypoint grep -v jm-play-local-config:/config "$IMAGE" -c '<SyncIntervalSeconds>60<' "$XML")"
docker start jm-play-local >/dev/null
sleep 3
wait_ready local
login local
check "it runs as 300" 300 "$(get local "/Plugins/$PLUGIN/Configuration" | jq -r .SyncIntervalSeconds)"
for i in $(seq 1 20); do [ "$(interval_ticks)" = 3000000000 ] && break; sleep 1; done
check "the scheduled task runs every 300 seconds" 3000000000 "$(interval_ticks)"
check "the log has a warning" 1 "$(docker logs jm-play-local 2>&1 | grep -c 'SyncIntervalSeconds.*60.*300')"
docker logs jm-play-local 2>&1 | grep 'SyncIntervalSeconds' | tail -1
sync_now local
check "the sync keeps working" false "$(get local /JellyMesh/Admin/Sync | jq -r .IsRunning)"
check "and it succeeded" 0 "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[]? | select(.Failure != null and .Failure != "None")] | length')"

echo "--- new saves"
check "a save below 300 is refused" 400 "$(settings local '.SyncIntervalSeconds = 299')"
check "and leaves the value" 300 "$(get local "/Plugins/$PLUGIN/Configuration" | jq -r .SyncIntervalSeconds)"
check "a save of 300 goes through" 204 "$(settings local '.SyncIntervalSeconds = 300')"
check "the file now holds 300" 1 "$(docker exec jm-play-local grep -c '<SyncIntervalSeconds>300<' "$XML")"
check "an idle time at the slot expiry is refused" 400 "$(settings local '.PlaybackIdleSeconds = 120')"
check "the first refused value" 400 "$(settings local '.PlaybackIdleSeconds = 110')"
check "the last accepted value" 204 "$(settings local '.PlaybackIdleSeconds = 109')"
check "a remote wait out of range" 400 "$(settings local '.RemotePlaybackInfoTimeoutSeconds = 2')"
check "an opening wait out of range" 400 "$(settings local '.StreamOpenTimeoutSeconds = 61')"
check "a retention out of range" 400 "$(settings local '.SessionRetentionHours = 0')"
check "a policy cache out of range" 400 "$(settings local '.PolicyCacheMinutes = 61')"

echo "--- the configuration page"
me=$(get local /Users/Me | jq -r .Id)
sid=$(curl -fsS http://127.0.0.1:18430/System/Info/Public | jq -r .Id)
mkdir -p "$WORK/web"
cp "$S/configpage.py" "$WORK/web/configpage.py"
docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v jm-play-pw:/pw -e PYTHONPATH=/pw "$PW_IMAGE" \
  python3 /w/configpage.py http://host.docker.internal:18430 "$(token_of local)" "$me" "$sid" /w/config | tee "$WORK/web/config.out"
fields=$(grep '^FIELDS' "$WORK/web/config.out" | cut -d' ' -f2-)
check "the page shows the interval in force" 300 "$(echo "$fields" | jq -r .jmSyncInterval.value)"
check "the page refuses less" 300 "$(echo "$fields" | jq -r .jmSyncInterval.min)"
check "the page shows the idle time" 109 "$(echo "$fields" | jq -r .jmPlaybackIdle.value)"
check "the page knows the limit" 109 "$(echo "$fields" | jq -r .jmPlaybackIdle.max)"
check "the browser stops a low interval" false "$(grep '^INVALID' "$WORK/web/config.out" | cut -d' ' -f2 | tr 'FT' 'ft')"
check "the page saved a new time" 95 "$(get local "/Plugins/$PLUGIN/Configuration" | jq -r .PlaybackIdleSeconds)"
check "and kept the interval" 300 "$(get local "/Plugins/$PLUGIN/Configuration" | jq -r .SyncIntervalSeconds)"
settings local '.PlaybackIdleSeconds = 90' >/dev/null

echo "--- the wait on a remote that stopped answering"
viewer_login
uid=$(user_id local viewer)
item=$(stub_item local "Sonda Senza ID")
docker pause jm-play-remote >/dev/null
timed() { # timed: seconds a PlaybackInfo takes
  local started ended code
  started=$(python3 -c 'import time; print(time.time())')
  code=$(as_viewer POST "/Items/$item/PlaybackInfo?userId=$uid" "$PROFILE_HLS")
  ended=$(python3 -c 'import time; print(time.time())')
  echo "$code $(python3 -c "print(round($ended - $started))")"
}
settings local '.RemotePlaybackInfoTimeoutSeconds = 3' >/dev/null
fast=$(timed)
settings local '.RemotePlaybackInfoTimeoutSeconds = 10' >/dev/null
slow=$(timed)
docker unpause jm-play-remote >/dev/null
echo "3 s: $fast, 10 s: $slow"
close_to() { awk -v got="${2#* }" -v limit="$1" 'BEGIN { print (got >= limit && got <= limit + 2) ? 1 : 0 }'; }
check "a limit of 3 seconds refuses in about 3 seconds" "503 1" "${fast% *} $(close_to 3 "$fast")"
check "the default refuses in about 10 seconds" "503 1" "${slow% *} $(close_to 10 "$slow")"
exit $FAILED
