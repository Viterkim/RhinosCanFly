#define _CRT_SECURE_NO_WARNINGS
#include <assert.h>
#include <math.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../native/mac/capture.h"

_Static_assert(sizeof(RcfCaptureEvent) == 160, "capture event ABI");
_Static_assert(sizeof(RcfCaptureConfig) == 4960, "capture configuration ABI");
_Static_assert(offsetof(RcfCaptureConfig, configured) == 28, "capture key layout");
_Static_assert(offsetof(RcfCaptureConfig, terminal) == 560, "capture binding layout");
_Static_assert(offsetof(RcfCaptureConfig, command) == 2736, "capture Command layout");
_Static_assert(offsetof(RcfCaptureConfig, held_buttons) == 4912, "held entry layout");
_Static_assert(offsetof(RcfCaptureConfig, entry_source_time) == 4920, "entry provenance layout");

static RcfCapture *capture;
static RcfCaptureConfig config;
static bool held[RCF_CAPTURE_CONTROLS];

static void reset(void) {
    memset(capture, 0, sizeof(*capture));
    memset(&config, 0, sizeof(config));
    memset(held, 0, sizeof(held));
    config.session = 1;
    config.entry_mouse_button = UINT32_MAX;
    config.configured[13] = config.configured[14] = config.configured[53] = 1;
    config.configured[56] = config.configured[58] = 1;
    config.configured[60] = 1;
    config.configured[128] = config.configured[129] = 1;
    config.terminal_count = 1;
    config.terminal[0].count = 1;
    config.terminal[0].keys[0] = 53;
}

static bool edge(uint32_t kind, uint32_t code, bool down, bool repeated, uint64_t flags, uint64_t source, double time) {
    RcfCaptureEvent event = {0};
    event.kind = kind;
    event.code = code;
    event.down = down;
    event.repeated = repeated;
    event.modifiers = flags;
    event.source_time = source;
    return rcf_capture_route(capture, event, time, true);
}

static bool mouse_edge(uint32_t code, uint64_t id, bool down, double time) {
    RcfCaptureEvent event = {0};
    event.kind = RCF_CAPTURE_BUTTON;
    event.code = code;
    event.down = down;
    event.press_id = id;
    return rcf_capture_route(capture, event, time, true);
}

static void shortcut(void) {
    reset();
    held[14] = held[56] = held[58] = true;
    config.quarantine[14] = config.quarantine[56] = config.quarantine[58] = 1;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!capture->initial[14] && !capture->initial[56] && !capture->initial[58]);

    assert(edge(RCF_CAPTURE_KEY, 14, true, true, 0, 10, 1.01));
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 11, 1.02));
    assert(capture->controls[13].logical);
    assert(!edge(RCF_CAPTURE_KEY, 14, false, false, 0, 12, 1.03));
    assert(!edge(RCF_CAPTURE_MODIFIER, 56, false, false, 0, 13, 1.04));
    assert(!edge(RCF_CAPTURE_MODIFIER, 58, false, false, 0, 14, 1.05));
    assert(edge(RCF_CAPTURE_KEY, 14, true, false, 0, 15, 1.06));
    assert(capture->controls[14].logical);
}

static void ownership(void) {
    reset();
    held[13] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(capture->initial[13]);
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 1, 1.01));
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 2, 1.02));
    rcf_capture_revoke(capture, 0);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 3, 1.03));
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 4, 1.04));
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 5, 1.05));

    held[13] = false;
    config.session = 2;
    rcf_capture_begin(capture, &config, held, 2);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 20, 2.01));
    rcf_capture_revoke(capture, 0);
    held[13] = true;
    config.session = 3;
    rcf_capture_begin(capture, &config, held, 3);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 21, 3.01));

    reset();
    held[128] = true;
    config.appkit_owned[128] = 1;
    config.entry_mouse_button = 0;
    config.entry_mouse_press_id = 10;
    config.exit_buttons = config.entry_buttons = 1;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!mouse_edge(0, 10, false, 1.01));
    assert(capture->active);
    assert(mouse_edge(0, 11, true, 1.02));
    assert(mouse_edge(0, 11, false, 1.03));
    assert(!capture->active);

    assert(!mouse_edge(0, 12, true, 1.9));
    config.entry_mouse_press_id = 12;
    rcf_capture_begin(capture, &config, held, 2);
    rcf_capture_recover(capture, 128, false, 2.01);
    rcf_capture_recover(capture, 128, false, 2.07);
    assert(capture->active);
    assert(capture->controls[128].owner == RCF_OWNER_APPKIT);
}

