#include <CoreGraphics/CoreGraphics.h>
#include <CoreFoundation/CoreFoundation.h>
#import <Foundation/Foundation.h>
#include <pthread.h>
#include <pthread/qos.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdlib.h>
#include <unistd.h>
#include "pointer-worker.h"

enum { EVENT_CAPACITY = 8192, TRANSFER_BUDGET = 64 };

typedef struct {
    RcfRelativeMotion motion;
    CGEventType type;
    int64_t code;
    uint64_t sequence;
    uint64_t repair_id;
    bool recovery;
} RcfPointerEvent;

struct RcfPointerWorker {
    pthread_mutex_t gate;
    _Atomic uint32_t state;
    _Atomic bool stopping, finished, notified;
    CFRunLoopRef loop;
    CFRunLoopSourceRef stop_source;
    CFRunLoopSourceRef notify_source;
    RcfMacNotify notify;
    double started_at, last_timestamp, repair_barrier;
    uint64_t read, write;
    uint64_t sequence;
    uint64_t discard_through;
    uint64_t blocked_sequence;
    double blocked_seconds, checked_at;
    bool stall_granted;
    double release_candidate[133];
    uint64_t release_sequence[133];
    uint64_t control_sequence[133];
    double repaired_at[133];
    uint64_t repair_sequence[133];
    _Atomic uint32_t error;
    bool initial_keys[133];
    RcfPointerEvent events[EVENT_CAPACITY];
};

static void notify_work(RcfPointerWorker *worker) {
    if (!atomic_exchange(&worker->notified, true) && worker->notify_source) {
        CFRunLoopSourceSignal(worker->notify_source);
        CFRunLoopWakeUp(worker->loop);
    }
}

static void deliver_notification(void *context) {
    RcfPointerWorker *worker = context;
    @autoreleasepool { worker->notify(); }
}

static void fail_worker(RcfPointerWorker *worker) {
    atomic_store(&worker->state, RCF_RAW_FAILED);
    notify_work(worker);
    CFRunLoopStop(CFRunLoopGetCurrent());
}

static bool is_movement(CGEventType type) {
    return type == kCGEventMouseMoved || type == kCGEventLeftMouseDragged ||
        type == kCGEventRightMouseDragged || type == kCGEventOtherMouseDragged;
}

static int64_t event_code(CGEventRef event, CGEventType type) {
    switch (type) {
        case kCGEventKeyDown: case kCGEventKeyUp: case kCGEventFlagsChanged:
            return CGEventGetIntegerValueField(event, kCGKeyboardEventKeycode);
        case kCGEventLeftMouseDown: case kCGEventLeftMouseUp:
        case kCGEventRightMouseDown: case kCGEventRightMouseUp:
        case kCGEventOtherMouseDown: case kCGEventOtherMouseUp:
            return CGEventGetIntegerValueField(event, kCGMouseEventButtonNumber);
        default: return 0;
    }
}

static int control_code(CGEventType type, int64_t code) {
    switch (type) {
        case kCGEventKeyDown: case kCGEventKeyUp: case kCGEventFlagsChanged:
            return code >= 0 && code < 128 ? (int)code : -1;
        case kCGEventLeftMouseDown: case kCGEventLeftMouseUp:
        case kCGEventRightMouseDown: case kCGEventRightMouseUp:
        case kCGEventOtherMouseDown: case kCGEventOtherMouseUp:
            return code >= 0 && code < 5 ? 128 + (int)code : -1;
        default: return -1;
    }
}

