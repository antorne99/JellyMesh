#!/usr/bin/env bash
# Task 828 and decision 408: a server removed and added back is a new server, in a new folder. Proves on series that
# its titles come back with every user's watch state, those without provider IDs too, kept by content in the
# plugin's SQLite file while the server is gone (rows 412 and 414).
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-rms] [PORT=18470] removal-series.sh   (KEEP=1 leaves the containers up)
set -euo pipefail
PREFIX=${PREFIX:-jm-rms}
PORT=${PORT:-18470}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Own containers and ports. alice and bob are consumer users: their requests go to local with their own token.
port_of() { case $1 in local | alice | bob) echo "$PORT" ;; remote) echo $((PORT + 1)) ;; esac; }
name_of() { echo "$PREFIX-$1"; }
down() {
  docker rm -f "$PREFIX-local" "$PREFIX-remote" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" \
    "$PREFIX-remote-config" "$PREFIX-remote-cache" "$PREFIX-remote-fed" "$PREFIX-local-media" >/dev/null 2>&1 || true
}
[ "${KEEP:-}" = 1 ] || trap down EXIT

down
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Serie tvshows /media/serie
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-local-media:/media"
user local alice >/dev/null
user local bob >/dev/null
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
login alice alice
login bob bob
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
library local Serie tvshows "$(server_folder "$id")/Serie"
library local Film movies "$(server_folder "$id")/Film"
import_all local "$id"
sync_now local
scan local

# episodes: the federated episodes as "<series> SxxEyy<TAB>id<TAB>path", sorted.
episodes() {
  get local '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path' |
    jq -r '.Items[] | select((.Path // "") | startswith("/federata")) |
      "\(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber)\t\(.Id)\t\(.Path)"' | sort
}
series_rows() { get local '/Items?Recursive=true&IncludeItemTypes=Series&Fields=Path' | jq -r '.Items[] | "\(.Name)\t\(.Id)\t\(.Path)"' | sort; }
# named_episodes: waits until every federated episode has its series and numbers, which a scan can return before.
named_episodes() { for _ in $(seq 60); do episodes | grep -q '^null ' || return 0; sleep 2; done; }
ep_id() { episodes | awk -F'\t' -v k="$1" '$1 == k { print $2 }'; }
series_id() { series_rows | awk -F'\t' -v k="$1" '$1 == k { print $2 }'; }

# film_id: the federated film whose name starts with the given text.
film_id() {
  get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' |
    jq -r --arg t "$1" '[.Items[] | select((.Path // "") | startswith("/federata")) | select(.Name | startswith($t))][0].Id // ""'
}

# state: every user's watch state on every federated episode, series and film, one line each, sorted.
state() {
  local name
  for name in alice bob; do
    get "$name" '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' |
      jq -r --arg u "$name" '.Items[] | select((.Path // "") | startswith("/federata")) | .UserData as $d |
        "\($u) film \(.Name): played=\($d.Played) count=\($d.PlayCount) pos=\($d.PlaybackPositionTicks) fav=\($d.IsFavorite) last=\($d.LastPlayedDate // "-")"'
    get "$name" '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path' |
      jq -r --arg u "$name" '.Items[] | select((.Path // "") | startswith("/federata")) | .UserData as $d |
        "\($u) \(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber): played=\($d.Played) count=\($d.PlayCount) pos=\($d.PlaybackPositionTicks) fav=\($d.IsFavorite) last=\($d.LastPlayedDate // "-")"'
    get "$name" '/Items?Recursive=true&IncludeItemTypes=Series' |
      jq -r --arg u "$name" '.Items[] | .UserData as $d |
        "\($u) \(.Name) series: played=\($d.Played) unplayed=\($d.UnplayedItemCount) percent=\($d.PlayedPercentage // "-") fav=\($d.IsFavorite)"'
  done | sort
}

# play_to_end <user> <item>: one whole playback reported through the stock session routes.
play_to_end() {
  local runtime
  runtime=$(get "$1" "/Items/$2" | jq -r .RunTimeTicks)
  post "$1" /Sessions/Playing "{\"ItemId\":\"$2\",\"PlaySessionId\":\"rms-$RANDOM\",\"PositionTicks\":0}" >/dev/null
  post "$1" /Sessions/Playing/Stopped "{\"ItemId\":\"$2\",\"PlaySessionId\":\"rms-$RANDOM\",\"PositionTicks\":$runtime}" >/dev/null
}
# pause_at_third <user> <item>: a resume point a third of the way in. The clips are under the 300 s Jellyfin
# needs to keep one from a playback report, so it is written as user data, as a client would after a long film.
pause_at_third() {
  local third
  third=$(get "$1" "/Items/$2" | jq -r '.RunTimeTicks / 3 | floor')
  post "$1" "/UserItems/$2/UserData" "{\"Played\":false,\"PlaybackPositionTicks\":$third}" >/dev/null
  check "$1 has a resume point on $2" 200 "$(status)"
}

