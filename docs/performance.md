# Performance in F#

`match a, b` can be completely erased. With two bool parameters it produced identical IL to nested `if`s.

This is not guaranteed. 0.3.1 `FlightLoop` emitted `System.Tuple<bool,bool>` from the same syntax with mutable operands. Similar cases sometimes erase and sometimes don't.

`match struct (a, b)` avoids the GC allocation, but can leave a `ValueTuple` in IL. Don't blindly use struct matches either.

0.2.2 `FlightLoop` also allocated from normal tuple returns and `Some`. Those allocations are gone now.

Current `FlightLoop` has no per frame heap allocation from looking at the IL.

`Some x` allocates. `ValueSome x` does not. `Result` is already a struct.

`match` can also produce better code than repeated boolean checks. `requested_gesture_action` went from 9 modifier reads to 3 after changing it to a match.

For hot code, check the IL when something looks suspicious.