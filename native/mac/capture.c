#include "capture.h"
#include <stddef.h>
#include <string.h>

_Static_assert(sizeof(RcfCaptureConfig) == 4960, "Mac capture configuration ABI");
#define RCF_CONFIG_OFFSET(field, offset) \
    _Static_assert(offsetof(RcfCaptureConfig, field) == offset, "Mac configuration field offset: " #field)
RCF_CONFIG_OFFSET(session, 0);
RCF_CONFIG_OFFSET(window, 8);
RCF_CONFIG_OFFSET(exit_buttons, 12);
RCF_CONFIG_OFFSET(terminal_count, 16);
RCF_CONFIG_OFFSET(entry_buttons, 20);
RCF_CONFIG_OFFSET(command_count, 24);
RCF_CONFIG_OFFSET(configured, 28);
RCF_CONFIG_OFFSET(command_keys, 161);
RCF_CONFIG_OFFSET(quarantine, 294);
RCF_CONFIG_OFFSET(appkit_owned, 427);
RCF_CONFIG_OFFSET(terminal, 560);
RCF_CONFIG_OFFSET(command, 2736);
RCF_CONFIG_OFFSET(held_buttons, 4912);
RCF_CONFIG_OFFSET(entry_code, 4916);
RCF_CONFIG_OFFSET(entry_source_time, 4920);
RCF_CONFIG_OFFSET(entry_modifiers, 4928);
RCF_CONFIG_OFFSET(entry_mouse_button, 4936);
RCF_CONFIG_OFFSET(entry_mouse_press_id, 4944);
RCF_CONFIG_OFFSET(entry_mouse_source_time, 4952);
#undef RCF_CONFIG_OFFSET

static bool control_held(const RcfCapture *capture, uint32_t code, bool physical) {
    if (code >= RCF_CAPTURE_CONTROLS) return false;
    const RcfCaptureControl *control = &capture->controls[code];
    return physical ? control->physical && !control->quarantine : control->logical;
}

static bool key(const RcfCapture *capture, uint32_t code, bool physical) {
    switch (code) {
        case 36: return control_held(capture, 36, physical) || control_held(capture, 52, physical) || control_held(capture, 76, physical);
        case 133: return control_held(capture, 56, physical) || control_held(capture, 60, physical);
        case 134: return control_held(capture, 59, physical) || control_held(capture, 62, physical);
        case 135: return control_held(capture, 58, physical) || control_held(capture, 61, physical);
        case 136: return control_held(capture, 55, physical) || control_held(capture, 54, physical);
        default: return control_held(capture, code, physical);
    }
}

bool rcf_capture_binding(const RcfCapture *capture, const RcfCaptureBinding *binding) {
    if (!binding->count || binding->count > 16) return false;

    for (uint32_t index = 0; index < binding->count; ++index)
        if (!key(capture, binding->keys[index], false)) return false;

    return true;
}

static bool modifier(uint32_t code) {
    return code == 54 || code == 55 || code == 56 || code == 60 || code == 58 || code == 61 || code == 59 || code == 62;
}

static bool command_allowed(const RcfCapture *capture, uint32_t code) {
    if (!capture->config.command_keys[code]) return false;
    if (modifier(code)) return true;

    for (uint32_t index = 0; index < capture->config.command_count; ++index) {
        const RcfCaptureBinding *binding = &capture->config.command[index];
        bool complete = binding->count > 0 && binding->count <= 16;
        bool contains = false;

        for (uint32_t member = 0; complete && member < binding->count; ++member) {
            uint32_t key_code = binding->keys[member];
            contains |= key_code == code || (key_code == 36 && (code == 52 || code == 76));
            complete = key(capture, key_code, true);
        }

        if (contains && complete) return true;
    }

    return false;
}

static void publish(RcfCapture *capture, RcfCaptureEvent event, double now) {
    if (now < capture->last_receipt) now = capture->last_receipt;
    capture->last_receipt = now;
    event.timestamp = now;
    event.sequence = ++capture->sequence;
    event.session = capture->config.session;
    event.navigation_modifiers = 0;
    if (key(capture, 133, false)) event.navigation_modifiers |= 1ull << 17;
    if (key(capture, 134, false)) event.navigation_modifiers |= 1ull << 18;
    if (key(capture, 135, false)) event.navigation_modifiers |= 1ull << 19;
    if (key(capture, 136, false)) event.navigation_modifiers |= 1ull << 20;
    event.buttons = 0;
    for (uint32_t button = 0; button < 5; ++button)
        if (capture->controls[128 + button].physical) event.buttons |= 1u << button;

    if (capture->write - capture->read == RCF_CAPTURE_CAPACITY) {
        if (!capture->error) capture->failure = event;
        rcf_capture_revoke(capture, 11);
        return;
    }

    capture->events[capture->write++ % RCF_CAPTURE_CAPACITY] = event;
    uint32_t depth = (uint32_t)(capture->write - capture->read);
    if (depth > capture->high_water) capture->high_water = depth;
    capture->frontier = now;
    capture->frontier_sequence = capture->sequence;
}

void rcf_capture_tick(RcfCapture *capture, double now) {
    if (!capture->active) return;
    if (now < capture->last_receipt) now = capture->last_receipt;
    capture->frontier = now;
    capture->frontier_sequence = capture->sequence;
}

void rcf_capture_begin(RcfCapture *capture, const RcfCaptureConfig *config,
    const bool physical[RCF_CAPTURE_CONTROLS], double now) {
    capture->config = *config;
    capture->active = true;
    capture->error = 0;
    capture->read = capture->write;
    capture->discard_through = capture->sequence;
    capture->started_at = now;
    capture->last_receipt = now;
    capture->high_water = capture->regressions = 0;
    capture->maximum_source_delay = 0;
    capture->failure = (RcfCaptureEvent){0};
    capture->terminal_reason = UINT32_MAX;

    if (config->terminal_count > RCF_CAPTURE_BINDINGS || config->command_count > RCF_CAPTURE_BINDINGS) {
        rcf_capture_revoke(capture, 12);
        return;
    }

    bool admission_cancelled = false;
    uint32_t recognised_entry = 0;

    for (uint32_t button = 0; button < 5; ++button) {
        RcfCaptureControl *control = &capture->controls[128 + button];
        bool entry = config->entry_mouse_button == button && config->entry_mouse_press_id;
        bool matches = entry && (control->observed && control->physical &&
            control->press_id == config->entry_mouse_press_id &&
            (!config->entry_mouse_source_time || control->source_time == config->entry_mouse_source_time));
        bool unseen = entry && !control->cycle && !control->last_edge_source && config->appkit_owned[128 + button];
        if (matches || unseen) recognised_entry |= 1u << button;

        if (unseen && physical[128 + button]) {
            // The AppKit Down can precede tap startup. Retain its identity for the real Up.
            control->cycle = ++capture->sequence;
            control->press_id = config->entry_mouse_press_id;
            control->source_time = config->entry_mouse_source_time;
            control->physical = control->observed = true;
        }

        if (physical[128 + button] && !(matches || unseen)) admission_cancelled = true;
        if ((config->held_buttons & (1u << button)) && (!physical[128 + button] || !entry)) admission_cancelled = true;
    }

    for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code) {
        RcfCaptureControl *control = &capture->controls[code];
        control->sampled = physical[code] && control->owner == RCF_OWNER_HOST &&
            !(control->physical && control->observed && !control->repaired);
        if (control->owner == RCF_OWNER_NATIVE && !physical[code]) control->repaired = true;
        control->physical = physical[code];
        control->quarantine = physical[code] && config->quarantine[code];
        // A later observed Down is a different entry cycle. No history stays conservative.
        if (control->quarantine && config->entry_source_time && control->source_time > config->entry_source_time)
            control->quarantine = false;
        if (physical[code] && control->release_debt && !control->retired_press_id)
            control->quarantine = true;
        capture->initial_quarantine[code] = control->quarantine;
        control->release_candidate = 0;
        if (physical[code] && config->appkit_owned[code] && control->owner == RCF_OWNER_HOST &&
            (code < 128 || (recognised_entry & (1u << (code - 128)))))
            control->owner = RCF_OWNER_APPKIT;
    }

    for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code) {
        RcfCaptureControl *control = &capture->controls[code];
        bool command = physical[54] || physical[55];
        control->logical = !admission_cancelled && control->physical && !control->quarantine && config->configured[code] &&
            (!command || command_allowed(capture, code));
        control->accepted = control->logical;
        capture->initial[code] = control->logical;
    }

    for (uint32_t index = 0; index < config->terminal_count; ++index)
        capture->terminal_held[index] = rcf_capture_binding(capture, &config->terminal[index]);

    RcfCaptureEvent begin = {0};
    begin.kind = RCF_CAPTURE_BEGIN;
    publish(capture, begin, now);
    if (admission_cancelled) rcf_capture_finish(capture, RCF_END_CANCEL_ENTRY, now);
}

