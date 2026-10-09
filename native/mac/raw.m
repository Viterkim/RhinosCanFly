#import <AppKit/AppKit.h>
#import <GameController/GameController.h>
#include <stdbool.h>
#include <stdatomic.h>
#include "input.h"
#include "pointer-worker.h"

@interface RcfMouse : NSObject
@property(nonatomic, strong) GCMouse *mouse;
@property(nonatomic, strong) dispatch_queue_t previous_queue;
@property(nonatomic, copy) GCMouseMoved handler;
@end
@implementation RcfMouse
@end

static NSMutableArray<RcfMouse *> *attached;
static dispatch_queue_t input_queue;
static char input_queue_key;
static id connected, disconnected;
static _Atomic bool accepting;
static _Atomic uint32_t count;
static RcfRelativeMotionHandler receiver;
static _Atomic uint64_t generation;
static _Atomic uint32_t motion_source;
static _Atomic bool queue_drained;
static bool drain_requested;
static RcfPointerWorker *pointer_worker;
static RcfPointerWorker *last_worker;
static double started_at;

static bool foreign_handlers(GCMouseInput *input) {
    if (input.valueDidChangeHandler || input.scroll.valueChangedHandler) return true;
    NSDictionary<NSString *, GCControllerButtonInput *> *buttons = input.buttons;
    for (NSString *name in buttons) {
        GCControllerButtonInput *button = buttons[name];
        if (button.pressedChangedHandler || button.valueChangedHandler ||
            button.touchedChangedHandler) return true;
    }
    NSDictionary<NSString *, GCControllerAxisInput *> *axes = input.axes;
    for (NSString *name in axes) {
        GCControllerAxisInput *axis = axes[name];
        if (axis.valueChangedHandler) return true;
    }
    return false;
}

static void attach_mouse(GCMouse *mouse) {
    if (!receiver || !atomic_load_explicit(&accepting, memory_order_acquire) ||
        atomic_load(&motion_source) != RCF_RAW_GCMOUSE) return;
    for (RcfMouse *item in attached) if (item.mouse == mouse) return;
    // GCMouse handlers are shared with the host; leave occupied devices alone.
    GCMouseInput *input = mouse.mouseInput;
    if (!input || input.mouseMovedHandler || foreign_handlers(input)) return;

    RcfMouse *item = [RcfMouse new];
    item.mouse = mouse;
    item.previous_queue = mouse.handlerQueue;
    uint64_t session_generation = atomic_load(&generation);
    item.handler = ^(GCMouseInput *source, float dx, float dy) {
        (void)source;
        if (!atomic_load_explicit(&accepting, memory_order_acquire) ||
            atomic_load(&generation) != session_generation ||
            dispatch_get_specific(&input_queue_key) != &input_queue_key) return;
        RcfRelativeMotion event = {0};
        event.timestamp = NSProcessInfo.processInfo.systemUptime;
        event.dx = dx; event.dy = -dy;
        if (receiver) receiver(&event);
    };
    [attached addObject:item];
    input.mouseMovedHandler = item.handler;
    mouse.handlerQueue = input_queue;
    atomic_store(&count, (uint32_t)attached.count);
}

static void detach_mouse(RcfMouse *item) {
    if (item.mouse.mouseInput.mouseMovedHandler == item.handler) {
        item.mouse.mouseInput.mouseMovedHandler = nil;
    }
    if (item.mouse.handlerQueue == input_queue) item.mouse.handlerQueue = item.previous_queue;
    [attached removeObject:item];
    atomic_store(&count, (uint32_t)attached.count);
}

uint32_t rcf_mac_raw_available(void) {
    if (!atomic_load_explicit(&accepting, memory_order_acquire)) return 0;
    if (pointer_worker) {
        return rcf_pointer_validate(pointer_worker);
    }
    return atomic_load(&motion_source) == RCF_RAW_POINTER ? 1 : atomic_load(&count);
}

