#!/usr/bin/env bash
# Integration 13 of the 0.6-0.7 spec (F61, row 458): the new stock routes on a federated track and book. Anonymous gets
# 401 and a user who cannot see the title 404; without the download permission Download is 403 and File is served (D12).
# No answer holds a remote token, item or server ID, or the remote address (leak.py). The photo image is F60's.
# usage: [PREFIX=jm-f61] [PORT=18610] [KEEP=1] permissions.sh   (KEEP=1 leaves the containers up; lib.sh has down)
set -euo pipefail
PREFIX=${PREFIX:-jm-f61}
PORT=${PORT:-18610}
MEDIA=${MEDIA:-unused}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
[ "${KEEP:-0}" = 1 ] || trap down EXIT

# install: builds the plugin of this worktree in the SDK container of the project and installs it, then restarts.
install() {
  local main src out version target
  main=$(git -C "$WT" worktree list | head -1 | awk '{print $1}')
  src=/src${WT#"$main"}
  out=/tmp/$PREFIX-build
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$WT/build.yaml")
  docker exec -w "$src" jellymesh-sdk sh -c "rm -rf $out && dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o $out --nologo -v quiet" >/dev/null
  jq -n --arg v "$version" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$WORK/meta.json"
  docker cp "jellymesh-sdk:$out/Jellyfin.Plugin.JellyMesh.dll" "$WORK/Jellyfin.Plugin.JellyMesh.dll"
  target=/config/plugins/JellyMesh_$version
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$WORK/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/"
  docker cp "$WORK/meta.json" "$PREFIX-local:$target/"
  docker restart "$PREFIX-local" >/dev/null
}

# A short tagged FLAC track and a minimal EPUB, made in the remote container.
make_media() {
  docker exec "$PREFIX-remote" sh -c '
    mkdir -p "/media/musica/Artista/Album" /media/libri/Autore /tmp/epub/META-INF /tmp/epub/OEBPS
    /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=440:duration=20" -c:a flac \
      -metadata title=Brano -metadata artist=Artista -metadata album_artist=Artista -metadata album=Album -metadata track=1 \
      "/media/musica/Artista/Album/01 - Brano.flac"'
  mkdir -p "$WORK/epub/META-INF" "$WORK/epub/OEBPS"
  printf 'application/epub+zip' >"$WORK/epub/mimetype"
  cat >"$WORK/epub/META-INF/container.xml" <<'XML'
<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
XML
  cat >"$WORK/epub/OEBPS/content.opf" <<'XML'
<?xml version="1.0"?>
<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Libro di prova</dc:title><dc:creator>Autore</dc:creator><dc:identifier id="id">jm-f61-book</dc:identifier><dc:language>it</dc:language></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>
XML
  printf '<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Prova</p></body></html>' >"$WORK/epub/OEBPS/c.xhtml"
  (cd "$WORK/epub" && rm -f ../libro.epub && zip -qX0 ../libro.epub mimetype && zip -qXr9 ../libro.epub META-INF OEBPS)
  docker cp "$WORK/libro.epub" "$PREFIX-remote:/media/libri/Autore/Libro di prova.epub"
}

# as <user> <method> <path>: prints the status; the body goes to $WORK/answers/<n>, kept for the leak check. It runs in
# a subshell, so the count is kept in a file and last() names the body of the latest call.
as() {
  local who=$1 method=$2 path=$3 auth n
  n=$(($(cat "$WORK/answers/count") + 1))
  echo "$n" >"$WORK/answers/count"
  auth="MediaBrowser Client=\"JellyMeshPlay\", Device=\"$who\", DeviceId=\"$PREFIX-$who\", Version=\"0.1\""
  [ "$who" = anonymous ] || auth="$auth, Token=\"$(cat "$WORK/token-$who")\""
  curl -sS -o "$WORK/answers/$n" -D "$WORK/answers/$n.headers" -w '%{http_code}' -X "$method" -H "$ACCEPT" -H "Authorization: $auth" \
    "http://127.0.0.1:$PORT$path"
}
last() { echo "$WORK/answers/$(cat "$WORK/answers/count")"; }
user_login() {
  curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -d "{\"Username\":\"$1\",\"Pw\":\"jellymesh-test\"}" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"$1\", DeviceId=\"$PREFIX-$1\", Version=\"0.1\"" \
    "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/token-$1"
}
policy() { # policy <user> <jq filter>
  local uid
  uid=$(user_id local "$1")
  post local "/Users/$uid/Policy" "$(get local "/Users/$uid" | jq -c ".Policy | $2")" >/dev/null
}
stub_of() { get local "/Items?Recursive=true&IncludeItemTypes=$1&Fields=Path" | jq -r '[.Items[] | select((.Path // "") | startswith("/federata"))][0].Id // ""'; }

down
rm -rf "$WORK/answers" && mkdir -p "$WORK/answers" && echo 0 >"$WORK/answers/count"
start remote "$PREFIX-media:/media"
make_media
library remote Musica music /media/musica
library remote Libri books /media/libri
user remote jellymesh >/dev/null
scan remote

mkdir -p "$WORK/local-media"
start local "$WORK/local-media:/media"
install
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
import_all local "$id"
sync_now local
scan local
sync_now local
scan local

track=$(stub_of Audio)
book=$(stub_of Book)
echo "track stub $track, book stub $book"
check "a track stub" yes "$([ -n "$track" ] && echo yes || echo no)"
check "a book stub" yes "$([ -n "$book" ] && echo yes || echo no)"

user local blind >/dev/null
user local nodownload >/dev/null
policy blind '.EnableAllFolders = false | .EnabledFolders = []'
policy nodownload '.EnableAllFolders = true | .EnableContentDownloading = false'
user_login blind
user_login nodownload
check "blind does not see the track" 404 "$(as blind GET "/Items/$track")"
check "nodownload sees the track" 200 "$(as nodownload GET "/Items/$track")"

routes=(
  "GET /Audio/$track/universal?Container=flac&AudioCodec=flac"
  "HEAD /Audio/$track/universal?Container=flac"
  "GET /Audio/$track/stream?static=true"
  "GET /Audio/$track/stream.flac?static=true"
  "GET /Audio/$track/main.m3u8?AudioCodec=aac"
  "GET /Audio/$track/master.m3u8?MediaSourceId=$track&AudioCodec=aac"
  "GET /Audio/$track/Lyrics"
  "GET /Items/$track/File"
  "GET /Items/$track/Download"
  "GET /MediaSegments/$track"
  "GET /Items/$book/File"
  "GET /Items/$book/Download"
)
for route in "${routes[@]}"; do
  read -r method path <<<"$route"
  check "anonymous $method $path" 401 "$(as anonymous "$method" "$path")"
  check "blind $method $path" 404 "$(as blind "$method" "$path")"
done

check "nodownload Download of the track" 403 "$(as nodownload GET "/Items/$track/Download")"
check "nodownload Download of the book" 403 "$(as nodownload GET "/Items/$book/Download")"
remote_track=$(docker exec "$PREFIX-remote" sha256sum "/media/musica/Artista/Album/01 - Brano.flac" | cut -c1-64)
remote_book=$(docker exec "$PREFIX-remote" sha256sum "/media/libri/Autore/Libro di prova.epub" | cut -c1-64)
check "nodownload File of the track" 200 "$(as nodownload GET "/Items/$track/File")"
check "the File of the track is the remote file" "$remote_track" "$(shasum -a 256 "$(last)" | cut -c1-64)"
check "nodownload File of the book" 200 "$(as nodownload GET "/Items/$book/File")"
check "the File of the book is the remote file" "$remote_book" "$(shasum -a 256 "$(last)" | cut -c1-64)"
check "nodownload stream of the track" 200 "$(as nodownload GET "/Audio/$track/stream?static=true")"
check "admin Download of the book" 200 "$(as local GET "/Items/$book/Download")"

# The answers that hold URLs: the universal stream and the HLS playlists, with the first URI each playlist gives.
uri_of() { case $1 in /*) echo "$1" ;; *) echo "/Audio/$track/$1" ;; esac; }
check "nodownload universal of the track" 200 "$(as nodownload GET "/Audio/$track/universal?Container=flac&AudioCodec=flac")"
check "nodownload main.m3u8 of the track" 200 "$(as nodownload GET "/Audio/$track/main.m3u8?AudioCodec=aac&MediaSourceId=$track")"
segment=$(grep -v '^#' "$(last)" | head -1 | tr -d '\r')
check "nodownload first segment of main.m3u8" 200 "$(as nodownload GET "$(uri_of "$segment")")"
check "nodownload master.m3u8 of the track" 200 "$(as nodownload GET "/Audio/$track/master.m3u8?MediaSourceId=$track&AudioCodec=aac")"
variant=$(grep -v '^#' "$(last)" | head -1 | tr -d '\r')
check "nodownload variant of master.m3u8" 200 "$(as nodownload GET "$(uri_of "$variant")")"

scan=$WORK/leak
rm -rf "$scan" && mkdir -p "$scan/remote-db"
docker cp "$PREFIX-remote:/config/data/jellyfin.db" "$scan/remote-db/"
docker cp "$PREFIX-remote:/config/data/jellyfin.db-wal" "$scan/remote-db/" 2>/dev/null || true
# leak.py reads the sources of the stubs as the registry file of 0.5 held them.
mkdir -p "$scan/db"
docker cp "$PREFIX-local:$DATA/." "$scan/db/"
python3 - "$scan/db/jellymesh.db" "$scan/federated-stubs.json" <<'PY'
import json, sqlite3, sys
rows = sqlite3.connect(sys.argv[1]).execute("select stub_id, server_id, item_id from stub_entry_sources").fetchall()
stubs = {}
for stub, server, item in rows:
    stubs.setdefault(stub, []).append({"ServerId": server, "ItemId": item})
json.dump({"Stubs": [{"Sources": sources} for sources in stubs.values()]}, open(sys.argv[2], "w"))
PY
if REMOTE_PORT=$(port_of remote) python3 "$S/leak.py" "$scan" "$WORK/answers"; then check "no remote value in the answers" 0 0; else check "no remote value in the answers" 0 1; fi

# The URL of a .strm is /JellyMesh/Play/<id>?sig=; the playlists name the proxy, /JellyMesh/Playback/, with the local token.
check "no stub URL in the answers" 0 "$(grep -l -e 'sig=' -e '/JellyMesh/Play/' "$WORK/answers"/* | wc -l | tr -d ' ')"
check "the playlists carry only the local token" 0 "$(grep -ho 'ApiKey=[0-9a-f]*' "$WORK/answers"/* | grep -vc "ApiKey=$(cat "$WORK/token-nodownload")" || true)"

echo "=== finished, failed: $FAILED"
exit $FAILED