static bool control_is_down(CGEventRef event, CGEventType type, int code) {
    if (type == kCGEventFlagsChanged) {
        uint64_t mask = 0;
        switch (code) {
            case 59: mask = 0x1; break;
            case 56: mask = 0x2; break;
            case 60: mask = 0x4; break;
            case 55: mask = 0x8; break;
            case 54: mask = 0x10; break;
            case 58: mask = 0x20; break;
            case 61: mask = 0x40; break;
            case 57: mask = 0x80; break;
            case 62: mask = 0x2000; break;
            case 63: mask = 0x800000; break;
        }
        return (CGEventGetFlags(event) & mask) != 0;
    }
    return type == kCGEventKeyDown || type == kCGEventLeftMouseDown ||
        type == kCGEventRightMouseDown || type == kCGEventOtherMouseDown;
}

static bool repaired_release(RcfPointerWorker *worker, CGEventRef event, CGEventType type, int64_t code, double timestamp) {
    int control = control_code(type, code);
    return control >= 0 && !control_is_down(event, type, control) && worker->repaired_at[control] > 0 &&
        timestamp <= worker->repaired_at[control];
}

static CGEventRef pointer_event(CGEventTapProxy proxy, CGEventType type, CGEventRef event, void *context) {
    (void)proxy;
    RcfPointerWorker *worker = context;

    if (atomic_load(&worker->stopping) || atomic_load(&worker->state) != RCF_RAW_RUNNING) return event;
    if (type == kCGEventTapDisabledByTimeout || type == kCGEventTapDisabledByUserInput) {
        fail_worker(worker);
        return event;
    }

    switch (type) {
        case kCGEventMouseMoved: case kCGEventLeftMouseDragged:
        case kCGEventRightMouseDragged: case kCGEventOtherMouseDragged:
        case kCGEventKeyDown: case kCGEventKeyUp: case kCGEventFlagsChanged:
        case kCGEventLeftMouseDown: case kCGEventLeftMouseUp:
        case kCGEventRightMouseDown: case kCGEventRightMouseUp:
        case kCGEventOtherMouseDown: case kCGEventOtherMouseUp:
        case kCGEventScrollWheel: break;
        default: return event;
    }

    RcfPointerEvent packet = {0};
    packet.motion.timestamp = (double)CGEventGetTimestamp(event) / 1e9;
    packet.type = type;
    packet.code = event_code(event, type);
    if (packet.motion.timestamp < worker->started_at) return event;

    if (is_movement(type)) {
        packet.motion.dx = (double)CGEventGetIntegerValueField(event, kCGEventUnacceleratedPointerMovementX);
        packet.motion.dy = (double)CGEventGetIntegerValueField(event, kCGEventUnacceleratedPointerMovementY);
    }

    pthread_mutex_lock(&worker->gate);
    bool late_release = repaired_release(worker, event, type, packet.code, packet.motion.timestamp);
    bool valid = late_release || (packet.motion.timestamp >= worker->last_timestamp &&
        packet.motion.timestamp >= worker->repair_barrier);
    if (!valid) worker->error = 10;

    RcfPointerEvent *previous = worker->write > worker->read ?
        &worker->events[(worker->write - 1) % EVENT_CAPACITY] : NULL;
    bool merge = valid && is_movement(type) && previous && is_movement(previous->type) &&
        previous->sequence > worker->discard_through;
    if (valid && !merge && worker->write - worker->read >= EVENT_CAPACITY) {
        valid = false;
        worker->error = 11;
    }
    if (valid) {
        packet.sequence = ++worker->sequence;
        int control = control_code(type, packet.code);
        if (late_release) packet.repair_id = worker->repair_sequence[control];
        if (merge) {
            packet.motion.dx += previous->motion.dx;
            packet.motion.dy += previous->motion.dy;
            *previous = packet;
        } else {
            worker->events[worker->write++ % EVENT_CAPACITY] = packet;
        }
        if (packet.motion.timestamp > worker->last_timestamp) worker->last_timestamp = packet.motion.timestamp;

        if (control >= 0) {
            worker->control_sequence[control] = packet.sequence;
            worker->release_candidate[control] = 0;
            if (control_is_down(event, type, control)) worker->repaired_at[control] = 0;
        }
    }
    pthread_mutex_unlock(&worker->gate);

    if (valid) notify_work(worker);
    else fail_worker(worker);
    return event;
}

