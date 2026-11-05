# Helpers for the checks of the configuration page, on the private servers listed in the README. Source it.
# Reuses merge/ and dedup/, and names all it makes after UI_PREFIX. Never touches the jellymesh-test stack.
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
source "$UI/../merge/lib.sh"

SERVERS="fresh stock peer first"
NETWORK=$UI_PREFIX-net
AUTH="MediaBrowser Client=\"JellyMeshUi\", Device=\"ui\", DeviceId=\"$UI_PREFIX-admin\", Version=\"0.1\""
FAILED=0

port_of() { case $1 in fresh) echo "$UI_PORT" ;; stock) echo $((UI_PORT + 1)) ;; peer) echo $((UI_PORT + 2)) ;; first) echo $((UI_PORT + 3)) ;; esac; }
container_of() { echo "$UI_PREFIX-$1"; }
media_of() { case $1 in fresh | first) echo locale ;; stock) echo remoto-standard ;; peer) echo remoto-jellymesh ;; esac; }

# set_key <server> [key]: the same server restarted with that key, or with none when it is left out.
set_key() {
  docker rm -f "$(container_of "$1")" >/dev/null
  run_container "$1" "${2:-}"
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
    out=$(jq -c --arg s "$s" --arg base "http://host.docker.internal:$(port_of "$s")" --arg token "$(token_of "$s")" \
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
  docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v "$UI:/ui:ro" -v "$UI_PREFIX-pw:/pw" \
    -e PYTHONPATH=/pw:/ui -e PYTHONDONTWRITEBYTECODE=1 "$PW_IMAGE" \
    bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
    "/w/$(basename "$script")" "$@"
}
