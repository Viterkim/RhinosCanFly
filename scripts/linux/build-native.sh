#!/bin/sh

set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
configuration="${1:-Release}"
case "$configuration" in
    Release|Debug) ;;
    *)
        echo "Use Release or Debug" >&2
        exit 1
        ;;
esac

generated="$root/obj/linux/protocols"
output="$root/bin/linux/$configuration/net10.0"
protocols=$(pkg-config --variable=pkgdatadir wayland-protocols)
compile_flags=$(pkg-config --cflags wayland-client)
link_flags=$(pkg-config --libs wayland-client)

mkdir -p "$generated" "$output"

for protocol in relative-pointer pointer-constraints; do
    xml="$protocols/unstable/$protocol/$protocol-unstable-v1.xml"
    wayland-scanner client-header "$xml" "$generated/$protocol-unstable-v1-client-protocol.h"
    wayland-scanner private-code "$xml" "$generated/$protocol-unstable-v1-protocol.c"
done

case "$configuration" in
    Release)
        set -- -O2
        ;;
    Debug)
        set -- -O0 -g
        ;;
esac

# pkg-config supplies compiler/linker arguments, intentionally word-split.
${CC:-cc} -std=c11 -shared -fPIC -Wall -Wextra -Werror "$@" \
    $compile_flags -I"$generated" \
    "$root/native/linux/input.c" \
    "$generated/relative-pointer-unstable-v1-protocol.c" \
    "$generated/pointer-constraints-unstable-v1-protocol.c" \
    $link_flags -o "$output/libRhinosCanFlyWayland.so"
