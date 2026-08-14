# White screen

## Main issue

Sometimes, and it happens more the more 'young' the rhino proccess is (so it happens more at the start, and less later), the ui will stop rendering, options menus will be white, the cmdline/bars will be frozen, right clicking on the cmdline fixes it.

Rhino is not frozen. The dialog is already open and fully created and the interface thread is sitting in the normal ShowDialog message loop.

The dispatcher is empty too so no deadlock or rhino being busy to not rendering the window.

white screen memory dumps had the same wpf state:

```text
_commitPendingAfterRender = 1
_needToCommitChannel      = 1
_interlockState           = 3
```

A good dump had all three at `0`, so WPF seems to know it still has something to render but never finishes it.

Right clicking the cmdline fixes the ui freeze that maybe means wpf is waiting for some extra message before it continues.

## Things tested that do not seem to be stuck when it happens

- RhinosCanFly timer
- synthetic Shift / MMB
- Mouse4 / Mouse5 state
- raw input session
- raw input cleanup
- RhinosCanFly wake queue
- dispatcher backlog
- WPF dispatcher shutdown
- deadlock

could still be triggering the problem through input / focus / message timing. 

dump only shows the state after it has already gone white.

Current best guess is some Rhino or Eto or WPF render commit or modal focus timing bug, but a graphics or DWM or driver thingy is also a maybe.
