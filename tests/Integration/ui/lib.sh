# Helpers for the checks of the configuration page, on the private servers listed in the README. Source it.
# Reuses dedup/, and names all it makes after UI_PREFIX. Never touches the jellymesh-test stack.
: "${UI_PREFIX:?set UI_PREFIX, a name of its own such as jm-ui-811}"
: "${UI_PORT:?set UI_PORT, the first of four free ports such as 18811}"
case $UI_PREFIX in jellymesh-test*)
  echo "UI_PREFIX must not name the shared stack" >&2
  exit 1 ;;
esac

UI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/$UI_PREFIX-work}
# The media is gitignored, so a worktree takes the one of the main checkout.
MEDIA=${MEDIA:-$(dirname "$(git -C "$UI" rev-parse --path-format=absolute --git-common-dir)")/tests/Integration/media}
source "$UI/../dedup/lib.sh"
PW_IMAGE=mcr.microsoft.com/playwright/python:v1.48.0-noble

# add_clip <server> <clip> <folder> <title> <tmdb> <imdb> <plot>: a film in /extra/film, with its .nfo. Empty IDs are left out.
add_clip() {
  local name folder=/extra/film/$3 file=$3 nfo
  name=$(container_of "$1")
  nfo="<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><movie><title>$4</title><year>2026</year><plot>$7</plot>"
  [ -n "$5" ] && nfo+="<uniqueid type=\"tmdb\" default=\"true\">$5</uniqueid>"
  [ -n "$6" ] && nfo+="<uniqueid type=\"imdb\">$6</uniqueid>"
  nfo+="</movie>"
  docker exec "$name" mkdir -p "$folder"
  docker cp "$2" "$name:$folder/$file.mkv" >/dev/null
  printf '%s' "$nfo" | docker exec -i "$name" sh -c "cat >'$folder/movie.nfo'"
  docker exec "$name" chmod -R a+rwX "$folder"
}
list() { get "$1" /JellyMesh/Admin/Merges; }
# The plugin keeps its credential key in a file now (decision 42), so these servers run without the old variable.

SERVERS="fresh stock peer first"
NETWORK=$UI_PREFIX-net
AUTH="MediaBrowser Client=\"JellyMeshUi\", Device=\"ui\", DeviceId=\"$UI_PREFIX-admin\", Version=\"0.1\""
FAILED=0

port_of() { case $1 in fresh) echo "$UI_PORT" ;; stock) echo $((UI_PORT + 1)) ;; peer) echo $((UI_PORT + 2)) ;; first) echo $((UI_PORT + 3)) ;; esac; }
container_of() { echo "$UI_PREFIX-$1"; }
media_of() { case $1 in fresh | first) echo locale ;; stock) echo remoto-standard ;; peer) echo remoto-jellymesh ;; esac; }

restart_server() {
  docker restart "$(container_of "$1")" >/dev/null
  wait_ready "$1"
}

# key_state <server>: the state of the credential key, as the page reads it.
key_state() { get "$1" /JellyMesh/Admin/Status | jq -r .KeyState; }

# key_path <server>: where the credential key file goes, next to the .strm signing key in the plugin data folder.
key_path() {
  get "$1" /JellyMesh/Admin/Status >/dev/null
  docker exec "$(container_of "$1")" sh -c 'echo "$(dirname "$(find /config -name strm-signing.key -o -name credential.key | head -1)")/credential.key"'
}

# lose_key <server>: the key file deleted and the server restarted, so its stored logins no longer decrypt.
lose_key() {
  local path
  path=$(key_path "$1")
  docker exec "$(container_of "$1")" rm -f "$path"
  restart_server "$1"
}

# block_key <server>: a folder where the key file goes and a restart, so that the plugin cannot keep a key.
block_key() {
  local path
  path=$(key_path "$1")
  docker exec "$(container_of "$1")" sh -c "rm -rf '$path' && mkdir '$path'"
  restart_server "$1"
}

# fix_key <server>: a blocked key file freed, and every login that no longer decrypts entered again.
fix_key() {
  local path id
  path=$(key_path "$1")
  if docker exec "$(container_of "$1")" test -d "$path"; then
    docker exec "$(container_of "$1")" rmdir "$path"
    restart_server "$1"
  fi
  for id in $(get "$1" /JellyMesh/Admin/Status | jq -r '.Servers[] | select(.NeedsPassword) | .Id'); do
    post "$1" "/JellyMesh/Admin/Servers/$id/Credentials" '{"Username":"jellymesh","Password":"jellymesh-test"}' >/dev/null
  done
}

# offline <remote>: stops a remote, and syncs so that the page knows.
offline() {
  docker stop "$(container_of "$1")" >/dev/null
  sync_now fresh
}

# online <remote>: starts a remote again, and syncs.
online() {
  docker start "$(container_of "$1")" >/dev/null
  wait_ready "$1"
  sync_now fresh
}

# What the browser needs to open the page as the administrator of each plugin server.
write_targets() {
  local s out="{}" info
  for s in fresh first; do
    info=$(curl -fsS "http://127.0.0.1:$(port_of "$s")/System/Info/Public")
    out=$(jq -c --arg s "$s" --arg base "http://$(container_of "$s"):8096" --arg token "$(token_of "$s")" \
      --arg user "$(admin_id "$s")" --arg id "$(jq -r .Id <<<"$info")" \
      '.[$s] = {base: $base, token: $token, user: $user, server: $id}' <<<"$out")
  done
  (umask 077; echo "$out" >"$WORK/web/targets.json")
}

# pw <script> [args]: a Python script in the Playwright image, with uipage importable. /w is $WORK/web.
pw() {
  local script=$1
  shift
  mkdir -p "$WORK/web"
  write_targets
  cp "$script" "$WORK/web/"
  docker run --rm --network "$NETWORK" -v "$WORK/web:/w" -v "$UI:/ui:ro" -v "$UI_PREFIX-pw:/pw" \
    -e PYTHONPATH=/pw:/ui -e PYTHONDONTWRITEBYTECODE=1 "$PW_IMAGE" \
    bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
    "/w/$(basename "$script")" "$@"
}
