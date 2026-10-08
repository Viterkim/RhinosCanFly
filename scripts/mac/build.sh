#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

description='Build the managed plugin and native input bridge on Mac.'

read_options "$@"
read_output

if [ "$preflight" = false ]; then
    rm -f -- "$output/.verified-build.json"
fi

require_native_tools
[ "$preflight" = false ] || exit 0

build_inputs=$(dotnet fsi "$root/tools/mac-build-record.fsx" -- inputs "$root")
arguments=(--rhino-version "$rhino_version" --configuration "$configuration")

if [ "$clean" = true ]; then
    arguments+=(--clean)
fi

bash "$root/scripts/mac/build-managed.sh" "${arguments[@]}"
bash "$root/scripts/mac/build-native.sh" "${arguments[@]}"

read_output
dotnet fsi "$root/tools/check-native-bridge.fsx" -- "$output/RhinosCanFly.rhp" "$output/libRhinosCanFlyMac.dylib"
dotnet fsi "$root/tools/mac-build-record.fsx" -- write "$root" "$output" "$sdk_version" "$build_inputs"
