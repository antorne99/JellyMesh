#!/usr/bin/env bash
# Removes the servers of the linked versions proof, their volumes and the media volume.
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
down
exit 0
