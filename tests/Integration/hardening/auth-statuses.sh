#!/usr/bin/env bash
# What a real server answers for each kind of refused call, which decides what may trigger a new login.
# usage: PORT=18459 auth-statuses.sh   against a fresh stock server that has not finished its first-run wizard.
set -euo pipefail
PORT=${PORT:-18459}
U="http://127.0.0.1:$PORT"
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshHard", Device="hard", DeviceId="jm-hard-auth", Version="0.1"'

until curl -fsS "$U/System/Info/Public" >/dev/null 2>&1; do sleep 1; done
curl -sS -o /dev/null -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' "$U/Startup/Configuration"
curl -sS -o /dev/null -H "$ACCEPT" -H "Authorization: $AUTH" "$U/Startup/User"
curl -sS -o /dev/null -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d '{"Name":"admin","Password":"jellymesh-test"}' "$U/Startup/User"
curl -sS -o /dev/null -X POST -H "$ACCEPT" -H "Authorization: $AUTH" "$U/Startup/Complete"

login() { curl -sS -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d "{\"Username\":\"$1\",\"Pw\":\"$2\"}" "$U/Users/AuthenticateByName"; }
code() { # code <token> <method> <path>
  curl -sS -o /dev/null -w '%{http_code}' -X "$2" -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$1\"" "$U$3"
}

admin=$(login admin jellymesh-test | jq -r .AccessToken)
curl -sS -o /dev/null -X POST -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$admin\"" -H 'Content-Type: application/json' -d '{"Name":"peer","Password":"jellymesh-test"}' "$U/Users/New"
uid=$(curl -sS -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$admin\"" "$U/Users" | jq -r '.[] | select(.Name == "peer") | .Id')
token=$(login peer jellymesh-test | jq -r .AccessToken)

echo "wrong password:            $(curl -sS -o /dev/null -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d '{"Username":"peer","Pw":"wrong"}' "$U/Users/AuthenticateByName")"
echo "bad token, /Users/Me:      $(code nonsense GET /Users/Me)"
echo "bad token, /Items:         $(code nonsense GET '/Items?recursive=true')"
echo "good token, /Items:        $(code "$token" GET '/Items?recursive=true')"
echo "good token, admin route:   $(code "$token" GET /System/Configuration)"
echo "good token, encoding:      $(code "$token" GET /System/Configuration/encoding)"

# A login with the same device replaces the token, which is what a needless login costs.
second=$(login peer jellymesh-test | jq -r .AccessToken)
echo "old token after a login:   $(code "$token" GET /Users/Me)"
echo "new token after a login:   $(code "$second" GET /Users/Me)"

policy=$(curl -sS -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$admin\"" "$U/Users/$uid" | jq -c '.Policy | .IsDisabled = true')
curl -sS -o /dev/null -X POST -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$admin\"" -H 'Content-Type: application/json' -d "$policy" "$U/Users/$uid/Policy"
echo "disabled user, /Users/Me:  $(code "$second" GET /Users/Me)"
echo "disabled user, /Items:     $(code "$second" GET '/Items?recursive=true')"
echo "disabled user, login:      $(curl -sS -o /dev/null -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d '{"Username":"peer","Pw":"jellymesh-test"}' "$U/Users/AuthenticateByName")"