uint32_t rcf_mac_raw_state(void) {
    if (!atomic_load_explicit(&accepting, memory_order_acquire)) return RCF_RAW_STOPPED;
    return pointer_worker ? rcf_pointer_state(pointer_worker) :
        rcf_mac_raw_available() ? RCF_RAW_RUNNING : RCF_RAW_FAILED;
}

uint32_t rcf_mac_raw_drain(void) {
    if (![NSThread isMainThread]) return RCF_INPUT_FAILED;
    if (!pointer_worker) return RCF_INPUT_EMPTY;
    if (!receiver || !atomic_load_explicit(&accepting, memory_order_acquire)) return RCF_INPUT_FAILED;
    return rcf_pointer_pump(pointer_worker, rcf_mac_raw_input);
}

double rcf_mac_raw_boundary(void) {
    return pointer_worker ? rcf_pointer_boundary(pointer_worker) : 0;
}

uint32_t rcf_mac_raw_pending(void) {
    return pointer_worker ? rcf_pointer_pending(pointer_worker) : RCF_INPUT_EMPTY;
}

void rcf_mac_raw_discard(void) {
    if ([NSThread isMainThread] && pointer_worker) rcf_pointer_discard(pointer_worker);
}

void rcf_mac_raw_reconcile(uint32_t code) {
    (void)code;
}

uint32_t rcf_mac_raw_initial_key(uint32_t code) {
    return pointer_worker ? rcf_pointer_initial_key(pointer_worker, code) : 0;
}

uint32_t rcf_mac_raw_error(void) {
    return pointer_worker ? rcf_pointer_error(pointer_worker) : 0;
}

double rcf_mac_raw_started_at(void) {
    return pointer_worker ? rcf_pointer_started_at(pointer_worker) : started_at;
}

int32_t rcf_mac_raw_activate(const RcfCaptureConfig *config, void *expected_window) {
    if (![NSThread isMainThread] || !pointer_worker || !config || !expected_window) return -1;
    RcfCaptureConfig prepared = *config;
    prepared.window = (uint32_t)((__bridge NSWindow *)expected_window).windowNumber;
    return rcf_pointer_activate(pointer_worker, &prepared);
}

void rcf_mac_raw_revoke(void) {
    if (pointer_worker) rcf_pointer_end(pointer_worker);
}

int32_t rcf_mac_raw_finish(uint32_t reason) {
    if (![NSThread isMainThread] || !pointer_worker) return -1;
    return rcf_pointer_finish(pointer_worker, reason);
}

uint32_t rcf_mac_raw_diagnostics(char *destination, uint32_t capacity) {
    return last_worker ? rcf_pointer_diagnostics(last_worker, destination, capacity) : 0;
}

uint32_t rcf_mac_raw_guards_pending(void) {
    return last_worker ? rcf_pointer_guards_pending(last_worker) : 0;
}

