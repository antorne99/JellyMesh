#!/usr/bin/env bash
#
# Prints what each instance really indexed, as Jellyfin reports it, which is
# what the plugin will read through /Items. Compare it with the README matrix.
#
# Bitrate columns: "source" is MediaSource.Bitrate, "video" the video stream's.
# A dash means Jellyfin did not report the value.

set -euo pipefail

source "$(dirname "$0")/common.sh"

FIELDS=ProviderIds,MediaSources,MediaStreams,Path,ProductionYear,Overview

for instance in locale remoto-standard remoto-jellymesh; do
  use_instance "$instance"
  login
  user=$(get /Users/Me | jq -r .Id)
  echo "== $instance"
  {
    printf 'item\theight\tcodec\trange\tsource\tvideo\tversions\taudio\tsubs\tids\tplot\n'
    get "/Items?userId=$user&Recursive=true&IncludeItemTypes=Movie,Series,Episode&SortBy=Path&Fields=$FIELDS" |
      jq -r '
        .Items[]
        | (.MediaSources // []) as $sources
        | ($sources[0].MediaStreams // []) as $streams
        | ($streams | map(select(.Type == "Video")) | .[0] // {}) as $video
        | [
            (if .Type == "Episode"
             then "\(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber)"
             else "\(.Name) (\(.ProductionYear // "?"))" end),
            ($video.Height // "-"),
            ($video.Codec // "-"),
            ($video.VideoRangeType // "-"),
            ($sources[0].Bitrate // "-"),
            ($video.BitRate // "-"),
            ($sources | length),
            ($streams | map(select(.Type == "Audio")) | length),
            ($streams | map(select(.Type == "Subtitle")) | length),
            ((.ProviderIds // {}) | to_entries | map("\(.key)=\(.value)") | sort | join(",") | if . == "" then "none" else . end),
            ((.Overview // "-") | .[0:24])
          ]
        | @tsv'
  } | column -t -s "$(printf '\t')"
  echo
done

echo "== non-admin user on the remotes"
for instance in remoto-standard remoto-jellymesh; do
  use_instance "$instance"
  login "$REMOTE_USER" "$REMOTE_PASSWORD"
  get /Users/Me | jq -r --arg i "$instance" \
    '"\($i): \(.Name), IsAdministrator=\(.Policy.IsAdministrator), EnableAllFolders=\(.Policy.EnableAllFolders)"'
done