named_episodes
echo "--- before removal"
episodes | tee "$WORK/episodes-before"
series_rows | tee "$WORK/series-before"
check "both series are federated" 2 "$(wc -l <"$WORK/series-before" | tr -d ' ')"
check "all five episodes are federated" 5 "$(wc -l <"$WORK/episodes-before" | tr -d ' ')"
echo "runtime of the episodes in seconds: $(get local '/Items?Recursive=true&IncludeItemTypes=Episode' | jq -c '[.Items[].RunTimeTicks / 10000000 | floor]')"
docker exec "$PREFIX-local" find "$(server_folder "$id")/Serie" -name '*.nfo' -exec sh -c 'echo "$1: $(grep -o "<uniqueid[^<]*</uniqueid>" "$1" | tr "\n" " ")"' _ {} \;

d3=$(ep_id "JM Serie Divisa S1E3")
d4=$(ep_id "JM Serie Divisa S1E4")
d5=$(ep_id "JM Serie Divisa S1E5")
d6=$(ep_id "JM Serie Divisa S1E6")
n2=$(ep_id "Serie Senza ID S1E2")
divisa=$(series_id "JM Serie Divisa")
senza=$(series_id "Serie Senza ID")
sonda=$(film_id "Sonda Senza ID")
check "the film without IDs is federated" 1 "$([ -n "$sonda" ] && echo 1 || echo 0)"

# alice: one episode marked, one watched twice, one paused, the series a favorite.
post alice "/UserPlayedItems/$d3" >/dev/null
play_to_end alice "$d4"
play_to_end alice "$d4"
pause_at_third alice "$d5"
post alice "/UserFavoriteItems/$divisa" >/dev/null
post alice "/UserPlayedItems/$n2" >/dev/null
# bob: different episodes, a pause on the one alice only marked, a favorite episode.
play_to_end bob "$d5"
pause_at_third bob "$d3"
post bob "/UserFavoriteItems/$d6" >/dev/null
pause_at_third bob "$n2"
post bob "/UserFavoriteItems/$senza" >/dev/null
# A film without IDs: alice watches it, bob pauses it and marks it a favorite.
play_to_end alice "$sonda"
pause_at_third bob "$sonda"
post bob "/UserFavoriteItems/$sonda" >/dev/null
sleep 3

state | tee "$WORK/state-before"
check "alice watched one episode twice" 1 "$(grep -c '^alice JM Serie Divisa S1E4: played=true count=2' "$WORK/state-before" || true)"
check "alice has a real resume point" 1 "$(grep -Ec '^alice JM Serie Divisa S1E5: played=false count=0 pos=[1-9]' "$WORK/state-before" || true)"
check "bob has a real resume point" 1 "$(grep -Ec '^bob JM Serie Divisa S1E3: played=false count=0 pos=[1-9]' "$WORK/state-before" || true)"
check "bob has a resume point on the film without IDs" 1 "$(grep -Ec '^bob film Sonda Senza ID[^:]*: played=false count=0 pos=[1-9].* fav=true' "$WORK/state-before" || true)"
check "bob has a resume point on the series without IDs" 1 "$(grep -Ec '^bob Serie Senza ID S1E2: played=false count=0 pos=[1-9]' "$WORK/state-before" || true)"

echo "--- removal"
req local DELETE "/JellyMesh/Admin/Servers/$id" >/dev/null
check "the server is removed" 204 "$(status)"
for _ in $(seq 1 120); do
  [ "$(docker exec "$PREFIX-local" sh -c 'find /federata/*/Serie -name "*.strm" | wc -l')" = 0 ] && break
  sleep 1
done
check "its stubs left the federated folder" 0 "$(docker exec "$PREFIX-local" sh -c 'find /federata/*/Serie -name "*.strm" | wc -l')"
# F19: the folder of the server goes once its stubs are gone, while the library keeps its path.
for _ in $(seq 1 60); do
  docker exec "$PREFIX-local" test -e "$(server_folder "$id")" || break
  sleep 1
