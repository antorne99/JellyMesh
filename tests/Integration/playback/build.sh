#!/usr/bin/env bash
# Builds in docker from the worktree root. Args: plugin | test [filter]
source "$(dirname "$0")/lib.sh"
mode=${1:-plugin}; filter=${2:-}
case $mode in
  plugin) cmd='dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release --nologo -v q -o /tmp/out/build' ;;
  test) cmd="dotnet test tests/Unit/Jellyfin.Plugin.JellyMesh.Tests.csproj --nologo -v q ${filter:+--filter $filter}" ;;
esac
docker run --rm -v "$WT:/src" -v jellymesh-nuget:/root/.nuget/packages -v jm-play-obj:/tmp/o -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c "$cmd 2>&1; rc=\$?; find . -name bin -type d -prune -exec rm -rf {} + ; find . -name obj -type d -prune -exec rm -rf {} + ; exit \$rc" 2>&1 | grep -vE '^\s*$' | sed -E 's|/src/||' | sort -u | tail -${LINES_MAX:-45}
