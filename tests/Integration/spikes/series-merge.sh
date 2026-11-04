#!/usr/bin/env bash
#
# Phase 0, point 2: when a series has episodes in the local path and in the
# federated path, does Jellyfin show one series?
#
# Starts a private instance per scenario, on a two-path library:
# /media/serie (read-only, from media/locale) and a writable /federata/Serie.
# Results land in $WORK/results/<scenario>.txt. Never touches the shared instances.
#
# Run one script at a time, or give each its own WORK, NAME and PORT: they share
# the fixture folders under WORK. v12 needs python3 on the host, v16 needs TMDb.
#
# Usage: WORK=<dir> REPO_MEDIA=<media/locale> series-merge.sh <scenario>... | all | clean
# Env:   GROUPING=false creates the library with EnableAutomaticSeriesGrouping off.
#        LOCK=0 leaves <lockdata> out of the stubs. The default is locked with full
#        metadata, as the plugin writes them with metadata protection on (plan §3.1).
#        SERIES_LOCK overrides what tvshow.nfo carries: 1 <lockdata>, 0 nothing,
#        fields <lockedfields> with every text field. Episodes follow LOCK.
#        SUFFIX is added to the result file name.

cd "$(dirname "$0")"
source ./private-instance.sh

DIVISA="JM Serie Divisa (2026)"
SENZA="Serie Senza ID (2026)"
TARGET=http://jellymesh.invalid/JellyMesh/Play
LOCK=${LOCK:-1}
SUFFIX=${SUFFIX:-}

SERIES_LOCK=${SERIES_LOCK:-$LOCK}

lockdata() { [ "$LOCK" = 1 ] && echo "  <lockdata>true</lockdata>" || true; }

series_lock() {
  case $SERIES_LOCK in
    1) echo "  <lockdata>true</lockdata>" ;;
    fields) echo "  <lockedfields>Cast|Genres|ProductionLocations|Studios|Tags|Name|Overview|OfficialRating</lockedfields>" ;;
  esac
}

tvshow() { # <series folder> <title> <uniqueid xml>
  mkdir -p "$FED/Serie/$1"
  cat >"$FED/Serie/$1/tvshow.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>$2</title>
  <year>2026</year>
  <plot>From the federated tvshow.nfo.</plot>
  <genre>GenereStub</genre>
  <studio>Studio Stub</studio>
  <mpaa>Stub-Rating</mpaa>
$(series_lock)
$3
</tvshow>
XML
}

stub() { # <series folder> <file prefix> <season> <episode>
  local dir base
  dir="$FED/Serie/$1/Season $(printf %02d "$3")"
  base="$2 S$(printf %02d "$3")E$(printf %02d "$4")"
  mkdir -p "$dir"
  echo "$TARGET/$base" | tr ' ' _ >"$dir/$base.strm"
  cat >"$dir/$base.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<episodedetails>
  <title>Stub $base</title>
  <season>$3</season>
  <episode>$4</episode>
  <plot>From the federated episode nfo.</plot>
  <aired>2026-01-0$4</aired>
$(lockdata)
</episodedetails>
XML
}

IDS_ALL='  <uniqueid type="tmdb" default="true">999000101</uniqueid>
  <uniqueid type="tvdb">999000102</uniqueid>
  <uniqueid type="imdb">tt99000101</uniqueid>'

library() {
  local options
  if [ -n "${GROUPING:-}" ]; then
    options=$(jq -n --argjson g "$GROUPING" '{LibraryOptions:{EnableInternetProviders:true,PreferredMetadataLanguage:"it",MetadataCountryCode:"IT",EnableAutomaticSeriesGrouping:$g}}')
  else
    options='{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
  fi
  add_library Serie tvshows "$options" /media/serie /federata/Serie
}

