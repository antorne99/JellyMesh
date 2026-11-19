#!/usr/bin/env bash
#
# Builds the test media from Blender open movies (see README.md for the matrix).
# Encoding runs inside the Jellyfin image, so the host needs only Docker, and
# real HDR10 is available: jellyfin-ffmpeg ships zscale, Homebrew's ffmpeg may not.
#
# Safe to re-run: existing outputs are skipped. Delete media/ to rebuild.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# MEDIA writes elsewhere, such as the main checkout's media/ when run from a worktree.
MEDIA="${MEDIA:-$ROOT/media}"
SOURCES="$MEDIA/.sources"
IMAGE="$(awk '/image:/ {print $2; exit}' "$ROOT/docker-compose.yml")"
FONT=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf
BLENDER=https://download.blender.org

ffmpeg() {
  docker run --rm --user "$(id -u):$(id -g)" -v "$MEDIA:/work" \
    --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$IMAGE" \
    -hide_banner -loglevel error -nostdin -y "$@"
}

fetch() {
  local file=$1 url=$2
  [ -f "$SOURCES/$file" ] && return
  echo "download  $file"
  curl -fL --retry 3 --progress-bar -o "$SOURCES/$file.part" "$url"
  mv "$SOURCES/$file.part" "$SOURCES/$file"
}

# Video arguments for a codec name, into the global array VIDEO_ARGS.
video_args() {
  local codec=$1 rate=$2 bufsize=$(($2 * 2))
  local limits=(-b:v "${rate}k" -maxrate "${rate}k" -bufsize "${bufsize}k")
  case $codec in
    h264) VIDEO_ARGS=(-c:v libx264 -preset veryfast -pix_fmt yuv420p "${limits[@]}") ;;
    hevc) VIDEO_ARGS=(-c:v libx265 -preset superfast -pix_fmt yuv420p10le -tag:v hvc1
                      -x265-params log-level=error "${limits[@]}") ;;
    av1)  VIDEO_ARGS=(-c:v libsvtav1 -preset 10 -pix_fmt yuv420p10le -b:v "${rate}k") ;;
    hdr10) VIDEO_ARGS=(-c:v libx265 -preset superfast -pix_fmt yuv420p10le -tag:v hvc1
                       -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc
                       -x265-params "log-level=error:hdr10=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400"
                       "${limits[@]}") ;;
    *) echo "unknown codec $codec" >&2; exit 1 ;;
  esac
}

