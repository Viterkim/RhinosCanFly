#import <AppKit/AppKit.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../../native/mac/pointer-worker.m"
#include "../../native/mac/raw.m"
#include "../../native/mac/input.m"

static double total, total_y;
static unsigned repairs;
static unsigned notifications;
static RcfMacEvent last_event;
static unsigned wheels, stops;
static dispatch_semaphore_t entered, resume;
static _Atomic bool hold_callback;

static void receive(const RcfRelativeMotion *motion) { total += motion->dx; total_y += motion->dy; }

static uint32_t receive_event(const RcfMacEvent *event) {
    last_event = *event;
    if (event->kind == 7) {
        assert(event->code < 133 && event->timestamp > 0);
        ++repairs;
    } else if (event->kind == 2) ++wheels;
    else if (event->kind == 6) ++stops;
    return 1;
}

static void notify(void) { ++notifications; }

static void reset_queue(RcfPointerWorker *worker) {
    worker->read = worker->write = 0;
    worker->last_timestamp = 0;
    worker->repair_barrier = 0;
    worker->discard_through = 0;
    worker->blocked_sequence = 0;
    worker->blocked_seconds = worker->checked_at = 0;
    worker->stall_granted = false;
    memset(worker->release_candidate, 0, sizeof(worker->release_candidate));
    memset(worker->control_sequence, 0, sizeof(worker->control_sequence));
    memset(worker->repaired_at, 0, sizeof(worker->repaired_at));
    memset(worker->repair_sequence, 0, sizeof(worker->repair_sequence));
    atomic_store(&worker->state, RCF_RAW_RUNNING);
    atomic_store(&worker->notified, false);
}

static void blocked_notify(void) {
    if (atomic_exchange(&hold_callback, false)) {
        dispatch_semaphore_signal(entered);
        dispatch_semaphore_wait(resume, DISPATCH_TIME_FOREVER);
    }
}

static void move(RcfPointerWorker *worker, double timestamp, int64_t dx, CGEventType type) {
    CGEventRef event = CGEventCreateMouseEvent(NULL, type, CGPointZero, kCGMouseButtonLeft);
    assert(event);
    CGEventSetTimestamp(event, (uint64_t)(timestamp * 1e9));
    CGEventSetIntegerValueField(event, kCGEventUnacceleratedPointerMovementX, dx);
    assert(pointer_event(NULL, type, event, worker) == event);
    CFRelease(event);
}

static RcfPointerWorker *replenishing;
static unsigned remaining;

static void replenish(const RcfRelativeMotion *motion) {
    receive(motion);

    if (remaining) {
        --remaining;
        move(replenishing, replenishing->last_timestamp + 1, 1, kCGEventMouseMoved);
    }
}

static void boundary(RcfPointerWorker *worker, CGEventType type, double timestamp) {
    CGEventRef event = CGEventCreate(NULL);
    assert(event);
    CGEventSetType(event, type);
    CGEventSetTimestamp(event, (uint64_t)(timestamp * 1e9));
    if (type == kCGEventRightMouseDown || type == kCGEventRightMouseUp)
        CGEventSetIntegerValueField(event, kCGMouseEventButtonNumber, 1);
    uint64_t before = worker->write;

    assert(pointer_event(NULL, type, event, worker) == event);
    assert(worker->write == before + 1);
    assert(rcf_pointer_drain(worker, event, receive, rcf_mac_raw_release) == 0);
    CFRelease(event);
}

static int32_t drain(RcfPointerWorker *worker, double timestamp) {
    CGEventRef event = CGEventCreate(NULL);
    assert(event);
    CGEventSetType(event, kCGEventMouseMoved);
    CGEventSetTimestamp(event, (uint64_t)(timestamp * 1e9));

    int32_t result = rcf_pointer_drain(worker, event, receive, rcf_mac_raw_release);
    CFRelease(event);
    return result;
}

static void quiet_notify(void) {}

