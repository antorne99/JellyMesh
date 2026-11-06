#!/usr/bin/env bash
# Moves the running servers between states of the page: a remote stopped or back, the secret key gone or back.
# usage: UI_PREFIX=<name> UI_PORT=<port> state.sh offline|online [stock|peer] | nokey|key [fresh|first]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

case ${1:-} in
  offline) offline "${2:-stock}" ;;
  online) online "${2:-stock}" ;;
  nokey) set_key "${2:-fresh}" ;;
  key) set_key "${2:-fresh}" "$TEST_KEY" ;;
  *) sed -n 2p "$0" >&2; exit 2 ;;
esac