# Filter chain: fit to the exact size, then burn in a label so that playback
# shows which server and which variant is really being served. For HDR10 the
# SDR picture is converted to PQ/BT.2020, so tags and pixels agree.
video_filter() {
  local size=$1 codec=$2 label=$3 w=${1%x*} h=${1#*x}
  local vf="scale=$w:$h:force_original_aspect_ratio=decrease,pad=$w:$h:(ow-iw)/2:(oh-ih)/2,setsar=1"
  vf+=",drawtext=fontfile=$FONT:text='$label':x=24:y=24:fontsize=h/26:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=8"
  if [ "$codec" = hdr10 ]; then
    vf+=",zscale=tin=bt709:min=bt709:pin=bt709:rin=tv:t=smpte2084:m=bt2020nc:p=bt2020:r=tv:npl=100"
    vf+=",format=yuv420p10le"
  fi
  echo "$vf"
}

# encode <output, relative to media/> <source> <start s> <seconds> <WxH> <codec> <kbit/s> <label>
encode() {
  local out=$1 src=$2 start=$3 secs=$4 size=$5 codec=$6 rate=$7 label=$8
  [ -f "$MEDIA/$out" ] && return
  echo "encode    $out"
  mkdir -p "$MEDIA/$(dirname "$out")"
  local tmp ext="${out##*.}"
  tmp="$(dirname "$out")/.tmp-$$.$ext"
  video_args "$codec" "$rate"
  ffmpeg -ss "$start" -t "$secs" -i "/work/.sources/$src" \
    -vf "$(video_filter "$size" "$codec" "$label")" -r 24 \
    "${VIDEO_ARGS[@]}" -c:a aac -ac 2 -b:a 128k "/work/$tmp"
  mv "$MEDIA/$tmp" "$MEDIA/$out"
}

# movie <server> <title> <year> <source> <start> <WxH> <codec> <kbit/s> [ext] [suffix]
# The suffix makes a Jellyfin "version" of a title, e.g. " - 720p".
movie() {
  local server=$1 title=$2 year=$3 src=$4 start=$5 size=$6 codec=$7 rate=$8 ext=${9:-mkv} suffix=${10:-}
  local folder="$title ($year)"
  encode "$server/film/$folder/$folder$suffix.$ext" "$src" "$start" 60 "$size" "$codec" "$rate" \
    "$server - ${size#*x}p $codec ${rate}k"
}

# movie_nfo <server> <title> <year> <tmdb> <imdb>
movie_nfo() {
  local dir="$MEDIA/$1/film/$2 ($3)"
  mkdir -p "$dir"
  cat >"$dir/movie.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>$2</title>
  <year>$3</year>
  <plot>JellyMesh test clip.</plot>
  <lockdata>false</lockdata>
  <uniqueid type="tmdb" default="true">$4</uniqueid>
  <uniqueid type="imdb">$5</uniqueid>
</movie>
EOF
}

# movie_plot <server> <title> <year> <plot>: metadata, but no provider IDs.
movie_plot() {
  local dir="$MEDIA/$1/film/$2 ($3)"
  mkdir -p "$dir"
  cat >"$dir/movie.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>$2</title>
  <year>$3</year>
  <plot>$4</plot>
  <lockdata>false</lockdata>
</movie>
EOF
}

# episode <server> <series> <year> <season> <episode> <source> <WxH> <codec> <kbit/s>
# Each episode number cuts its own part of the source, the same on every server.
episode() {
  local server=$1 series=$2 year=$3 season=$4 ep=$5 src=$6 size=$7 codec=$8 rate=$9
  local tag start=$((100 + season * 100 + ep * 35))
  tag=$(printf 'S%02dE%02d' "$season" "$ep")
  encode "$server/serie/$series ($year)/Season $(printf %02d "$season")/$series $tag.mkv" \
    "$src" "$start" 30 "$size" "$codec" "$rate" "$server - $tag ${size#*x}p $codec ${rate}k"
}

# episodes <server> <series> <year> <season> <first> <last> <source> <WxH> <codec> <kbit/s>
# One file with several episodes. Jellyfin takes IndexNumberEnd from the name.
episodes() {
  local server=$1 series=$2 year=$3 season=$4 first=$5 last=$6 src=$7 size=$8 codec=$9 rate=${10}
  local tag start=$((100 + season * 100 + first * 35))
  tag=$(printf 'S%02dE%02d-E%02d' "$season" "$first" "$last")
  encode "$server/serie/$series ($year)/Season $(printf %02d "$season")/$series $tag.mkv" \
    "$src" "$start" 60 "$size" "$codec" "$rate" "$server - $tag ${size#*x}p $codec ${rate}k"
}

# series_nfo <server> <series> <year> <tmdb> <tvdb> <imdb>
series_nfo() {
  local dir="$MEDIA/$1/serie/$2 ($3)"
  mkdir -p "$dir"
  cat >"$dir/tvshow.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>$2</title>
  <year>$3</year>
  <plot>JellyMesh test series.</plot>
  <uniqueid type="tmdb" default="true">$4</uniqueid>
  <uniqueid type="tvdb">$5</uniqueid>
  <uniqueid type="imdb">$6</uniqueid>
</tvshow>
EOF
}

subtitles() {
  local file=$1 text=$2 i start
  : >"$file"
  for i in 0 1 2 3 4 5 6 7 8 9 10 11; do
    start=$((i * 5))
    printf '%d\n00:00:%02d,000 --> 00:00:%02d,500\n%s %d\n\n' \
      "$((i + 1))" "$start" "$((start + 4))" "$text" "$((i + 1))" >>"$file"
  done
}

# Two audio tracks taken from different films, so a track switch is audible,
# one embedded subtitle track and two external ones (spike point 4).
audio_and_subtitles() {
  local folder="JM Audio e Sottotitoli (2026)" dir="remoto-standard/film/JM Audio e Sottotitoli (2026)"
  local out="$dir/$folder.mkv"
  mkdir -p "$MEDIA/$dir"
  subtitles "$MEDIA/$dir/$folder.ita.srt" "Sottotitolo esterno italiano"
  subtitles "$MEDIA/$dir/$folder.eng.srt" "External English subtitle"
  subtitles "$MEDIA/.sources/embedded.srt" "Sottotitolo incorporato"
  [ -f "$MEDIA/$out" ] && return
  echo "encode    $out"
  local tmp="$dir/.tmp-$$.mkv"
  video_args h264 4000
  ffmpeg -ss 300 -t 60 -i /work/.sources/tears_of_steel_720p.mov \
    -ss 300 -t 60 -i /work/.sources/elephantsdream-720-h264-st-aac.mov \
    -i /work/.sources/embedded.srt \
    -vf "$(video_filter 1920x1080 h264 'remoto-standard - 2 audio 3 sub')" -r 24 \
    -map 0:v:0 -map 0:a:0 -map 1:a:0 -map 2:0 "${VIDEO_ARGS[@]}" \
    -c:a:0 ac3 -b:a:0 192k -ac:a:0 2 -c:a:1 aac -b:a:1 128k -ac:a:1 2 -c:s srt \
    -metadata:s:a:0 language=ita -metadata:s:a:0 title="Traccia 1 (Tears of Steel)" \
    -metadata:s:a:1 language=eng -metadata:s:a:1 title="Track 2 (Elephants Dream)" \
    -metadata:s:s:0 language=ita -shortest "/work/$tmp"
  mv "$MEDIA/$tmp" "$MEDIA/$out"
}

mkdir -p "$SOURCES" "$ROOT/federata/Film" "$ROOT/federata/Serie"
for server in locale remoto-standard remoto-jellymesh; do
  mkdir -p "$MEDIA/$server/film" "$MEDIA/$server/serie"
done

fetch tears_of_steel_720p.mov "$BLENDER/demo/movies/ToS/tears_of_steel_720p.mov"
fetch elephantsdream-720-h264-st-aac.mov "$BLENDER/ED/elephantsdream-720-h264-st-aac.mov"
fetch sintel_trailer-1080p.mp4 "$BLENDER/durian/trailer/sintel_trailer-1080p.mp4"

TOS=tears_of_steel_720p.mov
ED=elephantsdream-720-h264-st-aac.mov
SINTEL=sintel_trailer-1080p.mp4
HD=1920x1080
SD=1280x720

# --- Films with real provider IDs -------------------------------------------

# Local only.
movie_nfo locale "Elephants Dream" 2006 9761 tt0807840
movie locale "Elephants Dream" 2006 "$ED" 120 "$SD" h264 3000

# Remote only, one per remote type.
movie_nfo remoto-standard "Tears of Steel" 2012 133701 tt2285752
movie remoto-standard "Tears of Steel" 2012 "$TOS" 60 "$SD" h264 3000

# Local and remote: the remote copy is better, and local must still win.
movie_nfo locale "Sintel" 2010 45745 tt1727587
movie locale "Sintel" 2010 "$SINTEL" 0 "$SD" h264 1500
movie_nfo remoto-jellymesh "Sintel" 2010 45745 tt1727587
movie remoto-jellymesh "Sintel" 2010 "$SINTEL" 0 "$HD" hevc 6000

# --- Films with synthetic IDs, one per comparison rule ----------------------
# Each pair differs on the criterion under test, and where possible the next
# criterion points the other way, to prove the order of plan §4.3.

movie_nfo remoto-jellymesh "JM Solo JellyMesh" 2026 999000001 tt99000001
movie remoto-jellymesh "JM Solo JellyMesh" 2026 "$TOS" 130 "$SD" h264 3000

# Resolution beats bitrate: 1080p at 3 Mbit/s wins over 720p at 6 Mbit/s.
movie_nfo remoto-standard "JM Confronto Risoluzione" 2026 999000002 tt99000002
movie remoto-standard "JM Confronto Risoluzione" 2026 "$TOS" 200 "$SD" h264 6000
movie_nfo remoto-jellymesh "JM Confronto Risoluzione" 2026 999000002 tt99000002
movie remoto-jellymesh "JM Confronto Risoluzione" 2026 "$TOS" 200 "$HD" h264 3000

# HDR beats bitrate: same size, HDR10 at 3 Mbit/s wins over SDR at 6 Mbit/s.
movie_nfo remoto-standard "JM Confronto HDR" 2026 999000003 tt99000003
movie remoto-standard "JM Confronto HDR" 2026 "$TOS" 270 "$HD" hevc 6000
movie_nfo remoto-jellymesh "JM Confronto HDR" 2026 999000003 tt99000003
movie remoto-jellymesh "JM Confronto HDR" 2026 "$TOS" 270 "$HD" hdr10 3000

# Bitrate alone, same size, range and codec. MP4, because its per-stream bitrate
# is measured, while for MKV Jellyfin only estimates it (see README.md).
movie_nfo remoto-standard "JM Confronto Bitrate" 2026 999000004 tt99000004
movie remoto-standard "JM Confronto Bitrate" 2026 "$TOS" 340 "$HD" h264 3000 mp4
movie_nfo remoto-jellymesh "JM Confronto Bitrate" 2026 999000004 tt99000004
movie remoto-jellymesh "JM Confronto Bitrate" 2026 "$TOS" 340 "$HD" h264 6000 mp4

# Codec: same size and range, AV1 against H.264. SVT-AV1 undershoots its target,
# so the H.264 target is set to land within the 5% bitrate tolerance of plan §4.3.
movie_nfo remoto-standard "JM Confronto Codec" 2026 999000005 tt99000005
movie remoto-standard "JM Confronto Codec" 2026 "$TOS" 410 "$HD" h264 2900
movie_nfo remoto-jellymesh "JM Confronto Codec" 2026 999000005 tt99000005
movie remoto-jellymesh "JM Confronto Codec" 2026 "$TOS" 410 "$HD" av1 4000

# Two versions of one title on the same server.
movie_nfo remoto-standard "JM Versioni Multiple" 2026 999000006 tt99000006
movie remoto-standard "JM Versioni Multiple" 2026 "$TOS" 480 "$HD" h264 4000 mkv " - 1080p"
movie remoto-standard "JM Versioni Multiple" 2026 "$TOS" 480 "$SD" h264 2000 mkv " - 720p"

movie_nfo remoto-standard "JM Audio e Sottotitoli" 2026 999000007 tt99000007
audio_and_subtitles

# --- Films without provider IDs: no .nfo, title and year come from the path ---

# Same title and year on both remotes.
movie remoto-standard "Sonda Senza ID" 2026 "$TOS" 550 "$SD" h264 2000
movie remoto-jellymesh "Sonda Senza ID" 2026 "$TOS" 550 "$HD" h264 4000

# Same title once normalized: accent, article and punctuation differ.
movie remoto-standard "L'Éclisse Fantasma" 2026 "$ED" 200 "$SD" h264 2000
movie remoto-jellymesh "Eclisse Fantasma" 2026 "$ED" 200 "$HD" h264 4000

# Same title, different year: these are two films and must not merge.
movie remoto-standard "Titolo Omonimo" 2024 "$ED" 270 "$SD" h264 2000
movie remoto-jellymesh "Titolo Omonimo" 2025 "$ED" 340 "$SD" h264 2000

# Local and remote without IDs: local must win on title and year alone.
movie locale "Sonda Locale Senza ID" 2026 "$ED" 410 "$SD" h264 2000
movie remoto-standard "Sonda Locale Senza ID" 2026 "$ED" 410 "$HD" h264 4000

# --- Decision 13: what merges and what must not --------------------------------

# A copy without IDs joins the copy that has them, because the runtime confirms it.
movie_nfo remoto-standard "JM Copia Senza ID" 2026 999000008 tt99000008
movie remoto-standard "JM Copia Senza ID" 2026 "$TOS" 620 "$SD" h264 2000
movie remoto-jellymesh "JM Copia Senza ID" 2026 "$TOS" 620 "$HD" h264 4000

# One shared ID is enough, even with the other one different.
movie_nfo remoto-standard "JM ID Parziali" 2026 999000009 tt99000009
movie remoto-standard "JM ID Parziali" 2026 "$TOS" 690 "$SD" h264 2000
movie_nfo remoto-jellymesh "JM ID Parziali" 2026 999000009 tt99000010
movie remoto-jellymesh "JM ID Parziali" 2026 "$TOS" 690 "$HD" h264 4000

# Equal only without articles, and with the same runtime: the plots tell them apart.
movie_plot remoto-standard "La La Sonda Muta" 2026 "Un pianista insegue un sogno tra le colline della California."
movie remoto-standard "La La Sonda Muta" 2026 "$ED" 480 "$SD" h264 2000
movie_plot remoto-jellymesh "Sonda Muta" 2026 "Una spedizione polare perde i contatti con la base dopo una tempesta."
movie remoto-jellymesh "Sonda Muta" 2026 "$ED" 480 "$HD" h264 4000

# The same against a local copy, which must not hide a different film.
movie_plot locale "Sonda Trama" 2026 "Un guardiano del faro scopre un messaggio nella sabbia."
movie locale "Sonda Trama" 2026 "$ED" 550 "$SD" h264 2000
movie_plot remoto-standard "Sonda Trama" 2026 "Due fratelli riaprono una vecchia officina in montagna."
movie remoto-standard "Sonda Trama" 2026 "$ED" 550 "$HD" h264 4000

# --- Series -----------------------------------------------------------------

# Split across all servers. E03 overlaps local and remote, E05-E06 are on both
# remotes at different quality, and season 2 exists on one remote only.
S="JM Serie Divisa"
for server in locale remoto-standard remoto-jellymesh; do
  series_nfo "$server" "$S" 2026 999000101 999000102 tt99000101
done
for ep in 1 2 3; do episode locale "$S" 2026 1 "$ep" "$TOS" "$HD" h264 3000; done
for ep in 3 4 5 6; do episode remoto-standard "$S" 2026 1 "$ep" "$TOS" "$SD" h264 2000; done
for ep in 5 6; do episode remoto-jellymesh "$S" 2026 1 "$ep" "$TOS" "$HD" h264 4000; done
for ep in 1 2; do episode remoto-jellymesh "$S" 2026 2 "$ep" "$TOS" "$SD" h264 2000; done

# No provider IDs: identity is the normalized series title, year, season, episode.
S="Serie Senza ID"
episode locale "$S" 2026 1 1 "$ED" "$SD" h264 2000
episode remoto-standard "$S" 2026 1 2 "$ED" "$SD" h264 2000
episode remoto-jellymesh "$S" 2026 1 2 "$ED" "$HD" h264 4000

# Files with several episodes, one pair of numbers per case (decision 13a).
S="JM Serie Doppia"
for server in locale remoto-standard remoto-jellymesh; do
  series_nfo "$server" "$S" 2026 999000201 999000202 tt99000201
done
# E01-E02: a local double against remote singles, so both stay local.
episodes locale "$S" 2026 1 1 2 "$ED" "$HD" h264 3000
for ep in 1 2; do episode remoto-standard "$S" 2026 1 "$ep" "$ED" "$SD" h264 2000; done
# E03-E04: a double on one remote only, so one stub with the range.
episodes remoto-standard "$S" 2026 1 3 4 "$ED" "$SD" h264 2000
# E05-E06: a double on both remotes, so one stub, the better one, the other as backup.
episodes remoto-standard "$S" 2026 1 5 6 "$ED" "$SD" h264 2000
episodes remoto-jellymesh "$S" 2026 1 5 6 "$ED" "$HD" h264 4000
# E07-E08: E07 is local, so only E08 comes from the double (§4.2 rule 4).
episode locale "$S" 2026 1 7 "$ED" "$SD" h264 2000
episodes remoto-standard "$S" 2026 1 7 8 "$ED" "$SD" h264 2000
# E09-E10: a better single of E10, so each episode has its own source.
episodes remoto-standard "$S" 2026 1 9 10 "$ED" "$SD" h264 2000
episode remoto-jellymesh "$S" 2026 1 10 "$ED" "$HD" h264 4000

# --- 0.7 content types (spec 0.6-0.7, integration test 11) -------------------
# Small files, all generated: tones from ffmpeg, frames of the films above as
# pictures, and books, comics and EXIF written by media-types.py in Python.
# Each server plays its own tone (locale 659 Hz, standard 440, JellyMesh 554,
# a little higher per track), so playback shows which copy is really served.

PY_IMAGE=mcr.microsoft.com/playwright/python:v1.48.0-noble
SCRIPTS="$(cd "$(dirname "$0")" && pwd)"
BITEXACT=(-fflags +bitexact -flags:v +bitexact -flags:a +bitexact)

pymedia() {
  docker run --rm --user "$(id -u):$(id -g)" -v "$MEDIA:/work" -v "$SCRIPTS:/scripts:ro" -w /work \
    "$PY_IMAGE" python3 -I /scripts/media-types.py "$@"
}

tone_of() {
  case $1 in locale) echo 659 ;; remoto-standard) echo 440 ;; remoto-jellymesh) echo 554 ;; esac
}