static void modifiers_and_commands(void) {
    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(!edge(RCF_CAPTURE_MODIFIER, 56, true, false, 1ull << 17, 1, 1.01));
    assert(capture->controls[56].logical);
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 1ull << 20, 2, 1.02));
    assert(!capture->controls[13].logical);
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 1ull << 20, 3, 1.03));

    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 4, 1.04));
    assert(!edge(RCF_CAPTURE_MODIFIER, 55, true, false, 1ull << 20, 5, 1.05));
    assert(!capture->controls[13].logical);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 1ull << 20, 6, 1.06));

    config.command_keys[13] = 1;
    config.command_count = 1;
    config.command[0].count = 2;
    config.command[0].keys[0] = 136;
    config.command[0].keys[1] = 13;
    held[55] = true;
    rcf_capture_begin(capture, &config, held, 2);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 1ull << 20, 4, 2.01));
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 1ull << 20, 5, 2.02));

    config.command[0].count = 3;
    config.command[0].keys[2] = 133;
    held[13] = held[56] = false;
    rcf_capture_begin(capture, &config, held, 3);
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 1ull << 20, 7, 3.01));
}

static void recovery_and_source_order(void) {
    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 100, 1.01));
    rcf_capture_recover(capture, 13, false, 1.02);
    rcf_capture_recover(capture, 13, false, 1.08);
    assert(!capture->controls[13].logical);
    assert(capture->controls[13].owner == RCF_OWNER_NATIVE);
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 120, 1.10));
    assert(capture->error == 13 && !capture->active);
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 130, 1.12));
    assert(capture->controls[13].owner == RCF_OWNER_HOST);
    assert(!capture->controls[13].logical);

    for (uint64_t cycle = 0; cycle < 8; ++cycle) {
        assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 140 + cycle * 2, 1.2 + cycle * 0.02));
        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 141 + cycle * 2, 1.21 + cycle * 0.02));
        assert(!capture->controls[13].release_debt && !capture->controls[13].physical);
    }

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 100, 1.01));
    rcf_capture_revoke(capture, 0);

    for (uint64_t cycle = 0; cycle < 8; ++cycle) {
        assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 110 + cycle * 2, 1.1 + cycle * 0.02));
        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 111 + cycle * 2, 1.11 + cycle * 0.02));
        assert(capture->controls[13].owner == RCF_OWNER_HOST && !capture->controls[13].release_debt);
    }

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 120, 1.10));
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 115, 1.11));
    assert(!capture->controls[13].logical && !capture->error);

    RcfCaptureEvent event = {0};
    event.kind = RCF_CAPTURE_MOVE;
    event.source_time = 90;
    event.dx = 2;
    assert(rcf_capture_route(capture, event, 1.13, true));
    assert(capture->events[(capture->write - 1) % RCF_CAPTURE_CAPACITY].timestamp == 1.13);
}

static void missed_modifier_notifications(void) {
    reset();
    held[56] = true;
    config.quarantine[56] = 1;
    config.configured[57] = 1;
    rcf_capture_begin(capture, &config, held, 1);
    rcf_capture_flags(capture, (1ull << 17) | 2, 1, 1.01, true);
    assert(!capture->controls[56].logical && capture->controls[56].quarantine);
    rcf_capture_flags(capture, 0, 2, 1.02, true);
    assert(!capture->controls[56].quarantine);
    rcf_capture_flags(capture, (1ull << 17) | 4, 3, 1.03, true);
    assert(capture->controls[60].logical && !capture->controls[56].logical);
    rcf_capture_flags(capture, 1ull << 17, 4, 1.04, true);
    assert(capture->controls[60].logical && !capture->controls[56].logical);

    rcf_capture_flags(capture, 1ull << 16, 5, 1.05, true);
    assert(capture->controls[57].logical);
    rcf_capture_flags(capture, 0, 6, 1.06, true);
    assert(!capture->controls[57].logical);

    config.command_keys[13] = config.command_keys[55] = 1;
    config.configured[55] = 1;
    config.command_count = 1;
    config.command[0].count = 2;
    config.command[0].keys[0] = 136;
    config.command[0].keys[1] = 13;
    memset(held, 0, sizeof(held));
    rcf_capture_begin(capture, &config, held, 2);
    rcf_capture_flags(capture, (1ull << 20) | 8, 7, 2.01, true);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, (1ull << 20) | 8, 8, 2.02));
    assert(rcf_capture_binding(capture, &config.command[0]));
}

