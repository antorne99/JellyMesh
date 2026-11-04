# Point 6, API side. Sourced by unavailable-title.sh, which provides get, post, raw,
# login, mode, item_id and scan. Who gets the refusal, and what the bytes contain.

SENTINEL=remoto-segreto.tailnet-sentinel.ts.net
BODY=$RUN/body

refusals() { docker logs "$NAME" 2>&1 | grep -c 'JMPROBE refusal' || true; }

show() { # <label> <status>
  printf '%-52s %s  %s\n' "$1" "$2" "$(tr -d '\n' <"$BODY" | cut -c1-150)"
  if grep -q "$SENTINEL" "$BODY"; then echo "    ^ body carries the remote name"; fi
}

secret_fixture() {
  # Not something JellyMesh may write: .strm files never hold a remote address. It
  # shows what the server itself reveals when a dead target reaches it unfiltered.
  strm "Film/JM Segreto (2026)" "JM Segreto (2026)" x
  echo "http://$SENTINEL:8096/Videos/x/stream" >"$RUN/fed/Film/JM Segreto (2026)/JM Segreto (2026).strm"
  login admin
  scan
}

secret_fixture
login p6user
user_token=$TOKEN
offline=$(item_id '.Name=="JM Offline"')
online=$(item_id '.Name=="JM Online"')
secret=$(item_id '.Name | startswith("JM Segreto")')
login p6noaccess; noaccess_token=$TOKEN
login p6noplay; noplay_token=$TOKEN
missing=00000000000000000000000000000abc

echo "--- ErrorCode as sent on the wire"
mode code=NotAllowed
s=$(raw "-$user_token" POST "/Items/$offline/PlaybackInfo" '{}'); show "code=NotAllowed" "$s"
mode code=NotAllowed
s=$(raw "-$user_token" GET "/Items/$offline/PlaybackInfo"); show "same, GET variant" "$s"

echo "--- refusal only after the local checks (status=503 set)"
mode status=503
before=$(refusals)
s=$(raw "-" POST "/Items/$offline/PlaybackInfo" '{}'); show "no token" "$s"
s=$(raw "-bad$user_token" POST "/Items/$offline/PlaybackInfo" '{}'); show "wrong token" "$s"
s=$(raw "-$noaccess_token" POST "/Items/$offline/PlaybackInfo" '{}'); show "user without the library" "$s"
s=$(raw "-$noaccess_token" POST "/Items/$missing/PlaybackInfo" '{}'); show "same user, id that does not exist" "$s"
echo "refusal branch reached by those calls: $(($(refusals) - before)) (expected 0)"
s=$(raw "-$user_token" POST "/Items/$offline/PlaybackInfo" '{}'); show "user with the library" "$s"

echo "--- user with EnableMediaPlayback=false"
mode ""
s=$(raw "-$noplay_token" POST "/Items/$online/PlaybackInfo" '{}'); show "stock answer, no filter involved" "$s"
jq -c '{ErrorCode, sources: (.MediaSources | length), directPlay: .MediaSources[0].SupportsDirectPlay}' "$BODY"
mode status=503
s=$(raw "-$noplay_token" POST "/Items/$offline/PlaybackInfo" '{}'); show "filter refusing as offline" "$s"

echo "--- late failure, what the server passes on from the play endpoint"
mode source=file,target=dead
s=$(raw "-$user_token" GET "/Videos/$offline/stream.webm?Static=true&mediaSourceId=$offline"); show "server proxies a 503" "$s"
s=$(raw "-" GET "/Videos/$offline/stream.webm?Static=true&mediaSourceId=$offline"); show "same, no token" "$s"
s=$(raw "-" GET "/JellyMesh/Play/film-offline"); show "play endpoint itself, no token (spike only)" "$s"
mode ""
printf '%-52s %s\n' "stock static stream of a live stub, no token" \
  "$(curl -s -o /dev/null -w '%{http_code} %{size_download} bytes' -r 0-999 "$BASE/Videos/$online/stream.webm?Static=true&mediaSourceId=$online")"

echo "--- unfiltered dead target with the remote name in the .strm (never in JellyMesh)"
mode ""
s=$(raw "-$user_token" POST "/Items/$secret/PlaybackInfo" '{}'); show "PlaybackInfo" "$s"
s=$(raw "-$user_token" GET "/Videos/$secret/stream.webm?Static=true&mediaSourceId=$secret"); show "static stream, unresolvable host" "$s"
s=$(raw "-$user_token" GET "/Items/$secret?Fields=Path,MediaSources"); show "item for a non admin" "$s"
jq -c '[.MediaSources[]?.Path]' "$BODY"
echo "server log lines naming the remote: $(docker logs "$NAME" 2>&1 | grep -c "$SENTINEL" || true)"

echo "--- DTO rewrite reaches the API (item=placeholder,virtual,playaccess)"
mode "" placeholder,virtual,playaccess
login p6user
get "/Items/$offline?Fields=PlayAccess" | jq -c '{Name,IsPlaceHolder,LocationType,PlayAccess}'
get "/Items/$online?Fields=PlayAccess" | jq -c '{Name,IsPlaceHolder,LocationType,PlayAccess}'
get "/Items?Ids=$offline&Fields=PlayAccess" | jq -c '.Items[0] | {Name,IsPlaceHolder,LocationType,PlayAccess}'
get "/Items/Latest?Fields=PlayAccess" | jq -c '.[] | select(.Name=="JM Offline") | {Name,IsPlaceHolder,LocationType,PlayAccess}'
get "/Search/Hints?searchTerm=Offline" | jq -c '.SearchHints[] | {Name,Type}' | head -3
mode ""

echo "--- stored IsPlaceHolder, set by the pre-refresh provider"
login admin
refresh() { post "/Items/$offline/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false" >/dev/null; sleep 4; }
state() { get "/Items/$offline" | jq -c '{IsPlaceHolder,LocationType}'; }
echo "before: $(state)"
docker exec "$NAME" sh -c "printf placeholder >/config/jmprobe.mode"
refresh; echo "after refresh with the mode on: $(state)"
scan; echo "after a library scan: $(state)"
docker exec "$NAME" rm -f /config/jmprobe.mode
refresh; echo "after refresh with the mode off (server back): $(state)"
scan; echo "after a library scan: $(state)"
