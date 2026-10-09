#include <CoreGraphics/CoreGraphics.h>
#include <CoreFoundation/CoreFoundation.h>
#include <ApplicationServices/ApplicationServices.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <pthread/qos.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include "pointer-worker.h"

typedef struct {
    RcfCaptureConfig config;
    RcfCaptureControl controls[RCF_CAPTURE_CONTROLS];
    bool quarantine[RCF_CAPTURE_CONTROLS];
    RcfCaptureEvent failure;
    uint64_t sequence, read, write, replay;
    double frontier, source_delay, consumer_delay, lock_wait, copy_time;
    uint32_t error, high_water, regressions, state, permitted, terminal;
    bool active, cleaned;
} RcfWorkerSnapshot;

struct RcfPointerWorker {
    pthread_mutex_t gate;
    _Atomic uint32_t state;
    _Atomic bool permitted;
    _Atomic bool notifications_enabled;
    CFRunLoopRef loop;
    CFRunLoopSourceRef command, wake;
    CFRunLoopTimerRef timer;
    CFMachPortRef tap;
    RcfMacNotify notify;
    RcfCaptureConfig requested;
    bool activate, finish, clock_scheduled;
    uint32_t finish_reason;
    double through;
    double maximum_consumer_delay;
    double maximum_lock_wait, maximum_copy_time;
    RcfWorkerSnapshot last_failure;
    bool has_failure;
    uint64_t delivered_sequence, last_source;
    RcfCapture capture;
    RcfCaptureEvent batch[8192];
};

static void snapshot(RcfPointerWorker *worker, RcfWorkerSnapshot *value) {
    RcfCapture *capture = &worker->capture;
    value->config = capture->config;
    memcpy(value->controls, capture->controls, sizeof(value->controls));
    memcpy(value->quarantine, capture->initial_quarantine, sizeof(value->quarantine));
    value->failure = capture->failure;
    value->sequence = capture->sequence;
    value->read = capture->error ? capture->failure_read : capture->read;
    value->write = capture->error ? capture->failure_write : capture->write;
    value->replay = worker->delivered_sequence;
    value->frontier = worker->through;
    value->source_delay = capture->maximum_source_delay;
    value->consumer_delay = worker->maximum_consumer_delay;
    value->lock_wait = worker->maximum_lock_wait;
    value->copy_time = worker->maximum_copy_time;
    value->error = capture->error;
    value->high_water = capture->high_water;
    value->regressions = capture->regressions;
    value->active = capture->active;
    value->state = atomic_load(&worker->state);
    value->permitted = atomic_load(&worker->permitted);
    value->terminal = capture->terminal_reason;
    value->cleaned = false;
}

static void preserve_failure(RcfPointerWorker *worker) {
    if (worker->capture.error && (!worker->has_failure || worker->last_failure.config.session != worker->capture.config.session)) {
        snapshot(worker, &worker->last_failure);
        worker->has_failure = true;
    }
}

static RcfPointerWorker *router;
static mach_timebase_info_data_t clock_scale;
static pthread_once_t clock_once = PTHREAD_ONCE_INIT;

static void initialize_clock(void) { mach_timebase_info(&clock_scale); }

double rcf_pointer_clock(void) {
    pthread_once(&clock_once, initialize_clock);
    return (double)mach_absolute_time() * clock_scale.numer / clock_scale.denom / 1e9;
}

static void notify_main(RcfPointerWorker *worker) {
    if (!atomic_load(&worker->notifications_enabled)) return;
    CFRunLoopSourceSignal(worker->wake);
    CFRunLoopWakeUp(CFRunLoopGetMain());
}

static void deliver_notification(void *context) {
    RcfPointerWorker *worker = context;
    // Callback replacement and delivery both happen on the main thread.
    if (worker->notify) worker->notify();
}