static void wheel_gestures(void) {
    reset();
    RcfCaptureEvent wheel = {0};
    wheel.kind = RCF_CAPTURE_WHEEL;
    wheel.phase = 1;
    assert(!rcf_capture_route(capture, wheel, 0.9, false));
    rcf_capture_begin(capture, &config, held, 1);
    wheel.phase = 2;
    assert(!rcf_capture_route(capture, wheel, 1.01, true));
    assert(!capture->active && capture->error == 14);
    rcf_capture_begin(capture, &config, held, 1.015);
    wheel.phase = 1;
    wheel.wheel = 0.25;
    assert(rcf_capture_route(capture, wheel, 1.02, true));
    wheel.phase = 4;
    assert(rcf_capture_route(capture, wheel, 1.03, true));
    rcf_capture_revoke(capture, 0);
    wheel.phase = 0;
    wheel.momentum = 1;
    assert(rcf_capture_route(capture, wheel, 1.04, false));
    wheel.momentum = 2;
    assert(rcf_capture_route(capture, wheel, 1.05, false));
    config.session = 2;
    rcf_capture_begin(capture, &config, held, 1.055);
    uint64_t before = capture->write;
    assert(rcf_capture_route(capture, wheel, 1.056, true));
    assert(capture->write == before);
    wheel.momentum = 3;
    assert(rcf_capture_route(capture, wheel, 1.06, false));
    assert(!capture->wheel_owned);
}

static void preliminary_wheel(void) {
    const uint32_t endings[] = {RCF_SCROLL_ENDED, RCF_SCROLL_CANCELLED};

    for (unsigned ending = 0; ending < 2; ++ending) {
        for (unsigned scrolling = 0; scrolling < 2; ++scrolling) {
            for (unsigned exit_early = 0; exit_early < 2; ++exit_early) {
                reset();
                rcf_capture_begin(capture, &config, held, 1);
                RcfCaptureEvent wheel = {0};
                wheel.kind = RCF_CAPTURE_WHEEL;
                wheel.phase = RCF_SCROLL_MAY_BEGIN;
                assert(rcf_capture_route(capture, wheel, 1.01, true));
                if (exit_early) rcf_capture_revoke(capture, 0);
                assert(rcf_capture_route(capture, wheel, 1.02, !exit_early));

                if (scrolling) {
                    wheel.phase = RCF_SCROLL_BEGAN;
                    assert(rcf_capture_route(capture, wheel, 1.03, !exit_early));
                    wheel.phase = RCF_SCROLL_CHANGED;
                    assert(rcf_capture_route(capture, wheel, 1.04, !exit_early));
                }

                wheel.phase = endings[ending];
                assert(rcf_capture_route(capture, wheel, 1.05, !exit_early));
                assert(!capture->error && !capture->wheel_preliminary);
                assert(capture->wheel_ended == (scrolling && ending == 0));

                if (capture->wheel_ended) {
                    wheel.phase = 0;
                    wheel.momentum = RCF_MOMENTUM_BEGAN;
                    assert(rcf_capture_route(capture, wheel, 1.06, !exit_early));
                    wheel.momentum = RCF_MOMENTUM_ENDED;
                    assert(rcf_capture_route(capture, wheel, 1.07, !exit_early));
                    assert(!capture->wheel_owned && !capture->wheel_ended);
                }
            }
        }
    }

    for (unsigned scrolling = 0; scrolling < 3; ++scrolling) {
        reset();
        RcfCaptureEvent wheel = {0};
        wheel.kind = RCF_CAPTURE_WHEEL;
        wheel.phase = RCF_SCROLL_MAY_BEGIN;
        assert(!rcf_capture_route(capture, wheel, 0.9, false));
        rcf_capture_begin(capture, &config, held, 1);
        wheel.phase = scrolling == 0 ? RCF_SCROLL_CANCELLED :
            (scrolling == 1 ? RCF_SCROLL_ENDED : RCF_SCROLL_BEGAN);
        assert(rcf_capture_route(capture, wheel, 1.01, true) == (scrolling == 2));
        assert(capture->active && !capture->error);
    }

    reset();
    RcfCaptureEvent wheel = {0};
    wheel.kind = RCF_CAPTURE_WHEEL;
    wheel.phase = RCF_SCROLL_MAY_BEGIN;
    assert(!rcf_capture_route(capture, wheel, 0.9, false));
    rcf_capture_begin(capture, &config, held, 1);
    wheel.phase = RCF_SCROLL_CHANGED;
    assert(!rcf_capture_route(capture, wheel, 1.01, true));
    assert(!capture->active && capture->error == 14);

    reset();
    wheel.phase = RCF_SCROLL_BEGAN;
    assert(!rcf_capture_route(capture, wheel, 0.9, false));
    rcf_capture_begin(capture, &config, held, 1);
    wheel.phase = RCF_SCROLL_MAY_BEGIN;
    assert(rcf_capture_route(capture, wheel, 1.01, true));
    wheel.phase = RCF_SCROLL_BEGAN;
    assert(rcf_capture_route(capture, wheel, 1.02, true));
    assert(capture->active && !capture->error);
}

