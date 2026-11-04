#!/usr/bin/env bash
#
# Phase 0, point 7: does "watched" survive when a title moves from a federated
# .strm to a real local file?
#
# Each scenario starts a private instance, marks the stub played and adds a
# partial resume position, then swaps the stub for a real file. The real file
# lives in /local-extra, standing in for a local library path, because /media is
# read-only. Results land in $WORK/results/w-<scenario>.txt, with the UserData
# rows from the database, since the API shows only one row per item.
#
# Run one script at a time, or give each its own WORK, NAME and PORT: they share
# the fixture folders under WORK. F12 needs TMDb reachable.
#
# Usage: WORK=<dir> REPO_MEDIA=<media/locale> watch-state.sh <scenario>... | all | clean
# Env:   LOCK=0 leaves <lockdata> out of the stubs. SUFFIX is added to the file name.
#        LOG_LEVEL=Debug also prints how the server removed the stub.

cd "$(dirname "$0")"
source ./private-instance.sh

LOCK=${LOCK:-1}
SUFFIX=${SUFFIX:-}
TARGET=http://jellymesh.invalid/JellyMesh/Play
RESUME=123450000
SRC_MOVIE="$REPO_MEDIA/film/Sintel (2010)/Sintel (2010).mkv"
SRC_EP="$REPO_MEDIA/serie/Serie Senza ID (2026)/Season 01/Serie Senza ID S01E01.mkv"
TMDB=999000501
IMDB=tt99000501
TVDB=999000502

lockdata() { [ "$LOCK" = 1 ] && echo "  <lockdata>true</lockdata>" || true; }

movie_ids() { # with | imdb | without
  [ "$1" = with ] && echo "  <uniqueid type=\"tmdb\" default=\"true\">$TMDB</uniqueid>"
  [ "$1" = with ] || [ "$1" = imdb ] && echo "  <uniqueid type=\"imdb\">$IMDB</uniqueid>"
  return 0
}

series_ids() { # with | tmdb | without
  case $1 in
    with) echo "  <uniqueid type=\"tvdb\">$TVDB</uniqueid>" ;;
    tmdb) echo "  <uniqueid type=\"tmdb\" default=\"true\">$TMDB</uniqueid>" ;;
  esac
}

movie_nfo() { # <dir> <ids> <locked>
  cat >"$1/movie.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>JM Stato Film</title>
  <year>2026</year>
  <plot>Spike.</plot>
$(movie_ids "$2")
$3
</movie>
XML
}

tvshow_nfo() { # <dir> <ids> <locked>
  cat >"$1/tvshow.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>JM Stato Serie</title>
  <year>2026</year>
  <plot>Spike.</plot>
$(series_ids "$2")
$3
</tvshow>
XML
}

stub_movie() { # <ids>
  local dir="$FED/Film/JM Stato Film (2026)"
  mkdir -p "$dir"
  echo "$TARGET/film" >"$dir/JM Stato Film (2026).strm"
  movie_nfo "$dir" "$1" "$(lockdata)"
}

remove_stub_movie() { rm -rf "${FED:?}/Film/JM Stato Film (2026)"; }

real_movie() { # <ids> <folder and file name>
  local dir="$EXTRA/film/$2"
  mkdir -p "$dir"
  cp "$SRC_MOVIE" "$dir/$2.mkv"
  movie_nfo "$dir" "$1" ""
}

stub_episode() { # <ids>
  local dir="$FED/Serie/JM Stato Serie (2026)"
  mkdir -p "$dir/Season 01"
  tvshow_nfo "$dir" "$1" "$(lockdata)"
  echo "$TARGET/ep" >"$dir/Season 01/JM Stato Serie S01E02.strm"
  cat >"$dir/Season 01/JM Stato Serie S01E02.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<episodedetails>
  <title>Episodio Stub</title>
  <season>1</season>
  <episode>2</episode>
$(lockdata)
</episodedetails>
XML
}

remove_stub_episode() { rm -f "${FED:?}/Serie/JM Stato Serie (2026)/Season 01/"*; }
remove_stub_series() { rm -rf "${FED:?}/Serie/JM Stato Serie (2026)"; }

# The local series already holds S01E01 in every scenario, so its Series row
# exists before the title arrives, as for a real local series.
local_series() { # <ids> <folder name>
  local dir="$EXTRA/serie/$2"
  mkdir -p "$dir/Season 01"
  tvshow_nfo "$dir" "$1" ""
  cp "$SRC_EP" "$dir/Season 01/JM Stato Serie S01E01.mkv"
}

real_episode() { # <folder name> <file name>
  cp "$SRC_EP" "$EXTRA/serie/$1/Season 01/$2.mkv"
}

libraries() {
  local o='{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
  add_library Film movies "$o" /federata/Film /local-extra/film
  add_library Serie tvshows "$o" /federata/Serie /local-extra/serie
}

