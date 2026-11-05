#!/usr/bin/env bash
#
# Packs a Release build into the zip a Jellyfin plugin repository serves, with its checksums, all in artifacts/. It expects
# the build in artifacts/build, and needs zip, md5sum and sha256sum.
#
# Jellyfin checks the manifest's "checksum" as MD5. The SHA-256 is for people who download the zip by hand.
#
# usage: package.sh [version tag, such as v0.1.0.0]

set -euo pipefail

cd "$(dirname "$0")/../.."

tag=${1:-}
out=artifacts

value() {
  sed -n "s/^$1: \"\(.*\)\"$/\1/p" build.yaml
}

# The text of a "key: >" block of build.yaml, on one line.
folded() {
  awk -v key="$1:" '
    $1 == key && $2 == ">" { on = 1; next }
    on && /^  / { sub(/^ +/, ""); text = text (text == "" ? "" : " ") $0; next }
    on { exit }
    END { print text }' build.yaml
}

escaped() {
  sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' <<<"$1"
}

version=$(value version)
props_version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)
if [ "$version" != "$props_version" ]; then
  echo "build.yaml has version $version, Directory.Build.props has $props_version" >&2
  exit 1
fi

if [ -n "$tag" ] && [ "$tag" != "v$version" ]; then
  echo "tag $tag does not match build.yaml version $version" >&2
  exit 1
fi

dll=$(sed -n 's/^- "\(.*\.dll\)"$/\1/p' build.yaml)
staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT
cp "$out/build/$dll" "$staging/"

cat >"$staging/meta.json" <<JSON
{
  "guid": "$(value guid)",
  "name": "$(value name)",
  "overview": "$(escaped "$(value overview)")",
  "description": "$(escaped "$(folded description)")",
  "owner": "$(value owner)",
  "category": "$(value category)",
  "version": "$version",
  "changelog": "$(escaped "$(folded changelog)")",
  "targetAbi": "$(value targetAbi)",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["$dll"]
}
JSON

zip_name=jellymesh_$version.zip
absolute_out=$(cd "$out" && pwd)
rm -f "$absolute_out/$zip_name" "$absolute_out/$zip_name.md5" "$absolute_out/$zip_name.sha256"
(cd "$staging" && zip -q -X "$absolute_out/$zip_name" "$dll" meta.json)
(cd "$out" && md5sum "$zip_name" >"$zip_name.md5" && sha256sum "$zip_name" >"$zip_name.sha256")

echo "$out/$zip_name"
cat "$out/$zip_name.md5" "$out/$zip_name.sha256"
