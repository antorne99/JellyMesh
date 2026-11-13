#!/usr/bin/env bash
# Proof of tasks 835 and 836 (decision 50): a stub season shows the name, plot and poster of the remote season, films and
# series get logo, banner and thumb, the season keeps them through a rescan and a full refresh, a season the consumer
# already has gets a change, and an episode added later to a locked season keeps its numbers.
# usage: MEDIA=<main checkout>/tests/Integration/media PREFIX=jm-seasons PORT=18820 run.sh   (KEEP=1 keeps the stack)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SERIES="JM Serie Divisa (2026)"
FILM="Tears of Steel (2012)"
[ "${KEEP:-}" = 1 ] || trap down EXIT

# upload <server> <item> <type> <png file>
upload() {
  curl -fsS -o /dev/null -X POST -H "Authorization: $AUTH, Token=\"$(token_of "$1")\"" -H 'Content-Type: image/png' \
    --data-binary @<(base64 <"$4" | tr -d '\n') "http://127.0.0.1:$(port_of "$1")/Items/$2/Images/$3"
}
remote_series() { get remote '/Items?Recursive=true&IncludeItemTypes=Series' | jq -r --arg n "${SERIES% (*}" '.Items[] | select(.Name == $n) | .Id'; }
remote_film() { get remote '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -r '.Items[] | select(.Name == "Tears of Steel") | .Id'; }
local_series() { get local '/Items?Recursive=true&IncludeItemTypes=Series&Fields=Path' | jq -r --arg n "$SERIES" '[.Items[] | select((.Path // "") | (startswith("/federata") and endswith($n)))][0].Id // ""'; }
local_season() { get local "/Shows/$1/Seasons?Fields=Overview,Path" | jq -c '[.Items[] | select(.IndexNumber == 1)][0] | {Name, Overview, Poster: (.ImageTags.Primary != null)}'; }
art_of() { get local "/Items?Ids=$1&Fields=Path" | jq -c '.Items[0].ImageTags | {Logo: has("Logo"), Banner: has("Banner"), Thumb: has("Thumb")}'; }

down
python3 -I - "$WORK" <<'PY'
import struct, sys, zlib
def png(path, rgb, w=64, h=36):
    rows = b''.join(b'\x00' + bytes(rgb) * w for _ in range(h))
    chunk = lambda t, d: struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))
for name, rgb in [('a', (200, 30, 30)), ('b', (30, 200, 30))]:
    png(f'{sys.argv[1]}/{name}.png', rgb)
PY

# The remote media live in a volume, so that an episode can be added later.
docker volume create "$PREFIX-media" >/dev/null
docker run --rm --entrypoint sh -v "$MEDIA/remoto-standard:/from:ro" -v "$PREFIX-media:/to" "$IMAGE" -c \
  "mkdir -p /to/film /to/serie && cp -r '/from/film/$FILM' /to/film/ && cp -r '/from/serie/$SERIES' /to/serie/" 
start remote "$PREFIX-media:/media"
library remote Film movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
full_scan remote

echo "== the remote season gets a name, a plot and a poster; the series and the film get their art"
rs=$(remote_series)
rseason=$(get remote "/Shows/$rs/Seasons" | jq -r '.Items[] | select(.IndexNumber == 1) | .Id')
check "remote season renamed" 204 "$(edit remote "$rseason" '.Name = "Prima Stagione" | .Overview = "Trama della stagione"')"
upload remote "$rseason" Primary "$WORK/a.png"
rf=$(remote_film)
for item in "$rs" "$rf"; do
  for type in Logo Banner Thumb; do upload remote "$item" "$type" "$WORK/b.png"; done
done

start local "$PREFIX-pw:/media"
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
import_all local "$id"
sync_now local
folder=$(server_folder "$id")
library local Film movies "$folder/Film"
library local Serie tvshows "$folder/Serie"
full_scan local

docker exec "$PREFIX-local" sh -c "cat '$folder/Serie/$SERIES/Season 01/season.nfo'; ls '$folder/Serie/$SERIES' '$folder/Serie/$SERIES/Season 01' '$folder/Film/$FILM'"
series=$(local_series)
check "the series is a stub" true "$([ -n "$series" ] && echo true || echo false)"
check "a new season reads its season.nfo and poster" '{"Name":"Prima Stagione","Overview":"Trama della stagione","Poster":true}' "$(local_season "$series")"
check "the series has logo, banner and thumb" '{"Logo":true,"Banner":true,"Thumb":true}' "$(art_of "$series")"
film=$(stub_item local "$FILM")
check "the film has logo, banner and thumb" '{"Logo":true,"Banner":true,"Thumb":true}' "$(art_of "$film")"

echo "== a rescan and a full refresh of the series leave the season as it is"
full_scan local
post local "/Items/$series/Refresh?Recursive=true&MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=false" >/dev/null
sleep 15
check "the season after a rescan and a full refresh" '{"Name":"Prima Stagione","Overview":"Trama della stagione","Poster":true}' "$(local_season "$series")"

echo "== a change of the remote season reaches the season the consumer already has"
check "remote season renamed again" 204 "$(edit remote "$rseason" '.Name = "Stagione Uno" | .Overview = "Nuova trama"')"
sync_now local
sleep 3
check "the indexed season follows the remote" '{"Name":"Stagione Uno","Overview":"Nuova trama","Poster":true}' "$(local_season "$series")"

echo "== an episode added later to the locked season keeps its numbers"
docker exec "$PREFIX-remote" sh -c "cp '/media/serie/$SERIES/Season 01/JM Serie Divisa S01E03.mkv' '/media/serie/$SERIES/Season 01/JM Serie Divisa S01E07.mkv'"
full_scan remote
sync_now local
sleep 3
full_scan local
check "the new episode has its numbers" '{"ParentIndexNumber":1,"IndexNumber":7,"SeasonName":"Stagione Uno"}' \
  "$(get local "/Shows/$series/Episodes" | jq -c '[.Items[] | select(.IndexNumber == 7)][0] | {ParentIndexNumber, IndexNumber, SeasonName}')"
check "the season still shows the remote" '{"Name":"Stagione Uno","Overview":"Nuova trama","Poster":true}' "$(local_season "$series")"

[ "$FAILED" = 0 ] && echo "all checks passed" || { echo "some checks failed"; exit 1; }
