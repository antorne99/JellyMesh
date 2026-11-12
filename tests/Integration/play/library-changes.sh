#!/usr/bin/env bash
# Test 11 of the spec (Q45, Q46, Q50, Q53): a library over the federated folder, a server folder in one more library, a
# library taken off a shared server folder, and the version links rebuilt by the next sync, on the real page too.
#   local  $PREFIX-local  $PORT      plugin from this checkout, consumer of a and b
#   a, b   $PREFIX-a/-b   $PORT+1/2  stock Jellyfin with the media of remoto-standard
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-libchanges] [PORT=18740] library-changes.sh [--keep]
set -euo pipefail
PREFIX=${PREFIX:-jm-libchanges}
PORT=${PORT:-18740}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
port_of() { case $1 in local) echo "$PORT" ;; a) echo $((PORT + 1)) ;; b) echo $((PORT + 2)) ;; esac; }

down_changes() {
  docker rm -f "$PREFIX-local" "$PREFIX-a" "$PREFIX-b" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" "$PREFIX-local-media" \
    "$PREFIX-a-config" "$PREFIX-a-cache" "$PREFIX-a-fed" "$PREFIX-b-config" "$PREFIX-b-cache" "$PREFIX-b-fed" "$PREFIX-pw" >/dev/null 2>&1 || true
}
[ "${1:-}" = --keep ] || trap down_changes EXIT
down_changes

for r in a b; do
  start "$r" "$MEDIA/remoto-standard:/media:ro"
  library "$r" Film movies /media/film
  library "$r" Serie tvshows /media/serie
  user "$r" jellymesh >/dev/null
  scan "$r"
done

start local "$PREFIX-local-media:/media"
docker exec "$PREFIX-local" sh -c 'rm -rf /federata/Film /federata/Serie'
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id_a=$(add_remote local Casa "http://host.docker.internal:$(port_of a)" jellymesh)
id_b=$(add_remote local Amico "http://host.docker.internal:$(port_of b)" jellymesh)
import_all local "$id_a"
import_all local "$id_b"
sync_now local
fa=$(server_folder "$id_a")
fb=$(server_folder "$id_b")
folder() { get local /JellyMesh/Admin/Status | jq -c ".FederatedFolder | $1"; }

echo "--- a fresh install: two servers, nothing linked, no migration"
check "each server has its two paths" "[\"$fa/Film\",\"$fa/Serie\",\"$fb/Film\",\"$fb/Serie\"]" "$(folder '[.Servers[] | .MoviesPath, .SeriesPath]')"
check "the folders are not linked" '"NotLinked"' "$(folder .Link)"
check "no flat path is offered" 0 "$(get local /JellyMesh/Admin/Status | grep -c '/federata/Film\|/federata/Serie' || true)"
check "and no migration banner" null "$(folder .Migration)"