static void startup_provenance(void) {
    reset();
    held[13] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(capture->initial[13] && capture->controls[13].sampled);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 1.01));
    assert(!capture->error && !capture->controls[13].sampled && capture->controls[13].observed);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 1.02));
    assert(!capture->controls[13].logical && capture->controls[13].owner == RCF_OWNER_HOST);

    reset();
    held[14] = true;
    config.quarantine[14] = 1;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!edge(RCF_CAPTURE_KEY, 14, true, false, 0, 1, 1.01));
    assert(!edge(RCF_CAPTURE_KEY, 14, false, false, 0, 2, 1.02));
    assert(!capture->error && !capture->controls[14].quarantine);

    reset();
    held[53] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 1, 1.01));
    assert(capture->active && !capture->error);
    assert(edge(RCF_CAPTURE_KEY, 53, false, false, 0, 2, 1.02));
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 3, 1.03));
    assert(!capture->active && !capture->error);

    for (uint32_t button_index = 0; button_index < 5; ++button_index) {
        reset();
        config.held_buttons = config.entry_buttons = 1u << button_index;
        edge(RCF_CAPTURE_BUTTON, button_index, true, false, 0, 1, 0.9);
        edge(RCF_CAPTURE_BUTTON, button_index, false, false, 0, 2, 0.95);
        rcf_capture_begin(capture, &config, held, 1);
        assert(!capture->active && !capture->error && capture->write - capture->read == 2);
        assert(capture->events[capture->read].kind == RCF_CAPTURE_BEGIN);
        assert(capture->events[capture->read + 1].kind == RCF_CAPTURE_END);
        assert(capture->events[capture->read + 1].code == 3);
    }
}

static bool button(uint64_t id, bool down, double time) {
    return mouse_edge(1, id, down, time);
}