static void check_monitor_bridge(NSWindow *test_window) {
    callback = NULL;
    assert(rcf_mac_monitor_begin(receive_event, (__bridge void *)test_window) == 0);
    RcfPointerWorker *worker = calloc(1, sizeof(*worker));
    assert(worker && pthread_mutex_init(&worker->gate, NULL) == 0);
    atomic_init(&worker->state, RCF_RAW_RUNNING);
    atomic_init(&worker->stopping, false);
    atomic_init(&worker->notified, false);
    atomic_init(&worker->error, 0);
    worker->notify = quiet_notify;
    double origin = NSProcessInfo.processInfo.systemUptime;
    started_at = worker->started_at = origin;
    pointer_worker = worker;
    receiver = receive;
    atomic_store(&accepting, true);
    atomic_store(&motion_source, RCF_RAW_WORKER);

    CGEventRef wheel = CGEventCreateScrollWheelEvent(NULL, kCGScrollEventUnitLine, 1, 4);
    assert(wheel);
    CGEventSetTimestamp(wheel, (uint64_t)((origin + 0.01) * 1e9));
    pointer_event(NULL, kCGEventScrollWheel, wheel, worker);
    rcf_mac_raw_discard();
    unsigned before_wheels = wheels;
    [NSApp sendEvent:[NSEvent eventWithCGEvent:wheel]];
    assert(wheels == before_wheels + 1 && last_event.kind == 2 && last_event.wheel == 0);
    assert(rcf_pointer_pending(worker) == RCF_INPUT_EMPTY);

    CGEventSetTimestamp(wheel, (uint64_t)((origin + 0.02) * 1e9));
    pointer_event(NULL, kCGEventScrollWheel, wheel, worker);
    [NSApp sendEvent:[NSEvent eventWithCGEvent:wheel]];
    assert(wheels == before_wheels + 2 && last_event.wheel != 0);
    CFRelease(wheel);

    unsigned before_repairs = repairs;
    assert(!reconcile_release(worker, 13, worker->sequence, false, origin + 0.03));
    assert(reconcile_release(worker, 13, worker->sequence, false, origin + 0.09));
    assert(rcf_mac_raw_drain() == RCF_INPUT_EMPTY);
    assert(repairs == before_repairs + 1 && last_event.kind == 7 && last_event.code == 13);
    assert(last_event.timestamp == origin + 0.09);

    unsigned before_stops = stops;
    CFRunLoopAddCommonMode(CFRunLoopGetMain(), (__bridge CFStringRef)NSEventTrackingRunLoopMode);
    CFRunLoopRunInMode((__bridge CFStringRef)NSEventTrackingRunLoopMode, 0.001, true);
    assert(stops > before_stops && last_event.kind == 6);

    assert(rcf_mac_monitor_end() == 0);
    pointer_worker = NULL;
    receiver = NULL;
    atomic_store(&accepting, false);
    atomic_store(&motion_source, RCF_RAW_STOPPED);
    pthread_mutex_destroy(&worker->gate);
    free(worker);
    puts("Production monitor wheel discard, release callback and tracking notification passed.");
}

