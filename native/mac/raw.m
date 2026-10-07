#import <AppKit/AppKit.h>
#import <GameController/GameController.h>
#include <stdbool.h>
#include <stdatomic.h>
#include "input.h"

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
static _Atomic uint32_t discovered, rejected;
static _Atomic uint64_t motion_count;
// No GCMouse (trackpads never appear as one): AppKit deltas from the event monitor drive the session.
static _Atomic bool fallback;

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
    if (!receiver || !atomic_load_explicit(&accepting, memory_order_acquire)) return;
    // One motion source per session; a mouse connected mid-fallback waits for the next start.
    if (atomic_load_explicit(&fallback, memory_order_acquire)) return;
    for (RcfMouse *item in attached) if (item.mouse == mouse) return;
    ++discovered;
    // GCMouse handlers are shared with the host; leave occupied devices alone.
    GCMouseInput *input = mouse.mouseInput;
    if (!input || input.mouseMovedHandler || foreign_handlers(input)) { ++rejected; return; }

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
        atomic_fetch_add_explicit(&motion_count, 1, memory_order_relaxed);
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
    return atomic_load_explicit(&fallback, memory_order_acquire) ? 1 : atomic_load(&count);
}

uint32_t rcf_mac_raw_fallback_motion(double timestamp, double dx, double dy) {
    if (![NSThread isMainThread] || !atomic_load_explicit(&fallback, memory_order_acquire) ||
        !atomic_load_explicit(&accepting, memory_order_acquire) || !receiver) return 0;
    // AppKit deltas are accelerated points with y already pointing down.
    RcfRelativeMotion event = {0};
    event.timestamp = timestamp;
    event.dx = dx; event.dy = dy;
    atomic_fetch_add_explicit(&motion_count, 1, memory_order_relaxed);
    receiver(&event);
    return 1;
}

uint32_t rcf_mac_raw_discovered(void) { return atomic_load(&discovered); }
uint32_t rcf_mac_raw_rejected(void) { return atomic_load(&rejected); }
uint64_t rcf_mac_raw_motion_count(void) { return atomic_load_explicit(&motion_count, memory_order_relaxed); }

uint32_t rcf_mac_raw_validate(void) {
    if (!atomic_load_explicit(&accepting, memory_order_acquire)) return 0;
    @try {
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
    atomic_store_explicit(&accepting, false, memory_order_release);
    ++generation;
    @try {
        if (connected) {
            [[NSNotificationCenter defaultCenter] removeObserver:connected]; connected = nil;
        }
        if (disconnected) {
            [[NSNotificationCenter defaultCenter] removeObserver:disconnected]; disconnected = nil;
        }
        while (attached.count) detach_mouse(attached.lastObject);
        // The managed delegate stays rooted until queued callbacks have returned.
        if (input_queue) dispatch_sync(input_queue, ^{});
        atomic_store_explicit(&fallback, false, memory_order_release);
        receiver = NULL;
        attached = nil;
        return 0;
    } @catch (NSException *exception) { (void)exception; return 2; }
}

int32_t rcf_mac_raw_begin(RcfRelativeMotionHandler handler) {
    if (![NSThread isMainThread] || !handler || receiver) return -1;
    // SDL also avoids the broken GCMouse delivery on macOS 12/13.
    if (@available(macOS 14.0, *)) {
        @try {
            if (!input_queue) {
                input_queue = dispatch_queue_create("RhinosCanFly.mouse", DISPATCH_QUEUE_SERIAL);
                dispatch_queue_set_specific(input_queue, &input_queue_key, &input_queue_key, NULL);
                dispatch_set_target_queue(input_queue, dispatch_get_global_queue(QOS_CLASS_USER_INTERACTIVE, 0));
            }
            attached = [NSMutableArray new];
            atomic_store(&discovered, 0);
            atomic_store(&rejected, 0);
            atomic_store(&motion_count, 0);
            atomic_store_explicit(&fallback, false, memory_order_release);
            receiver = handler;
            atomic_store_explicit(&accepting, true, memory_order_release);
            uint64_t session_generation = ++generation;
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
            if (attached.count) return (int32_t)attached.count;
            atomic_store_explicit(&fallback, true, memory_order_release);
            return 1;
        } @catch (NSException *exception) { (void)exception; rcf_mac_raw_end(); return -1; }
    }
    return 0;
}