static void press_protocol(void) {
    reset();
    config.exit_buttons = 2;
    rcf_capture_begin(capture, &config, held, 1);
    edge(RCF_CAPTURE_MODIFIER, 55, true, false, 1ull << 20, 1, 1.01);
    assert(!edge(RCF_CAPTURE_BUTTON, 1, true, false, 1ull << 20, 2, 1.02));
    edge(RCF_CAPTURE_MODIFIER, 55, false, false, 0, 3, 1.03);
    assert(!edge(RCF_CAPTURE_BUTTON, 1, false, false, 0, 4, 1.04));
    assert(capture->active && !capture->controls[129].logical);
    assert(capture->write - capture->read == 1);

    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 1ull << 20, 5, 1.05));
    assert(!edge(RCF_CAPTURE_KEY, 13, true, true, 0, 6, 1.06));
    assert(!capture->controls[13].logical && capture->controls[13].owner == RCF_OWNER_HOST);
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 7, 1.07));

    config.exit_buttons = 0;
    assert(button(21, true, 1.10));
    rcf_capture_recover(capture, 129, false, 1.11);
    rcf_capture_recover(capture, 129, false, 1.17);
    assert(button(22, true, 1.18));
    assert(button(21, false, 1.19));
    assert(capture->active && capture->controls[129].logical);
    assert(button(22, false, 1.20));
    assert(!capture->controls[129].logical && capture->controls[129].owner == RCF_OWNER_HOST);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(button(31, true, 1.01));
    assert(button(32, true, 1.02));
    assert(!button(33, true, 1.03));
    assert(!capture->active && capture->error == 13);
    assert(!button(33, false, 1.04));

    for (uint64_t id = 34; id < 42; ++id) {
        assert(!button(id, true, 1.05 + (id - 34) * 0.02));
        assert(!button(id, false, 1.06 + (id - 34) * 0.02));
        assert(capture->controls[129].release_debt == 1);
        assert(capture->controls[129].press_id == 32 && capture->controls[129].retired_press_id == 31);
    }

    assert(button(31, false, 1.3));
    assert(button(32, false, 1.31));
    assert(capture->controls[129].owner == RCF_OWNER_HOST && !capture->controls[129].release_debt);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(button(41, true, 1.01));
    rcf_capture_revoke(capture, 0);
    assert(!button(42, true, 1.02));
    assert(!button(42, false, 1.03));
    assert(button(41, false, 1.04));
    assert(capture->controls[129].owner == RCF_OWNER_HOST && !capture->controls[129].release_debt);

    reset();
    config.terminal_count = 2;
    config.terminal[1] = config.terminal[0];
    rcf_capture_begin(capture, &config, held, 2);
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 1, 2.01));
    assert(!capture->active);
    RcfCaptureEvent ending = capture->events[(capture->write - 1) % RCF_CAPTURE_CAPACITY];
    assert(ending.kind == RCF_CAPTURE_END && ending.code == 1);

    reset();
    config.held_buttons = 2;
    held[129] = true;
    config.entry_mouse_button = 1;
    config.entry_mouse_press_id = 10;
    config.appkit_owned[129] = 1;
    rcf_capture_begin(capture, &config, held, 3);
    rcf_capture_recover(capture, 129, false, 3.01);
    rcf_capture_recover(capture, 129, false, 3.07);
    ending = capture->events[(capture->write - 1) % RCF_CAPTURE_CAPACITY];
    assert(ending.kind == RCF_CAPTURE_END && ending.code == 2);

    reset();
    edge(RCF_CAPTURE_KEY, 14, true, false, 0, 100, 4);
    edge(RCF_CAPTURE_KEY, 14, false, false, 0, 110, 4.01);
    edge(RCF_CAPTURE_KEY, 14, true, false, 0, 120, 4.02);
    config.entry_source_time = 100;
    config.quarantine[14] = 1;
    held[14] = true;
    rcf_capture_begin(capture, &config, held, 4.03);
    assert(capture->initial[14]);
}

static void write_trace(FILE *file, uint32_t scenario) {
    uint8_t initial[RCF_CAPTURE_CONTROLS];
    for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code) initial[code] = capture->initial[code];
    uint32_t count = (uint32_t)(capture->write - capture->read);
    uint32_t active = capture->active;
    assert(fwrite(&scenario, sizeof(scenario), 1, file) == 1);
    assert(fwrite(initial, sizeof(initial), 1, file) == 1);
    assert(fwrite(&capture->error, sizeof(capture->error), 1, file) == 1);
    assert(fwrite(&active, sizeof(active), 1, file) == 1);
    assert(fwrite(&count, sizeof(count), 1, file) == 1);

    for (uint64_t index = capture->read; index < capture->write; ++index)
        assert(fwrite(&capture->events[index % RCF_CAPTURE_CAPACITY], sizeof(RcfCaptureEvent), 1, file) == 1);
}

