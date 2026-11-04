# Helpers for the play proof. Source it.
NAME=jm-play PORT=18320 IMAGE=jellyfin/jellyfin:12.4.20261026-211555
PW=mcr.microsoft.com/playwright/python:v1.48.0-noble
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
MEDIA=${MEDIA:-$S/../media}  # gitignored: in a worktree, point it at the main checkout
WT=$(cd "$S/../../.." && pwd)
BASE=http://127.0.0.1:$PORT
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTHB='MediaBrowser Client="JellyMeshPlay", Device="play", DeviceId="jm-play-admin", Version="0.1"'
TOKEN=
hdr() { if [ -n "$TOKEN" ]; then printf 'Authorization: %s, Token="%s"' "$AUTHB" "$TOKEN"; else printf 'Authorization: %s' "$AUTHB"; fi; }
get() { curl -fsS -H "$ACCEPT" -H "$(hdr)" "$BASE$1"; }
post() { curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"; }
wait_up() { for i in $(seq 1 120); do get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return 0; sleep 1; done; echo "not up"; return 1; }
up() {
  docker rm -f $NAME >/dev/null 2>&1 || true
  docker volume rm $NAME-config $NAME-cache >/dev/null 2>&1 || true
  docker run -d --name $NAME -p 127.0.0.1:$PORT:8096 -e TZ=Europe/Rome \
    -e JELLYMESH_SECRET_KEY=play-test-secret-key-0123456789-abcdef \
    --add-host host.docker.internal:host-gateway \
    -v $NAME-config:/config -v $NAME-cache:/cache \
    -v "$MEDIA/locale:/media:ro" -v "$S/fed:/federata" "$IMAGE" >/dev/null
  wait_up
  post /Startup/Configuration '{"UICulture":"it-IT","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  get /Startup/User >/dev/null
  post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}'
  post /Startup/Complete
}
login() { TOKEN=; TOKEN=$(post /Users/AuthenticateByName "$(jq -n --arg u "$1" --arg p "${2:-jellymesh-test}" '{Username:$u,Pw:$p}')" | jq -r .AccessToken); }