# Audio arguments for a format name, into the globals AUDIO_ARGS and AUDIO_RATE.
audio_args() {
  case $1 in
    flac16) AUDIO_RATE=44100 AUDIO_ARGS=(-c:a flac -sample_fmt s16) ;;
    flac24) AUDIO_RATE=96000 AUDIO_ARGS=(-c:a flac -sample_fmt s32 -bits_per_raw_sample 24) ;;
    mp3-320) AUDIO_RATE=44100 AUDIO_ARGS=(-c:a libmp3lame -b:a 320k -write_id3v1 0) ;;
    mp3-128) AUDIO_RATE=44100 AUDIO_ARGS=(-c:a libmp3lame -b:a 128k -write_id3v1 0) ;;
    aac) AUDIO_RATE=44100 AUDIO_ARGS=(-c:a aac -b:a 96k -f mp4) ;;
    *) echo "unknown audio format $1" >&2; exit 1 ;;
  esac
}

# audio <output> <seconds> <Hz> <format> <chapters file, relative to media/, or ""> <tag>=<value>...
audio() {
  local out=$1 secs=$2 hz=$3 format=$4 chapters=$5 tags=() kv tmp
  shift 5
  [ -f "$MEDIA/$out" ] && return
  echo "audio     $out"
  mkdir -p "$MEDIA/$(dirname "$out")"
  for kv in "$@"; do tags+=(-metadata "$kv"); done
  audio_args "$format"
  local inputs=(-f lavfi -i "aevalsrc=0.2*sin(2*PI*$hz*t)|0.2*sin(3*PI*$hz*t):s=$AUDIO_RATE:d=$secs")
  local maps=(-map 0:a -map_metadata -1)
  if [ -n "$chapters" ]; then
    inputs+=(-i "/work/$chapters")
    maps=(-map 0:a -map_metadata 1 -map_chapters 1)
  fi
  tmp="$(dirname "$out")/.tmp-$$.${out##*.}"
  ffmpeg "${inputs[@]}" "${maps[@]}" "${AUDIO_ARGS[@]}" "${BITEXACT[@]}" "${tags[@]}" "/work/$tmp"
  mv "$MEDIA/$tmp" "$MEDIA/$out"
}