find_item() { # <Movie|Episode> <root path prefix>
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=$1&Fields=Path" |
    jq -r --arg p "$2" '[.Items[] | select(.Path | startswith($p)) | select(.Name | test("S01E01$") | not)][0].Id // empty'
}

mark() { # <item id>
  post "/UserPlayedItems/$1?userId=$USER_ID" >/dev/null
  post "/UserItems/$1/UserData?userId=$USER_ID" "{\"PlaybackPositionTicks\":$RESUME}" >/dev/null
}

state() { # <label> <Movie|Episode>
  echo "### $1"
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=$2&Fields=Path,ProviderIds&enableUserData=true" |
    jq -c '.Items[] | select(.Name | test("S01E01$") | not) | {Id: .Id[0:8], Path, ProviderIds, Played: .UserData.Played, PlayCount: .UserData.PlayCount, Ticks: .UserData.PlaybackPositionTicks, Key: .UserData.Key}'
  [ -z "${LOG_LEVEL:-}" ] || docker logs "$NAME" 2>&1 |
    grep -E "Removed item|Removing item|Deleting item|Item type changed|stale|JM Stato" | grep -v "Probing\|ffprobe" | tail -8 | cut -c1-170
  sql "select case when u.ItemId like '00000000-%' then 'PLACEHOLDER' else substr(u.ItemId,1,8) end item, u.CustomDataKey key, u.Played played, u.PlayCount count, u.PlaybackPositionTicks ticks, substr(u.RetentionDate,1,10) retention, coalesce(b.Path,'') path
       from UserData u left join BaseItems b on b.Id = u.ItemId order by item, key;"
  echo
}

# Film scenarios. arg 1: IDs on the stub and on the real file.
start_film() {
  reset_fixtures; up; libraries
  stub_movie "$1"
  scan
  mark "$(find_item Movie /federata/)"
  state "stub marked played with resume" Movie
}

# Stub removed and scanned, then the real file arrives in a later scan.
film_stub_first() { # <ids> [name]
  local name=${2:-JM Stato Film (2026)}
  start_film "$1"
  remove_stub_movie
  scan
  state "stub removed, scanned" Movie
  real_movie "$1" "$name"
  scan
  state "real file added, scanned" Movie
}

# Real file arrives first and both coexist, as the plan's post-scan task expects.
film_real_first() { # <ids> [name]
  local name=${2:-JM Stato Film (2026)}
  start_film "$1"
  real_movie "$1" "$name"
  scan
  state "real file added, both present" Movie
  remove_stub_movie
  scan
  state "stub removed, scanned" Movie
  scan
  state "one more scan" Movie
}

# Swap inside a single scan.
film_same_scan() { # <ids>
  start_film "$1"
  remove_stub_movie
  real_movie "$1" "JM Stato Film (2026)"
  scan
  state "swapped within one scan" Movie
}

f1() { film_stub_first with; }
f2() { film_real_first with; }
f3() { film_same_scan with; }
f4() { film_stub_first with "Copia Locale Diversa (2026)"; }
f5() { film_real_first with "Copia Locale Diversa (2026)"; }
f6() { film_stub_first without; }
f7() { film_real_first without; }
f8() { film_same_scan without; }

# Episode scenarios. arg 1: series IDs. arg 2: name of the local series folder.
start_ep() {
  reset_fixtures; up; libraries
  local_series "$1" "$2"
  stub_episode "$1"
  scan
  mark "$(find_item Episode /federata/)"
  state "stub marked played with resume" Episode
}

ep_stub_first() { # <ids> <local folder> <file name>
  start_ep "$1" "$2"
  remove_stub_episode
  scan
  state "stub removed, scanned" Episode
  real_episode "$2" "$3"
  scan
  state "real file added, scanned" Episode
}

ep_real_first() { # <ids> <local folder> <file name>
  start_ep "$1" "$2"
  real_episode "$2" "$3"
  scan
  state "real file added, both present" Episode
  remove_stub_episode
  scan
  state "stub removed, scanned" Episode
  scan
  state "one more scan" Episode
  remove_stub_series
  scan
  state "federated series folder removed, scanned" Episode
}

ep_same_scan() { # <ids> <local folder> <file name>
  start_ep "$1" "$2"
  remove_stub_episode
  real_episode "$2" "$3"
  scan
  state "swapped within one scan" Episode
}

SAME="JM Stato Serie (2026)"
OTHER="JM Stato Serie Locale"
EP="JM Stato Serie S01E02"

e1() { ep_stub_first with "$SAME" "$EP"; }
e2() { ep_real_first with "$SAME" "$EP"; }
e3() { ep_same_scan with "$SAME" "$EP"; }
e4() { ep_stub_first with "$OTHER" "1x02 nome diverso"; }
e5() { ep_real_first with "$OTHER" "1x02 nome diverso"; }
e6() { ep_stub_first without "$SAME" "$EP"; }
e7() { ep_real_first without "$SAME" "$EP"; }
e8() { ep_stub_first tmdb "$SAME" "$EP"; }
e9() { ep_real_first tmdb "$SAME" "$EP"; }

