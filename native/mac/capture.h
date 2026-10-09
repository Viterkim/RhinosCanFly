#pragma once
#include <stdbool.h>
#include <stdint.h>

enum { RCF_CAPTURE_CONTROLS = 133, RCF_CAPTURE_BINDINGS = 32, RCF_CAPTURE_CAPACITY = 32768 };
enum { RCF_CAPTURE_MOVE = 1, RCF_CAPTURE_WHEEL = 2, RCF_CAPTURE_KEY = 3,
    RCF_CAPTURE_BUTTON = 4, RCF_CAPTURE_MODIFIER = 5, RCF_CAPTURE_BEGIN = 8, RCF_CAPTURE_END = 9 };
enum { RCF_OWNER_HOST, RCF_OWNER_NATIVE, RCF_OWNER_APPKIT };
enum { RCF_BRIDGE_ABI = 5 };
enum { RCF_END_KEEP, RCF_END_RESTORE, RCF_END_HELD_RELEASE, RCF_END_CANCEL_ENTRY };
enum { RCF_EVENT_REPAIRED = 1, RCF_EVENT_TERMINAL = 2 };
enum { RCF_SCROLL_BEGAN = 1, RCF_SCROLL_CHANGED = 2, RCF_SCROLL_ENDED = 4,
    RCF_SCROLL_CANCELLED = 8, RCF_SCROLL_MAY_BEGIN = 128 };
enum { RCF_MOMENTUM_BEGAN = 1, RCF_MOMENTUM_CHANGED = 2, RCF_MOMENTUM_ENDED = 3 };

typedef struct {
    uint32_t kind, code, down, repeated;
    uint64_t modifiers;
    double timestamp, dx, dy, wheel;
    uint32_t precise, inverted;
    void *window;
    double screen_x, screen_y;
    uint32_t buttons, content;
    uint64_t source_time, sequence, session;
    uint32_t routing, phase, momentum, reserved;
    uint32_t target_window;
    uint64_t navigation_modifiers;
    uint64_t press_id;
} RcfCaptureEvent;

typedef struct {
    uint32_t count, keys[16];
} RcfCaptureBinding;

typedef struct {
    uint64_t session;
    uint32_t window, exit_buttons, terminal_count, entry_buttons, command_count;
    uint8_t configured[RCF_CAPTURE_CONTROLS];
    uint8_t command_keys[RCF_CAPTURE_CONTROLS];
    uint8_t quarantine[RCF_CAPTURE_CONTROLS];
    uint8_t appkit_owned[RCF_CAPTURE_CONTROLS];
    RcfCaptureBinding terminal[RCF_CAPTURE_BINDINGS];
    RcfCaptureBinding command[RCF_CAPTURE_BINDINGS];
    uint32_t held_buttons, entry_code;
    uint64_t entry_source_time, entry_modifiers;
    uint32_t entry_mouse_button;
    uint64_t entry_mouse_press_id, entry_mouse_source_time;
} RcfCaptureConfig;

typedef struct {
    uint64_t cycle, source_time, press_id, retired_press_id, last_edge_source;
    uint32_t owner, release_debt, retired_owner;
    bool physical, logical, quarantine, accepted, repaired, observed, sampled;
    double release_candidate;
} RcfCaptureControl;

typedef struct {
    RcfCaptureConfig config;
    RcfCaptureControl controls[RCF_CAPTURE_CONTROLS];
    bool terminal_held[RCF_CAPTURE_BINDINGS];
    bool initial[RCF_CAPTURE_CONTROLS];
    bool initial_quarantine[RCF_CAPTURE_CONTROLS];
    bool active, wheel_owned, wheel_ended, wheel_preliminary, wheel_started;
    uint64_t sequence, read, write, discard_through, frontier_sequence, wheel_session;
    double frontier, last_receipt, started_at;
    uint32_t error, high_water, regressions;
    uint32_t terminal_reason;
    uint64_t failure_read, failure_write;
    double maximum_source_delay;
    RcfCaptureEvent failure, events[RCF_CAPTURE_CAPACITY];
} RcfCapture;

void rcf_capture_begin(RcfCapture *capture, const RcfCaptureConfig *config,
    const bool physical[RCF_CAPTURE_CONTROLS], double now);
bool rcf_capture_route(RcfCapture *capture, RcfCaptureEvent event, double now, bool permitted);
void rcf_capture_flags(RcfCapture *capture, uint64_t flags, uint64_t source, double now, bool permitted);
void rcf_capture_tick(RcfCapture *capture, double now);
void rcf_capture_recover(RcfCapture *capture, uint32_t code, bool held, double now);
void rcf_capture_revoke(RcfCapture *capture, uint32_t error);
void rcf_capture_finish(RcfCapture *capture, uint32_t reason, double now);
void rcf_capture_discard(RcfCapture *capture);
uint32_t rcf_capture_read(RcfCapture *capture, RcfCaptureEvent *destination, uint32_t capacity,
    double *through, uint64_t *sequence);
bool rcf_capture_binding(const RcfCapture *capture, const RcfCaptureBinding *binding);