# chapters <file, relative to media/> <seconds per chapter> <title>...: an ffmetadata file.
chapters() {
  local file=$1 secs=$2 start=0 title
  shift 2
  mkdir -p "$MEDIA/$(dirname "$file")"
  printf ';FFMETADATA1\n' >"$MEDIA/$file"
  for title in "$@"; do
    printf '[CHAPTER]\nTIMEBASE=1/1000\nSTART=%d\nEND=%d\ntitle=%s\n' \
      $((start * 1000)) $(((start + secs) * 1000)) "$title" >>"$MEDIA/$file"
    start=$((start + secs))
  done
}

# picture <output .jpg or .png> <source> <start s> <WxH> <label>: one frame of a film.
picture() {
  local out=$1 src=$2 start=$3 size=$4 label=$5 tmp
  [ -f "$MEDIA/$out" ] && return
  echo "picture   $out"
  mkdir -p "$MEDIA/$(dirname "$out")"
  tmp="$(dirname "$out")/.tmp-$$.${out##*.}"
  ffmpeg -ss "$start" -i "/work/.sources/$src" -frames:v 1 -vf "$(video_filter "$size" h264 "$label")" \
    -q:v 4 -map_metadata -1 "${BITEXACT[@]}" "/work/$tmp"
  mv "$MEDIA/$tmp" "$MEDIA/$out"
}

