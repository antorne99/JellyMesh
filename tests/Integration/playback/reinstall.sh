source "$(dirname "$0")/lib.sh"
bash "$WT/tests/Integration/scripts/install-plugin.sh" $NAME 2>&1 | grep -E 'error|restarted|Warn' | cut -c1-220
rm -rf "$WT/Jellyfin.Plugin.JellyMesh/bin" "$WT/Jellyfin.Plugin.JellyMesh/obj"
sleep 3; wait_up; echo up
