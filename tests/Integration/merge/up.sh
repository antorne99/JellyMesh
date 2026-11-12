#!/usr/bin/env bash
# Fresh private servers for manual merges, with what the media matrix lacks: a chain of contradicting IDs, two links of
# it on one server, a pair without IDs, and two local films of one title. Both server folders are in the Film library.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=..] [PORT=..] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

SD="$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
HD="$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"

merge_down
docker volume create "$PREFIX-media" >/dev/null
for s in remote peer; do
  START_ENV="-v $PREFIX-$s-extra:/extra" start "$s" "$MEDIA/$([ "$s" = remote ] && echo remoto-standard || echo remoto-jellymesh):/media:ro"
  docker exec "$PREFIX-$s" sh -c 'mkdir -p /extra/film && chmod a+rwx /extra/film'
  library "$s" Film movies /media/film /extra/film
  user "$s" jellymesh >/dev/null
done

# A(1, a) and C(2, b) on remote, B(1, b) on peer. The lowest key is A's, and the best quality is B's.
add_clip remote "$SD" "JM Catena Uno (2026)" "JM Catena Uno" 999100001 tt99100001 "Primo anello."
add_clip remote "$SD" "JM Catena Tre (2026)" "JM Catena Tre" 999100002 tt99100002 "Terzo anello."
add_clip peer "$HD" "JM Catena Due (2026)" "JM Catena Due" 999100001 tt99100002 "Secondo anello."
add_clip remote "$SD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Un pianista insegue un sogno tra le colline."
add_clip peer "$HD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Una spedizione polare perde i contatti con la base."
scan remote
scan peer

start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
docker cp "$MEDIA/locale/film/Sonda Locale Senza ID (2026)" "$PREFIX-local:/media/film/"
for t in "JM Due Locali" "JM Due Locali Bis"; do
  docker exec "$PREFIX-local" mkdir -p "/media/film/$t (2026)"
  docker cp "$SD" "$PREFIX-local:/media/film/$t (2026)/$t (2026).mkv" >/dev/null
done
printf '%s' '<?xml version="1.0" encoding="utf-8"?><movie><title>JM Due Locali</title><year>2026</year><uniqueid type="tmdb" default="true">999100011</uniqueid></movie>' |
  docker exec -i "$PREFIX-local" sh -c 'cat >"/media/film/JM Due Locali (2026)/movie.nfo"'
printf '%s' '<?xml version="1.0" encoding="utf-8"?><movie><title>JM Due Locali</title><year>2026</year><uniqueid type="tmdb" default="true">999100012</uniqueid></movie>' |
  docker exec -i "$PREFIX-local" sh -c 'cat >"/media/film/JM Due Locali Bis (2026)/movie.nfo"'
docker exec "$PREFIX-local" chmod -R a+rwX /media/film
library local Film movies /media/film
user local jellymesh >/dev/null

NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
scan local
for s in remote peer; do
  id=$(add_remote local "$s" "http://host.docker.internal:$(port_of "$s")" jellymesh)
  echo "$id" >"$WORK/id-$s"
  import_all local "$id"
done
sync_now local
for s in remote peer; do
  check "the Film folder of $s is linked" 204 "$(link_folder local Film "$(server_folder "$(cat "$WORK/id-$s")")/Film")"
done
scan local
sync_now local
exit "$FAILED"