static int terminal(RcfCapture *capture) {
    int reason = -1;

    for (uint32_t index = 0; index < capture->config.terminal_count; ++index) {
        bool held = rcf_capture_binding(capture, &capture->config.terminal[index]);
        if (held && !capture->terminal_held[index])
            reason = index == 1 ? RCF_END_RESTORE : (reason < 0 ? RCF_END_KEEP : reason);
        capture->terminal_held[index] = held;
    }

    return reason;
}

static int release_terminal(RcfCapture *capture, uint32_t code, bool accepted) {
    int reason = terminal(capture);
    bool entry = code >= 128 && (capture->config.entry_buttons & (1u << (code - 128)));
    if (entry) capture->config.entry_buttons &= ~(1u << (code - 128));

    if (code >= 128 && (capture->config.held_buttons & (1u << (code - 128))))
        reason = reason == RCF_END_RESTORE ? RCF_END_RESTORE : RCF_END_HELD_RELEASE;
    else if (accepted && code >= 128 && !entry && (capture->config.exit_buttons & (1u << (code - 128))))
        reason = reason < 0 ? RCF_END_KEEP : reason;

    return reason;
}

bool rcf_capture_route(RcfCapture *capture, RcfCaptureEvent event, double now, bool permitted) {
    bool paired = event.kind == RCF_CAPTURE_KEY || event.kind == RCF_CAPTURE_MODIFIER || event.kind == RCF_CAPTURE_BUTTON;
    uint32_t code = event.kind == RCF_CAPTURE_BUTTON ? event.code + 128 : event.code;
    bool consumed = false;

    if (!permitted && capture->active) rcf_capture_revoke(capture, 0);

    if (paired && code < RCF_CAPTURE_CONTROLS) {
        RcfCaptureControl *control = &capture->controls[code];

        bool fresh = event.down && !event.repeated;
        control->last_edge_source = event.source_time ? event.source_time : 1;
        bool sampled = fresh && control->sampled;
        bool identified = event.kind == RCF_CAPTURE_BUTTON && control->press_id && event.press_id;

        if (event.kind == RCF_CAPTURE_BUTTON && !event.press_id && control->press_id &&
            control->owner != RCF_OWNER_HOST && (fresh || !event.down)) {
            if (capture->active) {
                capture->failure = event;
                capture->failure.timestamp = now;
                capture->failure.session = capture->config.session;
                rcf_capture_revoke(capture, 13);
            }
            return false;
        }

        if (fresh && !sampled && identified && (control->physical || control->repaired) &&
            (event.press_id == control->press_id ||
                (control->owner != RCF_OWNER_HOST && control->release_debt))) {
            if (capture->active) {
                capture->failure = event;
                capture->failure.timestamp = now;
                capture->failure.session = capture->config.session;
                rcf_capture_revoke(capture, 13);
            }
            return event.press_id == control->press_id && control->owner == RCF_OWNER_NATIVE;
        }

        // A fresh ordinary cycle retires abandoned history that has no mouse identity.
        if (fresh && !sampled && !identified && event.kind != RCF_CAPTURE_MODIFIER) {
            if (capture->active && (control->repaired || (control->physical && control->observed))) {
                capture->failure = event;
                capture->failure.timestamp = now;
                capture->failure.session = capture->config.session;
                rcf_capture_revoke(capture, 13);
            }

            control->owner = RCF_OWNER_HOST;
            control->release_debt = 0;
            control->retired_press_id = 0;
            control->retired_owner = RCF_OWNER_HOST;
            control->physical = control->logical = control->accepted = control->repaired = false;
            if (!capture->active) control->quarantine = false;
        }

        if (!event.down && control->release_debt && event.press_id &&
            event.press_id == control->retired_press_id) {
            --control->release_debt;
            bool native = control->retired_owner != RCF_OWNER_APPKIT;
            control->retired_owner = RCF_OWNER_NATIVE;
            if (!control->release_debt) control->retired_press_id = 0;
            return event.kind != RCF_CAPTURE_MODIFIER && native;
        }

        if (!event.down && event.press_id && control->press_id && event.press_id != control->press_id)
            return false;

        bool was_logical = control->logical;
        bool was_accepted = control->accepted;
        if (fresh) {
            if (!sampled && identified && control->owner != RCF_OWNER_HOST && (control->repaired || control->physical)) {
                control->retired_owner = control->owner;
                control->release_debt = 1;
                control->retired_press_id = control->press_id;
            }
            control->owner = RCF_OWNER_HOST;
            control->cycle = ++capture->sequence;
            control->source_time = event.source_time;
            control->press_id = event.press_id;
            control->accepted = false;
            control->repaired = false;
            control->sampled = false;
            control->observed = true;
        }

        control->physical = event.down != 0;
        control->release_candidate = 0;

        if (capture->active && permitted && event.down && (code == 54 || code == 55)) {
            for (uint32_t held_code = 0; held_code < 128; ++held_code) {
                RcfCaptureControl *held = &capture->controls[held_code];
                if (!held->logical || modifier(held_code) || command_allowed(capture, held_code)) continue;
                held->logical = false;
                held->quarantine = true;
                RcfCaptureEvent release = {0};
                release.kind = RCF_CAPTURE_KEY;
                release.code = held_code;
                release.routing = held->owner;
                release.source_time = event.source_time;
                publish(capture, release, now);
            }
        }

        bool command = (event.modifiers & (1ull << 20)) && !command_allowed(capture, code);
        bool eligible = capture->active && permitted && !control->quarantine && !command && capture->config.configured[code];
        if (event.down && !event.repeated) control->accepted = eligible;
        eligible = eligible && control->accepted;

        if (eligible && event.down && !event.repeated && event.kind != RCF_CAPTURE_MODIFIER && capture->config.configured[code])
            control->owner = RCF_OWNER_NATIVE;

        consumed = event.kind != RCF_CAPTURE_MODIFIER && control->owner == RCF_OWNER_NATIVE;
        if (capture->active && permitted && event.repeated && event.kind == RCF_CAPTURE_KEY &&
            (control->quarantine || eligible))
            consumed = true;

        control->logical = eligible && event.down != 0;
        event.routing = control->owner;
        int reason = -1;

        if (capture->active && permitted) {
            reason = control->physical ? terminal(capture) : release_terminal(capture, code, was_accepted);
            if (reason >= 0) event.reserved |= RCF_EVENT_TERMINAL;
        }

        if (capture->active && permitted && (eligible || was_logical)) {
            event.down = control->logical;
            publish(capture, event, now);
        }

        if (reason >= 0) rcf_capture_finish(capture, (uint32_t)reason, now);

        if (!control->physical) {
            control->quarantine = false;
            control->accepted = false;
            control->repaired = false;
            control->owner = RCF_OWNER_HOST;
            control->observed = control->sampled = false;
        }
    } else if (event.kind == RCF_CAPTURE_MOVE) {
        if (capture->active && permitted) {
            publish(capture, event, now);
            consumed = true;
        }
    } else if (event.kind == RCF_CAPTURE_WHEEL) {
        bool gesture = event.phase || event.momentum;
        bool preliminary = (event.phase & RCF_SCROLL_MAY_BEGIN) != 0;
        bool beginning = (event.phase & RCF_SCROLL_BEGAN) != 0;

        if (preliminary && !capture->wheel_preliminary) {
            capture->wheel_preliminary = true;
            capture->wheel_started = false;
            capture->wheel_owned = capture->active && permitted;
            capture->wheel_session = capture->wheel_owned ? capture->config.session : 0;
            capture->wheel_ended = false;
        }

        if (beginning) {
            bool retained = capture->wheel_preliminary && capture->wheel_owned;
            if (!retained) capture->wheel_owned = capture->active && permitted;
            if (!retained) capture->wheel_session = capture->wheel_owned ? capture->config.session : 0;
            capture->wheel_preliminary = false;
            capture->wheel_started = true;
            capture->wheel_ended = false;
        }
        if ((event.phase & RCF_SCROLL_CHANGED) && capture->wheel_preliminary) {
            capture->wheel_preliminary = false;
            capture->wheel_started = true;
        }
        if (!gesture) consumed = capture->active && permitted;
        else consumed = capture->wheel_owned || (event.momentum && capture->wheel_ended);

        if (gesture && !consumed && capture->active && !capture->wheel_preliminary) {
            capture->failure = event;
            capture->failure.timestamp = now;
            capture->failure.session = capture->config.session;
            rcf_capture_revoke(capture, 14);
        }

        if (capture->active && permitted && consumed && (!gesture || capture->wheel_session == capture->config.session))
            publish(capture, event, now);
        if (event.momentum && consumed) capture->wheel_owned = true;
        if (event.phase & RCF_SCROLL_ENDED) {
            bool tail = consumed && capture->wheel_started;
            capture->wheel_owned = false;
            capture->wheel_ended = tail;
            capture->wheel_preliminary = capture->wheel_started = false;
        }
        if ((event.phase & RCF_SCROLL_CANCELLED) || event.momentum == RCF_MOMENTUM_ENDED) {
            capture->wheel_owned = capture->wheel_ended = false;
            capture->wheel_preliminary = capture->wheel_started = false;
        }
    }

    rcf_capture_tick(capture, now);
    return consumed;
}

