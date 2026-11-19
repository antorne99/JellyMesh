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

# The 0.7 types: music, music videos, home videos and photos, books and audiobooks,
# and the mixed library. "detail" is the audio format, the book file, chapter
# count, or for photos the camera and whether GPS came through. Long IDs show
# their last four characters, MusicBrainz as Mb.
FIELDS07=ProviderIds,MediaSources,MediaStreams,Path,ProductionYear,Chapters,ParentId
TYPES07=MusicAlbum,Audio,MusicVideo,Video,Photo,PhotoAlbum,Book,AudioBook
for instance in locale remoto-standard remoto-jellymesh; do
  use_instance "$instance"
  login
  user=$(get /Users/Me | jq -r .Id)
  views=$(get "/UserViews?userId=$user" | jq -c '[.Items[] | {(.Id): (.CollectionType // "mixed")}] | add // {}')
  items=$(get "/Items?userId=$user&Recursive=true&IncludeItemTypes=$TYPES07&SortBy=Path&Fields=$FIELDS07")
  [ "$(echo "$items" | jq '.Items | length')" -gt 0 ] || continue
  echo "== $instance, 0.7 types (views: $(echo "$views" | jq -r 'to_entries | map(.value) | join(", ")'))"
  {
    printf 'type\titem\tdisc/track\tcontainer\tdetail\tids\tpath\n'
    echo "$items" | jq -r '
        .Items[]
        | (.MediaSources // []) as $sources
        | ($sources[0].MediaStreams // []) as $streams
        | ($streams | map(select(.Type == "Audio")) | .[0] // {}) as $audio
        | [
            .Type,
            (if .Type == "Audio" then "\(.Album) / \(.Name)" else .Name end),
            (if .IndexNumber then "\(.ParentIndexNumber // "-")/\(.IndexNumber)" else "-" end),
            ($sources[0].Container // "-"),
            (if .Type == "Photo" then "\(.CameraMake // "-") \(.CameraModel // "-"), gps=\(if .Latitude then "\(.Latitude),\(.Longitude)" else "none" end)"
             elif .Type == "AudioBook" then "\($audio.Codec // "-"), chapters=\(.Chapters // [] | length)"
             elif .Type == "Audio" then "\($audio.Codec // "-") \($audio.BitDepth // "-")bit \($audio.SampleRate // "-")Hz \($audio.BitRate // "-")"
             else "-" end),
            ((.ProviderIds // {}) | to_entries | map("\(.key | sub("^MusicBrainz"; "Mb"))=\(.value | if length > 12 then "…" + .[-4:] else . end)")
             | sort | join(",") | if . == "" then "none" else . end),
            ((.Path // "-") | sub("^/media/"; ""))
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
