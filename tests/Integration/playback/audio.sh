#!/usr/bin/env bash
# Fourth part: audio change timeline from the gate log, shutdown closes, segment speed.
source "$(dirname "$0")/lib.sh"
G=$S/run
echo "### audio change timeline"
: > "$G/gatelog-standard"
MAXBIT=300000 WATCH=45 bash "$S/web.sh" "JM Audio e Sottotitoli" audio 14 "$1" 2>&1 | grep -E 'MENU|PICK|REQ POST .*PlaybackInfo|REQ DELETE|AFTER' | sed -E "s/'src': '[^']*', //; s#http://host.docker.internal:18320##g" | cut -c1-120
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
echo "### end"
