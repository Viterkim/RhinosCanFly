# For future raw input batching

Currently every WM_INPUT gets read separately with GetRawInputData. 

Movement is already accumulated before Rhino uses it, but a stupidly high polling mouse still makes the input thread process every message alone.

Later maybe: read the current packet with GetRawInputData, then drain queued packets with GetRawInputBuffer. reuse one correctly aligned buffer and feed everything into the existing accumulator.

Should give less message/native call overhead and harder to overwhelm with high polling rates.
