#!/usr/bin/env bash
# Moves the running servers between states of the page: a remote stopped or back, the key file lost, blocked or fixed.
# usage: UI_PREFIX=<name> UI_PORT=<port> state.sh offline|online [stock|peer] | nokey|blockkey|key [fresh|first]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

case ${1:-} in
  offline) offline "${2:-stock}" ;;
  online) online "${2:-stock}" ;;
  nokey) lose_key "${2:-fresh}" ;;
  blockkey) block_key "${2:-first}" ;;
  key) fix_key "${2:-fresh}" ;;
  *) sed -n 2p "$0" >&2; exit 2 ;;
esac