static void check_appkit_delivery(void) {
    [NSApplication sharedApplication];
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    [NSApp finishLaunching];
    NSWindow *test_window = [[NSWindow alloc] initWithContentRect:NSMakeRect(200, 200, 320, 200)
        styleMask:NSWindowStyleMaskTitled backing:NSBackingStoreBuffered defer:NO];
    test_window.releasedWhenClosed = NO;
    test_window.title = @"RCF pointer check";
    test_window.acceptsMouseMovedEvents = YES;
    [test_window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];

    double activation_deadline = NSProcessInfo.processInfo.systemUptime + 3;
    while (rcf_mac_foreground_window() != (__bridge void *)test_window) {
        if (NSProcessInfo.processInfo.systemUptime >= activation_deadline) {
            fputs("Test window activation timed out. AppKit checks did not run.\n", stderr);
            [test_window close];
            exit(1);
        }

        NSEvent *event = [NSApp nextEventMatchingMask:NSEventMaskAny
            untilDate:[NSDate dateWithTimeIntervalSinceNow:0.01] inMode:NSDefaultRunLoopMode dequeue:YES];
        if (event) [NSApp sendEvent:event];
    }

    check_monitor_bridge(test_window);
    callback = receive_event;

    for (NSString *mode in @[NSDefaultRunLoopMode, NSEventTrackingRunLoopMode]) {
        RcfPointerWorker *worker = rcf_pointer_begin(quiet_notify);
        assert(worker);
        unsigned attempt = 0;
        while (rcf_pointer_state(worker) == RCF_RAW_STARTING) {
            assert(++attempt < 2000);
            usleep(1000);
        }

        if (rcf_pointer_state(worker) != RCF_RAW_RUNNING) {
            uint32_t error = rcf_pointer_error(worker);
            assert(error == 2 || error == 3);
            while (rcf_pointer_end(worker) != 0) usleep(1000);
            puts("Quartz/AppKit integration skipped: tap permission or event coverage unavailable.");
            break;
        }

        __block unsigned controls = 0;
        __block double before_control = 0, before_control_y = 0;
        double before = total, before_y = total_y;
        id monitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskAny handler:^NSEvent *(NSEvent *event) {
            switch (event.type) {
                case NSEventTypeMouseMoved: case NSEventTypeKeyDown: case NSEventTypeKeyUp:
                    assert(rcf_pointer_drain(worker, event.CGEvent, receive, rcf_mac_raw_release) == 0);
                    if (event.type != NSEventTypeMouseMoved) {
                        if (controls == 0) { before_control = total - before; before_control_y = total_y - before_y; }
                        else { assert(total - before == before_control && total_y - before_y == before_control_y); }
                        ++controls;
                    }
                    return nil;
                default: return event;
            }
        }];
        assert(monitor);

        dispatch_semaphore_t posted = dispatch_semaphore_create(0);
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0), ^{
            CGEventType types[] = {kCGEventMouseMoved, kCGEventKeyDown, kCGEventKeyUp, kCGEventMouseMoved};
            for (unsigned index = 0; index < 4; ++index) {
                CGEventRef event = index == 1 || index == 2 ?
                    CGEventCreateKeyboardEvent(NULL, 0, index == 1) :
                    CGEventCreateMouseEvent(NULL, types[index], CGPointMake(250, 250), kCGMouseButtonLeft);
                assert(event);
                if (index == 0 || index == 3)
                    CGEventSetIntegerValueField(event, kCGEventUnacceleratedPointerMovementX, index == 0 ? 2 : 3);
                CGEventPostToPid(getpid(), event);
                CFRelease(event);
                usleep(2000);
            }
            dispatch_semaphore_signal(posted);
        });

        // No AppKit pumping here. The process tap must collect while the UI is occupied.
        assert(dispatch_semaphore_wait(posted, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC)) == 0);
        usleep(100000);
        pthread_mutex_lock(&worker->gate);
        uint64_t collected = worker->sequence;
        double captured_x = 0, captured_y = 0, first_x = 0, first_y = 0;
        bool first_segment = true;
        for (uint64_t index = worker->read; index < worker->write; ++index) {
            RcfPointerEvent packet = worker->events[index % EVENT_CAPACITY];
            if (is_movement(packet.type)) {
                captured_x += packet.motion.dx; captured_y += packet.motion.dy;
                if (first_segment) { first_x += packet.motion.dx; first_y += packet.motion.dy; }
            } else first_segment = false;
        }
        pthread_mutex_unlock(&worker->gate);
        assert(collected >= 4);

        double deadline = NSProcessInfo.processInfo.systemUptime + 2;
        while (controls < 2 || rcf_pointer_pending(worker) != RCF_INPUT_EMPTY) {
            assert(NSProcessInfo.processInfo.systemUptime < deadline);
            NSEvent *event = [NSApp nextEventMatchingMask:NSEventMaskAny
                untilDate:[NSDate dateWithTimeIntervalSinceNow:0.01] inMode:mode dequeue:YES];
            if (event) [NSApp sendEvent:event];
            assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) != RCF_INPUT_FAILED);
        }

        assert(total - before == captured_x && total_y - before_y == captured_y);
        assert(before_control == first_x && before_control_y == first_y);
        if (captured_x == 5 && captured_y == 0) puts("Live movement +2/+3 delivered in control order.");
        else puts("Synthetic route changed the requested +2/+3 raw deltas; raw movement fidelity is unverified.");

        [NSEvent removeMonitor:monitor];
        while (rcf_pointer_end(worker) != 0) usleep(1000);
    }

    [test_window close];
    puts("Live dispatch pass finished. Synthetic process events only; real mouse/trackpad still need Rhino testing.");
}

