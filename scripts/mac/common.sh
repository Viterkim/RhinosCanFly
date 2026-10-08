#!/bin/bash

root=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
project="$root/RhinosCanFly.fsproj"

configuration=Release
rhino_version="${RCF_RHINO_VERSION:-}"
clean=false
rhino_app=
yak=
replace=false
rollback_package=
preflight=false

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

read_options() {
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --configuration|--rhino-version|--rhino-app|--rollback-package|--yak)
                [ "$#" -ge 2 ] && [ -n "$2" ] || fail "Missing value for $1"

                case "$1" in
                    --configuration)
                        configuration=$2
                        ;;
                    --rhino-version)
                        rhino_version=$2
                        ;;
                    --rhino-app)
                        [ "${install_options:-false}" = true ] || fail "Unknown option: $1"
                        rhino_app=$2
                        ;;
                    --yak)
                        [ "${package_options:-false}" = true ] || fail "Unknown option: $1"
                        yak=$2
                        ;;
                    --rollback-package)
                        [ "${install_options:-false}" = true ] || fail "Unknown option: $1"
                        rollback_package=$2

                        case "$rollback_package" in
                            /*) ;;
                            *) rollback_package="$PWD/$rollback_package" ;;
                        esac
                        ;;
                esac

                shift 2
                ;;
            --clean)
                clean=true
                shift
                ;;
            --preflight)
                preflight=true
                shift
                ;;
            --replace)
                [ "${install_options:-false}" = true ] || fail "Unknown option: $1"
                replace=true
                shift
                ;;
            --help|-h)
                printf 'Usage: bash scripts/mac/%s [--rhino-version 8|9] [--configuration Release|Debug] [--clean] [--preflight]\n' \
                    "$(basename -- "$0")"

                if [ "${install_options:-false}" = true ]; then
                    printf '       [--rhino-app "/Applications/Rhino 8.app"] [--replace --rollback-package previous.yak]\n'
                fi

                if [ "${package_options:-false}" = true ]; then
                    printf '       --yak /path/to/yak\n'
                fi

                printf '%s\n' "${description:-}"
                exit 0
                ;;
            *)
                fail "Unknown option: $1 (use --help)"
                ;;
        esac
    done

    case "$configuration" in
        Release|Debug) ;;
        *) fail "Use Release or Debug" ;;
    esac

    require_sdk

    if [ -z "$rhino_version" ]; then
        rhino_version=$(dotnet msbuild "$project" -nologo -getProperty:RhinoDefaultVersion -p:RhinosCanFlyPlatform=mac)
    fi

    case "$rhino_version" in
        8|9) ;;
        *) fail "Use Rhino version 8 or 9" ;;
    esac

    properties=(
        -p:RhinosCanFlyPlatform=mac
        "-p:RhinoMajorVersion=$rhino_version"
        "-p:Configuration=$configuration"
    )
}

read_output() {
    framework=$(dotnet msbuild "$project" -nologo "${properties[@]}" -getProperty:TargetFramework)
    output="$root/bin/mac/rh$rhino_version/$configuration/$framework"
}

require_mac() {
    [ "$(uname -s)" = Darwin ] || fail "This step needs macOS. Use build-managed.sh for a managed-only build."

    local host_version
    host_version=$(sw_vers -productVersion)

    [ "${host_version%%.*}" -ge 14 ] || fail 'The native input bridge requires macOS 14 or later.'
    printf 'macOS: %s\n' "$host_version"
}

require_sdk() {
    command -v dotnet >/dev/null || fail "Install the .NET SDK specified in $root/global.json."

    cd "$root"
    sdk_version=$(dotnet --version) || fail "The SDK selected by $root/global.json is unavailable."
    sdk_architecture=$(dotnet fsi "$root/tools/check-native-bridge.fsx" -- host) \
        || fail 'Could not inspect the selected .NET process architecture.'

    printf '.NET SDK: %s (%s)\n.NET process: %s\nHost: %s %s\n' \
        "$sdk_version" "$(command -v dotnet)" "$sdk_architecture" "$(uname -s)" "$(uname -m)"
}

require_bridge_host() {
    case "$rhino_version:$sdk_architecture" in
        8:Arm64|8:X64|9:Arm64) ;;
        *)
            fail "Rhino $rhino_version bridge checks cannot use the selected $sdk_architecture .NET process. Use an arm64 SDK for Rhino 9, or arm64/x64 for Rhino 8."
            ;;
    esac
}

require_native_tools() {
    require_mac
    require_bridge_host

    command -v xcrun >/dev/null || fail 'Install Xcode command-line tools (xcode-select --install).'
    command -v codesign >/dev/null || fail 'codesign is unavailable.'

    native_compiler=$(xcrun --sdk macosx --find clang) || fail 'Xcode clang is unavailable.'
    native_sdk_path=$(xcrun --sdk macosx --show-sdk-path) || fail 'The Apple macOS SDK is unavailable.'
    native_sdk_version=$(xcrun --sdk macosx --show-sdk-version) || fail 'The Apple macOS SDK version is unavailable.'

    [ -x "$native_compiler" ] && [ -d "$native_sdk_path" ] || fail 'The resolved macOS compiler or SDK is missing.'

    printf 'Clang: %s\nmacOS SDK: %s (%s)\n' "$native_compiler" "$native_sdk_path" "$native_sdk_version"
    "$native_compiler" --version
}