static void update_clock(RcfPointerWorker *worker) {
    if (!worker->loop || !worker->timer || CFRunLoopGetCurrent() != worker->loop) return;
    bool needed = worker->capture.active && atomic_load(&worker->permitted);

    if (needed && !worker->clock_scheduled) {
        CFRunLoopTimerSetNextFireDate(worker->timer, CFAbsoluteTimeGetCurrent() + 1.0 / 120);
        CFRunLoopAddTimer(worker->loop, worker->timer, kCFRunLoopDefaultMode);
        worker->clock_scheduled = true;
    } else if (!needed && worker->clock_scheduled) {
        CFRunLoopRemoveTimer(worker->loop, worker->timer, kCFRunLoopDefaultMode);
        worker->clock_scheduled = false;
    }
}

static void wake_command(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    CFRunLoopRef loop = worker->loop;
    CFRunLoopSourceRef command = worker->command;
    pthread_mutex_unlock(&worker->gate);

    if (loop && command) {
        CFRunLoopSourceSignal(command);
        CFRunLoopWakeUp(loop);
    }
}

static bool physical(uint32_t code) {
    if (code == 57 || code == 63) {
        CGEventFlags flags = CGEventSourceFlagsState(kCGEventSourceStateCombinedSessionState);
        return (flags & (code == 57 ? kCGEventFlagMaskAlphaShift : kCGEventFlagMaskSecondaryFn)) != 0;
    }

    return code < 128 ? CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState, code) :
        CGEventSourceButtonState(kCGEventSourceStateCombinedSessionState, code - 128);
}

static bool modifier_down(uint32_t code, uint64_t flags) {
    uint64_t mask = 0;
    switch (code) {
        case 59: mask = 0x1; break;
        case 56: mask = 0x2; break;
        case 60: mask = 0x4; break;
        case 55: mask = 0x8; break;
        case 54: mask = 0x10; break;
        case 58: mask = 0x20; break;
        case 61: mask = 0x40; break;
        case 57: mask = kCGEventFlagMaskAlphaShift; break;
        case 62: mask = 0x2000; break;
        case 63: mask = kCGEventFlagMaskSecondaryFn; break;
    }
    return (flags & mask) != 0;
}

