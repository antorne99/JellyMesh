#!/usr/bin/env bash
# Anonymous reads of a track stub left outside the federated folder: the folder is moved, then emptied, and the old
# track is asked for on the stock media routes with no login, before and after a scan. Every call must be 401 or 404
# (row 458); a body is also told apart: the remote file, other audio, or the signed .strm URL (rows 522 and 531).
# usage: [PREFIX=jm-ax] [PORT=19900] [KEEP=1] [JM_PLUGIN_DLL=...] anon-moved.sh   (KEEP=1 leaves the containers up)
set -euo pipefail
PREFIX=${PREFIX:-jm-ax}
PORT=${PORT:-19900}
MEDIA=${MEDIA:-unused}
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"
[ "${KEEP:-0}" = 1 ] || trap down EXIT

# install: builds the plugin of this worktree in the SDK container of the project (or takes JM_PLUGIN_DLL) and installs
# it, then restarts.
install() {
  local main src out version target
  main=$(git -C "$WT" worktree list | head -1 | awk '{print $1}')
  src=/src${WT#"$main"}
  out=/tmp/$PREFIX-build
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$WT/build.yaml")
  if [ -z "${JM_PLUGIN_DLL:-}" ]; then
    docker exec -w "$src" jellymesh-sdk sh -c "rm -rf $out && dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o $out --nologo -v quiet" >/dev/null
  fi
  jq -n --arg v "$version" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$WORK/meta.json"
  if [ -n "${JM_PLUGIN_DLL:-}" ]; then cp "$JM_PLUGIN_DLL" "$WORK/Jellyfin.Plugin.JellyMesh.dll"
  else docker cp "jellymesh-sdk:$out/Jellyfin.Plugin.JellyMesh.dll" "$WORK/Jellyfin.Plugin.JellyMesh.dll"; fi
  target=/config/plugins/JellyMesh_$version
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$WORK/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/"
  docker cp "$WORK/meta.json" "$PREFIX-local:$target/"
  docker restart "$PREFIX-local" >/dev/null
}

# Three short tagged FLAC tracks, made in the remote container.
make_media() {
  docker exec "$PREFIX-remote" sh -c '
    mkdir -p "/media/musica/Artista/Album"
    for n in 1 2 3; do
      /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=$((300 + n * 100)):duration=20" -c:a flac \
        -metadata title="Brano $n" -metadata artist=Artista -metadata album_artist=Artista -metadata album=Album -metadata track=$n \
        "/media/musica/Artista/Album/0$n - Brano $n.flac"
    done'
}

# anon <label> <path>: an anonymous GET; prints status, size, type and what the body is, then checks it.
anon() {
  local label=$1 path=$2 got size type body
  got=$(curl -sS -o "$WORK/body-anon" -D "$WORK/headers-anon" -w '%{http_code}' -m 60 -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"anon\", DeviceId=\"$PREFIX-anon\", Version=\"0.1\"" \
    "http://127.0.0.1:$PORT$path" || true)
  size=$(wc -c <"$WORK/body-anon" | tr -d ' ')
  type=$(tr -d '\r' <"$WORK/headers-anon" | sed -n 's/^[Cc]ontent-[Tt]ype: *//p' | tail -1)
  body=other
  if [ "$(shasum -a 256 "$WORK/body-anon" | cut -c1-64)" = "$remote_sha" ]; then body="the remote file"
  elif grep -qaE '/JellyMesh/Play/|sig=' "$WORK/body-anon"; then body="the signed .strm URL"
  elif [ "$(head -c 4 "$WORK/body-anon")" = fLaC ]; then body="FLAC audio"
  elif [ "$(head -c 3 "$WORK/body-anon")" = ID3 ] || [ "$(head -c 2 "$WORK/body-anon" | od -An -tx1 | tr -d ' ')" = fffb ]; then body="MP3 audio"
  elif [ "$(head -c 7 "$WORK/body-anon")" = '#EXTM3U' ]; then body="a playlist"
  elif [ "$size" -gt 1000 ] && case $type in audio/* | video/* | application/octet-stream*) true ;; *) false ;; esac; then body="media bytes"
  fi
  printf '     %-66s %s %8s bytes %-28s %s\n' "$path" "$got" "$size" "[$type]" "$body"
  case $got in 401 | 404) check "anonymous $label" refused refused ;; *) check "anonymous $label" refused "$got" ;; esac
  case $body in "the remote file" | "the signed .strm URL" | *audio | "media bytes") check "anonymous $label gives nothing of the remote" none "$body" ;; esac
  if [ "$body" = "the signed .strm URL" ]; then
    url=$(grep -aoE 'https?://[^[:space:]]+' "$WORK/body-anon" | head -1)
    check "the signed URL from $label is refused on the published port" 403 \
      "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/$(cut -d/ -f4- <<<"$url")")"
    echo "     the signed URL read inside the server: $(docker exec "$PREFIX-local" sh -c 'curl -sS -o /tmp/leaked -w "%{http_code} " "$1" && sha256sum /tmp/leaked | cut -c1-64' sh "$url" | tr '\n' ' ')(remote file $remote_sha)"
  fi
}

# probe <phase> <item>: the stock media routes of an item, asked for with no login.
probe() {
  local phase=$1 it=$2 variant
  echo "== $phase: item $it"
  anon "$phase stream static" "/Audio/$it/stream?static=true"
  anon "$phase stream.flac static" "/Audio/$it/stream.flac?static=true"
  anon "$phase stream.mp3" "/Audio/$it/stream.mp3"
  anon "$phase universal" "/Audio/$it/universal?Container=flac&AudioCodec=flac"
  anon "$phase universal mp3" "/Audio/$it/universal?Container=mp3&TranscodingContainer=mp3&AudioCodec=mp3"
  anon "$phase Download" "/Items/$it/Download"
  anon "$phase File" "/Items/$it/File"
  anon "$phase master.m3u8" "/Audio/$it/master.m3u8?MediaSourceId=$it&AudioCodec=aac"
  variant=$(grep -av '^#' "$WORK/body-anon" | head -1 | tr -d '\r' || true)
  case $variant in main.m3u8*) anon "$phase master variant" "/Audio/$it/$variant" ;; esac
  anon "$phase main.m3u8" "/Audio/$it/main.m3u8?MediaSourceId=$it&AudioCodec=aac"
  segment=$(grep -av '^#' "$WORK/body-anon" | head -1 | tr -d '\r' || true)
  if [ -n "$segment" ] && [ "$(head -c 7 "$WORK/body-anon")" = '#EXTM3U' ]; then
    case $segment in /*) anon "$phase first segment" "$segment" ;; *) anon "$phase first segment" "/Audio/$it/$segment" ;; esac
  fi
  # Who served it: reads of the .strm target on the local server and stream requests on the remote, since the last probe.
  echo "     local reads of /JellyMesh/Play/: $(docker logs --since "$since" "$PREFIX-local" 2>&1 | grep -c 'JellyMesh/Play/' || true)," \
    "remote stream requests: $(docker logs --since "$since" "$PREFIX-remote" 2>&1 | grep -ciE '/Audio/[^ ]*/(stream|universal)|/Items/[^ ]*/(File|Download)|ffmpeg' || true)"
  since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
}

# where <item>: the path Jellyfin holds for an item, empty when it is gone.
where() { get local "/Items?Ids=$1&Fields=Path" | jq -r '.Items[0].Path // ""'; }
tracks() { get local '/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path' | jq -r '.Items[] | "     \(.Id) \(.Path)"'; }
track_of() { get local '/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path' | jq -r --arg r "$1" '[.Items[] | select((.Path // "") | (startswith($r) and contains("Brano 1")))][0].Id // ""'; }
# registry: the track entries of the plugin database, read from a copy with the host sqlite3.
registry() {
  rm -rf "$WORK/db" && mkdir -p "$WORK/db"
  docker cp "$PREFIX-local:$DATA/jellymesh.db" "$WORK/db/" >/dev/null
  docker cp "$PREFIX-local:$DATA/jellymesh.db-wal" "$WORK/db/" >/dev/null 2>&1 || true
  sqlite3 "$WORK/db/jellymesh.db" "SELECT id, kind, relative_path FROM stub_entries WHERE kind LIKE '%Track%'" | sed 's/^/     /'
}
on_disk() { docker exec "$PREFIX-local" sh -c 'test -f "$1" && echo yes || echo no' sh "$1"; }

down
start remote "$PREFIX-media:/media"
make_media
library remote Musica music /media/musica
user remote jellymesh >/dev/null
scan remote
remote_sha=$(docker exec "$PREFIX-remote" sha256sum "/media/musica/Artista/Album/01 - Brano 1.flac" | cut -c1-64)

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

since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "== tracks before the move"; tracks
old=$(track_of /federata/)
old_strm=$(where "$old")
check "a track stub in the folder" yes "$([ -n "$old" ] && echo yes || echo no)"
echo "     .strm of $old: $(docker exec "$PREFIX-local" cat "$old_strm" | sed 's/sig=.*/sig=.../')"
echo "     libraries: $(get local /Library/VirtualFolders | jq -c '[.[] | {Name, Locations}]')"
echo "     registry:"; registry
probe "control, in the folder" "$old"

# The folder moves: the old stubs stay on disk and in Jellyfin, outside the new root.
docker exec "$PREFIX-local" mkdir -p /fed2
echo "     the folder moves: $(settings local '.FederatedFolderPath = "/fed2"')"
echo "== right after the move"
echo "     old .strm on disk: $(on_disk "$old_strm"), Jellyfin path: $(where "$old")"
probe "moved, before a sync" "$old"
sync_now local
echo "== after the move, before a scan"
echo "     old .strm on disk: $(on_disk "$old_strm"), Jellyfin path: $(where "$old")"
echo "     libraries: $(get local /Library/VirtualFolders | jq -c '[.[] | {Name, Locations}]')"
echo "     registry:"; registry
probe "moved, before a scan" "$old"
scan local
echo "== after the move and a scan"; tracks
echo "     old .strm on disk: $(on_disk "$old_strm"), Jellyfin path: $(where "$old")"
if [ -n "$(where "$old")" ]; then probe "moved, after a scan" "$old"; else echo "     the old track is gone from Jellyfin"; fi
new=$(track_of /fed2/)
if [ -n "$new" ]; then probe "control, in the new folder" "$new"; fi

# The folder is emptied: no file counts as in it (row 522).
echo "     the folder is emptied: $(settings local '.FederatedFolderPath = ""')"
echo "== after emptying the folder"
echo "     old Jellyfin path: $(where "$old")"
if [ -n "$(where "$old")" ]; then probe "emptied" "$old"; else echo "     the old track is gone from Jellyfin"; fi
if [ -n "$new" ] && [ -n "$(where "$new")" ]; then probe "emptied, newer stub" "$new"; fi

echo "=== finished, failed: $FAILED"
exit $FAILED
