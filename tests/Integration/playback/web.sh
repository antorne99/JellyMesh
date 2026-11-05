#!/usr/bin/env bash
# Usage: web.sh <title substring> <scenario> [seconds] [menu index]. Runs real jellyfin-web as the local user viewer.
source "$(dirname "$0")/lib.sh"
title=$1; scenario=$2; secs=${3:-12}; pick=${4:-}
AUTHB='MediaBrowser Client="Jellyfin Web", Device="Chromium", DeviceId="jm-play-browser", Version="12.4.0"'
login viewer
ME=$(get /Users/Me | jq -r .Id); SID=$(get /System/Info/Public | jq -r .Id)
ITEM=${ITEM:-$(get "/Items?Recursive=true&IncludeItemTypes=Movie,Episode&userId=$ME&SearchTerm=$(jq -rn --arg t "$title" '$t|@uri')" | jq -r '.Items[0].Id')}
echo "item $ITEM viewer token $TOKEN"
echo "$TOKEN" > "$S/run/viewer.token"
tag=$(echo "$title-$scenario" | tr -c 'A-Za-z0-9\n' '_')
rm -f "$S/run/cap-$tag".*
[ -n "${WATCH:-}" ] && { nohup bash "$S/watch.sh" "$WATCH" "$S/run/watch-$tag.txt" >/dev/null 2>&1 & }
bash "$S/pw.sh" /w/webplay.py ${WEBBASE:-http://host.docker.internal:$PORT} "$TOKEN" "$ME" "$SID" "$ITEM" "$scenario" "/w/run/cap-$tag" "$secs" $pick 2>&1
