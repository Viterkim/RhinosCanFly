#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

description='Build for Rhino 8 and 9 on Mac.'

for option in "$@"; do
    [ "$option" != --rhino-version ] || fail "build-all.sh always builds both Rhino 8 and 9. Use build.sh to select one."
done

read_options "$@"
require_mac

arguments=(--configuration "$configuration")

if [ "$preflight" = true ]; then
    arguments+=(--preflight)
fi

if [ "$clean" = true ]; then
    arguments+=(--clean)
fi

for version in 8 9; do
    bash "$root/scripts/mac/build.sh" --rhino-version "$version" "${arguments[@]}"
done