# photo <output .jpg> <source> <start s> <WxH> <date YYYY:MM:DD HH:MM:SS> <model> [lat,lon,alt]
# A picture with EXIF: camera, dates, exposure, and GPS when given.
photo() {
  local out=$1 src=$2 start=$3 size=$4 date=$5 model=$6 gps=${7:-}
  [ -f "$MEDIA/$out" ] && return
  local raw=".sources/photo-$$.jpg"
  picture "$raw" "$src" "$start" "$size" "$(basename "$out")"
  echo "exif      $out"
  mkdir -p "$MEDIA/$(dirname "$out")"
  pymedia exif "$raw" "$out" "description=JellyMesh test photo" make=JellyMesh "model=$model" \
    "date=$date" ${gps:+"gps=$gps"}
  rm -f "$MEDIA/$raw"
}

# clip <output> <source> <start s> <seconds> <WxH> <kbit/s> <label> [creation time]: like encode, but
# without the source's tags (its artist would become the artist of a music video),
# and dated as a camera does when a creation time is given.
clip() {
  local out=$1 src=$2 start=$3 secs=$4 size=$5 rate=$6 label=$7 date=${8:-} tmp
  [ -f "$MEDIA/$out" ] && return
  echo "encode    $out"
  mkdir -p "$MEDIA/$(dirname "$out")"
  tmp="$(dirname "$out")/.tmp-$$.${out##*.}"
  video_args h264 "$rate"
  ffmpeg -ss "$start" -t "$secs" -i "/work/.sources/$src" -vf "$(video_filter "$size" h264 "$label")" \
    -r 24 "${VIDEO_ARGS[@]}" -c:a aac -ac 2 -b:a 128k -map_metadata -1 ${date:+-metadata "creation_time=$date"} "/work/$tmp"
  mv "$MEDIA/$tmp" "$MEDIA/$out"
}

