#!/usr/bin/env bash
#
# Builds the test media from Blender open movies (see README.md for the matrix).
# Encoding runs inside the Jellyfin image, so the host needs only Docker, and
# real HDR10 is available: jellyfin-ffmpeg ships zscale, Homebrew's ffmpeg may not.
#
# Safe to re-run: existing outputs are skipped. Delete media/ to rebuild.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MEDIA="$ROOT/media"
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

echo "done: $(du -sh "$MEDIA" | cut -f1) in $MEDIA"