static void trace_interchange(const char *path) {
    FILE *file = fopen(path, "wb");
    assert(file);
    uint32_t magic = 0x52434635, scenarios = 10;
    assert(fwrite(&magic, sizeof(magic), 1, file) == 1);
    assert(fwrite(&scenarios, sizeof(scenarios), 1, file) == 1);

    reset();
    config.exit_buttons = 2;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!edge(RCF_CAPTURE_BUTTON, 1, true, false, 1ull << 20, 1, 1.01));
    assert(!edge(RCF_CAPTURE_BUTTON, 1, false, false, 0, 2, 1.02));
    assert(capture->active);
    write_trace(file, 0);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 1ull << 20, 1, 1.01));
    assert(!edge(RCF_CAPTURE_KEY, 13, true, true, 0, 2, 1.02));
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 3, 1.03));
    assert(!capture->controls[13].logical);
    write_trace(file, 1);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(button(31, true, 1.01));
    rcf_capture_recover(capture, 129, false, 1.02);
    rcf_capture_recover(capture, 129, false, 1.08);
    assert(button(32, true, 1.09));
    assert(button(31, false, 1.10));
    assert(capture->controls[129].logical);
    assert(button(32, false, 1.11));
    write_trace(file, 2);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    RcfCaptureEvent wheel = {0};
    wheel.kind = RCF_CAPTURE_WHEEL;
    wheel.phase = 2;
    assert(!rcf_capture_route(capture, wheel, 1.01, true));
    assert(!capture->active && capture->error == 14);
    write_trace(file, 3);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 1.01));
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 1.03));
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 3, 1.04));
    assert(!capture->active);
    write_trace(file, 4);

    reset();
    held[13] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 1.01));
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 1.03));
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 3, 1.04));
    write_trace(file, 5);

    reset();
    config.held_buttons = 2;
    rcf_capture_begin(capture, &config, held, 1);
    write_trace(file, 6);

    reset();
    rcf_capture_begin(capture, &config, held, 1);
    wheel.phase = RCF_SCROLL_MAY_BEGIN;
    assert(rcf_capture_route(capture, wheel, 1.01, true));
    wheel.phase = RCF_SCROLL_CANCELLED;
    assert(rcf_capture_route(capture, wheel, 1.02, true));
    write_trace(file, 7);

    reset();
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 0.9));
    held[13] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, true, 0, 2, 1.01));
    assert(edge(RCF_CAPTURE_KEY, 13, true, true, 0, 3, 1.02));
    assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 4, 1.03));
    assert(edge(RCF_CAPTURE_KEY, 53, true, false, 0, 5, 1.04));
    write_trace(file, 8);

    reset();
    assert(!edge(RCF_CAPTURE_BUTTON, 0, true, false, 0, 1, 0.9));
    held[128] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!capture->active);
    write_trace(file, 9);
    assert(fclose(file) == 0);
}

static double replay(uint32_t batch_size) {
    reset();
    rcf_capture_begin(capture, &config, held, 1);
    edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 1.01);
    edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 1.03);
    rcf_capture_tick(capture, 1.10);
    RcfCaptureEvent records[64];
    double through, previous = 1, distance = 0;
    uint64_t sequence = 0, last_sequence = 0;
    bool moving = false;

    do {
        uint32_t count = rcf_capture_read(capture, records, batch_size, &through, &sequence);

        for (uint32_t index = 0; index < count; ++index) {
            RcfCaptureEvent event = records[index];
            assert(event.sequence > last_sequence && event.session == 1);
            assert(event.timestamp >= previous);
            if (moving) distance += event.timestamp - previous;
            previous = event.timestamp;
            last_sequence = event.sequence;
            if (event.kind == RCF_CAPTURE_KEY && event.code == 13) moving = event.down;
        }

        if (moving) distance += through - previous;
        previous = through;
    } while (capture->read < capture->write);

    return distance;
}

static void ordered_exit(void) {
    reset();
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 1.01));
    rcf_capture_finish(capture, RCF_END_RESTORE, 1.02);
    assert(!capture->active && capture->read < capture->write);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 1.03));
    assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 3, 1.04));

    RcfCaptureEvent records[64];
    double through;
    uint64_t sequence;
    assert(rcf_capture_read(capture, records, 64, &through, &sequence) == 3);
    assert(records[0].kind == RCF_CAPTURE_BEGIN);
    assert(records[1].kind == RCF_CAPTURE_KEY && records[1].down);
    assert(records[2].kind == RCF_CAPTURE_END && records[2].code == 1);
    assert(through == 1.02 && sequence == records[2].sequence);

    rcf_capture_finish(capture, RCF_END_KEEP, 1.05);
    assert(rcf_capture_read(capture, records, 64, &through, &sequence) == 0);
}