MB=99000000-0000-4000-8000
ARTIST="JM Artista"

# mb_tag <format> <Vorbis key> <ID3 description>: the tag name Picard writes for that format.
mb_tag() {
  case $1 in flac*) echo "$2" ;; *) echo "$3" ;; esac
}

# album_federato <server> <format> <ext>: three tracks with MusicBrainz IDs, the same on every server.
album_federato() {
  local server=$1 format=$2 ext=$3 n title hz dir
  local album="JM Album Federato" titles=("Prima Traccia" "Seconda Traccia" "Terza Traccia")
  dir="$server/musica/$ARTIST/$album (2026)"
  for n in 1 2 3; do
    title=${titles[$((n - 1))]}
    hz=$(($(tone_of "$server") + 20 * n))
    audio "$dir/0$n - $title.$ext" 20 "$hz" "$format" "" "title=$title" "artist=$ARTIST" \
      "album_artist=$ARTIST" "album=$album" "track=$n/3" "disc=1/1" date=2026 genre=Test \
      "$(mb_tag "$format" MUSICBRAINZ_TRACKID "MusicBrainz Track Id")=$MB-00000000030$n" \
      "$(mb_tag "$format" MUSICBRAINZ_RELEASETRACKID "MusicBrainz Release Track Id")=$MB-00000000040$n" \
      "$(mb_tag "$format" MUSICBRAINZ_ALBUMID "MusicBrainz Album Id")=$MB-000000000101" \
      "$(mb_tag "$format" MUSICBRAINZ_RELEASEGROUPID "MusicBrainz Release Group Id")=$MB-000000000201" \
      "$(mb_tag "$format" MUSICBRAINZ_ARTISTID "MusicBrainz Artist Id")=$MB-000000000001" \
      "$(mb_tag "$format" MUSICBRAINZ_ALBUMARTISTID "MusicBrainz Album Artist Id")=$MB-000000000001"
  done
  picture "$dir/folder.jpg" "$ED" 400 500x500 "$server - $album"
}

# album_doppio <server> <format>: two discs of two FLAC tracks, in Disc folders, with MusicBrainz IDs.
album_doppio() {
  local server=$1 format=$2 disc n i title dir
  local album="JM Album Doppio" titles=("Lato A Uno" "Lato A Due" "Lato B Uno" "Lato B Due")
  dir="$server/musica/$ARTIST/$album (2025)"
  for disc in 1 2; do
    for n in 1 2; do
      i=$(((disc - 1) * 2 + n))
      title=${titles[$((i - 1))]}
      audio "$dir/Disc $disc/0$n - $title.flac" 15 $(($(tone_of "$server") + 20 * i)) "$format" "" \
        "title=$title" "artist=$ARTIST" "album_artist=$ARTIST" "album=$album" "track=$n/2" "disc=$disc/2" \
        date=2025 genre=Test "MUSICBRAINZ_TRACKID=$MB-00000000031$i" "MUSICBRAINZ_RELEASETRACKID=$MB-00000000041$i" \
        "MUSICBRAINZ_ALBUMID=$MB-000000000102" "MUSICBRAINZ_RELEASEGROUPID=$MB-000000000202" \
        "MUSICBRAINZ_ARTISTID=$MB-000000000001" "MUSICBRAINZ_ALBUMARTISTID=$MB-000000000001"
    done
  done
  picture "$dir/folder.jpg" "$ED" 420 500x500 "$server - $album"
}

