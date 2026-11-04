#!/usr/bin/env bash
# Second part of the proof: audio change on a really different track, watch state, sweeper evidence, stock comparison.
source "$(dirname "$0")/lib.sh"
G=$S/run
FILTER='SEEN|STARTED|DIALOGS|AFTER|MENU|PICK|PAGEERR|Traceback|STATE|REQ POST.*PlaybackInfo|CON HLS Error: Type: otherError'
webq() { bash "$S/web.sh" "$@" 2>&1 | grep -E "$FILTER" | sed -E "s/'src': '[^']*', //; s#http://host.docker.internal:18320##g" | cut -c1-170; }
played() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items/$1?userId=$ME" | jq -c '{Name,UserData:(.UserData|{Played,PlayCount,PlaybackPositionTicks})}'; }
byname() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME&SearchTerm=$(jq -rn --arg t "$1" '$t|@uri')" | jq -r '.Items[0].Id'; }
echo "### sweeper evidence from the resume run"
grep -E 'DELETE|Sessions|Ping' "$G/gatelog-jellymesh" | cut -c1-120 | tail -4
echo "remote /Sessions/Playing calls on both gates: $(cat "$G"/gatelog-* | grep -c 'Sessions/Playing')"
echo "user agents: $(cat "$G"/gatelog-* | grep -oE 'ua=[^ ]+' | sort | uniq -c | tr '\n' ' ')"
echo "ApiKey in remote URLs: $(cat "$G"/gatelog-* | grep -c 'ApiKey=')"
echo "### audio change"
MAXBIT=300000 WATCH=70 webq "JM Audio e Sottotitoli" audio 20 1
awk '/^---/{t=$2} /ffmpeg/{print t, $3, $7, $8, $9, $10}' "$G/watch-JM_Audio_e_Sottotitoli_audio.txt" | cut -c1-90 | awk '{k=$2; if(!(k in s)){s[k]=1; print "first", $0} l[k]=$1} END{for(k in l) print "last", k, l[k]}'
sed -n '$p' "$G/watch-JM_Audio_e_Sottotitoli_audio.txt"
echo "### watch state: remote dies after PlaybackInfo, before any byte plays. 60 s title, position 0"
T=$(byname "Tears of Steel"); played "$T"
: > "$G/gatelog-standard"; echo late0 > "$G/gate-standard"
MAXBIT=300000 ITEM=$T webq "Tears" fail 25
echo up > "$G/gate-standard"
played "$T"
echo "### same as a real film: MinResumeDurationSeconds 0, remote dies mid-play"
login admin; post /System/Configuration "$(get /System/Configuration | jq -c '.MinResumeDurationSeconds=0')"
T=$(byname "Sonda Senza ID"); played "$T"
(sleep 12; echo down > "$G/gate-jellymesh") &
MAXBIT=300000 ITEM=$T webq "SondaLate" fail 28
echo up > "$G/gate-jellymesh"
played "$T"
login admin; post /System/Configuration "$(get /System/Configuration | jq -c '.MinResumeDurationSeconds=300')"
echo "### stock local title in the same browser, for the hls.js internalException"
MAXBIT=300000 webq "Elephants Dream" play 20
echo "### api answers, as viewer"
login viewer; ME=$(get /Users/Me | jq -r .Id)
get "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Series&userId=$ME&Fields=MediaSources,MediaStreams,Path,People,Overview,ProviderIds&Limit=500" > "$G/api-items.json"
IT=$(jq -r '.Items[]|select(.Name=="JM Audio e Sottotitoli").Id' "$G/api-items.json")
get "/Items/$IT?userId=$ME" > "$G/api-item.json"
get "/Sessions" > "$G/api-sessions.json"
curl -fsS -H "$(hdr)" "$BASE/Items/$IT/File" > "$G/api-file.json"
echo "### logs"
docker logs $NAME 2>&1 > "$G/server-log.txt"
docker exec $NAME sh -c 'cat /config/log/log_*.log' >> "$G/server-log.txt"
for v in 18321 18322 host.docker.internal ':8097' ':8098'; do echo "$v in server log: $(grep -c "$v" "$G/server-log.txt")"; done
for t in $(cat "$G"/gatelog-* | grep -oE 'auth=[0-9a-f]{16,}' | sort -u | cut -d= -f2); do echo "remote token in server log: $(grep -c "$t" "$G/server-log.txt")"; done
echo "strm: $(docker exec $NAME cat "/federata/Film/JM Audio e Sottotitoli (2026)/JM Audio e Sottotitoli (2026).strm")"
echo "### end"
