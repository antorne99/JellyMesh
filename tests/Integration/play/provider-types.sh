#!/usr/bin/env bash
# Integration 16 of the 0.6-0.7 spec (F62, row 460): a JellyMesh peer reads music, music videos, home videos and books
# from a JellyMesh provider. Stubs, photos and libraries of federated paths only stay out; the audio, lyrics and file
# routes keep the share and the stream limit; the change feed carries a new track and an album as its tracks. A track of a
# library without a type, which gives films and series only, stays out.
#   remote  stock, media of remoto-standard: the provider federates its music and books, so it holds stubs of them
#   local   the provider, media of remoto-jellymesh plus the home videos of remoto-standard and one .lrc
#   peer    a JellyMesh consumer of local
# usage: [PREFIX=jm-f62] [PORT=18620] [KEEP=1] provider-types.sh   (KEEP=1 leaves the containers up; lib.sh has down)
set -uo pipefail
PREFIX=${PREFIX:-jm-f62}
PORT=${PORT:-18620}
S0=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
MEDIA=${MEDIA:-$(git -C "$S0" worktree list | head -1 | awk '{print $1}')/tests/Integration/media}
source "$S0/lib.sh"
[ "${KEEP:-0}" = 1 ] || trap down EXIT

# install <server>: builds the plugin of this worktree once in the SDK container of the project (or takes JM_PLUGIN_DLL),
# installs it, restarts.
install() {
  local main src out version target
  main=$(git -C "$WT" worktree list | head -1 | awk '{print $1}')
  src=/src${WT#"$main"}
  out=/tmp/$PREFIX-build
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$WT/build.yaml")
  if [ ! -f "$WORK/Jellyfin.Plugin.JellyMesh.dll" ] && [ -n "${JM_PLUGIN_DLL:-}" ]; then
    cp "$JM_PLUGIN_DLL" "$WORK/Jellyfin.Plugin.JellyMesh.dll"
  elif [ ! -f "$WORK/Jellyfin.Plugin.JellyMesh.dll" ]; then
    docker exec -w "$src" jellymesh-sdk sh -c "rm -rf $out && dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o $out --nologo -v quiet" >/dev/null
    docker cp "jellymesh-sdk:$out/Jellyfin.Plugin.JellyMesh.dll" "$WORK/Jellyfin.Plugin.JellyMesh.dll"
  fi
  jq -n --arg v "$version" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$WORK/meta.json"
  target=/config/plugins/JellyMesh_$version
  docker exec "$(name_of "$1")" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$WORK/Jellyfin.Plugin.JellyMesh.dll" "$(name_of "$1"):$target/"
  docker cp "$WORK/meta.json" "$(name_of "$1"):$target/"
  docker restart "$(name_of "$1")" >/dev/null
  sleep 3
  wait_ready "$1"
  login "$1"
}

JM_AUTH="MediaBrowser Client=\"JellyMeshPlay\", Device=\"probe\", DeviceId=\"$PREFIX-probe\", Version=\"0.1\""
# as_jm <method> <path>: the request of the dedicated user of the peer on local. Prints the status; the body is in
# $WORK/jm-body and the headers in $WORK/jm-head.
as_jm() {
  curl -sS -o "$WORK/jm-body" -D "$WORK/jm-head" -w '%{http_code}' -X "$1" -H "$ACCEPT" \
    -H "Authorization: $JM_AUTH, Token=\"$(cat "$WORK/token-jm")\"" "http://127.0.0.1:$PORT$2"
}
# items <server> <type> <stub|real>: the ids of the items of that type in the federated folder, or out of it.
items() {
  local pick='startswith("/federata")'
  [ "$3" = real ] && pick='startswith("/federata") | not'
  get "$1" "/Items?Recursive=true&IncludeItemTypes=$2&Fields=Path" | jq -r "[.Items[] | select((.Path // \"\") | $pick) | .Id] | sort | .[]"
}
count() { grep -c . || true; }
library_of() { get local /JellyMesh/Admin/Peers/Options | jq -r --arg n "$1" '.Libraries[] | select(.Name == $n) | .Id'; }
active_streams() { get local /JellyMesh/Admin/Peers | jq -r '.[0].ActiveStreams'; }
# wait_free: the slot of an aborted stream goes when the provider sees the abort.
wait_free() {
  local i
  for i in $(seq 1 30); do
    [ "$(active_streams)" = 0 ] && break
    sleep 1
  done
  active_streams
}
# holder <path> <rate>: a slow reader of a stream, in the background. Prints its pid.
holder() {
  curl -sS -o /dev/null --limit-rate "$2" -H "Authorization: $JM_AUTH, Token=\"$(cat "$WORK/token-jm")\"" "http://127.0.0.1:$PORT$1" >/dev/null 2>&1 &
  echo $!
}
sha() { shasum -a 256 "$1" | cut -c1-64; }

down
mkdir -p "$WORK/peer-media"
rm -f "$WORK/Jellyfin.Plugin.JellyMesh.dll" "$WORK"/token-*

echo "--- remote: stock, music and books"
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Musica music /media/musica
library remote Libri books /media/libri
user remote jellymesh >/dev/null
scan remote

echo "--- local: the provider"
start local "$PREFIX-media:/media"
for folder in musica libri videomusicali; do docker cp "$MEDIA/remoto-jellymesh/$folder" "$PREFIX-local:/media/$folder"; done
docker cp "$MEDIA/remoto-standard/videodomestici" "$PREFIX-local:/media/videodomestici"
docker exec "$PREFIX-local" sh -c "mkdir -p '/media/misti/Brano' && cp '/media/musica/JM Artista/JM Album Senza ID (2024)/01 - Ultima Nota.mp3' '/media/misti/Brano/01 - Brano Misto.mp3'"
printf '[00:00.00]Prima riga di prova\n[00:02.00]Seconda riga di prova\n' >"$WORK/prima.lrc"
docker cp "$WORK/prima.lrc" "$PREFIX-local:/media/musica/JM Artista/JM Album Federato (2026)/01 - Prima Traccia.lrc"
# A track too large for the socket buffers between the host and the container, so that a slow reader holds its stream.
docker exec "$PREFIX-local" sh -c 'mkdir -p "/media/musica/JM Artista/JM Album Lungo (2026)" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y   -f lavfi -i "anoisesrc=duration=90:sample_rate=44100" -ac 2 -c:a flac -metadata title=Rumore -metadata artist="JM Artista"   -metadata album="JM Album Lungo" -metadata track=1 "/media/musica/JM Artista/JM Album Lungo (2026)/01 - Rumore.flac"'
install local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
remote_id=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$(port_of remote)" '{Name:"Stock",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"None"}')" | jq -r .Id)
import_all local "$remote_id"
sync_now local
manual_path() { get local /JellyMesh/Admin/Status | jq -r --arg t "$1" '[.Servers[].AutomaticLibraries.ManualPaths[] | select(.LibraryType == $t)][0].Path // ""'; }
music_stubs=$(manual_path music)
book_stubs=$(manual_path books)
check "the stubs of the stock music and books are written" yes "$(docker exec "$PREFIX-local" sh -c "[ -n '$music_stubs' ] && [ -d '$music_stubs' ] && [ -n '$book_stubs' ] && [ -d '$book_stubs' ] && echo yes || echo no")"
library local "Mia Musica" music /media/musica "$music_stubs"
library local "Miei Libri" books /media/libri "$book_stubs"
library local "Miei Video musicali" musicvideos /media/videomusicali
library local "Miei Video domestici" homevideos /media/videodomestici
library local "Solo federati" music /federata/Film
library local "Misti" "" /media/misti
scan local
sync_now local
scan local