done
check "its folder left the federated folder" gone "$(docker exec "$PREFIX-local" test -e "$(server_folder "$id")" && echo kept || echo gone)"
scan local
check "no federated episode is left in the library" 0 "$(episodes | wc -l | tr -d ' ')"
check "no series is left in the library" 0 "$(series_rows | wc -l | tr -d ' ')"
# The user data Jellyfin keeps while the items are gone, read from a copy of its database.
rm -rf "$WORK/db" && mkdir -p "$WORK/db"
docker exec "$PREFIX-local" sh -c 'ls /config/data/jellyfin.db*' | while read -r f; do docker cp "$PREFIX-local:$f" "$WORK/db/" >/dev/null; done
if command -v sqlite3 >/dev/null; then
  echo "user data rows in the gap: $(sqlite3 "$WORK/db/jellyfin.db" 'SELECT COUNT(*) FROM UserData;')"
  sqlite3 -header "$WORK/db/jellyfin.db" 'SELECT CustomDataKey, ItemId, Played, PlayCount, PlaybackPositionTicks, IsFavorite, RetentionDate FROM UserData ORDER BY CustomDataKey;' || true
fi

# Row 412: the plugin keeps the watch state of the removed titles by content, keyed by local user, in its own SQLite file.
kept_db=$(docker exec "$PREFIX-local" sh -c 'find /config -name watch-state.db | head -1')
check "the watch state of the removed titles is kept in the plugin data folder" 1 "$([ -n "$kept_db" ] && echo 1 || echo 0)"
# kept_copy: a copy of the kept file, in $WORK/kept.
kept_copy() {
  rm -rf "$WORK/kept" && mkdir -p "$WORK/kept"
  docker exec "$PREFIX-local" sh -c "ls '$kept_db'*" | while read -r f; do docker cp "$PREFIX-local:$f" "$WORK/kept/" >/dev/null; done
}
kept_copy
check "the kept file holds no remote address" 0 "$(grep -ac -e host.docker.internal -e :// "$WORK/kept/watch-state.db" || true)"
if command -v sqlite3 >/dev/null; then
  sqlite3 -header "$WORK/kept/watch-state.db" 'SELECT Kind, Title, Year, Tmdb, Imdb, Tvdb, SeasonNumber, EpisodeNumber,
    (SELECT COUNT(*) FROM UserStates WHERE TitleId = Titles.Id) AS Users FROM Titles ORDER BY Title, SeasonNumber, EpisodeNumber;'
  check "the film without IDs is kept" 1 "$(sqlite3 "$WORK/kept/watch-state.db" "SELECT COUNT(*) FROM Titles WHERE Title LIKE 'Sonda Senza ID%';")"
  check "the series without IDs is kept with its episode" 2 "$(sqlite3 "$WORK/kept/watch-state.db" "SELECT COUNT(*) FROM Titles WHERE Title = 'Serie Senza ID';")"
  check "the kept rows are those of the local users" "$(printf '%s\n' "$(user_id local alice)" "$(user_id local bob)" | tr -d - | tr 'A-F' 'a-f' | sort)" \
    "$(sqlite3 "$WORK/kept/watch-state.db" 'SELECT DISTINCT UserId FROM UserStates;' | sort)"
  # The tokens the remote gave the plugin, read from a copy of its database.
  rm -rf "$WORK/rdb" && mkdir -p "$WORK/rdb"
  docker exec "$PREFIX-remote" sh -c 'ls /config/data/jellyfin.db*' | while read -r f; do docker cp "$PREFIX-remote:$f" "$WORK/rdb/" >/dev/null; done
  leaks=0
  for token in $(sqlite3 "$WORK/rdb/jellyfin.db" 'SELECT AccessToken FROM Devices;'); do
    grep -aqF "$token" "$WORK/kept/watch-state.db" && leaks=$((leaks + 1))
  done
  check "the kept file holds no remote token" 0 "$leaks"
fi

# Jellyfin 12.4 schedules no run of its user data cleanup, which drops what stayed detached 90 days. A run now keeps it.
task=$(get local /ScheduledTasks | jq -r '.[] | select(.Key == "CleanupUserDataTask") | .Id')
echo "user data cleanup triggers: $(get local "/ScheduledTasks/$task" | jq -c .Triggers)"
post local "/ScheduledTasks/Running/$task" >/dev/null
for _ in $(seq 1 60); do
  [ "$(get local "/ScheduledTasks/$task" | jq -r '.LastExecutionResult.Status // ""')" != "" ] && break
  sleep 1
done
check "the user data cleanup ran" Completed "$(get local "/ScheduledTasks/$task" | jq -r '.LastExecutionResult.Status // ""')"

