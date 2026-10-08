#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

install_options=true
description="Package and install with Rhino's Yak tool. Close Rhino first."

read_options "$@"
[ "$clean" = false ] || fail "Use build-and-install.sh --clean to rebuild before installing."

require_mac
require_bridge_host
read_output

if [ -z "$rhino_app" ]; then
    for candidate in "/Applications/Rhino $rhino_version.app" "$HOME/Applications/Rhino $rhino_version.app" /Applications/RhinoWIP.app "$HOME/Applications/RhinoWIP.app"; do
        if [ -d "$candidate" ]; then
            candidate_version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$candidate/Contents/Info.plist")

            case "$candidate_version" in
                "$rhino_version".*)
                    rhino_app=$candidate
                    break
                    ;;
            esac
        fi
    done
fi

[ -n "$rhino_app" ] && [ -d "$rhino_app" ] || fail "Rhino $rhino_version was not found. Supply --rhino-app."
rhino_app=$(cd "$rhino_app" && pwd -P)
app_version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$rhino_app/Contents/Info.plist")

case "$app_version" in
    "$rhino_version".*) ;;
    *) fail "That app is Rhino $app_version; expected Rhino $rhino_version." ;;
esac

rhino_package=$(dotnet msbuild "$project" -nologo "${properties[@]}" -getProperty:RhinoCommonPackageVersion)
IFS=. read -r minimum_major minimum_minor _ <<< "${rhino_package%%-*}"
app_minor=${app_version#*.}
app_minor=${app_minor%%.*}

case "$minimum_major:$minimum_minor:$app_minor" in
    *[!0-9:]*|::*|*::*) fail 'Could not read the Rhino compatibility baseline.' ;;
esac

[ "$minimum_major" = "$rhino_version" ] && [ -n "$minimum_minor" ] && [ -n "$app_minor" ] || fail 'Could not read the Rhino compatibility baseline.'
[ "$app_minor" -ge "$minimum_minor" ] || fail "This build requires Rhino $minimum_major.$minimum_minor or later; selected app is $app_version."

yak="$rhino_app/Contents/Resources/bin/yak"
[ -x "$yak" ] || fail "Yak was not found at $yak"

printf 'Rhino: %s (%s)\nRequired Rhino: %s.%s+ (RhinoCommon %s)\nPlugin target: %s\nRhino hosted runtime: unverified offline\nYak: %s\n' \
    "$app_version" "$rhino_app" "$minimum_major" "$minimum_minor" "$rhino_package" "$framework" "$yak"

codesign -d --entitlements :- "$rhino_app" 2>&1 || fail 'Could not inspect Rhino signing entitlements.'

if [ "$preflight" = true ]; then
    exit 0
fi
dotnet fsi "$root/tools/mac-build-record.fsx" -- check "$root" "$output" "$sdk_version"
processes=$(ps -axo comm=)