static CGEventRef pointer_event(CGEventTapProxy proxy, CGEventType type, CGEventRef event, void *context) {
    (void)proxy;
    RcfPointerWorker *worker = context;
    double now = rcf_pointer_clock();

    if (type == kCGEventTapDisabledByTimeout || type == kCGEventTapDisabledByUserInput) {
        atomic_store(&worker->permitted, false);
        pthread_mutex_lock(&worker->gate);
        if (!worker->capture.error) {
            worker->capture.failure.kind = (uint32_t)type;
            worker->capture.failure.timestamp = now;
            worker->capture.failure.session = worker->capture.config.session;
        }
        rcf_capture_revoke(&worker->capture, type == kCGEventTapDisabledByTimeout ? 4 : 5);
        update_clock(worker);
        preserve_failure(worker);
        pthread_mutex_unlock(&worker->gate);
        atomic_store(&worker->state, RCF_RAW_FAILED);
        // A timeout retry only drains owned releases. Flight permission stays revoked.
        if (type == kCGEventTapDisabledByTimeout && worker->tap) CGEventTapEnable(worker->tap, true);
        notify_main(worker);
        return event;
    }

    RcfCaptureEvent value = {0};
    value.source_time = CGEventGetTimestamp(event);
    value.modifiers = CGEventGetFlags(event);
    CGPoint position = CGEventGetLocation(event);
    value.screen_x = position.x;
    value.screen_y = position.y;
    switch (type) {
        case kCGEventMouseMoved: case kCGEventLeftMouseDragged:
        case kCGEventRightMouseDragged: case kCGEventOtherMouseDragged:
            value.kind = RCF_CAPTURE_MOVE;
            value.dx = CGEventGetIntegerValueField(event, kCGEventUnacceleratedPointerMovementX);
            value.dy = CGEventGetIntegerValueField(event, kCGEventUnacceleratedPointerMovementY);
            break;
        case kCGEventKeyDown: case kCGEventKeyUp:
            value.kind = RCF_CAPTURE_KEY;
            value.code = (uint32_t)CGEventGetIntegerValueField(event, kCGKeyboardEventKeycode);
            value.down = type == kCGEventKeyDown;
            value.repeated = (uint32_t)CGEventGetIntegerValueField(event, kCGKeyboardEventAutorepeat);
            break;
        case kCGEventFlagsChanged:
            value.kind = RCF_CAPTURE_MODIFIER;
            value.code = (uint32_t)CGEventGetIntegerValueField(event, kCGKeyboardEventKeycode);
            value.down = modifier_down(value.code, value.modifiers);
            break;
        case kCGEventLeftMouseDown: case kCGEventLeftMouseUp:
        case kCGEventRightMouseDown: case kCGEventRightMouseUp:
        case kCGEventOtherMouseDown: case kCGEventOtherMouseUp:
            value.kind = RCF_CAPTURE_BUTTON;
            value.code = (uint32_t)CGEventGetIntegerValueField(event, kCGMouseEventButtonNumber);
            value.press_id = (uint64_t)CGEventGetIntegerValueField(event, kCGMouseEventNumber);
            value.down = type == kCGEventLeftMouseDown || type == kCGEventRightMouseDown || type == kCGEventOtherMouseDown;
            break;
        case kCGEventScrollWheel:
            value.kind = RCF_CAPTURE_WHEEL;
            value.precise = (uint32_t)CGEventGetIntegerValueField(event, kCGScrollWheelEventIsContinuous);
            value.wheel = value.precise ? CGEventGetIntegerValueField(event, kCGScrollWheelEventPointDeltaAxis1) :
                CGEventGetDoubleValueField(event, kCGScrollWheelEventFixedPtDeltaAxis1);
            value.phase = (uint32_t)CGEventGetIntegerValueField(event, kCGScrollWheelEventScrollPhase);
            value.momentum = (uint32_t)CGEventGetIntegerValueField(event, kCGScrollWheelEventMomentumPhase);
            break;
        default: return event;
    }

    double waiting = rcf_pointer_clock();
    pthread_mutex_lock(&worker->gate);
    double waited = rcf_pointer_clock() - waiting;
    if (waited > worker->maximum_lock_wait) worker->maximum_lock_wait = waited;

    bool permitted = atomic_load(&worker->permitted);
    if (value.kind == RCF_CAPTURE_BUTTON || value.kind == RCF_CAPTURE_WHEEL) {
        uint32_t target = (uint32_t)CGEventGetIntegerValueField(event, kCGMouseEventWindowUnderMousePointer);
        value.target_window = target;
        if (target && target != worker->capture.config.window && worker->capture.active) {
            permitted = false;
            atomic_store(&worker->permitted, false);
            if (!worker->capture.error) {
                worker->capture.failure = value;
                worker->capture.failure.timestamp = now;
                worker->capture.failure.session = worker->capture.config.session;
            }
            rcf_capture_revoke(&worker->capture, 8);
        }
    }

    if (value.source_time) {
        if (value.source_time < worker->last_source) ++worker->capture.regressions;
        worker->last_source = value.source_time;
        double delay = now - (double)value.source_time / 1e9;
        if (delay > worker->capture.maximum_source_delay) worker->capture.maximum_source_delay = delay;
    }
    if (value.kind != RCF_CAPTURE_MODIFIER)
        rcf_capture_flags(&worker->capture, value.modifiers, value.source_time, now, permitted);

    bool consumed = rcf_capture_route(&worker->capture, value, now, permitted);
    bool failed = worker->capture.error != 0;
    update_clock(worker);
    preserve_failure(worker);
    pthread_mutex_unlock(&worker->gate);

    if (failed) {
        atomic_store(&worker->permitted, false);
        atomic_store(&worker->state, RCF_RAW_FAILED);
    }
    notify_main(worker);
    return consumed ? NULL : event;
}

static CGEventTapInformation *tap_list(uint32_t *count) {
    if (CGGetEventTapList(0, NULL, count) != kCGErrorSuccess) return NULL;
    uint32_t capacity = *count + 16;
    CGEventTapInformation *items = calloc(capacity, sizeof(*items));

    if (!items || CGGetEventTapList(capacity, items, count) != kCGErrorSuccess || *count > capacity) {
        free(items);
        return NULL;
    }

    return items;
}

