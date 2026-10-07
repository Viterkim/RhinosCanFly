#!/bin/bash

set -euo pipefail
source "$(dirname -- "$0")/common.sh"

case "$#:$*" in
    0:)
        check=false
        ;;
    1:--check)
        check=true
        ;;
    *)
        fail "Usage: bash scripts/mac/format.sh [--check]"
        ;;
esac

cd "$root"
require_sdk

dotnet tool restore --verbosity quiet

if [ "$check" = true ]; then
    dotnet fantomas --check src tools
else
    dotnet fantomas src tools
fi