static void stress_and_discard(void) {
    reset();
    rcf_capture_begin(capture, &config, held, 1);
    RcfCaptureEvent event = {0};
    event.kind = RCF_CAPTURE_MOVE;
    event.dx = 1;
    RcfCaptureEvent batch[64];
    uint64_t sequence = 0, last = 0;
    double through;
    unsigned delivered = 0;

    for (unsigned index = 0; index < 100000; ++index) {
        assert(rcf_capture_route(capture, event, 1 + index / 8000., true));

        if (index % 64 == 63) {
            uint32_t count = rcf_capture_read(capture, batch, 64, &through, &sequence);
            for (uint32_t sample = 0; sample < count; ++sample) {
                assert(batch[sample].sequence > last);
                last = batch[sample].sequence;
                if (batch[sample].kind == RCF_CAPTURE_MOVE) ++delivered;
            }
        }
    }

    while (capture->read < capture->write) {
        uint32_t count = rcf_capture_read(capture, batch, 64, &through, &sequence);
        for (uint32_t sample = 0; sample < count; ++sample)
            if (batch[sample].kind == RCF_CAPTURE_MOVE) ++delivered;
    }
    assert(delivered == 100000 && !capture->error);

    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 1, 20));
    rcf_capture_route(capture, event, 20.01, true);
    rcf_capture_discard(capture);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 2, 20.02));
    assert(rcf_capture_read(capture, batch, 64, &through, &sequence) == 2);
    assert(batch[0].kind == RCF_CAPTURE_KEY && batch[1].kind == RCF_CAPTURE_KEY);

    assert(edge(RCF_CAPTURE_KEY, 13, true, false, 0, 3, 21));
    for (unsigned index = 0; index < RCF_CAPTURE_CAPACITY; ++index)
        rcf_capture_route(capture, event, 21.01, true);

    assert(capture->error == 11 && !capture->active);
    assert(capture->read == capture->write);
    assert(edge(RCF_CAPTURE_KEY, 13, false, false, 0, 4, 21.02));
}

static void inherited_keys(void) {
    for (unsigned observed = 0; observed < 2; ++observed) {
        reset();
        if (observed) assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 10, 0.9));
        held[13] = true;
        rcf_capture_begin(capture, &config, held, 1);
        assert(capture->initial[13] && capture->controls[13].owner == RCF_OWNER_HOST);

        for (unsigned repeat = 0; repeat < 4; ++repeat) {
            assert(edge(RCF_CAPTURE_KEY, 13, true, true, 0, 11 + repeat, 1.01 + repeat * 0.01));
            assert(capture->controls[13].logical && capture->controls[13].owner == RCF_OWNER_HOST);
        }

        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 20, 1.06));
        assert(!capture->controls[13].logical && !capture->error);

        reset();
        if (observed) assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 10, 0.9));
        held[13] = true;
        rcf_capture_begin(capture, &config, held, 1);
        rcf_capture_finish(capture, RCF_END_KEEP, 1.01);
        assert(!edge(RCF_CAPTURE_KEY, 13, true, true, 0, 21, 1.02));
        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 22, 1.03));
        assert(!edge(RCF_CAPTURE_KEY, 13, true, false, 0, 23, 1.04));
        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 24, 1.05));

        reset();
        held[13] = held[55] = true;
        rcf_capture_begin(capture, &config, held, 1);
        assert(!capture->initial[13]);
        edge(RCF_CAPTURE_MODIFIER, 55, false, false, 0, 25, 1.01);
        assert(!edge(RCF_CAPTURE_KEY, 13, true, true, 0, 26, 1.02));
        assert(!capture->controls[13].logical);
        assert(!edge(RCF_CAPTURE_KEY, 13, false, false, 0, 27, 1.03));
    }
}

