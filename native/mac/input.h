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
int32_t rcf_mac_raw_begin(RcfRelativeMotionHandler handler);
int32_t rcf_mac_raw_end(void);
uint32_t rcf_mac_raw_available(void);
uint32_t rcf_mac_raw_validate(void);

#ifdef __OBJC__
@class NSEvent;
void rcf_mac_raw_motion(NSEvent *event);
#endif
