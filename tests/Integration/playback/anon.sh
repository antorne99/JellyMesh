source "$(dirname "$0")/lib.sh"
G=$S/run
login admin
curl -fsS "$BASE/api-docs/openapi.json" > "$G/openapi.json"
echo "== anonymous operations in the OpenAPI document with an item in the path:"
jq -r '.paths|to_entries[]|.key as $p|.value|to_entries[]|select((.value.security//[])|length==0)|"\(.key) \($p)"' "$G/openapi.json" | grep -iE '\{(itemId|routeItemId|id)\}' | sort
IT=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=JM%20Audio&Fields=MediaSources" | jq -r '.Items[0].Id')
STUB=$(get "/Items/$IT?Fields=MediaSources" | jq -r '.MediaSources[0].Path|split("/")|last')
echo "item $IT stub $STUB"
code() { # label curl-args
  l=$1; shift; : > "$G/b"
  printf '%-50s %s\n' "$l" "$(curl -sS -o "$G/b" -w '%{http_code} %{size_download}' -m 25 -H 'Range: bytes=0-99' "$@")"
  if grep -qaE ':8097|:8098|host.docker|tailnet|/media/' "$G/b"; then echo "   LEAK in body"; fi
}
for who in anon nolib viewer; do
  if [ $who = anon ]; then A=(); else login $who; A=(-H "$(hdr)"); fi
  echo "== $who"
  code "Videos/stream static" "${A[@]}" "$BASE/Videos/$IT/stream?static=true"
  code "Videos/stream.mkv static" "${A[@]}" "$BASE/Videos/$IT/stream.mkv?static=true"
  code "HEAD Videos/stream" -I "${A[@]}" "$BASE/Videos/$IT/stream?static=true"
  code "Audio/stream static" "${A[@]}" "$BASE/Audio/$IT/stream?static=true"
  code "Audio/stream.mp3" "${A[@]}" "$BASE/Audio/$IT/stream.mp3?static=true"
  code "Audio/universal" "${A[@]}" "$BASE/Audio/$IT/universal"
  code "Subtitle embedded" "${A[@]}" "$BASE/Videos/$IT/$IT/Subtitles/5/0/Stream.vtt"
  code "Subtitle external" "${A[@]}" "$BASE/Videos/$IT/$IT/Subtitles/0/0/Stream.vtt"
  code "Subtitle legacy" "${A[@]}" "$BASE/Videos/$IT/$IT/Subtitles/5/Stream.vtt"
  code "Subtitle with ticks" "${A[@]}" "$BASE/Videos/$IT/$IT/Subtitles/5/0/Stream.vtt?startPositionTicks=0"
  code "Attachment" "${A[@]}" "$BASE/Videos/$IT/$IT/Attachments/0"
  code "master.m3u8" "${A[@]}" "$BASE/Videos/$IT/master.m3u8?MediaSourceId=$IT"
  code "live.m3u8" "${A[@]}" "$BASE/Videos/$IT/live.m3u8?MediaSourceId=$IT"
  code "Items/Download" "${A[@]}" "$BASE/Items/$IT/Download"
  code "Items/File" "${A[@]}" "$BASE/Items/$IT/File"
  code "AdditionalParts" "${A[@]}" "$BASE/Videos/$IT/AdditionalParts"
  code "Play endpoint" "${A[@]}" "$BASE/JellyMesh/Play/$STUB"
done
echo "== loopback"
code "published port" "$BASE/JellyMesh/Play/$STUB"
code "published port, forged XFF" -H 'X-Forwarded-For: 127.0.0.1' "$BASE/JellyMesh/Play/$STUB"
code "published port, forged X-Real-IP" -H 'X-Real-IP: 127.0.0.1' "$BASE/JellyMesh/Play/$STUB"
docker exec $NAME curl -sS -o /dev/null -w 'inside container: %{http_code} %{size_download}\n' -H 'Range: bytes=0-99' "http://127.0.0.1:8096/JellyMesh/Play/$STUB"
docker exec $NAME curl -sS -o /dev/null -w 'inside, XFF: %{http_code}\n' -H 'X-Forwarded-For: 8.8.8.8' "http://127.0.0.1:8096/JellyMesh/Play/$STUB"
docker exec $NAME curl -sS -o /dev/null -w 'inside, unknown stub: %{http_code}\n' "http://127.0.0.1:8096/JellyMesh/Play/nosuchstub"
echo "== plugin session"
login viewer
B=$(jq -c '{DeviceProfile:.DeviceProfile}' "$G/web-playbackinfo.json")
curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo?maxStreamingBitrate=300000" > "$G/pi.json"
U=$(jq -r '.MediaSources[0].TranscodingUrl' "$G/pi.json"); P=${U%%\?*}; PS=$(jq -r .PlaySessionId "$G/pi.json")
curl -sS -o /dev/null -w 'master with token: %{http_code}\n' "$BASE$U"
curl -sS -o /dev/null -w 'master no token: %{http_code}\n' "$BASE$P"
curl -sS -o /dev/null -w 'master bad token: %{http_code}\n' "$BASE$P?ApiKey=deadbeef"
VT=$TOKEN; login nolib
curl -sS -o /dev/null -w 'master other user: %{http_code}\n' "$BASE$P?ApiKey=$TOKEN"
login viewer
curl -sS -o /dev/null -w 'DELETE by other user keeps it: %{http_code}\n' -X DELETE -H "$(TOKEN=; hdr)" "$BASE/Videos/ActiveEncodings?deviceId=x&playSessionId=$PS"
curl -sS -o /dev/null -w 'DELETE: %{http_code}\n' -X DELETE -H "$(hdr)" "$BASE/Videos/ActiveEncodings?deviceId=x&playSessionId=$PS"
curl -sS -o /dev/null -w 'master after close: %{http_code}\n' "$BASE$P?ApiKey=$TOKEN"
