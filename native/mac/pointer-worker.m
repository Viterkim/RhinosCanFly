#include <CoreGraphics/CoreGraphics.h>
#include <CoreFoundation/CoreFoundation.h>
#include <ApplicationServices/ApplicationServices.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <pthread/qos.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <unistd.h>
#include "pointer-worker.h"

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
    RcfCapture capture;
    RcfCaptureEvent batch[8192];
};

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
        rcf_capture_revoke(&worker->capture, type == kCGEventTapDisabledByTimeout ? 4 : 5);
        update_clock(worker);
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

    pthread_mutex_lock(&worker->gate);

    bool permitted = atomic_load(&worker->permitted);
    if (value.kind == RCF_CAPTURE_BUTTON || value.kind == RCF_CAPTURE_WHEEL) {
        uint32_t target = (uint32_t)CGEventGetIntegerValueField(event, kCGMouseEventWindowUnderMousePointer);
        value.target_window = target;
        if (target && target != worker->capture.config.window && worker->capture.active) {
            permitted = false;
            atomic_store(&worker->permitted, false);
            rcf_capture_revoke(&worker->capture, 8);
        }
    }

    if (value.kind != RCF_CAPTURE_MODIFIER)
        rcf_capture_flags(&worker->capture, value.modifiers, value.source_time, now, permitted);

    bool consumed = rcf_capture_route(&worker->capture, value, now, permitted);
    bool failed = worker->capture.error != 0;
    update_clock(worker);
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

            rcf_capture_begin(&worker->capture, &worker->requested, held, rcf_pointer_clock());
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
        router->capture.error = 0;
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
    uint64_t sequence;
    pthread_mutex_lock(&worker->gate);
    uint32_t count = rcf_capture_read(&worker->capture, batch, 8192, &worker->through, &sequence);
    pthread_mutex_unlock(&worker->gate);

    for (uint32_t index = 0; index < count; ++index) {
        batch[index].reserved |= 0x80000000u;
        receiver(&batch[index]);
        if (!atomic_load(&worker->permitted)) break;
    }

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
    rcf_capture_revoke(&worker->capture, 0);
    pthread_mutex_unlock(&worker->gate);
    wake_command(worker);
    return 0;
}