# album_senza_id <server> <format> <first title> <seconds of track 2>: MP3s with no MusicBrainz IDs.
album_senza_id() {
  local server=$1 format=$2 first=$3 secs=$4 album="JM Album Senza ID" dir
  dir="$server/musica/$ARTIST/$album (2024)"
  audio "$dir/01 - $first.mp3" 25 $(($(tone_of "$server") + 20)) "$format" "" "title=$first" \
    "artist=$ARTIST" "album_artist=$ARTIST" "album=$album" "track=1/2" "disc=1/1" date=2024 genre=Test
  audio "$dir/02 - Canzone Senza ID.mp3" "$secs" $(($(tone_of "$server") + 40)) "$format" "" \
    "title=Canzone Senza ID" "artist=$ARTIST" "album_artist=$ARTIST" "album=$album" "track=2/2" "disc=1/1" \
    date=2024 genre=Test
}

# musicvideo <server> <WxH> <kbit/s>: one music video by the same artist, with a <musicvideo> .nfo.
musicvideo() {
  local server=$1 size=$2 rate=$3 name="JM Video Musicale (2026)" dir
  dir="$server/videomusicali/$name"
  mkdir -p "$MEDIA/$dir"
  cat >"$MEDIA/$dir/$name.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<musicvideo>
  <title>JM Video Musicale</title>
  <artist>$ARTIST</artist>
  <album>JM Album Federato</album>
  <year>2026</year>
  <plot>JellyMesh test music video.</plot>
  <lockdata>false</lockdata>
</musicvideo>
EOF
  clip "$dir/$name.mkv" "$SINTEL" 10 30 "$size" "$rate" "$server - music video ${size#*x}p ${rate}k"
}

# Music: the same album as FLAC and as MP3 on the two remotes and a local copy, a
# multi-disc album that differs in bit depth, and an album with no IDs at all
# whose first title differs by an article.
album_federato remoto-standard flac16 flac
album_federato remoto-jellymesh mp3-320 mp3
album_federato locale mp3-128 mp3
album_doppio remoto-standard flac24
album_doppio remoto-jellymesh flac16
album_senza_id remoto-standard mp3-320 "L'Ultima Nota" 25
album_senza_id remoto-jellymesh mp3-128 "Ultima Nota" 26

cat >"$MEDIA/remoto-standard/musica/$ARTIST/JM Album Federato (2026)/01 - Prima Traccia.lrc" <<EOF
[ti:Prima Traccia]
[ar:$ARTIST]
[al:JM Album Federato]
[00:00.00]Prima riga del testo di prova
[00:05.00]Seconda riga del testo di prova
[00:10.00]Terza riga del testo di prova
[00:15.00]Ultima riga del testo di prova
EOF

musicvideo remoto-standard "$SD" 2000
musicvideo remoto-jellymesh "$HD" 4000

# Home videos in nested folders, dated by creation_time, and photos: an album with
# EXIF and GPS, EXIF without GPS (portrait), a JPEG and a PNG without EXIF, and a
# photo with GPS beside videos whose names it does not start with.
HV=remoto-standard/videodomestici
clip "$HV/2025/Estate/Spiaggia.mp4" "$TOS" 600 20 "$SD" 2000 Spiaggia.mp4 2025-07-14T10:00:00.000000Z
clip "$HV/2025/Inverno/Neve.mkv" "$ED" 400 20 "$SD" 2000 Neve.mkv 2025-12-20T16:30:00.000000Z
clip "$HV/2026/Compleanno.mp4" "$SINTEL" 20 20 "$SD" 2000 Compleanno.mp4 2026-03-02T19:00:00.000000Z
photo "$HV/2025/Estate/Tramonto.jpg" "$TOS" 640 1600x900 "2025:07:14 20:45:00" "Test Camera GPS" 44.4056,8.9463,12
photo "$HV/Album Vacanze 2025/IMG_0001.jpg" "$TOS" 700 1600x1200 "2025:08:10 18:30:00" "Test Camera GPS" 43.7731,11.2560,50
photo "$HV/Album Vacanze 2025/IMG_0002.jpg" "$ED" 500 900x1200 "2025:08:11 09:15:00" "Test Camera"
picture "$HV/Album Vacanze 2025/IMG_0003.jpg" "$SINTEL" 30 1600x900 "IMG_0003, no EXIF"
picture "$HV/Album Vacanze 2025/IMG_0004.png" "$ED" 520 800x600 "IMG_0004, PNG, no EXIF"