uint32_t rcf_mac_raw_guard(NSEvent *event) {
    if (!last_worker) return 0;
    RcfCaptureEvent value = {0};

    switch (event.type) {
        case NSEventTypeKeyDown: case NSEventTypeKeyUp:
            value.kind = RCF_CAPTURE_KEY;
            value.code = event.keyCode;
            value.down = event.type == NSEventTypeKeyDown;
            value.repeated = event.isARepeat;
            break;
        case NSEventTypeLeftMouseDown: case NSEventTypeRightMouseDown: case NSEventTypeOtherMouseDown:
        case NSEventTypeLeftMouseUp: case NSEventTypeRightMouseUp: case NSEventTypeOtherMouseUp:
            value.kind = RCF_CAPTURE_BUTTON;
            value.code = (uint32_t)event.buttonNumber;
            value.press_id = event.CGEvent ? (uint64_t)CGEventGetIntegerValueField(event.CGEvent, kCGMouseEventNumber) : (uint64_t)event.eventNumber;
            value.down = event.type == NSEventTypeLeftMouseDown || event.type == NSEventTypeRightMouseDown ||
                event.type == NSEventTypeOtherMouseDown;
            break;
        case NSEventTypeScrollWheel:
            value.kind = RCF_CAPTURE_WHEEL;
            value.phase = ((event.phase & NSEventPhaseBegan) ? RCF_SCROLL_BEGAN : 0u) |
                ((event.phase & NSEventPhaseChanged) ? RCF_SCROLL_CHANGED : 0u) |
                ((event.phase & NSEventPhaseEnded) ? RCF_SCROLL_ENDED : 0u) |
                ((event.phase & NSEventPhaseCancelled) ? RCF_SCROLL_CANCELLED : 0u) |
                ((event.phase & NSEventPhaseMayBegin) ? RCF_SCROLL_MAY_BEGIN : 0u);
            value.momentum = (event.momentumPhase & NSEventPhaseBegan) ? RCF_MOMENTUM_BEGAN :
                (event.momentumPhase & NSEventPhaseChanged) ? RCF_MOMENTUM_CHANGED :
                (event.momentumPhase & (NSEventPhaseEnded | NSEventPhaseCancelled)) ? RCF_MOMENTUM_ENDED : 0u;
            if (event.CGEvent) {
                uint32_t phase = (uint32_t)CGEventGetIntegerValueField(event.CGEvent, kCGScrollWheelEventScrollPhase);
                uint32_t momentum = (uint32_t)CGEventGetIntegerValueField(event.CGEvent, kCGScrollWheelEventMomentumPhase);
                if (phase) value.phase = phase;
                if (momentum) value.momentum = momentum;
            }
            break;
        default: return 0;
    }

    value.source_time = event.CGEvent ? CGEventGetTimestamp(event.CGEvent) : 0;
    value.modifiers = event.CGEvent ? CGEventGetFlags(event.CGEvent) : event.modifierFlags;
    return rcf_pointer_guard(last_worker, value);
}

void rcf_mac_raw_motion(NSEvent *event) {
    if (![NSThread isMainThread] || !receiver ||
        !atomic_load_explicit(&accepting, memory_order_acquire) ||
        atomic_load(&motion_source) != RCF_RAW_POINTER || event.timestamp < started_at) return;
    CGEventRef native_event = event.CGEvent;
    if (!native_event) {
        atomic_store_explicit(&accepting, false, memory_order_release);
        return;
    }
    // CoreGraphics supplies unaccelerated deltas, including trackpad pointer motion.
    RcfRelativeMotion motion = {
        event.timestamp,
        (double)CGEventGetIntegerValueField(native_event, kCGEventUnacceleratedPointerMovementX),
        (double)CGEventGetIntegerValueField(native_event, kCGEventUnacceleratedPointerMovementY)
    };
    if (!motion.dx && !motion.dy) return;
    receiver(&motion);
}

uint32_t rcf_mac_raw_validate(void) {
    if (!atomic_load_explicit(&accepting, memory_order_acquire)) return 0;
    @try {
        if ([NSThread isMainThread] && pointer_worker)
            return rcf_pointer_validate(pointer_worker);

        if ([NSThread isMainThread]) {
            for (RcfMouse *item in attached) {
                GCMouseInput *input = item.mouse.mouseInput;
                if (!input || input.mouseMovedHandler != item.handler ||
                    item.mouse.handlerQueue != input_queue || foreign_handlers(input)) {
                    atomic_store_explicit(&accepting, false, memory_order_release);
                    atomic_store(&count, 0);
                    break;
                }
            }
        }
    } @catch (NSException *exception) {
        (void)exception;
        atomic_store_explicit(&accepting, false, memory_order_release);
        atomic_store(&count, 0);
    }
    return rcf_mac_raw_available();
}

