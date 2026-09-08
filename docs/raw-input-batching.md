# Raw input batching

Implemented using the hybrid Windows raw-input pattern.

The `WM_INPUT` currently being dispatched has already left the raw-input queue, so read that handle first with `GetRawInputData`. Then drain everything which queued behind it with `GetRawInputBuffer` until the queue is empty.

Reuse one 8-byte-aligned unmanaged buffer. It starts with room for 64 mouse packets and only grows when Windows reports `ERROR_INSUFFICIENT_BUFFER`. Process records in order with the native record alignment, feed each one into the existing accumulator, then send one coalesced wake for the whole drain.

Keep `DefWindowProc` for the dispatched foreground `WM_INPUT`. Do not replace this with a fixed-rate timer or allocate a packet array for each drain.
