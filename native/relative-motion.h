#pragma once
#include <stdint.h>
#include <stddef.h>

typedef struct {
    double timestamp, dx, dy;
} RcfRelativeMotion;

typedef void (*RcfRelativeMotionHandler)(const RcfRelativeMotion *motion);

_Static_assert(sizeof(RcfRelativeMotion) == 24, "Relative motion ABI must remain 24 bytes");
_Static_assert(offsetof(RcfRelativeMotion, timestamp) == 0 &&
    offsetof(RcfRelativeMotion, dx) == 8 && offsetof(RcfRelativeMotion, dy) == 16,
    "Relative motion field offsets must match the managed packet");