static void stop_loop(void *context) {
    RcfPointerWorker *worker = context;
    if (atomic_load(&worker->stopping)) CFRunLoopStop(CFRunLoopGetCurrent());
}

static CGEventTapInformation *snapshot_taps(uint32_t *count) {
    if (CGGetEventTapList(0, NULL, count) != kCGErrorSuccess || *count == UINT32_MAX) return NULL;

    uint32_t capacity = *count + 1;
    CGEventTapInformation *taps = calloc(capacity, sizeof(*taps));
    if (!taps) return NULL;

    if (CGGetEventTapList(capacity, taps, count) != kCGErrorSuccess || *count > capacity) {
        free(taps);
        return NULL;
    }

    return taps;
}

static CFMachPortRef create_ordered_tap(RcfPointerWorker *worker) {
    uint32_t before_count = 0, after_count = 0;
    CGEventTapInformation *before = snapshot_taps(&before_count);
    if (!before) { worker->error = 1; return NULL; }

    CGEventMask mask = CGEventMaskBit(kCGEventMouseMoved) | CGEventMaskBit(kCGEventLeftMouseDragged) |
        CGEventMaskBit(kCGEventRightMouseDragged) | CGEventMaskBit(kCGEventOtherMouseDragged) |
        CGEventMaskBit(kCGEventLeftMouseDown) | CGEventMaskBit(kCGEventLeftMouseUp) |
        CGEventMaskBit(kCGEventRightMouseDown) | CGEventMaskBit(kCGEventRightMouseUp) |
        CGEventMaskBit(kCGEventOtherMouseDown) | CGEventMaskBit(kCGEventOtherMouseUp) |
        CGEventMaskBit(kCGEventScrollWheel) | CGEventMaskBit(kCGEventKeyDown) |
        CGEventMaskBit(kCGEventKeyUp) | CGEventMaskBit(kCGEventFlagsChanged);

    // A passive tap can arrive after AppKit has already applied the next key or button.
    // Pass events unchanged, but record motion before Rhino receives that event stream.
    CFMachPortRef tap = CGEventTapCreateForPid(getpid(), kCGHeadInsertEventTap,
        kCGEventTapOptionDefault, mask, pointer_event, worker);
    if (!tap) worker->error = 2;
    CGEventTapInformation *after = tap ? snapshot_taps(&after_count) : NULL;
    unsigned added = 0;
    bool complete = false;

    if (after) {
        for (uint32_t index = 0; index < after_count; ++index) {
            CGEventTapInformation info = after[index];
            if (info.tappingProcess != getpid() || info.processBeingTapped != getpid()) continue;

            bool existed = false;
            for (uint32_t previous = 0; previous < before_count; ++previous)
                if (before[previous].eventTapID == info.eventTapID) { existed = true; break; }

            if (!existed) {
                ++added;
                complete = info.enabled && info.options == kCGEventTapOptionDefault &&
                    (info.eventsOfInterest & mask) == mask;
            }
        }
    }

    free(before);
    free(after);

    // macOS can silently remove keyboard events from an otherwise successful tap.
    if (tap && (added != 1 || !complete)) {
        worker->error = 3;
        CFMachPortInvalidate(tap);
        CFRelease(tap);
        return NULL;
    }

    return tap;
}

