#import <AppKit/AppKit.h>
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include "../../native/mac/pointer-worker.m"
#include "../../native/mac/raw.m"
#include "../../native/mac/input.m"

static unsigned records, notifications;
static double total;
static pthread_t producer;
static bool join_producer;
static unsigned key_downs, key_ups, wheels, moves;

static uint32_t receive_event(const RcfMacEvent *event) {
    assert([NSThread isMainThread]);
    assert(event->reserved & 0x80000000u);
    ++records;

    if (event->kind == RCF_CAPTURE_MOVE) {
        total += event->dx;
        ++moves;
    }

    if (event->kind == RCF_CAPTURE_KEY && event->code == 13) {
        if (event->down) ++key_downs;
        else ++key_ups;
    }

    if (event->kind == RCF_CAPTURE_WHEEL) ++wheels;

    if (join_producer) {
        join_producer = false;
        assert(pthread_join(producer, NULL) == 0);
    }

    return 1;
}

static void notify(void) {
    assert([NSThread isMainThread]);
    ++notifications;
}

static void ignored_motion(const RcfRelativeMotion *motion) { (void)motion; }

static CGEventRef movement(int64_t dx) {
    CGEventRef event = CGEventCreateMouseEvent(NULL, kCGEventMouseMoved, CGPointZero, kCGMouseButtonLeft);
    assert(event);
    CGEventSetIntegerValueField(event, kCGEventUnacceleratedPointerMovementX, dx);
    return event;
}

static void *produce(void *context) {
    RcfPointerWorker *worker = context;
    CGEventRef event = movement(1);

    for (unsigned index = 0; index < 20000; ++index) {
        CGEventSetTimestamp(event, (uint64_t)(rcf_pointer_clock() * 1e9));
        assert(pointer_event(NULL, kCGEventMouseMoved, event, worker) == NULL);
    }

    CFRelease(event);
    return NULL;
}

static void adapter_checks(void) {
    RcfPointerWorker *worker = calloc(1, sizeof(*worker));
    assert(worker && pthread_mutex_init(&worker->gate, NULL) == 0);
    atomic_init(&worker->state, RCF_RAW_RUNNING);
    atomic_init(&worker->permitted, true);
    atomic_init(&worker->notifications_enabled, true);
    CFRunLoopSourceContext context = {0};
    context.info = worker;
    context.perform = deliver_notification;
    worker->wake = CFRunLoopSourceCreate(NULL, 0, &context);
    assert(worker->wake);
    worker->notify = notify;
    bool physical_keys[RCF_CAPTURE_CONTROLS] = {0};
    RcfCaptureConfig config = {0};
    config.session = 1;
    config.configured[13] = 1;
    rcf_capture_begin(&worker->capture, &config, physical_keys, rcf_pointer_clock());

    CGEventRef event = movement(7);
    assert(pointer_event(NULL, kCGEventMouseMoved, event, worker) == NULL);
    CFRelease(event);
    assert(rcf_pointer_pump(worker, receive_event) == RCF_INPUT_EMPTY);
    assert(records == 2 && total == 7);

    CGEventRef wheel = CGEventCreateScrollWheelEvent(NULL, kCGScrollEventUnitLine, 1, 1);
    assert(wheel);
    CGEventSetDoubleValueField(wheel, kCGScrollWheelEventFixedPtDeltaAxis1, 0.25);
    assert(pointer_event(NULL, kCGEventScrollWheel, wheel, worker) == NULL);
    assert(fabs(worker->capture.events[(worker->capture.write - 1) % RCF_CAPTURE_CAPACITY].wheel - 0.25) < 1e-9);
    CFRelease(wheel);
    rcf_pointer_pump(worker, receive_event);

    assert(pthread_create(&producer, NULL, produce, worker) == 0);
    join_producer = true;
    // Replay blocks until production finishes. The producer must never enter managed delivery.
    while (rcf_pointer_pending(worker) == RCF_INPUT_EMPTY) usleep(1000);
    rcf_pointer_pump(worker, receive_event);

    while (rcf_pointer_pending(worker) == RCF_INPUT_ELIGIBLE)
        rcf_pointer_pump(worker, receive_event);

    assert(total == 20007 && !worker->capture.error);
    CGEventRef press = CGEventCreateKeyboardEvent(NULL, 13, true);
    CGEventRef release = CGEventCreateKeyboardEvent(NULL, 13, false);
    assert(press && release);
    assert(pointer_event(NULL, kCGEventKeyDown, press, worker) == NULL);
    last_worker = worker;
    assert(rcf_mac_raw_guards_pending() == 1);
    pointer_event(NULL, kCGEventTapDisabledByTimeout, press, worker);
    assert(rcf_mac_raw_guards_pending() == 1);
    assert(rcf_pointer_end(worker) == 0);
    assert(rcf_mac_raw_guard([NSEvent eventWithCGEvent:release]) == 1);
    assert(rcf_mac_raw_guards_pending() == 0);
    assert(rcf_mac_raw_guard([NSEvent eventWithCGEvent:press]) == 0);
    assert(rcf_mac_raw_guard([NSEvent eventWithCGEvent:release]) == 0);

    char diagnostics[4096];
    rcf_pointer_diagnostics(worker, diagnostics, sizeof(diagnostics));
    assert(strstr(diagnostics, "last-failure{") && strstr(diagnostics, "error=4"));

    atomic_store(&worker->state, RCF_RAW_RUNNING);
    atomic_store(&worker->permitted, true);
    rcf_capture_begin(&worker->capture, &config, physical_keys, rcf_pointer_clock());
    assert(pointer_event(NULL, kCGEventKeyDown, press, worker) == NULL);
    assert(rcf_pointer_end(worker) == 0);
    unsigned before = notifications;
    deliver_notification(worker);
    assert(notifications == before);
    assert(pointer_event(NULL, kCGEventKeyUp, release, worker) == NULL);
    rcf_pointer_diagnostics(worker, diagnostics, sizeof(diagnostics));
    assert(strstr(diagnostics, "last-failure{") && strstr(diagnostics, "error=4"));
    CFRelease(press);
    CFRelease(release);
    last_worker = NULL;
    CFRelease(worker->wake);
    pthread_mutex_destroy(&worker->gate);
    free(worker);
}