static CFMachPortRef create_ordered_tap(RcfPointerWorker *worker) {
    CGEventMask mask = CGEventMaskBit(kCGEventMouseMoved) | CGEventMaskBit(kCGEventLeftMouseDragged) |
        CGEventMaskBit(kCGEventRightMouseDragged) | CGEventMaskBit(kCGEventOtherMouseDragged) |
        CGEventMaskBit(kCGEventLeftMouseDown) | CGEventMaskBit(kCGEventLeftMouseUp) |
        CGEventMaskBit(kCGEventRightMouseDown) | CGEventMaskBit(kCGEventRightMouseUp) |
        CGEventMaskBit(kCGEventOtherMouseDown) | CGEventMaskBit(kCGEventOtherMouseUp) |
        CGEventMaskBit(kCGEventScrollWheel) | CGEventMaskBit(kCGEventKeyDown) |
        CGEventMaskBit(kCGEventKeyUp) | CGEventMaskBit(kCGEventFlagsChanged);

    uint32_t before_count = 0;
    CGEventTapInformation *before = tap_list(&before_count);
    if (!before) {
        worker->capture.error = 1;
        return NULL;
    }

    // Capture after existing filters. Later filters still cannot be treated as acknowledgements.
    CFMachPortRef tap = CGEventTapCreateForPid(getpid(), kCGTailAppendEventTap,
        kCGEventTapOptionDefault, mask, pointer_event, worker);
    if (!tap) {
        free(before);
        worker->capture.error = 2;
        return NULL;
    }

    uint32_t count = 0;
    uint32_t candidates = 0;
    bool complete = false;
    CGEventTapInformation *taps = tap_list(&count);
    if (taps) {
        for (uint32_t index = 0; index < count; ++index) {
            CGEventTapInformation item = taps[index];
            bool added = true;

            for (uint32_t old = 0; old < before_count; ++old)
                if (before[old].eventTapID == item.eventTapID) added = false;

            if (added && item.tappingProcess == getpid() && item.processBeingTapped == getpid()) {
                ++candidates;
                complete = item.enabled && item.options == kCGEventTapOptionDefault && (item.eventsOfInterest & mask) == mask;
            }
        }
    }
    free(taps);
    free(before);

    if (!complete || candidates != 1) {
        worker->capture.error = 3;
        CFMachPortInvalidate(tap);
        CFRelease(tap);
        return NULL;
    }
    return tap;
}

static void activate_session(void *context) {
    RcfPointerWorker *worker = context;
    pthread_mutex_lock(&worker->gate);

    if (!worker->activate && atomic_load(&worker->state) == RCF_RAW_STARTING && worker->tap) {
        CGEventTapEnable(worker->tap, true);

        if (CGEventTapIsEnabled(worker->tap)) {
            atomic_store(&worker->state, RCF_RAW_RUNNING);
        } else {
            rcf_capture_revoke(&worker->capture, 4);
            atomic_store(&worker->state, RCF_RAW_FAILED);
            atomic_store(&worker->permitted, false);
        }
    }

    if (worker->activate && atomic_load(&worker->permitted)) {
        if (worker->tap && CGEventTapIsEnabled(worker->tap)) {
            bool held[RCF_CAPTURE_CONTROLS];

            for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code)
                held[code] = physical(code);

            preserve_failure(worker);
            worker->maximum_lock_wait = worker->maximum_copy_time = worker->maximum_consumer_delay = 0;
            rcf_capture_begin(&worker->capture, &worker->requested, held, rcf_pointer_clock());
            worker->last_source = 0;
        } else {
            rcf_capture_revoke(&worker->capture, 4);
        }

        worker->activate = false;
        atomic_store(&worker->state, worker->capture.error ? RCF_RAW_FAILED : RCF_RAW_RUNNING);
        if (worker->capture.error) atomic_store(&worker->permitted, false);
    }

    if (worker->finish) {
        if (atomic_load(&worker->permitted))
            rcf_capture_finish(&worker->capture, worker->finish_reason, rcf_pointer_clock());

        worker->finish = false;

        if (worker->capture.error) {
            atomic_store(&worker->state, RCF_RAW_FAILED);
            atomic_store(&worker->permitted, false);
        }
    }

    preserve_failure(worker);
    update_clock(worker);
    pthread_mutex_unlock(&worker->gate);
    notify_main(worker);
}