static void *collect_pointer(void *context) {
    RcfPointerWorker *worker = context;
    pthread_setname_np("RhinosCanFly.pointer");

    CFMachPortRef tap = create_ordered_tap(worker);
    CFRunLoopSourceRef source = tap ? CFMachPortCreateRunLoopSource(NULL, tap, 0) : NULL;
    CFRunLoopSourceContext stop_context = {0};
    stop_context.info = worker;
    stop_context.perform = stop_loop;
    CFRunLoopSourceRef stop = CFRunLoopSourceCreate(NULL, 0, &stop_context);
    CFRunLoopSourceContext notify_context = {0};
    notify_context.info = worker;
    notify_context.perform = deliver_notification;
    CFRunLoopSourceRef notification = CFRunLoopSourceCreate(NULL, 0, &notify_context);

    if (source && stop && notification && CGEventTapIsEnabled(tap)) {
        CFRunLoopRef loop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(loop, source, kCFRunLoopDefaultMode);
        CFRunLoopAddSource(loop, stop, kCFRunLoopDefaultMode);
        CFRunLoopAddSource(loop, notification, kCFRunLoopDefaultMode);

        @autoreleasepool { worker->started_at = NSProcessInfo.processInfo.systemUptime; }
        for (unsigned code = 0; code < 133; ++code)
            worker->initial_keys[code] = code < 128 ? CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState, code) :
                CGEventSourceButtonState(kCGEventSourceStateCombinedSessionState, code - 128);

        pthread_mutex_lock(&worker->gate);
        worker->loop = loop;
        worker->stop_source = stop;
        worker->notify_source = notification;
        pthread_mutex_unlock(&worker->gate);

        uint32_t expected = RCF_RAW_STARTING;
        if (!atomic_load(&worker->stopping) &&
            atomic_compare_exchange_strong(&worker->state, &expected, RCF_RAW_RUNNING)) {
            notify_work(worker);
            CFRunLoopRun();
        }

        pthread_mutex_lock(&worker->gate);
        worker->loop = NULL;
        worker->stop_source = NULL;
        worker->notify_source = NULL;
        pthread_mutex_unlock(&worker->gate);

        CFRunLoopRemoveSource(loop, stop, kCFRunLoopDefaultMode);
        CFRunLoopRemoveSource(loop, source, kCFRunLoopDefaultMode);
        CFRunLoopRemoveSource(loop, notification, kCFRunLoopDefaultMode);
    } else {
        if (!worker->error) worker->error = 4;
        atomic_store(&worker->state, RCF_RAW_FAILED);
    }

    if (source) { CFRunLoopSourceInvalidate(source); CFRelease(source); }
    if (stop) { CFRunLoopSourceInvalidate(stop); CFRelease(stop); }
    if (notification) { CFRunLoopSourceInvalidate(notification); CFRelease(notification); }
    if (tap) { CFMachPortInvalidate(tap); CFRelease(tap); }
    if (atomic_load(&worker->state) != RCF_RAW_FAILED) atomic_store(&worker->state, RCF_RAW_STOPPED);

    @autoreleasepool { worker->notify(); }
    // After this acknowledgement the UI may free the context and callback roots.
    atomic_store_explicit(&worker->finished, true, memory_order_release);
    return NULL;
}

RcfPointerWorker *rcf_pointer_begin(RcfMacNotify notify) {
    if (!notify) return NULL;
    RcfPointerWorker *worker = calloc(1, sizeof(*worker));
    if (!worker) return NULL;
    if (pthread_mutex_init(&worker->gate, NULL) != 0) { free(worker); return NULL; }

    worker->notify = notify;
    atomic_init(&worker->state, RCF_RAW_STARTING);
    atomic_init(&worker->stopping, false);
    atomic_init(&worker->finished, false);
    atomic_init(&worker->notified, false);
    atomic_init(&worker->error, 0);

    pthread_attr_t attributes;
    if (pthread_attr_init(&attributes) != 0) {
        pthread_mutex_destroy(&worker->gate);
        free(worker);
        return NULL;
    }

    pthread_t thread;
    int result = pthread_attr_setdetachstate(&attributes, PTHREAD_CREATE_DETACHED);
    if (result == 0) result = pthread_attr_set_qos_class_np(&attributes, QOS_CLASS_USER_INTERACTIVE, 0);
    if (result == 0) result = pthread_create(&thread, &attributes, collect_pointer, worker);
    pthread_attr_destroy(&attributes);

    if (result != 0) {
        pthread_mutex_destroy(&worker->gate);
        free(worker);
        return NULL;
    }
    return worker;
}