# What the API shows, then what the rows hold.
report() {
  local series id seasons sid
  echo "### $1"
  get /Library/VirtualFolders | jq -c '.[] | {Name, Locations, EnableAutomaticSeriesGrouping: .LibraryOptions.EnableAutomaticSeriesGrouping}'
  echo "-- /Items Series (API view)"
  series=$(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Series&Fields=Path,ProviderIds,Overview,RecursiveItemCount,ChildCount&enableUserData=true")
  echo "$series" | jq -c '{count: .TotalRecordCount}, (.Items[] | {Id: .Id[0:8], Name, Path, ProviderIds, Overview: .Overview[0:30], ChildCount, RecursiveItemCount, Unplayed: .UserData.UnplayedItemCount})'
  for id in $(echo "$series" | jq -r '.Items[].Id'); do
    echo "-- series ${id:0:8}: seasons"
    seasons=$(get "/Shows/$id/Seasons?userId=$USER_ID&Fields=Path")
    echo "$seasons" | jq -c '.Items[] | {Id: .Id[0:8], Name, IndexNumber, Path, SeriesId: .SeriesId[0:8]}'
    echo "-- series ${id:0:8}: /Shows/Episodes"
    get "/Shows/$id/Episodes?userId=$USER_ID&Fields=Path" |
      jq -c '.Items[] | {S: .ParentIndexNumber, E: .IndexNumber, Name, Path: (.Path | split("/")[1]), SeriesId: .SeriesId[0:8], SeasonId: .SeasonId[0:8]}'
    for sid in $(echo "$seasons" | jq -r '.Items[].Id'); do
      echo "-- series ${id:0:8}: season ${sid:0:8} via /Shows/Episodes?seasonId and via /Items?ParentId"
      get "/Shows/$id/Episodes?userId=$USER_ID&seasonId=$sid&Fields=Path" |
        jq -c '[.Items[] | "S\(.ParentIndexNumber)E\(.IndexNumber) \(.Path | split("/")[1])"]'
      get "/Items?userId=$USER_ID&ParentId=$sid&Fields=Path" |
        jq -c '[.Items[] | "S\(.ParentIndexNumber)E\(.IndexNumber) \(.Path | split("/")[1])"]'
    done
  done
  echo "-- rows: Series and Season"
  sql "select substr(Id,1,8) id, substr(Type,instr(Type,'TV.')+3) type, Path, Name, IndexNumber idx, substr(ParentId,1,8) parent, IsLocked lock, substr(Overview,1,24) overview, Genres, PresentationUniqueKey pkey
       from BaseItems where Type like '%TV.Series' or Type like '%TV.Season' order by Type desc, Path;"
  echo "-- rows: Episode"
  sql "select substr(Path,1,10) root, ParentIndexNumber s, IndexNumber e, Name, IsLocked lock, substr(SeriesId,1,8) series, substr(SeasonId,1,8) season, SeriesPresentationUniqueKey skey
       from BaseItems where Type like '%TV.Episode' order by s, e, root;"
  echo
}

# Marks episodes in air order and reads NextUp after each. A series that really
# merged walks across the two paths.
nextup_walk() { # <series name>
  local eps id label
  echo "### NextUp walk: $1"
  eps=$(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Episode&Fields=Path&SortBy=ParentIndexNumber,IndexNumber" |
    jq -c --arg s "$1" '.Items[] | select(.SeriesName == $s) | {Id, label: "S\(.ParentIndexNumber)E\(.IndexNumber) \(.Path | split("/")[1])"}')
  while IFS= read -r line; do
    id=$(echo "$line" | jq -r .Id)
    label=$(echo "$line" | jq -r .label)
    post "/UserPlayedItems/$id?userId=$USER_ID" >/dev/null
    echo "played $label -> NextUp: $(get "/Shows/NextUp?userId=$USER_ID&Fields=Path" |
      jq -c '[.Items[] | "\(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber) \(.Path | split("/")[1])"]')"
  done <<<"$eps"
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Series&enableUserData=true" |
    jq -c '.Items[] | {Name, Played: .UserData.Played, Unplayed: .UserData.UnplayedItemCount}'
  echo
}

start() { reset_fixtures; up; library; }

# V1: same folder name, tvshow.nfo with the same IDs, season 1 completed, season
# 2 only on the federated side.
v1() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  stub "$DIVISA" "JM Serie Divisa" 1 5
  stub "$DIVISA" "JM Serie Divisa" 1 6
  stub "$DIVISA" "JM Serie Divisa" 2 1
  stub "$DIVISA" "JM Serie Divisa" 2 2
  scan
  report "first scan"
  scan
  report "second scan"
  nextup_walk "JM Serie Divisa"
}

# V2: same folder name, no tvshow.nfo on the federated side.
v2() {
  start
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "first scan"
  nextup_walk "JM Serie Divisa"
}

# V3: tvshow.nfo with only the TMDb ID, which Series.GetUserDataKeys ignores.
v3() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" '  <uniqueid type="tmdb" default="true">999000101</uniqueid>'
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "first scan"
}