misti_track=$(get local "/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | startswith("/media/misti"))][0].Id // ""')
check "local indexes the track of the library without a type" true "$([ -n "$misti_track" ] && echo true || echo false)"
real_tracks=$(items local Audio real | grep -v -x -F "${misti_track:-none}")
stub_tracks=$(items local Audio stub)
stub_books=$(items local Book stub)
real_book=$(get local '/Items?Recursive=true&IncludeItemTypes=Book&Fields=Path' | jq -r '[.Items[] | select((.Path // "") | endswith(".epub") and startswith("/media"))][0].Id')
echo "local: $(echo "$real_tracks" | count) tracks, $(echo "$stub_tracks" | count) track stubs, $(echo "$stub_books" | count) book stubs"
check "local has track stubs in a shared library" true "$([ "$(echo "$stub_tracks" | count)" -gt 0 ] && echo true || echo false)"
check "local has book stubs in a shared library" true "$([ "$(echo "$stub_books" | count)" -gt 0 ] && echo true || echo false)"
check "local has its own photos" true "$([ "$(get local '/Items?Recursive=true&IncludeItemTypes=Photo' | jq '.TotalRecordCount')" -gt 0 ] && echo true || echo false)"

echo "--- what local offers"
offered=$(get local /JellyMesh/Admin/Peers/Options | jq -c '[.Libraries[] | {Name, CollectionType}] | sort_by(.Name)')
check "the libraries offered, with their types" \
  '[{"Name":"Mia Musica","CollectionType":"music"},{"Name":"Miei Libri","CollectionType":"books"},{"Name":"Miei Video domestici","CollectionType":"homevideos"},{"Name":"Miei Video musicali","CollectionType":"musicvideos"},{"Name":"Misti","CollectionType":"mixed"}]' \
  "$offered"
jm_uid=$(user local jellymesh)
ids=$(get local /JellyMesh/Admin/Peers/Options | jq -c '[.Libraries[].Id]')
req local PUT "/JellyMesh/Admin/Peers/$jm_uid" "$(jq -nc --argjson l "$ids" '{SharedLibraryIds: $l, MaxStreams: 1}')" >/dev/null
check "local shares them with a limit of 1" 204 "$(status)"
curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -d '{"Username":"jellymesh","Pw":"jellymesh-test"}' \
  -H "Authorization: $JM_AUTH" "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/token-jm"
as_jm GET /JellyMesh/Provider/Changes >/dev/null
cursor=$(jq -r .Cursor "$WORK/jm-body")

echo "--- the provider API, as the peer"
music=$(library_of "Mia Musica")
home=$(library_of "Miei Video domestici")
books=$(library_of "Miei Libri")
check "tracks of Mia Musica" 200 "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Audio&parentId=$music&limit=500")"
cp "$WORK/jm-body" "$WORK/jm-tracks"
check "only the tracks of local" "$(echo "$real_tracks" | tr -d - | paste -sd, -)" "$(jq -r '[.Items[].Id | gsub("-"; "")] | sort | join(",")' "$WORK/jm-tracks")"
check "every track names its album" 0 "$(jq '[.Items[] | select((.AlbumId // "") == "")] | length' "$WORK/jm-tracks")"
check "the track with a .lrc has lyrics" true "$(jq '[.Items[] | select(.Name == "Prima Traccia")][0].HasLyrics' "$WORK/jm-tracks")"
check "photos read as empty" '200 0' "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Photo&parentId=$home") $(jq '.Items | length' "$WORK/jm-body")"
check "home videos" 200 "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Video&parentId=$home")"
cp "$WORK/jm-body" "$WORK/jm-videos"
check "each home video has its parent" 0 "$(jq '[.Items[] | select((.ParentId // "") == "")] | length' "$WORK/jm-videos")"
check "books" 200 "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Book&parentId=$books")"
check "only the book of local, sent as its format alone" '[".epub"]' "$(jq -c '[.Items[].Path] | sort' "$WORK/jm-body")"
misti=$(library_of "Misti")
check "the library without a type gives no track" '200 0' "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Audio&parentId=$misti") $(jq '.Items | length' "$WORK/jm-body")"
check "its track is not read by id" '200 0' "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Audio&ids=$misti_track") $(jq '.Items | length' "$WORK/jm-body")"
check "no path of local reaches the peer" 0 "$(cat "$WORK/jm-tracks" "$WORK/jm-videos" "$WORK/jm-body" | grep -c '/media/\|/federata/')"

