#!/usr/bin/env bash
# Proof of row 236 (Q236): the same series on two stock remotes, each with its own name and plot for season 1, with both
# server folders in one local library. Prints which season text Jellyfin shows; checks only what the proof needs.
# Observed on 12.4 (two runs): in both, the season came from the server of the series row Jellyfin shows, a different
# server in each run; each episode keeps the season name of its own server.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-seasons2] [PORT=18850] two-servers.sh   (KEEP=1 keeps it)
set -euo pipefail
PREFIX=${PREFIX:-jm-seasons2}
PORT=${PORT:-18850}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SERIES="JM Serie Divisa (2026)"
cleanup() { down; docker volume rm "$PREFIX-media-peer" >/dev/null 2>&1 || true; }
[ "${KEEP:-}" = 1 ] || trap cleanup EXIT
cleanup

# The two remotes: remote with the media of remoto-standard, peer with those of remoto-jellymesh, each in its own volume.
for s in remote peer; do
  volume=$PREFIX-media$([ "$s" = peer ] && echo -peer || true)
  from=$([ "$s" = remote ] && echo remoto-standard || echo remoto-jellymesh)
  docker volume create "$volume" >/dev/null
  docker run --rm --entrypoint sh -v "$MEDIA/$from:/from:ro" -v "$volume:/to" "$IMAGE" -c "mkdir -p /to/serie && cp -r '/from/serie/$SERIES' /to/serie/"
  start "$s" "$volume:/media"
  library "$s" Serie tvshows /media/serie
  user "$s" jellymesh >/dev/null
  full_scan "$s"
  series=$(get "$s" '/Items?Recursive=true&IncludeItemTypes=Series' | jq -r --arg n "${SERIES% (*}" '.Items[] | select(.Name == $n) | .Id')
  season=$(get "$s" "/Shows/$series/Seasons" | jq -r '.Items[] | select(.IndexNumber == 1) | .Id')
  check "season 1 of $s renamed" 204 "$(edit "$s" "$season" ".Name = \"Stagione di $s\" | .Overview = \"Trama di $s\"")"
done

start local "$PREFIX-pw:/media"
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
folders=()
rm -f "$WORK/folders"
for s in remote peer; do
  id=$(add_remote local "$s" "http://host.docker.internal:$(port_of "$s")" jellymesh)
  import_all local "$id"
  echo "$(server_folder "$id")=$s" >>"$WORK/folders"
  folders+=("$(server_folder "$id")/Serie")
done
sync_now local
library local Serie tvshows "${folders[@]}"
full_scan local

# server_of <path>: the remote whose server folder holds a path.
server_of() { local line; while IFS= read -r line; do case $1 in "${line%%=*}"/*) echo "${line#*=}"; return ;; esac; done <"$WORK/folders"; echo "?"; }

for s in remote peer; do
  folder=$(grep "=$s\$" "$WORK/folders" | cut -d= -f1)
  check "the season.nfo of $s has its own name" 1 \
    "$(docker exec "$PREFIX-local" grep -c "<title>Stagione di $s</title>" "$folder/Serie/$SERIES/Season 01/season.nfo" || true)"
done

rows=$(get local '/Items?Recursive=true&IncludeItemTypes=Series&Fields=Path' | jq -c --arg n "${SERIES% (*}" '[.Items[] | select(.Name == $n) | {Id, Path}]')
uid=$(get local /Users/Me | jq -r .Id)
grouped=$(get local "/Users/$uid/Items?ParentId=$(get local "/Users/$uid/Views" | jq -r '.Items[] | select(.Name == "Serie") | .Id')&IncludeItemTypes=Series&Recursive=true" | jq --arg n "${SERIES% (*}" '[.Items[] | select(.Name == $n)] | length')
echo "series items the API lists: $(echo "$rows" | jq length); series rows the user sees in the library: $grouped"
echo "server folders in path order: $(sort "$WORK/folders" | cut -d= -f2 | paste -sd' ' -)"

echo "== row 236: the season 1 Jellyfin shows for each series item"
for id in $(echo "$rows" | jq -r '.[].Id'); do
  get local "/Shows/$id/Seasons?userId=$uid&Fields=Overview,Path" | jq -c '.Items[] | select(.IndexNumber == 1) | {Name, Overview, Path}' |
    while IFS= read -r season; do
      echo "series $(server_of "$(echo "$rows" | jq -r --arg i "$id" '.[] | select(.Id == $i) | .Path')"): season 1 shows $(echo "$season" | jq -c '{Name, Overview}') from the folder of $(server_of "$(echo "$season" | jq -r .Path)")"
    done
  get local "/Shows/$id/Episodes?userId=$uid&Season=1&Fields=Path" | jq -c '.Items[] | {IndexNumber, SeasonName, Path}' |
    while IFS= read -r episode; do
      echo "  episode $(echo "$episode" | jq -c '{IndexNumber, SeasonName}') of $(server_of "$(echo "$episode" | jq -r .Path)")"
    done
done

[ "$FAILED" = 0 ] && echo "all checks passed" || { echo "some checks failed"; exit 1; }
