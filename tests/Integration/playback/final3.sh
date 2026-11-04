#!/usr/bin/env bash
# Third part: watch state on titles really unplayed, the sweeper, and captures for the leak scan.
source "$(dirname "$0")/lib.sh"
G=$S/run
FILTER='SEEN|STARTED|DIALOGS|AFTER|PAGEERR|Traceback|STATE|SEEK'
webq() { bash "$S/web.sh" "$@" 2>&1 | grep -E "$FILTER" | sed -E "s/'src': '[^']*', //; s#http://host.docker.internal:18320##g" | cut -c1-170; }
played() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items/$1?userId=$ME" | jq -c '{Name,UserData:(.UserData|{Played,PlayCount,PlaybackPositionTicks})}'; }
byname() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME&SearchTerm=$(jq -rn --arg t "$1" '$t|@uri')" | jq -r '.Items[0].Id'; }
unplay() { login viewer; ME=$(get /Users/Me | jq -r .Id); curl -fsS -X DELETE -H "$ACCEPT" -H "$(hdr)" "$BASE/UserPlayedItems/$1?userId=$ME" >/dev/null; }
echo "### watch state 1: remote dies between PlaybackInfo and the first byte, 60 s title, default settings"
T=$(byname "Tears of Steel"); unplay "$T"; played "$T"
echo late0 > "$G/gate-standard"
MAXBIT=300000 ITEM=$T webq "Tears" fail 15
echo up > "$G/gate-standard"
played "$T"
echo "### watch state 2: remote dies mid-play, MinResumeDurationSeconds 0 standing in for a real film"
login admin; post /System/Configuration "$(get /System/Configuration | jq -c '.MinResumeDurationSeconds=0')"
T=$(byname "Sonda Senza ID"); unplay "$T"; played "$T"
(sleep 12; echo down > "$G/gate-jellymesh") &
MAXBIT=300000 ITEM=$T webq "SondaLate" fail 28
echo up > "$G/gate-jellymesh"
played "$T"
login admin; post /System/Configuration "$(get /System/Configuration | jq -c '.MinResumeDurationSeconds=300')"
echo "### remote watch state of the dedicated user, unchanged by all of this"
: > "$G/gatelog-standard"
echo "calls to /Sessions/Playing on the remotes, whole run: $(cat "$G"/gatelog-*.all | grep -c 'Sessions/Playing')"
echo "### sweeper: client vanishes after segment 0"
login viewer
B=$(jq -c '{DeviceProfile:.DeviceProfile}' "$G/web-playbackinfo.json")
IT=$(byname "JM Solo JellyMesh"); login viewer
: > "$G/gatelog-jellymesh"
curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo?maxStreamingBitrate=300000" > "$G/sw.json"
U=$(jq -r '.MediaSources[0].TranscodingUrl' "$G/sw.json"); P=${U%%/master*}
V=$(curl -fsS "$BASE$U" | grep -v '^#' | head -1)
SEG=$(curl -fsS "$BASE$P/$V" | grep -v '^#' | sed -n 6p)
echo "started $(date +%H:%M:%S)"; bash "$S/ffm.sh" | grep -A1 remoto-jellymesh | cut -c1-100
for i in $(seq 1 24); do grep -q 'DELETE' "$G/gatelog-jellymesh" && break; perl -e 'select(undef,undef,undef,5)'; done
grep -E 'DELETE' "$G/gatelog-jellymesh" | cut -c1-40
perl -e 'select(undef,undef,undef,3)'; bash "$S/ffm.sh" | grep -A1 remoto-jellymesh | cut -c1-100
echo "same URL after the sweep, seeking to the 6th line of the variant:"
curl -sS -o /dev/null -w '%{http_code} %{size_download} %{time_total}s\n' "$BASE$P/f/$(echo "$V" | cut -d/ -f2)/../${SEG#../}"
bash "$S/ffm.sh" | grep -A1 remoto-jellymesh | cut -c1-100
curl -sS -o /dev/null -w 'DELETE %{http_code}\n' -X DELETE -H "$(hdr)" "$BASE/Videos/ActiveEncodings?deviceId=x&playSessionId=$(jq -r .PlaySessionId "$G/sw.json")"
perl -e 'select(undef,undef,undef,3)'; bash "$S/ffm.sh" | grep -A1 remoto-jellymesh | cut -c1-100
echo "### captures for the leak scan"
MAXBIT= webq "JM Confronto Codec" seek 12
MAXBIT=300000 webq "Tears of Steel" seek 30
MAXBIT= ITEM=0697962069b7fd3ac2a82f14e637dbd3 webq Diretto seek 12
MAXBIT=300000 webq "JM Audio e Sottotitoli" subs 12
MAXBIT=300000 webq "JM Audio e Sottotitoli" audio 14 1
echo "### end"
