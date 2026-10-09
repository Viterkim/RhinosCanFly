// Run against either compiled platform with its matching RhinoCommon reference.
open System
open System.Diagnostics
open Rhino.Geometry
open RhinosCanFly

let mutable checks = 0

let near (name: string) (expected: float) (actual: float) =
    checks <- checks + 1

    if abs (expected - actual) > 1e-10 then
        failwithf "%s: expected %g, got %g" name expected actual

let rotated =
    Movement.rotate_vector (Vector3d(0., 0., 10.)) (Math.PI / 2.) Vector3d.XAxis

near "rotation normalizes its axis" 1. rotated.Y
near "rotation preserves length" 1. rotated.Length

let pauses = ResizeArray<struct (int64 * int64)>()

let ticks (milliseconds: int) =
    int64 milliseconds * Stopwatch.Frequency / 1000L

let struct (finish, seconds) =
    FlightLoop.movement_interval (ticks 10) (ticks 100) (ticks 30) pauses

near "short press ends at its release" 0.02 seconds
near "short press keeps ordered boundary" 30. (float finish * 1000. / float Stopwatch.Frequency)

for factors in [ [ 2.; 0.5 ]; [ 1.5; 2.; 0.25 ]; [ 0.5; 0.5; 4. ] ] do
    let width = List.fold FlightCamera.magnified_parallel_width 100. factors
    near "parallel zoom applies every pending factor once" (100. / List.fold (*) 1. factors) width

for rate in [ -30.; -1.; 1.; 30. ] do
    let first = FlightCamera.parallel_zoom_factor 100. (rate * 0.02) 1. 0.02
    let width = FlightCamera.magnified_parallel_width 100. first
    let second = FlightCamera.parallel_zoom_factor width (rate * 0.03) 1. 0.03
    let together = FlightCamera.parallel_zoom_factor 100. (rate * 0.05) 1. 0.05
    near "parallel movement preserves batch partitions" together (first * second)

printfn "%d compiled shared camera and replay checks passed." checks