# books <server> full|short: EPUB and a one-file audiobook with chapters; with full
# also a PDF over 128 KiB (pdf.js uses Range only above that) with an .opf beside
# it, a CBZ, and an audiobook in three files of two chapters each. Each book is
# alone in its folder, as BookResolver wants.
books() {
  local server=$1 full=$2 dir n pages=()
  dir="$server/libri/JM Libro Epub"
  if [ ! -f "$MEDIA/$dir/JM Libro Epub.epub" ]; then
    picture ".sources/epub-cover.jpg" "$TOS" 720 600x900 "JM Libro Epub"
    mkdir -p "$MEDIA/$dir"
    echo "epub      $dir/JM Libro Epub.epub"
    pymedia epub "$dir/JM Libro Epub.epub" "JM Libro Epub" "JM Autrice" 9790000000014 2026-01-15 .sources/epub-cover.jpg
  fi
  chapters ".sources/chapters-single.txt" 30 "Capitolo 1" "Capitolo 2" "Capitolo 3"
  audio "$server/libri/JM Audiolibro Capitoli/JM Audiolibro Capitoli.m4b" 90 "$(tone_of "$server")" aac \
    .sources/chapters-single.txt "title=JM Audiolibro Capitoli" "album=JM Audiolibro Capitoli" "artist=JM Autrice" \
    "album_artist=JM Autrice" "composer=JM Narratore" date=2026 genre=Audiolibro
  [ "$full" = full ] || return 0

  dir="$server/libri/JM Libro Pdf"
  if [ ! -f "$MEDIA/$dir/JM Libro Pdf.pdf" ]; then
    for n in 1 2 3 4 5 6 7 8; do
      picture ".sources/pdf-$n.jpg" "$TOS" $((100 + n * 40)) 900x1272 "JM Libro Pdf, pagina $n"
      pages+=(".sources/pdf-$n.jpg")
    done
    mkdir -p "$MEDIA/$dir"
    echo "pdf       $dir/JM Libro Pdf.pdf"
    pymedia pdf "$dir/JM Libro Pdf.pdf" "JM Libro Pdf" "JM Autrice" "${pages[@]}"
  fi
  cat >"$MEDIA/$dir/JM Libro Pdf.opf" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" xmlns:opf="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid" opf:scheme="ISBN">9790000000021</dc:identifier>
    <dc:title>JM Libro Pdf</dc:title>
    <dc:creator opf:role="aut">JM Autrice</dc:creator>
    <dc:language>it</dc:language>
    <dc:date>2026-02-01</dc:date>
    <dc:publisher>JellyMesh Test</dc:publisher>
    <dc:description>JellyMesh test PDF.</dc:description>
  </metadata>
</package>
EOF

  dir="$server/libri/JM Fumetto"
  if [ ! -f "$MEDIA/$dir/JM Fumetto.cbz" ]; then
    pages=()
    for n in 1 2 3 4 5 6; do
      picture ".sources/cbz-$n.jpg" "$SINTEL" $((n * 7)) 800x1200 "JM Fumetto, tavola $n"
      pages+=(".sources/cbz-$n.jpg")
    done
    mkdir -p "$MEDIA/$dir"
    echo "cbz       $dir/JM Fumetto.cbz"
    pymedia cbz "$dir/JM Fumetto.cbz" "JM Fumetto" "JM Serie a Fumetti" 1 "JM Autrice" 2026 "${pages[@]}"
  fi

  chapters ".sources/chapters-part.txt" 20 "Inizio" "Fine"
  for n in 1 2 3; do
    audio "$server/libri/JM Audiolibro Parti/JM Audiolibro Parti - Parte $n.mp3" 40 $(($(tone_of "$server") + 30 * n)) \
      mp3-128 .sources/chapters-part.txt "title=Parte $n" "album=JM Audiolibro Parti" "artist=JM Autrice" \
      "album_artist=JM Autrice" "composer=JM Narratore" "track=$n/3" date=2026 genre=Audiolibro
  done
}
books remoto-standard full
books remoto-jellymesh short

# Mixed library (no collection type): a film and a series of its own.
MIX=remoto-standard/mista
mkdir -p "$MEDIA/$MIX/JM Film Misto (2026)" "$MEDIA/$MIX/JM Serie Mista (2026)"
cat >"$MEDIA/$MIX/JM Film Misto (2026)/movie.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>JM Film Misto</title>
  <year>2026</year>
  <plot>JellyMesh test clip in a mixed library.</plot>
  <lockdata>false</lockdata>
  <uniqueid type="tmdb" default="true">999000301</uniqueid>
  <uniqueid type="imdb">tt99000301</uniqueid>
</movie>
EOF
cat >"$MEDIA/$MIX/JM Serie Mista (2026)/tvshow.nfo" <<EOF
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>JM Serie Mista</title>
  <year>2026</year>
  <plot>JellyMesh test series in a mixed library.</plot>
  <uniqueid type="tmdb" default="true">999000401</uniqueid>
  <uniqueid type="tvdb">999000402</uniqueid>
  <uniqueid type="imdb">tt99000401</uniqueid>
</tvshow>
EOF
encode "$MIX/JM Film Misto (2026)/JM Film Misto (2026).mkv" "$TOS" 620 30 "$SD" h264 2000 "remoto-standard - mixed film"
for n in 1 2; do
  encode "$MIX/JM Serie Mista (2026)/Season 01/JM Serie Mista S01E0$n.mkv" "$ED" $((450 + n * 30)) 20 "$SD" h264 2000 \
    "remoto-standard - mixed S01E0$n"
done

echo "done: $(du -sh "$MEDIA" | cut -f1) in $MEDIA"