int32_t rcf_mac_raw_end(void) {
    if (![NSThread isMainThread]) return 1;
    if (atomic_exchange_explicit(&accepting, false, memory_order_acq_rel)) ++generation;
    @try {
        if (connected) {
            [[NSNotificationCenter defaultCenter] removeObserver:connected]; connected = nil;
        }
        if (disconnected) {
            [[NSNotificationCenter defaultCenter] removeObserver:disconnected]; disconnected = nil;
        }
        while (attached.count) detach_mouse(attached.lastObject);

        if (pointer_worker) {
            if (rcf_pointer_end(pointer_worker) != 0) return -1;
            pointer_worker = NULL;
        }

        if (receiver && atomic_load(&motion_source) == RCF_RAW_GCMOUSE && input_queue) {
            if (!drain_requested) {
                drain_requested = true;
                dispatch_async(input_queue, ^{
                    atomic_store_explicit(&queue_drained, true, memory_order_release);
                });
            }
            if (!atomic_load_explicit(&queue_drained, memory_order_acquire)) return -1;
        }

        receiver = NULL;
        attached = nil;
        atomic_store(&motion_source, RCF_RAW_AUTO);
        drain_requested = false;
        return 0;
    } @catch (NSException *exception) { (void)exception; return 2; }
}

int32_t rcf_mac_raw_begin(RcfRelativeMotionHandler handler, RcfMacNotify notify, uint32_t backend) {
    if (![NSThread isMainThread] || !handler || receiver || pointer_worker || backend > RCF_RAW_WORKER) return -1;
    // SDL also avoids the broken GCMouse delivery on macOS 12/13.
    if (@available(macOS 14.0, *)) {
        @try {
            if (!input_queue) {
                input_queue = dispatch_queue_create("RhinosCanFly.mouse", DISPATCH_QUEUE_SERIAL);
                dispatch_queue_set_specific(input_queue, &input_queue_key, &input_queue_key, NULL);
                dispatch_set_target_queue(input_queue, dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0));
            }
            attached = [NSMutableArray new];
            atomic_store(&motion_source, backend == RCF_RAW_AUTO ? RCF_RAW_GCMOUSE : backend);
            atomic_store(&queue_drained, false);
            started_at = NSProcessInfo.processInfo.systemUptime;
            receiver = handler;
            uint64_t session_generation = ++generation;
            atomic_store_explicit(&accepting, true, memory_order_release);

            if (backend == RCF_RAW_WORKER) {
                pointer_worker = rcf_pointer_begin(notify);
                if (pointer_worker) last_worker = pointer_worker;
                if (!pointer_worker) { rcf_mac_raw_end(); return -1; }
                return RCF_RAW_WORKER;
            }

            if (backend == RCF_RAW_POINTER) return RCF_RAW_POINTER;

            NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
            connected = [center addObserverForName:GCMouseDidConnectNotification object:nil
                queue:NSOperationQueue.mainQueue usingBlock:^(NSNotification *note) {
                    @try {
                        if (generation == session_generation) attach_mouse(note.object);
                    } @catch (NSException *exception) {
                        (void)exception; atomic_store_explicit(&accepting, false, memory_order_release);
                    }
                }];
            disconnected = [center addObserverForName:GCMouseDidDisconnectNotification object:nil
                queue:NSOperationQueue.mainQueue usingBlock:^(NSNotification *note) {
                    if (generation != session_generation) return;
                    @try {
                        for (RcfMouse *item in attached)
                            if (item.mouse == note.object) {
                                atomic_store_explicit(&accepting, false, memory_order_release);
                                atomic_store(&count, 0);
                                break;
                            }
                    } @catch (NSException *exception) {
                        (void)exception; atomic_store_explicit(&accepting, false, memory_order_release);
                    }
                }];
            for (GCMouse *mouse in GCMouse.mice) attach_mouse(mouse);
            // Keep one source for the session so AppKit and GCMouse never count a move twice.
            if (backend == RCF_RAW_AUTO && !attached.count) atomic_store(&motion_source, RCF_RAW_POINTER);
            return (int32_t)rcf_mac_raw_available();
        } @catch (NSException *exception) { (void)exception; rcf_mac_raw_end(); return -1; }
    }
    return 0;
}
