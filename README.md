# Rhinos Can Fly - Flying camera controls for Rhino 3D (level editor WASD / custom binds)

## Demo

https://github.com/user-attachments/assets/52dc3237-e12b-4a54-8776-88f6b6475fac

## How to Use / Recommendations

- Right click to enter flying, right click to exit (change it in `RhinosCanFlyOptions`).

- Set lens length in your file/project/default Rhino setup to something like `18` or so, which gives that 100 FOV feel.

## Links

[McNeel / Rhino Forum Link](https://discourse.mcneel.com/t/rhinos-can-fly-wasd-game-engine-fly-camera-controls-for-rhino/220880)

[Food 4 Rhino Link](https://www.food4rhino.com/en/app/rhinos-can-fly-rhinoscanfly-flying-custom-perspective-view)

[Building On Windows](./docs/building-on-windows.md)

[Other Installation Options](./docs/other-installation-options.md)

## Main Commands

`RhinosCanFly` main command, fly around in perspective view or an enabled parallel view, exit on right click and escape, keep the current camera position. (default flying behaviour, usually done with right click).

`RhinosCanFlyOptions` pops up a panel where you can change keybindings / features (same menu is also in Tools -> Options... -> Rhinos Can Fly).

## Functionality

Main focus is flying as a workflow for editing, which includes things like: teleporting / syncing other views via retarget, pivot/pan toggleable buttons, parallel/iso flying behaviour and toggle, retarget options, everything configurable and supports mouse 4/5 stuff.

Walking is not the point of the project, and it won't have collision, the idea is more of a full workflow inspired by old editor workflows with shortcuts that makes things like retarget/zoom to target fast. The walk command does the bare minimum, if some people want a small variant for seeing something stuck to a specific height (uses the documents units).

## Extra Commands

`RhinosCanFlyTempFly` goes back to the initial camera position after flight ends, this can be set to default behaviour in options.

`RhinosCanFlySetSpeed` sets the current flying speed, you can also use a bind or the mousewheel up / down in settings.

`RhinosCanWalk` very bare bones 'walk' command that just limits your mouse movement on an axis on the current CPlane. Set the CPlane to what you want the ground to be, height is in document units, and set an alias for something like `'_-RhinosCanWalk 1.75 _Enter`. It only supports the CPlane being 'somewhatfriendly' to the z axis from the world, or you become spiderman on the side of a wall.

`RhinosCanFlyPan` pans around via a toggle (extra, if you want the functionality Rhino doesn't provide).

`RhinosCanFlyPivot` pivots around via a toggle (extra, same).

`RhinosCanFlyUntiltView` untilts the active view without moving it.

`RhinosCanFlyToggleEnable` enables or disables Rhinos Can Fly until Rhino is restarted.

## Options

![image](./docs/img/options1.jpg)

![image](./docs/img/options2.jpg)

## Main Install (Package Manager / Yak)

Install in Rhino itself, run `PackageManager` and search for `RhinosCanFly`, click install and restart Rhino.

## Artistic Logo / Icon

![image](./docs/img/rcf-logo.png)

We might have to use cardboard wings, but it works. Credit to Peter for the artistic logo.

## If you care about file meta data

If you enable "save current speed to document" it will write the current speed to your Rhino document. Just disable it if you don't want any persisted state.

It only saves "RhinosCanFly\FlyingSpeed"

The project still has a config file for the normal settings, and the options menu will show you the full path like `C:\Users\<username>\AppData\Roaming\McNeel\Rhinoceros\<version>\settings\rhinos-can-fly-config.json` and the raw JSON content.

## Disclaimer

Gippity was a major contributor in this, even that is an understatement.

Why F#? I chose microsoft ocaml over microsoft java.

Why no Mac support? I don't have a mac and can't test / develop it, feel free to add mac support if you want it, all the raw input / windows native stuff will have to be redone, but it is split up in a way so it should be possible (with a lot of work).

## Template from

Template: Rhino F# Template [Github Link](https://github.com/Viterkim/RhinoFSharpTemplate)
