#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

description='Checks pointer ordering and shutdown, then opens a test window for the monitor, tracking and Quartz/AppKit delivery. Real devices still need Rhino testing.'

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

"$native_compiler" -isysroot "$native_sdk_path" -fobjc-arc -fblocks -Wall -Wextra \
    -Werror=implicit-function-declaration -Werror=incompatible-pointer-types \
    -Werror=objc-method-access -Werror=unguarded-availability -mmacosx-version-min=14.0 \
    -framework AppKit -framework CoreGraphics -framework CoreFoundation -framework GameController \
    "$root/tools/manual/check-mac-pointer.m" -o "$stage/check-pointer"

"$stage/check-pointer"
