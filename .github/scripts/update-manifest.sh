#!/usr/bin/env bash
#
# Writes a released version into manifest.json: its download URL, its MD5 and what the zip's meta.json says about it. A version
# already listed is replaced. Jellyfin reads the manifest from the main branch, and the release job commits this change there.
#
# usage: update-manifest.sh <manifest.json> <zip> <version tag, such as v0.1.0.0> <owner/repository>

set -euo pipefail

manifest=$1
zip=$2
tag=$3
repository=$4

version=${tag#v}
checksum=$(md5sum "$zip" | cut -d' ' -f1)
url=https://github.com/$repository/releases/download/$tag/$(basename "$zip")

unzip -p "$zip" meta.json |
  jq --arg url "$url" --arg checksum "$checksum" \
    '{version, changelog, targetAbi, sourceUrl: $url, checksum: $checksum, timestamp}' >"$manifest.entry"

jq --arg version "$version" --slurpfile entry "$manifest.entry" \
  '.[0].versions = $entry + (.[0].versions | map(select(.version != $version)))' "$manifest" >"$manifest.next"

mv "$manifest.next" "$manifest"
rm "$manifest.entry"
