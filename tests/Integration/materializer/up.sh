#!/usr/bin/env bash
# Fresh private instance with the two-path libraries, plugin installed, two remotes added.
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" "$NAME-extra" >/dev/null 2>&1 || true
rm -f "$TOKEN_FILE"; TOKEN=
docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway \
  -e TZ=Europe/Rome \
  -e 'JELLYFIN_Serilog__MinimumLevel__Override__MediaBrowser.MediaEncoding.Encoder.MediaEncoder=Debug' \
  -e 'JELLYFIN_Serilog__MinimumLevel__Override__Microsoft.AspNetCore.Hosting.Diagnostics=Information' \
  -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$NAME-fed:/federata" -v "$NAME-extra:/local-extra" \
  -v "$MEDIA/locale:/media:ro" "$IMAGE" >/dev/null
wait_ready
post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
get /Startup/User >/dev/null
post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}'
post /Startup/Complete
docker exec "$NAME" sh -c 'mkdir -p /federata/Film /federata/Serie /local-extra/film /local-extra/serie && touch /federata/Film/.jellymesh /federata/Serie/.jellymesh /local-extra/film/.keep /local-extra/serie/.keep'
login admin jellymesh-test
OPTS='{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
post "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film&paths=/local-extra/film&paths=/federata/Film" "$OPTS"
post "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/media/serie&paths=/local-extra/serie&paths=/federata/Serie" "$OPTS"
post /Users/New '{"Name":"mario","Password":"jellymesh-test"}' >/dev/null
scan
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$NAME"
sleep 3
wait_ready
login admin jellymesh-test
get /Plugins | jq -c '.[] | select(.Name=="JellyMesh") | {Name,Version,Status}'
