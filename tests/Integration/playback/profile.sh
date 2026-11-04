source "$(dirname "$0")/lib.sh"
login viewer
ME=$(get /Users/Me | jq -r .Id); SID=$(get /System/Info/Public | jq -r .Id)
ITEM=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME" | jq -r '.Items[0].Id')
bash "$S/pw.sh" /w/profile.py http://host.docker.internal:$PORT "$TOKEN" "$ME" "$SID" "$ITEM" /w/run/web-playbackinfo.json 2>&1 | tail -8
jq -c 'del(.DeviceProfile)' "$S/run/web-playbackinfo.json"
jq -c '.DeviceProfile|{MaxStreamingBitrate,MaxStaticBitrate,MusicStreamingTranscodingBitrate}' "$S/run/web-playbackinfo.json"
jq -c '.DeviceProfile.DirectPlayProfiles[]' "$S/run/web-playbackinfo.json"
jq -c '.DeviceProfile.TranscodingProfiles[]' "$S/run/web-playbackinfo.json"
jq -c '.DeviceProfile.SubtitleProfiles' "$S/run/web-playbackinfo.json"
