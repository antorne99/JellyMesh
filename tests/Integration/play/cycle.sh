#!/usr/bin/env bash
# Decision 11 and §4.4 with signed URLs: a server that reads this one imports its real copies and none of its stubs, in
# standard mode, where only the URL of the .strm tells, and in JellyMesh mode. Run after localcopy.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

login local
movies() { get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq --arg p "$1" '[.Items[] | select((.Path // "") | startswith($p))] | length'; }
stubs=$(movies /federata)
copies=$(movies /media/)
in_film=$(movies /media/film)
echo "on local: $stubs stubs, $copies real copies, $in_film of them in the shared library"
check "there are stubs and copies to tell apart" 1 "$([ "$stubs" -gt 0 ] && [ "$in_film" -gt 0 ] && echo 1 || echo 0)"
signed=$(stub_url local "$(stub_item local "Tears of Steel")")
check "a stub of local has a signed URL" 1 "$(echo "$signed" | grep -c 'sig=')"
docker exec "$PREFIX-local" sh -c "find /federata -name '*.strm' -exec sh -c 'sed \"s/.*sig=//\" \"\$1\"; echo' _ {} \;" | grep . >"$WORK/local-signatures"

reader=$(user_id local reader)
[ -n "$reader" ] || reader=$(user local reader)
req local DELETE "/JellyMesh/Admin/Peers/$reader" >/dev/null
start peer "$PREFIX-media:/media:ro"
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-peer"
sleep 3
wait_ready peer
login peer
settings peer '.FederatedFolderPath = "/federata"' >/dev/null

titles() { get peer /JellyMesh/Admin/Index/Decisions | jq -c '[.Contents[].Winner.Title] | sort'; }
leaks() { docker exec -i "$PREFIX-peer" sh -c 'grep -rlFf /dev/stdin /config/plugins/Jellyfin.Plugin.JellyMesh /federata /config/log 2>/dev/null | wc -l' <"$WORK/local-signatures"; }

echo "--- standard mode: local has not connected this server yet"
id=$(add_remote peer local "http://host.docker.internal:$PORT" reader)
library peer Film movies "$(server_folder "$id")/Film"
check "the mode is standard" Standard "$(get peer /JellyMesh/Admin/Status | jq -r --arg i "$id" '.Servers[] | select(.Id == $i) | .Mode')"
import_all peer "$id"
sync_now peer
echo "imported: $(titles)"
check "only the real copies are imported" "$copies" "$(get peer /JellyMesh/Admin/Index/Decisions | jq '.Contents | length')"
check "no signature of local is kept or logged" 0 "$(leaks)"

echo "--- JellyMesh mode"
share=$(get local /JellyMesh/Admin/Peers/Options | jq -c '{SharedLibraryIds: [.Libraries[] | select(.Name == "Film") | .Id], MaxStreams: 2}')
req local PUT "/JellyMesh/Admin/Peers/$reader" "$share" >/dev/null
check "the share is saved" 204 "$(status)"
req peer DELETE "/JellyMesh/Admin/Servers/$id" >/dev/null
id=$(add_remote peer local "http://host.docker.internal:$PORT" reader)
# A server added back is a new server, in a new folder that the admin adds to the library (decision 408).
check "the folder of the server joins the library" 204 "$(link_folder peer Film "$(server_folder "$id")/Film")"
check "the mode is JellyMesh" JellyMesh "$(get peer /JellyMesh/Admin/Status | jq -r --arg i "$id" '.Servers[] | select(.Id == $i) | .Mode')"
import_all peer "$id"
sync_now peer
echo "imported: $(titles)"
check "the provider leaves the stubs out" "$in_film" "$(get peer /JellyMesh/Admin/Index/Decisions | jq '.Contents | length')"
check "no signature of local is kept or logged" 0 "$(leaks)"
exit $FAILED
