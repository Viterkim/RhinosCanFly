# Input and navigation

Hooks own native Down/Up delivery. Keyboard and raw mouse transitions share a timeline; the UI consumes it in arrival order. Held keys are seeded on entry without triggering toggle actions.

Retarget, projection changes and untilt discard pending pointer movement, including the rest of the current batch. Releases are kept. Ordinary pivot/pan changes rebase without discarding later movement.

Flight and standalone navigation share `NavigationLoop`. Ready input runs before the Rhino UI pump. Standalone keeps its own controls and release rules; it does not enable flight keyboard suppression.

Only the running loop updates standalone navigation. Reentered timer callbacks return. Physical-button polling keeps its existing cadence.

External exit and foreground loss veto camera writes before queued input is applied. Releases still drain for cleanup. Ordinary motion before a button Up keeps its order.

Input locks are released before camera changes or Rhino calls. Keyboard publication may take the timeline lock; timeline consumers must not hold it while calling keyboard code.

See [never-do.md](never-do.md) for native mouse ownership rules.
