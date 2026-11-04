source "$(dirname "$0")/lib.sh"
# Stock remote on the same private Jellyfin: a real 12.4 deciding direct play for a clip this Chromium decodes.
mkdir -p "$S/extra/remota/JM Diretto Fonte (2026)"
docker exec $NAME /usr/lib/jellyfin-ffmpeg/ffmpeg -y -v error -t 40 -i "/media/film/Elephants Dream (2006)/Elephants Dream (2006).mkv" -vf scale=640:-2 -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 700k -c:a libopus -b:a 96k "/extra/remota/JM Diretto Fonte (2026)/JM Diretto Fonte (2026).webm"
ls -la "$S/extra/remota/JM Diretto Fonte (2026)"
login admin
PID=$(get /Plugins | jq -r '.[]|select(.Name=="JellyMesh").Id')
post "/Plugins/$PID/Configuration" "$(get "/Plugins/$PID/Configuration" | jq -c '.SyncIntervalSeconds=86400')"
post '/Library/VirtualFolders?name=Remota&collectionType=movies&refreshLibrary=true&paths=/extra/remota' '{"LibraryOptions":{"EnableRealtimeMonitor":false}}'
sleep 6
DU=$(post /Users/New '{"Name":"dedicato","Password":"jellymesh-test"}' | jq -r .Id); echo "dedicato $DU"
SELF=$(post /JellyMesh/Admin/Servers '{"Name":"self","Url":"http://127.0.0.1:8096","Username":"dedicato","Password":"jellymesh-test"}' | jq -r .Id); echo "self $SELF"
SRC=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=Diretto" | jq -r '.Items[0].Id'); echo "src $SRC"
ST=$(uuidgen | tr -d - | tr A-Z a-z); echo "stub $ST"
D="/federata/Film/JM Diretto Stub (2026)"
docker exec $NAME mkdir -p "$D"
printf 'http://127.0.0.1:8096/JellyMesh/Play/%s' "$ST" > "$S/run/d.strm"
docker cp "$S/run/d.strm" "$NAME:$D/JM Diretto Stub (2026).strm"
docker exec $NAME cat /config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json > "$S/run/stubs.json"
jq -c '.Stubs[0]' "$S/run/stubs.json" | cut -c1-500
jq --arg st "$ST" --arg srv "$SELF" --arg src "$SRC" '.Stubs += [{Id:$st,Kind:"Movie",RelativePath:"Film/JM Diretto Stub (2026)/JM Diretto Stub (2026).strm",Sources:[{ServerId:$srv,ItemId:$src,MediaSourceId:$src}],IdentityKey:"play-direct-fixture"}]' "$S/run/stubs.json" > "$S/run/stubs2.json"
docker cp "$S/run/stubs2.json" "$NAME:/config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json"
docker restart $NAME >/dev/null; sleep 3; wait_up; login admin
post /Library/Refresh; sleep 10
get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=Diretto&Fields=Path,MediaSources" | jq -r '.Items[]|.Id+" "+.Name+" "+(.RunTimeTicks//0|tostring)+" "+.MediaSources[0].Path'
