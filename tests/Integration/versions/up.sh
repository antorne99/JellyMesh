#!/usr/bin/env bash
# Two stock remotes with titles in common, and a consumer with the current build whose Film library has the Nfo saver on.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=..] [PORT=..] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

down
docker volume create "$PREFIX-media" >/dev/null

for s in remote peer; do
  start "$s" "$MEDIA/$([ "$s" = remote ] && echo remoto-standard || echo remoto-jellymesh):/media:ro"
  library "$s" Film movies /media/film
  library "$s" Serie tvshows /media/serie
  user "$s" jellymesh >/dev/null
  scan "$s"
done

start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film /media/serie && touch /media/film/.keep /media/serie/.keep'
saver='{"LibraryOptions":{"EnableInternetProviders":false,"MetadataSavers":["Nfo"]}}'
post local "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film" "$saver" >/dev/null
post local "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/media/serie" "$saver" >/dev/null

NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
for s in remote peer; do
  id=$(add_remote local "$s" "http://host.docker.internal:$(port_of "$s")" jellymesh)
  echo "$id" >"$WORK/id-$s"
  import_all local "$id"
done
sync_now local
for s in remote peer; do
  folder=$(server_folder "$(cat "$WORK/id-$s")")
  check "the Film folder of $s is linked" 204 "$(link_folder local Film "$folder/Film")"
  check "the Serie folder of $s is linked" 204 "$(link_folder local Serie "$folder/Serie")"
done
scan local
exit "$FAILED"