void rcf_capture_recover(RcfCapture *capture, uint32_t code, bool held, double now) {
    if (!capture->active || code >= RCF_CAPTURE_CONTROLS) return;
    RcfCaptureControl *control = &capture->controls[code];

    if (held || !control->physical) {
        control->release_candidate = 0;
        return;
    }

    if (!control->release_candidate) control->release_candidate = now;
    if (now - control->release_candidate < 0.05) return;

    bool logical = control->logical;
    bool accepted = control->accepted;
    control->physical = control->logical = control->quarantine = false;
    control->accepted = false;
    control->repaired = true;
    control->release_candidate = 0;
    int reason = release_terminal(capture, code, accepted);

    if (logical) {
        RcfCaptureEvent event = {0};
        event.kind = code < 128 ? RCF_CAPTURE_KEY : RCF_CAPTURE_BUTTON;
        event.code = code < 128 ? code : code - 128;
        event.routing = control->owner;
        event.reserved = RCF_EVENT_REPAIRED | (reason >= 0 ? RCF_EVENT_TERMINAL : 0);
        publish(capture, event, now);
    }

    if (reason >= 0) rcf_capture_finish(capture, (uint32_t)reason, now);
}

void rcf_capture_flags(RcfCapture *capture, uint64_t flags, uint64_t source, double now, bool permitted) {
    static const uint32_t codes[10] = {56, 60, 59, 62, 58, 61, 55, 54, 57, 63};
    static const uint64_t side[10] = {2, 4, 1, 0x2000, 0x20, 0x40, 8, 0x10, 0, 0};
    static const uint64_t generic[10] = {
        1ull << 17, 1ull << 17, 1ull << 18, 1ull << 18, 1ull << 19,
        1ull << 19, 1ull << 20, 1ull << 20, 1ull << 16, 1ull << 23
    };

    for (uint32_t index = 0; index < 10; ++index) {
        uint32_t code = codes[index];
        bool down = (flags & generic[index]) != 0;

        if (index < 8 && down) {
            uint32_t other = index ^ 1u;
            bool sided = (flags & (side[index] | side[other])) != 0;
            down = sided ? (flags & side[index]) != 0 :
                capture->controls[code].physical || (index % 2 == 0 && !capture->controls[codes[other]].physical);
        }

        if (capture->controls[code].physical == down) continue;

        RcfCaptureEvent event = {0};
        event.kind = RCF_CAPTURE_MODIFIER;
        event.code = code;
        event.down = down;
        event.modifiers = flags;
        event.source_time = source;
        rcf_capture_route(capture, event, now, permitted);
    }
}

