#!/usr/bin/env bash
# Fourth part: audio change timeline from the gate log, shutdown closes, segment speed.
source "$(dirname "$0")/lib.sh"
G=$S/run
echo "### audio change timeline"
: > "$G/gatelog-standard"
MAXBIT=300000 WATCH=45 bash "$S/web.sh" "JM Audio e Sottotitoli" audio 14 1 2>&1 | grep -E 'MENU|PICK|REQ POST .*PlaybackInfo|REQ DELETE|AFTER' | sed -E "s/'src': '[^']*', //; s#http://host.docker.internal:18320##g" | cut -c1-120
python3 - "$G/gatelog-standard" <<'PY'
import re, sys
seen = {}
for l in open(sys.argv[1]):
    m = re.search(r'PlaySessionId=(\w+)', l)
    t = l.split()[0]
    if ' POST ' in l and 'PlaybackInfo' in l:
        print(t, 'PlaybackInfo')
    if not m:
        continue
    ps = m[1][:6]
    kind = 'DELETE' if ' DELETE ' in l else 'segment' if '/hls1/' in l else 'playlist'
    if (ps, kind) not in seen:
        print(t, ps, 'first', kind)
    seen[(ps, kind)] = t
for (ps, kind), t in seen.items():
    print('last', ps, kind, t)
PY
awk '/^---/{t=$2; n=0} /ffmpeg/{n++; c[t]=n; o[++k]=t} END{for(i=1;i<=k;i++) if(!(o[i] in d)){d[o[i]]=1; printf "%s:%d ", o[i], c[o[i]]} print ""}' "$G/watch-JM_Audio_e_Sottotitoli_audio.txt"
echo "### files on the remotes now"
bash "$S/ffm.sh" | cut -c1-90
echo "### shutdown"
login viewer; ME=$(get /Users/Me | jq -r .Id)
B=$(jq -c '{DeviceProfile:.DeviceProfile}' "$G/web-playbackinfo.json")
IT=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&userId=$ME&SearchTerm=JM%20Solo" | jq -r '.Items[0].Id')
: > "$G/gatelog-jellymesh"
curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo?maxStreamingBitrate=300000" > "$G/sd.json"
echo "session open; restarting $(date +%H:%M:%S)"
docker restart $NAME >/dev/null
grep -E 'DELETE' "$G/gatelog-jellymesh" | cut -c1-60
perl -e 'select(undef,undef,undef,3)'; bash "$S/ffm.sh" | grep -A1 remoto-jellymesh | cut -c1-100
wait_up
echo "### segment time during a software transcode"
login viewer
curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "$B" "$BASE/Items/$IT/PlaybackInfo?maxStreamingBitrate=300000" > "$G/sp.json"
U=$(jq -r '.MediaSources[0].TranscodingUrl' "$G/sp.json"); P=${U%%/master*}
V=$(curl -fsS "$BASE$U" | grep -v '^#' | head -1)
for n in 1 2 3 4 5 6 7; do
  SEG=$(curl -fsS "$BASE$P/$V" | grep -v '^#' | sed -n ${n}p)
  curl -sS -o /dev/null -w "segment $n: %{http_code} %{time_total}s\n" "$BASE$P/f/$(echo "$V" | cut -d/ -f2)/../${SEG#../}"
done
curl -sS -o /dev/null -w 'DELETE %{http_code}\n' -X DELETE -H "$(hdr)" "$BASE/Videos/ActiveEncodings?deviceId=x&playSessionId=$(jq -r .PlaySessionId "$G/sp.json")"
echo "### anon rerun with a clean body file"
bash "$S/anon.sh" 2>&1 | grep -E 'LEAK|^== |^(Videos|Audio/stream static|Play endpoint|published|inside|master)' | cut -c1-80
echo "### end"
