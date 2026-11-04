# Scenario helpers on top of env.sh. Source this file.
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
USER_ID=$(get /Users/Me 2>/dev/null | jq -r .Id || true)
PID=$(get /Plugins 2>/dev/null | jq -r '.[] | select(.Name=="JellyMesh") | .Id' || true)
set_folder() {
  local cfg
  cfg=$(get "/Plugins/$PID/Configuration" | jq -c --argjson p "${1:-true}" '.FederatedFolderPath="/federata" | .MetadataProtection=$p')
  post "/Plugins/$PID/Configuration" "$cfg" >/dev/null
}
add_server() { # <name> <url> [user] -> prints id
  local id libs
  id=$(post /JellyMesh/Admin/Servers "$(jq -n --arg n "$1" --arg u "$2" --arg us "${3:-jellymesh}" '{Name:$n,Url:$u,Username:$us,Password:"jellymesh-test"}')" | jq -r .Id)
  libs=$(get "/JellyMesh/Admin/Servers/$id/Libraries" | jq -c '{LibraryIds:[.[] | .Id]}')
  post "/JellyMesh/Admin/Servers/$id/Libraries" "$libs" >/dev/null
  echo "$id"
}
sync_and_scan() { sync_full; sleep 4; scan_settle; sleep 3; }
items() { # <type>
  get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=$1&Fields=Path,ProviderIds,Overview,MediaSources&Limit=500"
}
stub_files() { docker exec "$NAME" sh -c 'cd /federata && find . -type f -name "*.strm" | sort'; }
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
    get "/Shows/$id/Episodes?userId=$USER_ID&Fields=Path" | jq -c '[.Items[] | "S\(.ParentIndexNumber)E\(.IndexNumber)@\(.Path|split("/")[1])"]'
  done
}
nextup_walk() { # <series name>
  local eps line id label
  eps=$(get "/Items?userId=$USER_ID&Recursive=true&IncludeItemTypes=Episode&Fields=Path&SortBy=ParentIndexNumber,IndexNumber&Limit=500" |
    jq -c --arg s "$1" '.Items[] | select(.SeriesName == $s) | {Id, label: "S\(.ParentIndexNumber)E\(.IndexNumber)@\(.Path | split("/")[1])"}')
  while IFS= read -r line; do
    id=$(echo "$line" | jq -r .Id)
    label=$(echo "$line" | jq -r .label)
    post "/UserPlayedItems/$id?userId=$USER_ID" >/dev/null
    echo "played $label -> NextUp: $(get "/Shows/NextUp?userId=$USER_ID&Fields=Path" |
      jq -c --arg s "$1" '[.Items[] | select(.SeriesName==$s) | "S\(.ParentIndexNumber)E\(.IndexNumber)@\(.Path | split("/")[1])"]')"
  done <<<"$eps"
}
unplay_all() {
  local id
  for id in $(items Episode | jq -r '.Items[].Id'); do curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/UserPlayedItems/$id?userId=$USER_ID" >/dev/null; done
}
refresh_replace() { # <item id>
  post "/Items/$1/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh&replaceAllMetadata=true&replaceAllImages=false"
}