static void clock_lifecycle(void) {
    RcfPointerWorker *worker = calloc(1, sizeof(*worker));
    assert(worker && pthread_mutex_init(&worker->gate, NULL) == 0);
    atomic_init(&worker->permitted, true);
    atomic_init(&worker->state, RCF_RAW_RUNNING);
    atomic_init(&worker->notifications_enabled, false);
    worker->loop = CFRunLoopGetCurrent();
    CFRunLoopTimerContext context = {0};
    context.info = worker;
    worker->timer = CFRunLoopTimerCreate(NULL, CFAbsoluteTimeGetCurrent(), 1.0 / 120, 0, 0, publish_clock, &context);
    assert(worker->timer);
    bool held[RCF_CAPTURE_CONTROLS] = {0};
    RcfCaptureConfig config = {0};
    config.session = 1;
    config.entry_mouse_button = UINT32_MAX;
    config.configured[13] = 1;
    held[13] = true;

    for (uint32_t session = 1; session <= 4; ++session) {
        config.session = session;
        rcf_capture_begin(&worker->capture, &config, held, rcf_pointer_clock());
        assert(worker->capture.initial[13] && worker->capture.started_at > 0);
        update_clock(worker);
        assert(worker->clock_scheduled && CFRunLoopContainsTimer(worker->loop, worker->timer, kCFRunLoopDefaultMode));
        double before = worker->capture.frontier;
        publish_clock(worker->timer, worker);
        assert(worker->capture.frontier > before && worker->capture.controls[13].logical);

        if (session == 1) rcf_capture_finish(&worker->capture, RCF_END_KEEP, rcf_pointer_clock());
        else if (session == 2) rcf_capture_revoke(&worker->capture, 8);
        else if (session == 3) rcf_capture_revoke(&worker->capture, 4);
        else atomic_store(&worker->permitted, false);

        update_clock(worker);
        assert(!worker->clock_scheduled && !CFRunLoopContainsTimer(worker->loop, worker->timer, kCFRunLoopDefaultMode));
        atomic_store(&worker->permitted, true);
    }

    config.held_buttons = 2;
    rcf_capture_begin(&worker->capture, &config, held, rcf_pointer_clock());
    update_clock(worker);
    assert(!worker->capture.active && !worker->clock_scheduled);
    CFRunLoopTimerInvalidate(worker->timer);
    CFRelease(worker->timer);
    pthread_mutex_destroy(&worker->gate);
    free(worker);
}