# V3b: only IMDb, while the local series also has TVDB, which comes first.
v3b() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" '  <uniqueid type="imdb">tt99000101</uniqueid>'
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "first scan"
}

# V3c: only TVDB: the first local key, with the rest missing.
v3c() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" '  <uniqueid type="tvdb">999000102</uniqueid>'
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "first scan"
}

# V4: different folder name, same IDs.
v4() {
  start
  tvshow "JM Serie Divisa Remota" "JM Serie Divisa" "$IDS_ALL"
  stub "JM Serie Divisa Remota" "JM Serie Divisa" 1 4
  scan
  report "first scan"
  nextup_walk "JM Serie Divisa"
}

# V4b: different folder name AND different title in the nfo, same IDs.
v4b() {
  start
  tvshow "Altro Nome (2026)" "Altro Nome" "$IDS_ALL"
  stub "Altro Nome (2026)" "Altro Nome" 1 4
  stub "Altro Nome (2026)" "Altro Nome" 2 1
  scan
  report "first scan"
  scan
  report "second scan"
  nextup_walk "JM Serie Divisa"
  nextup_walk "Altro Nome"
}

# V5: series without IDs. Same folder name, no tvshow.nfo.
v5() {
  start
  stub "$SENZA" "Serie Senza ID" 1 2
  stub "$SENZA" "Serie Senza ID" 2 1
  scan
  report "first scan"
  nextup_walk "Serie Senza ID"
}

# V5b: no IDs, different folder name, no tvshow.nfo.
v5b() {
  start
  stub "Serie Senza ID Remota" "Serie Senza ID" 1 2
  scan
  report "first scan"
}

# V5c: no IDs, different folder, tvshow.nfo with the same title and no IDs.
v5c() {
  start
  tvshow "Serie Senza ID Remota" "Serie Senza ID" ""
  stub "Serie Senza ID Remota" "Serie Senza ID" 1 2
  scan
  report "first scan"
}

# V5d: no IDs on the local side, IDs on the federated side. A remote may know
# what the local copy does not.
v5d() {
  start
  tvshow "$SENZA" "Serie Senza ID" '  <uniqueid type="tvdb">999000301</uniqueid>'
  stub "$SENZA" "Serie Senza ID" 1 2
  scan
  report "first scan"
}

# V6: the same episode on both sides.
v6() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 3
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "first scan"
  nextup_walk "JM Serie Divisa"
}

# V7: grouping off, otherwise V1.
v7() {
  GROUPING=false v1
}