echo "--- stock routes, as the peer"
track=$(get local "/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path" | jq -r '[.Items[] | select(.Name == "Prima Traccia" and ((.Path // "") | startswith("/media")))][0].Id')
album=$(get local "/Items?Recursive=true&IncludeItemTypes=MusicAlbum&Fields=Path" | jq -r '[.Items[] | select((.Name | startswith("JM Album Federato")) and ((.Path // "") | startswith("/media")))][0].Id')
stub_album=$(get local "/Items?Recursive=true&IncludeItemTypes=MusicAlbum&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | startswith("/federata"))][0].Id // ""')
music_video=$(items local MusicVideo real | head -1)
stub_track=$(echo "$stub_tracks" | head -1)
stub_book=$(echo "$stub_books" | head -1)
photo=$(get local '/Items?Recursive=true&IncludeItemTypes=Photo' | jq -r '.Items[0].Id')
book_sha=$(sha "$MEDIA/remoto-jellymesh/libri/JM Libro Epub/JM Libro Epub.epub")
check "lyrics of a track" 200 "$(as_jm GET "/Audio/$track/Lyrics")"
check "a stub track" 404 "$(as_jm GET "/Audio/$stub_track/stream?static=true")"
check "the lyrics of a stub track" 404 "$(as_jm GET "/Audio/$stub_track/Lyrics")"
check "the file of a stub track" 404 "$(as_jm GET "/Items/$stub_track/File")"
check "the file of a stub book" 404 "$(as_jm GET "/Items/$stub_book/File")"
photo_parent=$(get local "/Items/$photo" | jq -r '.ParentId // ""')
check "the album of a photo is a photo album" PhotoAlbum "$(get local "/Items?Ids=$photo_parent" | jq -r '.Items[0].Type')"
check "the cover of a photo album" 404 "$(as_jm GET "/Items/$photo_parent/Images/Primary")"
check "photo albums read as empty" '200 0' "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=PhotoAlbum&parentId=$home") $(jq '.Items | length' "$WORK/jm-body")"
year_folder=$(get local '/Items?Recursive=true&IncludeItemTypes=Folder&Fields=Path' | jq -r '[.Items[] | select(.Path == "/media/videodomestici/2025")][0].Id // ""')
echo "info the folder above a photo album: $(get local "/Items?Ids=$year_folder" | jq -c '.Items[0] | {Type, HasPrimary: (.ImageTags.Primary != null)}'), its cover to the peer: $(as_jm GET "/Items/$year_folder/Images/Primary")"
check "the file of a photo" 404 "$(as_jm GET "/Items/$photo/File")"
check "the image of a photo" 404 "$(as_jm GET "/Items/$photo/Images/Primary")"
check "the file of an album" 404 "$(as_jm GET "/Items/$album/File")"
check "the cover of an album" 200 "$(as_jm GET "/Items/$album/Images/Primary")"
check "the cover of a stub album" 404 "$(as_jm GET "/Items/$stub_album/Images/Primary")"
check "the stream of a track of the library without a type" 404 "$(as_jm GET "/Audio/$misti_track/stream?static=true")"
check "its lyrics" 404 "$(as_jm GET "/Audio/$misti_track/Lyrics")"
misti_album=$(get local "/Items?Recursive=true&IncludeItemTypes=MusicAlbum&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | startswith("/media/misti"))][0].Id // ""')
if [ -n "$misti_album" ]; then
  check "the album of the library without a type" 404 "$(as_jm GET "/Items/$misti_album/Images/Primary")"
else
  echo "info no album in the library without a type"
fi
check "the universal audio route" 403 "$(as_jm GET "/Audio/$track/universal?Container=mp3")"
check "the file of a book" 200 "$(as_jm GET "/Items/$real_book/File")"
check "the file is the book" "$book_sha" "$(sha "$WORK/jm-body")"
check "the download of a book" 200 "$(as_jm GET "/Items/$real_book/Download")"
echo "Download sends: $(grep -i '^content-disposition:' "$WORK/jm-head" | tr -d '\r')"
check "the download names no folder of local" 0 "$(grep -i '^content-disposition:' "$WORK/jm-head" | grep -c '/media\|/federata')"
check "an audio stream" 200 "$(as_jm GET "/Audio/$track/stream?static=true")"
check "an audio stream by container" 200 "$(as_jm GET "/Audio/$track/stream.mp3?static=true")"
check "no slot left behind" 0 "$(wait_free)"

echo "--- the stream limit on the audio and file routes"
pid=$(holder "/Items/$music_video/File" 100k)
sleep 2
check "a file holds the slot" 1 "$(active_streams)"
check "an audio stream over the limit" 429 "$(as_jm GET "/Audio/$track/stream?static=true")"
check "it carries the signal" 1 "$(grep -ci 'x-jellymesh-refusal: stream-limit' "$WORK/jm-head")"
check "an audio stream by container over the limit" 429 "$(as_jm GET "/Audio/$track/stream.mp3?static=true")"
check "a book over the limit" 429 "$(as_jm GET "/Items/$real_book/File")"
check "a download over the limit" 429 "$(as_jm GET "/Items/$real_book/Download")"
check "lyrics take no slot" 200 "$(as_jm GET "/Audio/$track/Lyrics")"
kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
check "the aborted file frees its slot" 0 "$(wait_free)"
long=$(get local "/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | contains("Rumore"))][0].Id')
pid=$(holder "/Audio/$long/stream?static=true" 50k)
sleep 2
check "an audio stream holds the slot" 1 "$(active_streams)"
check "a file over the limit" 429 "$(as_jm GET "/Items/$music_video/File")"
kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
check "the aborted audio stream frees its slot" 0 "$(wait_free)"

echo "--- without the download permission"
jm_policy=$(get local "/Users/$jm_uid" | jq -c '.Policy | .EnableContentDownloading = false')
post local "/Users/$jm_uid/Policy" "$jm_policy" >/dev/null
check "the policy is saved" 204 "$(status)"
check "the download of a book is refused" 403 "$(as_jm GET "/Items/$real_book/Download")"
check "its file is served" 200 "$(as_jm GET "/Items/$real_book/File")"
check "no slot left behind" 0 "$(wait_free)"

echo "--- the peer"
start peer "$WORK/peer-media:/media"
install peer
settings peer '.FederatedFolderPath = "/federata"' >/dev/null
local_id=$(post peer /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$PORT" '{Name:"Provider",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"ByType"}')" | jq -r .Id)
check "the peer is offered the five libraries" 5 "$(check_server peer "$local_id" | jq '.Libraries | length')"
import_all peer "$local_id"
sync_now peer
scan peer
sync_now peer
scan peer
check "every track of local, no stub" "$(echo "$real_tracks" | count)" "$(items peer Audio stub | count)"
peer_album=$(get peer '/Items?Recursive=true&IncludeItemTypes=MusicAlbum' | jq -r '[.Items[] | select(.Name | startswith("JM Album Federato"))][0].Id // ""')
check "the album of the track" true "$([ -n "$peer_album" ] && echo true || echo false)"
check "the album has its cover" true "$(get peer "/Items?Ids=$peer_album" | jq '.Items[0].ImageTags.Primary != null')"
peer_track=$(get peer "/Items?Recursive=true&IncludeItemTypes=Audio" | jq -r '[.Items[] | select(.Name == "Prima Traccia")][0].Id')
check "the track has lyrics" true "$(get peer "/Items?Ids=$peer_track" | jq '.Items[0].HasLyrics')"
check "its lyrics come from local" 200 "$(get peer "/Audio/$peer_track/Lyrics" >/dev/null; status)"
check "the lyrics are those of the .lrc" true "$(jq '[.Lyrics[].Text] | index("Prima riga di prova") != null' "$WORK/body")"
check "a music video" 1 "$(items peer MusicVideo stub | count)"
check "the home videos" 3 "$(items peer Video stub | count)"
get peer '/Items?Recursive=true&IncludeItemTypes=Video&Fields=Path' >"$WORK/peer-videos"
check "a home video in its folders" 1 "$(jq '[.Items[] | select((.Path // "") | test("/2025/Inverno/"))] | length' "$WORK/peer-videos")"
# A folder with photos is a photo album, held back with the photos until F60: its video lands without that folder.
check "a home video beside photos, without their album" '1 0' "$(jq -r '"\([.Items[] | select(.Name == "Spiaggia")] | length) \([.Items[] | select((.Path // "") | test("/Estate/"))] | length)"' "$WORK/peer-videos")"
check "no photo" 0 "$(items peer Photo stub | count)"
peer_book=$(get peer '/Items?Recursive=true&IncludeItemTypes=Book&Fields=Path' | jq -r '[.Items[] | select((.Path // "") | (startswith("/federata") and endswith(".epub")))][0].Id // ""')
check "the epub book" true "$([ -n "$peer_book" ] && echo true || echo false)"
check "the book file through the peer" 200 "$(get peer "/Items/$peer_book/File" >/dev/null; status)"
check "it is the book of local" "$book_sha" "$(sha "$WORK/body")"
check "no slot left behind" 0 "$(wait_free)"

echo "--- the change feed"
docker exec "$PREFIX-local" sh -c "mkdir -p '/media/musica/JM Artista/JM Album Nuovo (2026)' && cp '/media/musica/JM Artista/JM Album Senza ID (2024)/01 - Ultima Nota.mp3' '/media/musica/JM Artista/JM Album Nuovo (2026)/01 - Nuova Nota.mp3'"
scan local
new=$(get local "/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | contains("Nuova Nota"))][0].Id | gsub("-"; "")')
check "the feed reads" 200 "$(as_jm GET "/JellyMesh/Provider/Changes?cursor=$cursor")"
check "the new track is in it, from Mia Musica" "[\"$(echo "$music" | tr -d -)\"]" "$(jq -c --arg id "$new" '[.Changes[] | select((.Id | gsub("-"; "")) == $id)][0].LibraryIds | map(gsub("-"; ""))' "$WORK/jm-body")"
check "no stub is in it" 0 "$(jq -r '.Changes[].Id' "$WORK/jm-body" | tr -d - | grep -c -F -f <(printf '%s\n%s\n' "$stub_tracks" "$stub_books" | tr -d - | grep .) || true)"
sync_now peer
scan peer
check "the peer has the new track" "$(($(echo "$real_tracks" | count) + 1))" "$(items peer Audio stub | count)"

echo "--- an album change in the feed"
as_jm GET /JellyMesh/Provider/Changes >/dev/null
cursor=$(jq -r .Cursor "$WORK/jm-body")
album_tracks=$(get local "/Items?ParentId=$album&Recursive=true&IncludeItemTypes=Audio" | jq -r '[.Items[].Id | gsub("-"; "")] | sort | .[]')
album_item=$(get local "/Items/$album")
check "the album reads" 200 "$(status)"
post local "/Items/$album" "$(echo "$album_item" | jq -c '.Overview = "Album modificato dalla prova"')" >/dev/null
check "the album is edited" 204 "$(status)"
check "the feed reads" 200 "$(as_jm GET "/JellyMesh/Provider/Changes?cursor=$cursor")"
echo "feed after the album edit: $(jq -c '[.Changes[] | {Id, Type}]' "$WORK/jm-body")"
check "the album has tracks" true "$([ "$(echo "$album_tracks" | count)" -gt 0 ] && echo true || echo false)"
check "every track of the album is in it" "$(echo "$album_tracks" | count)" "$(jq -r '.Changes[] | select(.Type == "Audio") | .Id | gsub("-"; "")' "$WORK/jm-body" | grep -c -x -F -f <(echo "$album_tracks") || true)"
check "the album itself is not" 0 "$(jq -r '.Changes[].Id | gsub("-"; "")' "$WORK/jm-body" | grep -c -x -F "$(echo "$album" | tr -d -)" || true)"
check "no track of the library without a type is in it" 0 "$(jq -r '.Changes[].Id | gsub("-"; "")' "$WORK/jm-body" | grep -c -x -F "$(echo "$misti_track" | tr -d -)" || true)"

echo "--- a cleared federated folder leaves no stub to a peer"
settings local '.FederatedFolderPath = ""' >/dev/null
check "the folder is cleared" '""' "$(get local /JellyMesh/Admin/Settings | jq -c .FederatedFolderPath)"
check "a stub track" 404 "$(as_jm GET "/Audio/$stub_track/stream?static=true")"
check "the file of a stub book" 404 "$(as_jm GET "/Items/$stub_book/File")"
check "tracks of Mia Musica" 200 "$(as_jm GET "/JellyMesh/Provider/Items?includeItemTypes=Audio&parentId=$music&limit=500")"
check "still only the tracks of local" "$(items local Audio real | grep -v -x -F "$misti_track" | tr -d - | paste -sd, -)" "$(jq -r '[.Items[].Id | gsub("-"; "")] | sort | join(",")' "$WORK/jm-body")"

echo "=== finished, failed: $FAILED"
exit $FAILED
