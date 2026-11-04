#!/usr/bin/env bash
# Runs a Playwright script from the scratch folder, with the module installed once in a volume.
# Reaches the private instance through the published port, so the browser is never loopback for it.
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
exec docker run --rm ${NET:---add-host host.docker.internal:host-gateway} -v "$S:/w" -v jm-play-pw:/pw -e PYTHONPATH=/pw -e MAXBIT="$MAXBIT" \
  mcr.microsoft.com/playwright/python:v1.48.0-noble \
  bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ "$@"
