#!/usr/bin/env bash
# Samples the managed stacks of the threads running JellyMesh code in the consumer of run.sh (SCALE_NAME, by default
# jm-scale-consumer), while run.sh runs with KEEP=1 or during a phase. The Jellyfin image is self-contained, so
# dotnet-stack runs from an SDK container that shares the consumer's PID namespace and reaches its diagnostic socket
# through /proc/1/root/tmp.
# usage: stacks.sh <out file> [count] [interval s]   (remove the tool volume afterwards: docker volume rm <name>-tools)
set -uo pipefail
out=${1:?usage: stacks.sh <out file> [count] [interval s]}
count=${2:-10}
every=${3:-15}
SDK=mcr.microsoft.com/dotnet/sdk:10.0
NAME=${SCALE_NAME:-jm-scale-consumer}
TOOLS=${NAME%-consumer}-tools

docker run --rm -v "$TOOLS":/tools -v jellymesh-nuget:/root/.nuget/packages "$SDK" \
  sh -c '[ -x /tools/dotnet-stack ] || dotnet tool install dotnet-stack --tool-path /tools' >/dev/null
for i in $(seq 1 "$count"); do
  echo "=== sample $i $(date -u +%T)" >>"$out"
  docker run --rm --name "${NAME%-consumer}-probe" --pid=container:"$NAME" --cap-add SYS_PTRACE -e TMPDIR=/proc/1/root/tmp \
    -v "$TOOLS":/tools "$SDK" /tools/dotnet-stack report -p 1 2>/dev/null |
    awk '/^Thread/ { if (t ~ /JellyMesh/) print t; t = $0 "\n"; next } { t = t $0 "\n" } END { if (t ~ /JellyMesh/) print t }' >>"$out"
  sleep "$every"
done