static void admission_ownership(void) {
    for (unsigned replacement = 0; replacement < 3; ++replacement) {
        reset();
        config.entry_mouse_button = 1;
        config.entry_mouse_press_id = 50;
        config.held_buttons = 2;
        config.appkit_owned[129] = 1;
        assert(!button(50, true, 0.9));
        held[129] = true;

        if (replacement == 1) {
            assert(!edge(RCF_CAPTURE_BUTTON, 0, true, false, 0, 10, 0.95));
            held[128] = true;
        } else if (replacement == 2) {
            assert(!button(50, false, 0.95));
            assert(!button(51, true, 0.96));
        }

        rcf_capture_begin(capture, &config, held, 1);
        assert(capture->active == (replacement == 0));
        assert(!capture->error);

        if (replacement) {
            assert(!capture->initial[128] && !capture->initial[129]);
            RcfCaptureEvent movement = {0};
            movement.kind = RCF_CAPTURE_MOVE;
            assert(!rcf_capture_route(capture, movement, 1.01, true));
            if (replacement == 1) assert(!edge(RCF_CAPTURE_BUTTON, 0, false, false, 0, 11, 1.02));
            assert(!button(replacement == 2 ? 51 : 50, false, 1.03));
            assert(capture->terminal_reason == RCF_END_CANCEL_ENTRY);
        } else {
            assert(capture->controls[129].owner == RCF_OWNER_APPKIT);
        }
    }

    reset();
    assert(!edge(RCF_CAPTURE_BUTTON, 0, true, false, 0, 10, 0.9));
    held[128] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!capture->active && capture->terminal_reason == RCF_END_CANCEL_ENTRY);
    assert(!edge(RCF_CAPTURE_BUTTON, 0, false, false, 0, 11, 1.01));
    assert(!mouse_edge(0, 60, true, 1.02));
    assert(!mouse_edge(0, 60, false, 1.03));

    reset();
    config.entry_mouse_button = 1;
    config.entry_mouse_press_id = 50;
    config.held_buttons = 2;
    config.appkit_owned[129] = 1;
    assert(!button(0, true, 0.9));
    held[129] = true;
    rcf_capture_begin(capture, &config, held, 1);
    assert(!capture->active && capture->controls[129].owner == RCF_OWNER_HOST);
    assert(!button(0, false, 1.01));

    reset();
    config.configured[131] = 1;
    config.exit_buttons = 8;
    rcf_capture_begin(capture, &config, held, 1);
    assert(edge(RCF_CAPTURE_BUTTON, 3, true, false, 0, 10, 1.01));
    assert(edge(RCF_CAPTURE_BUTTON, 3, false, false, 0, 11, 1.02));
    RcfCaptureEvent release = capture->events[(capture->write - 2) % RCF_CAPTURE_CAPACITY];
    assert(release.kind == RCF_CAPTURE_BUTTON && (release.reserved & RCF_EVENT_TERMINAL));
    assert(!capture->active && capture->terminal_reason == RCF_END_KEEP);
}

static void compiled_bindings(const char *path) {
    FILE *fixture = fopen(path, "rb");
    assert(fixture);
    uint32_t count;
    assert(fread(&count, sizeof(count), 1, fixture) == 1);

    for (uint32_t sample = 0; sample < count; ++sample) {
        RcfCaptureBinding binding;
        uint8_t states[RCF_CAPTURE_CONTROLS], expected;
        assert(fread(&binding, sizeof(binding), 1, fixture) == 1);
        assert(fread(states, sizeof(states), 1, fixture) == 1);
        assert(fread(&expected, sizeof(expected), 1, fixture) == 1);

        for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code)
            capture->controls[code].logical = states[code] != 0;

        assert(rcf_capture_binding(capture, &binding) == (expected != 0));
    }

    assert(fgetc(fixture) == EOF);
    fclose(fixture);
    printf("%u compiled managed/native terminal-binding comparisons passed.\n", count);
}

int main(int argc, char **argv) {
    capture = calloc(1, sizeof(*capture));
    assert(capture);
    shortcut();
    ownership();
    inherited_keys();
    admission_ownership();
    modifiers_and_commands();
    recovery_and_source_order();
    missed_modifier_notifications();
    wheel_gestures();
    preliminary_wheel();
    startup_provenance();
    ordered_exit();
    press_protocol();
    assert(fabs(replay(1) - 0.02) < 1e-12);
    assert(fabs(replay(64) - 0.02) < 1e-12);
    stress_and_discard();
    if (argc >= 2) compiled_bindings(argv[1]);
    if (argc == 3) trace_interchange(argv[2]);
    free(capture);
    puts("Native routing, ownership, frontier, wheel and 100000-sample stress checks passed.");
    return 0;
}
