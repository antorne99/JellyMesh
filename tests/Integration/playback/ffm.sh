#!/usr/bin/env bash
# Read-only: what runs on the two remotes and the private instance. No ps in the image, so /proc via a mounted script.
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
read -r -d '' Q <<'EOF'
for p in /proc/[0-9]*; do
  [ "$(readlink $p/exe 2>/dev/null)" = /usr/lib/jellyfin-ffmpeg/ffmpeg ] || continue
  c=$(tr '\000' ' ' <$p/cmdline 2>/dev/null)
  echo "  ffmpeg ${p#/proc/}:$(echo "$c" | grep -oE ' -i [^ ]+| -ss [0-9:.]+| -start_number [0-9]+| -codec:[av]:0 [a-z0-9_-]+| -map 0:[0-9]+' | tr -d '\n' | cut -c1-200)"
done
echo "  files: $(ls /cache/transcodes 2>/dev/null | wc -l)"
EOF
for c in jellymesh-test-remoto-standard-1 jellymesh-test-remoto-jellymesh-1 jm-play; do
  echo "$c"; docker exec "$c" /bin/sh -c "$Q"
done
