#!/bin/bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
description='Build and ad-hoc sign the native input bridge. Requires Xcode command-line tools.'
read_options "$@"
read_output
if [ "$preflight" = false ]; then rm -f -- "$output/.verified-build.json"; fi
require_native_tools
[ "$preflight" = false ] || exit 0
[ -f "$output/RhinosCanFly.rhp" ] || fail 'Build the managed plugin first (build-managed.sh).'
case "$rhino_version" in
    8) architectures=(-arch arm64 -arch x86_64) ;;
    9) architectures=(-arch arm64) ;;
esac
case "$configuration" in
    Release) optimization=(-O2) ;;
    Debug) optimization=(-O0 -g) ;;
esac
mkdir -p "$output"
stage=$(mktemp -d "$output/native.XXXXXX")
cleanup() {
    case "$stage" in "$output/native".??????) rm -rf -- "$stage" ;; esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
"$native_compiler" -isysroot "$native_sdk_path" -dynamiclib -fobjc-arc -fblocks -Wall -Wextra \
    -Werror=implicit-function-declaration -Werror=incompatible-pointer-types \
    -Werror=objc-method-access -Werror=unguarded-availability \
    "${architectures[@]}" "${optimization[@]}" -mmacosx-version-min=14.0 \
    -framework AppKit -framework CoreGraphics -framework GameController \
    -install_name @rpath/libRhinosCanFlyMac.dylib \
    "$root/native/mac/input.m" "$root/native/mac/raw.m" -o "$stage/libRhinosCanFlyMac.dylib"
codesign --force --sign - "$stage/libRhinosCanFlyMac.dylib"
codesign --verify "$stage/libRhinosCanFlyMac.dylib"
case "$rhino_version" in
    8) xcrun --sdk macosx lipo "$stage/libRhinosCanFlyMac.dylib" -verify_arch arm64 x86_64 ;;
    9) xcrun --sdk macosx lipo "$stage/libRhinosCanFlyMac.dylib" -verify_arch arm64 ;;
esac
dotnet fsi "$root/tools/check-native-bridge.fsx" -- "$output/RhinosCanFly.rhp" "$stage/libRhinosCanFlyMac.dylib"
mv -f -- "$stage/libRhinosCanFlyMac.dylib" "$output/libRhinosCanFlyMac.dylib"
