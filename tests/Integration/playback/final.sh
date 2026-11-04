#!/usr/bin/env bash
# The whole proof, in order. Output goes to the caller; captures stay in run/.
source "$(dirname "$0")/lib.sh"
G=$S/run
rm -f "$G"/cap-* "$G"/watch-* "$G"/gatelog-*
bash "$S/gates.sh"
sc() { bash "$S/sc.sh" "$@"; }
FILTER='SEEN|STARTED|PROGRESS|DIALOGS|AFTER|RESUMED|PAUSED|SEEK|MENU|PICK|PAGEERR|Traceback'
webq() { bash "$S/web.sh" "$@" 2>&1 | grep -E "$FILTER" | cut -c1-200; }
played() { login viewer; ME=$(get /Users/Me | jq -r .Id); get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME&SearchTerm=$(jq -rn --arg t "$1" '$t|@uri')" | jq -c '.Items[0]|{Name,UserData:(.UserData|{Played,PlayCount,PlaybackPositionTicks})}'; }

echo "### 1 HLS copy video, seek, remoto-jellymesh"; MAXBIT= webq "JM Confronto Codec" seek 12
echo "### 2 HLS software transcode, seek, remoto-standard"; MAXBIT=300000 webq "Tears of Steel" seek 30
echo "### 3 direct-capable fixture, seek"; MAXBIT= ITEM=0697962069b7fd3ac2a82f14e637dbd3 webq Diretto seek 12
echo "### 4 audio change and external subtitles"; MAXBIT=300000 WATCH=70 webq "JM Audio e Sottotitoli" audio 20 0
echo "ffmpeg timeline on remoto-standard:"; awk '/^---/{t=$2} /ffmpeg/{print t, $0}' "$G/watch-JM_Audio_e_Sottotitoli_audio.txt" | cut -c1-110 | awk '{k=$3; if(!(k in s)){s[k]=$1; print "first " $0} l[k]=$1} END{for(k in l) print "last  " k, l[k]}'
echo "### 5 subtitle menu"; MAXBIT=300000 webq "JM Audio e Sottotitoli" subs 12
echo "### 6 episode stub"; login viewer; ME=$(get /Users/Me | jq -r .Id)
EP=$(get "/Items?Recursive=true&IncludeItemTypes=Episode&userId=$ME&Fields=Path" | jq -r '[.Items[]|select(.Path|startswith("/federata"))][0].Id'); echo "episode $EP"
MAXBIT=300000 ITEM=$EP webq "episode" play 20
echo "### 7 stop"; sc stop
awk '/^---/{t=$2} /ffmpeg/{print t, $0}' "$G/watch-JM_Solo_JellyMesh_play.txt" | cut -c1-90 | sed -n '1p;$p'; sed -n '$p' "$G/watch-JM_Solo_JellyMesh_play.txt"
echo "### 8 backup"; sc backup
echo "### 9 offline"; sc offline
echo "### 10 hang"; sc hang
echo "### 11 auth"; sc auth
echo "### 12 next remote on segment 500"; sc noseg2
echo "### 13 next remote on codec"; sc badcodec
echo "### 14 next remote on redirect"; sc redirect
echo "### 15 local fallback"; sc noseg
echo "### 16 local fallback, subtitles"; sc nosegsubs
echo "### 17 late failure"; played "JM Solo JellyMesh"; sc late; played "JM Solo JellyMesh"
echo "### 18 resume after sweeper"; sc resume; awk '/^---/{t=$2} /ffmpeg/{print t, $0}' "$G/watch-JM_Solo_JellyMesh_resume.txt" | cut -c1-90 | sed -n '1p;$p'
echo "### 19 late on direct";
echo "### end"
