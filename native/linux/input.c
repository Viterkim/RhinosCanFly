#include <stdlib.h>
#include <wayland-client.h>
#include "relative-pointer-unstable-v1-client-protocol.h"
#include "pointer-constraints-unstable-v1-client-protocol.h"
#include "input.h"

struct RcfWaylandSession {
    struct zwp_relative_pointer_v1 *relative;
    struct zwp_locked_pointer_v1 *lock;
    RcfRelativeMotionHandler motion;
    RcfWaylandStateHandler state;
    int locked;
};

static void relative_motion(void *data, struct zwp_relative_pointer_v1 *pointer,
    uint32_t hi, uint32_t lo, wl_fixed_t dx, wl_fixed_t dy,
    wl_fixed_t dx_unaccel, wl_fixed_t dy_unaccel) {
    (void)pointer; (void)dx; (void)dy;
    struct RcfWaylandSession *session = data;
    if (!session->locked) return;
    RcfRelativeMotion motion = {
        .timestamp = (double)(((uint64_t)hi << 32) | lo) / 1000000.0,
        .dx = wl_fixed_to_double(dx_unaccel),
        .dy = wl_fixed_to_double(dy_unaccel)
    };
    session->motion(&motion);
}

static void locked(void *data, struct zwp_locked_pointer_v1 *pointer) {
    (void)pointer;
    struct RcfWaylandSession *session = data;
    session->locked = 1;
    session->state(1);
}

static void unlocked(void *data, struct zwp_locked_pointer_v1 *pointer) {
    (void)pointer;
    struct RcfWaylandSession *session = data;
    session->locked = 0;
    session->state(2);
}

static const struct zwp_relative_pointer_v1_listener relative_listener = { relative_motion };
static const struct zwp_locked_pointer_v1_listener lock_listener = { locked, unlocked };

uint32_t rcf_wayland_abi(void) { return 1; }
uint32_t rcf_wayland_motion_size(void) { return (uint32_t)sizeof(RcfRelativeMotion); }

void rcf_wayland_end(struct RcfWaylandSession *session) {
    if (!session) return;
    session->locked = 0;
    if (session->lock) zwp_locked_pointer_v1_destroy(session->lock);
    if (session->relative) zwp_relative_pointer_v1_destroy(session->relative);
    free(session);
}

struct RcfWaylandSession *rcf_wayland_begin(
    struct zwp_relative_pointer_manager_v1 *relative_manager,
    struct zwp_pointer_constraints_v1 *constraints,
    struct wl_pointer *pointer, struct wl_surface *surface, struct wl_event_queue *queue,
    RcfRelativeMotionHandler motion, RcfWaylandStateHandler state) {
    if (!relative_manager || !constraints || !pointer || !surface || !motion || !state) return NULL;
    struct RcfWaylandSession *session = calloc(1, sizeof(*session));
    if (!session) return NULL;
    session->motion = motion;
    session->state = state;
    struct zwp_relative_pointer_manager_v1 *relative_wrapper = wl_proxy_create_wrapper(relative_manager);
    struct zwp_pointer_constraints_v1 *constraints_wrapper = wl_proxy_create_wrapper(constraints);
    if (!relative_wrapper || !constraints_wrapper) {
        if (relative_wrapper) wl_proxy_wrapper_destroy(relative_wrapper);
        if (constraints_wrapper) wl_proxy_wrapper_destroy(constraints_wrapper);
        goto failed;
    }
    // Wrappers select the host's dispatch queue without modifying borrowed proxies.
    wl_proxy_set_queue((struct wl_proxy *)relative_wrapper, queue);
    wl_proxy_set_queue((struct wl_proxy *)constraints_wrapper, queue);
    session->relative = zwp_relative_pointer_manager_v1_get_relative_pointer(relative_wrapper, pointer);
    session->lock = zwp_pointer_constraints_v1_lock_pointer(
        constraints_wrapper, surface, pointer, NULL, ZWP_POINTER_CONSTRAINTS_V1_LIFETIME_ONESHOT);
    wl_proxy_wrapper_destroy(relative_wrapper);
    wl_proxy_wrapper_destroy(constraints_wrapper);
    if (!session->relative) goto failed;
    if (zwp_relative_pointer_v1_add_listener(session->relative, &relative_listener, session) < 0) goto failed;
    if (!session->lock) goto failed;
    if (zwp_locked_pointer_v1_add_listener(session->lock, &lock_listener, session) < 0) goto failed;
    return session;
failed:
    rcf_wayland_end(session);
    return NULL;
}
