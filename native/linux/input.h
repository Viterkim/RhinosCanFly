#pragma once
#include "../relative-motion.h"

struct zwp_relative_pointer_manager_v1;
struct zwp_pointer_constraints_v1;
struct wl_pointer;
struct wl_surface;
struct wl_event_queue;
struct RcfWaylandSession;

typedef void (*RcfWaylandStateHandler)(uint32_t state);

uint32_t rcf_wayland_abi(void);
uint32_t rcf_wayland_motion_size(void);
struct RcfWaylandSession *rcf_wayland_begin(
    struct zwp_relative_pointer_manager_v1 *relative_manager,
    struct zwp_pointer_constraints_v1 *constraints,
    struct wl_pointer *pointer, struct wl_surface *surface, struct wl_event_queue *queue,
    RcfRelativeMotionHandler motion, RcfWaylandStateHandler state);
void rcf_wayland_end(struct RcfWaylandSession *session);