echo "--- the server comes back"
old_id=$id
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
# A server added back is a new server, so in a folder of its own: the admin adds it to the library (decision 408).
check "the server comes back as a new server" 1 "$([ -n "$id" ] && [ "$id" != null ] && [ "$id" != "$old_id" ] && echo 1 || echo 0)"
check "the add recognizes no removed server" null "$(jq -r .RecognizedAs "$WORK/body")"
check "the new folder joins the library" 204 "$(link_folder local Serie "$(server_folder "$id")/Serie")"
check "the new film folder joins the library" 204 "$(link_folder local Film "$(server_folder "$id")/Film")"
# A remote already configured is refused under any name (decision 120).
post local /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$(port_of remote)" '{Name:"twice",Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" >/dev/null
check "adding it a second time is refused" 409 "$(status)"
check "the refusal names the server" "AlreadyConfigured remote" "$(jq -r '"\(.Failure) \(.ServerName)"' "$WORK/body")"
import_all local "$id"
sync_now local
check "its folder is written again" 0 "$(docker exec "$PREFIX-local" test -f "$(server_folder "$id")/Serie/.jellymesh"; echo $?)"
scan local
named_episodes
episodes | tee "$WORK/episodes-after"
series_rows | tee "$WORK/series-after"
# in_folder: the paths with the folder of the server taken off.
in_folder() { sed -E 's#/federata/[0-9a-f-]{36}/#/federata/<server>/#'; }
check "the same episodes come back at the same paths in the folder" "$(cut -f1,3 "$WORK/episodes-before" | in_folder)" "$(cut -f1,3 "$WORK/episodes-after" | in_folder)"
check "the same series come back at the same paths in the folder" "$(cut -f1,3 "$WORK/series-before" | in_folder)" "$(cut -f1,3 "$WORK/series-after" | in_folder)"
check "the episodes come back in the new folder" 5 "$(grep -cF "$(server_folder "$id")/" "$WORK/episodes-after" || true)"
echo "item ids changed: episodes $(cut -f2 "$WORK/episodes-before" | grep -cvxFf <(cut -f2 "$WORK/episodes-after") || true) of 5, series $(cut -f2 "$WORK/series-before" | grep -cvxFf <(cut -f2 "$WORK/series-after") || true) of 2"

# Every title gets its state back, the series without IDs too: the plugin puts it back after the scan (row 412).
for _ in $(seq 1 30); do
  state >"$WORK/state-after"
  cmp -s "$WORK/state-before" "$WORK/state-after" && break
  sleep 2
done
cat "$WORK/state-after"
while IFS= read -r line; do
  check "${line%%:*} keeps its state" "$line" "$(grep -F "${line%%:*}:" "$WORK/state-after" || true)"
done <"$WORK/state-before"
check "alice's episode without IDs is played again" 1 "$(grep -c '^alice Serie Senza ID S1E2: played=true' "$WORK/state-after" || true)"
check "bob's resume point without IDs is back" 1 "$(grep -Ec '^bob Serie Senza ID S1E2: played=false count=0 pos=[1-9]' "$WORK/state-after" || true)"
check "alice's film without IDs is played again" 1 "$(grep -c '^alice film Sonda Senza ID[^:]*: played=true' "$WORK/state-after" || true)"
check "bob's resume point and favorite on the film without IDs are back" 1 "$(grep -Ec '^bob film Sonda Senza ID[^:]*: played=false count=0 pos=[1-9].* fav=true' "$WORK/state-after" || true)"
check "bob's favorite series without IDs is back" 1 "$(grep -c '^bob Serie Senza ID series: .* fav=true' "$WORK/state-after" || true)"
if command -v sqlite3 >/dev/null; then
  kept_copy
  check "the kept records are forgotten once put back" 0 "$(sqlite3 "$WORK/kept/watch-state.db" 'SELECT COUNT(*) FROM Titles;')"
fi

# Row 414: the SQLite the server ships loads in the plugin.
check "the plugin is active" Active "$(get local /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Status')"
check "the plugin kept the watch state" 1 "$(docker logs "$PREFIX-local" 2>&1 | grep -cm1 'Kept the watch state of' || true)"
check "the plugin put the watch state back" 1 "$(docker logs "$PREFIX-local" 2>&1 | grep -cm1 'Put back the kept watch state of' || true)"
check "no load or watch state error is logged" 0 \
  "$(docker logs "$PREFIX-local" 2>&1 | grep -cE 'FileLoadException|TypeInitializationException|DllNotFoundException|MissingMethodException|Cannot keep the watch state|Cannot put back the kept watch state' || true)"

echo "=== failed: $FAILED"
exit $FAILED
