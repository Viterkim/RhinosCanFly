#import <AppKit/AppKit.h>
#include <dlfcn.h>
#include "input.h"

_Static_assert(sizeof(RcfMacEvent) == 96, "Mac event ABI must remain 96 bytes");
#define RCF_EVENT_OFFSET(field, offset) \
    _Static_assert(offsetof(RcfMacEvent, field) == offset, "Mac event field offset: " #field)
RCF_EVENT_OFFSET(kind, 0);
RCF_EVENT_OFFSET(code, 4);
RCF_EVENT_OFFSET(down, 8);
RCF_EVENT_OFFSET(repeated, 12);
RCF_EVENT_OFFSET(modifiers, 16);
RCF_EVENT_OFFSET(timestamp, 24);
RCF_EVENT_OFFSET(dx, 32);
RCF_EVENT_OFFSET(dy, 40);
RCF_EVENT_OFFSET(wheel, 48);
RCF_EVENT_OFFSET(precise, 56);
RCF_EVENT_OFFSET(inverted, 60);
RCF_EVENT_OFFSET(window, 64);
RCF_EVENT_OFFSET(screen_x, 72);
RCF_EVENT_OFFSET(screen_y, 80);
RCF_EVENT_OFFSET(buttons, 88);
RCF_EVENT_OFFSET(content, 92);
#undef RCF_EVENT_OFFSET

static id monitor;
static id observer;
static id window_observer, close_observer;
static id sleep_observer, session_observer;
static uint64_t monitor_generation;
static NSWindow *window;
static BOOL previous_mouse_moved;
static RcfMacHandler callback;

uint32_t rcf_mac_event_size(void) { return (uint32_t)sizeof(RcfMacEvent); }
uint32_t rcf_mac_motion_size(void) { return (uint32_t)sizeof(RcfRelativeMotion); }
uint32_t rcf_mac_abi(void) { return 10; }
double rcf_mac_uptime(void) { return NSProcessInfo.processInfo.systemUptime; }

uint32_t rcf_mac_capture_key(void) {
    if (![NSThread isMainThread]) return UINT32_MAX;
    NSEvent *event = NSApp.currentEvent;
    return event && (event.type == NSEventTypeKeyDown || event.type == NSEventTypeKeyUp
        || event.type == NSEventTypeFlagsChanged)
        ? event.keyCode : UINT32_MAX;
}

uint32_t rcf_mac_capture_button(void) {
    if (![NSThread isMainThread]) return UINT32_MAX;
    NSEvent *event = NSApp.currentEvent;
    if (!event) return UINT32_MAX;
    switch (event.type) {
        case NSEventTypeLeftMouseDown: case NSEventTypeLeftMouseUp:
        case NSEventTypeRightMouseDown: case NSEventTypeRightMouseUp:
        case NSEventTypeOtherMouseDown: case NSEventTypeOtherMouseUp:
            return (uint32_t)event.buttonNumber;
        default: return UINT32_MAX;
    }
}

double rcf_mac_keyboard_boundary(void) {
    if (![NSThread isMainThread]) return 0;
    NSEvent *event = [NSApp nextEventMatchingMask:NSEventMaskKeyDown | NSEventMaskKeyUp | NSEventMaskFlagsChanged
        untilDate:NSDate.distantPast inMode:NSDefaultRunLoopMode dequeue:NO];
    return event ? event.timestamp : 0;
}

void *rcf_mac_foreground_window(void) {
    if (![NSThread isMainThread] || !NSApp.active || NSApp.modalWindow) return NULL;
    NSWindow *candidate = NSApp.keyWindow;
    return candidate && candidate.visible && !candidate.attachedSheet ? (__bridge void *)candidate : NULL;
}

void *rcf_mac_view_window(uint32_t view_serial_number) {
    if (![NSThread isMainThread] || !view_serial_number) return NULL;
    typedef void *(*FindView)(uint32_t);
    typedef void *(*OSXView)(const void *);
    static FindView find_view;
    static OSXView osx_view;
    static BOOL resolved;
    if (!resolved) {
        // Public Rhino SDK: CRhinoView::FromRuntimeSerialNumber and CRhinoView::OSXView.
        find_view = (FindView)dlsym(RTLD_DEFAULT, "_ZN10CRhinoView23FromRuntimeSerialNumberEj");
        osx_view = (OSXView)dlsym(RTLD_DEFAULT, "_ZNK10CRhinoView7OSXViewEv");
        resolved = YES;
    }
    if (!find_view || !osx_view) return NULL;
    void *view = find_view(view_serial_number);
    if (!view) return NULL;
    @try {
        id object = (__bridge id)osx_view(view);
        return [object isKindOfClass:NSView.class] ? (__bridge void *)[(NSView *)object window] : NULL;
    } @catch (NSException *exception) { (void)exception; return NULL; }
}

static BOOL contains_handle(NSView *view, void *handle) {
    if ((__bridge void *)view == handle) return YES;
    for (NSView *child in view.subviews) {
        if (contains_handle(child, handle)) return YES;
    }
    return NO;
}

void *rcf_mac_window_from_handle(void *handle) {
    if (![NSThread isMainThread] || !handle) return NULL;
    @try {
        for (NSWindow *candidate in NSApp.windows) {
            if ((__bridge void *)candidate == handle) return handle;
        }
        for (NSWindow *candidate in NSApp.windows) {
            if (contains_handle(candidate.contentView, handle)) return (__bridge void *)candidate;
        }
        return NULL;
    } @catch (NSException *exception) { (void)exception; return NULL; }
}

int32_t rcf_mac_monitor_end(void) {
    if (![NSThread isMainThread]) return 1;
    ++monitor_generation;
    @try {
        if (monitor) { [NSEvent removeMonitor:monitor]; monitor = nil; }
        if (observer) {
            [[NSNotificationCenter defaultCenter] removeObserver:observer];
            observer = nil;
        }
        if (window_observer) {
            [[NSNotificationCenter defaultCenter] removeObserver:window_observer];
            window_observer = nil;
        }
        if (close_observer) {
            [[NSNotificationCenter defaultCenter] removeObserver:close_observer];
            close_observer = nil;
        }
        NSNotificationCenter *workspace = NSWorkspace.sharedWorkspace.notificationCenter;
        if (sleep_observer) { [workspace removeObserver:sleep_observer]; sleep_observer = nil; }
        if (session_observer) { [workspace removeObserver:session_observer]; session_observer = nil; }
        if (window && window.acceptsMouseMovedEvents) window.acceptsMouseMovedEvents = previous_mouse_moved;
        window = nil;
        callback = NULL;
        return 0;
    } @catch (NSException *exception) { (void)exception; return 2; }
}

int32_t rcf_mac_monitor_window(void *expected_window) {
    if (![NSThread isMainThread]) return 1;
    @try {
        if (window && window.acceptsMouseMovedEvents) window.acceptsMouseMovedEvents = previous_mouse_moved;
        window = (__bridge NSWindow *)expected_window;
        if (window) {
            previous_mouse_moved = window.acceptsMouseMovedEvents;
            window.acceptsMouseMovedEvents = YES;
        }
        return 0;
    } @catch (NSException *exception) { (void)exception; return 2; }
}

int32_t rcf_mac_monitor_begin(RcfMacHandler handler, void *expected_window) {
    if (![NSThread isMainThread] || !handler) return 1;
    if (monitor || observer || window_observer || close_observer || sleep_observer || session_observer || callback ||
        (expected_window && rcf_mac_foreground_window() != expected_window)) return 2;
    @try {
        if (rcf_mac_monitor_window(expected_window) != 0) return rcf_mac_monitor_end() == 0 ? 4 : 5;
        callback = handler;
        uint64_t generation = ++monitor_generation;
        NSEventMask mask = NSEventMaskKeyDown | NSEventMaskKeyUp | NSEventMaskFlagsChanged |
            NSEventMaskMouseMoved | NSEventMaskLeftMouseDragged | NSEventMaskRightMouseDragged |
            NSEventMaskOtherMouseDragged | NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown |
            NSEventMaskOtherMouseDown | NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp |
            NSEventMaskOtherMouseUp | NSEventMaskScrollWheel;
        monitor = [NSEvent addLocalMonitorForEventsMatchingMask:mask handler:^NSEvent *(NSEvent *event) {
            if (monitor_generation != generation) return event;
            switch (event.type) {
                case NSEventTypeMouseMoved: case NSEventTypeLeftMouseDragged:
                case NSEventTypeRightMouseDragged: case NSEventTypeOtherMouseDragged:
                    if (!(window && event.window == window &&
                        rcf_mac_foreground_window() == (__bridge void *)window && rcf_mac_raw_available())) return event;
                    rcf_mac_raw_fallback_motion(event.timestamp, event.deltaX, event.deltaY);
                    return nil;
                default: break;
            }
            RcfMacEvent value = {0};
            value.timestamp = event.timestamp;
            value.modifiers = event.modifierFlags;
            value.window = (__bridge void *)event.window;
            value.buttons = (uint32_t)NSEvent.pressedMouseButtons;
            switch (event.type) {
                case NSEventTypeScrollWheel:
                    value.kind = 2; value.wheel = event.scrollingDeltaY;
                    value.precise = event.hasPreciseScrollingDeltas;
                    value.inverted = event.isDirectionInvertedFromDevice; break;
                case NSEventTypeKeyDown: case NSEventTypeKeyUp:
                    value.kind = 3; value.code = event.keyCode;
                    value.down = event.type == NSEventTypeKeyDown; value.repeated = event.isARepeat; break;
                case NSEventTypeFlagsChanged:
                    value.kind = 5; value.code = event.keyCode; break;
                case NSEventTypeLeftMouseDown: case NSEventTypeRightMouseDown: case NSEventTypeOtherMouseDown:
                case NSEventTypeLeftMouseUp: case NSEventTypeRightMouseUp: case NSEventTypeOtherMouseUp:
                    value.kind = 4; value.code = (uint32_t)event.buttonNumber;
                    value.down = event.type == NSEventTypeLeftMouseDown || event.type == NSEventTypeRightMouseDown ||
                        event.type == NSEventTypeOtherMouseDown; break;
                default: return event;
            }
            if (value.kind == 4 && value.down) {
                if (event.CGEvent) {
                    CGPoint point = CGEventGetLocation(event.CGEvent);
                    value.screen_x = point.x; value.screen_y = point.y;
                }
                if (event.window) {
                    NSView *content = event.window.contentView;
                    NSPoint point = [content.superview convertPoint:event.locationInWindow fromView:nil];
                    NSView *hit = [content hitTest:point];
                    value.content = hit != nil;
                    for (NSView *view = hit; view; view = view.superview) {
                        if ([view isKindOfClass:NSControl.class] || [view isKindOfClass:NSText.class]) {
                            value.content = 0; break;
                        }
                    }
                }
            }
            return callback && callback(&value) ? nil : event;
        }];
        if (!monitor) return rcf_mac_monitor_end() == 0 ? 3 : 5;
        void (^lost_focus)(NSNotification *) = ^(NSNotification *notification) {
            (void)notification;
            RcfMacEvent value = {0}; value.kind = 6;
            if (monitor_generation == generation && callback) callback(&value);
        };
        observer = [[NSNotificationCenter defaultCenter]
            addObserverForName:NSApplicationDidResignActiveNotification object:NSApp
            queue:NSOperationQueue.mainQueue usingBlock:lost_focus];
        void (^lost_window)(NSNotification *) = ^(NSNotification *notification) {
            if (![NSThread isMainThread]) return;
            if ((!window || notification.object == window) && monitor_generation == generation && callback) {
                RcfMacEvent value = {0}; value.kind = 6;
                value.window = (__bridge void *)notification.object;
                callback(&value);
            }
        };
        window_observer = [[NSNotificationCenter defaultCenter]
            addObserverForName:NSWindowDidResignKeyNotification object:nil
            queue:nil usingBlock:lost_window];
        close_observer = [[NSNotificationCenter defaultCenter]
            addObserverForName:NSWindowWillCloseNotification object:nil
            queue:nil usingBlock:lost_window];
        NSNotificationCenter *workspace = NSWorkspace.sharedWorkspace.notificationCenter;
        sleep_observer = [workspace addObserverForName:NSWorkspaceWillSleepNotification object:nil
            queue:NSOperationQueue.mainQueue usingBlock:lost_focus];
        session_observer = [workspace addObserverForName:NSWorkspaceSessionDidResignActiveNotification object:nil
            queue:NSOperationQueue.mainQueue usingBlock:lost_focus];
        if (!observer || !window_observer || !close_observer || !sleep_observer || !session_observer)
            return rcf_mac_monitor_end() == 0 ? 4 : 5;
        return 0;
    } @catch (NSException *exception) { (void)exception; return rcf_mac_monitor_end() == 0 ? 4 : 5; }
}