static void publish_clock(CFRunLoopTimerRef timer, void *context) {
    (void)timer;
    RcfPointerWorker *worker = context;
    double now = rcf_pointer_clock();
    pthread_mutex_lock(&worker->gate);

    if (worker->capture.active && atomic_load(&worker->permitted)) {
        for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code)
            if (worker->capture.controls[code].physical)
                rcf_capture_recover(&worker->capture, code, physical(code), now);

        rcf_capture_tick(&worker->capture, now);
        if (worker->capture.error) {
            atomic_store(&worker->permitted, false);
            atomic_store(&worker->state, RCF_RAW_FAILED);
            preserve_failure(worker);
        }
        notify_main(worker);
    }

    update_clock(worker);
    pthread_mutex_unlock(&worker->gate);
}

static void *collect_pointer(void *context) {
    RcfPointerWorker *worker = context;
    pthread_setname_np("RhinosCanFly.input");
    pthread_mutex_lock(&worker->gate);
    worker->tap = create_ordered_tap(worker);
    CFRunLoopSourceRef events = worker->tap ? CFMachPortCreateRunLoopSource(NULL, worker->tap, 0) : NULL;
    CFRunLoopSourceContext command = {0};
    command.info = worker;
    command.perform = activate_session;
    CFRunLoopTimerContext timer = {0};
    timer.info = worker;
    worker->command = CFRunLoopSourceCreate(NULL, 0, &command);
    worker->timer = CFRunLoopTimerCreate(NULL, CFAbsoluteTimeGetCurrent(), 1.0 / 120, 0, 0, publish_clock, &timer);

    if (!events || !worker->command || !worker->timer) {
        if (events) CFRelease(events);

        if (worker->command) {
            CFRelease(worker->command);
            worker->command = NULL;
        }

        if (worker->timer) {
            CFRelease(worker->timer);
            worker->timer = NULL;
        }

        if (worker->tap) {
            CFMachPortInvalidate(worker->tap);
            CFRelease(worker->tap);
            worker->tap = NULL;
        }

        if (!worker->capture.error) worker->capture.error = 4;
        atomic_store(&worker->state, RCF_RAW_FAILED);
        preserve_failure(worker);
        pthread_mutex_unlock(&worker->gate);
        notify_main(worker);
        return NULL;
    }

    worker->loop = CFRunLoopGetCurrent();
    CFRunLoopAddSource(worker->loop, events, kCFRunLoopDefaultMode);
    CFRunLoopAddSource(worker->loop, worker->command, kCFRunLoopDefaultMode);
    atomic_store(&worker->state, RCF_RAW_RUNNING);
    pthread_mutex_unlock(&worker->gate);
    notify_main(worker);
    CFRunLoopRun();
    return NULL;
}

static int start_thread(RcfPointerWorker *worker) {
    pthread_t thread;
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    pthread_attr_set_qos_class_np(&attributes, QOS_CLASS_USER_INTERACTIVE, 0);
    int result = pthread_create(&thread, &attributes, collect_pointer, worker);
    pthread_attr_destroy(&attributes);
    return result;
}