# V7b: starts with grouping off, then the user turns it on. What does it take?
v7b() {
  GROUPING=false start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "grouping off"
  local info lib
  info=$(get /Library/VirtualFolders | jq -c '.[] | select(.Name == "Serie")')
  lib=$(echo "$info" | jq -r .ItemId)
  post /Library/VirtualFolders/LibraryOptions "$(echo "$info" | jq -c --arg id "$lib" '{Id: $id, LibraryOptions: (.LibraryOptions | .EnableAutomaticSeriesGrouping = true)}')"
  report "grouping turned on, no scan"
  scan
  report "after a normal scan"
  local id
  for id in $(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Series" | jq -r '.Items[].Id'); do
    post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false&replaceAllImages=false"
  done
  sleep 10
  report "after a full refresh of each series"
}

# V8: series only on the federated side, then its removal.
v8() {
  start
  tvshow "JM Solo Federata (2026)" "JM Solo Federata" '  <uniqueid type="tvdb">999000401</uniqueid>'
  stub "JM Solo Federata (2026)" "JM Solo Federata" 1 1
  stub "JM Solo Federata (2026)" "JM Solo Federata" 1 2
  scan
  report "federated only"
  rm -f "${FED:?}/Serie/JM Solo Federata (2026)/Season 01/"*
  scan
  report "episode files gone, Season folder and tvshow.nfo kept"
  rm -rf "${FED:?}/Serie/JM Solo Federata (2026)/Season 01"
  scan
  report "Season folder gone, tvshow.nfo kept"
  rm -rf "${FED:?}/Serie/JM Solo Federata (2026)"
  scan
  report "series folder gone"
}

# V9: removal from a merged series, one step at a time.
v9() {
  v1
  rm -f "${FED:?}/Serie/$DIVISA/Season 02/"*
  scan
  report "season 2 stubs gone, folder kept"
  rm -rf "${FED:?}/Serie/$DIVISA/Season 02"
  rm -f "${FED:?}/Serie/$DIVISA/Season 01/"*"S01E06"*
  scan
  report "season 2 folder and S01E06 gone"
  rm -rf "${FED:?}/Serie/$DIVISA/Season 01"
  scan
  report "all episodes gone, tvshow.nfo kept"
  rm -rf "${FED:?}/Serie/$DIVISA"
  scan
  report "federated series folder gone"
}

# Every Series row, including those the API collapses. Episodes carry the Id of
# their own row, so a client can navigate to one that /Items never lists.
every_row() {
  local id
  echo "-- every Series row, by Id"
  for id in $(sql_raw "select Id from BaseItems where Type like '%TV.Series' and Name not like 'Serie Senza%' order by Path;"); do
    echo "row ${id:0:8}: item $(get "/Items/$id?userId=$USER_ID" | jq -c '{Name, Path, Overview: .Overview[0:24]}')"
    echo "  seasons  $(get "/Shows/$id/Seasons?userId=$USER_ID" | jq -c '[.Items[] | "\(.Id[0:8]) \(.Name)"]')"
    echo "  episodes $(get "/Shows/$id/Episodes?userId=$USER_ID&Fields=Path" | jq -c '[.Items[] | "S\(.ParentIndexNumber)E\(.IndexNumber) \(.Path | split("/")[1])"]')"
  done
  echo
}

# V10: the stub was written with the wrong key. Can the plugin fix it later?
v10() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" '  <uniqueid type="tmdb" default="true">999000101</uniqueid>'
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  report "stub with TMDb only, not merged"
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  scan
  report "tvshow.nfo rewritten with all IDs, normal scan"
  local id=
  id=$(sql_raw "select Id from BaseItems where Type like '%TV.Series' and Path like '/federata/%';")
  post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false&replaceAllImages=false"
  sleep 8
  report "after a full refresh of the stub series"
  get "/Users/$USER_ID/Items/$id" |
    jq '.ProviderIds.Tvdb = "999000102" | .ProviderIds.Imdb = "tt99000101"' >"$WORK/item.json"
  post "/Items/$id" "$(cat "$WORK/item.json")"
  report "POST /Items with the IDs, no scan"
  scan
  report "POST /Items with the IDs, then a scan"
}

# V11: the real case. The local series has been in the library for a while, so
# its seasons already hold ID-based keys, and the stub arrives later.
settled() { # <federated folder>
  start
  scan
  scan
  report "local series settled, nothing federated"
  tvshow "$1" "JM Serie Divisa" "$IDS_ALL"
  stub "$1" "JM Serie Divisa" 1 4
  stub "$1" "JM Serie Divisa" 2 1
  scan
  report "stub added, first scan"
  every_row
  scan
  report "second scan"
  every_row
  nextup_walk "JM Serie Divisa"
}
v11() { settled "$DIVISA"; }
v11b() { settled "JM Serie Divisa Remota"; }

# V11c: a new stub season arrives in a stub series that is already merged.
v11c() {
  settled "$DIVISA" >/dev/null
  stub "$DIVISA" "JM Serie Divisa" 1 5
  stub "$DIVISA" "JM Serie Divisa" 3 1
  scan
  report "new stubs in a merged series, first scan"
  every_row
}

# V12: the Series Id is MD5(type + path), and the lowest Id represents the merged
# series. Picks a folder name whose Id sorts after the local one.
v12() {
  local folder
  folder=$(python3 - <<'PY'
import hashlib, uuid
def series_id(path):
    key = "MediaBrowser.Controller.Entities.TV.Series" + path
    return str(uuid.UUID(bytes_le=hashlib.md5(key.encode("utf-16-le")).digest())).upper()
local = series_id("/media/serie/JM Serie Divisa (2026)")
n = 0
while True:
    name = "JM Serie Divisa (2026) [%d]" % n
    if series_id("/federata/Serie/" + name) > local:
        print(name)
        break
    n += 1
PY
  )
  echo "folder: $folder"
  start
  tvshow "$folder" "JM Serie Divisa" "$IDS_ALL"
  stub "$folder" "JM Serie Divisa" 1 4
  scan
  report "first scan"
  scan
  report "second scan"
  every_row
}

