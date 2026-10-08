#pragma once
#include <stdint.h>
#include "../relative-motion.h"

typedef struct {
    uint32_t kind, code, down, repeated;
    uint64_t modifiers;
    double timestamp, dx, dy, wheel;
    uint32_t precise, inverted;
    void *window;
    double screen_x, screen_y;
    uint32_t buttons, content;
} RcfMacEvent;

typedef uint32_t (*RcfMacHandler)(const RcfMacEvent *event);
typedef void (*RcfMacNotify)(void);

enum { RCF_RAW_AUTO, RCF_RAW_GCMOUSE, RCF_RAW_POINTER, RCF_RAW_WORKER };
enum { RCF_RAW_STOPPED, RCF_RAW_RUNNING, RCF_RAW_STOPPING, RCF_RAW_STARTING, RCF_RAW_FAILED };
enum { RCF_INPUT_EMPTY, RCF_INPUT_ELIGIBLE, RCF_INPUT_BOUNDARY, RCF_INPUT_FAILED };

uint32_t rcf_mac_event_size(void);
uint32_t rcf_mac_motion_size(void);
uint32_t rcf_mac_abi(void);
double rcf_mac_uptime(void);
double rcf_mac_keyboard_boundary(void);
uint32_t rcf_mac_capture_key(void);
uint32_t rcf_mac_capture_button(void);
void *rcf_mac_foreground_window(void);
void *rcf_mac_view_window(uint32_t view_serial_number);
void *rcf_mac_window_from_handle(void *handle);
// Result 5 retains partial acquisition; retry monitor_end before another begin.
int32_t rcf_mac_monitor_begin(RcfMacHandler handler, void *expected_window);
int32_t rcf_mac_monitor_end(void);
int32_t rcf_mac_monitor_window(void *expected_window);
int32_t rcf_mac_raw_begin(RcfRelativeMotionHandler handler, RcfMacNotify notify, uint32_t backend);
// 0: complete, -1: still stopping, positive: cleanup failed, roots must stay alive.
int32_t rcf_mac_raw_end(void);
uint32_t rcf_mac_raw_available(void);
uint32_t rcf_mac_raw_validate(void);
uint32_t rcf_mac_raw_state(void);
uint32_t rcf_mac_raw_drain(void);
uint32_t rcf_mac_raw_pending(void);
double rcf_mac_raw_boundary(void);
void rcf_mac_raw_discard(void);
void rcf_mac_raw_reconcile(uint32_t code);
uint32_t rcf_mac_raw_initial_key(uint32_t code);
uint32_t rcf_mac_raw_error(void);
double rcf_mac_raw_started_at(void);

#ifdef __OBJC__
@class NSEvent;
void rcf_mac_raw_motion(NSEvent *event);
// -1: ordering failure, 0: accepted, 1: acknowledge but discard wheel payload.
int32_t rcf_mac_raw_before_event(NSEvent *event);
void rcf_mac_raw_release(uint32_t code, double timestamp);
#endif