while IFS= read -r executable; do
    case "$executable" in
        "$rhino_app"/Contents/MacOS/*|*/Rhino*.app/Contents/MacOS/*)
            fail "Close Rhino before installing."
            ;;
    esac
done <<< "$processes"

library="$output/libRhinosCanFlyMac.dylib"
[ -f "$library" ] || fail "Missing native bridge. Run build.sh first."

case "$rhino_version" in
    8)
        xcrun --sdk macosx lipo "$library" -verify_arch arm64 x86_64
        ;;
    9)
        xcrun --sdk macosx lipo "$library" -verify_arch arm64
        ;;
esac

codesign --verify "$library"
dotnet fsi "$root/tools/check-native-bridge.fsx" -- "$output/RhinosCanFly.rhp" "$library"

package_root="$root/dist/mac"
mkdir -p "$package_root"
stage=$(mktemp -d "$package_root/stage-rh$rhino_version.XXXXXX")
stage=$(cd "$stage" && pwd -P)
replacement_pending=false
rollback=
previous_version=
desired_version=
replacement_phase=prepared
yak_listing() {
    local listing
    listing=$("$yak" --debug list 2>&1) || return 1

    package_folder=$(printf '%s\n' "$listing" | sed -n 's/^Package directory: //p')
    [ -n "$package_folder" ] && [ "${package_folder#/}" != "$package_folder" ] || return 1

    installed=$(printf '%s\n' "$listing" | sed '/^Package directory: /d')
}

installed_matches() {
    local archive=$1 directory="$package_folder/RhinosCanFly/$2"
    [ -d "$directory" ] && dotnet fsi "$root/tools/check-mac-package.fsx" -- installed "$archive" "$directory" >/dev/null 2>&1
}

cleanup() {
    local status=$? installed current_version listed=true
    trap - EXIT HUP INT TERM

    if [ "$replacement_pending" = true ]; then
        replacement_pending=false
        yak_listing || {
            installed=
            listed=false
        }

        current_version=$(printf '%s\n' "$installed" | sed -nE 's/^[[:space:]]*[Rr][Hh][Ii][Nn][Oo][Ss][Cc][Aa][Nn][Ff][Ll][Yy][[:space:]]*\(([^)]+)\)[[:space:]]*$/\1/p')

        if [ "$current_version" = "$desired_version" ] && installed_matches "$package" "$desired_version"; then
            printf 'New RhinosCanFly payload verified after %s. Recovery archive: %s\n' "$replacement_phase" "$rollback" >&2
        elif [ "$current_version" = "$previous_version" ] && installed_matches "$rollback" "$previous_version"; then
            printf 'Previous RhinosCanFly payload verified after %s. Recovery archive: %s\n' "$replacement_phase" "$rollback" >&2
        elif [ "$listed" = true ] &&
            ! printf '%s\n' "$installed" | grep -Ei '^[[:space:]]*rhinoscanfly[[:space:]]*\(' >/dev/null &&
            "$yak" install "$rollback" && installed_matches "$rollback" "$previous_version"; then
            printf 'Previous RhinosCanFly restored. Recovery archive: %s\n' "$rollback" >&2
        else
            printf 'Could not verify the installed payload after %s. Existing files were kept. Restore manually with Yak using: %s\n' "$replacement_phase" "$rollback" >&2
        fi

        [ "$status" -ne 0 ] || status=1
    fi

    case "$stage" in
        "$root/dist/mac/stage-rh$rhino_version".??????)
            rm -rf -- "$stage"
            ;;
    esac

    exit "$status"
}

trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
dotnet fsi "$root/tools/stage-mac-package.fsx" -- "$root" "$output" "$framework" "$rhino_package" "$stage"

(
    cd "$stage"
    "$yak" build --platform mac
)

shopt -s nullglob
packages=("$stage"/*.yak)
[ "${#packages[@]}" -eq 1 ] || fail "Expected one Yak package."
dotnet fsi "$root/tools/check-mac-package.fsx" -- archive "$stage" "${packages[0]}"

package_dir="$package_root/rh$rhino_version/$configuration"
mkdir -p "$package_dir"
package="$package_dir/$(basename -- "${packages[0]}")"

install_package() {
    local installed rollback_dir manifest retain_rollback=false

    dotnet fsi "$root/tools/mac-build-record.fsx" -- check "$root" "$output" "$sdk_version" "$stage"
    yak_listing || fail "Could not read Yak's installed package directory. Existing package is untouched."
    previous_version=$(printf '%s\n' "$installed" | sed -nE 's/^[[:space:]]*[Rr][Hh][Ii][Nn][Oo][Ss][Cc][Aa][Nn][Ff][Ll][Yy][[:space:]]*\(([^)]+)\)[[:space:]]*$/\1/p')
    rollback=

    if [ -n "$previous_version" ]; then
        [ "$replace" = true ] || fail "RhinosCanFly is already installed. Use --replace with --rollback-package."
        [ -f "$rollback_package" ] || fail "Keep the existing install: supply its Mac Yak archive with --rollback-package."

        case "$(basename -- "$rollback_package")" in
            rhinoscanfly-"$previous_version"-rh"$rhino_version"-mac.yak|rhinoscanfly-"$previous_version"-rh"$rhino_version"_*-mac.yak) ;;
            *) fail "Rollback archive must match installed version $previous_version and Rhino $rhino_version on Mac." ;;
        esac

        dotnet fsi "$root/tools/check-mac-package.fsx" -- recovery "$rollback_package" || fail "The rollback payload is incomplete. Existing package is untouched."
        unzip -tqq "$rollback_package" || fail "The rollback archive is damaged."
        manifest=$(unzip -p "$rollback_package" manifest.yml) || fail "The rollback manifest is missing."

        printf '%s\n' "$manifest" | grep -Ei '^name:[[:space:]]*rhinoscanfly[[:space:]]*$' >/dev/null || fail "Unexpected rollback package name."
        [ "$(printf '%s\n' "$manifest" | sed -nE 's/^version:[[:space:]]*([^[:space:]]+)[[:space:]]*$/\1/p')" = "$previous_version" ] || fail "Rollback manifest version does not match the installed package."

        unzip -l "$rollback_package" RhinosCanFly.rhp libRhinosCanFlyMac.dylib | grep 'RhinosCanFly.rhp' >/dev/null || fail "Rollback plugin is missing."
        unzip -l "$rollback_package" libRhinosCanFlyMac.dylib | grep 'libRhinosCanFlyMac.dylib' >/dev/null || fail "Rollback native bridge is missing."
        installed_matches "$rollback_package" "$previous_version" || fail "The rollback archive does not match the installed payload. Existing package is untouched."

        rollback_dir=$(mktemp -d "$package_dir/rollback.XXXXXX")
        rollback="$rollback_dir/$(basename -- "$rollback_package")"
        cp "$rollback_package" "$rollback"

        if [ "$rollback_package" -ef "$package" ]; then
            retain_rollback=true
        fi

        printf 'Yak: %s\nPrevious version: %s\nRecovery archive: %s\n' "$yak" "$previous_version" "$rollback" > "$rollback_dir/restore.txt"
    elif printf '%s\n' "$installed" | grep -Ei '^[[:space:]]*rhinoscanfly[[:space:]]*\(' >/dev/null; then
        fail "Could not determine the installed RhinosCanFly version. Existing package is untouched."
    fi

    cp "${packages[0]}" "$package"
    desired_version=$(sed -nE 's/^version:[[:space:]]*([^[:space:]]+)[[:space:]]*$/\1/p' "$stage/manifest.yml")
    [ -n "$desired_version" ] || fail "The package version is missing. Existing package is untouched."

    if [ -n "$rollback" ]; then
        printf 'Recovery archive: %s\n' "$rollback"
        replacement_pending=true
    fi

    replacement_phase=uninstalling
    if [ -z "$rollback" ] || "$yak" uninstall rhinoscanfly; then
        replacement_phase=installing

        if "$yak" install "$package" && installed_matches "$package" "$desired_version"; then
            replacement_phase=installed
            replacement_pending=false

            if [ -n "$rollback" ] && [ "$retain_rollback" = false ]; then
                rm -- "$rollback" "$rollback_dir/restore.txt"
                rmdir -- "$rollback_dir"
            fi

            return
        fi
    fi

    if [ -n "$rollback" ]; then
        fail "Replacement failed. Checking the installed package before rollback."
    fi

    fail "Installation failed: $package"
}

install_package

printf 'Installed: %s\nStart Rhino %s to load it.\n' "$package" "$rhino_version"