uint32_t rcf_pointer_state(RcfPointerWorker *worker) {
    return atomic_load(&worker->state);
}

int32_t rcf_pointer_drain(RcfPointerWorker *worker, CGEventRef event, RcfRelativeMotionHandler receiver, RcfPointerRelease release) {
    atomic_store(&worker->notified, false);
    uint32_t state = atomic_load(&worker->state);
    if (state != RCF_RAW_RUNNING) return state == RCF_RAW_STARTING ? 0 : -1;

    if (!event || !receiver) {
        worker->error = 6;
        atomic_store(&worker->state, RCF_RAW_FAILED);
        return -1;
    }

    CGEventType type = CGEventGetType(event);
    double through = (double)CGEventGetTimestamp(event) / 1e9;
    if (through < worker->started_at) return 0;
    int64_t code = event_code(event, type);
    bool movement = is_movement(type);

    pthread_mutex_lock(&worker->gate);
    uint64_t end = worker->write;
    bool late_release = false;
    if (!movement && through <= worker->repair_barrier) {
        // A newer press may already have replaced the producer's repair history.
        for (uint64_t index = worker->read; index < end; ++index) {
            RcfPointerEvent packet = worker->events[index % EVENT_CAPACITY];
            if (!packet.recovery && packet.type == type && packet.code == code && packet.motion.timestamp == through) {
                late_release = packet.repair_id != 0;
                break;
            }
        }
    }
    pthread_mutex_unlock(&worker->gate);

    bool matched = false, discarded_wheel = false;
    while (worker->read < end) {
        pthread_mutex_lock(&worker->gate);
        RcfPointerEvent packet = worker->events[worker->read % EVENT_CAPACITY];
        bool motion = is_movement(packet.type);
        if (packet.recovery) {
            ++worker->read;
            pthread_mutex_unlock(&worker->gate);
            release((uint32_t)packet.code, packet.motion.timestamp);
            if (atomic_load(&worker->state) != RCF_RAW_RUNNING || atomic_load(&worker->stopping)) return -1;
            continue;
        }

        if ((!late_release && packet.motion.timestamp > through) || (movement && !motion)) {
            pthread_mutex_unlock(&worker->gate);
            if (!motion && !packet.repair_id && packet.motion.timestamp < through) {
                worker->error = 5;
                atomic_store(&worker->state, RCF_RAW_FAILED);
                return -1;
            }
            break;
        }

        if (!motion && (packet.type != type || packet.code != code || packet.motion.timestamp != through)) {
            pthread_mutex_unlock(&worker->gate);
            break;
        }

        bool deliver = packet.sequence > worker->discard_through;
        if (!motion && packet.repair_id) {
            int control = control_code(packet.type, packet.code);
            if (control >= 0 && worker->repair_sequence[control] == packet.repair_id) {
                worker->repaired_at[control] = 0;
                worker->repair_sequence[control] = 0;
            }
        }

        ++worker->read;
        pthread_mutex_unlock(&worker->gate);

        if (motion) {
            if (deliver && (packet.motion.dx || packet.motion.dy)) receiver(&packet.motion);
        } else {
            // AppKit applies this transition next. Leave all following motion queued, even at the same timestamp.
            matched = true;
            discarded_wheel = packet.type == kCGEventScrollWheel && !deliver;
            break;
        }
    }

    if (!movement && !matched) {
        worker->error = 5;
        atomic_store(&worker->state, RCF_RAW_FAILED);
        return -1;
    }

    return atomic_load(&worker->state) == RCF_RAW_RUNNING ? (discarded_wheel ? 1 : 0) : -1;
}