static void gcmouse_cleanup(void) {
    dispatch_semaphore_t entered = dispatch_semaphore_create(0);
    dispatch_semaphore_t resume = dispatch_semaphore_create(0);
    input_queue = dispatch_queue_create("RhinosCanFly.shutdown-check", DISPATCH_QUEUE_SERIAL);
    receiver = ignored_motion;
    atomic_store(&accepting, true);
    atomic_store(&motion_source, RCF_RAW_GCMOUSE);
    atomic_store(&queue_drained, false);
    dispatch_async(input_queue, ^{
        dispatch_semaphore_signal(entered);
        dispatch_semaphore_wait(resume, DISPATCH_TIME_FOREVER);
    });
    assert(dispatch_semaphore_wait(entered, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) == 0);
    assert(rcf_mac_raw_end() == -1 && receiver == ignored_motion);
    assert(rcf_mac_raw_end() == -1);
    dispatch_semaphore_signal(resume);

    unsigned attempts = 0;
    while (rcf_mac_raw_end() != 0) {
        assert(++attempts < 2000);
        usleep(1000);
    }

    assert(!receiver);
}

static void live_delivery(RcfPointerWorker *worker) {
    RcfCaptureConfig config = {0};
    config.session = 99;
    config.configured[13] = 1;
    assert(rcf_pointer_activate(worker, &config) == 0);
    bool active = false;

    for (unsigned attempt = 0; attempt < 2000 && !active; ++attempt) {
        pthread_mutex_lock(&worker->gate);
        active = worker->capture.active;
        pthread_mutex_unlock(&worker->gate);
        usleep(1000);
    }

    assert(active);
    unsigned previous_downs = key_downs, previous_ups = key_ups;
    unsigned previous_moves = moves, previous_wheels = wheels;
    CGEventRef press = CGEventCreateKeyboardEvent(NULL, 13, true);
    CGEventRef release = CGEventCreateKeyboardEvent(NULL, 13, false);
    CGEventRef motion = movement(7);
    CGEventRef wheel = CGEventCreateScrollWheelEvent(NULL, kCGScrollEventUnitLine, 1, 1);
    assert(press && release && motion && wheel);
    CGEventSetIntegerValueField(motion, kCGMouseEventWindowUnderMousePointer, 0);
    CGEventSetIntegerValueField(wheel, kCGMouseEventWindowUnderMousePointer, 0);
    CGEventPostToPid(getpid(), press);
    CGEventPostToPid(getpid(), motion);
    CGEventPostToPid(getpid(), wheel);
    CGEventPostToPid(getpid(), release);

    // No main-thread run loop or receiver runs while the process tap captures these posts.
    usleep(100000);
    assert(rcf_pointer_state(worker) == RCF_RAW_RUNNING);

    while (rcf_pointer_pending(worker) == RCF_INPUT_ELIGIBLE)
        rcf_pointer_pump(worker, receive_event);

    assert(key_downs > previous_downs && key_ups > previous_ups);
    assert(moves > previous_moves && wheels > previous_wheels);
    CFRelease(press);
    CFRelease(release);
    CFRelease(motion);
    CFRelease(wheel);
    puts("Posted key, pointer and wheel events reached the live process tap with its main thread blocked.");
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        [NSApplication sharedApplication];
        adapter_checks();
        clock_lifecycle();
        gcmouse_cleanup();
        puts("Quartz adapter, flight clock lifecycle, blocked consumer, retained releases and cleanup checks passed.");

        if (argc > 1 && strcmp(argv[1], "--live") == 0) {
            if (!AXIsProcessTrusted()) { puts("SKIP: Accessibility permission is missing."); return 78; }
            RcfPointerWorker *worker = rcf_pointer_begin(notify);
            if (!worker) { puts("FAIL: permitted process tap could not start."); return 1; }

            NSDate *until = [NSDate dateWithTimeIntervalSinceNow:2];
            while (rcf_pointer_state(worker) == RCF_RAW_STARTING && until.timeIntervalSinceNow > 0)
                CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.01, false);

            if (rcf_pointer_state(worker) != RCF_RAW_RUNNING) {
                printf("FAIL: permitted process tap failed or timed out with error %u.\n", rcf_pointer_error(worker));
                rcf_pointer_end(worker);
                return 1;
            }

            live_delivery(worker);
            rcf_pointer_end(worker);
            puts("Live synthetic process tap delivery passed. Physical device delivery still needs Rhino.");
        } else {
            puts("SKIP: live process tap and physical device delivery were not requested.");
        }
    }
    return 0;
}