# V13: a Series favourite set on the merged view, then the stub series goes away.
v13() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  local id
  id=$(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Series" | jq -r '.Items[] | select(.Name == "JM Serie Divisa") | .Id')
  post "/UserFavoriteItems/$id?userId=$USER_ID" >/dev/null
  favourites() {
    echo "### $1"
    get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Series&enableUserData=true&Fields=Path" |
      jq -c '.Items[] | select(.Name == "JM Serie Divisa") | {Id: .Id[0:8], Path, IsFavorite: .UserData.IsFavorite}'
    sql "select substr(u.ItemId,1,8) item, u.CustomDataKey key, u.IsFavorite fav from UserData u order by item, key;"
  }
  favourites "favourite set on the merged view"
  rm -rf "${FED:?}/Serie/$DIVISA"
  scan
  favourites "stub series removed"
}

# V14: a series folder with no file at all left in it.
v14() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  rm -rf "${FED:?}/Serie/$DIVISA/Season 01"
  rm -f "${FED:?}/Serie/$DIVISA/tvshow.nfo"
  scan
  report "series folder present but completely empty"
}

# V15: incremental sync. The stub series is already in the library when new
# episodes arrive, so its row can pass a lock on to them.
v15() {
  start
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  stub "$DIVISA" "JM Serie Divisa" 2 1
  scan
  scan
  report "stub series settled"
  stub "$DIVISA" "JM Serie Divisa" 1 5
  stub "$DIVISA" "JM Serie Divisa" 3 1
  scan
  report "new stubs, first scan"
  scan
  report "new stubs, second scan"
  local id
  for id in $(sql_raw "select Id from BaseItems where Type like '%TV.Episode' and Path like '%S01E05%' or Path like '%S03E01%';"); do
    post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=true&replaceAllImages=false"
  done
  sleep 6
  report "new stubs, full refresh of each with replace all"
  nextup_walk "JM Serie Divisa"
}

# V16: what each locking strategy protects. A real series, so the fetchers have
# something to overwrite with. Needs TMDb.
v16() {
  start
  tvshow "Titolo Stub (2011)" "Titolo Stub" '  <uniqueid type="tmdb" default="true">1399</uniqueid>
  <uniqueid type="tvdb">121361</uniqueid>
  <uniqueid type="imdb">tt0944947</uniqueid>'
  stub "Titolo Stub (2011)" "Titolo Stub" 1 1
  scan
  scan
  local id
  id=$(sql_raw "select Id from BaseItems where Type like '%TV.Series' and Path like '/federata/%';")
  shown() {
    echo "### $1"
    get "/Users/$USER_ID/Items/$id" |
      jq -c '{Name, Overview: .Overview[0:50], Genres, Studios: (.Studios | map(.Name)), OfficialRating, ProviderIds, LockData, LockedFields}'
    sql "select PresentationUniqueKey pkey from BaseItems where Id = '$id';"
  }
  shown "after two scans"
  post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllMetadata=true&replaceAllImages=false"
  sleep 12
  shown "after replace all metadata"
}

# V17: does refreshing the stub series alone settle duplicate seasons, or does it
# take a whole library scan?
v17() {
  start
  scan
  scan
  tvshow "$DIVISA" "JM Serie Divisa" "$IDS_ALL"
  stub "$DIVISA" "JM Serie Divisa" 1 4
  scan
  every_row
  local id
  id=$(sql_raw "select Id from BaseItems where Type like '%TV.Series' and Path like '/federata/%';")
  post "/Items/$id/Refresh?metadataRefreshMode=Default&imageRefreshMode=Default&replaceAllMetadata=false&replaceAllImages=false"
  sleep 6
  echo "### after a default refresh of the stub series"
  every_row
  post "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false&replaceAllImages=false"
  sleep 6
  echo "### after a full refresh of the stub series"
  every_row
}

clean() {
  down
  rm -rf "${FED:?}" "${EXTRA:?}"
}

[ "${1:-}" = clean ] && { clean; exit; }
[ "${1:-}" = all ] && set -- v1 v2 v3 v3b v3c v4 v4b v5 v5b v5c v5d v6 v7 v7b v8 v9 v10 v11 v11b v11c v12 v13 v14 v15 v16 v17
for s in "$@"; do
  echo "== $s"
  "$s" >"$RESULTS/$s$SUFFIX.txt" 2>&1 || echo "$s failed, see results"
done
down
