source "$(dirname "$0")/lib.sh"
mkdir -p "$S/run"
docker rm -f $NAME >/dev/null 2>&1 || true
docker volume rm $NAME-config $NAME-cache $NAME-fed >/dev/null 2>&1 || true
docker volume create $NAME-fed >/dev/null
docker run -d --name $NAME -p 127.0.0.1:$PORT:8096 -e TZ=Europe/Rome \
  --add-host host.docker.internal:host-gateway \
  -v $NAME-config:/config -v $NAME-cache:/cache -v $NAME-fed:/federata \
  -v "$MEDIA/locale:/media:ro" -v "$S/extra:/extra" "$IMAGE" >/dev/null
docker exec $NAME mkdir -p /federata/Film /federata/Serie
wait_up
post /Startup/Configuration '{"UICulture":"it-IT","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
get /Startup/User >/dev/null
post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}'
post /Startup/Complete
login admin
post '/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film&paths=/federata/Film' '{"LibraryOptions":{"EnableRealtimeMonitor":false}}'
post '/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/media/serie&paths=/federata/Serie' '{"LibraryOptions":{"EnableRealtimeMonitor":false}}'
post /Library/Refresh
sleep 6
post /Users/New '{"Name":"viewer","Password":"jellymesh-test"}' | jq -r .Id
NOLIB=$(post /Users/New '{"Name":"nolib","Password":"jellymesh-test"}' | jq -r .Id)
P=$(get "/Users/$NOLIB" | jq -c '.Policy|.EnableAllFolders=false|.EnabledFolders=[]')
post "/Users/$NOLIB/Policy" "$P"
bash "$WT/tests/Integration/scripts/install-plugin.sh" $NAME 2>&1 | tail -2
sleep 3; wait_up; login admin
PID=$(get /Plugins | jq -r '.[]|select(.Name=="JellyMesh").Id'); echo "plugin $PID"
C=$(get "/Plugins/$PID/Configuration" | jq -c '.FederatedFolderPath="/federata"')
post "/Plugins/$PID/Configuration" "$C"
add() { # name url
  id=$(post /JellyMesh/Admin/Servers "$(jq -n --arg n "$1" --arg u "$2" '{Name:$n,Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" | jq -r .Id)
  libs=$(get "/JellyMesh/Admin/Servers/$id/Libraries" | jq -c '{LibraryIds:[.[].Id]}')
  post "/JellyMesh/Admin/Servers/$id/Libraries" "$libs" >/dev/null
  echo "$1 $id"
}
add standard http://host.docker.internal:18321
add jellymesh http://host.docker.internal:18322
post /JellyMesh/Admin/Sync