RcfPointerWorker *rcf_pointer_begin(RcfMacNotify notify) {
    if (CFRunLoopGetCurrent() != CFRunLoopGetMain()) return NULL;

    const void *key = kAXTrustedCheckOptionPrompt;
    const void *value = kCFBooleanTrue;
    CFDictionaryRef options = CFDictionaryCreate(NULL, &key, &value, 1,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    bool trusted = AXIsProcessTrustedWithOptions(options);
    CFRelease(options);
    if (!trusted) return NULL;

    if (router) {
        router->notify = notify;
        atomic_store(&router->notifications_enabled, true);
        pthread_mutex_lock(&router->gate);
        preserve_failure(router);
        router->capture.error = 0;
        router->maximum_consumer_delay = 0;
        pthread_mutex_unlock(&router->gate);
        atomic_store(&router->permitted, true);

        if (atomic_load(&router->state) == RCF_RAW_FAILED) {
            atomic_store(&router->state, RCF_RAW_STARTING);
            if (router->loop) {
                CFRunLoopSourceSignal(router->command);
                CFRunLoopWakeUp(router->loop);
            } else if (start_thread(router)) {
                atomic_store(&router->state, RCF_RAW_FAILED);
                atomic_store(&router->permitted, false);
                return NULL;
            }
        }

        return router;
    }

    RcfPointerWorker *worker = calloc(1, sizeof(*worker));
    if (!worker || pthread_mutex_init(&worker->gate, NULL)) {
        free(worker);
        return NULL;
    }

    worker->notify = notify;
    atomic_init(&worker->state, RCF_RAW_STARTING);
    atomic_init(&worker->permitted, true);
    atomic_init(&worker->notifications_enabled, true);
    CFRunLoopSourceContext wake = {0};
    wake.info = worker;
    wake.perform = deliver_notification;
    worker->wake = CFRunLoopSourceCreate(NULL, 0, &wake);
    if (!worker->wake) {
        pthread_mutex_destroy(&worker->gate);
        free(worker);
        return NULL;
    }

    CFRunLoopAddSource(CFRunLoopGetMain(), worker->wake, kCFRunLoopCommonModes);

    if (start_thread(worker)) {
        CFRunLoopSourceInvalidate(worker->wake);
        CFRelease(worker->wake);
        pthread_mutex_destroy(&worker->gate);
        free(worker);
        return NULL;
    }

    // The router and release guards live with the loaded library, across flight sessions.
    router = worker;
    return worker;
}

int32_t rcf_pointer_activate(RcfPointerWorker *worker, const RcfCaptureConfig *config) {
    if (atomic_load(&worker->state) != RCF_RAW_RUNNING || !worker->loop) return -1;
    pthread_mutex_lock(&worker->gate);
    worker->requested = *config;
    worker->activate = true;
    worker->finish = false;
    worker->through = 0;
    pthread_mutex_unlock(&worker->gate);
    atomic_store(&worker->permitted, true);
    CFRunLoopSourceSignal(worker->command);
    CFRunLoopWakeUp(worker->loop);
    return 0;
}

uint32_t rcf_pointer_state(RcfPointerWorker *worker) { return atomic_load(&worker->state); }

int32_t rcf_pointer_finish(RcfPointerWorker *worker, uint32_t reason) {
    if (atomic_load(&worker->state) != RCF_RAW_RUNNING || !atomic_load(&worker->permitted)) return -1;

    pthread_mutex_lock(&worker->gate);
    worker->finish = true;
    worker->finish_reason = reason;
    pthread_mutex_unlock(&worker->gate);
    CFRunLoopSourceSignal(worker->command);
    CFRunLoopWakeUp(worker->loop);
    return 0;
}

uint32_t rcf_pointer_pump(RcfPointerWorker *worker, RcfMacHandler receiver) {
    if (atomic_load(&worker->state) == RCF_RAW_STARTING) return RCF_INPUT_EMPTY;
    if (atomic_load(&worker->state) != RCF_RAW_RUNNING) return RCF_INPUT_FAILED;
    RcfCaptureEvent *batch = worker->batch;
    pthread_mutex_lock(&worker->gate);
    double copying = rcf_pointer_clock();
    uint32_t count = rcf_capture_read(&worker->capture, batch, 8192, &worker->through, &worker->delivered_sequence);
    double copied = rcf_pointer_clock() - copying;
    if (copied > worker->maximum_copy_time) worker->maximum_copy_time = copied;
    pthread_mutex_unlock(&worker->gate);

    double maximum_delay = 0;
    for (uint32_t index = 0; index < count; ++index) {
        double delay = rcf_pointer_clock() - batch[index].timestamp;
        if (delay > maximum_delay) maximum_delay = delay;
        batch[index].reserved |= 0x80000000u;
        receiver(&batch[index]);
        if (!atomic_load(&worker->permitted)) break;
    }

    pthread_mutex_lock(&worker->gate);
    if (maximum_delay > worker->maximum_consumer_delay) worker->maximum_consumer_delay = maximum_delay;
    pthread_mutex_unlock(&worker->gate);

    return rcf_pointer_pending(worker);
}

uint32_t rcf_pointer_pending(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    uint32_t result = worker->capture.error ? RCF_INPUT_FAILED :
        worker->capture.read < worker->capture.write ? RCF_INPUT_ELIGIBLE : RCF_INPUT_EMPTY;
    pthread_mutex_unlock(&worker->gate);
    return result;
}

double rcf_pointer_boundary(RcfPointerWorker *worker) { return worker->through; }

void rcf_pointer_discard(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    rcf_capture_discard(&worker->capture);
    pthread_mutex_unlock(&worker->gate);
}

uint32_t rcf_pointer_initial_key(RcfPointerWorker *worker, uint32_t code) {
    pthread_mutex_lock(&worker->gate);
    uint32_t result = code < RCF_CAPTURE_CONTROLS && worker->capture.initial[code];
    pthread_mutex_unlock(&worker->gate);
    return result;
}

uint32_t rcf_pointer_validate(RcfPointerWorker *worker) {
    if (atomic_load(&worker->state) == RCF_RAW_RUNNING && worker->tap && !CGEventTapIsEnabled(worker->tap)) {
        atomic_store(&worker->permitted, false);
        pthread_mutex_lock(&worker->gate);
        rcf_capture_revoke(&worker->capture, 4);
        preserve_failure(worker);
        pthread_mutex_unlock(&worker->gate);
        atomic_store(&worker->state, RCF_RAW_FAILED);
    }
    if (!atomic_load(&worker->permitted)) wake_command(worker);
    return atomic_load(&worker->state) == RCF_RAW_RUNNING && atomic_load(&worker->permitted);
}

uint32_t rcf_pointer_error(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    uint32_t result = worker->capture.error;
    pthread_mutex_unlock(&worker->gate);
    return result;
}

double rcf_pointer_started_at(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    double result = worker->capture.started_at;
    pthread_mutex_unlock(&worker->gate);
    return result;
}

uint32_t rcf_pointer_guard(RcfPointerWorker *worker, RcfCaptureEvent event) {
    pthread_mutex_lock(&worker->gate);
    if (worker->tap && CGEventTapIsEnabled(worker->tap)) {
        pthread_mutex_unlock(&worker->gate);
        return 0;
    }

    bool starting = atomic_load(&worker->state) == RCF_RAW_STARTING && !worker->capture.active;
    if (!starting) atomic_store(&worker->permitted, false);
    if (worker->capture.active) rcf_capture_revoke(&worker->capture, 4);
    preserve_failure(worker);
    bool consumed = rcf_capture_route(&worker->capture, event, rcf_pointer_clock(), false);
    pthread_mutex_unlock(&worker->gate);
    if (!starting) atomic_store(&worker->state, RCF_RAW_FAILED);
    if (!starting) wake_command(worker);
    return consumed;
}

uint32_t rcf_pointer_guards_pending(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    uint32_t count = worker->capture.wheel_owned || worker->capture.wheel_ended;

    for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code)
        count += (worker->capture.controls[code].owner == RCF_OWNER_NATIVE) + worker->capture.controls[code].release_debt;

    pthread_mutex_unlock(&worker->gate);
    return count;
}

