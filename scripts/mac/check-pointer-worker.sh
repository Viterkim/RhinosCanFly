#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

description='Runs native routing and Quartz adapter checks, including capture while replay is blocked. Live process tap and device delivery need a permitted Mac and Rhino.'

read_options "$@"
require_native_tools
[ "$preflight" = false ] || exit 0

temporary_root=${TMPDIR:-/tmp}
temporary_root=${temporary_root%/}
stage=$(mktemp -d "$temporary_root/rcf-pointer.XXXXXX")

cleanup() {
    case "$stage" in
        "$temporary_root"/rcf-pointer.??????)
            rm -rf -- "$stage"
            ;;
    esac
}

trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

"$native_compiler" -isysroot "$native_sdk_path" -std=c11 -Wall -Wextra -Werror \
    -fsanitize=address,undefined "$root/tools/check-mac-capture.c" "$root/native/mac/capture.c" \
    -o "$stage/check-capture"

if [ -n "${RCF_BINDING_FIXTURE:-}" ]; then
    "$stage/check-capture" "$RCF_BINDING_FIXTURE" "$stage/mac-protocol.bin"

    if [ -n "${RCF_MANAGED_PLUGIN:-}" ] && [ -n "${RCF_MANAGED_LIBRARIES:-}" ]; then
        dotnet fsi --reference:"$RCF_MANAGED_LIBRARIES/RhinoCommon.dll" \
            --reference:"$RCF_MANAGED_LIBRARIES/Eto.dll" --reference:"$RCF_MANAGED_PLUGIN" \
            "$root/tools/manual/check-mac-protocol.fsx" -- "$stage/mac-protocol.bin"

        dotnet fsi --reference:"$RCF_MANAGED_LIBRARIES/RhinoCommon.dll" \
            --reference:"$RCF_MANAGED_LIBRARIES/Eto.dll" --reference:"$RCF_MANAGED_PLUGIN" \
            "$root/tools/manual/check-mac-protocol.fsx" -- --managed-smoke
    fi
else
    "$stage/check-capture"
fi

"$native_compiler" -isysroot "$native_sdk_path" -fobjc-arc -fblocks -Wall -Wextra \
    -Werror=implicit-function-declaration -Werror=incompatible-pointer-types \
    -Werror=objc-method-access -Werror=unguarded-availability -mmacosx-version-min=14.0 \
    -framework AppKit -framework CoreGraphics -framework CoreFoundation -framework GameController -framework ApplicationServices \
    "$root/tools/manual/check-mac-pointer.m" "$root/native/mac/capture.c" -o "$stage/check-pointer"

result=0
"$stage/check-pointer" --live || result=$?

if [ "$result" -ne 0 ] && [ "$result" -ne 78 ]; then
    exit "$result"
fi
