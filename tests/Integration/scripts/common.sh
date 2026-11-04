# Shared helpers for the integration scripts. Source this file, do not run it.

ADMIN_USER=${ADMIN_USER:-admin}
ADMIN_PASSWORD=${ADMIN_PASSWORD:-jellymesh-test}
REMOTE_USER=${REMOTE_USER:-jellymesh}
REMOTE_PASSWORD=${REMOTE_PASSWORD:-jellymesh-test}
AUTH='MediaBrowser Client="JellyMeshTest", Device="scripts", DeviceId="jellymesh-test-scripts", Version="0.1"'
# Jellyfin picks the JSON key casing from Accept. Without this it may answer
# in camelCase, while the OpenAPI schema and these scripts use PascalCase.
ACCEPT='Accept: application/json; profile="PascalCase"'

BASE=
TOKEN=

use_instance() {
  case $1 in
    locale) BASE=http://127.0.0.1:8096 ;;
    remoto-standard) BASE=http://127.0.0.1:8097 ;;
    remoto-jellymesh) BASE=http://127.0.0.1:8098 ;;
    *) echo "unknown instance $1" >&2; return 1 ;;
  esac
}

auth_header() {
  if [ -n "$TOKEN" ]; then
    printf 'Authorization: %s, Token="%s"' "$AUTH" "$TOKEN"
  else
    printf 'Authorization: %s' "$AUTH"
  fi
}

get() {
  curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"
}

post() {
  curl -fsS -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' \
    -d "${2:-}" "$BASE$1"
}

# login [user] [password]: sets TOKEN.
login() {
  local user=${1:-$ADMIN_USER} password=${2:-$ADMIN_PASSWORD}
  TOKEN=
  TOKEN=$(post /Users/AuthenticateByName \
    "$(jq -n --arg u "$user" --arg p "$password" '{Username:$u,Pw:$p}')" | jq -r .AccessToken)
}