int32_t rcf_pointer_end(RcfPointerWorker *worker) {
    atomic_store(&worker->permitted, false);
    worker->notify = NULL;
    atomic_store(&worker->notifications_enabled, false);
    pthread_mutex_lock(&worker->gate);
    worker->activate = false;
    worker->finish = false;
    preserve_failure(worker);
    if (worker->has_failure && worker->last_failure.config.session == worker->capture.config.session)
        worker->last_failure.cleaned = true;
    rcf_capture_revoke(&worker->capture, 0);
    pthread_mutex_unlock(&worker->gate);
    wake_command(worker);
    return 0;
}

static int format_snapshot(const RcfWorkerSnapshot *value, char *destination, uint32_t capacity) {
    uint64_t quarantine[3] = {0}, native[3] = {0}, appkit[3] = {0};
    char guards[768] = {0};
    size_t used = 0;
    uint32_t failure_code = value->failure.kind == RCF_CAPTURE_BUTTON ? value->failure.code + 128 : value->failure.code;
    RcfCaptureControl failed_control = {0};
    if (failure_code < RCF_CAPTURE_CONTROLS) failed_control = value->controls[failure_code];

    for (uint32_t code = 0; code < RCF_CAPTURE_CONTROLS; ++code) {
        uint64_t bit = 1ull << (code % 64);
        const RcfCaptureControl *control = &value->controls[code];
        if (value->quarantine[code]) quarantine[code / 64] |= bit;
        if (control->owner == RCF_OWNER_NATIVE) native[code / 64] |= bit;
        if (control->owner == RCF_OWNER_APPKIT) appkit[code / 64] |= bit;

        if ((control->owner != RCF_OWNER_HOST || control->release_debt) && used < sizeof(guards) - 1) {
            int written = snprintf(guards + used, sizeof(guards) - used, "%s%u:%u:%llu:%llu:%u",
                used ? "," : "", code, control->owner, (unsigned long long)control->cycle,
                (unsigned long long)control->press_id, control->release_debt);

            if (written > 0) {
                size_t remaining = sizeof(guards) - used - 1;
                used += (size_t)written > remaining ? remaining : (size_t)written;
            }
        }
    }

    return snprintf(destination, capacity,
        "ABI=5 session=%llu entry=%u/%llu/%llx terminal=%u sequence=%llu replay=%llu frontier=%.6f active=%u state=%u permitted=%u error=%u queue=%llu/%llu high-water=%u source-regressions=%u source-delay=%.6f consumer-delay=%.6f producer-lock-wait=%.6f batch-copy=%.6f failure=%u/%u/%u/%llu/%.6f/%llu phases=%u/%u failed-control=%u/%u/%u/%u/%u/%u cleanup=%u quarantine=%llx/%llx/%llx native-owned=%llx/%llx/%llx appkit-owned=%llx/%llx/%llx guards=%s",
        (unsigned long long)value->config.session, value->config.entry_code,
        (unsigned long long)value->config.entry_source_time, (unsigned long long)value->config.entry_modifiers,
        value->terminal, (unsigned long long)value->sequence, (unsigned long long)value->replay,
        value->frontier, value->active, value->state, value->permitted, value->error,
        (unsigned long long)value->read, (unsigned long long)value->write, value->high_water, value->regressions,
        value->source_delay, value->consumer_delay, value->lock_wait, value->copy_time,
        value->failure.kind, value->failure.code, value->failure.target_window,
        (unsigned long long)value->failure.sequence, value->failure.timestamp,
        (unsigned long long)value->failure.source_time, value->failure.phase, value->failure.momentum,
        failed_control.owner, failed_control.physical, failed_control.observed, failed_control.sampled,
        failed_control.repaired, failed_control.release_debt, value->cleaned,
        (unsigned long long)quarantine[0], (unsigned long long)quarantine[1], (unsigned long long)quarantine[2],
        (unsigned long long)native[0], (unsigned long long)native[1], (unsigned long long)native[2],
        (unsigned long long)appkit[0], (unsigned long long)appkit[1], (unsigned long long)appkit[2], guards);
}

uint32_t rcf_pointer_diagnostics(RcfPointerWorker *worker, char *destination, uint32_t capacity) {
    if (!destination || !capacity) return 0;
    RcfWorkerSnapshot current, failure;
    pthread_mutex_lock(&worker->gate);
    snapshot(worker, &current);
    bool failed = worker->has_failure;
    if (failed) failure = worker->last_failure;
    pthread_mutex_unlock(&worker->gate);

    int count = format_snapshot(&current, destination, capacity);
    if (count < 0) return 0;
    uint32_t used = (uint32_t)count < capacity ? (uint32_t)count : capacity - 1;

    if (failed && used + 15 < capacity) {
        memcpy(destination + used, " last-failure{", 14);
        used += 14;
        count = format_snapshot(&failure, destination + used, capacity - used);
        if (count > 0) used += (uint32_t)count < capacity - used ? (uint32_t)count : capacity - used - 1;
        if (used + 1 < capacity) destination[used++] = '}';
        destination[used] = 0;
    }

    return used;
}
