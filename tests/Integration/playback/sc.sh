#!/usr/bin/env bash
# Scenarios on the private instance. Usage: sc.sh <name>
source "$(dirname "$0")/lib.sh"
G=$S/run
FILTER='SEEN|STARTED|PROGRESS|DIALOGS|STATE|URL|PlaybackInfo|master|AFTER|RESUMED|PAUSED|SEEK|PAGEERR|Traceback'
web() { MAXBIT=${MAXBIT-300000} bash "$S/web.sh" "$@" 2>&1 | grep -E "$FILTER" | cut -c1-230; }
gate() { echo "$2" > "$G/gate-$1"; }
glog() { : > "$G/gatelog-standard"; : > "$G/gatelog-jellymesh"; }
played() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME&SearchTerm=$(jq -rn --arg t "$1" '$t|@uri')" | jq -c --arg n "$1" '.Items[0]|{Name,UserData:(.UserData|{Played,PlayCount,PlaybackPositionTicks})}'; }
case $1 in
  backup)
    echo "== winner jellymesh offline, backup standard"; glog; gate jellymesh down
    web "Eclisse Fantasma" play 20
    gate jellymesh up
    echo "gate standard:"; grep -c . "$G/gatelog-standard"; grep -m3 -E 'PlaybackInfo|master' "$G/gatelog-standard" | cut -c1-120
    echo "gate jellymesh:"; grep -c ' GET\| POST' "$G/gatelog-jellymesh"
    ;;
  offline)
    echo "== offline, no backup"; gate jellymesh down
    played "JM Solo JellyMesh"
    web "JM Solo JellyMesh" fail 5
    played "JM Solo JellyMesh"
    gate jellymesh up
    ;;
  hang)
    echo "== remote accepts and never answers: timeout 10 s"; gate jellymesh hang
    web "JM Solo JellyMesh" fail 14
    gate jellymesh up
    ;;
  auth)
    echo "== refusal order with the remote offline"; gate jellymesh down
    login admin; IT=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=JM%20Solo" | jq -r '.Items[0].Id')
    B='{"DeviceProfile":{"DirectPlayProfiles":[],"TranscodingProfiles":[]}}'
    curl -sS -o "$G/b" -w 'no token: %{http_code} %{size_download}\n' -X POST -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo"
    login nolib
    curl -sS -o "$G/b" -w 'no library: %{http_code} %{size_download}\n' -X POST -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo"
    login viewer
    curl -sS -o "$G/b" -w 'allowed POST: %{http_code} %{size_download}\n' -X POST -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo"; cat "$G/b"; echo
    curl -sS -o "$G/b" -w 'allowed GET: %{http_code} %{size_download}\n' -H "$(hdr)" "$BASE/Items/$IT/PlaybackInfo"; cat "$G/b"; echo
    gate jellymesh up
    curl -sS -H "$ACCEPT" -o "$G/b" -w 'online GET: %{http_code} %{size_download}\n' -H "$(hdr)" "$BASE/Items/$IT/PlaybackInfo"; jq -c '.MediaSources[0]|{Path,SupportsDirectPlay,SupportsTranscoding,TranscodingSubProtocol,TranscodingContainer,TranscodingUrl}' "$G/b"
    ;;
  late)
    echo "== remote dies after PlaybackInfo: no retry loop"; glog
    (sleep 9; echo down > "$G/gate-jellymesh") &
    MAXBIT=300000 bash "$S/web.sh" "JM Solo JellyMesh" fail 30 2>&1 | grep -cE 'REQ POST .*PlaybackInfo'
    gate jellymesh up
    ;;
  noseg)
    echo "== first segment 500 on both: local fallback"; glog; gate jellymesh noseg
    web "JM Solo JellyMesh" play 30
    gate jellymesh up
    ;;
  noseg2)
    echo "== first segment 500 on the winner: next remote"; glog; gate jellymesh noseg
    web "Sonda Senza ID" play 20
    gate jellymesh up; grep -c . "$G/gatelog-standard"
    ;;
  badcodec)
    echo "== master offers a codec the client lacks: next remote"; glog; gate jellymesh badcodec
    web "Sonda Senza ID" play 20
    gate jellymesh up; grep -c 'master' "$G/gatelog-standard"
    ;;
  redirect)
    echo "== remote redirects a segment: not followed"; glog; gate jellymesh redirect
    web "Sonda Senza ID" play 20
    gate jellymesh up; grep -c 'master' "$G/gatelog-standard"
    ;;
  nosegsubs)
    echo "== first segment 500, title with external subtitles: local fallback"; glog; gate standard noseg
    WATCH=50 web "JM Audio e Sottotitoli" play 30
    gate standard up
    docker logs --since 90s $NAME 2>&1 | grep -E 'ffmpeg -analyze' | sed -E 's/.* -i /-i /' | grep -oE '^-i "[^"]*"| -map 0:[0-9]+| -codec:v:0 [a-z0-9_-]+' | tr -d '\n'; echo
    awk '/^---/{t=$2} /ffmpeg/{print t, $0}' "$G/watch-JM_Audio_e_Sottotitoli_play.txt" | cut -c1-120 | tail -2
    ;;
  stop)
    echo "== stop closes the remote transcode"; WATCH=35 web "JM Solo JellyMesh" play 20
    sleep 6; bash "$S/ffm.sh" | cut -c1-120
    ;;
  resume)
    echo "== pause longer than the sweeper: resume"; WATCH=170 web "JM Solo JellyMesh" resume 125
    ;;
esac
