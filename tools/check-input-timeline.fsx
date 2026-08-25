#load "InputTimelineContractTypes.fs"
#load "../src/Core/InputAccumulator.fs"

open System
open RhinosCanFly

let fail (message: string) =
    Console.Error.WriteLine message
    Environment.Exit 1

let expect condition (message: string) =
    if not condition then
        fail message

let input = InputAccumulator.create ()
let output = InputAccumulator.timeline_buffer ()

InputAccumulator.add_mouse 2 3 input
InputAccumulator.add_wheel 120 input
InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleDown 10L input
InputAccumulator.add_mouse 5 7 input
InputAccumulator.add_wheel -120 input

InputAccumulator.add_keyboard_actions InputAccumulator.KeyboardAction.PivotHoldStarted 20L input

InputAccumulator.add_mouse 11 13 input
InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleUp 30L input
InputAccumulator.add_mouse 17 19 input
InputAccumulator.add_wheel 240 input

let struct (count, overflowed) = InputAccumulator.drain_timeline output input

expect (not overflowed) "The ordered input timeline overflowed during its basic contract check."
expect (count = 7) $"Expected seven ordered input events, got {count}."
expect (output[0].kind = InputAccumulator.TimelineEventKind.Movement) "The first movement segment was lost."
expect (output[0].dx = 2L && output[0].dy = 3L && output[0].wheel = 120L) "The first movement segment changed."
expect (output[1].kind = InputAccumulator.TimelineEventKind.RawMouseButton) "The mouse-down boundary moved."
expect (output[1].button.event = RawMouseButtonEvent.MiddleDown) "The mouse-down boundary changed."

expect
    (output[2].dx = 5L && output[2].dy = 7L && output[2].wheel = -120L)
    "Movement or wheel input before the keyboard hold was lost."

expect (output[3].kind = InputAccumulator.TimelineEventKind.KeyboardActions) "The keyboard boundary moved."

expect
    (output[3].keyboard_actions = InputAccumulator.KeyboardAction.PivotHoldStarted)
    "The keyboard hold boundary changed."

expect (output[4].dx = 11L && output[4].dy = 13L) "Movement during the keyboard hold was lost."
expect (output[5].button.event = RawMouseButtonEvent.MiddleUp) "The mouse-up boundary moved."

expect
    (output[6].dx = 17L && output[6].dy = 19L && output[6].wheel = 240L)
    "Movement or wheel input after mouse-up was lost."

InputAccumulator.add_keyboard_actions InputAccumulator.KeyboardAction.ProjectionToggle 40L input
InputAccumulator.add_keyboard_actions InputAccumulator.KeyboardAction.ProjectionToggle 41L input

let struct (tapCount, tapOverflowed) = InputAccumulator.drain_timeline output input

expect (not tapOverflowed) "The input timeline overflowed while checking rapid taps."
expect (tapCount = 2) $"Two rapid keyboard taps collapsed to {tapCount} event(s)."

expect
    (output[0].keyboard_actions = InputAccumulator.KeyboardAction.ProjectionToggle
     && output[1].keyboard_actions = InputAccumulator.KeyboardAction.ProjectionToggle)
    "Two rapid projection toggles did not survive independently."

let startupInput = InputAccumulator.create ()
InputAccumulator.add_mouse 23 29 startupInput
InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleDown 50L startupInput
InputAccumulator.add_mouse 31 37 startupInput
InputAccumulator.add_keyboard_actions InputAccumulator.KeyboardAction.Exit 55L startupInput
InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleUp 60L startupInput
InputAccumulator.add_mouse 41 43 startupInput
InputAccumulator.discard_movement startupInput

let struct (startupCount, startupOverflowed) =
    InputAccumulator.drain_timeline output startupInput

expect (not startupOverflowed) "The startup discard check overflowed."
expect (startupCount = 3) $"Startup movement cleanup kept {startupCount} events instead of three input boundaries."
expect (output[0].button.event = RawMouseButtonEvent.MiddleDown) "Startup cleanup lost mouse-down."

expect (output[1].keyboard_actions = InputAccumulator.KeyboardAction.Exit) "Startup cleanup lost the keyboard exit."

expect (output[2].button.event = RawMouseButtonEvent.MiddleUp) "Startup cleanup lost mouse-up."

printfn "Input timeline ordering check passed."
