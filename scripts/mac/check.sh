#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

[ "$#" -eq 0 ] || fail "Usage: bash scripts/mac/check.sh"

cd "$root"
require_sdk

dotnet fsi tools/check-all.fsx -- src
dotnet fsi tools/check-source-style.fsx -- tools

printf 'Source checks passed.\n'
