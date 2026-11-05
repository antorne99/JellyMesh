#!/usr/bin/env bash
# Installs a new build of this checkout on the servers whose page is checked, keeping their state.
# usage: UI_PREFIX=<name> UI_PORT=<port> reinstall.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

install_plugin fresh
install_plugin first