uint32_t rcf_pointer_pump(RcfPointerWorker *worker, RcfRelativeMotionHandler receiver, RcfPointerRelease release) {
    atomic_store(&worker->notified, false);
    uint32_t state = atomic_load(&worker->state);
    if (state != RCF_RAW_RUNNING) return state == RCF_RAW_STARTING ? RCF_INPUT_EMPTY : RCF_INPUT_FAILED;

    // Limit callbacks per UI turn. Adjacent packets already share one motion record.
    for (unsigned delivered = 0; delivered < TRANSFER_BUDGET; ++delivered) {
        pthread_mutex_lock(&worker->gate);
        if (worker->read == worker->write) {
            pthread_mutex_unlock(&worker->gate);
            return RCF_INPUT_EMPTY;
        }

        RcfPointerEvent packet = worker->events[worker->read % EVENT_CAPACITY];
        if (!is_movement(packet.type) && !packet.recovery) {
            pthread_mutex_unlock(&worker->gate);
            return RCF_INPUT_BOUNDARY;
        }

        bool deliver = packet.sequence > worker->discard_through;
        ++worker->read;
        pthread_mutex_unlock(&worker->gate);
        if (packet.recovery) release((uint32_t)packet.code, packet.motion.timestamp);
        else if (deliver && (packet.motion.dx || packet.motion.dy)) receiver(&packet.motion);

        if (atomic_load(&worker->state) != RCF_RAW_RUNNING || atomic_load(&worker->stopping))
            return RCF_INPUT_FAILED;
    }

    return rcf_pointer_pending(worker);
}

uint32_t rcf_pointer_pending(RcfPointerWorker *worker) {
    uint32_t state = atomic_load(&worker->state);
    if (state != RCF_RAW_RUNNING) return state == RCF_RAW_STARTING ? RCF_INPUT_EMPTY : RCF_INPUT_FAILED;

    pthread_mutex_lock(&worker->gate);
    uint32_t progress = worker->read == worker->write ? RCF_INPUT_EMPTY :
        (is_movement(worker->events[worker->read % EVENT_CAPACITY].type) ||
         worker->events[worker->read % EVENT_CAPACITY].recovery) ? RCF_INPUT_ELIGIBLE : RCF_INPUT_BOUNDARY;
    pthread_mutex_unlock(&worker->gate);
    return progress;
}

double rcf_pointer_boundary(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    double timestamp = 0;
    for (uint64_t index = worker->read; index < worker->write; ++index) {
        RcfPointerEvent packet = worker->events[index % EVENT_CAPACITY];
        if (!is_movement(packet.type)) { timestamp = packet.motion.timestamp; break; }
    }
    pthread_mutex_unlock(&worker->gate);
    return timestamp;
}

void rcf_pointer_discard(RcfPointerWorker *worker) {
    pthread_mutex_lock(&worker->gate);
    // Keep control acknowledgements, but discard old motion and wheel payloads.
    worker->discard_through = worker->sequence;
    pthread_mutex_unlock(&worker->gate);
}

static bool reconcile_release(RcfPointerWorker *worker, uint32_t code, uint64_t sequence, bool held, double now) {
    pthread_mutex_lock(&worker->gate);
    bool running = atomic_load(&worker->state) == RCF_RAW_RUNNING && !atomic_load(&worker->stopping);
    bool idle = worker->read == worker->write && worker->sequence == sequence;
    bool accepted = false;

    if (!running || held) worker->release_candidate[code] = 0;
    else if (!worker->release_candidate[code] || worker->release_sequence[code] != worker->control_sequence[code]) {
        worker->release_candidate[code] = now;
        worker->release_sequence[code] = worker->control_sequence[code];
    } else if (idle && now - worker->release_candidate[code] >= 0.05) {
        // Drain earlier input first. Mouse movement alone mustn't postpone a missing key release forever.
        RcfPointerEvent packet = {0};
        packet.type = code < 128 ? kCGEventKeyUp : kCGEventOtherMouseUp;
        packet.code = code;
        packet.recovery = true;
        packet.motion.timestamp = now;
        packet.sequence = ++worker->sequence;
        packet.repair_id = packet.sequence;
        worker->events[worker->write++ % EVENT_CAPACITY] = packet;
        worker->repaired_at[code] = now;
        worker->repair_sequence[code] = packet.sequence;
        worker->repair_barrier = now;
        worker->release_candidate[code] = 0;
        accepted = true;
    } else if (now - worker->release_candidate[code] >= 0.25) {
        worker->error = 9;
        atomic_store(&worker->state, RCF_RAW_FAILED);
    }

    pthread_mutex_unlock(&worker->gate);
    return accepted;
}