echo "--- the folders linked as the page says"
library local Film movies "$fa/Film" "$fb/Film"
library local Serie tvshows "$fa/Serie" "$fb/Serie"
library local "Casa - Film" movies "$fa/Film"
scan local
sync_now local
check "every server is linked" '"Linked"' "$(folder .Link)"
check "nothing covers the folders" '[]' "$(folder .Covering)"
check "the series folders sit in the same libraries" false "$(folder .SeriesSplit)"
check "the sync took in the new libraries" '{"Added":[],"Removed":[]}' "$(folder .Changes)"
linked() { get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,Path' | jq '[.Items[] | select((.Path // "") | startswith("/federata")) | select((.MediaSources | length) >= 2)] | length'; }
before=$(linked)
echo "films with two versions: $before"
check "the films of both servers are versions of one title" true "$([ "$before" -gt 0 ] && echo true || echo false)"

echo "--- a library over the federated folder, and a series folder in one more library"
library local Tutto mixed /federata
library local "Casa - Serie" tvshows "$fa/Serie"
check "the page names the covering library" '["Tutto"]' "$(folder .Covering)"
check "and the libraries added since the sync" '["Tutto","Casa - Serie"]' "$(folder '.Changes.Added | sort_by(. != "Tutto")')"
check "the series of the two servers stay split" true "$(folder .SeriesSplit)"
check "with the libraries of each" "[[\"Casa - Serie\",\"Serie\"],[\"Serie\"]]" "$(folder '[.Servers[].SeriesLibraries | sort]')"

echo "--- the page as an admin sees it"
me=$(get local /Users/Me | jq -r .Id)
sid=$(curl -fsS "http://127.0.0.1:$PORT/System/Info/Public" | jq -r .Id)
cp "$S/serverpage.py" "$WORK/web/serverpage.py"
docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v "$PREFIX-pw:/pw" -e PYTHONPATH=/pw "$PW_IMAGE" \
  bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
  /w/serverpage.py "http://host.docker.internal:$PORT" "$(token_of local)" "$me" "$sid" /w/page | tee "$WORK/web/page.out"
check "the page shows a block for each server" 2 "$(grep -c '^SERVER ' "$WORK/web/page.out")"
check "the page warns about the covering library" 1 "$(grep '^NOTES en' "$WORK/web/page.out" | grep -c 'Tutto holds the whole federated folder')"
check "the page warns about the split series" 1 "$(grep '^NOTES en' "$WORK/web/page.out" | grep -c 'Serie folders of the servers are in different libraries')"
check "the page says what changed since the sync" 1 "$(grep '^NOTES en' "$WORK/web/page.out" | grep -c 'came over the federated folder')"
check "no page scrolls sideways" 0 "$(grep -c "^SCROLLS .* True" "$WORK/web/page.out" || true)"
check "the Italian page warns too" 1 "$(grep '^NOTES it' "$WORK/web/page.out" | grep -c 'Tutto contiene tutta la cartella federata')"
check "the page shows no remote address" 0 "$(grep -c "host.docker.internal:$(port_of a)\|host.docker.internal:$(port_of b)" "$WORK/web/page.text" || true)"

echo "--- a library taken off a shared server folder"
req local DELETE "/Library/VirtualFolders?name=Tutto&refreshLibrary=false" >/dev/null
req local DELETE "/Library/VirtualFolders?name=Casa%20-%20Serie&refreshLibrary=false" >/dev/null
req local DELETE "/Library/VirtualFolders?name=Casa%20-%20Film&refreshLibrary=true" >/dev/null
scan local
check "the page says it left the folder" '["Casa - Film"]' "$(folder .Changes.Removed)"
echo "films with two versions after the removal: $(linked)"
sync_now local
check "the next sync clears the warning" '{"Added":[],"Removed":[]}' "$(folder .Changes)"
check "and links the versions again" "$before" "$(linked)"
check "nothing covers the folders any more" '[]' "$(folder .Covering)"

echo "--- the automatic libraries of one server, one of them deleted in the dashboard, then another library on its folder"
own() { get local /JellyMesh/Admin/Status | jq -c --arg id "$id_b" ".Servers[] | select(.Id == \$id) | .AutomaticLibraries | $1"; }
post local "/JellyMesh/Admin/Servers/$id_b/AutomaticLibraries" '{"Enabled":true}' >/dev/null
check "switching on answers 202" 202 "$(cat "$WORK/status")"
for i in $(seq 1 120); do [ "$(own '[.Busy, (.Libraries | length)]')" = '[false,2]' ] && break; sleep 1; done
check "the server has its two libraries" 2 "$(own '.Libraries | length')"
own_serie=$(own '.Libraries[] | select(.Kind == "Series") | .Name' | jq -r .)
req local DELETE "/Library/VirtualFolders?name=$(jq -rn --arg v "$own_serie" '$v | @uri')&refreshLibrary=false" >/dev/null
scan local
sync_now local
check "the deleted library is left to the admin" "[\"$own_serie\"]" "$(own .Released)"
check "and the other one stays" 1 "$(own '.Libraries | length')"
library local "Amico - Extra" tvshows "$fb/Serie"
check "the library only on its folders is named" '["Amico - Extra"]' "$(folder "[.Servers[] | select(.Id == \"$id_b\") | .Unregistered[]]")"
docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v "$PREFIX-pw:/pw" -e PYTHONPATH=/pw "$PW_IMAGE" \
  python3 /w/serverpage.py "http://host.docker.internal:$PORT" "$(token_of local)" "$me" "$sid" /w/own | tee "$WORK/web/own.out"
servers_en=$(grep '^SERVERS en' "$WORK/web/own.out")
check "the page says restricted users do not see them" 1 "$(echo "$servers_en" | grep -c 'Users with access to only some libraries')"
check "the page names the library left to the admin" 1 "$(echo "$servers_en" | grep -c "JellyMesh no longer manages these libraries.*$own_serie")"
check "the page offers to recreate it" 1 "$(echo "$servers_en" | grep -c 'Recreate missing libraries')"
check "the page warns about the library outside the registry" 1 "$(grep '^NOTES en' "$WORK/web/own.out" | grep -c 'Amico - Extra is also on the folders of Amico')"
check "the Italian page says it too" 1 "$(grep '^SERVERS it' "$WORK/web/own.out" | grep -c 'Ricrea le librerie mancanti')"
check "no page scrolls sideways" 0 "$(grep -c "^SCROLLS .* True" "$WORK/web/own.out" || true)"

echo "=== failed: $FAILED"
exit $FAILED
