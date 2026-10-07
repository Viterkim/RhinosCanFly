#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

install_options=true
description='Build and install locally. Close Rhino first.'

read_options "$@"
require_mac

if [ "$preflight" = true ]; then
    require_native_tools
    arguments=(--rhino-version "$rhino_version" --configuration "$configuration" --preflight)

    if [ -n "$rhino_app" ]; then
        arguments+=(--rhino-app "$rhino_app")
    fi

    bash "$root/scripts/mac/install-plugin.sh" "${arguments[@]}"
    exit 0
fi

arguments=(--rhino-version "$rhino_version" --configuration "$configuration")
build_arguments=("${arguments[@]}")

if [ "$clean" = true ]; then
    build_arguments+=(--clean)
fi

bash "$root/scripts/mac/build.sh" "${build_arguments[@]}"

if [ -n "$rhino_app" ]; then
    arguments+=(--rhino-app "$rhino_app")
fi

if [ "$replace" = true ]; then
    arguments+=(--replace)
fi

if [ -n "$rollback_package" ]; then
    arguments+=(--rollback-package "$rollback_package")
fi

bash "$root/scripts/mac/install-plugin.sh" "${arguments[@]}"
