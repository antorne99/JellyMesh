# Scenario helpers on top of env.sh. Source this file.
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
USER_ID=$(get /Users/Me 2>/dev/null | jq -r .Id || true)
set_folder() {
  put /JellyMesh/Admin/Settings "$(jq -nc --argjson p "${1:-true}" '{FederatedFolderPath: "/federata", MetadataProtection: $p}')" >/dev/null
}
# add_server <name> <url> [user] -> prints id. The server gets the library mode "None": sync_and_scan adds its folders to
# the Film and Serie libraries by hand, as the admin does (rows 437, 443).
add_server() {
  local id
  id=$(post /JellyMesh/Admin/Servers "$(jq -n --arg n "$1" --arg u "$2" --arg us "${3:-jellymesh}" '{Name:$n,Url:$u,Username:$us,Password:"jellymesh-test",LibraryMode:"None"}')" | jq -r .Id)
  choose_libraries "$id" '[.Libraries[].Id]'
  echo "$id"
}
choose_libraries() { # <server id> <jq filter of the edit check that gives the library ids>: saved with the edit form
  local form ids
  form=$(get /JellyMesh/Admin/Status | jq -c --arg id "$1" '.Servers[] | select(.Id == $id) | {Name, Url, Username: "", Password: "", LibraryMode: .AutomaticLibraries.Mode}')
  ids=$(post "/JellyMesh/Admin/Servers/$1/Edit/Check" "$form" | jq -c "$2")
  put "/JellyMesh/Admin/Servers/$1" "$(jq -c --argjson l "$ids" '. + {LibraryIds: $l}' <<<"$form")" >/dev/null
}
# link_folders: adds each folder the page lists to add by hand to the Film or Serie library; prints how many it added.
link_folders() {
  local held type path lib n=0
  held=$(get /Library/VirtualFolders | jq -c '[.[].Locations[]]')
  while read -r type path; do
    [ -n "$path" ] || continue
    jq -e --arg p "$path" 'index($p)' <<<"$held" >/dev/null && continue
    case $type in movies) lib=Film ;; tvshows) lib=Serie ;; *) echo "no library of type $type for $path" >&2; continue ;; esac
    post "/Library/VirtualFolders/Paths?refreshLibrary=false" "$(jq -n --arg n "$lib" --arg p "$path" '{Name: $n, PathInfo: {Path: $p}}')" >/dev/null
    n=$((n + 1))
  done < <(get /JellyMesh/Admin/Status | jq -r '.Servers[].AutomaticLibraries.ManualPaths[] | "\(.LibraryType) \(.Path)"')
  echo "$n"
}
# unlink_server <server id>: takes the folders of a removed server out of the libraries, as the admin does.
unlink_server() {
  local id=${1//-/} lib path
  id="${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"
  get /Library/VirtualFolders | jq -r --arg g "/federata/$id/" '.[] | .Name as $n | .Locations[] | select(startswith($g)) | "\($n)\t\(.)"' |
    while IFS=$'\t' read -r lib path; do
      curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/Library/VirtualFolders/Paths?refreshLibrary=false&name=$(jq -rn --arg v "$lib" '$v|@uri')&path=$(jq -rn --arg v "$path" '$v|@uri')"
    done
}
# sync_and_scan: a full sync, the folders of new servers linked, then a scan once they are.
sync_and_scan() {
  sync_full; sleep 4; scan_settle
  if [ "$(link_folders)" != 0 ]; then scan; fi
  sleep 3
}
# remote <method> <path> [body]: a request to the Standard remote as its admin, from this machine.
remote() {
  local token
  token=$(curl -fsS -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
    -d "$(jq -n --arg p "$REMOTE_ADMIN_PASSWORD" '{Username: "admin", Pw: $p}')" "$STANDARD_HOST_URL/Users/AuthenticateByName" | jq -r .AccessToken)
  curl -fsS -X "$1" -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$token\"" -H 'Content-Type: application/json' ${3:+-d "$3"} "$STANDARD_HOST_URL$2"
}
# fed_file <path suffix>: the first file under the federated folder whose path ends so.
fed_file() { docker exec "$NAME" find /federata -path "*$1" | sort | head -1; }
items() { # <type>
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=$1&Fields=Path,ProviderIds,Overview,MediaSources&Limit=500"
}
stub_files() { docker exec "$NAME" sh -c 'cd /federata && find . -type f -name "*.strm" | sort'; }
# where: a jq function naming where an item lives, the label of a stub or "local".
WHERE='def where: if .Path == null then "none" elif (.Path | startswith("/federata")) then (.Path | split(" - ") | last | rtrimstr(".strm")) else "local" end;'
film_id() { # <name>
  items Movie | jq -r --arg n "$1" '.Items[] | select(.Name==$n) | .Id' | head -1
}
film_id_at() { # <name> <root>
  items Movie | jq -r --arg n "$1" --arg r "$2" '.Items[] | select(.Name==$n and (.Path|startswith($r))) | .Id' | head -1
}
ep_id() { # <series> <season> <episode> [root]
  items Episode | jq -r --arg n "$1" --argjson s "$2" --argjson e "$3" --arg r "${4:-/}" '.Items[] | select(.SeriesName==$n and .ParentIndexNumber==$s and .IndexNumber==$e and (.Path|startswith($r))) | .Id' | head -1
}
user_data() { # <item id> [user id]
  get "/Users/${2:-$USER_ID}/Items/$1" | jq -c '.UserData | {Played,PlayCount,PlaybackPositionTicks,LastPlayedDate,IsFavorite}'
}
series_view() {
  local id
  items Series | jq -c '.Items[] | {Id:.Id[0:8],Name,Path,Ids:.ProviderIds}'
  for id in $(items Series | jq -r '.Items[].Id'); do
    get "/Shows/$id/Seasons?userId=$USER_ID&Fields=Path" | jq -c '[.Items[] | "\(.Name) @\(.Path|split("/")[1])"]'
    get "/Shows/$id/Episodes?userId=$USER_ID&Fields=Path" | jq -c "$WHERE"'[.Items[] | "S\(.ParentIndexNumber)E\(.IndexNumber)@\(where)"]'
  done
}
nextup_walk() { # <series name>
  local eps line id label
  eps=$(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Episode&Fields=Path&SortBy=ParentIndexNumber,IndexNumber&Limit=500" |
    jq -c --arg s "$1" "$WHERE"'.Items[] | select(.SeriesName == $s) | {Id, label: "S\(.ParentIndexNumber)E\(.IndexNumber)@\(where)"}')
  while IFS= read -r line; do
    id=$(echo "$line" | jq -r .Id)
    label=$(echo "$line" | jq -r .label)
    post "/UserPlayedItems/$id?userId=$USER_ID" >/dev/null
    echo "played $label -> NextUp: $(get "/Shows/NextUp?userId=$USER_ID&Fields=Path" |
      jq -c --arg s "$1" "$WHERE"'[.Items[] | select(.SeriesName==$s) | "S\(.ParentIndexNumber)E\(.IndexNumber)@\(where)"]')"
  done <<<"$eps"
}
unplay_all() {
  local id
  for id in $(items Episode | jq -r '.Items[].Id'); do curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/UserPlayedItems/$id?userId=$USER_ID" >/dev/null; done
}
refresh_replace() { # <item id>
  post "/Items/$1/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllMetadata=true&replaceAllImages=false"
}
