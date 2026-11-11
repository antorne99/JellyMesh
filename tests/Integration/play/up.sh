#!/usr/bin/env bash
# A stock remote and a consumer running the plugin as it was before signing, so that the stubs on disk and in the database
# are the unsigned ones of a real upgrade. OLD is a checkout of a commit before the signed URLs, made with archive and tar.
# usage: MEDIA=<main checkout>/tests/Integration/media OLD=<folder> up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
OLD=${OLD:?a checkout of a commit before the signed URLs}

down
docker volume create "$PREFIX-media" >/dev/null

start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
scan remote

# The plugin before signing keeps remote servers suspended without the secret key variable.
START_ENV=${START_ENV:--e JELLYMESH_SECRET_KEY=jm-play-test-key-0123456789abcdef0123456789} start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film /media/privata && touch /media/film/.keep /media/privata/.keep'
library local Film movies /media/film /federata/Film
library local Privato movies /media/privata
user local viewer >/dev/null
film=$(get local /Library/VirtualFolders | jq -r '.[] | select(.Name == "Film") | .ItemId')
uid=$(user_id local viewer)
policy=$(get local "/Users/$uid" | jq -c --arg f "$film" '.Policy | .EnableAllFolders = false | .EnabledFolders = [$f]')
post local "/Users/$uid/Policy" "$policy" >/dev/null

NUGET_VOLUME=jellymesh-nuget bash "$OLD/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
echo "$id" >"$WORK/id-remote"
import_all local "$id"
sync_now local
scan local
get local /JellyMesh/Admin/Sync | jq -c '{IsRunning}'
