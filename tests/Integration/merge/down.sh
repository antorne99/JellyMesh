#!/usr/bin/env bash
# Removes the private servers, their volumes and the scratch folder.
set -uo pipefail
MEDIA=${MEDIA:-unused}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
merge_down
rm -rf "$WORK"
exit 0
