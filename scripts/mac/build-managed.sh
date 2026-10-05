#!/bin/bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
description='Build the managed Mac backend. This also works on Windows/Linux with the required SDK.'
read_options "$@"
read_output
[ "$preflight" = false ] || { printf 'Managed target: Rhino %s, %s\nOutput: %s\n' "$rhino_version" "$framework" "$output"; exit 0; }
rm -f -- "$output/.verified-build.json"
bash "$root/scripts/mac/format.sh" --check
dotnet fsi tools/check-source-style.fsx -- tools
if [ "$clean" = true ]; then
    dotnet restore "$project" "${properties[@]}"
    dotnet clean "$project" --configuration "$configuration" "${properties[@]}"
fi
dotnet build "$project" --configuration "$configuration" "${properties[@]}"