# What a plugin could do before removing the stub: read each user's state from
# the stub and write it to the real item. Uses REST, as a stand-in for
# IUserDataManager, so the spike needs no plugin.
copy_state() { # <from id> <to id>
  local data
  data=$(get "/UserItems/$1/UserData?userId=$USER_ID" | jq -c '{Played, PlayCount, PlaybackPositionTicks, LastPlayedDate, IsFavorite}')
  echo "copying $data"
  post "/UserItems/$2/UserData?userId=$USER_ID" "$data" >/dev/null
}

# F9: real file first, state copied, then the stub goes.
f9() {
  start_film with
  real_movie with "JM Stato Film (2026)"
  scan
  copy_state "$(find_item Movie /federata/)" "$(find_item Movie /local-extra/)"
  state "state copied, both present" Movie
  remove_stub_movie
  scan
  state "stub removed, scanned" Movie
}

# F9b: the same with no provider IDs, where Jellyfin has no key to go on.
f9b() {
  start_film without
  real_movie without "JM Stato Film (2026)"
  scan
  copy_state "$(find_item Movie /federata/)" "$(find_item Movie /local-extra/)"
  remove_stub_movie
  scan
  state "state copied, stub removed, scanned" Movie
}

# F13: real file first, as F2, but with both under the same library path. Tells
# whether the reattach in Folder.cs works within a path and fails across paths.
f13() {
  start_film with
  local dir="$FED/Film/Copia Reale (2026)"
  mkdir -p "$dir"
  cp "$SRC_MOVIE" "$dir/Copia Reale (2026).mkv"
  movie_nfo "$dir" with ""
  scan
  state "real file added in the same path, both present" Movie
  remove_stub_movie
  scan
  state "stub removed, scanned" Movie
}

# F10: the state is stuck under the placeholder. Does a refresh pick it up?
f10() {
  film_real_first with
  local id
  id=$(find_item Movie /local-extra/)
  post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false&replaceAllImages=false"
  sleep 6
  state "after a full refresh of the real item" Movie
  post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=true&replaceAllImages=false"
  sleep 6
  state "after replacing all metadata of the real item" Movie
}

# F11: IDs overlap only in part: IMDb on the stub, TMDb and IMDb on the file.
f11() {
  start_film imdb
  remove_stub_movie
  scan
  real_movie with "JM Stato Film (2026)"
  scan
  state "real file added, IDs overlap on IMDb only" Movie
}

# F12: a real local copy has no .nfo, so its IDs come from a fetcher. Needs TMDb.
f12() {
  reset_fixtures; up; libraries
  local dir="$FED/Film/Elephants Dream (2006)"
  mkdir -p "$dir"
  echo "$TARGET/ed" >"$dir/Elephants Dream (2006).strm"
  cat >"$dir/movie.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>Elephants Dream</title>
  <year>2006</year>
  <uniqueid type="tmdb" default="true">9761</uniqueid>
  <uniqueid type="imdb">tt0807840</uniqueid>
$(lockdata)
</movie>
XML
  scan
  mark "$(find_item Movie /federata/)"
  state "stub marked played with resume" Movie
  rm -rf "${FED:?}/Film/Elephants Dream (2006)"
  scan
  mkdir -p "$EXTRA/film/Elephants Dream (2006)"
  cp "$SRC_MOVIE" "$EXTRA/film/Elephants Dream (2006)/Elephants Dream (2006).mkv"
  scan
  state "real file with no .nfo, stub removed first" Movie
}

# E10: same as F9, for an episode.
e10() {
  start_ep with "$SAME"
  real_episode "$SAME" "$EP"
  scan
  copy_state "$(find_item Episode /federata/)" "$(find_item Episode /local-extra/)"
  state "state copied, both present" Episode
  remove_stub_series
  scan
  state "stub series removed, scanned" Episode
}

# E10b: the same with a series Jellyfin keys by row Id, here TMDb only.
e10b() {
  start_ep tmdb "$SAME"
  real_episode "$SAME" "$EP"
  scan
  copy_state "$(find_item Episode /federata/)" "$(find_item Episode /local-extra/)"
  remove_stub_series
  scan
  state "state copied, stub series removed, scanned" Episode
}

clean() {
  down
  rm -rf "${FED:?}" "${EXTRA:?}"
}

[ "${1:-}" = clean ] && { clean; exit; }
[ "${1:-}" = all ] && set -- f1 f2 f3 f4 f5 f6 f7 f8 f9 f9b f10 f11 f12 f13 e1 e2 e3 e4 e5 e6 e7 e8 e9 e10 e10b
for s in "$@"; do
  echo "== $s"
  "$s" >"$RESULTS/w-$s$SUFFIX.txt" 2>&1 || echo "$s failed, see results"
done
down
