source "$(dirname "$0")/lib.sh"
SELF=$(jq -r '.Stubs[-1].Sources[0].ServerId' "$S/run/stubs2.json")
D=$(echo "$SELF" | sed -E 's/^(.{8})(.{4})(.{4})(.{4})(.{12})$/\1-\2-\3-\4-\5/')
jq --arg d "$D" '.Stubs[-1].Sources[0].ServerId=$d' "$S/run/stubs2.json" > "$S/run/stubs3.json"
docker cp "$S/run/stubs3.json" "$NAME:/config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json"
docker restart $NAME >/dev/null; sleep 3; wait_up
docker logs --since 40s $NAME 2>&1 | grep -c 'Cannot read the stub registry'