int main(void) {
    @autoreleasepool {
        callback = receive_event;
        RcfPointerWorker *worker = calloc(1, sizeof(*worker));
        assert(worker && pthread_mutex_init(&worker->gate, NULL) == 0);
        atomic_init(&worker->state, RCF_RAW_RUNNING);
        atomic_init(&worker->stopping, false);
        atomic_init(&worker->finished, false);
        atomic_init(&worker->notified, false);
        atomic_init(&worker->error, 0);
        worker->notify = notify;
        worker->loop = CFRunLoopGetCurrent();
        CFRunLoopSourceContext notification_context = {0};
        notification_context.info = worker;
        notification_context.perform = deliver_notification;
        worker->notify_source = CFRunLoopSourceCreate(NULL, 0, &notification_context);
        assert(worker->notify_source);
        CFRunLoopAddSource(worker->loop, worker->notify_source, kCFRunLoopDefaultMode);

        move(worker, 10, 3, kCGEventMouseMoved);
        move(worker, 20, 5, kCGEventLeftMouseDragged);
        move(worker, 30, -2, kCGEventOtherMouseDragged);
        assert(total == 0 && worker->write == 1 && notifications == 0);
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.01, true);
        assert(notifications == 1);
        assert(drain(worker, 15) == 0 && total == 0);
        assert(drain(worker, 25) == 0 && total == 0);
        assert(drain(worker, 35) == 0 && total == 6);

        boundary(worker, kCGEventKeyDown, 35);
        boundary(worker, kCGEventKeyUp, 35);
        boundary(worker, kCGEventFlagsChanged, 35);
        boundary(worker, kCGEventScrollWheel, 35);
        boundary(worker, kCGEventRightMouseUp, 35);

        move(worker, 35, 4, kCGEventRightMouseDragged);
        assert(drain(worker, 35) == 0 && total == 10);

        move(worker, 34, 100, kCGEventRightMouseDragged);
        assert(rcf_pointer_state(worker) == RCF_RAW_FAILED && total == 10);

        reset_queue(worker);

        CGEventType transitions[] = {kCGEventKeyDown, kCGEventScrollWheel, kCGEventRightMouseUp};
        for (unsigned index = 0; index < sizeof(transitions) / sizeof(*transitions); ++index) {
            worker->read = worker->write = 0;
            CGEventRef event = CGEventCreate(NULL);
            assert(event);
            CGEventSetType(event, transitions[index]);
            CGEventSetTimestamp(event, 40ULL * 1000000000ULL);
            double before = total;

            move(worker, 40, 2, kCGEventMouseMoved);
            assert(pointer_event(NULL, transitions[index], event, worker) == event);
            move(worker, 40, 3, kCGEventMouseMoved);

            assert(rcf_pointer_drain(worker, event, receive, rcf_mac_raw_release) == 0 && total == before + 2);
            assert(worker->read == 2 && worker->write == 3);
            assert(drain(worker, 40) == 0 && total == before + 5);
            CFRelease(event);
        }

        reset_queue(worker);
        double before = total;

        for (unsigned index = 1; index <= 100000; ++index)
            move(worker, index, 1, kCGEventMouseMoved);
        assert(worker->write == 1 && rcf_pointer_state(worker) == RCF_RAW_RUNNING);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY && total == before + 100000);

        reset_queue(worker);
        replenishing = worker;
        remaining = 99;
        before = total;
        move(worker, 1, 1, kCGEventMouseMoved);
        assert(rcf_pointer_pump(worker, replenish, rcf_mac_raw_release) == RCF_INPUT_ELIGIBLE && total == before + TRANSFER_BUDGET);
        assert(rcf_pointer_pump(worker, replenish, rcf_mac_raw_release) == RCF_INPUT_EMPTY && total == before + 100);

        reset_queue(worker);
        CGEventRef release = CGEventCreate(NULL);
        assert(release);
        CGEventSetType(release, kCGEventRightMouseUp);
        CGEventSetTimestamp(release, 50ULL * 1000000000ULL);
        move(worker, 49, 2, kCGEventMouseMoved);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        move(worker, 51, 3, kCGEventMouseMoved);
        before = total;

        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY && total == before + 2);
        assert(rcf_pointer_boundary(worker) == 50);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY && total == before + 2);
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY && total == before + 5);
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == -1);

        reset_queue(worker);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY);
        assert(drain(worker, 51) == -1 && rcf_pointer_error(worker) == 5);

        reset_queue(worker);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        CGEventSetType(release, kCGEventLeftMouseUp);
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == -1);

        reset_queue(worker);
        move(worker, 49, 100, kCGEventMouseMoved);
        CGEventSetType(release, kCGEventRightMouseUp);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        move(worker, 51, 100, kCGEventMouseMoved);
        rcf_pointer_discard(worker);
        move(worker, 52, 7, kCGEventMouseMoved);
        before = total;
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY && total == before);
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY && total == before + 7);

        reset_queue(worker);
        CGEventRef wheel = CGEventCreate(NULL);
        assert(wheel);
        CGEventSetType(wheel, kCGEventScrollWheel);
        CGEventSetTimestamp(wheel, 50ULL * 1000000000ULL);
        move(worker, 49, 100, kCGEventMouseMoved);
        pointer_event(NULL, kCGEventScrollWheel, wheel, worker);
        rcf_pointer_discard(worker);
        move(worker, 51, 7, kCGEventMouseMoved);
        before = total;
        assert(rcf_pointer_drain(worker, wheel, receive, rcf_mac_raw_release) == 1 && total == before);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY && total == before + 7);
        CGEventSetTimestamp(wheel, 52ULL * 1000000000ULL);
        pointer_event(NULL, kCGEventScrollWheel, wheel, worker);
        assert(rcf_pointer_drain(worker, wheel, receive, rcf_mac_raw_release) == 0);
        CFRelease(wheel);

        reset_queue(worker);
        uint64_t sampled_sequence = worker->sequence;
        assert(!reconcile_release(worker, 129, sampled_sequence, false, 60));
        move(worker, 60.01, 2, kCGEventMouseMoved);
        CGEventSetType(release, kCGEventRightMouseUp);
        CGEventSetTimestamp(release, 60020000000ULL);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        before = total;
        unsigned before_repairs = repairs;
        assert(!reconcile_release(worker, 129, sampled_sequence, false, 60.1));
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY && total == before + 2);
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0 && repairs == before_repairs);

        reset_queue(worker);
        sampled_sequence = worker->sequence;
        assert(!reconcile_release(worker, 129, sampled_sequence, false, 70));
        assert(!reconcile_release(worker, 129, sampled_sequence, false, 70.01));
        assert(reconcile_release(worker, 129, sampled_sequence, false, 70.1));
        move(worker, 70.2, 3, kCGEventMouseMoved);
        before = total;
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);
        assert(repairs == before_repairs + 1 && total == before + 3);

        reset_queue(worker);
        sampled_sequence = worker->sequence;
        assert(!reconcile_release(worker, 129, sampled_sequence, false, 80));
        move(worker, 80.01, 2, kCGEventMouseMoved);
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);
        assert(reconcile_release(worker, 129, worker->sequence, false, 80.1));
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);

        reset_queue(worker);
        assert(!reconcile_release(worker, 13, worker->sequence, false, 80));
        assert(!reconcile_release(worker, 13, worker->sequence, true, 80.01));
        assert(!reconcile_release(worker, 13, worker->sequence, false, 80.02));

        reset_queue(worker);
        before_repairs = repairs;
        for (unsigned index = 0; index < 20 && repairs == before_repairs; ++index) {
            double now = 81 + index * 0.01;
            move(worker, now, 1, kCGEventMouseMoved);
            assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);
            reconcile_release(worker, 13, worker->sequence, false, now);
        }
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);
        assert(repairs == before_repairs + 1 && rcf_pointer_state(worker) == RCF_RAW_RUNNING);

        reset_queue(worker);
        for (unsigned index = 0; index < 40 && rcf_pointer_state(worker) == RCF_RAW_RUNNING; ++index) {
            double now = 82 + index * 0.01;
            move(worker, now, 1, kCGEventMouseMoved);
            reconcile_release(worker, 13, worker->sequence, false, now);
        }
        assert(rcf_pointer_state(worker) == RCF_RAW_FAILED && rcf_pointer_error(worker) == 9);

        for (unsigned schedule = 0; schedule < 16; ++schedule) {
            bool consumed = schedule & 1;
            bool pump_between = schedule & 2;
            bool new_press_queued = schedule & 4;
            bool mouse = schedule & 8;
            uint32_t control = mouse ? 129 : 13;
            CGEventType up_type = mouse ? kCGEventRightMouseUp : kCGEventKeyUp;
            CGEventType down_type = mouse ? kCGEventRightMouseDown : kCGEventKeyDown;

            reset_queue(worker);
            before_repairs = repairs;
            before = total;
            assert(!reconcile_release(worker, control, worker->sequence, false, 83));
            assert(reconcile_release(worker, control, worker->sequence, false, 83.06));
            if (consumed) assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);

            move(worker, 83.065, 1, kCGEventMouseMoved);
            CGEventSetType(release, up_type);
            CGEventSetTimestamp(release, 82990000000ULL);
            CGEventSetIntegerValueField(release, mouse ? kCGMouseEventButtonNumber : kCGKeyboardEventKeycode, mouse ? 1 : 13);
            pointer_event(NULL, up_type, release, worker);

            CGEventRef press = CGEventCreate(NULL);
            assert(press);
            CGEventSetType(press, down_type);
            CGEventSetTimestamp(press, 83100000000ULL);
            CGEventSetIntegerValueField(press, mouse ? kCGMouseEventButtonNumber : kCGKeyboardEventKeycode, mouse ? 1 : 13);
            if (new_press_queued) pointer_event(NULL, down_type, press, worker);

            assert(drain(worker, 83.065) == 0 && total == before + 1);
            if (pump_between) assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_BOUNDARY);
            assert(rcf_pointer_state(worker) == RCF_RAW_RUNNING);
            assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0);
            assert(repairs == before_repairs + 1);
            assert(worker->repaired_at[control] == 0 && worker->repair_sequence[control] == 0);

            if (!new_press_queued) pointer_event(NULL, down_type, press, worker);
            assert(rcf_pointer_drain(worker, press, receive, rcf_mac_raw_release) == 0);
            CGEventSetTimestamp(release, 83200000000ULL);
            pointer_event(NULL, up_type, release, worker);
            assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0);
            assert(rcf_pointer_pending(worker) == RCF_INPUT_EMPTY && rcf_pointer_state(worker) == RCF_RAW_RUNNING);
            CFRelease(press);
        }

        reset_queue(worker);
        assert(!reconcile_release(worker, 13, worker->sequence, false, 84));
        assert(reconcile_release(worker, 13, worker->sequence, false, 84.06));
        assert(rcf_pointer_pump(worker, receive, rcf_mac_raw_release) == RCF_INPUT_EMPTY);
        move(worker, 84.01, 1, kCGEventMouseMoved);
        assert(rcf_pointer_state(worker) == RCF_RAW_FAILED && rcf_pointer_error(worker) == 10);

        reset_queue(worker);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        assert(rcf_pointer_validate(worker, 90, false));
        for (unsigned index = 1; index <= 20; ++index)
            assert(rcf_pointer_validate(worker, 90 + index * 0.01, false));
        assert(rcf_pointer_validate(worker, 95, false));
        for (unsigned index = 1; index <= 20; ++index)
            assert(rcf_pointer_validate(worker, 95 + index * 0.01, false));
        assert(!rcf_pointer_validate(worker, 95.26, false) && rcf_pointer_error(worker) == 7);

        double cadences[] = {0.01, 0.125, 0.25};
        for (unsigned cadence = 0; cadence < 3; ++cadence) {
            reset_queue(worker);
            pointer_event(NULL, kCGEventRightMouseUp, release, worker);
            for (unsigned index = 0; index < 100 && rcf_pointer_state(worker) == RCF_RAW_RUNNING; ++index)
                rcf_pointer_validate(worker, 96 + index * cadences[cadence], false);
            assert(rcf_pointer_state(worker) == RCF_RAW_FAILED && rcf_pointer_error(worker) == 7);
        }

        reset_queue(worker);
        pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        assert(rcf_pointer_validate(worker, 96, false));
        assert(rcf_pointer_validate(worker, 101, false));
        assert(rcf_pointer_drain(worker, release, receive, rcf_mac_raw_release) == 0);
        assert(rcf_pointer_validate(worker, 101.01, false));

        reset_queue(worker);
        assert(!rcf_pointer_validate(worker, 100, true) && rcf_pointer_error(worker) == 8);

        reset_queue(worker);
        for (unsigned index = 0; index <= EVENT_CAPACITY; ++index)
            pointer_event(NULL, kCGEventRightMouseUp, release, worker);
        assert(worker->write == EVENT_CAPACITY && rcf_pointer_state(worker) == RCF_RAW_FAILED);
        CFRelease(release);
        CFRunLoopRemoveSource(worker->loop, worker->notify_source, kCFRunLoopDefaultMode);
        CFRunLoopSourceInvalidate(worker->notify_source);
        CFRelease(worker->notify_source);
        pthread_mutex_destroy(&worker->gate);
        free(worker);

        entered = dispatch_semaphore_create(0);
        resume = dispatch_semaphore_create(0);
        atomic_store(&hold_callback, true);
        worker = rcf_pointer_begin(blocked_notify);
        bool worker_checked = worker != NULL;
        bool tap_created = false;
        unsigned attempt = 0;

        if (worker) {
            assert(dispatch_semaphore_wait(entered, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) == 0);
            tap_created = rcf_pointer_state(worker) == RCF_RAW_RUNNING;
            assert(rcf_pointer_end(worker) == -1 && !atomic_load(&worker->finished));
            assert(rcf_pointer_end(worker) == -1);
            dispatch_semaphore_signal(resume);

            while (rcf_pointer_end(worker) != 0) {
                assert(++attempt < 2000);
                usleep(1000);
            }
        }

        input_queue = dispatch_queue_create("RhinosCanFly.shutdown-check", DISPATCH_QUEUE_SERIAL);
        receiver = receive;
        atomic_store(&accepting, true);
        atomic_store(&motion_source, RCF_RAW_GCMOUSE);
        atomic_store(&queue_drained, false);
        atomic_store(&hold_callback, true);
        dispatch_async(input_queue, ^{ blocked_notify(); });
        assert(dispatch_semaphore_wait(entered, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) == 0);

        assert(rcf_mac_raw_end() == -1 && receiver == receive);
        uint64_t stopped_generation = atomic_load(&generation);
        assert(rcf_mac_raw_end() == -1 && atomic_load(&generation) == stopped_generation);
        assert(rcf_mac_raw_begin(receive, notify, RCF_RAW_POINTER) < 0);
        dispatch_semaphore_signal(resume);

        attempt = 0;
        while (rcf_mac_raw_end() != 0) {
            assert(++attempt < 2000);
            usleep(1000);
        }
        assert(!receiver);
        assert(rcf_mac_raw_begin(receive, notify, RCF_RAW_POINTER) > 0);
        assert(rcf_mac_raw_end() == 0);

        puts("Pointer queue, boundary passthrough and GCMouse asynchronous stop checks passed.");
        puts(worker_checked ? "Worker callback retention passed."
            : "Process tap unavailable or missing event coverage. Worker retention skipped.");
        puts(tap_created ? "Process tap created. Real device delivery still needs Rhino testing."
            : "Real device delivery skipped.");
        check_appkit_delivery();
    }
    return 0;
}
