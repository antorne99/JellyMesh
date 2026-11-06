#!/usr/bin/env bash
#
# Builds the plugin in the .NET SDK image and installs it into a running
# Jellyfin container, then restarts it. Jellyfin discovers plugins at startup.
#
# usage: install-plugin.sh <container>
#   e.g. install-plugin.sh jellymesh-test-locale-1
#
# NUGET_VOLUME names the Docker volume used as package cache.

set -euo pipefail

container=${1:?usage: install-plugin.sh <container>}
repo=$(cd "$(dirname "$0")/../../.." && pwd)
nuget_volume=${NUGET_VOLUME:-jellymesh-nuget}

yaml_value() {
  sed -n "s/^$1: \"\(.*\)\"$/\1/p" "$repo/build.yaml"
}

version=$(yaml_value version)
image=$(yaml_value image)
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT

echo "building $version"
docker run --rm -v "$repo":/src -v "$nuget_volume":/root/.nuget/packages -v "$out":/out -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o /out/build --nologo -v quiet

jq -n \
  --arg guid "$(yaml_value guid)" --arg name "$(yaml_value name)" --arg version "$version" \
  --arg targetAbi "$(yaml_value targetAbi)" --arg category "$(yaml_value category)" \
  --arg owner "$(yaml_value owner)" --arg overview "$(yaml_value overview)" \
  --arg imagePath "$(basename "$image")" \
  '{$guid, $name, $version, $targetAbi, $category, $owner, $overview, description: $overview,
    status: "Active", autoUpdate: false, $imagePath, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' \
  >"$out/meta.json"

target=/config/plugins/JellyMesh_$version
echo "installing into $container:$target"
docker exec "$container" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
docker cp "$out/build/Jellyfin.Plugin.JellyMesh.dll" "$container:$target/"
docker cp "$repo/$image" "$container:$target/"
docker cp "$out/meta.json" "$container:$target/"
docker restart "$container" >/dev/null
echo "restarted $container"