void rcf_pointer_reconcile(RcfPointerWorker *worker, uint32_t code) {
    if (code >= 133 || atomic_load(&worker->state) != RCF_RAW_RUNNING) return;

    pthread_mutex_lock(&worker->gate);
    uint64_t sequence = worker->sequence;
    pthread_mutex_unlock(&worker->gate);

    bool held = code < 128 ? CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState, code) :
        CGEventSourceButtonState(kCGEventSourceStateCombinedSessionState, code - 128);
    reconcile_release(worker, code, sequence, held, NSProcessInfo.processInfo.systemUptime);
}

uint32_t rcf_pointer_validate(RcfPointerWorker *worker, double now, bool tracking) {
    if (atomic_load(&worker->state) != RCF_RAW_RUNNING) return 0;

    pthread_mutex_lock(&worker->gate);
    RcfPointerEvent *head = worker->read < worker->write ? &worker->events[worker->read % EVENT_CAPACITY] : NULL;
    bool blocked = head && !is_movement(head->type) && !head->recovery;
    double elapsed = now - worker->checked_at;

    // Give a stalled UI one fresh chance per boundary, even when later turns stay slow.
    if (!blocked || head->sequence != worker->blocked_sequence || elapsed < 0) {
        worker->blocked_seconds = 0;
        worker->stall_granted = false;
    } else if (elapsed > 0.1 && !worker->stall_granted) {
        worker->blocked_seconds = 0;
        worker->stall_granted = true;
    } else worker->blocked_seconds += elapsed > 0.1 ? 0.1 : elapsed;

    worker->blocked_sequence = blocked ? head->sequence : 0;
    worker->checked_at = now;
    bool failed = tracking || worker->blocked_seconds >= 0.25;
    pthread_mutex_unlock(&worker->gate);

    if (failed) {
        worker->error = tracking ? 8 : 7;
        atomic_store(&worker->state, RCF_RAW_FAILED);
    }
    return !failed;
}

uint32_t rcf_pointer_initial_key(RcfPointerWorker *worker, uint32_t code) {
    return atomic_load(&worker->state) == RCF_RAW_RUNNING && code < 133 && worker->initial_keys[code];
}

uint32_t rcf_pointer_error(RcfPointerWorker *worker) { return worker->error; }

double rcf_pointer_started_at(RcfPointerWorker *worker) {
    return atomic_load(&worker->state) == RCF_RAW_RUNNING ? worker->started_at : 0;
}

int32_t rcf_pointer_end(RcfPointerWorker *worker) {
    atomic_store(&worker->stopping, true);
    if (!atomic_load_explicit(&worker->finished, memory_order_acquire)) {
        atomic_store(&worker->state, RCF_RAW_STOPPING);
        pthread_mutex_lock(&worker->gate);
        if (worker->stop_source) {
            CFRunLoopSourceSignal(worker->stop_source);
            CFRunLoopWakeUp(worker->loop);
        }
        pthread_mutex_unlock(&worker->gate);
        return -1;
    }

    pthread_mutex_destroy(&worker->gate);
    free(worker);
    return 0;
}
