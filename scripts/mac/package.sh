#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

package_options=true
description='Package an existing Mac build as ZIP and Yak. Does not install or publish.'

read_options "$@"
[ "$clean" = false ] || fail 'Run build.sh --clean before packaging.'
require_native_tools
read_output

[ -n "$yak" ] || fail 'Supply --yak /path/to/yak.'
yak=$(cd "$(dirname -- "$yak")" && printf '%s/%s' "$PWD" "$(basename -- "$yak")")
[ -x "$yak" ] || fail "Yak is not executable: $yak"
[ "$preflight" = false ] || exit 0

dotnet fsi "$root/tools/mac-build-record.fsx" -- check "$root" "$output" "$sdk_version"
library="$output/libRhinosCanFlyMac.dylib"
codesign --verify "$library"

case "$rhino_version" in
    8) xcrun --sdk macosx lipo "$library" -verify_arch arm64 x86_64 ;;
    9) xcrun --sdk macosx lipo "$library" -verify_arch arm64 ;;
esac

dotnet fsi "$root/tools/check-native-bridge.fsx" -- "$output/RhinosCanFly.rhp" "$library"
rhino_package=$(dotnet msbuild "$project" -nologo "${properties[@]}" -getProperty:RhinoCommonPackageVersion)

mkdir -p "$root/dist"
stage=$(mktemp -d "$root/dist/package-mac-rh$rhino_version.XXXXXX")

cleanup() {
    case "$stage" in
        "$root/dist/package-mac-rh$rhino_version".??????)
            rm -f -- "$stage.zip"
            rm -rf -- "$stage"
            ;;
    esac
}

trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

dotnet fsi "$root/tools/stage-mac-package.fsx" -- "$root" "$output" "$framework" "$rhino_package" "$stage"
version=$(sed -nE 's/^version:[[:space:]]*([^[:space:]]+)[[:space:]]*$/\1/p' "$stage/manifest.yml")
[ -n "$version" ] || fail 'Missing release version.'
zip_name="RhinosCanFly-$version-rh$rhino_version-mac.zip"

(
    cd "$stage"
    zip -q -r "$stage.zip" .
)

(
    cd "$stage"
    "$yak" build --platform mac
)

shopt -s nullglob
packages=("$stage"/*.yak)
[ "${#packages[@]}" -eq 1 ] || fail 'Expected one Mac Yak package.'

dotnet fsi "$root/tools/check-mac-package.fsx" -- release "$root" "$version" "$framework" "$rhino_package" "$stage.zip" "${packages[0]}"
dotnet fsi "$root/tools/mac-build-record.fsx" -- check "$root" "$output" "$sdk_version" "$stage"

cp "$stage.zip" "$root/dist/$zip_name"
cp "${packages[0]}" "$root/dist/$(basename -- "${packages[0]}")"
printf 'Manual ZIP: %s\nYak package: %s\n' "$root/dist/$zip_name" "$root/dist/$(basename -- "${packages[0]}")"
