#!/usr/bin/env bash
#
# Phase 0, point 1: is a .strm with .nfo and local images indexed correctly in a
# second library path, with no metadata overwritten?
#
# Needs the environment running and stub_server.py listening on STUB_PORT.
# Writes fixtures to federata/Film and removes them with "clean".
#
# Set LOCK=1 to add <lockdata>true</lockdata> to every .nfo, and a fifth fixture
# holding only what the plan puts in a .nfo.
#
# Usage: strm-indexing.sh setup | snapshot <file> | refresh <mode> | task <key> | clean

set -euo pipefail

cd "$(dirname "$0")/.."
source scripts/common.sh

STUB_PORT=${STUB_PORT:-18099}
STUB="http://host.docker.internal:$STUB_PORT"
DIR=federata/Film
FIELDS=Path,OriginalTitle,Overview,Genres,Tags,Taglines,OfficialRating,Studios,People,ProviderIds,ImageTags,BackdropImageTags,RunTimeTicks,MediaSources,LocationType,ProductionYear,LockedFields,LockData

nfo() { # <folder> <extra xml>
  cat >"$DIR/$1/movie.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
$2${LOCK:+
  <lockdata>true</lockdata>}
</movie>
EOF
}

images() { # <folder> <label>
  local src="/media/film/Sintel (2010)/Sintel (2010).mkv" font=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf
  local text="drawtext=fontfile=$font:text='$2':x=20:y=20:fontsize=36:fontcolor=white:box=1:boxcolor=red"
  docker compose exec -T locale /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -loglevel error -nostdin -y \
    -ss 10 -i "$src" -frames:v 1 -vf "scale=600:900:force_original_aspect_ratio=increase,crop=600:900,$text" \
    "/federata/Film/$1/poster.jpg"
  docker compose exec -T locale /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -loglevel error -nostdin -y \
    -ss 20 -i "$src" -frames:v 1 -vf "$text" "/federata/Film/$1/fanart.jpg"
}

setup() {
  # A: real title and IDs, and .nfo values that differ from TMDb's. This is the
  # overwrite test: fetchers are on and know this film.
  local a="Cosmos Laundromat (2015)"
  mkdir -p "$DIR/$a"
  echo "$STUB/JellyMesh/Play/cosmos" >"$DIR/$a/$a.strm"
  nfo "$a" '  <title>Cosmos NFO Title</title>
  <originaltitle>Cosmos NFO Original</originaltitle>
  <year>2015</year>
  <plot>Plot from the NFO.</plot>
  <tagline>Tagline from the NFO</tagline>
  <mpaa>NFO-Rating</mpaa>
  <genre>GenereNFO</genre>
  <studio>Studio NFO</studio>
  <runtime>12</runtime>
  <uniqueid type="tmdb" default="true">358332</uniqueid>
  <uniqueid type="imdb">tt4957236</uniqueid>
  <fileinfo><streamdetails><video><codec>h264</codec><width>1920</width><height>1080</height><durationinseconds>720</durationinseconds></video><audio><codec>aac</codec><channels>2</channels></audio></streamdetails></fileinfo>'
  images "$a" "POSTER LOCALE A"

  # B: invented title and IDs, nothing to fetch.
  local b="JM Strm Sintetico (2026)"
  mkdir -p "$DIR/$b"
  echo "$STUB/JellyMesh/Play/sintetico" >"$DIR/$b/$b.strm"
  nfo "$b" '  <title>JM Strm Sintetico</title>
  <year>2026</year>
  <plot>Plot from the NFO.</plot>
  <uniqueid type="tmdb" default="true">999000201</uniqueid>
  <uniqueid type="imdb">tt99000201</uniqueid>'
  images "$b" "POSTER LOCALE B"

  # C: real title with no IDs in the .nfo, as for a remote title without IDs.
  # Does a fetcher match it by name and overwrite the .nfo?
  local c="Big Buck Bunny (2008)"
  mkdir -p "$DIR/$c"
  echo "$STUB/JellyMesh/Play/bunny" >"$DIR/$c/$c.strm"
  nfo "$c" '  <title>Big Buck Bunny</title>
  <year>2008</year>
  <plot>Plot from the NFO.</plot>'
  images "$c" "POSTER LOCALE C"

  # D: target answers 404, as a remote that lost the title.
  local d="JM Strm Target Rotto (2026)"
  mkdir -p "$DIR/$d"
  echo "$STUB/JellyMesh/Play/missing" >"$DIR/$d/$d.strm"
  nfo "$d" '  <title>JM Strm Target Rotto</title>
  <year>2026</year>
  <plot>Plot from the NFO.</plot>
  <uniqueid type="tmdb" default="true">999000202</uniqueid>'
  images "$d" "POSTER LOCALE D"

  # E: real title, and a .nfo with only title, year and IDs (plan §3.1). Does a
  # lock stop fetchers from completing it?
  [ -n "${LOCK:-}" ] || return 0
  local e="Tears of Steel (2012)"
  mkdir -p "$DIR/$e"
  echo "$STUB/JellyMesh/Play/tears" >"$DIR/$e/$e.strm"
  nfo "$e" '  <title>Tears of Steel</title>
  <year>2012</year>
  <uniqueid type="tmdb" default="true">133701</uniqueid>
  <uniqueid type="imdb">tt2285752</uniqueid>'
  images "$e" "POSTER LOCALE E"
}

items() {
  use_instance locale
  login
  USER_ID=$(get /Users/Me | jq -r .Id)
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Movie&Fields=$FIELDS" |
    jq '[.Items[] | select(.Path | startswith("/federata/"))]'
}

snapshot() {
  items | jq 'map({
      Path, Name, OriginalTitle, ProductionYear, Overview, Genres, Tags, Taglines, OfficialRating,
      Studios: (.Studios | map(.Name)), People: (.People | length), ProviderIds, RunTimeTicks,
      LocationType, LockData, Images: (.ImageTags | keys), Backdrops: (.BackdropImageTags | length),
      Sources: (.MediaSources | map({Protocol, Path, Container, IsRemote, RunTimeTicks, Bitrate,
                                     SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding,
                                     Streams: (.MediaStreams | map(.Type + ":" + (.Codec // "?")))}))
    }) | sort_by(.Path)' >"$1"
}

refresh() { # Default | Full | ReplaceAll
  local query
  case $1 in
    Default) query="metadataRefreshMode=Default&imageRefreshMode=Default" ;;
    Full) query="metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh" ;;
    ReplaceAll) query="metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllMetadata=true&replaceAllImages=true" ;;
  esac
  # items runs in a subshell here, so log in again in this shell afterwards.
  local ids id
  ids=$(items | jq -r '.[].Id')
  use_instance locale
  login
  for id in $ids; do
    post "/Items/$id/Refresh?$query"
  done
}

task() { # <task key>: run a scheduled task and wait for it
  use_instance locale
  login
  local id last i
  id=$(get /ScheduledTasks | jq -r --arg k "$1" '.[] | select(.Key == $k) | .Id')
  last=$(get "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post "/ScheduledTasks/Running/$id"
  for i in $(seq 1 180); do
    [ "$(get "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')" != "$last" ] && break
    sleep 1
  done
  get "/ScheduledTasks/$id" | jq -c '{Key, Status: .LastExecutionResult.Status}'
}

clean() {
  rm -rf "$DIR/Cosmos Laundromat (2015)" "$DIR/JM Strm Sintetico (2026)" \
    "$DIR/Big Buck Bunny (2008)" "$DIR/JM Strm Target Rotto (2026)" "$DIR/Tears of Steel (2012)"
}

"$@"
