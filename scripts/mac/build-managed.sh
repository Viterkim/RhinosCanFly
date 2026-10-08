#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

description='Build the managed Mac plugin. Works on Windows/Linux too.'

read_options "$@"
read_output

if [ "$preflight" = true ]; then
    printf 'Managed target: Rhino %s, %s\nOutput: %s\n' "$rhino_version" "$framework" "$output"
    exit 0
fi

rm -f -- "$output/.verified-build.json"

bash "$root/scripts/mac/format.sh" --check
dotnet fsi tools/check-source-style.fsx -- tools

if [ "$clean" = true ]; then
    dotnet restore "$project" "${properties[@]}"
    dotnet clean "$project" --configuration "$configuration" "${properties[@]}"
fi

dotnet build "$project" --configuration "$configuration" "${properties[@]}"