void rcf_capture_revoke(RcfCapture *capture, uint32_t error) {
    capture->active = false;
    if (!capture->error && error) {
        capture->error = error;
        capture->failure_read = capture->read;
        capture->failure_write = capture->write;
    }
    capture->read = capture->write;
}

void rcf_capture_finish(RcfCapture *capture, uint32_t reason, double now) {
    if (!capture->active) return;

    RcfCaptureEvent event = {0};
    event.kind = RCF_CAPTURE_END;
    event.code = reason;
    capture->terminal_reason = reason;
    publish(capture, event, now);
    capture->active = false;
}

void rcf_capture_discard(RcfCapture *capture) {
    capture->discard_through = capture->sequence;
}

uint32_t rcf_capture_read(RcfCapture *capture, RcfCaptureEvent *destination, uint32_t capacity,
    double *through, uint64_t *sequence) {
    uint32_t count = 0;

    while (capture->read < capture->write && count < capacity) {
        RcfCaptureEvent event = capture->events[capture->read++ % RCF_CAPTURE_CAPACITY];
        bool pointer = event.kind == RCF_CAPTURE_MOVE || event.kind == RCF_CAPTURE_WHEEL;
        if (!pointer || event.sequence > capture->discard_through) destination[count++] = event;
    }

    *through = capture->read < capture->write ? capture->events[capture->read % RCF_CAPTURE_CAPACITY].timestamp : capture->frontier;
    *sequence = capture->read < capture->write ? capture->events[capture->read % RCF_CAPTURE_CAPACITY].sequence - 1 : capture->frontier_sequence;
    return count;
}
