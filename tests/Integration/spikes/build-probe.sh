#!/usr/bin/env bash
#
# Builds the probe in docker. Output: <dir>/bin-out/JellyMeshProbe.dll and meta.json.
# Only the plugin's own dll is shipped: server assemblies come from the server.

set -euo pipefail

dir=$(cd "$(dirname "$0")/JellyMeshProbe" && pwd)
docker run --rm -v "$dir:/src" -v jellymesh-nuget:/root/.nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'dotnet build -c Release -o /tmp/out --nologo -v q 2>&1 | tail -25 && mkdir -p bin-out && cp /tmp/out/JellyMeshProbe.dll bin-out/ && cp meta.json bin-out/ && rm -rf bin obj'
ls -la "$dir/bin-out"
